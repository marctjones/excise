using System;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

using static Excise.Avalonia.Controls.PdfViewerControl;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Edit-mode switch study, option 3 (#1473): when single-page view has nothing
/// to show yet, size the page from its geometry at once and show a copy of the
/// continuous view's composite for that page until the sharp render lands.
/// </summary>
/// <remarks>
/// Lifetime (#1466/#1467): the placeholder is always a viewer-owned COPY. The
/// slot composite is read once, synchronously on the UI thread while it is
/// still bound (so it cannot be disposed underneath the copy), and never bound
/// to the page Image itself. The copy is released only after the Image binding
/// has moved off it: when a final render or cache hit publishes, and in
/// ClearDisplay. A composite built at a different DPI or page size than the
/// current zoom is not used; the page is then only sized, which still gives
/// the overlay its real geometry for input.
/// </remarks>
internal sealed partial class SinglePageView
{
    private WriteableBitmap? _singlePagePlaceholder;

    internal long SinglePagePlaceholderShownCount { get; private set; }
    internal long SinglePagePlaceholderSizedOnlyCount { get; private set; }
    internal long SinglePagePlaceholderReleasedCount { get; private set; }

    /// <summary>The placeholder currently owned by the viewer (tests only).</summary>
    internal WriteableBitmap? SinglePagePlaceholderForTests => _singlePagePlaceholder;

    /// <summary>
    /// Show a placeholder for <paramref name="pageNumber"/> if the Image is
    /// empty or already showing a placeholder. Never replaces a real render.
    /// </summary>
    private void ShowSinglePagePlaceholder(int pageNumber, double widthPt, double heightPt, int logicalDpi)
    {
        if (PdfImage == null)
            return;
        if (PdfImage.Source != null && !ReferenceEquals(PdfImage.Source, _singlePagePlaceholder))
            return;

        // (a) Geometry first: the logical-DIP size the final render will have,
        // so the ZoomHost and overlay are laid out before any bitmap exists.
        var layout = SinglePageLayoutSize(widthPt, heightPt, logicalDpi);
        PdfImage.Width = layout.Width;
        PdfImage.Height = layout.Height;

        // (b) A copy of the continuous composite, if one matches this zoom.
        var copy = _preview.TryCopyCompositeForPage(pageNumber, widthPt, heightPt);
        var previous = _singlePagePlaceholder;
        if (copy == null)
        {
            SinglePagePlaceholderSizedOnlyCount++;
            if (previous != null)
            {
                PdfImage.Source = null;
                _singlePagePlaceholder = null;
                ReleasePlaceholderLater(previous);
            }
            Trace($"SinglePagePlaceholder page={pageNumber} sized-only");
            return;
        }

        _singlePagePlaceholder = copy;
        PdfImage.Source = copy;
        SinglePagePlaceholderShownCount++;
        if (previous != null)
            ReleasePlaceholderLater(previous);
        Trace($"SinglePagePlaceholder page={pageNumber} px={copy.PixelSize.Width}x{copy.PixelSize.Height}");
    }

    /// <summary>
    /// Release the placeholder once the Image binding has moved off it. Call
    /// after the final bitmap (or null) has been assigned to the Image.
    /// </summary>
    private void ReleaseSinglePagePlaceholder()
    {
        var placeholder = _singlePagePlaceholder;
        if (placeholder == null)
            return;
        _singlePagePlaceholder = null;
        ReleasePlaceholderLater(placeholder);
    }

    private void ReleasePlaceholderLater(WriteableBitmap placeholder)
    {
        SinglePagePlaceholderReleasedCount++;
        // Same deferral as the #1466 composite release: the renderer may still
        // hold the previous frame's reference until the binding change is drawn.
        Dispatcher.UIThread.Post(placeholder.Dispose, DispatcherPriority.Background);
    }
}
