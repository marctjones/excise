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
    /// </summary>
    public static WriteableBitmap? ToAvaloniaBitmap(SKBitmap? skBitmap)
    {
        if (skBitmap == null || skBitmap.Width <= 0 || skBitmap.Height <= 0)
            return null;

        // SkiaSharp on x64 produces Bgra8888 by default. If we ever encounter
        // a different format we copy through a converted SKBitmap rather than
        // letting Avalonia paint random bytes.
        SKBitmap source = skBitmap;
        SKBitmap? owned = null;
        try
        {
            if (source.ColorType != SKColorType.Bgra8888 ||
                source.AlphaType != SKAlphaType.Premul)
            {
                owned = new SKBitmap(source.Width, source.Height,
                    SKColorType.Bgra8888, SKAlphaType.Premul);
                if (!source.CopyTo(owned, SKColorType.Bgra8888))
                    return null;
                source = owned;
            }

            var size = new PixelSize(source.Width, source.Height);
            // 96 DPI matches Avalonia's default device-independent unit; the
            // viewer scales the image with a ScaleTransform regardless, so
            // baking a higher DPI into the bitmap metadata is purely cosmetic.
            var dpi = new Vector(96, 96);
            var wb = new WriteableBitmap(size, dpi,
                PixelFormat.Bgra8888, AlphaFormat.Premul);

            using (var locked = wb.Lock())
            {
                int srcRowBytes = source.RowBytes;
                int dstRowBytes = locked.RowBytes;
                IntPtr srcBase = source.GetPixels();
                IntPtr dstBase = locked.Address;
                int rowCopy = Math.Min(srcRowBytes, dstRowBytes);

                if (srcRowBytes == dstRowBytes)
                {
                    // Single contiguous copy when stride matches — the common
                    // case for power-of-two-friendly widths.
                    Buffer(srcBase, dstBase, srcRowBytes * source.Height);
                }
                else
                {
                    for (int y = 0; y < source.Height; y++)
                    {
                        Buffer(
                            srcBase + y * srcRowBytes,
                            dstBase + y * dstRowBytes,
                            rowCopy);
                    }
                }
            }

            return wb;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static unsafe void Buffer(IntPtr src, IntPtr dst, long bytes)
    {
        System.Buffer.MemoryCopy(src.ToPointer(), dst.ToPointer(), bytes, bytes);
    }
}
