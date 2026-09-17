namespace Excise.Core.Document;

/// <summary>
/// What a redaction did with one embedded file (#1572). Since the 2026-09-17
/// product decision, redacted output carries no attachments unless the caller
/// explicitly keeps them.
/// </summary>
public enum AttachmentDisposition
{
    /// <summary>The file was removed from the document.</summary>
    Removed,

    /// <summary>
    /// Kept (the caller opted out of removal), inspected, and the term was not
    /// found in it.
    /// </summary>
    KeptTermNotFound,

    /// <summary>
    /// Kept, inspected, and the term was removed from it — cut out of a text
    /// file, or a nested PDF redacted with the same options.
    /// </summary>
    KeptTermRemoved,

    /// <summary>
    /// Kept and inspected, but the result is NOT clean: the term is still in
    /// the file, or a nested PDF's own redaction reported a gap.
    /// </summary>
    KeptNotClean,

    /// <summary>
    /// Kept and NOT inspected — excise cannot read this kind of file, or there
    /// was no term to look for. It may contain the redacted text.
    /// </summary>
    KeptNotChecked,
}

/// <summary>
/// One embedded file a redaction removed or kept, and why (#1572).
/// </summary>
/// <param name="Name">The file name (<c>/UF</c>, <c>/F</c>, or the name-tree
/// key), or a placeholder when the file specification names none.</param>
/// <param name="SizeBytes">The payload size in bytes, when known.</param>
/// <param name="Location">Where the file was attached, e.g.
/// <c>document</c> or <c>page 2 file attachment annotation</c>.</param>
/// <param name="Disposition">What happened to it.</param>
/// <param name="Detail">Why, for anything other than a plain removal.</param>
public sealed record AttachmentRedactionResult(
    string Name,
    long? SizeBytes,
    string Location,
    AttachmentDisposition Disposition,
    string? Detail = null)
{
    /// <summary>
    /// False when the file is still in the output and may hold the term. A
    /// redaction with any such file is not a clean success.
    /// </summary>
    public bool IsClean =>
        Disposition is not (AttachmentDisposition.KeptNotClean or AttachmentDisposition.KeptNotChecked);

    /// <summary>A one-line description safe to print.</summary>
    public override string ToString()
    {
        var size = SizeBytes is { } bytes ? $", {bytes:N0} bytes" : "";
        var what = Disposition switch
        {
            AttachmentDisposition.Removed => "removed",
            AttachmentDisposition.KeptTermNotFound => "kept; term not found",
            AttachmentDisposition.KeptTermRemoved => "kept; term removed",
            AttachmentDisposition.KeptNotClean => "kept; NOT clean",
            _ => "kept; NOT checked — may contain the term",
        };
        return Detail == null
            ? $"{Name} ({Location}{size}): {what}"
            : $"{Name} ({Location}{size}): {what} — {Detail}";
    }
}

/// <summary>
/// Thrown instead of redacting a PDF portfolio (a catalog with
/// <c>/Collection</c>) when its attachments would be removed.
/// </summary>
/// <remarks>
/// A portfolio's pages are only a cover sheet; the documents the reader
/// actually opens are its embedded files. Stripping them to redact the cover
/// sheet would delete the content the user meant to redact, so excise refuses
/// before changing anything. Keeping attachments
/// (<c>RedactionOptions.KeepAttachments</c>, <c>--keep-attachments</c>)
/// redacts the member PDFs one by one instead.
/// </remarks>
public sealed class PdfPortfolioRedactionException : System.InvalidOperationException
{
    /// <summary>Create the refusal with its standard explanation.</summary>
    public PdfPortfolioRedactionException()
        : base("This PDF is a portfolio: its visible pages are a cover sheet and the documents in it " +
               "are embedded files. Redaction removes attachments by default, which would delete " +
               "those documents, so excise refused and changed nothing. Keep attachments " +
               "(--keep-attachments, or Preferences > Redaction) to redact each member PDF instead, " +
               "or extract the member documents and redact them separately.")
    {
    }
}

/// <summary>
/// Thrown when a redaction that keeps attachments cannot redact one of them
/// and will not guess (#1572): an encrypted or unreadable nested PDF, or
/// nesting deeper than excise follows.
/// </summary>
public sealed class AttachmentRedactionRefusedException : System.InvalidOperationException
{
    /// <summary>Create the refusal for <paramref name="attachmentName"/>.</summary>
    public AttachmentRedactionRefusedException(string attachmentName, string reason)
        : base($"Attachment '{attachmentName}' could not be redacted: {reason}. excise refused and " +
               "saved nothing. Remove attachments (the default) or remove this file first.")
    {
        AttachmentName = attachmentName;
    }

    /// <summary>The attachment that could not be redacted.</summary>
    public string AttachmentName { get; }
}
