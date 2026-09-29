using System;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using static Excise.Avalonia.Controls.PdfViewerControl;

namespace Excise.Avalonia.Controls;

/// <summary>
/// The continuous view as the single-page placeholder's source (#1473, #1842): the
/// composite a page already shows here, copied for the single-page view to show while
/// its sharp render runs.
/// </summary>
internal sealed partial class ContinuousPageView : IPagePreviewSource
{
    /// <summary>
    /// A copy of the composite this view shows for <paramref name="pageNumber"/>, if one
    /// matches the current zoom, for the single-page placeholder (#1473). Read once,
    /// synchronously on the UI thread while the composite is still bound, so it cannot be
    /// disposed underneath the copy; the copy is the caller's to release.
    /// </summary>
    public WriteableBitmap? TryCopyCompositeForPage(int pageNumber, double widthPt, double heightPt)
    {
        var slots = ContinuousSlots;
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
