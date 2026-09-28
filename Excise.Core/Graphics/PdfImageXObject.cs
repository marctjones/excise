using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Graphics;

/// <summary>
/// Image XObjects (ISO 32000-2 §8.9.5) written into a document: the one builder behind
/// <see cref="PdfGraphics.DrawImage"/>, image stamps and raster pages.
/// </summary>
internal static class PdfImageXObject
{
    /// <summary>
    /// Add a lossless DeviceRGB image from top-down RGB24 pixels, with an <c>/SMask</c> from
    /// <paramref name="alpha"/> (one byte per pixel, 0 transparent) unless every pixel is opaque.
    /// </summary>
    internal static PdfReference AddRgb(PdfDocument document, byte[] rgb, int width, int height, byte[]? alpha)
    {
        var image = NewImage(PdfStream.CreateCompressed(rgb), width, height, "DeviceRGB"); // #1549: lossless

        // A soft mask is its own DeviceGray image of the same size: the reader multiplies the
        // picture's opacity by it. Skipped when every pixel is opaque, which is the common case
        // for a photograph and saves a whole extra image.
        if (alpha != null && alpha.Any(a => a != 255))
            image["SMask"] = document.AddIndirectObject(NewImage(PdfStream.CreateCompressed(alpha), width, height, "DeviceGray"));
        return document.AddIndirectObject(image);
    }

    internal static PdfStream NewImage(PdfStream image, int width, int height, string colorSpace)
    {
        image.SetName("Type", "XObject");
        image.SetName("Subtype", "Image");
        image.SetInt("Width", width);
        image.SetInt("Height", height);
        image.SetName("ColorSpace", colorSpace);
        image.SetInt("BitsPerComponent", 8);
        return image;
    }
}
