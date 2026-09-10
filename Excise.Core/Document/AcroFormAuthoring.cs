using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// Programmatic AcroForm authoring — create new form fields on a PDF that
/// may not yet have an interactive form. PDF spec §12.7.
///
/// Pattern:
///   <code>
///   var field = doc.AddTextField(pageNumber: 1,
///       rect: new PdfRectangle(72, 700, 300, 720),
///       fieldName: "Name");
///   field.SetValue("Alice");
///   doc.Save("filled.pdf");
///   </code>
///
/// All Add* methods:
///   • Create the /AcroForm catalog entry if absent.
///   • Append a new widget annotation to the target page's /Annots (creating
///     /Annots if absent).
///   • Append the widget's reference to /AcroForm/Fields.
///   • Set /AcroForm/NeedAppearances = true so readers regenerate the visual
///     appearance from the field state.
///   • Return a fully-parsed <see cref="PdfField"/> the caller can
///     immediately call <c>SetValue</c> on.
///
/// The same widget object stands in for both the field-tree node and the
/// annotation — a common shortcut allowed by the PDF spec for "leaf field
/// has a single widget" (§12.7.3.3 NOTE).
/// </summary>
public static class AcroFormAuthoring
{
    /// <summary>
    /// Add a new text input field to <paramref name="pageNumber"/> at
    /// <paramref name="rect"/> (in PDF points, bottom-left origin).
    /// </summary>
    public static PdfField AddTextField(
        this PdfDocument document,
        int pageNumber,
        PdfRectangle rect,
        string fieldName,
        string? defaultValue = null,
        bool multiline = false,
        bool readOnly = false,
        bool required = false,
        string? tooltip = null,
        int? maxLength = null,
        bool comb = false,
        Graphics.PdfFont? appearanceFont = null)
    {
        ValidateName(fieldName);

        var widget = NewWidgetDict(rect);
        widget.SetName("FT", "Tx");
        widget.SetString("T", fieldName);
        if (defaultValue != null)
            widget.SetString("V", defaultValue);
        SetTooltip(widget, tooltip);

        // /MaxLen caps the input length; required for a comb field.
        if (maxLength.HasValue)
            widget.SetInt("MaxLen", maxLength.Value);

        int flags = 0;
        if (readOnly)  flags |= 0x1;        // ReadOnly
        if (required)  flags |= 0x2;        // Required
        if (multiline) flags |= 0x1000;     // Multiline (bit 13)
        // Comb (bit 25) lays text into /MaxLen equal cells. Spec §12.7.4.3:
        // valid only with /MaxLen and not combined with Multiline/Password/
        // FileSelect — ignore the request if those preconditions aren't met.
        if (comb && maxLength.HasValue && !multiline)
            flags |= 0x1000000;
        if (flags != 0) widget.SetInt("Ff", flags);

        // Default appearance: black text in appearanceFont, or 10-point
        // Helvetica when none is given. The font resource is registered in the
        // AcroForm /DR so the /DA name resolves.
        widget.SetString("DA", DefaultAppearance(document, pageNumber, appearanceFont));

        return AttachWidget(document, pageNumber, widget, fieldName);
    }

    /// <summary>
    /// Add a date text field — a text field carrying Acrobat date format/keystroke
    /// JavaScript actions so conforming viewers validate and format input, plus an
    /// optional <paramref name="tooltip"/> (<c>/TU</c>) accessible name.
    /// <paramref name="format"/> is an Acrobat date mask, e.g. <c>"mm/dd/yyyy"</c>
    /// or <c>"yyyy-mm-dd"</c>.
    /// </summary>
    public static PdfField AddDateField(
        this PdfDocument document,
        int pageNumber,
        PdfRectangle rect,
        string fieldName,
        string format = "yyyy-mm-dd",
        string? defaultValue = null,
        bool required = false,
        string? tooltip = null,
        Graphics.PdfFont? appearanceFont = null)
    {
        ValidateName(fieldName);

        var widget = NewWidgetDict(rect);
        widget.SetName("FT", "Tx");
        widget.SetString("T", fieldName);
        if (defaultValue != null)
            widget.SetString("V", defaultValue);
        SetTooltip(widget, tooltip);

        int flags = 0;
        if (required) flags |= 0x2;
        if (flags != 0) widget.SetInt("Ff", flags);
        widget.SetString("DA", DefaultAppearance(document, pageNumber, appearanceFont));

        // /AA additional-actions: format (F) on display, keystroke (K) on input.
        var format1 = new PdfDictionary();
        format1.SetName("S", "JavaScript");
        format1.SetString("JS", $"AFDate_FormatEx(\"{format}\");");
        var keystroke = new PdfDictionary();
        keystroke.SetName("S", "JavaScript");
        keystroke.SetString("JS", $"AFDate_KeystrokeEx(\"{format}\");");
        var aa = new PdfDictionary();
        aa["F"] = format1;
        aa["K"] = keystroke;
        widget["AA"] = aa;

        return AttachWidget(document, pageNumber, widget, fieldName);
    }

    /// <summary>
    /// Add a checkbox. Toggle by calling <c>field.SetValue("Yes" | "Off")</c>.
    /// </summary>
    public static PdfField AddCheckBox(
        this PdfDocument document,
        int pageNumber,
        PdfRectangle rect,
        string fieldName,
        bool defaultChecked = false,
        bool readOnly = false,
        string? tooltip = null)
    {
        ValidateName(fieldName);

        var widget = NewWidgetDict(rect);
        widget.SetName("FT", "Btn");
        widget.SetString("T", fieldName);
        widget.SetName("V", defaultChecked ? "Yes" : "Off");
        widget.SetName("AS", defaultChecked ? "Yes" : "Off");
        SetTooltip(widget, tooltip);
        if (readOnly) widget.SetInt("Ff", 0x1);

        return AttachWidget(document, pageNumber, widget, fieldName);
    }

    /// <summary>
    /// Add a single-select choice (combo-box) field. Pass display values;
    /// the user-selected value is stored as <c>field.Value</c>.
    /// </summary>
    public static PdfField AddChoiceField(
        this PdfDocument document,
        int pageNumber,
        PdfRectangle rect,
        string fieldName,
        IEnumerable<string> options,
        string? defaultValue = null,
        bool readOnly = false,
        string? tooltip = null,
        Graphics.PdfFont? appearanceFont = null)
    {
        ValidateName(fieldName);
        var optList = options?.ToList() ?? new List<string>();
        if (optList.Count == 0)
            throw new ArgumentException("Choice fields require at least one option.", nameof(options));

        var widget = NewWidgetDict(rect);
        widget.SetName("FT", "Ch");
        widget.SetString("T", fieldName);
        SetTooltip(widget, tooltip);

        var optArr = new PdfArray();
        foreach (var opt in optList)
            optArr.Add((PdfObject)new PdfString(opt));
        widget["Opt"] = optArr;

        if (defaultValue != null)
        {
            if (!optList.Contains(defaultValue))
                throw new ArgumentException(
                    $"Default value '{defaultValue}' is not one of the supplied options.",
                    nameof(defaultValue));
            widget.SetString("V", defaultValue);
        }

        // Combo flag (Ff bit 18) — show as dropdown rather than list-box.
        int flags = 1 << 17;
        if (readOnly) flags |= 0x1;
        widget.SetInt("Ff", flags);
        widget.SetString("DA", DefaultAppearance(document, pageNumber, appearanceFont));

        return AttachWidget(document, pageNumber, widget, fieldName);
    }

    /// <summary>
    /// Add a signature placeholder field. The widget reserves the visual
    /// region; actual signing is a separate operation handled by the
    /// signing API.
    /// </summary>
    public static PdfField AddSignatureField(
        this PdfDocument document,
        int pageNumber,
        PdfRectangle rect,
        string fieldName,
        string? tooltip = null)
    {
        ValidateName(fieldName);
        var widget = NewWidgetDict(rect);
        widget.SetName("FT", "Sig");
        widget.SetString("T", fieldName);
        SetTooltip(widget, tooltip);
        return AttachWidget(document, pageNumber, widget, fieldName);
    }

    /// <summary>
    /// Widget/annotation tab-traversal order for a page (<c>/Tabs</c>, §12.5).
    /// </summary>
    public enum TabOrder
    {
        /// <summary>Rows, left-to-right then top-to-bottom (<c>/R</c>).</summary>
        Row,
        /// <summary>Columns, top-to-bottom then left-to-right (<c>/C</c>).</summary>
        Column,
        /// <summary>Document logical-structure order (<c>/S</c>) — recommended for accessibility.</summary>
        Structure
    }

    /// <summary>
    /// Set the tab-traversal order of a page's annotations/fields by writing the
    /// page <c>/Tabs</c> entry. <see cref="TabOrder.Structure"/> follows the
    /// document's logical structure tree and is the accessible choice.
    /// </summary>
    public static void SetTabOrder(this PdfDocument document, int pageNumber, TabOrder order)
    {
        var page = document.GetPage(pageNumber);
        var name = order switch
        {
            TabOrder.Row => "R",
            TabOrder.Column => "C",
            _ => "S"
        };
        page.Dictionary.SetName("Tabs", name);
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    /// <summary>Set the field's <c>/TU</c> (tooltip / accessible name) when provided.</summary>
    private static void SetTooltip(PdfDictionary widget, string? tooltip)
    {
        if (!string.IsNullOrEmpty(tooltip))
            widget.SetString("TU", tooltip);
    }

    private static PdfDictionary NewWidgetDict(PdfRectangle rect)
    {
        var widget = new PdfDictionary();
        widget.SetName("Type", "Annot");
        widget.SetName("Subtype", "Widget");

        var rectArr = new PdfArray();
        rectArr.Add((PdfObject)new PdfReal(rect.Left));
        rectArr.Add((PdfObject)new PdfReal(rect.Bottom));
        rectArr.Add((PdfObject)new PdfReal(rect.Right));
        rectArr.Add((PdfObject)new PdfReal(rect.Top));
        widget["Rect"] = rectArr;

        return widget;
    }

    private static PdfField AttachWidget(
        PdfDocument document,
        int pageNumber,
        PdfDictionary widget,
        string fieldName)
    {
        var page = document.GetPage(pageNumber);
        var pageDict = page.Dictionary;

        // 1. Wire the widget to its page via /P.
        // We don't have the page's own indirect ref handy on PdfPage, so
        // walk the catalog/pages chain via PageCollection. Using a fresh
        // indirect object for the widget keeps it serializable as a
        // top-level object the writer can emit.
        var pageRef = FindPageRef(document, pageNumber);
        if (pageRef != null)
            widget["P"] = pageRef;

        var widgetRef = document.AddIndirectObject(widget);

        // 2. Append to the page's /Annots array (create if absent).
        var annotsObj = pageDict.GetOptional("Annots");
        PdfArray annots;
        if (annotsObj == null)
        {
            annots = new PdfArray();
            pageDict["Annots"] = annots;
        }
        else if (document.Resolve(annotsObj) is PdfArray existing)
        {
            annots = existing;
        }
        else
        {
            annots = new PdfArray();
            pageDict["Annots"] = annots;
        }
        annots.Add(widgetRef);

        // 3. Get-or-create /Catalog/AcroForm and append to /Fields.
        var acroForm = EnsureAcroForm(document);
        var fieldsObj = acroForm.GetOptional("Fields");
        PdfArray fields;
        if (fieldsObj != null && document.Resolve(fieldsObj) is PdfArray existingFields)
        {
            fields = existingFields;
        }
        else
        {
            fields = new PdfArray();
            acroForm["Fields"] = fields;
        }
        fields.Add(widgetRef);

        // 4. Tell readers to regenerate appearance.
        acroForm.SetBool("NeedAppearances", true);

        // 5. Re-parse so the caller gets a hydrated PdfField with all
        // computed properties (rect, page, type) populated.
        var form = document.GetAcroForm()!;
        return form.FindField(fieldName)
            ?? throw new InvalidOperationException(
                $"Internal error: just-added field '{fieldName}' not found by parser.");
    }

    /// <summary>
    /// Build a widget's <c>/DA</c> (default appearance) string and register the
    /// font it names in the AcroForm <c>/DR</c> so the name resolves.
    ///
    /// <para>With no <paramref name="font"/> this is the historical
    /// <c>/Helv 10 Tf 0 g</c> — the non-embedded base-14 Helvetica. That is a
    /// PDF/A violation waiting to happen: a viewer generates the field's
    /// appearance from this string, so a document whose body text is embedded
    /// still renders (and archives) its form fields with a font that is not in
    /// the file (#1435). Passing the document's real font — which
    /// <see cref="Authoring.PdfDocumentBuilder.DefaultFont"/> now does — keeps
    /// the /DA on the embedded program instead.</para>
    /// </summary>
    private static string DefaultAppearance(PdfDocument document, int pageNumber, Graphics.PdfFont? font)
    {
        if (font == null || font.IsStandard14)
        {
            // /Helv is only added to /DR when a /DA actually names it, so a
            // document whose fields all use an embedded font carries no
            // non-embedded base-14 font dictionary at all.
            EnsureHelvResource(document);
            return "/Helv 10 Tf 0 g";
        }

        var name = RegisterAppearanceFont(document, pageNumber, font);
        return $"/{name} {PdfNumberFormatter.Format(font.Size)} Tf 0 g";
    }

    /// <summary>
    /// Register <paramref name="font"/> in the AcroForm <c>/DR/Font</c> and
    /// return the resource name a <c>/DA</c> should use.
    ///
    /// <para>The font is added to the widget's PAGE first and the resulting
    /// object shared into /DR, so an embedded font program is written to the
    /// file ONCE rather than once per resource dictionary that names it.</para>
    /// </summary>
    private static string RegisterAppearanceFont(PdfDocument document, int pageNumber, Graphics.PdfFont font)
    {
        // A viewer generating the field appearance chooses glyphs from whatever
        // the user types, not from what we drew, so the subset has to cover more
        // than our own content streams asked for.
        font.ReserveGlyphs(DefaultAppearanceGlyphReservation);

        var page = document.GetPage(pageNumber);
        var pageFontName = page.AddFont(font);
        var pageFontObj = LookupPageFontObject(document, page, pageFontName);

        var fonts = EnsureDrFonts(document, EnsureAcroForm(document));

        // Reuse an identical existing /DR entry (repeated fields, same font).
        foreach (var kvp in fonts)
        {
            if (pageFontObj != null && ReferenceEquals(kvp.Value, pageFontObj))
                return kvp.Key.Value;
            if (kvp.Value is PdfReference existingRef && pageFontObj is PdfReference pageRef
                && existingRef.ObjectNumber == pageRef.ObjectNumber
                && existingRef.GenerationNumber == pageRef.GenerationNumber)
            {
                return kvp.Key.Value;
            }
        }

        // Prefer the page's own resource name; uniquify if /DR already uses it
        // for a different font.
        var name = pageFontName;
        int counter = 1;
        while (fonts.ContainsKey(name))
            name = $"{pageFontName}_{counter++}";

        fonts[name] = pageFontObj ?? font.BuildFontDictionary(document);
        return name;
    }

    /// <summary>
    /// The raw (unresolved) value of <paramref name="resourceName"/> in the
    /// page's <c>/Resources/Font</c> — a <see cref="PdfReference"/> for embedded
    /// fonts, so /DR and the page share one object.
    /// </summary>
    private static PdfObject? LookupPageFontObject(PdfDocument document, PdfPage page, string resourceName)
    {
        var resources = page.Resources;
        if (resources == null) return null;
        if (resources.GetOptional("Font") is not { } fontsObj) return null;
        if (document.Resolve(fontsObj) is not PdfDictionary fonts) return null;
        return fonts.TryGetValue(resourceName, out var value) ? value : null;
    }

    /// <summary>
    /// Characters a viewer may need to render into a field appearance from the
    /// <c>/DA</c> font: printable ASCII plus the Latin-1 supplement. A subsetting
    /// font keeps these even though our own writer never encodes them (#1435).
    /// </summary>
    private static readonly string DefaultAppearanceGlyphReservation = BuildGlyphReservation();

    private static string BuildGlyphReservation()
    {
        var sb = new System.Text.StringBuilder(0x7F - 0x20 + 0x100 - 0xA0);
        for (int c = 0x20; c < 0x7F; c++) sb.Append((char)c);
        for (int c = 0xA0; c < 0x100; c++) sb.Append((char)c);
        return sb.ToString();
    }

    /// <summary>Get-or-create the AcroForm <c>/DR/Font</c> dictionary.</summary>
    private static PdfDictionary EnsureDrFonts(PdfDocument document, PdfDictionary acroForm)
    {
        var drObj = acroForm.GetOptional("DR");
        PdfDictionary dr;
        if (drObj != null && document.Resolve(drObj) is PdfDictionary existingDr)
        {
            dr = existingDr;
        }
        else
        {
            dr = new PdfDictionary();
            acroForm["DR"] = dr;
        }

        var fontObj = dr.GetOptional("Font");
        if (fontObj != null && document.Resolve(fontObj) is PdfDictionary existingFonts)
            return existingFonts;

        var fonts = new PdfDictionary();
        dr["Font"] = fonts;
        return fonts;
    }

    /// <summary>
    /// Add the base-14 Helvetica <c>/Helv</c> entry to <c>/DR/Font</c> — only
    /// called when a <c>/DA</c> string names it.
    /// </summary>
    private static void EnsureHelvResource(PdfDocument document)
    {
        var fonts = EnsureDrFonts(document, EnsureAcroForm(document));
        if (fonts.ContainsKey("Helv")) return;

        var helv = new PdfDictionary();
        helv.SetName("Type", "Font");
        helv.SetName("Subtype", "Type1");
        helv.SetName("BaseFont", "Helvetica");
        helv.SetName("Encoding", "WinAnsiEncoding");
        fonts["Helv"] = helv;
    }

    /// <summary>
    /// Get-or-create /Catalog/AcroForm together with its <c>/DR/Font</c>
    /// resource dictionary. The fonts a <c>/DA</c> names are added by
    /// <see cref="DefaultAppearance"/>, not here.
    /// </summary>
    private static PdfDictionary EnsureAcroForm(PdfDocument document)
    {
        var catalog = document.Catalog;
        var existingObj = catalog.GetOptional("AcroForm");
        PdfDictionary acroForm;
        if (existingObj != null && document.Resolve(existingObj) is PdfDictionary existing)
        {
            acroForm = existing;
        }
        else
        {
            acroForm = new PdfDictionary();
            catalog["AcroForm"] = acroForm;
        }

        // /DR (default resources) — the font dictionary exists even when empty;
        // the fonts /DA strings name are registered on demand.
        EnsureDrFonts(document, acroForm);

        return acroForm;
    }

    /// <summary>
    /// Walk the /Pages tree to find the indirect reference whose page index
    /// matches <paramref name="pageNumber"/> (1-based). Returns null if the
    /// pages were created inline rather than as indirect refs (rare).
    /// </summary>
    private static PdfReference? FindPageRef(PdfDocument document, int pageNumber)
    {
        var pagesObj = document.Catalog.GetOptional("Pages");
        if (pagesObj == null) return null;
        if (document.Resolve(pagesObj) is not PdfDictionary pages) return null;

        int target = pageNumber - 1;
        int counter = 0;
        return WalkKids(document, pages, ref counter, target);
    }

    private static PdfReference? WalkKids(
        PdfDocument document, PdfDictionary node, ref int counter, int target)
    {
        var kidsObj = node.GetOptional("Kids");
        if (kidsObj == null || document.Resolve(kidsObj) is not PdfArray kids)
            return null;

        foreach (var kidObj in kids)
        {
            if (document.Resolve(kidObj) is not PdfDictionary kid) continue;
            var type = kid.GetNameOrNull("Type");

            if (type == "Page")
            {
                if (counter == target)
                    return kidObj as PdfReference;
                counter++;
            }
            else if (type == "Pages")
            {
                var hit = WalkKids(document, kid, ref counter, target);
                if (hit != null) return hit;
            }
        }
        return null;
    }

    private static void ValidateName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            throw new ArgumentException("Field name must not be empty.", nameof(fieldName));
        if (fieldName.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
            throw new ArgumentException("Field name contains invalid control characters.", nameof(fieldName));
    }
}
