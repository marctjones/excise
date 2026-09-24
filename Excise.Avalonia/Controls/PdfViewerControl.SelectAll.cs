using System.Linq;
using Excise.Core.Text;

namespace Excise.Avalonia.Controls;

/// <summary>Select All Text (#1814).</summary>
public partial class PdfViewerControl
{
    /// <summary>
    /// Select every letter of one page and report it exactly as a finished drag would, so the
    /// highlight, <see cref="TextSelected"/> and everything downstream of it (Copy, Highlight,
    /// Redact Selection) treat it as an ordinary selection.
    /// </summary>
    /// <param name="pageNumber">1-based page, or 0 for the page a right-click opened the menu on,
    /// else the page filling the viewport (continuous view) or the displayed page (single-page).</param>
    /// <returns>False when there is no document or the page has no text.</returns>
    public bool SelectAllText(int pageNumber = 0)
    {
        var doc = Document;
        if (doc == null) return false;

        if (ViewMode == PdfViewMode.Continuous)
        {
            var page = pageNumber > 0 ? pageNumber
                : ContextMenuPageNumber > 0 ? ContextMenuPageNumber
                : MostVisiblePage;
            if (page < 1 || page > doc.PageCount) return false;

            var letters = GetContinuousPageLetters(page);
            if (letters.Reading.Count == 0) return false;

            ClearContinuousSelectionHighlight();
            _continuousSelectionPage = _continuousSelectionFocusPage = page;
            _continuousSelectionAnchor = letters.Reading[0];
            _continuousSelectionFocus = letters.Reading[^1];
            DrawContinuousSelectionSpan();
            EndContinuousTextSelection();
            return true;
        }

        EnsurePageLettersLoaded();
        if (_readingOrderedLetters == null || _readingOrderedLetters.Count == 0) return false;

        _selectionAnchor = _readingOrderedLetters[0];
        _selectionFocus = _readingOrderedLetters[^1];
        DrawSelectionRange(TextSelectionEngine.ColumnAwareRange(
            _readingOrderedLetters, _selectionAnchor, _selectionFocus, _columnGapThreshold));
        RaiseSinglePageTextSelected();
        return true;
    }
}
