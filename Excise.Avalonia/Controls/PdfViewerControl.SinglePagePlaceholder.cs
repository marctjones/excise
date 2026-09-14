using System;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

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
public partial class PdfViewerControl
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
        if (_pdfImage == null)
            return;
        if (_pdfImage.Source != null && !ReferenceEquals(_pdfImage.Source, _singlePagePlaceholder))
            return;

        // (a) Geometry first: the logical-DIP size the final render will have,
        // so the ZoomHost and overlay are laid out before any bitmap exists.
        _pdfImage.Width = widthPt * logicalDpi / 72.0;
        _pdfImage.Height = heightPt * logicalDpi / 72.0;

        // (b) A copy of the continuous composite, if one matches this zoom.
        var copy = TryCopyContinuousCompositeForPage(pageNumber, widthPt, heightPt);
        var previous = _singlePagePlaceholder;
        if (copy == null)
        {
            SinglePagePlaceholderSizedOnlyCount++;
            if (previous != null)
            {
                _pdfImage.Source = null;
                _singlePagePlaceholder = null;
                ReleasePlaceholderLater(previous);
            }
            Trace($"SinglePagePlaceholder page={pageNumber} sized-only");
            return;
        }

        _singlePagePlaceholder = copy;
        _pdfImage.Source = copy;
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

    private WriteableBitmap? TryCopyContinuousCompositeForPage(int pageNumber, double widthPt, double heightPt)
    {
        var slots = _continuousSlots;
        if (slots == null || pageNumber < 1 || pageNumber > slots.Count || ZoomLevel <= 0)
            return null;
        var slot = slots[pageNumber - 1];
        var source = slot.Bitmap;
        if (source == null || slot.PageNumber != pageNumber)
            return null;

        int dpi = ContinuousRenderDpi;
        var key = slot.CompositeKey;
        double pageWidthDip = widthPt * 96.0 / 72.0 * ZoomLevel;
        double pageHeightDip = heightPt * 96.0 / 72.0 * ZoomLevel;
        if (key.Page != pageNumber || key.Dpi != dpi
            || key.PageWidthDip != (int)Math.Round(slot.DisplayWidth)
            || Math.Abs(slot.DisplayWidth - pageWidthDip) > 1
            || Math.Abs(slot.DisplayHeight - pageHeightDip) > 1)
            return null;

        double pxPerDip = dpi / (96.0 * ZoomLevel);
        int width = (int)Math.Ceiling(slot.DisplayWidth * pxPerDip);
        int height = (int)Math.Ceiling(slot.DisplayHeight * pxPerDip);
        if (width <= 0 || height <= 0 || (long)width * height > MaxSinglePagePreviewPixels)
            return null;

        var copy = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        try
        {
            using var dst = copy.Lock();
            FillOpaqueWhite(dst);
            BlitCell(dst, source,
                (int)Math.Round(slot.TileDisplayX * pxPerDip),
                (int)Math.Round(slot.TileDisplayY * pxPerDip),
                source.PixelSize.Width, source.PixelSize.Height);
            return copy;
        }
        catch (ObjectDisposedException)
        {
            copy.Dispose();
            return null;
        }
    }

    private static unsafe void FillOpaqueWhite(ILockedFramebuffer fb)
    {
        var bytes = (long)fb.RowBytes * fb.Size.Height;
        new Span<byte>((byte*)fb.Address, checked((int)bytes)).Fill(0xFF);
    }
}
