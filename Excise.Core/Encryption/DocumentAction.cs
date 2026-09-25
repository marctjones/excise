namespace Excise.Core.Security;

/// <summary>
/// What a caller is about to do to a document, mapped to the /P bits that govern it
/// (ISO 32000-2 Table 22). <see cref="PdfPermissions.Allows"/> is the one place the mapping lives.
/// </summary>
public enum DocumentAction
{
    /// <summary>Copy or extract text and graphics: bit 5.</summary>
    Extract,

    /// <summary>Extract in support of accessibility: bit 10 (deprecated in PDF 2.0, where readers ignore it).</summary>
    ExtractForAccessibility,

    /// <summary>Modify contents other than what bits 6, 9 and 11 govern: bit 4.</summary>
    ModifyContents,

    /// <summary>Add or modify annotations: bit 6.</summary>
    Annotate,

    /// <summary>Fill in existing interactive form fields: bit 6 or bit 9.</summary>
    FillForms,

    /// <summary>Create or modify interactive form fields, including signature fields: bit 6 and bit 4.</summary>
    CreateFormField,

    /// <summary>Insert, rotate or delete pages, split, merge: bit 11 alone.</summary>
    AssembleDocument,

    /// <summary>Print, possibly degraded: bit 3.</summary>
    Print,

    /// <summary>Print to a faithful digital representation: bit 3 and bit 12.</summary>
    PrintHighQuality,
}

internal static class DocumentActionExtensions
{
    /// <summary>The permission a refusal names, e.g. "copy/extract permission (/P bit 5)".</summary>
    internal static string Requirement(this DocumentAction action) => action switch
    {
        DocumentAction.Extract => "copy/extract permission (/P bit 5)",
        DocumentAction.ExtractForAccessibility => "extract-for-accessibility permission (/P bit 10)",
        DocumentAction.ModifyContents => "modify permission (/P bit 4)",
        DocumentAction.Annotate => "annotation permission (/P bit 6)",
        DocumentAction.FillForms => "form fill-in permission (/P bit 6 or 9)",
        DocumentAction.CreateFormField => "form-field creation permission (/P bits 4 and 6)",
        DocumentAction.AssembleDocument => "page-assembly permission (/P bit 11)",
        DocumentAction.Print => "printing permission (/P bit 3)",
        DocumentAction.PrintHighQuality => "full-quality printing permission (/P bit 12)",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}
