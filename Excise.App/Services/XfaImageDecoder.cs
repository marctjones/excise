using System.IO;
using Excise.Core.Xfa;
using SkiaSharp;

namespace Excise.App.Services;

/// <summary>
/// Decodes XFA form images that are not JPEG (#1575) with SkiaSharp, which the app already
/// ships: Core takes raw pixels and never grows an image-format dependency. PNG, BMP (1-bit
/// included) and GIF decode; SkiaSharp has no TIFF codec, so TIFF stays reported.
/// </summary>
internal sealed class XfaImageDecoder : IXfaImageDecoder
{
    public static readonly XfaImageDecoder Instance = new();

    public (int Width, int Height)? ReadSize(byte[] bytes)
    {
        try
        {
            // SKBitmap.Decode(byte[]) throws on bytes no codec opens; SKCodec.Create returns null.
            using var codec = SKCodec.Create(new MemoryStream(bytes, writable: false));
            return codec == null ? null : (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    public XfaDecodedImage? Decode(byte[] bytes)
    {
        try
        {
            using var codec = SKCodec.Create(new MemoryStream(bytes, writable: false));
            if (codec == null)
                return null;
            using var bitmap = SKBitmap.Decode(codec);
            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
                return null;

            int width = bitmap.Width, height = bitmap.Height;
            var rgb = new byte[(long)width * height * 3];
            var alpha = new byte[(long)width * height];
            var anyTransparent = false;

            // SKBitmap.Pixels is straight (unpremultiplied) colour, as a PDF image with a
            // separate soft mask wants.
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
            return new XfaDecodedImage(width, height, rgb, anyTransparent ? alpha : null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // One undecodable image must not fail the whole form's layout.
            return null;
        }
    }
}
