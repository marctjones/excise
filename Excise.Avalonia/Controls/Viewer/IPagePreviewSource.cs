using System;
using Avalonia;
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

    /// <summary>
    /// The pixel size <see cref="TryCopyCompositeInto"/> would write for the page, or null
    /// when the page has no picture matching the current zoom: the same match
    /// <see cref="TryCopyCompositeForPage"/> makes, without the copy.
    /// </summary>
    PixelSize? CompositeCopySize(int pageNumber, double widthPt, double heightPt);

    /// <summary>
    /// Write the page's current picture into memory the caller owns instead of a new
    /// <see cref="WriteableBitmap"/> (#1926: a single-page cache that owns its page memory
    /// cannot take a bitmap Avalonia allocated). The pixels are the ones
    /// <see cref="TryCopyCompositeForPage"/> would return, Bgra8888 premultiplied, in the
    /// top-left <see cref="CompositeCopySize"/> of the destination. The first
    /// <see cref="CompositeCopySize"/>.Height rows are overwritten in full,
    /// <paramref name="rowBytes"/> each, white where the picture does not reach. False, with
    /// nothing written, when there is no matching picture or the destination is smaller
    /// than <see cref="CompositeCopySize"/>.
    /// </summary>
    /// <param name="destination">The first byte of the destination's first row.</param>
    /// <param name="rowBytes">Bytes from one row to the next; at least 4 × the width.</param>
    /// <param name="destinationSize">The destination's size in pixels.</param>
    bool TryCopyCompositeInto(int pageNumber, double widthPt, double heightPt,
        IntPtr destination, int rowBytes, PixelSize destinationSize);
}
