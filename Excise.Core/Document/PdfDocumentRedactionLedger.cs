namespace Excise.Core.Document;

/// <summary>
/// What redaction removed WHOLE from this document instance, for the callers
/// that report it after the fact (#1572, #1574).
/// </summary>
/// <remarks>
/// <c>page.RedactArea</c> returns nothing, and its wholesale removals (every
/// attachment, the XFA form) happen before the caller's safety report runs.
/// Without this record that report could only count what was left, and it
/// used to print "none found" for attachments the area pass had just
/// deleted. The ledger lives as long as the in-memory document; it is never
/// saved.
/// </remarks>
internal sealed class PdfDocumentRedactionLedger
{
    private readonly List<AttachmentRedactionResult> _removedAttachments = new();
    private readonly List<string> _xfaRemovals = new();

    internal IReadOnlyList<AttachmentRedactionResult> RemovedAttachments => _removedAttachments;

    internal IReadOnlyList<string> XfaRemovals => _xfaRemovals;

    internal void RecordRemovedAttachments(IEnumerable<AttachmentRedactionResult> removed)
        => _removedAttachments.AddRange(removed);

    internal void RecordXfaRemoval(string description) => _xfaRemovals.Add(description);
}

public partial class PdfDocument
{
    internal PdfDocumentRedactionLedger RedactionLedger { get; } = new();
}
