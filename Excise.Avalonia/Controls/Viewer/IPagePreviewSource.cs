using Avalonia.Media.Imaging;

namespace Excise.Avalonia.Controls;

/// <summary>
/// A view that can hand the single-page view a picture of a page it already shows, for
/// the placeholder the single-page view displays while its sharp render runs (#1473,
/// #1842, <c>docs/architecture/pdf-viewer-control-architecture.md</c> §3.2). The
/// continuous view implements it; this is the seam that ends the single-page view's
/// reach into the continuous view's slots.
/// </summary>
internal interface IPagePreviewSource
{
    /// <summary>
    /// A viewer-owned copy of the page's current picture if it matches the current zoom,
    /// else null. The copy is the caller's to dispose.
    /// </summary>
    WriteableBitmap? TryCopyCompositeForPage(int pageNumber, double widthPt, double heightPt);
}
