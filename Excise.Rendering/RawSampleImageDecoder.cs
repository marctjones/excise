using Excise.Core.ColorSpaces;
using SkiaSharp;

namespace Excise.Rendering;

/// <summary>
/// Fast, context-free conversion for the common undecorated 8-bit Device
/// color spaces. Requests outside this exact contract return null so the one
/// general PDF sample path can handle Decode arrays, masks, and complex spaces.
/// </summary>
internal static class RawSampleImageDecoder
{
    private const int CmykLatticeSize = 17;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PdfColorSpace, float[]>
        CmykLattices = new();

    public static SKBitmap? TryDecodeFast(RawSampleImageDecodeRequest request)
    {
        if (request.BitsPerComponent != 8 ||
            request.ColorSpace == null ||
            request.HasDecodeArray ||
            request.Width <= 0 ||
            request.Height <= 0)
        {
            return null;
        }

        var expectedPixels = checked((long)request.Width * request.Height);
        var requiredBytes = expectedPixels * request.ComponentsPerPixel;
        if (requiredBytes > request.Samples.LongLength)
            return null;

        return request.ColorSpace.Type switch
        {
            PdfColorSpaceType.DeviceGray when request.ComponentsPerPixel == 1 =>
                CreateGrayBitmap(request.Samples, request.Width, request.Height),
            PdfColorSpaceType.DeviceRGB when request.ComponentsPerPixel == 3 =>
                CreateRgbBitmap(request.Samples, request.Width, request.Height),
            PdfColorSpaceType.DeviceCMYK when request.ComponentsPerPixel == 4 =>
                CreateCmykBitmap(request.Samples, request.Width, request.Height, request.ColorSpace),
            _ => null
        };
    }

    private static SKBitmap? CreateGrayBitmap(byte[] data, int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var pixels = SkiaBitmapPixelBuffer.GetWritableSpan(bitmap);
        if (pixels.IsEmpty)
        {
            bitmap.Dispose();
            return null;
        }

        var src = 0;
        var dst = 0;
        for (var i = 0; i < width * height; i++)
        {
            var gray = data[src++];
            pixels[dst++] = gray;
            pixels[dst++] = gray;
            pixels[dst++] = gray;
            pixels[dst++] = 255;
        }

        return bitmap;
    }

    private static SKBitmap? CreateRgbBitmap(byte[] data, int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var pixels = SkiaBitmapPixelBuffer.GetWritableSpan(bitmap);
        if (pixels.IsEmpty)
        {
            bitmap.Dispose();
            return null;
        }

        var src = 0;
        var dst = 0;
        for (var i = 0; i < width * height; i++)
        {
            pixels[dst++] = data[src++];
            pixels[dst++] = data[src++];
            pixels[dst++] = data[src++];
            pixels[dst++] = 255;
        }

        return bitmap;
    }

    private static SKBitmap? CreateCmykBitmap(
        byte[] data,
        int width,
        int height,
        PdfColorSpace colorSpace)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var pixels = SkiaBitmapPixelBuffer.GetWritableSpan(bitmap);
        if (pixels.IsEmpty)
        {
            bitmap.Dispose();
            return null;
        }

        // #915: sample the color space once into a 17^4 lattice rather than
        // running a locked/allocating ICC conversion for every image pixel.
        var lut = GetCmykLattice(colorSpace);
        var src = 0;
        var dst = 0;
        for (var i = 0; i < width * height; i++)
        {
            var (r, g, b) = LatticeToRgb(
                lut,
                data[src],
                data[src + 1],
                data[src + 2],
                data[src + 3]);
            src += 4;
            pixels[dst++] = r;
            pixels[dst++] = g;
            pixels[dst++] = b;
            pixels[dst++] = 255;
        }

        return bitmap;
    }

    private static float[] GetCmykLattice(PdfColorSpace colorSpace)
        => CmykLattices.GetValue(colorSpace, static cs =>
        {
            const int n = CmykLatticeSize;
            var lut = new float[n * n * n * n * 3];
            var cmyk = new double[4];
            var index = 0;
            for (var c = 0; c < n; c++)
            for (var m = 0; m < n; m++)
            for (var y = 0; y < n; y++)
            for (var k = 0; k < n; k++)
            {
                cmyk[0] = c / (double)(n - 1);
                cmyk[1] = m / (double)(n - 1);
                cmyk[2] = y / (double)(n - 1);
                cmyk[3] = k / (double)(n - 1);
                var (r, g, b) = cs.ToRgb(cmyk);
                lut[index++] = (float)r;
                lut[index++] = (float)g;
                lut[index++] = (float)b;
            }

            return lut;
        });

    private static (byte R, byte G, byte B) LatticeToRgb(
        float[] lut,
        byte cyan,
        byte magenta,
        byte yellow,
        byte black)
    {
        const int n = CmykLatticeSize;
        const double scale = (n - 1) / 255.0;

        var fc = cyan * scale; var ic = (int)fc; var tc = fc - ic; if (ic >= n - 1) { ic = n - 2; tc = 1; }
        var fm = magenta * scale; var im = (int)fm; var tm = fm - im; if (im >= n - 1) { im = n - 2; tm = 1; }
        var fy = yellow * scale; var iy = (int)fy; var ty = fy - iy; if (iy >= n - 1) { iy = n - 2; ty = 1; }
        var fk = black * scale; var ik = (int)fk; var tk = fk - ik; if (ik >= n - 1) { ik = n - 2; tk = 1; }

        double r = 0, g = 0, b = 0;
        for (var dc = 0; dc <= 1; dc++)
        for (var dm = 0; dm <= 1; dm++)
        for (var dy = 0; dy <= 1; dy++)
        for (var dk = 0; dk <= 1; dk++)
        {
            var weight = (dc == 0 ? 1 - tc : tc) * (dm == 0 ? 1 - tm : tm)
                         * (dy == 0 ? 1 - ty : ty) * (dk == 0 ? 1 - tk : tk);
            if (weight == 0)
                continue;

            var offset = ((((ic + dc) * n + (im + dm)) * n + (iy + dy)) * n + (ik + dk)) * 3;
            r += weight * lut[offset];
            g += weight * lut[offset + 1];
            b += weight * lut[offset + 2];
        }

        return (
            (byte)Math.Clamp(r * 255, 0, 255),
            (byte)Math.Clamp(g * 255, 0, 255),
            (byte)Math.Clamp(b * 255, 0, 255));
    }
}

internal readonly record struct RawSampleImageDecodeRequest(
    byte[] Samples,
    int Width,
    int Height,
    int BitsPerComponent,
    PdfColorSpace? ColorSpace,
    int ComponentsPerPixel,
    bool HasDecodeArray);

internal static class SkiaBitmapPixelBuffer
{
    public static unsafe Span<byte> GetWritableSpan(SKBitmap bitmap)
    {
        var pointer = bitmap.GetPixels();
        return pointer == IntPtr.Zero
            ? Span<byte>.Empty
            : new Span<byte>((void*)pointer, bitmap.RowBytes * bitmap.Height);
    }
}
