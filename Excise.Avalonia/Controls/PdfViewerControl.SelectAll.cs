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
        if (Document == null) return false;
        return ViewMode == PdfViewMode.Continuous
            ? ContinuousPart.SelectAll(pageNumber > 0 ? pageNumber : ContextMenuPageNumber)
            : SinglePagePart.SelectAll();
    }
}
