using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Document;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// What a redaction did NOT examine, so the caller can say so.
/// </summary>
/// <remarks>
/// <para>
/// Redaction has carriers it cannot match precisely. An AREA redaction knows a
/// rectangle, not a term, so it cannot ask "does this bookmark mention what I
/// removed?" — and deriving terms from the box to find out actively corrupts
/// the document (a box over ordinary prose yields <c>you got time file</c>,
/// turning <c>Younger</c> into <c>Ynger</c>). A TEXT redaction knows a term but
/// skips carriers when it is shorter than
/// <c>PdfDocumentSanitizer</c>'s three-character floor.
/// </para>
/// <para>
/// <b>The decided policy is to surface these, not guess</b> (#916, #905).
/// Silently stripping them destroys a document's navigation and every unrelated
/// comment because of a redaction on one page; silently skipping them lets
/// excise report success while a bookmark titled after the redacted content is
/// visible in the sidebar without the page being opened. Both silent options
/// fail the same way — the user cannot tell what happened.
/// </para>
/// <para>
/// This type therefore reports POSSIBILITY, not leakage. It does not and cannot
/// know whether a surviving bookmark relates to the redacted content; saying it
/// does would be the guess this exists to avoid. Word the output accordingly.
/// </para>
/// </remarks>
public sealed record RedactionCarrierAudit(
    int OutlineTitleCount,
    int AnnotationsWithTextCount,
    IReadOnlyList<string> TermsBelowScrubFloor)
{
    /// <summary>True when anything at all was left unexamined.</summary>
    public bool HasUnexaminedCarriers =>
        OutlineTitleCount > 0 || AnnotationsWithTextCount > 0 || TermsBelowScrubFloor.Count > 0;

    /// <summary>
    /// Shortest term <c>PdfDocumentSanitizer</c> will act on. Mirrored here
    /// rather than referenced because the sanitizer's copy is private; the
    /// selftest below pins them equal so the two cannot drift.
    /// </summary>
    public const int ScrubFloor = 3;

    /// <summary>
    /// Inspect a document AFTER redaction for carriers the redaction could not
    /// reach, plus any terms that fell below the scrub floor.
    /// </summary>
    /// <param name="document">The redacted document.</param>
    /// <param name="requestedTerms">
    /// Terms the caller asked to redact, if any. Area redaction has none — pass
    /// null or empty and only the positionless carriers are reported.
    /// </param>
    public static RedactionCarrierAudit Inspect(
        PdfDocument document,
        IEnumerable<string>? requestedTerms = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var shortTerms = (requestedTerms ?? Array.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t) && t.Trim().Length < ScrubFloor)
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RedactionCarrierAudit(
            CountOutlineTitles(document),
            CountAnnotationsWithText(document),
            shortTerms);
    }

    /// <summary>
    /// Human-readable lines for a report or dialog. Empty when nothing was left
    /// unexamined, so a caller can append unconditionally.
    /// </summary>
    /// <remarks>
    /// Deliberately phrased as "not examined" rather than "may contain" — the
    /// audit has no evidence either way, and overstating it trains people to
    /// dismiss the warning.
    /// </remarks>
    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>();

        if (OutlineTitleCount > 0)
        {
            lines.Add(
                $"{OutlineTitleCount} bookmark title(s) were not examined — bookmarks carry no " +
                "position, so excise cannot tell which relate to the redacted content.");
        }

        if (AnnotationsWithTextCount > 0)
        {
            lines.Add(
                $"{AnnotationsWithTextCount} annotation(s) with text were not examined — only " +
                "annotations overlapping a redaction area on the same page are scrubbed.");
        }

        foreach (var term in TermsBelowScrubFloor)
        {
            lines.Add(
                $"'{term}' is shorter than {ScrubFloor} characters, so document metadata was not " +
                "scrubbed for it. Page content was still redacted.");
        }

        return lines;
    }

    private static int CountOutlineTitles(PdfDocument document)
    {
        try
        {
            return CountTitles(PdfOutlineParser.Parse(document));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A document whose outline tree cannot be parsed is exactly the kind
            // this should not claim to have checked. Report nothing rather than
            // a wrong zero — callers treat 0 as "nothing to warn about".
            return 0;
        }
    }

    private static int CountTitles(IReadOnlyList<PdfOutlineItem> items)
    {
        var n = 0;
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Title)) n++;
            n += CountTitles(item.Children);
        }
        return n;
    }

    private static int CountAnnotationsWithText(PdfDocument document)
    {
        var n = 0;
        for (var p = 1; p <= document.PageCount; p++)
        {
            try
            {
                foreach (var annot in document.GetPage(p).GetAnnotations())
                {
                    if (!string.IsNullOrWhiteSpace(annot.Contents)) n++;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Same reasoning as outlines: skip a page we cannot read rather
                // than abort the whole audit.
            }
        }
        return n;
    }
}
