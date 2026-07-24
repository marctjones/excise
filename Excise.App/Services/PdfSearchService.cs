using Microsoft.Extensions.Logging;
using Excise.Core.Document;
using Excise.Core.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Excise.App.Services;

/// <summary>
/// Service for searching text within PDF documents
/// </summary>
public class PdfSearchService
{
    private readonly ILogger<PdfSearchService> _logger;

    public PdfSearchService(ILogger<PdfSearchService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Represents a single search match
    /// </summary>
    public class SearchMatch
    {
        public int PageIndex { get; set; }
        public string MatchedText { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string Context { get; set; } = string.Empty; // Surrounding text for preview
    }

    /// <summary>
    /// Progress report emitted as the service walks the document.
    /// Consumed via IProgress&lt;SearchProgress&gt; — typical use is to
    /// drive a status bar / inline spinner / determinate progress
    /// indicator while a long search runs on a large file.
    /// </summary>
    public readonly record struct SearchProgress(
        int PagesScanned, int TotalPages, int MatchesFound);

    /// <summary>
    /// Search for text in a PDF file
    /// </summary>
    /// <param name="pdfPath">Path to the PDF file</param>
    /// <param name="searchTerm">Text to search for</param>
    /// <param name="caseSensitive">Whether the search should be case sensitive</param>
    /// <param name="wholeWordsOnly">Whether to match whole words only</param>
    /// <param name="useRegex">Whether to treat searchTerm as a regular expression</param>
    /// <returns>List of search matches</returns>
    public List<SearchMatch> Search(
        string pdfPath,
        string searchTerm,
        bool caseSensitive = false,
        bool wholeWordsOnly = false,
        bool useRegex = false,
        IProgress<SearchProgress>? progress = null)
    {
        var matches = new List<SearchMatch>();

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            _logger.LogWarning("Search term is empty");
            return matches;
        }

        try
        {
            using var document = PdfDocument.Open(pdfPath);
            return Search(document, searchTerm, caseSensitive, wholeWordsOnly, useRegex,
                progress: progress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching PDF: {Message}", ex.Message);
            return matches;
        }
    }

    /// <summary>
    /// Search for text in an already-open PDF document. Use this overload
    /// when the caller already holds a parsed <see cref="PdfDocument"/> —
    /// re-parsing on every keystroke makes typed search unusable on
    /// large files. The optional <paramref name="cancellationToken"/>
    /// lets a debounced caller drop a stale search when a newer
    /// keystroke supersedes it.
    /// </summary>
    /// <summary>
    /// Search using a pre-built <see cref="DocumentTextIndex"/>. Skips
    /// per-page text extraction (the dominant cost) so subsequent
    /// queries on the same document are nearly instant.
    /// </summary>
    public List<SearchMatch> Search(
        DocumentTextIndex index,
        string searchTerm,
        bool caseSensitive = false,
        bool wholeWordsOnly = false,
        bool useRegex = false,
        CancellationToken cancellationToken = default,
        IProgress<SearchProgress>? progress = null)
    {
        var matches = new List<SearchMatch>();
        if (index == null || string.IsNullOrWhiteSpace(searchTerm))
            return matches;

        try
        {
            var totalPages = index.PageCount;
            progress?.Report(new SearchProgress(0, totalPages, 0));

            for (int i = 0; i < totalPages; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!index.IsPageIndexed(i))
                {
                    // Index built incrementally — skip pages not yet cached.
                    // The next search after the build completes covers them.
                    continue;
                }
                var pageText = index.GetPageText(i);
                var pageWords = index.GetPageWords(i);
                if (useRegex)
                    matches.AddRange(SearchWithRegex(pageText, searchTerm, pageWords, i, caseSensitive));
                else if (wholeWordsOnly)
                    matches.AddRange(SearchWholeWords(pageWords, searchTerm, i, caseSensitive));
                else
                    matches.AddRange(SearchSubstring(pageText, pageWords, searchTerm, i, caseSensitive));

                if (progress != null && ((i & 3) == 0 || i == totalPages - 1))
                    progress.Report(new SearchProgress(i + 1, totalPages, matches.Count));
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Indexed search cancelled at page progress; returning partial results");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching index: {Message}", ex.Message);
        }
        return matches;
    }

    public List<SearchMatch> Search(
        PdfDocument document,
        string searchTerm,
        bool caseSensitive = false,
        bool wholeWordsOnly = false,
        bool useRegex = false,
        CancellationToken cancellationToken = default,
        IProgress<SearchProgress>? progress = null)
    {
        if (document == null || string.IsNullOrWhiteSpace(searchTerm))
            return new List<SearchMatch>();

        var matches = new List<SearchMatch>();

        try
        {
            var totalPages = document.PageCount;
            _logger.LogInformation("Searching for '{SearchTerm}' in PDF with {PageCount} pages",
                searchTerm, totalPages);

            // Initial 0/N report so the UI can show the spinner+text
            // immediately rather than waiting for page 1 to finish.
            progress?.Report(new SearchProgress(0, totalPages, 0));

            // Sequential search through pages
            for (int i = 0; i < totalPages; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var page = document.GetPage(i + 1);
                    var pageMatches = SearchInPage(
                        page, searchTerm, caseSensitive, wholeWordsOnly, useRegex, i);

                    matches.AddRange(pageMatches);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error searching page {PageIndex}", i);
                }

                // Emit progress at i=0, every 4 pages, and on the last page
                if (progress != null && ((i & 3) == 0 || i == totalPages - 1))
                    progress.Report(new SearchProgress(i + 1, totalPages, matches.Count));
            }

            _logger.LogInformation("Found {MatchCount} matches for '{SearchTerm}'",
                matches.Count, searchTerm);

            return matches;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Search cancelled; returning partial results");
            return matches;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching PDF: {Message}", ex.Message);
            return matches;
        }
    }


    /// <summary>
    /// Search for text in a specific page
    /// </summary>
    public List<SearchMatch> SearchInPage(
        PdfPage page,
        string searchTerm,
        bool caseSensitive = false,
        bool wholeWordsOnly = false,
        bool useRegex = false,
        int pageIndex = 0)
    {
        var matches = new List<SearchMatch>();

        try
        {
            var pageText = page.Text;
            var words = page.GetWords();

            if (useRegex)
            {
                matches.AddRange(SearchWithRegex(pageText, searchTerm, words, pageIndex, caseSensitive));
            }
            else if (wholeWordsOnly)
            {
                matches.AddRange(SearchWholeWords(words, searchTerm, pageIndex, caseSensitive));
            }
            else
            {
                matches.AddRange(SearchSubstring(pageText, words, searchTerm, pageIndex, caseSensitive));
            }

            // Annotation text (sticky-note bodies, free-text callouts,
            // filled form-field values) lives outside the page content
            // stream and is invisible to page.Text / page.GetWords().
            // Without this scan, find-in-document silently misses every
            // comment and form value — exactly the case the renderer
            // started covering once /AP / /Contents / /V got wired in.
            matches.AddRange(SearchAnnotations(page, searchTerm, pageIndex,
                caseSensitive, wholeWordsOnly, useRegex));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error searching page {PageIndex}: {Message}",
                pageIndex, ex.Message);
        }

        return matches;
    }

    /// <summary>
    /// Walk a page's <c>/Annots</c> array and search the body text
    /// fields users expect to be searchable: <c>/Contents</c> for
    /// markup annotations (Text, FreeText, Highlight, Stamp, etc.) and
    /// <c>/V</c> for filled form-field widgets. Each match is reported
    /// at the annotation's <c>/Rect</c> — granular per-glyph positions
    /// aren't available without parsing the appearance stream, and the
    /// rect is what users want to highlight anyway.
    /// </summary>
    private IEnumerable<SearchMatch> SearchAnnotations(
        PdfPage page,
        string searchTerm,
        int pageIndex,
        bool caseSensitive,
        bool wholeWordsOnly,
        bool useRegex)
    {
        if (page.Dictionary.GetOptional("Annots") == null)
            yield break;

        IReadOnlyList<PdfAnnotation> annots;
        try { annots = page.GetAnnotations(); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetAnnotations failed on page {PageIndex}", pageIndex);
            yield break;
        }

        foreach (var annot in annots)
        {
            // /Contents — body text for sticky notes, FreeText callouts,
            // markup tooltips, etc.
            var contents = annot.Contents;
            if (!string.IsNullOrEmpty(contents))
            {
                foreach (var m in MatchAnnotationText(
                    contents!, annot, pageIndex, "annotation",
                    searchTerm, caseSensitive, wholeWordsOnly, useRegex))
                    yield return m;
            }

            // /V — current value of a form widget. Pull a string out of
            // the raw dict (annot.V isn't exposed on PdfAnnotation since
            // it's field-level, but the underlying RawDictionary keeps it).
            var vObj = annot.RawDictionary.GetOptional("V");
            if (vObj is Excise.Core.Primitives.PdfString vStr)
            {
                if (!string.IsNullOrEmpty(vStr.Value))
                {
                    foreach (var m in MatchAnnotationText(
                        vStr.Value, annot, pageIndex, "form value",
                        searchTerm, caseSensitive, wholeWordsOnly, useRegex))
                        yield return m;
                }
            }
        }
    }

    /// <summary>
    /// Apply the same matching mode (substring / whole-word / regex)
    /// the page-content path uses, but on a single annotation-text
    /// blob. Each match emits a SearchMatch positioned at the
    /// annotation's rect (caller doesn't have per-character positions
    /// in this text source).
    /// </summary>
    private IEnumerable<SearchMatch> MatchAnnotationText(
        string text,
        PdfAnnotation annot,
        int pageIndex,
        string source,
        string searchTerm,
        bool caseSensitive,
        bool wholeWordsOnly,
        bool useRegex)
    {
        var rect = annot.Rect;
        StringComparison cmp = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        // Fold Arabic presentation forms (#632) and Latin ligatures (#722)
        // on both sides; identity for other text. Indices below are all
        // within the folded string.
        text = PresentationFormFolding.Fold(text);
        searchTerm = PresentationFormFolding.Fold(searchTerm);

        if (useRegex)
        {
            Regex regex;
            try
            {
                regex = new Regex(searchTerm,
                    caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            }
            catch (ArgumentException) { yield break; }
            foreach (Match match in regex.Matches(text))
            {
                yield return BuildAnnotationMatch(rect, pageIndex, match.Value, text, match.Index, match.Length, source);
            }
            yield break;
        }

        if (wholeWordsOnly)
        {
            // Word boundaries via regex with the literal term escaped.
            var pattern = $@"\b{Regex.Escape(searchTerm)}\b";
            var regex = new Regex(pattern,
                caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            foreach (Match match in regex.Matches(text))
            {
                yield return BuildAnnotationMatch(rect, pageIndex, match.Value, text, match.Index, match.Length, source);
            }
            yield break;
        }

        // Substring search.
        int startIndex = 0;
        while (startIndex < text.Length)
        {
            int found = text.IndexOf(searchTerm, startIndex, cmp);
            if (found < 0) break;
            yield return BuildAnnotationMatch(
                rect, pageIndex,
                text.Substring(found, searchTerm.Length),
                text, found, searchTerm.Length, source);
            startIndex = found + Math.Max(1, searchTerm.Length);
        }
    }

    private SearchMatch BuildAnnotationMatch(
        Excise.Core.Document.PdfRectangle rect,
        int pageIndex,
        string matched,
        string fullText,
        int matchIndex,
        int matchLength,
        string source)
    {
        return new SearchMatch
        {
            PageIndex = pageIndex,
            MatchedText = matched,
            // Position: the annotation's /Rect (PDF Y-up). Normalize
            // so X/Y are the lower-left corner regardless of how the
            // PDF stored the rect ordering.
            X = Math.Min(rect.Left, rect.Right),
            Y = Math.Min(rect.Bottom, rect.Top),
            Width = Math.Abs(rect.Right - rect.Left),
            Height = Math.Abs(rect.Top - rect.Bottom),
            Context = GetContext(fullText, matchIndex, matchLength) + $" [in {source}]",
        };
    }

    /// <summary>
    /// Search using regular expression
    /// </summary>
    private List<SearchMatch> SearchWithRegex(
        string pageText,
        string pattern,
        IReadOnlyList<Word> words,
        int pageIndex,
        bool caseSensitive)
    {
        var matches = new List<SearchMatch>();

        // Arabic can be stored as shaped presentation forms (#632) and Latin
        // text as ligature code points (#722) while the user types plain
        // letters; fold both sides so matching sees plain letters. All index
        // arithmetic below (word spans, contexts) is done consistently in
        // folded space.
        pageText = PresentationFormFolding.Fold(pageText);
        pattern = PresentationFormFolding.Fold(pattern);

        try
        {
            var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            var regex = new Regex(pattern, options);
            var regexMatches = regex.Matches(pageText);
            var wordSpans = BuildWordSpans(words, pageText);

            foreach (Match match in regexMatches)
            {
                if (TryFindWordBoundsAtPosition(
                    wordSpans,
                    match.Index,
                    match.Length,
                    out var firstWord,
                    out var lastWord))
                {
                    matches.Add(new SearchMatch
                    {
                        PageIndex = pageIndex,
                        MatchedText = match.Value,
                        X = firstWord!.BoundingBox.Left,
                        Y = firstWord.BoundingBox.Bottom,
                        Width = lastWord!.BoundingBox.Right - firstWord.BoundingBox.Left,
                        Height = Math.Max(firstWord.BoundingBox.Height, lastWord.BoundingBox.Height),
                        Context = GetContext(pageText, match.Index, match.Length)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Invalid regex pattern: {Pattern}", pattern);
        }

        return matches;
    }

    /// <summary>
    /// Search for whole words only
    /// </summary>
    private List<SearchMatch> SearchWholeWords(
        IReadOnlyList<Word> words,
        string searchTerm,
        int pageIndex,
        bool caseSensitive)
    {
        var matches = new List<SearchMatch>();
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // Fold Arabic presentation forms (#632) and Latin ligatures (#722)
        // on both sides.
        searchTerm = PresentationFormFolding.Fold(searchTerm);

        foreach (var word in words)
        {
            if (PresentationFormFolding.Fold(word.Text).Equals(searchTerm, comparison))
            {
                matches.Add(new SearchMatch
                {
                    PageIndex = pageIndex,
                    MatchedText = word.Text,
                    X = word.BoundingBox.Left,
                    Y = word.BoundingBox.Bottom,
                    Width = word.BoundingBox.Width,
                    Height = word.BoundingBox.Height,
                    Context = $"...{word.Text}..."
                });
            }
        }

        return matches;
    }

    /// <summary>
    /// Search for substring matches
    /// </summary>
    private List<SearchMatch> SearchSubstring(
        string pageText,
        IReadOnlyList<Word> words,
        string searchTerm,
        int pageIndex,
        bool caseSensitive)
    {
        var matches = new List<SearchMatch>();
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // Arabic can be stored as shaped presentation forms (#632) and Latin
        // text as ligature code points (#722) while the user types plain
        // letters; fold both sides so matching sees plain letters. Word
        // spans and context are computed in the same folded space, so all
        // index arithmetic stays consistent.
        pageText = PresentationFormFolding.Fold(pageText);
        searchTerm = PresentationFormFolding.Fold(searchTerm);

        var wordSpans = BuildWordSpans(words, pageText);

        int index = 0;
        while ((index = pageText.IndexOf(searchTerm, index, comparison)) != -1)
        {
            if (TryFindWordBoundsAtPosition(
                wordSpans,
                index,
                searchTerm.Length,
                out var firstWord,
                out var lastWord))
            {
                matches.Add(new SearchMatch
                {
                    PageIndex = pageIndex,
                    MatchedText = pageText.Substring(index, searchTerm.Length),
                    X = firstWord!.BoundingBox.Left,
                    Y = firstWord.BoundingBox.Bottom,
                    Width = lastWord!.BoundingBox.Right - firstWord.BoundingBox.Left,
                    Height = Math.Max(firstWord.BoundingBox.Height, lastWord.BoundingBox.Height),
                    Context = GetContext(pageText, index, searchTerm.Length)
                });
            }

            index += searchTerm.Length;
        }

        return matches;
    }

    /// <summary>
    /// Find words at a specific text position.
    ///
    /// FIX for issue #96: The previous implementation used IndexOf to find word positions,
    /// which was unreliable when the same word appeared multiple times on a page.
    ///
    /// New approach: build page word spans once by iterating through words in order,
    /// then reuse those spans for every match on that page. This ensures correct
    /// bounding box mapping even when duplicate words exist without rebuilding a
    /// character map for each match.
    /// </summary>
    private static List<WordSpan> BuildWordSpans(IReadOnlyList<Word> words, string pageText)
    {
        var spans = new List<WordSpan>(words.Count);
        int charIndex = 0;

        foreach (var word in words)
        {
            // Word text is folded the same way callers fold pageText
            // (#632, #722) — identity for unaffected text — so spans line
            // up either way.
            var wordText = PresentationFormFolding.Fold(word.Text);

            // Find next occurrence of this word's text starting from current position
            // This handles duplicates correctly by advancing position after each match
            int wordPos = pageText.IndexOf(wordText, charIndex, StringComparison.Ordinal);

            if (wordPos != -1)
            {
                spans.Add(new WordSpan(wordPos, wordPos + wordText.Length, word));

                // Advance past this word (+ any whitespace/separator)
                charIndex = wordPos + wordText.Length;
            }
        }

        return spans;
    }

    private static bool TryFindWordBoundsAtPosition(
        IReadOnlyList<WordSpan> wordSpans,
        int startIndex,
        int length,
        out Word? firstWord,
        out Word? lastWord)
    {
        firstWord = null;
        lastWord = null;

        // Now find all words that overlap with the search range [startIndex, startIndex+length)
        var matchEndIndex = startIndex + length;

        foreach (var span in wordSpans)
        {
            if (span.Start >= matchEndIndex)
                break;

            if (span.End <= startIndex)
                continue;

            firstWord ??= span.Word;
            lastWord = span.Word;
        }

        return firstWord != null && lastWord != null;
    }

    private readonly record struct WordSpan(int Start, int End, Word Word);

    /// <summary>
    /// Get surrounding context for a match
    /// </summary>
    private string GetContext(string pageText, int matchIndex, int matchLength, int contextChars = 30)
    {
        int start = Math.Max(0, matchIndex - contextChars);
        int end = Math.Min(pageText.Length, matchIndex + matchLength + contextChars);

        string prefix = start > 0 ? "..." : "";
        string suffix = end < pageText.Length ? "..." : "";

        return prefix + pageText.Substring(start, end - start) + suffix;
    }
}
