using System.Text;
using System.Xml;
using System.Xml.Linq;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Primitives;

namespace Excise.Core.Xfa;

/// <summary>
/// Keeps the XFA datasets of a STATIC XFA form consistent with the AcroForm
/// field values (#2013, slice S4 of #1547 Phase 3). Called by
/// <see cref="PdfField.SetValue(string?)"/>, the one API every fill path uses
/// (<c>excise fill-form</c>, Save Filled Copy, the GUI form overlay).
/// </summary>
/// <remarks>
/// <para><b>Why.</b> ISO 32000-2 Annex K.2: a writer that modifies a PDF with
/// an XFA entry shall keep the XFA field values consistent with the AcroForm
/// fields' <c>/V</c>. An XFA processor reopens the form by merging the
/// template with the datasets (XFA 3.3 ch. 4 "Basic Data Binding", p171; Adobe
/// implementation ch. 28, p1263), so a stale datasets packet shows the old
/// value and keeps it in the file.</para>
/// <para><b>Field to data node.</b> A static (XFAF) form's AcroForm field name
/// is the XFA-SOM path of its template field, each segment indexed among
/// same-named siblings (XFA 3.3 "Field Names", p72-74). The path is resolved
/// in the MERGED form (<see cref="XfaMerge"/>), not the template or the
/// <c>/T</c> chain: the IRS W-9 puts every field of the transparent
/// <c>Page1</c> subform (<c>bind match="none"</c>) directly under the record,
/// and its seven <c>c1_1</c> boxes are <c>match="global"</c> to ONE data
/// node.</para>
/// <para><b>What is written.</b> The value is written eagerly into the
/// datasets stream, so every later save, scrub or redaction sees the current
/// bytes; only the datasets stream (and a non-empty <c>form</c> packet, see
/// <see cref="ResetFormPacketStream"/>) changes. Dynamic forms
/// (<see cref="PdfXfaFormKind.Dynamic"/>) are out of scope and untouched.</para>
/// <para><b>Redaction.</b> Any redaction removes the whole XFA form
/// (decision 5 of #1547); after that there is no datasets packet to write and
/// this is a no-op.</para>
/// </remarks>
internal sealed class XfaStaticDataSync
{
    private const string DataNamespacePrefix = "http://www.xfa.org/schema/xfa-data/";
    private const string DefaultDataNamespace = "http://www.xfa.org/schema/xfa-data/1.0/";
    private const string FormNamespacePrefix = "http://www.xfa.org/schema/xfa-form/";
    private static readonly TimeSpan MergeTimeLimit = TimeSpan.FromSeconds(10);

    private readonly List<string> _notes = new();
    private Snapshot? _snapshot;
    private PdfObject? _classifiedXfa;
    private bool _classifiedStatic;

    /// <summary>
    /// What could not be written, one line each (deduplicated): a field the
    /// template does not name, a binding excise cannot create, a malformed
    /// packet. Each one means that field's datasets value may still be stale.
    /// </summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>
    /// Refuse a value XML 1.0 cannot carry (control characters, lone
    /// surrogates) BEFORE the field changes, the way #1671 refuses a value the
    /// widget font cannot draw: writing it to <c>/V</c> but not to the
    /// datasets would break the K.2 consistency this class exists for.
    /// </summary>
    internal static void EnsureValueWritable(PdfDocument document, string? value, string fieldName)
    {
        if (value == null || !MayCarryStaticXfa(document))
            return;
        try
        {
            XmlConvert.VerifyXmlChars(value);
        }
        catch (XmlException)
        {
            throw new ArgumentException(
                $"Field '{fieldName}' belongs to an XFA form, whose data is XML; the value contains a " +
                "character XML cannot carry (a control character or an unpaired surrogate).",
                nameof(value));
        }
    }

    /// <summary>Write <paramref name="value"/> (null = cleared) into the data node of <paramref name="field"/>.</summary>
    internal static void Apply(PdfDocument document, PdfField field, string? value)
    {
        if (!MayCarryStaticXfa(document))
            return;
        var sync = document.XfaStaticDataSync ??= new XfaStaticDataSync();
        sync.ApplyCore(document, field, value);
    }

    /// <summary>
    /// The cheap gate: an <c>/AcroForm /XFA</c> entry and no catalog
    /// <c>/NeedsRendering true</c>. The full static/dynamic classification
    /// runs once per XFA object after this passes.
    /// </summary>
    private static bool MayCarryStaticXfa(PdfDocument document)
    {
        if (!XfaXmlCarrier.TryGetXfa(document, out _, out _))
            return false;
        return !(document.Catalog.GetOptional("NeedsRendering") is { } needsRendering
                 && document.Resolve(needsRendering) is PdfBoolean { Value: true });
    }

    private void ApplyCore(PdfDocument document, PdfField field, string? value)
    {
        if (!XfaXmlCarrier.TryGetXfa(document, out _, out var xfaEntry))
            return;
        var xfa = document.Resolve(xfaEntry);

        if (!ReferenceEquals(_classifiedXfa, xfa))
        {
            _classifiedXfa = xfa;
            _classifiedStatic = document.DetectXfaForm() == PdfXfaFormKind.Static;
        }
        if (!_classifiedStatic)
            return;

        if (_snapshot == null || !_snapshot.IsCurrent(document))
        {
            _snapshot = Snapshot.TryBuild(document, xfa, out var reason);
            if (_snapshot == null)
            {
                Note($"XFA datasets not updated for '{field.FullName}': {reason}");
                return;
            }
        }

        var chain = ResolveChain(_snapshot.Form, field.FullName);
        if (chain == null)
        {
            Note($"XFA datasets not updated for '{field.FullName}': no XFA template field has that SOM name.");
            return;
        }

        var writer = new Writer(_snapshot, chain, Note);
        bool changed;
        try
        {
            changed = writer.Write(field, value);
        }
        catch (XfaLayoutException ex)
        {
            Note($"XFA datasets not updated for '{field.FullName}': {ex.Message}");
            return;
        }

        if (changed)
            _snapshot.Store(document, Note);
    }

    private void Note(string line)
    {
        if (!_notes.Contains(line))
            _notes.Add(line);
    }

    // ---------------------------------------------------------------- SOM

    /// <summary>
    /// Resolve an AcroForm full name (<c>root[0].Page1[0].f1_01[0]</c>, an
    /// optional leading <c>form.</c>) to the chain of merged form nodes from
    /// the root to the field. Unnamed containers are addressed by class
    /// (<c>#subform[0]</c>); a subform with <c>scope="none"</c> takes no part
    /// in SOM names (XFA 3.3 p849) and is looked through. Null when any
    /// segment does not resolve.
    /// </summary>
    internal static List<XfaFormNode>? ResolveChain(XfaFormNode root, string fullName)
    {
        var segments = SplitSom(fullName);
        if (segments == null || segments.Count == 0)
            return null;

        int i = 0;
        var rootName = root.Element.Attr("name");
        if (segments[0].Name == "form" && rootName != "form" && segments.Count > 1)
            i = 1;   // "form.root.page.field" (XFA 3.3 p72-73)

        if (segments[i].Index != 0 || !Matches(root, segments[i].Name))
            return null;

        var chain = new List<XfaFormNode> { root };
        var current = root;
        for (i++; i < segments.Count; i++)
        {
            var (name, index) = segments[i];
            int seen = 0;
            XfaFormNode? next = null;
            foreach (var child in SomChildren(current))
            {
                if (!Matches(child, name))
                    continue;
                if (seen++ == index)
                {
                    next = child;
                    break;
                }
            }
            if (next == null)
                return null;
            chain.Add(next);
            current = next;
        }
        return chain;
    }

    private static IEnumerable<XfaFormNode> SomChildren(XfaFormNode node)
    {
        foreach (var child in node.Children)
        {
            if (child.Kind == XfaNodeKind.Subform && child.Element.Attr("scope") == "none")
            {
                foreach (var inner in SomChildren(child))
                    yield return inner;
            }
            else
            {
                yield return child;
            }
        }
    }

    private static bool Matches(XfaFormNode node, string segment)
    {
        var name = node.Element.Attr("name");
        if (segment.StartsWith('#'))
            return string.IsNullOrEmpty(name) && node.Element.Name.LocalName == segment[1..];
        return name == segment;
    }

    /// <summary>Split on unescaped dots; <c>\.</c> is a literal dot in a name.</summary>
    private static List<(string Name, int Index)>? SplitSom(string fullName)
    {
        var result = new List<(string, int)>();
        var current = new StringBuilder();
        for (int i = 0; i <= fullName.Length; i++)
        {
            if (i < fullName.Length && fullName[i] == '\\' && i + 1 < fullName.Length && fullName[i + 1] == '.')
            {
                current.Append('.');
                i++;
                continue;
            }
            if (i < fullName.Length && fullName[i] != '.')
            {
                current.Append(fullName[i]);
                continue;
            }

            var segment = current.ToString();
            current.Clear();
            int index = 0;
            int open = segment.LastIndexOf('[');
            if (open >= 0 && segment.EndsWith(']'))
            {
                if (!int.TryParse(segment.AsSpan(open + 1, segment.Length - open - 2),
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out index))
                {
                    return null;
                }
                segment = segment[..open];
            }
            if (segment.Length == 0)
                return null;
            result.Add((segment, index));
        }
        return result;
    }

    // ---------------------------------------------------------------- writing

    /// <summary>Maps one AcroForm value onto the data nodes of one resolved field.</summary>
    private sealed class Writer
    {
        private readonly Snapshot _snapshot;
        private readonly List<XfaFormNode> _chain;
        private readonly Action<string> _note;
        private bool _changed;

        public Writer(Snapshot snapshot, List<XfaFormNode> chain, Action<string> note)
        {
            _snapshot = snapshot;
            _chain = chain;
            _note = note;
        }

        private XfaFormNode Target => _chain[^1];

        public bool Write(PdfField field, string? value)
        {
            var node = Target;
            switch (node.Kind)
            {
                case XfaNodeKind.Field:
                    WriteField(field, node, value);
                    break;
                case XfaNodeKind.ExclGroup:
                    WriteExclGroup(field, node, value);
                    break;
                default:
                    _note($"XFA datasets not updated for '{field.FullName}': its SOM name points at a " +
                          $"{node.Element.Name.LocalName}, not a field.");
                    break;
            }
            return _changed;
        }

        private void WriteField(PdfField field, XfaFormNode node, string? value)
        {
            var element = node.Element;
            var ui = UiKind(element);

            if (ui == "checkButton")
            {
                var on = OnValue(element);
                var off = OffValue(element);
                if (IsOff(value))
                {
                    // Several check boxes can share one data node (the W-9's
                    // seven c1_1 boxes, match="global", items 1..7 / 0). Turning
                    // one box off clears the node only when it holds THIS box's
                    // on value; otherwise another box owns the node.
                    var existing = FindData(node);
                    if (existing == null)
                        SetText(field, EnsureData(field, node), off);
                    else if (existing.Value == on)
                        SetText(field, existing, off);
                }
                else
                {
                    SetText(field, EnsureData(field, node), on);
                }
                return;
            }

            var text = value == null ? string.Empty : NormalizeNewlines(value);
            if (ui == "choiceList")
            {
                if (element.Child("ui")?.Child("choiceList")?.Attr("open") == "multiSelect")
                {
                    WriteMultiSelect(field, EnsureData(field, node), text);
                    return;
                }
                text = SaveValueFor(element, text);
            }

            SetText(field, EnsureData(field, node), text);
        }

        private void WriteExclGroup(PdfField field, XfaFormNode group, string? value)
        {
            var members = group.Children.Where(c => c.Kind == XfaNodeKind.Field).ToList();
            XfaFormNode? selected = null;
            if (!IsOff(value))
            {
                selected = members.FirstOrDefault(m => OnValue(m.Element) == value);
                if (selected == null)
                {
                    // The AcroForm on-state names need not equal the XFA items
                    // (a producer may number them); fall back to widget order.
                    var exports = field.ButtonExportValues;
                    int index = -1;
                    for (int i = 0; i < exports.Count; i++)
                        if (exports[i] == value) index = i;
                    var checkMembers = members.Where(m => UiKind(m.Element) == "checkButton").ToList();
                    if (index >= 0 && index < checkMembers.Count && exports.Count == checkMembers.Count)
                        selected = checkMembers[index];
                }
                if (selected == null)
                {
                    _note($"XFA datasets not updated for '{field.FullName}': value '{value}' matches no " +
                          "member of its exclusion group.");
                    return;
                }
            }

            // Long exclusion format (XFA 3.3 p196): members bound to their own
            // data values. Short format: the group's own data value.
            var boundMembers = members.Where(m => m.BoundData != null).ToList();
            if (group.BoundData == null && boundMembers.Count > 0)
            {
                foreach (var member in boundMembers)
                    SetText(field, member.BoundData!,
                        ReferenceEquals(member, selected) ? OnValue(member.Element) : OffValue(member.Element));
                return;
            }

            SetText(field, EnsureData(field, group), selected == null ? string.Empty : OnValue(selected.Element));
        }

        private void WriteMultiSelect(PdfField field, XElement? data, string text)
        {
            if (data == null)
                return;
            // XFA 3.3 p198: a multi-select choice list binds to a data group whose
            // children hold one value each; the field value is them joined by newlines.
            data.RemoveNodes();
            var ns = ChildNamespace(data);
            foreach (var line in text.Split('\n'))
            {
                if (line.Length > 0)
                    data.Add(new XElement(ns + "value", line));
            }
            _changed = true;
        }

        private void SetText(PdfField field, XElement? data, string text)
        {
            if (data == null)
                return;

            if (data.Elements().Any())
            {
                if (XfaRichText.FindXhtmlBody(data) == null)
                {
                    _note($"XFA datasets not updated for '{field.FullName}': its data node is a group, " +
                          "not a value.");
                    return;
                }
                // A rich-text value replaced by plain text: drop the XHTML and
                // the content type that announced it.
                data.Attributes().Where(a => a.Name.LocalName == "contentType"
                    && a.Name.NamespaceName.StartsWith(DataNamespacePrefix, StringComparison.Ordinal)).Remove();
            }
            else if (data.Value == text && !data.Nodes().Skip(1).Any())
            {
                return;
            }

            data.RemoveNodes();
            if (text.Length > 0)
                data.Add(new XText(text));
            _changed = true;
        }

        /// <summary>The node the merge bound, without creating one.</summary>
        private XElement? FindData(XfaFormNode node)
        {
            if (node.BoundData != null)
                return node.BoundData;
            if (BindMatch(node.Element) == "global" && _snapshot.DataRoot != null
                && node.Element.Attr("name") is { Length: > 0 } name)
            {
                return _snapshot.DataRoot.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == name);
            }
            return null;
        }

        /// <summary>
        /// The bound data node, created when the merge found none — as an XFA
        /// processor's merge creates it (XFA 3.3 p199; ch. 4 re-normalization,
        /// p209). Never for <c>match="none"</c> (no data by design) or an
        /// unbound <c>dataRef</c> (not modelled).
        /// </summary>
        private XElement? EnsureData(PdfField field, XfaFormNode node)
        {
            if (FindData(node) is { } existing)
            {
                node.BoundData = existing;
                return existing;
            }

            var match = BindMatch(node.Element);
            var name = node.Element.Attr("name");
            if (match == "none")
                return null;
            if (string.IsNullOrEmpty(name) || match == "dataRef")
            {
                _note($"XFA datasets not updated for '{field.FullName}': it has no data node and its " +
                      $"binding ({(string.IsNullOrEmpty(name) ? "unnamed" : "dataRef")}) is not one excise creates.");
                return null;
            }

            XElement? parent;
            if (match == "global")
            {
                // Global data lives outside any record subtree, at the record
                // level or above (XFA 3.3 p245-246): the W-9 keeps c1_1 there.
                parent = _snapshot.EnsureDataRoot(_chain[0].Element.Attr("name"));
            }
            else
            {
                parent = EnsureScope(field);
            }
            if (parent == null)
                return null;

            var created = new XElement(ChildNamespace(parent) + name);
            parent.Add(created);
            node.BoundData = created;
            _changed = true;
            return created;
        }

        /// <summary>
        /// The data group the target's container binds within, walking the
        /// chain and creating each missing data group a named,
        /// <c>match="once"</c> subform instance would have bound.
        /// </summary>
        private XElement? EnsureScope(PdfField field)
        {
            var scope = _snapshot.EnsureDataRoot(_chain[0].Element.Attr("name"));
            if (scope == null)
            {
                _note($"XFA datasets not updated for '{field.FullName}': the root subform is unnamed, so " +
                      "there is no data record to create.");
                return null;
            }
            _chain[0].DataScope ??= scope;

            for (int i = 1; i < _chain.Count - 1; i++)
            {
                var container = _chain[i];
                if (container.Kind != XfaNodeKind.Subform)
                    continue;   // areas and exclusion groups bind in their parent's scope

                var name = container.Element.Attr("name");
                var match = BindMatch(container.Element);
                if (string.IsNullOrEmpty(name) || match is "none" or "global")
                    continue;   // transparent to data (XfaMerge.ExpandSubform)

                if (container.DataScope != null)
                {
                    scope = container.DataScope;
                    continue;
                }

                if (match == "dataRef")
                {
                    _note($"XFA datasets not updated for '{field.FullName}': subform '{name}' binds by " +
                          "dataRef to a data group that does not exist.");
                    return null;
                }

                var group = new XElement(ChildNamespace(scope) + name);
                scope.Add(group);
                container.DataScope = group;
                scope = group;
                _changed = true;
            }
            return scope;
        }

        private static XNamespace ChildNamespace(XElement parent)
            => parent.Name.NamespaceName.StartsWith(DataNamespacePrefix, StringComparison.Ordinal)
                ? XNamespace.None
                : parent.Name.Namespace;
    }

    private static bool IsOff(string? value) => value == null || value == "Off";

    private static string NormalizeNewlines(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string BindMatch(XElement element) => element.Child("bind").AttrOr("match", "once");

    private static string? UiKind(XElement field)
        => field.Child("ui")?.Elements()
            .FirstOrDefault(w => w.Name.LocalName is not ("extras" or "picture"))?.Name.LocalName;

    /// <summary>The on value: the first <c>items</c> entry, else "1" (as <see cref="XfaValues.OnValue"/>).</summary>
    private static string OnValue(XElement field) => XfaValues.OnValue(field);

    /// <summary>
    /// The off value: the second <c>items</c> entry. When <c>items</c> is
    /// present without one, the spec default is the null string (XFA 3.3
    /// p759) — not the "0" <see cref="XfaValues.OffValue"/> returns, see
    /// #2016. With no <c>items</c> at all, "0" as the merge reads it.
    /// </summary>
    private static string OffValue(XElement field)
    {
        var items = field.ChildrenNamed("items").FirstOrDefault();
        if (items == null)
            return "0";
        return items.Elements().Skip(1).FirstOrDefault()?.Value ?? string.Empty;
    }

    /// <summary>
    /// A choice list with a display column and a <c>save="1"</c> column stores
    /// the saved value of the chosen row (XFA 3.3 p758, p760). The AcroForm
    /// value may be either; a display text maps to its row's saved value.
    /// </summary>
    private static string SaveValueFor(XElement field, string value)
    {
        var lists = field.ChildrenNamed("items").ToList();
        var save = lists.FirstOrDefault(l => l.Attr("save") == "1");
        var display = lists.FirstOrDefault(l => l.Attr("save") != "1");
        if (save == null || display == null)
            return value;
        var saveTexts = XfaValues.ItemTexts(save);
        if (saveTexts.Contains(value))
            return value;
        var index = XfaValues.ItemTexts(display).IndexOf(value);
        return index >= 0 && index < saveTexts.Count ? saveTexts[index] : value;
    }

    // ---------------------------------------------------------------- packets

    private enum Carrier
    {
        /// <summary>The datasets packet is its own well-formed stream in the /XFA array.</summary>
        PacketStream,
        /// <summary>/XFA is one stream holding the whole XDP.</summary>
        SingleStream,
        /// <summary>The /XFA array has no datasets packet; one is inserted on first write.</summary>
        MissingPacket,
        /// <summary>The datasets packet is not well-formed alone; the array is rewritten as one stream.</summary>
        Collapsed,
    }

    /// <summary>The parsed datasets and merged form of one /XFA value.</summary>
    private sealed class Snapshot
    {
        private PdfObject _xfa;
        private Carrier _carrier;
        private PdfStream? _stream;
        private byte[]? _streamBytes;
        private readonly XDocument _document;
        private readonly Encoding _encoding;
        private readonly XNamespace _dataNamespace;
        private bool _formPacketChecked;

        private Snapshot(PdfObject xfa, Carrier carrier, PdfStream? stream, XDocument document,
            Encoding encoding, XfaFormNode form, XElement? dataRoot, XNamespace dataNamespace)
        {
            _xfa = xfa;
            _carrier = carrier;
            _stream = stream;
            _streamBytes = stream?.DecodedData;
            _document = document;
            _encoding = encoding;
            Form = form;
            DataRoot = dataRoot;
            _dataNamespace = dataNamespace;
        }

        public XfaFormNode Form { get; }

        public XElement? DataRoot { get; private set; }

        /// <summary>
        /// Still the bytes this snapshot parsed: the same /XFA object, and the
        /// datasets stream holds the very array this snapshot wrote or read.
        /// Anything else (a scrub, a redaction, a rewrite) forces a re-parse.
        /// </summary>
        public bool IsCurrent(PdfDocument document)
        {
            if (!XfaXmlCarrier.TryGetXfa(document, out _, out var entry)
                || !ReferenceEquals(document.Resolve(entry), _xfa))
            {
                return false;
            }
            return _carrier switch
            {
                Carrier.MissingPacket => _xfa is PdfArray array && FindPacket(document, array, "datasets") == null,
                _ => _stream != null && ReferenceEquals(_stream.DecodedData, _streamBytes),
            };
        }

        public static Snapshot? TryBuild(PdfDocument document, PdfObject xfa, out string reason)
        {
            reason = string.Empty;
            if (!XfaPackets.TryRead(document, out var packets, out reason) || packets == null)
                return null;

            Carrier carrier;
            PdfStream? stream;
            XDocument? xml;
            Encoding encoding;

            if (xfa is PdfStream single)
            {
                carrier = Carrier.SingleStream;
                stream = single;
                if (!XfaXmlCarrier.TryLoadXml(single.DecodedData, out xml, out encoding))
                {
                    reason = "the XFA stream is not well-formed XML.";
                    return null;
                }
            }
            else if (xfa is PdfArray array)
            {
                stream = FindPacket(document, array, "datasets");
                if (stream == null)
                {
                    carrier = Carrier.MissingPacket;
                    encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                    xml = new XDocument();
                }
                else if (XfaXmlCarrier.TryLoadXml(stream.DecodedData, out xml, out encoding))
                {
                    carrier = Carrier.PacketStream;
                }
                else
                {
                    carrier = Carrier.Collapsed;
                    var streams = XfaXmlCarrier.ResolvePacketStreams(document, array);
                    if (!XfaXmlCarrier.TryLoadXml(XfaXmlCarrier.Concatenate(streams), out xml, out encoding))
                    {
                        reason = "the XFA datasets packet is not well-formed XML.";
                        return null;
                    }
                }
            }
            else
            {
                reason = "the /XFA entry is neither a stream nor a packet array.";
                return null;
            }

            var datasets = FindDatasets(xml);
            var data = datasets?.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "data" && e.Name.Namespace == datasets.Name.Namespace);
            var dataRoot = data?.Elements().FirstOrDefault();
            XNamespace dataNamespace = datasets?.Name.Namespace ?? (XNamespace)DefaultDataNamespace;

            try
            {
                var budget = new XfaBudget(MergeTimeLimit, CancellationToken.None);
                var report = new XfaReport();
                var template = new XfaTemplate(packets.Template, budget, report);
                var form = new XfaMerge(budget, report, dataRoot).Merge(template.Root);
                return new Snapshot(xfa, carrier, stream, xml, encoding, form, dataRoot, dataNamespace);
            }
            catch (XfaLayoutException ex)
            {
                reason = ex.Message;
                return null;
            }
        }

        /// <summary>The record data group, created (with datasets and data) when absent.</summary>
        public XElement? EnsureDataRoot(string? rootName)
        {
            if (DataRoot != null)
                return DataRoot;
            if (string.IsNullOrEmpty(rootName))
                return null;

            var datasets = FindDatasets(_document);
            if (datasets == null)
            {
                datasets = new XElement(_dataNamespace + "datasets",
                    new XAttribute(XNamespace.Xmlns + "xfa", _dataNamespace.NamespaceName));
                if (_document.Root == null)
                    _document.Add(datasets);
                else
                    InsertAfterTemplate(_document.Root, datasets);
            }

            var data = datasets.Elements(datasets.Name.Namespace + "data").FirstOrDefault();
            if (data == null)
            {
                data = new XElement(datasets.Name.Namespace + "data");
                datasets.Add(data);
            }

            DataRoot = new XElement(rootName);
            data.Add(DataRoot);
            return DataRoot;
        }

        /// <summary>Serialize and replace the carrier stream; reset a non-empty form packet.</summary>
        public void Store(PdfDocument document, Action<string> note)
        {
            if (!XfaXmlCarrier.TryGetXfa(document, out var acroForm, out _))
                return;

            if (!_formPacketChecked)
            {
                _formPacketChecked = true;
                // A packet array keeps the form packet in its own stream; a
                // single or collapsed XDP carries it inside _document.
                if (_carrier is Carrier.PacketStream or Carrier.MissingPacket)
                    ResetFormPacketStream(document, (PdfArray)_xfa, note);
                else
                    ResetFormPacketElement(_document, note);
            }

            var bytes = Serialize(_document, _encoding);
            switch (_carrier)
            {
                case Carrier.PacketStream:
                case Carrier.SingleStream:
                    _stream!.DecodedData = bytes;
                    break;

                case Carrier.MissingPacket:
                {
                    var array = (PdfArray)_xfa;
                    _stream = PdfStream.CreateCompressed(bytes);
                    var reference = document.AddIndirectObject(_stream);
                    InsertPacket(document, array, "datasets", reference);
                    _carrier = Carrier.PacketStream;
                    break;
                }

                case Carrier.Collapsed:
                    _stream = PdfStream.CreateCompressed(bytes);
                    acroForm["XFA"] = document.AddIndirectObject(_stream);
                    _xfa = _stream;
                    _carrier = Carrier.SingleStream;
                    break;
            }
            _streamBytes = _stream!.DecodedData;
        }

        private static XElement? FindDatasets(XDocument xml)
        {
            if (xml.Root is not { } root)
                return null;
            IEnumerable<XElement> candidates = root.Name.LocalName == "xdp" ? root.Elements() : new[] { root };
            return candidates.FirstOrDefault(e => e.Name.LocalName == "datasets"
                && e.Name.NamespaceName.StartsWith(DataNamespacePrefix, StringComparison.Ordinal));
        }

        private static void InsertAfterTemplate(XElement xdpRoot, XElement datasets)
        {
            var template = xdpRoot.Elements().FirstOrDefault(e => e.Name.LocalName == "template");
            if (template != null)
                template.AddAfterSelf(datasets);
            else
                xdpRoot.Add(datasets);
        }

        private static void InsertPacket(PdfDocument document, PdfArray array, string name, PdfReference stream)
        {
            // After the template packet, as Designer orders them; else before the postamble.
            int at = -1;
            for (int i = 0; i + 1 < array.Count; i += 2)
            {
                var packetName = (document.Resolve(array[i]) as PdfString)?.Value;
                if (packetName == "template")
                    at = i + 2;
                else if (packetName == "postamble" && at < 0)
                    at = i;
            }
            if (at < 0)
                at = array.Count;
            array.Insert(at, stream);
            array.Insert(at, new PdfString(name));
        }
    }

    internal static PdfStream? FindPacket(PdfDocument document, PdfArray array, string name)
    {
        for (int i = 0; i + 1 < array.Count; i += 2)
        {
            if ((document.Resolve(array[i]) as PdfString)?.Value == name
                && document.Resolve(array[i + 1]) is PdfStream stream)
            {
                return stream;
            }
        }
        return null;
    }

    /// <summary>
    /// Owner decision 8 on #1547: the <c>form</c> packet is an application's
    /// saved Form DOM, which Adobe products overlay on the form AFTER merging
    /// the data (XFA 3.3 p81-82, p1263; the spec defines neither its syntax nor
    /// a checksum). Once the datasets change, a non-empty one could override
    /// the new values on reopen and keeps old state in the file, so it is
    /// reset to an empty element of the same namespace — the shape the IRS
    /// forms ship with. An already-empty packet is left byte-identical.
    /// </summary>
    private const string FormPacketResetNote =
        "XFA form packet (saved form state) reset to empty so the new datasets values apply on reopen.";

    private static void ResetFormPacketStream(PdfDocument document, PdfArray array, Action<string> note)
    {
        var stream = FindPacket(document, array, "form");
        if (stream == null)
            return;
        if (!XfaXmlCarrier.TryLoadXml(stream.DecodedData, out var packet, out var encoding)
            || packet.Root is not { } root)
        {
            note("XFA form packet is not well-formed XML and was left as it is.");
            return;
        }
        if (IsEmptyFormPacket(root))
            return;
        stream.DecodedData = Serialize(new XDocument(new XElement(root.Name)), encoding);
        note(FormPacketResetNote);
    }

    private static void ResetFormPacketElement(XDocument whole, Action<string> note)
    {
        if (whole.Root is not { } xdp || xdp.Name.LocalName != "xdp")
            return;
        var form = xdp.Elements().FirstOrDefault(e => e.Name.LocalName == "form"
            && e.Name.NamespaceName.StartsWith(FormNamespacePrefix, StringComparison.Ordinal));
        if (form == null || IsEmptyFormPacket(form))
            return;
        form.ReplaceWith(new XElement(form.Name));
        note(FormPacketResetNote);
    }

    private static bool IsEmptyFormPacket(XElement form)
        => !form.Attributes().Any(a => !a.IsNamespaceDeclaration)
           && !form.Nodes().Any(n => n is not XText text || !string.IsNullOrWhiteSpace(text.Value));

    /// <summary>
    /// Serialize as <c>XfaXmlCarrier</c> does (declaration kept or omitted as
    /// read, no indentation, detected encoding), with CR in text entitized so
    /// nothing is lost to XML line-end normalization on the next read.
    /// </summary>
    private static byte[] Serialize(XDocument document, Encoding encoding)
    {
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Encoding = encoding,
            OmitXmlDeclaration = document.Declaration == null,
            Indent = false,
            NewLineHandling = NewLineHandling.Entitize,
            CloseOutput = false,
        }))
        {
            document.Save(writer);
        }
        return output.ToArray();
    }
}
