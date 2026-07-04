using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Pdfe.Core.Content;
using Pdfe.Core.Document;

namespace Pdfe.Core.Text.Segmentation;

/// <summary>
/// Document-level redaction helpers built on top of the per-page
/// <see cref="PdfPageRedactionExtensions.RedactArea"/> primitive. Locates
/// text by searching the extracted letter sequence of each page and
/// removes every occurrence from the content stream.
/// </summary>
/// <remarks>
/// <para>
/// This is the single source of truth for text-search-based redaction:
/// both the GUI (<c>PdfEditor.Services.RedactionService.RedactText</c>)
/// and the <c>pdfe</c> CLI <c>redact</c> command go through
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
                    else if (IsAcroFormMatch(matchLetters))
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

    private static bool IsAcroFormMatch(IReadOnlyList<Letter> letters) =>
        letters.Count > 0 &&
        letters.All(l => l.FontName.StartsWith("AcroForm:", StringComparison.Ordinal));

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

        foreach (var op in content.Operators)
        {
            if (op.Category == OperatorCategory.TextShowing &&
                (op.TextContent ?? string.Empty).IndexOf(searchText, comparison) >= 0)
            {
                removed++;
                continue;
            }

            kept.Add(op);
        }

        if (removed > 0)
            page.SetContentStream(new ContentStream(kept));

        return removed;
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

        int i = 0;
        while (i <= fullText.Length - needle.Length)
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
    /// Normalize typographic variants (curly quotes, en/em dashes) and
    /// collapse whitespace so that string comparison isn't defeated by
    /// inconsequential differences between the search term and the text
    /// as encoded in the PDF.
    /// </summary>
    private static string NormalizeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var normalized = text
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
