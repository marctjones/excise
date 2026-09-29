using Avalonia.Input;
using Excise.Core.Document;

namespace Excise.Avalonia.Controls;

/// <summary>
/// One view's half of the pointer-to-content funnel (#1842,
/// <c>docs/architecture/pdf-viewer-control-architecture.md</c> §3.2). The view says
/// which page is under the pointer and where on it, in its own DIP space; the
/// viewer performs the ONE <c>PdfCoordinateMapper.ToContentPoints</c>
/// conversion, so the two views cannot drift into two mappings (#667, #992).
/// </summary>
internal interface IPageHitSurface
{
    /// <summary>
    /// True when the event came from one of this view's own interactive overlays
    /// (a typewriter box, a continuous form field): the root handlers must leave it alone.
    /// </summary>
    bool IsOwnOverlayEvent(PointerEventArgs e);

    /// <summary>
    /// The page under the pointer and the pointer's position on it, as a zero-size
    /// rect tagged with this view's space and scale: <c>ViewerDips</c> at the logical
    /// render DPI (single-page) or <c>ContinuousDips</c> at <c>PointsToDip × zoom</c>
    /// (continuous). False when the pointer is on no page.
    /// </summary>
    bool TryMapPointToPage(PointerEventArgs e, out int pageNumber, out PdfPageRect point);
}
