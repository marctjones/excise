using Excise.Core.Document;

namespace Excise.App.Services;

/// <summary>An image ready for <c>AddImageStampAnnotation</c>: RGB plus an optional 8-bit opacity.</summary>
/// <param name="Rgb">Top-down, row-major RGB24, exactly width * height * 3 bytes.</param>
/// <param name="Alpha">Top-down, row-major opacity (0 transparent), width * height bytes; null when every pixel is opaque.</param>
internal sealed record DecodedStampImage(byte[] Rgb, byte[]? Alpha, int Width, int Height)
{
    public bool HasTransparency => Alpha != null;
}

/// <summary>
/// Decodes an image file for a stamp (a signature above all) and prepares it for placement. Core takes
/// raw pixels and never grows an image-format dependency, so SkiaSharp, which the app already ships for
/// rendering, decodes here at the UI boundary.
/// </summary>
internal static class ImageStampDecoder
{
    /// <summary>Largest image accepted, in pixels. A signature is a few hundred pixels across.</summary>
    internal const long MaxPixels = 40_000_000;

    /// <summary>
    /// Decode <paramref name="path"/> keeping its alpha channel. False when it is not an image the
    /// viewer can decode, or is implausibly large.
    /// </summary>
    public static bool TryDecode(string path, out DecodedStampImage image)
    {
        image = new DecodedStampImage(Array.Empty<byte>(), null, 0, 0);
        using var bitmap = SkiaSharp.SKBitmap.Decode(path);
        if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
            return false;
        if ((long)bitmap.Width * bitmap.Height > MaxPixels)
            return false;

        int width = bitmap.Width, height = bitmap.Height;
        var rgb = new byte[(long)width * height * 3];
        var alpha = new byte[(long)width * height];
        var anyTransparent = false;

        // SKBitmap.Pixels returns straight (unpremultiplied) colours, which is what a PDF
        // image with a separate soft mask wants: colour and opacity are stored apart.
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

        image = new DecodedStampImage(rgb, anyTransparent ? alpha : null, width, height);
        return true;
    }

    /// <summary>
    /// True for an opaque image whose four corners are near-white: a scan or photo of a signature on
    /// paper, where making the white transparent is what the user wants.
    /// </summary>
    public static bool LooksLikeInkOnWhite(DecodedStampImage image)
    {
        if (image.HasTransparency || image.Width < 4 || image.Height < 4) return false;
        bool NearWhite(int x, int y)
        {
            var i = ((long)y * image.Width + x) * 3;
            return image.Rgb[i] >= 225 && image.Rgb[i + 1] >= 225 && image.Rgb[i + 2] >= 225;
        }
        return NearWhite(0, 0) && NearWhite(image.Width - 1, 0)
            && NearWhite(0, image.Height - 1) && NearWhite(image.Width - 1, image.Height - 1);
    }

    /// <summary>
    /// Turn light paper into transparency, keeping the ink. Each pixel is treated as ink laid over white:
    /// opacity is how far it is from white, and the ink colour is recovered by un-blending it from white,
    /// so a soft pen edge stays soft and a dark-blue ballpoint stays blue. Pixels within
    /// <paramref name="paperCutoff"/> of white are dropped entirely, which removes scanner noise.
    /// </summary>
    public static DecodedStampImage RemoveLightBackground(DecodedStampImage image, int paperCutoff = 28)
    {
        // For an opaque image (see LooksLikeInkOnWhite); any existing opacity is replaced.
        var count = (long)image.Width * image.Height;
        var rgb = new byte[count * 3];
        var alpha = new byte[count];
        var cutoff = Math.Clamp(paperCutoff, 0, 200);
        var span = 255.0 - cutoff;

        for (long i = 0; i < count; i++)
        {
            double r = image.Rgb[i * 3], g = image.Rgb[i * 3 + 1], b = image.Rgb[i * 3 + 2];
            var darkest = Math.Min(r, Math.Min(g, b));
            var a = Math.Clamp((255.0 - darkest - cutoff) / span, 0, 1);
            if (a <= 0)
            {
                alpha[i] = 0;               // colour is irrelevant where nothing shows
                continue;
            }

            // Un-blend from white: p = a*ink + (1-a)*255  =>  ink = (p - (1-a)*255) / a
            rgb[i * 3] = ToByte((r - (1 - a) * 255) / a);
            rgb[i * 3 + 1] = ToByte((g - (1 - a) * 255) / a);
            rgb[i * 3 + 2] = ToByte((b - (1 - a) * 255) / a);
            alpha[i] = ToByte(a * 255);
        }

        return new DecodedStampImage(rgb, alpha, image.Width, image.Height);
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    /// <summary>
    /// The largest rectangle of the image's own proportions that fits inside <paramref name="box"/>,
    /// centred in it. A signature dragged into a wide, short box is not squashed to fill it.
    /// </summary>
    public static PdfRectangle FitInside(PdfRectangle box, int imageWidth, int imageHeight)
    {
        var b = box.Normalize();
        if (imageWidth <= 0 || imageHeight <= 0 || b.Width <= 0 || b.Height <= 0)
            return b;

        var scale = Math.Min(b.Width / imageWidth, b.Height / imageHeight);
        var w = imageWidth * scale;
        var h = imageHeight * scale;
        var left = b.Left + (b.Width - w) / 2;
        var bottom = b.Bottom + (b.Height - h) / 2;
        return new PdfRectangle(left, bottom, left + w, bottom + h);
    }
}
