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
///   • Give the widget its own /AP /N appearance stream and the /F Print
///     flag, so every reader draws and prints the field, including readers
///     that ignore NeedAppearances (#1444). /AcroForm/NeedAppearances is NOT
///     set: PDF/A forbids it, and it asks viewers to discard the appearance.
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
        var appearanceFontInfo = DefaultAppearance(document, pageNumber, appearanceFont);
        widget.SetString("DA", appearanceFontInfo.DefaultAppearanceString);
        WriteTextAppearance(document, widget, rect, defaultValue, appearanceFontInfo, multiline: multiline);

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
        var appearanceFontInfo = DefaultAppearance(document, pageNumber, appearanceFont);
        widget.SetString("DA", appearanceFontInfo.DefaultAppearanceString);
        WriteTextAppearance(document, widget, rect, defaultValue, appearanceFontInfo, multiline: false);

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
        WriteCheckBoxAppearance(document, widget, rect, onState: "Yes");

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
        var appearanceFontInfo = DefaultAppearance(document, pageNumber, appearanceFont);
        widget.SetString("DA", appearanceFontInfo.DefaultAppearanceString);
        WriteTextAppearance(document, widget, rect, defaultValue, appearanceFontInfo, multiline: false);

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
        // An unsigned placeholder still needs an appearance: PDF/A requires one
        // on every widget with a non-empty /Rect (ISO 19005-2 6.3.3).
        WriteEmptyAppearance(document, widget, rect);
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

    // ── appearance streams (#1444) ──────────────────────────────────────────

    /// <summary>A text-like widget's <c>/DA</c> string and the font it names.</summary>
    private sealed record AppearanceFont(string DefaultAppearanceString, string ResourceName, Graphics.PdfFont Font);

    /// <summary>
    /// Write a text or choice widget's <c>/AP</c> — only <c>/N</c>, as PDF/A
    /// requires (ISO 19005-2 6.3.3) — drawing <paramref name="value"/> with the
    /// <c>/DA</c> font, and remember that font so <see cref="PdfField.SetValue"/>
    /// can redraw the appearance in this session.
    /// </summary>
    private static void WriteTextAppearance(
        PdfDocument document, PdfDictionary widget, PdfRectangle rect, string? value, AppearanceFont font, bool multiline)
    {
        var authored = new AuthoredWidgetAppearance(AuthoredWidgetKind.Text, font.ResourceName, font.Font, multiline);
        SetNormalAppearance(widget, BuildTextAppearanceStream(document, rect, value, authored));
        document.RememberAuthoredWidgetAppearance(widget, authored);
    }

    /// <summary>
    /// A checkbox's <c>/AP /N</c> is a state dictionary (ISO 19005-2 6.3.3 for
    /// Btn widgets): the on state draws a vector tick, <c>/Off</c> draws nothing.
    /// A vector tick rather than a ZapfDingbats glyph, because base-14 fonts are
    /// not embedded and PDF/A requires every font an appearance uses to be.
    /// </summary>
    private static void WriteCheckBoxAppearance(PdfDocument document, PdfDictionary widget, PdfRectangle rect, string onState)
    {
        var width = Math.Abs(rect.Width);
        var height = Math.Abs(rect.Height);
        var lineWidth = Math.Max(0.5, Math.Min(width, height) * 0.1);
        var tick = new System.Text.StringBuilder()
            .Append("q 0 G ").Append(Fmt(lineWidth)).Append(" w 1 J 1 j\n")
            .Append(Fmt(width * 0.2)).Append(' ').Append(Fmt(height * 0.52)).Append(" m\n")
            .Append(Fmt(width * 0.42)).Append(' ').Append(Fmt(height * 0.26)).Append(" l\n")
            .Append(Fmt(width * 0.8)).Append(' ').Append(Fmt(height * 0.76)).Append(" l\nS Q\n")
            .ToString();

        var states = new PdfDictionary();
        states[onState] = AddFormXObject(document, width, height, tick, resources: null);
        states["Off"] = AddFormXObject(document, width, height, string.Empty, resources: null);
        var ap = new PdfDictionary();
        ap["N"] = states;
        widget["AP"] = ap;
        document.RememberAuthoredWidgetAppearance(
            widget, new AuthoredWidgetAppearance(AuthoredWidgetKind.CheckBox, ResourceName: null, Font: null, Multiline: false));
    }

    private static void WriteEmptyAppearance(PdfDocument document, PdfDictionary widget, PdfRectangle rect) =>
        SetNormalAppearance(widget, AddFormXObject(document, Math.Abs(rect.Width), Math.Abs(rect.Height), string.Empty, resources: null));

    /// <summary>
    /// Redraw the appearance of a widget authored by this class in this session
    /// for a new <paramref name="value"/>. Returns false — the caller then falls
    /// back to NeedAppearances — for a widget excise did not author here (no font
    /// to encode the value with; this includes an authored document reopened from
    /// bytes) or a checkbox value that names no appearance state.
    /// </summary>
    internal static bool TryRegenerateAppearance(PdfDocument document, PdfDictionary widget, string? value)
    {
        if (!document.TryGetAuthoredWidgetAppearance(widget, out var authored))
            return false;

        if (authored.Kind == AuthoredWidgetKind.CheckBox)
        {
            if (value == null || value == "Off")
                return true;
            return widget.GetOptional("AP") is { } apObj
                && document.Resolve(apObj) is PdfDictionary ap
                && ap.GetOptional("N") is { } normalObj
                && document.Resolve(normalObj) is PdfDictionary states
                && states.ContainsKey(value);
        }

        if (!TryReadRect(document, widget, out var rect))
            return false;

        SetNormalAppearance(widget, BuildTextAppearanceStream(document, rect, value, authored));
        return true;
    }

    private static PdfReference BuildTextAppearanceStream(
        PdfDocument document, PdfRectangle rect, string? value, AuthoredWidgetAppearance authored)
    {
        var width = Math.Abs(rect.Width);
        var height = Math.Abs(rect.Height);
        var font = authored.Font!;
        var resourceName = authored.ResourceName!;

        var content = new System.Text.StringBuilder("/Tx BMC\n");
        if (!string.IsNullOrEmpty(value))
        {
            const double padding = 2.0;
            var ascent = Math.Abs(font.Ascender);
            var descent = Math.Abs(font.Descender);
            var lines = authored.Multiline
                ? value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                : new[] { value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ') };
            var baseline = authored.Multiline
                ? height - padding - ascent
                : (height - (ascent + descent)) / 2 + descent;

            content.Append("q\n")
                .Append(Fmt(padding / 2)).Append(' ').Append(Fmt(padding / 2)).Append(' ')
                .Append(Fmt(Math.Max(0, width - padding))).Append(' ').Append(Fmt(Math.Max(0, height - padding)))
                .Append(" re W n\nBT\n/").Append(resourceName).Append(' ').Append(Fmt(font.Size)).Append(" Tf\n0 g\n")
                .Append(Fmt(padding)).Append(' ').Append(Fmt(baseline)).Append(" Td\n");
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                    content.Append("0 ").Append(Fmt(-font.LineHeight)).Append(" Td\n");
                // EncodeString, not a Latin-1 literal: an embedded /DA font is
                // Identity-H, and encoding through it also records the glyphs
                // for the subset built at save time.
                content.Append(font.EncodeString(lines[i])).Append(" Tj\n");
            }
            content.Append("ET\nQ\n");
        }
        content.Append("EMC\n");

        // The appearance names the same font object as /DR, so an embedded font
        // program is still written once.
        var fonts = new PdfDictionary();
        if (EnsureDrFonts(document, EnsureAcroForm(document)).TryGetValue(resourceName, out var fontObject))
            fonts[resourceName] = fontObject;
        var resources = new PdfDictionary();
        resources["Font"] = fonts;
        return AddFormXObject(document, width, height, content.ToString(), resources);
    }

    private static void SetNormalAppearance(PdfDictionary widget, PdfReference normal)
    {
        var ap = new PdfDictionary();
        ap["N"] = normal;
        widget["AP"] = ap;
    }

    private static PdfReference AddFormXObject(
        PdfDocument document, double width, double height, string content, PdfDictionary? resources)
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes(content);
        var dict = new PdfDictionary();
        dict.SetName("Type", "XObject");
        dict.SetName("Subtype", "Form");
        var bbox = new PdfArray();
        bbox.Add((PdfObject)new PdfReal(0));
        bbox.Add((PdfObject)new PdfReal(0));
        bbox.Add((PdfObject)new PdfReal(width));
        bbox.Add((PdfObject)new PdfReal(height));
        dict["BBox"] = bbox;
        dict["Resources"] = resources ?? new PdfDictionary();
        dict.SetInt("Length", bytes.Length);
        return document.AddIndirectObject(new PdfStream(dict, bytes));
    }

    private static bool TryReadRect(PdfDocument document, PdfDictionary widget, out PdfRectangle rect)
    {
        rect = default!;
        if (widget.GetOptional("Rect") is not { } rectObj || document.Resolve(rectObj) is not PdfArray array || array.Count < 4)
            return false;

        var values = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (document.Resolve(array[i]) is not PdfObject item || !item.TryGetNumber(out var number))
                return false;
            values[i] = number;
        }

        rect = new PdfRectangle(
            Math.Min(values[0], values[2]), Math.Min(values[1], values[3]),
            Math.Max(values[0], values[2]), Math.Max(values[1], values[3]));
        return true;
    }

    private static string Fmt(double value) => PdfNumberFormatter.Format(value);

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
        // /F 4 = Print (§12.5.3). Without it conforming readers do not print the
        // field, and PDF/A rejects the widget (ISO 19005-2 6.3.2) (#1444).
        widget.SetInt("F", 4);

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

        // 4. No /NeedAppearances: the Add* method already wrote this widget's
        // /AP (#1444). The flag is forbidden by PDF/A (ISO 19005-2 6.4.1) and
        // asks viewers to throw the typeset appearance away.

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
    private static AppearanceFont DefaultAppearance(PdfDocument document, int pageNumber, Graphics.PdfFont? font)
    {
        if (font == null || font.IsStandard14)
        {
            // /Helv is only added to /DR when a /DA actually names it, so a
            // document whose fields all use an embedded font carries no
            // non-embedded base-14 font dictionary at all.
            EnsureHelvResource(document);
            return new AppearanceFont("/Helv 10 Tf 0 g", "Helv", Graphics.PdfFont.Helvetica(10));
        }

        var name = RegisterAppearanceFont(document, pageNumber, font);
        return new AppearanceFont($"/{name} {PdfNumberFormatter.Format(font.Size)} Tf 0 g", name, font);
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

/// <summary>Which appearance an authored widget carries (#1444).</summary>
internal enum AuthoredWidgetKind
{
    /// <summary>A text or choice widget: one /N stream drawing the value.</summary>
    Text,

    /// <summary>A checkbox: an /N state dictionary with an on state and /Off.</summary>
    CheckBox,
}

/// <summary>
/// What <see cref="AcroFormAuthoring"/> needs to redraw a widget it authored:
/// the /DR font resource name and the font that encodes the value (#1444).
/// </summary>
internal sealed record AuthoredWidgetAppearance(
    AuthoredWidgetKind Kind,
    string? ResourceName,
    Graphics.PdfFont? Font,
    bool Multiline);
