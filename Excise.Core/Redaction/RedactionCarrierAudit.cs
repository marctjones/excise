using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Primitives;

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
    int UnexaminedXfaPacketCount,
    IReadOnlyList<string> TermsBelowScrubFloor)
{
    /// <summary>True when anything at all was left unexamined.</summary>
    public bool HasUnexaminedCarriers =>
        OutlineTitleCount > 0 || AnnotationsWithTextCount > 0 ||
        UnexaminedXfaPacketCount > 0 || TermsBelowScrubFloor.Count > 0;

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

        var normalizedTerms = (requestedTerms ?? Array.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var shortTerms = normalizedTerms
            .Where(t => !string.IsNullOrWhiteSpace(t) && t.Trim().Length < ScrubFloor)
            .ToList();
        var actionableTerms = normalizedTerms
            .Where(t => t.Length >= ScrubFloor)
            .ToList();

        // With no usable term (manual rectangle or a 1-2 character preview),
        // every positionless carrier remains unexamined. With an actionable
        // term, the caller's surgical scrub has examined these carriers; only
        // exact values that still contain the term are findings.
        IReadOnlyList<string>? termsToFind = actionableTerms.Count > 0 ? actionableTerms : null;

        return new RedactionCarrierAudit(
            CountOutlineTitles(document, termsToFind),
            CountAnnotationsWithText(document, termsToFind),
            CountUnexaminedXfaPackets(document, termsToFind),
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

        if (UnexaminedXfaPacketCount > 0)
        {
            lines.Add(
                $"{UnexaminedXfaPacketCount} XFA form XML packet(s) were not examined — the " +
                "packet was malformed, unsafe to parse, or no captured redaction text was available.");
        }

        foreach (var term in TermsBelowScrubFloor)
        {
            lines.Add(
                $"'{term}' is shorter than {ScrubFloor} characters, so document metadata was not " +
                "scrubbed for it. Page content was still redacted.");
        }

        return lines;
    }

    private static int CountOutlineTitles(PdfDocument document, IReadOnlyList<string>? terms)
    {
        try
        {
            return CountTitles(PdfOutlineParser.Parse(document), terms);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A parse failure is an unexamined carrier, never evidence that the
            // document has no bookmark text. One synthetic count is enough to
            // make the warning honest without guessing how many nodes exist.
            return 1;
        }
    }

    private static int CountTitles(IReadOnlyList<PdfOutlineItem> items, IReadOnlyList<string>? terms)
    {
        var n = 0;
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Title) && Matches(item.Title, terms)) n++;
            n += CountTitles(item.Children, terms);
        }
        return n;
    }

    private static int CountAnnotationsWithText(PdfDocument document, IReadOnlyList<string>? terms)
    {
        var n = 0;
        for (var p = 1; p <= document.PageCount; p++)
        {
            try
            {
                var page = document.GetPage(p);
                if (document.Resolve(page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance)
                    is not PdfArray annotations)
                {
                    continue;
                }

                foreach (var item in annotations)
                {
                    if (document.Resolve(item) is not PdfDictionary annotation) continue;
                    var contents = annotation.GetStringOrNull("Contents");
                    var title = annotation.GetStringOrNull("T");
                    if ((!string.IsNullOrWhiteSpace(contents) && Matches(contents, terms)) ||
                        (!string.IsNullOrWhiteSpace(title) && Matches(title, terms)))
                    {
                        n++;
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // One synthetic finding per unreadable page prevents a parser
                // failure from being reported as a clean carrier audit.
                n++;
            }
        }
        return n;
    }

    private static int CountUnexaminedXfaPackets(
        PdfDocument document,
        IReadOnlyList<string>? terms)
    {
        try
        {
            return XfaXmlCarrier.CountUnexaminedPackets(document, terms);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return 1;
        }
    }

    private static bool Matches(string value, IReadOnlyList<string>? terms) =>
        terms == null || terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}
