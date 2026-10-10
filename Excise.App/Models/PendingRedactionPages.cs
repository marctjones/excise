using Excise.Core.Document;

namespace Excise.App.Models;

/// <summary>
/// Where a pending redaction's page is in the document NOW. A mark is tied to the page it was
/// drawn on, not to the page number it had at the time: deleting, moving or inserting pages
/// (or undoing one) renumbers pages, and a stale number would redact a different page and
/// leave the marked text in the file.
/// </summary>
internal static class PendingRedactionPages
{
    /// <summary>
    /// The 1-based number of the marked page, or 0 when that page is no longer in the
    /// document. A mark with no page identity keeps its recorded number.
    /// </summary>
    public static int CurrentPageNumber(PdfDocument document, PendingRedaction pending)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pending);
        if (pending.PageIdentity is null)
            return pending.PageNumber;

        for (var i = 0; i < document.PageCount; i++)
        {
            if (ReferenceEquals(document.Pages[i].Dictionary, pending.PageIdentity))
                return i + 1;
        }

        return 0;
    }

    /// <summary>The same rectangle, addressed to another page number.</summary>
    public static PdfPageRect OnPage(PdfPageRect area, int pageNumber) =>
        new(pageNumber, area.X, area.Y, area.Width, area.Height, area.Space, area.UnitsPerPoint);
}
