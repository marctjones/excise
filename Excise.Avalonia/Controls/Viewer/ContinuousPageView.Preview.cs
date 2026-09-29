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
        if (!TryPlanCompositeCopy(pageNumber, widthPt, heightPt, out var plan))
            return null;

        var copy = new WriteableBitmap(plan.Size, new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        try
        {
            using var dst = copy.Lock();
            CopyComposite(dst, plan);
            return copy;
        }
        catch (ObjectDisposedException)
        {
            copy.Dispose();
            return null;
        }
    }

    /// <inheritdoc />
    public PixelSize? CompositeCopySize(int pageNumber, double widthPt, double heightPt) =>
        TryPlanCompositeCopy(pageNumber, widthPt, heightPt, out var plan) ? plan.Size : null;

    /// <summary>
    /// The copy <see cref="TryCopyCompositeForPage"/> makes, written into memory the caller
    /// owns (#1926) through the same fill and blit, so the pixels are identical. Same thread
    /// rule: synchronously on the UI thread while the composite is bound.
    /// </summary>
    public bool TryCopyCompositeInto(int pageNumber, double widthPt, double heightPt,
        IntPtr destination, int rowBytes, PixelSize destinationSize)
    {
        if (destination == IntPtr.Zero)
            throw new ArgumentException("The destination is null.", nameof(destination));
        if (destinationSize.Width < 0 || destinationSize.Height < 0)
            throw new ArgumentOutOfRangeException(nameof(destinationSize), destinationSize, "A size cannot be negative.");
        if (rowBytes < (long)destinationSize.Width * 4)
            throw new ArgumentOutOfRangeException(nameof(rowBytes), rowBytes, "A row of Bgra8888 needs 4 bytes per pixel.");

        if (!TryPlanCompositeCopy(pageNumber, widthPt, heightPt, out var plan)
            || destinationSize.Width < plan.Size.Width || destinationSize.Height < plan.Size.Height)
            return false;

        try
        {
            // The planned size, not the destination's: BlitCell clips to the framebuffer's
            // width, and a wider one would let it copy columns the bitmap copy never has.
            CopyComposite(new CallerFramebuffer(destination, plan.Size, rowBytes), plan);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>What one composite copy reads and how large it is.</summary>
    private readonly record struct CompositeCopyPlan(PdfPageSlot Slot, WriteableBitmap Source, double PxPerDip, PixelSize Size);

    /// <summary>
    /// Whether <paramref name="pageNumber"/>'s composite matches the current zoom and page
    /// geometry, and the copy's size if it does (at most <see cref="MaxSinglePagePreviewPixels"/>).
    /// </summary>
    private bool TryPlanCompositeCopy(int pageNumber, double widthPt, double heightPt, out CompositeCopyPlan plan)
    {
        plan = default;
        var slots = ContinuousSlots;
        if (slots == null || pageNumber < 1 || pageNumber > slots.Count || ZoomLevel <= 0)
            return false;
        var slot = slots[pageNumber - 1];
        var source = slot.Bitmap;
        if (source == null || slot.PageNumber != pageNumber)
            return false;

        int dpi = ContinuousRenderDpi;
        var key = slot.CompositeKey;
        double pageWidthDip = widthPt * 96.0 / 72.0 * ZoomLevel;
        double pageHeightDip = heightPt * 96.0 / 72.0 * ZoomLevel;
        if (key.Page != pageNumber || key.Dpi != dpi
            || key.PageWidthDip != (int)Math.Round(slot.DisplayWidth)
            || Math.Abs(slot.DisplayWidth - pageWidthDip) > 1
            || Math.Abs(slot.DisplayHeight - pageHeightDip) > 1)
            return false;

        double pxPerDip = dpi / (96.0 * ZoomLevel);
        int width = (int)Math.Ceiling(slot.DisplayWidth * pxPerDip);
        int height = (int)Math.Ceiling(slot.DisplayHeight * pxPerDip);
        if (width <= 0 || height <= 0 || (long)width * height > MaxSinglePagePreviewPixels)
            return false;

        plan = new CompositeCopyPlan(slot, source, pxPerDip, new PixelSize(width, height));
        return true;
    }

    /// <summary>White page, then the composite at its place on the page.</summary>
    private static void CopyComposite(ILockedFramebuffer dst, CompositeCopyPlan plan)
    {
        FillOpaqueWhite(dst);
        BlitCell(dst, plan.Source,
            (int)Math.Round(plan.Slot.TileDisplayX * plan.PxPerDip),
            (int)Math.Round(plan.Slot.TileDisplayY * plan.PxPerDip),
            plan.Source.PixelSize.Width, plan.Source.PixelSize.Height);
    }

    private static unsafe void FillOpaqueWhite(ILockedFramebuffer fb)
    {
        var bytes = (long)fb.RowBytes * fb.Size.Height;
        new Span<byte>((byte*)fb.Address, checked((int)bytes)).Fill(0xFF);
    }

    /// <summary>Caller-owned Bgra8888 memory presented as a framebuffer, for FillOpaqueWhite and BlitCell.</summary>
    private sealed class CallerFramebuffer(IntPtr address, PixelSize size, int rowBytes) : ILockedFramebuffer
    {
        public IntPtr Address { get; } = address;
        public PixelSize Size { get; } = size;
        public int RowBytes { get; } = rowBytes;
        public Vector Dpi => new(96, 96);
        public PixelFormat Format => PixelFormat.Bgra8888;
        public AlphaFormat AlphaFormat => AlphaFormat.Premul;
        public void Dispose() { }
    }
}
