using System.Globalization;
using System.Text;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Xfa;

/// <summary>
/// The AcroForm fields ISO 32000-2 Annex K.2 asks of a writer that creates a PDF with <c>/XFA</c>
/// (#2028, XFA Phase 3 S1), made from the field map when the layout replaces a dynamic form's pages:
/// one field per field-map entry, named by its SOM path (<see cref="XfaFormSom"/>), <c>/V</c> the form
/// value, the appearance the layout drew for the value, no <c>/A</c> or <c>/AA</c>.
/// </summary>
/// <remarks>
/// <para><b>Display only.</b> Every generated field is read-only (<c>/Ff</c> bit 1) whatever
/// <see cref="XfaFieldInfo.Editable"/> says: without the datasets write-back (S2), an edit to
/// <c>/V</c> would leave the datasets behind and break K.2.</para>
/// <para><b>Appearance.</b> The value drawing is in page coordinates with the page's resources, so
/// its form XObject's <c>/BBox</c> equals the widget <c>/Rect</c> and the §12.5.5 mapping is the
/// identity: the widget draws exactly what the page drew before.</para>
/// <para><b>Hidden fields.</b> A field not drawn (hidden, inactive, invisible, or never placed)
/// still gets a field, with one hidden zero-size widget on the first page, so the #2012 cut (which
/// prunes a field left with no widget on a live page) keeps it.</para>
/// <para><b>Record.</b> Each page's <c>/PieceInfo /Excise /Private</c> lists the widgets generated
/// on it (<c>/XfaWidgets</c>), with the page ordinal, the datasets hash and the layout-engine
/// version, so S2 tells generated widgets from user annotations and redaction finds them to
/// flatten (decision 17).</para>
/// </remarks>
internal static class XfaWidgetWriter
{
    /// <summary>The layout engine version recorded with the widgets (1 was Phase 2, values on the page).</summary>
    public const int LayoutEngineVersion = 2;

    public const string WidgetsKey = "XfaWidgets";
    public const string PageOrdinalKey = "XfaPageOrdinal";
    public const string EngineKey = "XfaLayoutEngine";
    public const string DatasetsHashKey = "XfaDatasetsHash";

    private const int ReadOnly = 1;
    private const int Multiline = 1 << 12;
    private const int Password = 1 << 13;
    private const int Radio = 1 << 15;
    private const int Pushbutton = 1 << 16;
    private const int Combo = 1 << 17;
    private const int Edit = 1 << 18;
    private const int MultiSelect = 1 << 21;
    private const int Comb = 1 << 24;

    private const int AnnotPrint = 4;
    private const int AnnotHidden = 2;

    private const string DefaultAppearance = "/Helv 0 Tf 0 g";

    /// <summary>
    /// Build the fields and attach them: widgets to their pages' <c>/Annots</c>, roots to
    /// <c>/AcroForm /Fields</c>. Nothing is attached unless everything was built. Returns the value
    /// drawings the widgets took (the rest the writer draws on the page) and the number of fields.
    /// </summary>
    public static (HashSet<(XfaFormNode Node, int Page)> Taken, int Fields) Emit(
        PdfDocument document,
        IReadOnlyList<XfaFieldInfo> fields,
        IReadOnlyDictionary<(XfaFormNode Node, int Page), XfaFieldValueDrawing> drawings,
        string datasetsHash,
        XfaBudget budget,
        XfaReport report)
    {
        var builder = new Builder(document, drawings, budget, report);
        foreach (var field in fields)
        {
            budget.Tick();
            builder.Add(field, fields);
        }
        builder.Attach(datasetsHash);
        return (builder.Taken, builder.FieldCount);
    }

    /// <summary>
    /// The widgets the layout generated on <paramref name="page"/>, from its record; empty when the
    /// page has none (not a generated page, a Phase 2 layout, or already flattened).
    /// </summary>
    public static List<PdfDictionary> GeneratedWidgets(PdfDocument document, PdfPage page)
    {
        var result = new List<PdfDictionary>();
        if (Record(document, page) is not { } record
            || document.Resolve(record.GetOptional(WidgetsKey) ?? PdfNull.Instance) is not PdfArray widgets)
        {
            return result;
        }
        foreach (var item in widgets)
        {
            if (document.Resolve(item) is PdfDictionary widget)
                result.Add(widget);
        }
        return result;
    }

    /// <summary>
    /// Decision 17: bake the generated widgets into their pages and remove them. Each shown widget's
    /// appearance is stamped into the page content through the AcroForm flatten path
    /// (<see cref="AcroFormFlattener.StampWidgetAppearances"/>); hidden widgets draw nothing and are
    /// dropped. The widgets leave <c>/Annots</c> and their fields leave the field tree, so no hidden or
    /// duplicate copy of a value survives an area redaction of the visible one. <c>/XFA</c> is left for
    /// the redaction's own decision 5. Returns the number of widgets flattened.
    /// </summary>
    public static int Flatten(PdfDocument document)
    {
        // Matched by object number where the arrays hold references (they do for everything the
        // generator wrote): on a reopened file a parsed dictionary can be evicted and re-read between
        // two resolves, and an instance comparison would then miss it, leaving a widget in /Annots
        // whose field already left the tree.
        var widgetNumbers = new HashSet<int>();
        var widgetInstances = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var rootNumbers = new HashSet<int>();
        var rootInstances = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        int count = 0;
        foreach (var page in document.Pages)
        {
            if (Record(document, page) is not { } record
                || document.Resolve(record.GetOptional(WidgetsKey) ?? PdfNull.Instance) is not PdfArray listed
                || listed.Count == 0)
            {
                continue;
            }

            var generated = new List<PdfDictionary>();
            foreach (var item in listed)
            {
                if (item is PdfReference r)
                    widgetNumbers.Add(r.ObjectNum);
                if (document.Resolve(item) is not PdfDictionary widget)
                    continue;
                generated.Add(widget);
                widgetInstances.Add(widget);
                AddRoot(document, item, widget, rootNumbers, rootInstances);
            }
            count += generated.Count;

            var shown = generated
                .Where(w => (Flags(document, w) & AnnotHidden) == 0 && w.GetOptional("AP") != null)
                .ToList();
            AcroFormFlattener.StampWidgetAppearances(document, page, shown);

            if (document.Resolve(page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance) is PdfArray annots)
            {
                for (int i = annots.Count - 1; i >= 0; i--)
                {
                    bool generatedWidget = annots[i] is PdfReference r
                        ? widgetNumbers.Contains(r.ObjectNum)
                        : document.Resolve(annots[i]) is PdfDictionary d && widgetInstances.Contains(d);
                    if (!generatedWidget)
                        continue;
                    if (annots[i] is PdfReference removed)
                        document.Pages.RecordRemovedAnnotation(removed.ObjectNum);
                    annots.RemoveAt(i);
                }
                if (page.Dictionary.GetOptional("Annots") is PdfReference annotsRef)
                    document.ReplaceIndirectObject(annotsRef.ObjectNum, annots);
            }
            record[WidgetsKey] = new PdfArray();
            // Reassigning marks a parsed page dictionary edited, so the object store keeps these
            // nested edits instead of re-reading the file's original (TryEvictFromCache).
            page.Dictionary["PieceInfo"] = page.Dictionary.GetOptional("PieceInfo")!;
        }
        if (count == 0)
            return 0;

        if (document.Resolve(document.Catalog.GetOptional("AcroForm") ?? PdfNull.Instance) is PdfDictionary acroForm
            && document.Resolve(acroForm.GetOptional("Fields") ?? PdfNull.Instance) is PdfArray fields)
        {
            for (int i = fields.Count - 1; i >= 0; i--)
            {
                bool generatedRoot = fields[i] is PdfReference r
                    ? rootNumbers.Contains(r.ObjectNum)
                    : document.Resolve(fields[i]) is PdfDictionary d && rootInstances.Contains(d);
                if (generatedRoot)
                    fields.RemoveAt(i);
            }
            acroForm["Fields"] = acroForm.GetOptional("Fields")!;   // marks the parsed dictionary edited
            if (fields.ObjectNumber is { } fieldsNumber)
                document.ReplaceIndirectObject(fieldsNumber, fields);
        }
        return count;
    }

    /// <summary>The root of a generated widget's field tree (climbing <c>/Parent</c>), by object number and instance.</summary>
    private static void AddRoot(PdfDocument document, PdfObject entry, PdfDictionary widget, HashSet<int> numbers, HashSet<PdfDictionary> instances)
    {
        PdfObject reference = entry;
        var node = widget;
        for (int depth = 0; depth < 64 && node.GetOptional("Parent") is { } parentEntry
             && document.Resolve(parentEntry) is PdfDictionary parent; depth++)
        {
            reference = parentEntry;
            node = parent;
        }
        if (reference is PdfReference r)
            numbers.Add(r.ObjectNum);
        instances.Add(node);
    }

    private static int Flags(PdfDocument document, PdfDictionary widget)
        => document.Resolve(widget.GetOptional("F") ?? PdfNull.Instance) is PdfObject f && f.TryGetNumber(out var n) ? (int)n : 0;

    /// <summary>The page's <c>/PieceInfo /Excise /Private</c> dictionary, or null.</summary>
    public static PdfDictionary? Record(PdfDocument document, PdfPage page)
    {
        if (document.Resolve(page.Dictionary.GetOptional("PieceInfo") ?? PdfNull.Instance) is not PdfDictionary pieceInfo
            || document.Resolve(pieceInfo.GetOptional("Excise") ?? PdfNull.Instance) is not PdfDictionary data)
        {
            return null;
        }
        return document.Resolve(data.GetOptional("Private") ?? PdfNull.Instance) as PdfDictionary;
    }

    /// <summary>A PDF name for an on value: UTF-8 bytes, each written as one character (ISO 32000-2 §7.3.5).</summary>
    internal static string StateName(string onValue)
    {
        var bytes = Encoding.UTF8.GetBytes(onValue);
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
            sb.Append((char)b);
        return sb.ToString();
    }

    private sealed class Builder
    {
        private readonly PdfDocument _document;
        private readonly IReadOnlyDictionary<(XfaFormNode Node, int Page), XfaFieldValueDrawing> _drawings;
        private readonly XfaBudget _budget;
        private readonly XfaReport _report;
        private readonly List<PdfPage> _pages;

        // Non-terminal and terminal field dictionaries by full name; the roots in order.
        private readonly Dictionary<string, (PdfDictionary Dict, PdfReference Ref)> _byName = new(StringComparer.Ordinal);
        private readonly List<PdfReference> _roots = new();

        // Widgets per page index, to attach.
        private readonly Dictionary<int, List<PdfReference>> _widgets = new();

        public HashSet<(XfaFormNode Node, int Page)> Taken { get; } = new();

        public int FieldCount { get; private set; }

        public Builder(
            PdfDocument document,
            IReadOnlyDictionary<(XfaFormNode Node, int Page), XfaFieldValueDrawing> drawings,
            XfaBudget budget,
            XfaReport report)
        {
            _document = document;
            _drawings = drawings;
            _budget = budget;
            _report = report;
            _pages = document.Pages.ToList();
        }

        public void Add(XfaFieldInfo field, IReadOnlyList<XfaFieldInfo> all)
        {
            // Members of an exclusion group are the widgets of the group's radio field.
            if (field.GroupSomPath != null && all.Any(g => g.UiKind == "exclGroup" && g.SomPath == field.GroupSomPath))
            {
                if (field.UiKind != "checkButton")
                    _report.Note($"generated XFA field skipped: '{field.SomPath}' is a {field.UiKind} inside an exclusion group");
                return;
            }

            if (_byName.ContainsKey(field.SomPath))
            {
                _report.Note($"generated XFA field skipped: '{field.SomPath}' named twice");
                return;
            }

            var segments = field.SomPath.Split('.');
            var parent = ParentFor(segments);
            var dict = new PdfDictionary();
            dict.SetString("T", segments[^1]);
            if (parent != null)
                dict["Parent"] = parent.Value.Ref;
            var reference = _document.AddIndirectObject(dict);
            Register(field.SomPath, dict, reference, parent);
            FieldCount++;

            int flags = ReadOnly;   // display only until S2 (#2028)
            switch (field.UiKind)
            {
                case "exclGroup":
                    flags |= Radio;
                    dict.SetName("FT", "Btn");
                    var selected = field.Value ?? string.Empty;
                    dict.SetName("V", selected.Length == 0 ? "Off" : StateName(selected));
                    var kids = new PdfArray();
                    foreach (var member in all.Where(m => m.GroupSomPath == field.SomPath && m.UiKind == "checkButton"))
                    {
                        _budget.Tick();
                        var widget = Widget(member, reference);
                        var on = member.OnValue ?? "1";
                        AddStates(widget, member, on, isOn: selected.Length > 0 && on == selected);
                        kids.Add(_document.AddIndirectObject(widget));
                        Place(member, (PdfReference)kids[^1]);
                    }
                    dict["Kids"] = kids;
                    break;

                case "checkButton":
                {
                    dict.SetName("FT", "Btn");
                    var on = field.OnValue ?? "1";
                    bool isOn = field.Value != null && field.Value == on;
                    dict.SetName("V", isOn ? StateName(on) : "Off");
                    MergeWidget(dict, field, reference);
                    AddStates(dict, field, on, isOn);
                    break;
                }

                case "choiceList":
                {
                    dict.SetName("FT", "Ch");
                    dict.SetString("DA", DefaultAppearance);
                    var open = field.Node.Element.Child("ui")?.Child("choiceList").AttrOr("open", "userControl");
                    if (open is not ("always" or "multiSelect"))
                        flags |= Combo;
                    if (field.MultiSelect)
                        flags |= MultiSelect;
                    if (field.Node.Element.Child("ui")?.Child("choiceList").AttrOr("textEntry", "0") == "1")
                        flags |= Edit;
                    var opt = new PdfArray();
                    foreach (var item in field.Items)
                    {
                        var pair = new PdfArray();
                        pair.Add((PdfObject)new PdfString(item.Save));
                        pair.Add((PdfObject)new PdfString(item.Display));
                        opt.Add(pair);
                    }
                    dict["Opt"] = opt;
                    if (field.Value is { } value)
                    {
                        if (field.MultiSelect)
                        {
                            var values = new PdfArray();
                            foreach (var v in value.Split('\n'))
                                values.Add((PdfObject)new PdfString(v));
                            dict["V"] = values;
                        }
                        else
                        {
                            dict.SetString("V", value);
                        }
                    }
                    MergeWidget(dict, field, reference);
                    break;
                }

                case "signature":
                    dict.SetName("FT", "Sig");
                    MergeWidget(dict, field, reference);
                    break;

                case "button":
                case "imageEdit":
                    // Designer writes both as push buttons; neither has a text value for /V.
                    flags |= Pushbutton;
                    dict.SetName("FT", "Btn");
                    MergeWidget(dict, field, reference);
                    break;

                default:
                {
                    // textEdit, numericEdit, dateTimeEdit, passwordEdit, barcode and any other kind.
                    dict.SetName("FT", "Tx");
                    dict.SetString("DA", DefaultAppearance);
                    if (field.Multiline)
                        flags |= Multiline;
                    if (field.UiKind == "passwordEdit")
                    {
                        // ISO 32000-2 Table 231: a password field's value is never stored in the PDF.
                        flags |= Password;
                    }
                    else if (field.Value is { } value)
                    {
                        dict.SetString("V", value);
                    }
                    if (field.CombCells > 0 && !field.Multiline && field.UiKind != "passwordEdit")
                    {
                        flags |= Comb;
                        dict.SetInt("MaxLen", field.CombCells);
                    }
                    else if (field.MaxChars is > 0 and var max)
                    {
                        dict.SetInt("MaxLen", max);
                    }
                    MergeWidget(dict, field, reference);
                    break;
                }
            }
            dict.SetInt("Ff", flags);
        }

        /// <summary>The non-terminal field for the name's parent segments, made on first use.</summary>
        private (PdfDictionary Dict, PdfReference Ref)? ParentFor(string[] segments)
        {
            (PdfDictionary Dict, PdfReference Ref)? parent = null;
            var name = new StringBuilder();
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (i > 0)
                    name.Append('.');
                name.Append(segments[i]);
                var key = name.ToString();
                if (!_byName.TryGetValue(key, out var node))
                {
                    var dict = new PdfDictionary();
                    dict.SetString("T", segments[i]);
                    dict["Kids"] = new PdfArray();
                    if (parent != null)
                        dict["Parent"] = parent.Value.Ref;
                    node = (dict, _document.AddIndirectObject(dict));
                    Register(key, node.Dict, node.Ref, parent);
                }
                else if (!node.Dict.ContainsKey("Kids"))
                {
                    // A name that is both a terminal field and a prefix cannot come from XfaFormSom
                    // (same-named siblings of any class share one index); refuse rather than corrupt.
                    throw new XfaLayoutException($"generated XFA field name '{key}' is both a field and a parent");
                }
                parent = node;
            }
            return parent;
        }

        private void Register(string name, PdfDictionary dict, PdfReference reference, (PdfDictionary Dict, PdfReference Ref)? parent)
        {
            _byName[name] = (dict, reference);
            if (parent == null)
                _roots.Add(reference);
            else if (_document.Resolve(parent.Value.Dict.GetOptional("Kids") ?? PdfNull.Instance) is PdfArray kids)
                kids.Add(reference);
        }

        /// <summary>A widget annotation dictionary for one drawn (or hidden) occurrence.</summary>
        private PdfDictionary Widget(XfaFieldInfo field, PdfReference parent)
        {
            var widget = new PdfDictionary();
            widget["Parent"] = parent;
            FillWidget(widget, field);
            return widget;
        }

        /// <summary>The field and its one widget in one dictionary (ISO 32000-2 §12.7.4.1).</summary>
        private void MergeWidget(PdfDictionary dict, XfaFieldInfo field, PdfReference reference)
        {
            FillWidget(dict, field);
            if (field.UiKind != "checkButton")
                SetSingleAppearance(dict, field);
            Place(field, reference);
        }

        private void FillWidget(PdfDictionary widget, XfaFieldInfo field)
        {
            widget.SetName("Type", "Annot");
            widget.SetName("Subtype", "Widget");
            var shown = Shown(field);
            var rect = new PdfArray();
            if (shown is { } r)
            {
                rect.Add((PdfObject)new PdfReal(r.X));
                rect.Add((PdfObject)new PdfReal(r.Y));
                rect.Add((PdfObject)new PdfReal(r.Right));
                rect.Add((PdfObject)new PdfReal(r.Y2));
                widget.SetInt("F", AnnotPrint);
            }
            else
            {
                for (int i = 0; i < 4; i++)
                    rect.Add((PdfObject)new PdfInteger(0));
                widget.SetInt("F", AnnotHidden);
            }
            widget["Rect"] = rect;
            var pageIndex = shown != null ? field.PageIndex : 0;
            if (_pages[pageIndex].Reference is { } pageRef)
                widget["P"] = pageRef;
        }

        /// <summary>The widget rectangle when the field is drawn with a box of some size; null otherwise.</summary>
        private PdfPageRect? Shown(XfaFieldInfo field)
            => field.PageIndex >= 0 && field.PageIndex < _pages.Count
               && field.Rect is { Width: > 0, Height: > 0 } rect
               && _drawings.ContainsKey((field.Node, field.PageIndex))
                ? rect
                : null;

        private void Place(XfaFieldInfo field, PdfReference widget)
        {
            var shown = Shown(field);
            int page = shown != null ? field.PageIndex : 0;
            if (!_widgets.TryGetValue(page, out var list))
                _widgets[page] = list = new List<PdfReference>();
            list.Add(widget);
            if (shown != null)
                Taken.Add((field.Node, field.PageIndex));
        }

        private void SetSingleAppearance(PdfDictionary widget, XfaFieldInfo field)
        {
            if (Shown(field) is not { } rect)
                return;
            var ap = new PdfDictionary();
            ap["N"] = Appearance(field, rect, _drawings[(field.Node, field.PageIndex)].Content);
            widget["AP"] = ap;
        }

        /// <summary>Checkbox and radio states: the on value's name draws the mark, <c>/Off</c> draws nothing.</summary>
        private void AddStates(PdfDictionary widget, XfaFieldInfo field, string on, bool isOn)
        {
            var state = StateName(on);
            if (on.Length == 0 || state == "Off")
                _report.Note($"generated XFA check button '{field.SomPath}' has the on value \"{on}\", which collides with its off appearance state");
            widget.SetName("AS", isOn ? state : "Off");
            if (Shown(field) is not { } rect)
                return;
            var normal = new PdfDictionary();
            normal[state] = Appearance(field, rect, _drawings[(field.Node, field.PageIndex)].Content);
            normal["Off"] = Appearance(field, rect, string.Empty);
            var ap = new PdfDictionary();
            ap["N"] = normal;
            widget["AP"] = ap;
        }

        private PdfReference Appearance(XfaFieldInfo field, PdfPageRect rect, string content)
        {
            var bytes = Encoding.Latin1.GetBytes(content);
            var dict = new PdfDictionary();
            dict.SetName("Type", "XObject");
            dict.SetName("Subtype", "Form");
            var bbox = new PdfArray();
            bbox.Add((PdfObject)new PdfReal(rect.X));
            bbox.Add((PdfObject)new PdfReal(rect.Y));
            bbox.Add((PdfObject)new PdfReal(rect.Right));
            bbox.Add((PdfObject)new PdfReal(rect.Y2));
            dict["BBox"] = bbox;
            dict["Resources"] = CopyResources(_pages[field.PageIndex]);
            dict.SetInt("Length", bytes.Length);
            return _document.AddIndirectObject(new PdfStream(dict, bytes));
        }

        /// <summary>
        /// The page's resources, copied one level deep: the value drawing names the same fonts, images
        /// and graphics states the page content does, and a later edit of the page's resource
        /// dictionaries (a flatten stamping this very appearance as an XObject) must not reach it.
        /// </summary>
        private PdfDictionary CopyResources(PdfPage page)
        {
            var copy = new PdfDictionary();
            if (page.Resources is not { } resources)
                return copy;
            foreach (var (key, value) in resources)
            {
                if (_document.Resolve(value) is PdfDictionary category and not PdfStream)
                {
                    var inner = new PdfDictionary();
                    foreach (var (name, item) in category)
                        inner[name] = item;
                    copy[key] = inner;
                }
                else
                {
                    copy[key] = value;
                }
            }
            return copy;
        }

        /// <summary>Attach widgets, fields and the per-page record; the only step that changes reachable objects.</summary>
        public void Attach(string datasetsHash)
        {
            if (_document.Resolve(_document.Catalog.GetOptional("AcroForm") ?? PdfNull.Instance) is not PdfDictionary acroForm)
                throw new XfaLayoutException("no /AcroForm dictionary to hold the generated fields");

            for (int i = 0; i < _pages.Count; i++)
            {
                var page = _pages[i];
                var mine = _widgets.TryGetValue(i, out var list) ? list : new List<PdfReference>();
                if (mine.Count > 0)
                {
                    var annots = _document.Resolve(page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance) as PdfArray ?? new PdfArray();
                    foreach (var widget in mine)
                        annots.Add(widget);
                    page.Dictionary["Annots"] = annots;
                }

                if (Record(_document, page) is { } record)
                {
                    var refs = new PdfArray();
                    foreach (var widget in mine)
                        refs.Add(widget);
                    record[WidgetsKey] = refs;
                    record.SetInt(PageOrdinalKey, i);
                    record.SetInt(EngineKey, LayoutEngineVersion);
                    record.SetString(DatasetsHashKey, datasetsHash);
                }
            }

            if (_roots.Count == 0)
                return;
            var fields = _document.Resolve(acroForm.GetOptional("Fields") ?? PdfNull.Instance) as PdfArray ?? new PdfArray();
            foreach (var root in _roots)
                fields.Add(root);
            // Reassigning (not only appending) marks the parsed dictionary edited, so the object store
            // never evicts it and re-reads the file's original (PdfDocumentObjectStore.TryEvictFromCache).
            acroForm["Fields"] = acroForm.GetOptional("Fields") is PdfReference fieldsRef ? fieldsRef : fields;
            if (fields.ObjectNumber is { } fieldsNumber)
                _document.ReplaceIndirectObject(fieldsNumber, fields);
            EnsureHelvetica(acroForm);
        }

        /// <summary><c>/DR /Font /Helv</c>, which the generated <c>/DA</c> names (ISO 32000-2 §12.7.4.3).</summary>
        private void EnsureHelvetica(PdfDictionary acroForm)
        {
            var dr = _document.Resolve(acroForm.GetOptional("DR") ?? PdfNull.Instance) as PdfDictionary ?? new PdfDictionary();
            var fonts = _document.Resolve(dr.GetOptional("Font") ?? PdfNull.Instance) as PdfDictionary ?? new PdfDictionary();
            if (!fonts.ContainsKey("Helv"))
            {
                var helv = new PdfDictionary();
                helv.SetName("Type", "Font");
                helv.SetName("Subtype", "Type1");
                helv.SetName("BaseFont", "Helvetica");
                helv.SetName("Encoding", "WinAnsiEncoding");
                fonts["Helv"] = _document.AddIndirectObject(helv);
            }
            if (dr.GetOptional("Font") is not PdfReference)
                dr["Font"] = fonts;
            if (acroForm.GetOptional("DR") is not PdfReference)
                acroForm["DR"] = dr;
            else if (dr.ObjectNumber is { } drNumber)
                _document.ReplaceIndirectObject(drNumber, dr);
        }
    }
}
