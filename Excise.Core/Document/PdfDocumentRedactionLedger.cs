using Excise.Core.Primitives;

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

    /// <summary>
    /// Structure elements carrying <c>/Alt</c> or <c>/ActualText</c> that an
    /// AREA redaction could NOT check against the image(s) it blacked out
    /// (#1586) — no <c>/MCID</c> link, and no removed text to content-match
    /// against.
    /// </summary>
    /// <remarks>
    /// Recorded for the same reason the attachment rows are: the GUI calls the
    /// <c>void</c> <c>RedactArea</c> overload and DROPS the report, so without
    /// this the refusal is computed and thrown away and the redacted-copy
    /// dialog says clean over a carrier we know we could not check. Standard's
    /// contract for this carrier is "kept but REPORTED", and a report nobody
    /// receives is not one.
    /// </remarks>
    internal int UncheckableAlternateText { get; private set; }

    internal void RecordUncheckableAlternateText(int count)
    {
        if (count > UncheckableAlternateText) UncheckableAlternateText = count;
    }

    private readonly List<string> _widthNotes = new();

    /// <summary>
    /// Lines where a width-closing policy could not do what it promised
    /// (#1751) — the gap would reopen, or a line kept its old alignment. A user
    /// who asked for the width to be closed must not be handed a page that
    /// looks closed and is not, and the GUI drops the engine's report, so they
    /// are recorded here like the #1586 refusal above.
    /// </summary>
    internal IReadOnlyList<string> WidthNotes => _widthNotes;

    internal void RecordWidthNote(string note)
    {
        if (!_widthNotes.Contains(note)) _widthNotes.Add(note);
    }

    private readonly HashSet<PdfDictionary> _formattedFields = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// #2059: dictionaries whose <c>/AA</c> held a format (<c>/F</c>) or
    /// keystroke (<c>/K</c>) action when the redaction profile removed it. The
    /// field's appearances may still show its value formatted ("$1,234.50"),
    /// and the term scrub of a later pass or term must still know it.
    /// </summary>
    internal bool HadFormatAction(PdfDictionary field) => _formattedFields.Contains(field);

    internal void RecordFormatAction(PdfDictionary field) => _formattedFields.Add(field);
}

public partial class PdfDocument
{
    internal PdfDocumentRedactionLedger RedactionLedger { get; } = new();
}
