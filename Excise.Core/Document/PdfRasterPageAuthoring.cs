using System;
using System.Globalization;
using System.Text;
using Excise.Core.Primitives;

namespace Excise.Core.Document;

/// <summary>Lossless page-image authoring used by the image-only redaction mode (#1186).</summary>
public static class PdfRasterPageAuthoring
{
    /// <summary>
    /// Add an RGB24 raster as the only visible content of a new page. The image
    /// is an indirect Image XObject; no annotation appearance or source-page
    /// carrier is retained.
    /// </summary>
    public static PdfPage AddRgbRasterPage(this PdfDocument document, byte[] rgb, int pixelWidth, int pixelHeight,
        double widthPoints, double heightPoints)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(rgb);
        if (pixelWidth <= 0 || pixelHeight <= 0 || widthPoints <= 0 || heightPoints <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        if (rgb.LongLength != (long)pixelWidth * pixelHeight * 3)
            throw new ArgumentException("RGB buffer must be tightly-packed RGB24 pixels.", nameof(rgb));

        var page = document.Pages.AddBlank(widthPoints, heightPoints);
        var image = new PdfStream(rgb);
        image.SetName("Type", "XObject"); image.SetName("Subtype", "Image");
        image.SetInt("Width", pixelWidth); image.SetInt("Height", pixelHeight);
        image.SetName("ColorSpace", "DeviceRGB"); image.SetInt("BitsPerComponent", 8);
        var xobjects = new PdfDictionary();
        xobjects["Im0"] = document.AddIndirectObject(image);
        var resources = new PdfDictionary(); resources["XObject"] = xobjects;
        page.Dictionary["Resources"] = resources;
        page.SetContentStreamBytes(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"q\n{widthPoints} 0 0 {heightPoints} 0 0 cm\n/Im0 Do\nQ\n")));
        return page;
    }
}
