using System.Globalization;
using System.Xml.Linq;
using Excise.Core.Document;

namespace Excise.Core.Xfa;

/// <summary><c>access</c> of a field, exclusion group or subform (XFA 3.3 p727, p842), least restrictive first.</summary>
internal enum XfaAccess
{
    Open,
    ReadOnly,
    Protected,
    NonInteractive,
}

/// <summary><c>bind match</c> (XFA 3.3 p176), as the merge applies it.</summary>
internal enum XfaBindingKind
{
    /// <summary>No data node: <c>match="none"</c>, or a nameless object, which never binds by name (p95).</summary>
    None,

    /// <summary><c>match="once"</c> (the default): the standard matching rules.</summary>
    Normal,

    /// <summary><c>match="global"</c>: one data node may feed many fields.</summary>
    Global,

    /// <summary><c>match="dataRef"</c>: the node the <c>ref</c> SOM expression names.</summary>
    DataRef,
}

/// <summary><c>calculate override</c> (XFA 3.3 p641-642).</summary>
internal enum XfaCalculateOverride
{
    /// <summary>No calculation; the default when the object has no <c>calculate</c> element.</summary>
    Disabled,

    /// <summary>The default when a <c>calculate</c> element is present: user values are refused.</summary>
    Error,

    /// <summary>The calculated value is mandatory; user values are ignored.</summary>
    Ignore,

    /// <summary>The user may override the calculated value after a warning.</summary>
    Warning,
}

/// <summary>
/// One field or exclusion group of the merged form as laid out (#2027, XFA Phase 3 S0): where it is,
/// what it holds and whether a user may change it. Read-only; nothing consumes it yet (S1 makes widgets
/// from it, S2 writes values through it).
/// </summary>
internal sealed record XfaFieldInfo
{
    /// <summary>The merged form node.</summary>
    public required XfaFormNode Node { get; init; }

    /// <summary>
    /// Full SOM name (the rule is on <see cref="XfaFormSom"/>, #2035), resolvable by
    /// <see cref="XfaFormSom.ResolveChain"/> for fields of the form body: named objects indexed among
    /// same-named SOM siblings, transparent objects (nameless subforms and exclusion groups, areas,
    /// <c>scope="none"</c>) not written, nameless or dotted objects by class (<c>#field[n]</c>), no
    /// leading <c>form.</c>. Page-area content is named <c>root[0].#pageSet[0].PageArea[k]...</c> with
    /// k the page area's occurrence. It is also the generated AcroForm field's full name (#2028).
    /// </summary>
    public required string SomPath { get; init; }

    /// <summary>The template <c>name</c>; null when unnamed.</summary>
    public string? Name { get; init; }

    /// <summary>The SOM name of the exclusion group a member field belongs to; null otherwise.</summary>
    public string? GroupSomPath { get; init; }

    /// <summary>0-based page index, or -1 when the object is not drawn (hidden, inactive, invisible).</summary>
    public int PageIndex { get; init; } = -1;

    /// <summary>The layout box in content-stream points (bottom-left origin); null when not drawn.</summary>
    public PdfPageRect? Rect { get; init; }

    /// <summary>The <c>ui</c> widget (<c>textEdit</c>, <c>choiceList</c>, <c>checkButton</c>...); <c>exclGroup</c> for a group.</summary>
    public required string UiKind { get; init; }

    /// <summary><c>presence</c>: visible, hidden, invisible or inactive.</summary>
    public required string Presence { get; init; }

    /// <summary>The raw value (bound data, else the template's); for a group, the selected member's on value or "".</summary>
    public string? Value { get; init; }

    /// <summary>
    /// The most restrictive <c>access</c> of the object and every enclosing subform and exclusion group:
    /// since XFA 2.8 a subform's access is the default for its content, and content may only restrict it
    /// further (p727, p842).
    /// </summary>
    public XfaAccess Access { get; init; }

    /// <summary>The object has its own <c>calculate</c> element.</summary>
    public bool HasCalculate { get; init; }

    public XfaCalculateOverride CalculateOverride { get; init; }

    /// <summary><c>error</c> and <c>ignore</c> refuse a user value (p641-642).</summary>
    public bool CalculateBlocksUserValue => HasCalculate && CalculateOverride is XfaCalculateOverride.Error or XfaCalculateOverride.Ignore;

    /// <summary>Choice lists: (display, save) pairs from <c>bindItems</c> or the template (p758-760).</summary>
    public IReadOnlyList<XfaItem> Items { get; init; } = Array.Empty<XfaItem>();

    /// <summary>The items came from <c>bindItems</c> in the datasets (p212, p624).</summary>
    public bool ItemsFromData { get; init; }

    /// <summary>Check buttons: the on, off and neutral values (p759: a missing one is the null string).</summary>
    public string? OnValue { get; init; }

    public string? OffValue { get; init; }

    public string? NeutralValue { get; init; }

    /// <summary>Exclusion groups: short or long data format (p196-197).</summary>
    public XfaExclGroupFormat GroupFormat { get; init; }

    /// <summary>A <c>multiSelect</c> choice list, bound to a data group with one child per value (p198).</summary>
    public bool MultiSelect { get; init; }

    /// <summary><c>value/text maxChars</c>; null when absent.</summary>
    public int? MaxChars { get; init; }

    public bool Multiline { get; init; }

    /// <summary>Comb cell count; 0 when the text edit is not a comb.</summary>
    public int CombCells { get; init; }

    public XfaBindingKind Binding { get; init; }

    /// <summary>The data node the merge bound (for a long-format group, null; its members carry theirs).</summary>
    public XElement? BoundData { get; init; }

    /// <summary>
    /// When nothing is bound but the object binds: where a data node would be created, as a SOM
    /// expression from <c>$record</c> (normal), the <c>ref</c> (dataRef), or the bare name (global).
    /// </summary>
    public string? CreatablePath { get; init; }

    /// <summary>A <c>bind picture</c> (p176-177): excise applies none yet (#2019).</summary>
    public bool HasBindPicture { get; init; }

    /// <summary>The form carries an XML signature (p559-562); its signed data must not change.</summary>
    public bool XmlSignature { get; init; }

    /// <summary>Content of a page area (page furniture). The merge never binds it to data.</summary>
    public bool InPageArea { get; init; }

    /// <summary>
    /// A user may set this value: access open, no calculation that refuses user values, a binding, no
    /// bind picture excise cannot apply, no XML signature, not page-area content (which excise's
    /// merge never binds), and a widget that takes a value from the user: buttons, barcodes,
    /// signatures and image fields never do here (#2035).
    /// </summary>
    public bool Editable => Access == XfaAccess.Open
        && UiKind is not ("button" or "barcode" or "signature" or "imageEdit")
        && !CalculateBlocksUserValue
        && Binding != XfaBindingKind.None
        && !HasBindPicture
        && !XmlSignature
        && !InPageArea;
}

/// <summary>Builds <see cref="XfaLayoutResult.Fields"/> from the merged form and its pages.</summary>
internal static class XfaFieldMap
{
    public static List<XfaFieldInfo> Build(
        XfaFormNode root,
        IReadOnlyList<XfaPage> pages,
        IReadOnlyList<PdfPage> documentPages,
        bool xmlSignature,
        XfaBudget budget)
    {
        // Where each object was drawn: page index and top-left page rectangle, every occurrence.
        var placements = new Dictionary<XfaFormNode, List<(int Page, XfaRect Rect)>>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < pages.Count; i++)
        {
            foreach (var paint in pages[i].Paints)
            {
                budget.Tick();
                var node = paint.Box.Node;
                bool isField = node.Kind == XfaNodeKind.Field && !paint.DecorationOnly;
                bool isGroup = node.Kind == XfaNodeKind.ExclGroup && paint.DecorationOnly;
                if (!isField && !isGroup)
                    continue;
                if (!placements.TryGetValue(node, out var list))
                    placements[node] = list = new List<(int, XfaRect)>();
                list.Add((i, paint.Rect));
            }
        }

        var result = new List<XfaFieldInfo>();
        var context = new Context(placements, documentPages, xmlSignature, budget, result);

        var paths = XfaFormSom.Paths(root, budget);
        context.Walk(root, paths, AccessOf(root.Element), new List<XfaFormNode> { root }, groupPath: null,
            pageArea: false, onPage: -1, depth: 0);

        // Page-area content is laid out on every page that uses the area: one entry per page.
        var rootPath = paths[root];
        var occurrences = new Dictionary<XfaPageArea, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < pages.Count; i++)
        {
            var area = pages[i].Area;
            occurrences.TryGetValue(area, out var k);
            occurrences[area] = k + 1;
            if (area.Fixed?.Node is not { } areaNode)
                continue;

            var areaPaths = XfaFormSom.Paths(areaNode, budget, rootIsEntered: true);
            var areaRoot = areaPaths[areaNode];
            // A nameless or dotted page area is written by class, as XfaFormSom writes any object the
            // normal syntax cannot reach (#2035); the page set is always written by class.
            var areaKey = area.Element.Attr("name") is { Length: > 0 } n && !n.Contains('.', StringComparison.Ordinal) ? n : "#pageArea";
            var prefix = (rootPath.Length == 0 ? string.Empty : rootPath + ".")
                + $"#pageSet[0].{areaKey}[{k.ToString(CultureInfo.InvariantCulture)}]";
            var rebased = new Dictionary<XfaFormNode, string>(ReferenceEqualityComparer.Instance);
            foreach (var (node, path) in areaPaths)
                rebased[node] = prefix + path[areaRoot.Length..];
            context.Walk(areaNode, rebased, AccessOf(root.Element), new List<XfaFormNode> { root, areaNode },
                groupPath: null, pageArea: true, onPage: i, depth: 0);
        }

        return result;
    }

    private sealed class Context
    {
        private readonly Dictionary<XfaFormNode, List<(int Page, XfaRect Rect)>> _placements;
        private readonly IReadOnlyList<PdfPage> _documentPages;
        private readonly bool _xmlSignature;
        private readonly XfaBudget _budget;
        private readonly List<XfaFieldInfo> _result;

        public Context(
            Dictionary<XfaFormNode, List<(int Page, XfaRect Rect)>> placements,
            IReadOnlyList<PdfPage> documentPages,
            bool xmlSignature,
            XfaBudget budget,
            List<XfaFieldInfo> result)
        {
            _placements = placements;
            _documentPages = documentPages;
            _xmlSignature = xmlSignature;
            _budget = budget;
            _result = result;
        }

        public void Walk(
            XfaFormNode node,
            IReadOnlyDictionary<XfaFormNode, string> paths,
            XfaAccess inherited,
            List<XfaFormNode> chain,
            string? groupPath,
            bool pageArea,
            int onPage,
            int depth)
        {
            XfaBudget.CheckDepth(depth);
            foreach (var child in node.Children)
            {
                _budget.Tick();
                var access = Restrict(inherited, AccessOf(child.Element));
                chain.Add(child);
                if (child.Kind is XfaNodeKind.Field or XfaNodeKind.ExclGroup && paths.TryGetValue(child, out var path))
                    _result.Add(Describe(child, path, access, chain, groupPath, pageArea, onPage));

                if (child.IsContainer)
                {
                    var innerGroup = child.Kind == XfaNodeKind.ExclGroup && paths.TryGetValue(child, out var gp) ? gp : groupPath;
                    Walk(child, paths, access, chain, innerGroup, pageArea, onPage, depth + 1);
                }
                chain.RemoveAt(chain.Count - 1);
            }
        }

        private XfaFieldInfo Describe(
            XfaFormNode node, string path, XfaAccess access, List<XfaFormNode> chain, string? groupPath, bool pageArea, int onPage)
        {
            var e = node.Element;
            bool isGroup = node.Kind == XfaNodeKind.ExclGroup;
            var widget = isGroup ? null : Widget(e);
            var ui = isGroup ? "exclGroup" : widget?.Name.LocalName ?? "textEdit";

            int pageIndex = -1;
            PdfPageRect? rect = null;
            if (_placements.TryGetValue(node, out var drawn))
            {
                // A body object is drawn once (an exclusion group split over pages: its first
                // fragment); page-area content once per page that uses the area.
                int at = pageArea ? drawn.FindIndex(d => d.Page == onPage) : 0;
                if (at >= 0)
                {
                    pageIndex = drawn[at].Page;
                    rect = ToContentPoints(pageIndex, drawn[at].Rect);
                }
            }

            var calculate = e.Child("calculate");
            var overrideValue = calculate == null
                ? XfaCalculateOverride.Disabled
                : calculate.AttrOr("override", "error") switch
                {
                    "disabled" => XfaCalculateOverride.Disabled,
                    "ignore" => XfaCalculateOverride.Ignore,
                    "warning" => XfaCalculateOverride.Warning,
                    _ => XfaCalculateOverride.Error,
                };

            var binding = BindingOf(e);
            var bound = node.BoundData;
            bool isCheck = ui == "checkButton";
            bool inGroup = groupPath != null;
            var items = ui == "choiceList" ? XfaValues.ChoiceItems(node) : Array.Empty<XfaItem>();
            var checkItems = isCheck ? XfaValues.ItemTexts(e.ChildrenNamed("items").FirstOrDefault()) : null;

            return new XfaFieldInfo
            {
                Node = node,
                SomPath = path,
                Name = e.Attr("name") is { Length: > 0 } name ? name : null,
                GroupSomPath = isGroup ? null : groupPath,
                PageIndex = pageIndex,
                Rect = rect,
                UiKind = ui,
                Presence = node.Presence,
                Value = isGroup ? GroupValue(node) : node.Value,
                Access = access,
                HasCalculate = calculate != null,
                CalculateOverride = overrideValue,
                Items = items,
                ItemsFromData = ui == "choiceList" && node.BoundItems != null,
                OnValue = isCheck ? XfaValues.OnValue(e) : null,
                OffValue = isCheck ? XfaValues.OffValue(e) : null,
                // p759: the third value is the neutral one, ignored for a radio button.
                NeutralValue = isCheck && !inGroup ? checkItems!.Skip(2).FirstOrDefault() ?? string.Empty : null,
                GroupFormat = isGroup ? node.Format : XfaExclGroupFormat.Unbound,
                MultiSelect = ui == "choiceList" && widget.AttrOr("open", "userControl") == "multiSelect",
                MaxChars = MaxChars(e),
                Multiline = ui == "textEdit" && widget.AttrOr("multiLine", "0") == "1",
                CombCells = CombCells(e, widget, ui),
                Binding = binding,
                BoundData = bound,
                CreatablePath = bound == null && binding != XfaBindingKind.None && !(isGroup && node.Format == XfaExclGroupFormat.Long)
                    ? CreatablePath(node, chain, binding)
                    : null,
                HasBindPicture = !string.IsNullOrWhiteSpace(e.Child("bind")?.Child("picture")?.Value),
                XmlSignature = _xmlSignature,
                InPageArea = pageArea,
            };
        }

        private PdfPageRect ToContentPoints(int pageIndex, XfaRect rect)
        {
            // Layout boxes are top-left page points (the visual space of an unrotated page that
            // XfaPdfWriter created); convert once, at this boundary.
            var page = _documentPages[pageIndex];
            var visual = PdfPageRect.VisualPoints(page.PageNumber, rect.X, rect.Y, Math.Max(0, rect.W), Math.Max(0, rect.H));
            return PdfCoordinateMapper.ToContentPoints(page, visual);
        }
    }

    private static XElement? Widget(XElement field)
        => field.Child("ui")?.Elements().FirstOrDefault(w => w.Name.LocalName is not ("extras" or "picture"));

    public static XfaAccess AccessOf(XElement element) => element.Attr("access")?.Trim() switch
    {
        "readOnly" => XfaAccess.ReadOnly,
        "protected" => XfaAccess.Protected,
        "nonInteractive" => XfaAccess.NonInteractive,
        _ => XfaAccess.Open,
    };

    /// <summary>p727: precedence nonInteractive, protected, readOnly, open; the more restrictive wins.</summary>
    private static XfaAccess Restrict(XfaAccess inherited, XfaAccess own) => own > inherited ? own : inherited;

    private static XfaBindingKind BindingOf(XElement element)
    {
        bool named = !string.IsNullOrEmpty(element.Attr("name"));
        return element.Child("bind").AttrOr("match", "once") switch
        {
            "none" => XfaBindingKind.None,
            "dataRef" => XfaBindingKind.DataRef,
            "global" => named ? XfaBindingKind.Global : XfaBindingKind.None,
            _ => named ? XfaBindingKind.Normal : XfaBindingKind.None,
        };
    }

    private static string GroupValue(XfaFormNode group)
    {
        foreach (var member in group.Children)
        {
            if (member.Kind == XfaNodeKind.Field && member.Value != null && member.Value == XfaValues.OnValue(member.Element))
                return member.Value;
        }
        return string.Empty;
    }

    private static int? MaxChars(XElement field)
    {
        var text = field.Child("value")?.Child("text");
        if (text?.Attr("maxChars") is not { } raw)
            return null;
        return int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static int CombCells(XElement field, XElement? widget, string ui)
    {
        if (ui != "textEdit" || widget?.Child("comb") is not { } comb)
            return 0;
        var cells = comb.IntAttr("numberOfCells", 0);
        return cells > 0 ? cells : MaxChars(field) ?? 0;
    }

    /// <summary>
    /// Where the data node of an unbound object would go: the named, binding containers from the
    /// record down (nameless and <c>match="none"</c> or global subforms are transparent to binding, as
    /// in the merge), each indexed among its same-named siblings. A dataRef ancestor restarts the path
    /// at its <c>ref</c>. S2 creates nodes along it (XFA 3.3 p199, p209).
    /// </summary>
    private static string CreatablePath(XfaFormNode node, List<XfaFormNode> chain, XfaBindingKind binding)
    {
        var e = node.Element;
        if (binding == XfaBindingKind.DataRef)
            return e.Child("bind").Attr("ref") ?? string.Empty;
        if (binding == XfaBindingKind.Global)
            return e.Attr("name") ?? string.Empty;

        var path = "$record";
        for (int i = 1; i < chain.Count; i++)
        {
            var current = chain[i];
            var name = current.Element.Attr("name");
            var match = current.Element.Child("bind").AttrOr("match", "once");
            bool last = i == chain.Count - 1;
            if (!last && current.Kind == XfaNodeKind.Subform)
            {
                if (match == "dataRef")
                {
                    path = current.Element.Child("bind").Attr("ref") ?? path;
                    continue;
                }
                if (string.IsNullOrEmpty(name) || match is "none" or "global")
                    continue;
            }
            else if (!last && current.Kind == XfaNodeKind.ExclGroup)
            {
                if (current.Format != XfaExclGroupFormat.Long || current.DataScope?.Name.LocalName != name)
                    continue;
            }
            else if (!last)
            {
                continue;   // areas are transparent
            }

            var parent = chain[i - 1];
            int index = 0;
            foreach (var sibling in parent.Children)
            {
                if (ReferenceEquals(sibling, current))
                    break;
                if (sibling.Kind == current.Kind && sibling.Element.Attr("name") == name)
                    index++;
            }
            path += "." + name + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
        }
        return path;
    }
}
