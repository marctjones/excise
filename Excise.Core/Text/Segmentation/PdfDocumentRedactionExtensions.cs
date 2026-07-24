using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Excise.Core.Content;
using Excise.Core.Document;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// Document-level redaction helpers built on top of the per-page
/// <see cref="PdfPageRedactionExtensions.RedactArea"/> primitive. Locates
/// text by searching the extracted letter sequence of each page and
/// removes every occurrence from the content stream.
/// </summary>
/// <remarks>
/// <para>
/// This is the single source of truth for text-search-based redaction:
/// both the GUI (<c>Excise.App.Services.RedactionService.RedactText</c>)
/// and the <c>excise</c> CLI <c>redact</c> command go through
/// <see cref="RedactText(PdfDocument, string, bool, GlyphRemovalStrategy, bool)"/>.
/// </para>
/// <para>
/// A black rectangle overlay is appended to each page's content stream
/// for visual confirmation. The overlay is purely cosmetic — the
/// <em>security</em> guarantee comes from the content-stream rewrite in
/// <see cref="PdfPageRedactionExtensions.RedactArea"/>, which deletes
/// the glyphs themselves. Callers that want pure structural removal with
/// no visual marker can pass <c>drawBlackRect: false</c>.
/// </para>
/// </remarks>
public static class PdfDocumentRedactionExtensions
{
    /// <summary>
    /// Redact every occurrence of <paramref name="text"/> in
    /// <paramref name="document"/>. The document is mutated in place;
    /// call <see cref="PdfDocument.Save(string)"/> to persist.
    /// </summary>
    /// <param name="document">The PDF document to redact.</param>
    /// <param name="text">The text to redact.</param>
    /// <param name="caseSensitive">Whether matching is case-sensitive.</param>
    /// <param name="strategy">Strategy for selecting glyphs to remove when bounding boxes overlap.</param>
    /// <param name="drawBlackRect">Whether to append a visual black rectangle overlay.</param>
    /// <param name="includeHiddenLayers">Whether to include text in Optional Content Groups
    /// (OCGs) that are OFF by default. When true, this closes a security gap where content
    /// on hidden layers is invisible in the default view but fully extractable via other tools.
    /// Defaults to true for security (redact even hidden content).</param>
    /// <returns>
    /// Total number of matches removed across all pages.
    /// </returns>
    public static int RedactText(
        this PdfDocument document,
        string text,
        bool caseSensitive = false,
        GlyphRemovalStrategy strategy = GlyphRemovalStrategy.AnyOverlap,
        bool drawBlackRect = true,
        bool includeHiddenLayers = true)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));
        if (string.IsNullOrEmpty(text)) return 0;

        int totalMatches = 0;

        for (int pageNum = 1; pageNum <= document.PageCount; pageNum++)
        {
            var page = document.GetPage(pageNum);
            string? previousSearchText = null;
            var usedConservativeFallback = false;

            for (var pass = 0; pass < 10; pass++)
            {
                var letters = page.Letters;
                if (letters.Count == 0) break;

                // Filter letters based on includeHiddenLayers setting
                var searchLetters = includeHiddenLayers
                    ? letters
                    : letters.Where(l => !l.IsInHiddenOptionalContent).ToList();

                if (searchLetters.Count == 0) break;

                var searchTextSnapshot = string.Concat(searchLetters.Select(l => l.Value));
                var matches = FindTextMatches(searchLetters, text, caseSensitive);
                if (matches.Count == 0) break;

                var stalled = searchTextSnapshot == previousSearchText;
                previousSearchText = searchTextSnapshot;
                if (stalled && usedConservativeFallback)
                    break;

                foreach (var matchLetters in matches)
                {
                    var bbox = BoundingBoxOf(matchLetters);
                    if (stalled)
                    {
                        RemoveIntersectingOperators(page, bbox);
                    }
                    else if (IsInteractiveOnlyMatch(matchLetters))
                    {
                        InteractiveRedactionScrubber.ScrubArea(page, bbox);
                    }
                    else
                    {
                        page.RedactArea(bbox, strategy);
                    }

                    if (drawBlackRect) AppendBlackRectangle(page, bbox);
                }

                totalMatches += matches.Count;
                if (stalled)
                {
                    usedConservativeFallback = true;
                }
            }

            if (includeHiddenLayers)
                totalMatches += RemoveTextShowingOperatorsContaining(page, text, caseSensitive);
            totalMatches += RemoveTextLinesStillContaining(page, text, caseSensitive, includeHiddenLayers);
        }

        return totalMatches;
    }

    /// <summary>
    /// Bounding box that encloses all <paramref name="letters"/>.
    /// </summary>
    private static PdfRectangle BoundingBoxOf(IReadOnlyList<Letter> letters)
    {
        return new PdfRectangle(
            letters.Min(l => l.GlyphRectangle.Left),
            letters.Min(l => l.GlyphRectangle.Bottom),
            letters.Max(l => l.GlyphRectangle.Right),
            letters.Max(l => l.GlyphRectangle.Top));
    }

    /// <summary>
    /// True when every letter in a match was synthesized from something OTHER
    /// than the content stream — an AcroForm widget value or FreeText
    /// annotation content (#660) — meaning there is no content-stream glyph
    /// for <see cref="PdfPage.RedactArea"/>'s glyph/image passes to find.
    /// These route to <see cref="InteractiveRedactionScrubber"/> directly
    /// instead, which removes the underlying field value/appearance or
    /// annotation object.
    /// </summary>
    private static bool IsInteractiveOnlyMatch(IReadOnlyList<Letter> letters) =>
        letters.Count > 0 &&
        letters.All(l => l.FontName.StartsWith("AcroForm:", StringComparison.Ordinal) ||
                          l.FontName.StartsWith("Annotation:", StringComparison.Ordinal));

    private static void RemoveIntersectingOperators(PdfPage page, PdfRectangle bounds)
    {
        var content = page.GetContentStream();
        page.SetContentStream(content.RemoveIntersecting(bounds));
    }

    private static int RemoveTextShowingOperatorsContaining(
        PdfPage page,
        string searchText,
        bool caseSensitive)
    {
        var content = page.GetContentStream();
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var removed = 0;
        var kept = new List<ContentOperator>(content.Operators.Count);

        // Operator text is raw content-stream order; RTL documents usually
        // store Arabic/Hebrew in VISUAL (reversed) order, so a logical-order
        // needle must also be checked in its visual form (#632).
        var visualNeedle = BidiReorderer.ContainsStrongRtl(searchText)
            ? BidiReorderer.ReverseRtlRunsInString(searchText)
            : null;

        // The stream may also carry Arabic as shaped presentation forms
        // (#632) or Latin ligature code points like ﬃ (#722) while the
        // needle is plain/base letters. Fold the needle once; each
        // operator's text is checked folded — both as stored and with its RTL
        // runs reversed to logical order FIRST (reversing must happen on the
        // raw shaped chars: a lam-alef ligature is one char before folding
        // but two after, and reversing the folded string would scramble it).
        var foldedNeedle = PresentationFormFolding.Fold(searchText);

        foreach (var op in content.Operators)
        {
            if (op.Category == OperatorCategory.TextShowing)
            {
                var opText = op.TextContent ?? string.Empty;
                if (opText.IndexOf(searchText, comparison) >= 0 ||
                    (visualNeedle != null && opText.IndexOf(visualNeedle, comparison) >= 0) ||
                    ContainsFolded(opText, foldedNeedle, comparison))
                {
                    removed++;
                    continue;
                }
            }

            kept.Add(op);
        }

        if (removed > 0)
            page.SetContentStream(new ContentStream(kept));

        return removed;
    }

    /// <summary>
    /// True when <paramref name="opText"/>, folded from Arabic presentation
    /// forms / Latin ligatures to plain letters, contains
    /// <paramref name="foldedNeedle"/> — checked in stored order and, for
    /// strong-RTL content, with its RTL runs reversed to logical order
    /// before folding (visual-order streams).
    /// </summary>
    private static bool ContainsFolded(
        string opText, string foldedNeedle, StringComparison comparison)
    {
        if (foldedNeedle.Length == 0 ||
            !PresentationFormFolding.ContainsFoldable(opText))
        {
            return false;
        }

        if (PresentationFormFolding.Fold(opText).IndexOf(foldedNeedle, comparison) >= 0)
            return true;

        return BidiReorderer.ContainsStrongRtl(opText) &&
               PresentationFormFolding.Fold(BidiReorderer.ReverseRtlRunsInString(opText))
                   .IndexOf(foldedNeedle, comparison) >= 0;
    }

    private static int RemoveTextLinesStillContaining(
        PdfPage page,
        string searchText,
        bool caseSensitive,
        bool includeHiddenLayers)
    {
        var letters = page.Letters;
        var searchLetters = includeHiddenLayers
            ? letters
            : letters.Where(l => !l.IsInHiddenOptionalContent).ToList();
        var matches = FindTextMatches(searchLetters, searchText, caseSensitive);
        if (matches.Count == 0)
            return 0;

        var lineBands = matches.Select(match =>
        {
            var bottom = match.Min(l => l.GlyphRectangle.Bottom) - 1.0;
            var top = match.Max(l => l.GlyphRectangle.Top) + 1.0;
            return (Bottom: bottom, Top: top);
        }).ToList();

        var content = page.GetContentStream();
        var kept = new List<ContentOperator>(content.Operators.Count);
        foreach (var op in content.Operators)
        {
            if (op.Category == OperatorCategory.TextShowing &&
                op.BoundingBox is { } bounds &&
                lineBands.Any(b => bounds.Top > b.Bottom && bounds.Bottom < b.Top))
            {
                continue;
            }

            kept.Add(op);
        }

        page.SetContentStream(new ContentStream(kept));
        return matches.Count;
    }

    /// <summary>
    /// Append a filled black rectangle at <paramref name="rect"/> to the
    /// page's content stream — the standard
    /// <c>q 0 0 0 rg X Y W H re f Q</c> sequence. Used as a cosmetic
    /// overlay on top of structural glyph removal.
    /// </summary>
    private static void AppendBlackRectangle(PdfPage page, PdfRectangle rect)
    {
        var content = page.GetContentStream();
        var ops = content.Operators.ToList();
        ops.Add(ContentOperator.SaveState());
        ops.Add(ContentOperator.SetFillRgb(0, 0, 0));
        ops.Add(ContentOperator.Rectangle(
            rect.Left, rect.Bottom, rect.Right - rect.Left, rect.Top - rect.Bottom));
        ops.Add(ContentOperator.Fill());
        ops.Add(ContentOperator.RestoreState());
        page.SetContentStream(new ContentStream(ops));
    }

    /// <summary>
    /// Find every occurrence of <paramref name="searchText"/> in the
    /// concatenated letter sequence of a page and return the letter-slices
    /// that spell each match.
    /// </summary>
    /// <remarks>
    /// The letter sequence is already in reading order (rotation-aware via
    /// <c>TextExtractor</c>). Text is normalized (curly→straight quotes,
    /// en/em dash→hyphen, whitespace collapse) before comparison so
    /// typographic variation doesn't block a match. Matches are
    /// non-overlapping — greedy left-to-right.
    /// </remarks>
    private static List<List<Letter>> FindTextMatches(
        IReadOnlyList<Letter> letters, string searchText, bool caseSensitive)
    {
        var matches = new List<List<Letter>>();
        if (string.IsNullOrEmpty(searchText) || letters.Count == 0)
            return matches;

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        var sb = new StringBuilder(letters.Count);
        foreach (var l in letters) sb.Append(l.Value);
        var fullText = sb.ToString();

        var needle = NormalizeText(searchText);
        if (needle.Length == 0) return matches;

        // NOTE: not `i <= fullText.Length - needle.Length` — normalization can
        // EXPAND raw text (a lam-alef ligature is one raw char but two needle
        // chars), so a raw window shorter than the needle can still match.
        int i = 0;
        while (i < fullText.Length)
        {
            // Normalize may collapse whitespace, so a window of 2× needle
            // length is a safe upper bound on "does the text here start with
            // needle?"
            var windowLen = Math.Min(needle.Length * 2, fullText.Length - i);
            var normWindow = NormalizeText(fullText.Substring(i, windowLen));

            if (normWindow.StartsWith(needle, comparison))
            {
                // Expand one original character at a time until the
                // normalized prefix equals the needle — that's the minimum
                // letter span covering the match.
                int endIndex = i;
                while (endIndex < fullText.Length)
                {
                    var cur = NormalizeText(fullText.Substring(i, endIndex - i + 1));
                    if (cur.Equals(needle, comparison)) break;
                    if (cur.Length >= needle.Length) break;
                    endIndex++;
                }

                var matchLen = endIndex - i + 1;
                if (matchLen > 0 && i + matchLen <= letters.Count)
                {
                    var slice = new List<Letter>(matchLen);
                    for (int k = 0; k < matchLen; k++)
                        slice.Add(letters[i + k]);
                    matches.Add(slice);
                    i = endIndex + 1;
                    continue;
                }
            }

            i++;
        }

        return matches;
    }

    /// <summary>
    /// Normalize typographic variants (curly quotes, en/em dashes), fold
    /// Arabic presentation forms to base letters, and collapse whitespace so
    /// that string comparison isn't defeated by inconsequential differences
    /// between the search term and the text as encoded in the PDF.
    /// </summary>
    private static string NormalizeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Arabic can be stored as shaped presentation forms (U+FB50–U+FDFF,
        // U+FE70–U+FEFF — #632) and Latin text as ligature code points
        // (U+FB00–U+FB06, e.g. "oﬃce" — #722) while the user types plain
        // letters; fold both sides of the comparison to the plain-letter
        // decomposition. Note the fold EXPANDS (lam-alef 1 char → 2,
        // ﬃ 1 → 3), so normalized length may exceed raw length —
        // FindTextMatches accounts for that.
        var normalized = PresentationFormFolding.Fold(text)
            .Replace('’', '\'')  // right single quote
            .Replace('‘', '\'')  // left single quote
            .Replace('ʼ', '\'')  // modifier letter apostrophe
            .Replace('′', '\'')  // prime
            .Replace('–', '-')   // en dash
            .Replace('—', '-')   // em dash
            .Replace('−', '-')   // minus sign
            .Trim();

        return Regex.Replace(normalized, @"\s+", " ");
    }
}
