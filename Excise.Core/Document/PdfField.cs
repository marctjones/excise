using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>
/// Represents a single PDF form field from an interactive form (AcroForm).
/// A field may have a text value (/V), a default value (/DV), flags (/Ff),
/// and widget annotations that define its visual representation on pages.
/// PDF spec §12.7.
/// </summary>
public sealed class PdfField
{
    private readonly PdfDocument _document;

    /// <summary>
    /// The fully-qualified field name, formed by concatenating the /T entries
    /// of the field and its ancestors with dots. For example, "Address.City".
    /// </summary>
    public string FullName { get; }

    /// <summary>
    /// The partial field name (/T entry) for this specific field, not including parent names.
    /// </summary>
    public string PartialName { get; }

    /// <summary>
    /// The field type (Button, Text, Choice, Signature, or Unknown).
    /// </summary>
    public PdfFieldType FieldType { get; }

    /// <summary>
    /// The field's current value (/V entry), or null if not set.
    /// For text fields, this is a string. For choice fields, this is
    /// typically one of the option values. For buttons, this is usually
    /// a name like /Yes or /Off.
    ///
    /// This property re-reads the underlying dictionary, so it reflects
    /// any mutation made via <see cref="SetValue(string?)"/>.
    /// </summary>
    public string? Value => ResolveString(RawDictionary.GetOptional("V"));

    /// <summary>
    /// The field's default value (/DV entry), or null if not set.
    /// Used when the field has no explicit value.
    /// </summary>
    public string? DefaultValue => ResolveString(RawDictionary.GetOptional("DV"));

    /// <summary>
    /// The field's alternate field name (/TU, §12.7.3.3) -- its accessible
    /// name in every reader that exposes one, and the tooltip authoring
    /// already writes via <c>AcroFormAuthoring.SetTooltip</c> / the
    /// <c>tooltip:</c> parameters on <c>AddTextField</c> etc. (#1443).
    /// Read-only: reading was the gap, the authoring path already covers
    /// writing.
    /// </summary>
    public string? Tooltip => ResolveString(RawDictionary.GetOptional("TU"));

    /// <summary>
    /// For choice fields, the list of available options (/Opt array).
    /// Each element is a string (the display/export value) or a 2-element
    /// array [exportValue, displayValue]. This property contains the
    /// display values. Null for non-choice fields.
    /// </summary>
    public IReadOnlyList<string>? Options { get; }

    /// <summary>
    /// For a <see cref="PdfFieldType.Button"/> field, the selectable "on" export
    /// values — the appearance-state names from each widget's <c>/AP /N</c>
    /// dictionary other than <c>Off</c>, in widget order, de-duplicated. A radio
    /// group exposes one value per option (its widgets); a single checkbox
    /// typically exposes one. Empty for non-Button fields (and for buttons with
    /// no on-state appearances). Lets consumers map a radio group to a
    /// choice/dropdown rather than a generic boolean (#424).
    /// </summary>
    public IReadOnlyList<string> ButtonExportValues
    {
        get
        {
            if (FieldType != PdfFieldType.Button)
                return Array.Empty<string>();

            var values = new List<string>();
            foreach (var widget in WidgetDictionaries)
                foreach (var state in GetWidgetOnStates(widget))
                    if (!values.Contains(state))
                        values.Add(state);
            return values;
        }
    }

    /// <summary>
    /// The effective field flags (<c>/Ff</c>) after inheriting from ancestor
    /// fields. See PDF §12.7.4.2.
    /// </summary>
    public int Flags { get; }

    /// <summary>
    /// True when this button field represents a radio group rather than a
    /// single checkbox.
    /// </summary>
    public bool IsRadioButton => FieldType == PdfFieldType.Button && (Flags & 0x8000) != 0;

    /// <summary>True when this button field is a push button.</summary>
    public bool IsPushButton => FieldType == PdfFieldType.Button && (Flags & 0x10000) != 0;

    /// <summary>True when this choice field is a combo box.</summary>
    public bool IsComboBox => FieldType == PdfFieldType.Choice && (Flags & 0x20000) != 0;

    /// <summary>The non-<c>Off</c> appearance-state names under a widget's <c>/AP /N</c>.</summary>
    private IEnumerable<string> GetWidgetOnStates(PdfDictionary widget)
    {
        var apObj = widget.GetOptional("AP");
        if (apObj == null || _document.Resolve(apObj) is not PdfDictionary ap)
            yield break;
        var nObj = ap.GetOptional("N");
        if (nObj == null || _document.Resolve(nObj) is not PdfDictionary normal)
            yield break;
        foreach (var key in normal.Keys)
            if (key.Value != "Off")
                yield return key.Value;
    }

    /// <summary>
    /// The field's bounding rectangle on the page (from /Rect in the
    /// associated Widget annotation), or null if the field has no visual
    /// representation or the rectangle could not be parsed.
    /// </summary>
    public PdfRectangle? Rect { get; }

    /// <summary>
    /// The 1-based page number where the field's Widget annotation is located,
    /// or null if the field is not associated with any page or the page
    /// number could not be determined.
    /// </summary>
    public int? PageNumber { get; }

    /// <summary>
    /// Whether the field is read-only (flag Ff bit 0 set).
    /// Read-only fields cannot be modified by the user.
    /// </summary>
    public bool IsReadOnly { get; }

    /// <summary>
    /// Whether the field is required (flag Ff bit 1 set).
    /// Required fields must have a value when the form is submitted.
    /// </summary>
    public bool IsRequired { get; }

    /// <summary>
    /// For text fields, whether the field allows multiple lines of text:
    /// flag Ff bit 12, OR'd with the static-XFA template's own multi-line
    /// signal when the document carries one (#1898). A static-XFA form's
    /// AcroForm shadow fields are generated by whatever authored the
    /// template, and that bit is not guaranteed to agree with the template
    /// it came from. Only applies to /FT /Tx fields.
    /// </summary>
    public bool IsMultiline { get; }

    /// <summary>
    /// The font size in the nearest default appearance string (<c>/DA</c>),
    /// inherited through ancestor fields and then the AcroForm dictionary
    /// (ISO 32000-2 §12.7.4.3). Null when that appearance requests automatic
    /// sizing, is absent, or cannot be parsed; callers retain their own fallback.
    /// </summary>
    public double? DefaultAppearanceFontSize
    {
        get
        {
            // See #1922. Re-read mutable dictionaries and bound malformed parent cycles.
            var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            PdfDictionary? current = RawDictionary;
            while (current != null && visited.Add(current))
            {
                if (current.GetOptional("DA") is { } appearance)
                {
                    var resolved = _document.Resolve(appearance);
                    if (resolved is not PdfNull)
                        return ParseDefaultAppearanceFontSize((resolved as PdfString)?.Value);
                }
                current = current.GetOptional("Parent") is { } parent
                    ? _document.Resolve(parent) as PdfDictionary : null;
            }
            var acroForm = _document.Catalog.GetOptional("AcroForm");
            var root = acroForm == null ? null : _document.Resolve(acroForm) as PdfDictionary;
            return ParseDefaultAppearanceFontSize(root?.GetOptional("DA") is { } rootAppearance
                ? (_document.Resolve(rootAppearance) as PdfString)?.Value : null);
        }
    }

    /// <summary>
    /// Parse "(/Helv 10 Tf 0 g)" for the "&lt;n&gt; Tf" font-size token. Null
    /// when <paramref name="da"/> is null or carries no usable token.
    /// </summary>
    internal static double? ParseDefaultAppearanceFontSize(string? da)
    {
        if (da == null) return null;

        var tokens = da.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < tokens.Length; i++)
        {
            if (tokens[i] == "Tf" &&
                double.TryParse(tokens[i - 1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var size) &&
                size > 0)
            {
                return size;
            }
        }
        return null;
    }

    /// <summary>
    /// The raw field dictionary (/FT, /T, /V, /DV, /Ff, etc.).
    /// Allows access to additional entries not exposed by properties.
    /// </summary>
    public PdfDictionary RawDictionary { get; }

    /// <summary>
    /// The Widget annotation dictionaries associated with this field. May be empty.
    /// Includes the field's own dictionary if it is itself a /Subtype /Widget,
    /// otherwise the dictionaries from its /Kids that are /Subtype /Widget.
    /// </summary>
    internal IReadOnlyList<PdfDictionary> WidgetDictionaries { get; }

    /// <summary>
    /// Widget annotations associated with this field, including per-widget
    /// rectangles and export values when available.
    /// </summary>
    public IReadOnlyList<PdfFieldWidget> Widgets { get; }

    internal PdfField(
        PdfDocument document,
        string fullName,
        string partialName,
        PdfFieldType fieldType,
        IReadOnlyList<string>? options,
        PdfRectangle? rect,
        int? pageNumber,
        bool isReadOnly,
        bool isRequired,
        bool isMultiline,
        PdfDictionary rawDictionary,
        IReadOnlyList<PdfDictionary> widgetDictionaries,
        int flags,
        IReadOnlyList<PdfFieldWidget> widgets)
    {
        _document = document;
        FullName = fullName;
        PartialName = partialName;
        FieldType = fieldType;
        Flags = flags;
        Options = options;
        Rect = rect;
        PageNumber = pageNumber;
        IsReadOnly = isReadOnly;
        IsRequired = isRequired;
        IsMultiline = isMultiline;
        RawDictionary = rawDictionary;
        WidgetDictionaries = widgetDictionaries;
        Widgets = widgets;
    }

    /// <summary>
    /// Set the field's value (/V entry). For Text and Choice fields, the value
    /// is stored as a PDF string. For Button (checkbox/radio) fields, the value
    /// is stored as a PDF name (e.g. "Yes" / "Off") and each widget annotation's
    /// /AS appearance state is updated to match.
    ///
    /// Redraws each text or choice widget's appearance for the new value, or,
    /// where it cannot, removes the stale appearance and sets
    /// /NeedAppearances=true so PDF readers regenerate it (#2017: the old
    /// appearance never survives a change). Callers who want
    /// to bake the value into static page content should call
    /// <see cref="PdfDocument.FlattenAcroForm"/> after setting all values.
    ///
    /// Pass null to clear the value (removes /V).
    ///
    /// Throws InvalidOperationException if the field is read-only or is a
    /// Signature field. Throws ArgumentException if the value isn't valid
    /// for the field's type (e.g. a Choice field's options), or — for a widget
    /// authored in this session with a non-embedded font — contains a character
    /// that font cannot represent (#1671; the field is left unchanged).
    /// </summary>
    public void SetValue(string? value)
    {
        if (IsReadOnly)
            throw new InvalidOperationException(
                $"Field '{FullName}' is read-only (Ff bit 0 set) and cannot be modified.");

        if (FieldType == PdfFieldType.Signature)
            throw new InvalidOperationException(
                $"Field '{FullName}' is a Signature field. Use the signing API to populate signatures.");

        // #2013: a static XFA form keeps a second copy of each value in its
        // datasets packet. Refuse a value XML cannot carry before /V changes.
        Excise.Core.Xfa.XfaStaticDataSync.EnsureValueWritable(_document, value, FullName);

        if (value == null)
        {
            RawDictionary.Remove("V");
            // For buttons, also reset /AS on widgets to /Off.
            if (FieldType == PdfFieldType.Button)
                SetButtonAppearanceState("Off");
            RefreshAppearances(null);
            Excise.Core.Xfa.XfaStaticDataSync.Apply(_document, this, null);
            return;
        }

        // #1671: an authored text widget redraws its appearance through its font,
        // and a character that font cannot represent would be written as '?'.
        // Refuse here, before /V changes, so a refusal leaves the field as it was.
        if (FieldType != PdfFieldType.Button)
        {
            IReadOnlyList<PdfDictionary> targets = WidgetDictionaries.Count > 0
                ? WidgetDictionaries
                : new[] { RawDictionary };
            foreach (var widget in targets)
                AcroFormAuthoring.EnsureValueDrawable(_document, widget, value, FullName);
        }

        switch (FieldType)
        {
            case PdfFieldType.Button:
                // Checkbox / radio: /V is a name, and each widget's /AS reflects state.
                RawDictionary.Set("V", new PdfName(value));
                SetButtonAppearanceState(value);
                break;

            case PdfFieldType.Choice:
                if (Options != null && Options.Count > 0 && !Options.Contains(value))
                    throw new ArgumentException(
                        $"Value '{value}' is not one of the choice field options for '{FullName}'. " +
                        $"Allowed: {string.Join(", ", Options)}",
                        nameof(value));
                RawDictionary.Set("V", new PdfString(value));
                break;

            case PdfFieldType.Text:
            default:
                RawDictionary.Set("V", new PdfString(value));
                break;
        }

        RefreshAppearances(value);

        // ISO 32000-2 Annex K.2: the XFA field values shall be consistent with
        // the AcroForm /V. Writes the same value into the datasets of a static
        // XFA form; a no-op for any other document (#2013).
        Excise.Core.Xfa.XfaStaticDataSync.Apply(_document, this, value);
    }

    /// <summary>
    /// Bring every widget's appearance in line with the new value.
    /// <list type="bullet">
    /// <item>A widget excise authored in this session is redrawn with its
    /// authored font (#1444).</item>
    /// <item>Any other text or choice widget (every document opened from a file,
    /// including one Acrobat filled) has its old <c>/AP</c> detached, because it
    /// draws the OLD value (#2017), and is redrawn from its <c>/DA</c> and the
    /// AcroForm <c>/DR</c> font by <see cref="FieldAppearanceRegenerator"/>.
    /// When that cannot be done faithfully the widget keeps no <c>/AP</c> and
    /// NeedAppearances is set, so the reader regenerates it from <c>/V</c>.</item>
    /// <item>A button keeps its <c>/AP</c>; <c>/AS</c> selects the state. A
    /// button value naming no authored state sets NeedAppearances.</item>
    /// </list>
    /// <para>⚠️ NeedAppearances costs PDF/A conformance (ISO 19005-2 6.4.1#3,
    /// ISO 19005-1 6.9#1, #1508); it is now set only for the widgets the
    /// regenerator refuses.</para>
    /// </summary>
    private void RefreshAppearances(string? value)
    {
        // #2017: a rich-text value (/RV, §12.7.4.3) is the OLD value in markup;
        // a plain /V replaces it, so it must not survive the change.
        if (FieldType == PdfFieldType.Text && RawDictionary.ContainsKey("RV"))
            RawDictionary.Remove("RV");

        IReadOnlyList<PdfDictionary> widgets = WidgetDictionaries.Count > 0
            ? WidgetDictionaries
            : new[] { RawDictionary };

        var allRedrawn = true;
        foreach (var widget in widgets)
        {
            if (AcroFormAuthoring.TryRegenerateAppearance(_document, widget, value))
                continue;

            if (FieldType != PdfFieldType.Button)
            {
                // #2017: the existing appearance draws the OLD value. Detach it
                // first, unconditionally, so it is neither drawn by a reader that
                // ignores NeedAppearances nor saved: replacing the widget's own
                // /AP entry never edits an /AP or stream another widget shares,
                // and the writer saves only what is still reachable. Then redraw
                // from /DA and /DR where that can be done faithfully; otherwise
                // the widget is left with no /AP and the reader regenerates it.
                var previousResources = PreviousAppearanceResources(widget);
                widget.Remove("AP");
                if (FieldAppearanceRegenerator.TryRegenerate(_document, this, widget, value, previousResources))
                    continue;
            }

            // Buttons keep their /AP: /AS selects among fixed on/off states that
            // carry no value text.
            allRedrawn = false;
        }

        if (!allRedrawn)
            _document.SetAcroFormNeedAppearances();
    }

    /// <summary>The <c>/Resources</c> of the widget's current <c>/AP /N</c> stream, read before it is detached.</summary>
    private PdfDictionary? PreviousAppearanceResources(PdfDictionary widget)
    {
        if (widget.GetOptional("AP") is not { } apObj || _document.Resolve(apObj) is not PdfDictionary ap) return null;
        if (ap.GetOptional("N") is not { } normalObj || _document.Resolve(normalObj) is not PdfStream normal) return null;
        return normal.GetOptional("Resources") is { } res ? _document.Resolve(res) as PdfDictionary : null;
    }

    /// <summary>
    /// Update the /AS (appearance state) entry on each widget dictionary so
    /// readers without /NeedAppearances support still display the correct
    /// state.
    /// </summary>
    private void SetButtonAppearanceState(string state)
    {
        foreach (var widget in WidgetDictionaries)
            widget.Set("AS", new PdfName(state));
    }

    private string? ResolveString(PdfObject? obj)
    {
        if (obj == null) return null;
        obj = _document.Resolve(obj);
        if (obj is PdfString s) return s.Value;
        if (obj is PdfName n) return n.Value;
        return null;
    }

    public override string ToString()
    {
        var parts = new List<string> { $"{FieldType} '{FullName}'" };

        if (Value != null)
            parts.Add($"= \"{Value}\"");

        if (PageNumber.HasValue)
            parts.Add($"on page {PageNumber}");

        if (IsReadOnly)
            parts.Add("(read-only)");

        return string.Join(" ", parts);
    }
}
