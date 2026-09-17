using System;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace Excise.Avalonia.Imaging;

/// <summary>
/// Bridges SkiaSharp <see cref="SKBitmap"/> output into displayable Avalonia
/// bitmaps without an encode/decode round-trip.
/// </summary>
/// <remarks>
/// The previous code path encoded each rendered page to PNG and then decoded
/// the PNG back into an <see cref="Avalonia.Media.Imaging.Bitmap"/>. For a
/// US-Letter page rendered at 200 DPI that's 3.7 M pixels of zlib-compress
/// followed immediately by zlib-decompress — pure waste, and easily 150–
/// 300 ms per render on commodity hardware. Direct pixel copy via
/// <see cref="WriteableBitmap"/> is one to two orders of magnitude faster.
/// </remarks>
public static class SkiaInterop
{
    /// <summary>
    /// Convert an SKBitmap to a displayable Avalonia <see cref="WriteableBitmap"/>
    /// by copying pixels directly. The caller still owns the input SKBitmap.
    /// Returns null for an empty bitmap or one whose pixels cannot be read or
    /// converted to Bgra8888 premultiplied.
    /// </summary>
    /// <param name="bitmapDpi">
    /// The DPI to stamp on the <see cref="WriteableBitmap"/>. The default 96
    /// makes one bitmap pixel one device-independent pixel (the continuous-view
    /// path, which sizes tiles by explicit DIP dimensions, relies on this). The
    /// single-page path renders at the display's *device* resolution
    /// (logicalDpi × devicePixelRatio) and stamps <c>96 × devicePixelRatio</c>
    /// here, so the bitmap's DIP <see cref="Bitmap.Size"/> — and therefore the
    /// Image's layout size and all coordinate mapping — stay identical while the
    /// raster carries the extra pixels a HiDPI display actually has (#682).
    /// </param>
    public static WriteableBitmap? ToAvaloniaBitmap(SKBitmap? skBitmap, double bitmapDpi = 96.0)
    {
        if (skBitmap == null || skBitmap.Width <= 0 || skBitmap.Height <= 0)
            return null;

        var size = new PixelSize(skBitmap.Width, skBitmap.Height);
        // The stamped DPI decides how many bitmap pixels map to one DIP.
        // 96 (default) = 1:1. ⚠️ Avalonia's Image control mispaints bitmaps
        // stamped at any other DPI — it takes the dip-sized source rect in
        // PIXEL units, painting only a magnified top-left crop (#697;
        // DpiStampedBitmapPaintProbeTests). Callers that render at device
        // resolution must keep the 96 stamp and set the Image's
        // Width/Height to the logical dip size instead.
        var dpi = new Vector(bitmapDpi, bitmapDpi);
        var wb = new WriteableBitmap(size, dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        bool copied;
        using (var locked = wb.Lock())
            copied = CopyPixelsAsBgraPremul(skBitmap, locked.Address, locked.RowBytes);

        if (copied)
            return wb;
        wb.Dispose();
        return null;
    }

    /// <summary>
    /// Write <paramref name="source"/>'s pixels into a caller-owned Bgra8888
    /// premultiplied buffer of the same size (#1496).
    /// </summary>
    /// <remarks>
    /// The renderer produces Rgba8888, so this is a conversion on every page,
    /// tile and thumbnail. It used to go through <c>SKBitmap.CopyTo</c>, which
    /// SkiaSharp implements as a shader draw: Skia copies the whole (mutable)
    /// source into an SkImage for the shader, allocates a second target bitmap,
    /// and the caller had already allocated a third that <c>CopyTo</c> swapped
    /// away. Three transient page-sized mallocs per render; under full malloc
    /// stack logging <c>sk_bitmap_make_shader</c> was the largest allocator in
    /// the heap regions macOS kept dirty after a document closed. Removing them
    /// took the IRS 1040 instructions footprint after 30 page-downs from
    /// 737-740 MB to 678 MB (2026-09-17). It does not by itself return the
    /// memory freed at close; see LSEnvironment in scripts/build-macos-app.sh.
    /// <see cref="SKPixmap.ReadPixels(SKImageInfo, nint, int, int, int)"/>
    /// converts straight into the destination with no allocation; for
    /// premultiplied 8888 sources it is a channel swap, byte-identical to the
    /// old path (SkiaInteropConversionTests).
    /// </remarks>
    internal static bool CopyPixelsAsBgraPremul(SKBitmap source, IntPtr destination, int destinationRowBytes)
    {
        if (source.ColorType == SKColorType.Bgra8888 && source.AlphaType == SKAlphaType.Premul)
        {
            IntPtr srcBase = source.GetPixels();
            if (srcBase == IntPtr.Zero)
                return false;
            int srcRowBytes = source.RowBytes;
            if (srcRowBytes == destinationRowBytes)
            {
                // Single contiguous copy when stride matches — the common case.
                Buffer(srcBase, destination, (long)srcRowBytes * source.Height);
            }
            else
            {
                int rowCopy = Math.Min(srcRowBytes, destinationRowBytes);
                for (int y = 0; y < source.Height; y++)
                {
                    Buffer(
                        srcBase + y * srcRowBytes,
                        destination + y * destinationRowBytes,
                        rowCopy);
                }
            }
            return true;
        }

        using var pixmap = source.PeekPixels();
        if (pixmap == null)
            return false;
        var info = new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        return pixmap.ReadPixels(info, destination, destinationRowBytes, 0, 0);
    }

    private static unsafe void Buffer(IntPtr src, IntPtr dst, long bytes)
    {
        System.Buffer.MemoryCopy(src.ToPointer(), dst.ToPointer(), bytes, bytes);
    }
}
