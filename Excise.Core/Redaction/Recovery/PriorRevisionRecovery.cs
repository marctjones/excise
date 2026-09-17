using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Excise.Core.Document;

namespace Excise.Core.Redaction.Recovery;

/// <summary>
/// #1592 — text that a previous revision of the file still holds.
///
/// <para><b>The failure mode.</b> A PDF can be updated INCREMENTALLY (§7.5.6):
/// the editor appends new objects and a new cross-reference section, leaving
/// the original bytes untouched at the front of the file. A tool that
/// "redacts" this way writes a new page whose glyphs are gone — and ships the
/// old page, glyphs and all, in the same file. Truncating the file at an
/// earlier <c>%%EOF</c> yields a complete, valid PDF of the document as it was
/// BEFORE the redaction. No parsing tricks, no estimation: the earlier
/// document is simply still there.</para>
///
/// <para><b>Why this is not the same as excise's own output.</b> excise writes
/// a fresh, fully-rewritten file rather than an incremental update, so its own
/// redacted copies have no prior revision to recover (#586). This channel
/// exists for everyone ELSE's output, which is the point of the de-redaction
/// side: the viewer does not expose prior revisions of an incrementally
/// updated INPUT, and neither does any mainstream reader, which is precisely
/// why this leak survives review.</para>
///
/// <para><b>What is reported.</b> A word present on a page in an earlier
/// revision and absent from the same page now, with the box it occupied THEN —
/// the location a restored copy draws into (#1588). The comparison is per page
/// and per word rather than whole-document, so an edit elsewhere in a long
/// document does not drown the one word that matters.</para>
///
/// <para><b>Noise, and why the finding is still CERTAIN.</b> Every legitimate
/// edit also shows up here: a typo fixed, a date changed, a paragraph rewritten.
/// That does not make the finding uncertain — the text really was in the file
/// and really is recoverable — it makes it UNINTERESTING, which is a question
/// for the report's reader, not for the channel. The mark linkage does that
/// filtering: a prior-revision word that lands under a redaction mark is the
/// signal; one that does not is an ordinary edit, and the report files it as
/// unlinked.</para>
/// </summary>
public static class PriorRevisionRecovery
{
    /// <summary>
    /// A file with more revisions than this is not walked exhaustively — each
    /// one costs a full parse. Real incremental-update chains are short; a file
    /// with hundreds is either generated or hostile.
    /// </summary>
    private const int MaxRevisionsExamined = 8;

    /// <summary>Shorter than this is punctuation-scale noise, not recovered content.</summary>
    private const int MinWordLength = 2;

    /// <param name="RevisionIndex">
    /// 0 is the ORIGINAL (oldest) revision; higher numbers are later updates.
    /// The current revision is not reported — it is what the reader already has.
    /// </param>
    /// <param name="Rect">The box the word occupied in that revision.</param>
    public readonly record struct PriorRevisionText(
        int RevisionIndex, int PageNumber, string Text, PdfRectangle Rect);

    /// <summary>What the scan could see, so a caller never reads silence as safety.</summary>
    /// <param name="RevisionCount">Total revisions in the file; 1 means no incremental update.</param>
    /// <param name="RevisionsParsed">How many earlier revisions actually opened.</param>
    /// <param name="RevisionsUnreadable">
    /// Earlier revisions that would not parse. NOT proof they are clean — a
    /// revision excise cannot open may still be readable by another tool, so
    /// the count is reported rather than swallowed.
    /// </param>
    /// <param name="PagesRemoved">Pages present in an earlier revision and gone now.</param>
    public readonly record struct PriorRevisionSummary(
        int RevisionCount, int RevisionsParsed, int RevisionsUnreadable, int PagesRemoved);

    /// <summary>
    /// Scan <paramref name="pdfBytes"/> for text earlier revisions still hold.
    /// Takes bytes, not a path: the channel needs the file's literal prefix, and
    /// an already-open <see cref="PdfDocument"/> cannot give it that.
    /// </summary>
    public static (IReadOnlyList<PriorRevisionText> Findings, PriorRevisionSummary Summary) Scan(
        byte[] pdfBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        var findings = new List<PriorRevisionText>();

        var boundaries = RevisionBoundaries(pdfBytes);
        // The last boundary is the current revision; everything before it is a
        // prior revision worth reading.
        var priorCount = Math.Max(0, boundaries.Count - 1);
        if (priorCount == 0)
            return (findings, new PriorRevisionSummary(Math.Max(1, boundaries.Count), 0, 0, 0));

        Dictionary<int, HashSet<string>> currentWords;
        int currentPageCount;
        try
        {
            using var current = PdfDocument.Open(pdfBytes);
            currentPageCount = current.PageCount;
            currentWords = WordsByPage(current, cancellationToken);
        }
        catch
        {
            // If the CURRENT revision will not parse there is nothing to diff
            // against, and reporting every prior word as "removed" would be a
            // fiction. Report the revision count and stop.
            return (findings, new PriorRevisionSummary(boundaries.Count, 0, priorCount, 0));
        }

        var parsed = 0;
        var unreadable = 0;
        var pagesRemoved = 0;

        // Newest-first: the revision immediately before the current one is the
        // one a redaction most likely replaced, and the budget should be spent
        // there rather than on the document's ancient history.
        var examined = 0;
        for (var i = priorCount - 1; i >= 0 && examined < MaxRevisionsExamined; i--, examined++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var prefix = new byte[boundaries[i]];
            Array.Copy(pdfBytes, prefix, prefix.Length);

            PdfDocument prior;
            try { prior = PdfDocument.Open(prefix); }
            catch { unreadable++; continue; }

            using (prior)
            {
                parsed++;
                if (prior.PageCount > currentPageCount)
                    pagesRemoved += prior.PageCount - currentPageCount;

                foreach (var (pageNumber, words) in WordsWithBoxes(prior, cancellationToken))
                {
                    currentWords.TryGetValue(pageNumber, out var nowOnPage);
                    foreach (var (text, rect) in words)
                    {
                        if (nowOnPage != null && nowOnPage.Contains(text)) continue;
                        findings.Add(new PriorRevisionText(i, pageNumber, text, rect));
                    }
                }
            }
        }

        return (findings,
            new PriorRevisionSummary(boundaries.Count, parsed, unreadable, pagesRemoved));
    }

    /// <summary>
    /// Byte offsets just past each <c>%%EOF</c>: the length of the file as it
    /// stood at each revision. §7.5.5 puts one at the end of every revision, so
    /// counting them counts revisions.
    /// </summary>
    internal static IReadOnlyList<int> RevisionBoundaries(byte[] bytes)
    {
        var marker = "%%EOF"u8;
        var offsets = new List<int>();
        for (var i = 0; i + marker.Length <= bytes.Length; i++)
        {
            if (bytes[i] != (byte)'%') continue;
            var match = true;
            for (var j = 1; j < marker.Length; j++)
            {
                if (bytes[i + j] == marker[j]) continue;
                match = false;
                break;
            }
            if (!match) continue;

            // Include the trailing EOL so the prefix is a well-formed file.
            var end = i + marker.Length;
            if (end < bytes.Length && bytes[end] == (byte)'\r') end++;
            if (end < bytes.Length && bytes[end] == (byte)'\n') end++;
            offsets.Add(end);
            i = end - 1;
        }
        return offsets;
    }

    private static Dictionary<int, HashSet<string>> WordsByPage(
        PdfDocument document, CancellationToken cancellationToken)
    {
        var byPage = new Dictionary<int, HashSet<string>>();
        for (var p = 1; p <= document.PageCount; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var word in document.GetPage(p).GetWords())
                    if (IsReportable(word.Text)) set.Add(word.Text);
            }
            catch { /* a page that will not lay out contributes nothing to the diff */ }
            byPage[p] = set;
        }
        return byPage;
    }

    private static IEnumerable<(int PageNumber, List<(string Text, PdfRectangle Rect)> Words)>
        WordsWithBoxes(PdfDocument document, CancellationToken cancellationToken)
    {
        for (var p = 1; p <= document.PageCount; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var words = new List<(string, PdfRectangle)>();
            try
            {
                // Deduplicated by text AND box: the same word repeated at the
                // same place is one recovery, not several.
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var word in document.GetPage(p).GetWords())
                {
                    if (!IsReportable(word.Text)) continue;
                    var box = word.BoundingBox.Normalize();
                    var key = $"{word.Text}|{box.Left:F1}|{box.Bottom:F1}";
                    if (!seen.Add(key)) continue;
                    words.Add((word.Text, box));
                }
            }
            catch { continue; }
            yield return (p, words);
        }
    }

    private static bool IsReportable(string? text)
        => !string.IsNullOrWhiteSpace(text)
           && text.Length >= MinWordLength
           && text.Any(char.IsLetterOrDigit);
}
