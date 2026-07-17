using Pdfe.Core.Primitives;

namespace Pdfe.Core.Document;

/// <summary>
/// Internal parser for PDF interactive forms (AcroForm).
/// Walks the field tree (/Fields array) and constructs PdfField and PdfAcroForm objects.
/// PDF spec §12.7.
/// </summary>
internal static class PdfAcroFormParser
{
    /// <summary>
    /// Parse an AcroForm dictionary and return a structured PdfAcroForm object.
    /// </summary>
    public static PdfAcroForm Parse(PdfDocument doc, PdfDictionary acroFormDict)
    {
        // Read NeedAppearances flag (default false). Some old fixtures used
        // the pluralized key; accept both, but prefer the spec key.
        bool needsAppearances = acroFormDict.GetBool(
            "NeedAppearances",
            defaultValue: acroFormDict.GetBool("NeedsAppearances", defaultValue: false));

        // Build a page-ref → page-number map for fast lookup
        var pageRefToNumber = PdfOutlineParser.BuildPageRefMap(doc);

        // Widget → page association from every page's own /Annots array
        // (#671: /P is optional per spec, so this is the fallback — and for
        // some widgets, the only source of truth at all).
        var widgetToPage = PdfWidgetAnnotationIndex.BuildWidgetToPageMap(doc);

        // Parse the /Fields array (top-level fields)
        var fields = new List<PdfField>();
        var fieldsObj = acroFormDict.GetOptional("Fields");
        if (fieldsObj != null && doc.Resolve(fieldsObj) is PdfArray fieldsArray)
        {
            foreach (var fieldRef in fieldsArray)
            {
                ParseFieldTree(doc, fieldRef, parentName: "", fields, pageRefToNumber, widgetToPage);
            }
        }

        return new PdfAcroForm(fields.AsReadOnly(), needsAppearances);
    }

    /// <summary>
    /// Recursively walk a field tree, collecting all fields (both branches and leaves).
    /// A "leaf" field is one with no /Kids array, or whose /Kids are Widget annotations.
    /// </summary>
    private static void ParseFieldTree(
        PdfDocument doc,
        PdfObject? fieldObj,
        string parentName,
        List<PdfField> outputFields,
        Dictionary<(int, int), int> pageRefToNumber,
        Dictionary<PdfDictionary, int> widgetToPage)
    {
        // Resolve indirect reference
        if (fieldObj == null || doc.Resolve(fieldObj) is not PdfDictionary fieldDict)
            return;

        // Get the field's partial name (/T)
        string? partialName = fieldDict.GetStringOrNull("T");
        if (partialName == null)
            return; // Skip fields without a name

        // Build the full name by appending to parent chain
        string fullName = string.IsNullOrEmpty(parentName) ? partialName : $"{parentName}.{partialName}";

        // Check if this field has kids (subsidiary fields)
        var kidsObj = fieldDict.GetOptional("Kids");
        bool hasKids = kidsObj != null && doc.Resolve(kidsObj) is PdfArray;

        if (hasKids && kidsObj != null)
        {
            if (doc.Resolve(kidsObj) is PdfArray kidsArray)
            {
                // If all kids are pure Widget annotations (no /T name), treat current node as a leaf.
                // Widget kids WITH /T are sub-fields that happen to also be widgets — recurse into them.
                bool allPureWidgets = kidsArray.All(k =>
                    doc.Resolve(k) is PdfDictionary d &&
                    d.GetNameOrNull("Subtype") == "Widget" &&
                    d.GetStringOrNull("T") == null);

                if (allPureWidgets)
                {
                    var field = ExtractField(doc, fieldDict, fullName, partialName, pageRefToNumber, widgetToPage);
                    if (field != null)
                        outputFields.Add(field);
                }
                else
                {
                    foreach (var kidRef in kidsArray)
                        ParseFieldTree(doc, kidRef, fullName, outputFields, pageRefToNumber, widgetToPage);
                }
            }
        }
        else
        {
            // Terminal field (leaf). Extract its properties and create a PdfField.
            var field = ExtractField(doc, fieldDict, fullName, partialName, pageRefToNumber, widgetToPage);
            if (field != null)
                outputFields.Add(field);
        }
    }

    /// <summary>
    /// Extract a single terminal field from its dictionary and create a PdfField object.
    /// </summary>
    private static PdfField? ExtractField(
        PdfDocument doc,
        PdfDictionary fieldDict,
        string fullName,
        string partialName,
        Dictionary<(int, int), int> pageRefToNumber,
        Dictionary<PdfDictionary, int> widgetToPage)
    {
        // Get field type (/FT: /Btn, /Tx, /Ch, /Sig). FT may be inherited from
        // a parent field — walk /Parent chain if not present locally.
        var fieldTypeStr = ResolveInheritedName(doc, fieldDict, "FT");
        var fieldType = fieldTypeStr switch
        {
            "Btn" => PdfFieldType.Button,
            "Tx" => PdfFieldType.Text,
            "Ch" => PdfFieldType.Choice,
            "Sig" => PdfFieldType.Signature,
            _ => PdfFieldType.Unknown,
        };

        // Get options (/Opt) for choice fields
        IReadOnlyList<string>? options = null;
        if (fieldType == PdfFieldType.Choice)
        {
            options = ExtractOptions(doc, fieldDict.GetOptional("Opt"));
        }

        // Get flags (/Ff) — bit flags for field properties
        int flags = ResolveInheritedInt(doc, fieldDict, "Ff", defaultValue: 0);
        bool isReadOnly = (flags & 0x1) != 0;     // Bit 0
        bool isRequired = (flags & 0x2) != 0;     // Bit 1
        bool isMultiline = (flags & 0x1000) != 0; // Bit 12

        // Get rectangle and page number from Widget annotation.
        // A field can be a widget itself, or have widget kids.
        PdfRectangle? rect = null;
        int? pageNumber = null;
        var widgetDicts = new List<PdfDictionary>();
        var widgets = new List<PdfFieldWidget>();

        // Check if the field itself is a widget annotation
        var subtype = fieldDict.GetNameOrNull("Subtype");
        if (subtype == "Widget")
        {
            widgetDicts.Add(fieldDict);
            (rect, pageNumber) = ExtractWidgetInfo(doc, fieldDict, pageRefToNumber, widgetToPage);
            if (rect != null)
                widgets.Add(new PdfFieldWidget(rect.Value, pageNumber, ExtractWidgetExportValue(doc, fieldDict)));
        }
        else
        {
            // Otherwise, look for widget kids
            var widgetKids = FindWidgetKids(doc, fieldDict);
            widgetDicts.AddRange(widgetKids);
            foreach (var widget in widgetKids)
            {
                var (widgetRect, widgetPageNumber) = ExtractWidgetInfo(doc, widget, pageRefToNumber, widgetToPage);
                if (widgetRect != null)
                    widgets.Add(new PdfFieldWidget(widgetRect.Value, widgetPageNumber, ExtractWidgetExportValue(doc, widget)));
            }

            if (widgets.Count > 0)
            {
                // Use the first Widget annotation
                rect = widgets[0].Rect;
                pageNumber = widgets[0].PageNumber;
            }
        }

        return new PdfField(
            document: doc,
            fullName: fullName,
            partialName: partialName,
            fieldType: fieldType,
            options: options,
            rect: rect,
            pageNumber: pageNumber,
            isReadOnly: isReadOnly,
            isRequired: isRequired,
            isMultiline: isMultiline,
            rawDictionary: fieldDict,
            widgetDictionaries: widgetDicts.AsReadOnly(),
            flags: flags,
            widgets: widgets.AsReadOnly());
    }

    /// <summary>
    /// Find Widget annotation dictionaries associated with a field.
    /// A field's /Kids array may contain Widget annotations (/Subtype /Widget).
    /// </summary>
    private static List<PdfDictionary> FindWidgetKids(PdfDocument doc, PdfDictionary fieldDict)
    {
        var widgets = new List<PdfDictionary>();
        var kidsObj = fieldDict.GetOptional("Kids");
        if (kidsObj == null || doc.Resolve(kidsObj) is not PdfArray kidsArray)
            return widgets;

        foreach (var kidRef in kidsArray)
        {
            if (doc.Resolve(kidRef) is PdfDictionary kidDict)
            {
                var subtype = kidDict.GetNameOrNull("Subtype");
                if (subtype == "Widget")
                    widgets.Add(kidDict);
            }
        }

        return widgets;
    }

    /// <summary>
    /// Extract rectangle and page number from a Widget annotation dictionary.
    /// </summary>
    private static (PdfRectangle? rect, int? pageNumber) ExtractWidgetInfo(
        PdfDocument doc,
        PdfDictionary widgetDict,
        Dictionary<(int, int), int> pageRefToNumber,
        Dictionary<PdfDictionary, int> widgetToPage)
    {
        PdfRectangle? rect = null;
        int? pageNumber = null;

        // Extract /Rect
        var rectObj = widgetDict.GetOptional("Rect");
        if (rectObj != null && doc.Resolve(rectObj) is PdfArray rectArray && rectArray.Count >= 4)
        {
            // Rect = [left bottom right top]
            if (rectArray[0].TryGetNumber(out var left) &&
                rectArray[1].TryGetNumber(out var bottom) &&
                rectArray[2].TryGetNumber(out var right) &&
                rectArray[3].TryGetNumber(out var top))
            {
                rect = new PdfRectangle(left, bottom, right, top);
            }
        }

        // Extract page number from /P (page reference)
        var pageRef = widgetDict.GetOptional("P");
        if (pageRef is PdfReference pr)
        {
            if (pageRefToNumber.TryGetValue((pr.ObjectNum, pr.Generation), out var pn))
                pageNumber = pn;
        }

        // #671: /P is OPTIONAL per spec (§12.5.2) — page association can be
        // established purely by the widget appearing in that page's own
        // /Annots array. Fall back to that when /P is absent, or present but
        // unresolvable (dangling reference).
        if (pageNumber == null && widgetToPage.TryGetValue(widgetDict, out var fallbackPage))
            pageNumber = fallbackPage;

        return (rect, pageNumber);
    }

    /// <summary>
    /// #670 fallback: surface Widget annotations that live in this page's own
    /// /Annots array but were never reached while walking /AcroForm/Fields —
    /// either because the document has no /AcroForm at all, or because the
    /// /Fields tree simply omits them. Per §12.7.3.1 a Widget annotation may BE
    /// its own field dictionary (a "merged" field/widget); such widgets carry
    /// /FT (and usually /V) directly and are legal fields in their own right
    /// even when nothing in /AcroForm/Fields points at them.
    ///
    /// Only widgets that carry /FT directly are surfaced — that is the merged-
    /// widget signal. This deliberately excludes ordinary Widget annotations
    /// that have no field semantics of their own (a widget kid whose field
    /// properties live on a separate parent field dictionary reached via
    /// /Kids — those are already covered by the AcroForm walk, and skipped
    /// here even if <paramref name="alreadyLinked"/> somehow missed them,
    /// because they fail the /FT check).
    ///
    /// Widgets already reached via /AcroForm/Fields (present in
    /// <paramref name="alreadyLinked"/>, compared by reference — see
    /// <see cref="PdfWidgetAnnotationIndex"/>) are skipped so their text isn't
    /// emitted or redacted twice.
    /// </summary>
    public static IReadOnlyList<PdfField> ExtractOrphanedPageWidgetFields(
        PdfDocument doc,
        PdfDictionary pageDict,
        int pageNumber,
        IReadOnlySet<PdfDictionary> alreadyLinked)
    {
        List<PdfField>? result = null;

        foreach (var widget in PdfWidgetAnnotationIndex.GetPageAnnotWidgets(doc, pageDict))
        {
            if (alreadyLinked.Contains(widget)) continue;
            if (widget.GetNameOrNull("FT") == null) continue; // not a merged field/widget

            var partialName = widget.GetStringOrNull("T");
            if (partialName == null) continue; // fields require a name (same rule as ParseFieldTree)

            // The widget's page is already known — it came from this page's
            // own /Annots — so a single-entry map is enough for ExtractField's
            // page-resolution fallback; there's no /P vs. /Annots ambiguity
            // to settle here.
            var widgetToPage = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance)
            {
                [widget] = pageNumber
            };

            var field = ExtractField(
                doc, widget, fullName: partialName, partialName: partialName,
                pageRefToNumber: EmptyPageRefMap, widgetToPage: widgetToPage);
            if (field != null)
                (result ??= new List<PdfField>()).Add(field);
        }

        return (IReadOnlyList<PdfField>?)result ?? Array.Empty<PdfField>();
    }

    private static readonly Dictionary<(int, int), int> EmptyPageRefMap = new();

    /// <summary>
    /// Walk the /Parent chain to find an inherited name entry. Used for /FT,
    /// /Ff, etc. which can be set on an ancestor and inherited by descendants
    /// per PDF §12.7.4.4 (Field hierarchy).
    /// </summary>
    private static string? ResolveInheritedName(PdfDocument doc, PdfDictionary fieldDict, string key)
    {
        var current = fieldDict;
        for (int depth = 0; depth < 16 && current != null; depth++)
        {
            var name = current.GetNameOrNull(key);
            if (name != null) return name;

            var parent = current.GetOptional("Parent");
            if (parent == null) return null;
            current = doc.Resolve(parent) as PdfDictionary;
        }
        return null;
    }

    private static int ResolveInheritedInt(PdfDocument doc, PdfDictionary fieldDict, string key, int defaultValue)
    {
        var current = fieldDict;
        for (int depth = 0; depth < 16 && current != null; depth++)
        {
            if (current.ContainsKey(key))
                return current.GetInt(key, defaultValue);

            var parent = current.GetOptional("Parent");
            if (parent == null) return defaultValue;
            current = doc.Resolve(parent) as PdfDictionary;
        }
        return defaultValue;
    }

    private static string? ExtractWidgetExportValue(PdfDocument doc, PdfDictionary widgetDict)
    {
        var apObj = widgetDict.GetOptional("AP");
        if (apObj == null || doc.Resolve(apObj) is not PdfDictionary ap)
            return null;

        var nObj = ap.GetOptional("N");
        if (nObj == null || doc.Resolve(nObj) is not PdfDictionary normal)
            return null;

        foreach (var key in normal.Keys)
            if (key.Value != "Off")
                return key.Value;

        return null;
    }

    /// <summary>
    /// Extract the /Opt array for choice fields.
    /// /Opt is an array of strings, or an array of [exportValue, displayValue] pairs.
    /// Returns the display values.
    /// </summary>
    private static IReadOnlyList<string>? ExtractOptions(PdfDocument doc, PdfObject? optObj)
    {
        if (optObj == null)
            return null;

        if (doc.Resolve(optObj) is not PdfArray optArray)
            return null;

        var options = new List<string>();
        foreach (var item in optArray)
        {
            if (item is PdfString str)
            {
                options.Add(str.Value);
            }
            else if (item is PdfName name)
            {
                options.Add(name.Value);
            }
            else if (doc.Resolve(item) is PdfArray pair && pair.Count >= 2)
            {
                // [exportValue, displayValue] — we want displayValue (index 1)
                var displayValue = pair[1];
                if (displayValue is PdfString ds)
                    options.Add(ds.Value);
                else if (displayValue is PdfName dn)
                    options.Add(dn.Value);
            }
        }

        return options.Count > 0 ? options.AsReadOnly() : null;
    }
}
