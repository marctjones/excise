namespace Excise.Core.Document;

/// <summary>
/// Represents the interactive form (AcroForm) of a PDF document.
/// An AcroForm is a collection of form fields that allow users to fill in information.
/// PDF spec §12.7.
/// </summary>
public sealed class PdfAcroForm
{
    /// <summary>
    /// All form fields in the document, flattened into a single list.
    /// This includes both top-level fields and descendant fields from the /Fields array.
    /// </summary>
    public IReadOnlyList<PdfField> Fields { get; }

    /// <summary>
    /// Whether the PDF reader should automatically generate field appearances
    /// (/NeedsAppearances flag in the AcroForm dictionary, default false).
    /// When true, the reader will generate appearance streams rather than using
    /// pre-rendered /AP entries.
    /// </summary>
    public bool NeedsAppearances { get; }

    public PdfAcroForm(IReadOnlyList<PdfField> fields, bool needsAppearances)
    {
        Fields = fields;
        NeedsAppearances = needsAppearances;
    }

    /// <summary>
    /// Returns all fields of a specific type.
    /// </summary>
    public IEnumerable<PdfField> GetFields(PdfFieldType type) =>
        Fields.Where(f => f.FieldType == type);

    /// <summary>
    /// Returns all text fields (/FT /Tx).
    /// </summary>
    public IEnumerable<PdfField> GetTextFields() =>
        GetFields(PdfFieldType.Text);

    /// <summary>
    /// Returns all button fields (/FT /Btn).
    /// </summary>
    public IEnumerable<PdfField> GetButtonFields() =>
        GetFields(PdfFieldType.Button);

    /// <summary>
    /// Returns all choice fields (/FT /Ch).
    /// </summary>
    public IEnumerable<PdfField> GetChoiceFields() =>
        GetFields(PdfFieldType.Choice);

    /// <summary>
    /// Returns all signature fields (/FT /Sig).
    /// </summary>
    public IEnumerable<PdfField> GetSignatureFields() =>
        GetFields(PdfFieldType.Signature);

    /// <summary>
    /// Finds a field by its full name.
    /// </summary>
    public PdfField? FindField(string fullName) =>
        Fields.FirstOrDefault(f => f.FullName == fullName);
}
