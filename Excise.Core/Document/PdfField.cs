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
    /// For text fields, whether the field allows multiple lines of text
    /// (flag Ff bit 12 set). Only applies to /FT /Tx fields.
    /// </summary>
    public bool IsMultiline { get; }

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

    public PdfField(
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
        IReadOnlyList<PdfDictionary> widgetDictionaries)
        : this(
            document,
            fullName,
            partialName,
            fieldType,
            options,
            rect,
            pageNumber,
            isReadOnly,
            isRequired,
            isMultiline,
            rawDictionary,
            widgetDictionaries,
            flags: 0,
            widgets: Array.Empty<PdfFieldWidget>())
    {
    }

    public PdfField(
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
    /// Sets /NeedAppearances=true on the AcroForm dictionary so PDF readers
    /// regenerate the visual appearance from the new value. Callers who want
    /// to bake the value into static page content should call
    /// <see cref="PdfDocument.FlattenAcroForm"/> after setting all values.
    ///
    /// Pass null to clear the value (removes /V).
    ///
    /// Throws InvalidOperationException if the field is read-only or is a
    /// Signature field. Throws ArgumentException if the value isn't valid
    /// for the field's type (e.g. a Choice field's options).
    /// </summary>
    public void SetValue(string? value)
    {
        if (IsReadOnly)
            throw new InvalidOperationException(
                $"Field '{FullName}' is read-only (Ff bit 0 set) and cannot be modified.");

        if (FieldType == PdfFieldType.Signature)
            throw new InvalidOperationException(
                $"Field '{FullName}' is a Signature field. Use the signing API to populate signatures.");

        if (value == null)
        {
            RawDictionary.Remove("V");
            // For buttons, also reset /AS on widgets to /Off.
            if (FieldType == PdfFieldType.Button)
                SetButtonAppearanceState("Off");
            _document.SetAcroFormNeedAppearances();
            return;
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

        _document.SetAcroFormNeedAppearances();
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
