using SkiaSharp;

namespace Excise.App.Services;

/// <summary>Separates a decoded bitmap's straight colour and opacity for PDF image storage (#1968).</summary>
internal static class PdfImagePixelConverter
{
    // Callers validate dimensions, own the bitmap, and retain their own codec/failure/size policies.
    internal static (byte[] Rgb, byte[]? Alpha) Extract(SKBitmap bitmap)
    {
        var rgb = new byte[(long)bitmap.Width * bitmap.Height * 3];
        var alpha = new byte[(long)bitmap.Width * bitmap.Height];
        var anyTransparent = false;

        // Retain Skia's unpremultiplication and format conversion, not raw native channel bytes.
        var pixels = bitmap.Pixels;
        var r = 0;
        for (var i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            rgb[r++] = c.Red;
            rgb[r++] = c.Green;
            rgb[r++] = c.Blue;
            alpha[i] = c.Alpha;
            if (c.Alpha != 255) anyTransparent = true;
        }
        return (rgb, anyTransparent ? alpha : null);
    }
}
