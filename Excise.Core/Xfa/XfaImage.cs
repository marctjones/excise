using System.Buffers.Binary;
using System.Xml.Linq;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Primitives;

namespace Excise.Core.Xfa;

/// <summary>
/// The picture a draw's <c>&lt;value&gt;&lt;image&gt;</c> or an <c>imageEdit</c> field shows
/// (#1575). Excise.Core decodes no image format, so only a JPEG is drawn: it is embedded as
/// is (<c>/DCTDecode</c>). Other formats, and images named by a link that is not in the
/// document, are reported, never fetched.
/// </summary>
internal static class XfaImage
{
    /// <summary>Larger images are refused: a renderer would decode every pixel.</summary>
    internal const long MaxPixels = 50_000_000;

    /// <summary>
    /// The image bytes a leaf shows, or null (with a note when there was something to show).
    /// An <c>imageEdit</c> field's bound data wins over its template value; an <c>href</c>
    /// resolves only through the document's <c>/Names /XFAImages</c> tree, as in pdf.js.
    /// </summary>
    public static byte[]? Bytes(XfaLeaf leaf, PdfDocument document, XfaReport report)
    {
        if (leaf.WidgetKind == "imageEdit" && !string.IsNullOrWhiteSpace(leaf.Node.Value))
            return FromBase64(leaf.Node.Value, report);

        var image = leaf.Image;
        if (image == null)
            return null;

        if (image.Attr("href") is { Length: > 0 } href)
        {
            var linked = FromDocument(document, href);
            if (linked == null)
                report.Note("linked images outside the document not drawn");
            return linked;
        }

        if (string.IsNullOrWhiteSpace(image.Value))
            return null;
        if (image.AttrOr("transferEncoding", "base64") != "base64")
        {
            report.Note("images not in base64 not drawn");
            return null;
        }
        return FromBase64(image.Value, report);
    }

    private static byte[]? FromBase64(string text, XfaReport report)
    {
        try
        {
            return Convert.FromBase64String(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));
        }
        catch (FormatException)
        {
            report.Note("images whose data is not valid base64 not drawn");
            return null;
        }
    }

    private static byte[]? FromDocument(PdfDocument document, string href)
    {
        if (document.Resolve(document.Catalog.GetOptional("Names") ?? PdfNull.Instance) is not PdfDictionary names)
            return null;
        foreach (var (key, value) in PdfNameTree.Enumerate(document, names.GetOptional("XFAImages")))
        {
            if (document.Resolve(key) is PdfString name && name.Value == href
                && document.Resolve(value) is PdfStream stream)
            {
                try
                {
                    return stream.DecodedData;
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// A drawable image and its natural size in points, or null with a note. Only JPEG is
    /// drawn; the format of anything else is named in the note.
    /// </summary>
    public static (PdfImage Image, double W, double H)? Decode(byte[] bytes, XfaReport report)
    {
        var format = Sniff(bytes);
        if (format != "JPEG")
        {
            report.Note($"{format} images not drawn (only JPEG is)");
            return null;
        }

        PdfImage image;
        try
        {
            image = PdfImage.FromJpeg(bytes);
        }
        catch (ArgumentException)
        {
            report.Note("JPEG images in an unsupported coding not drawn");
            return null;
        }
        if ((long)image.Width * image.Height > MaxPixels)
        {
            report.Note("images over 50 megapixels not drawn");
            return null;
        }

        var (dpiX, dpiY) = JpegDensity(bytes);
        return (image, image.Width * 72 / dpiX, image.Height * 72 / dpiY);
    }

    private static string Sniff(byte[] b)
    {
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return "JPEG";
        if (b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G')
            return "PNG";
        if (b.Length > 2 && b[0] == (byte)'B' && b[1] == (byte)'M')
            return "BMP";
        if (b.Length > 4 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F')
            return "GIF";
        if (b.Length > 4 && ((b[0] == (byte)'I' && b[1] == (byte)'I' && b[2] == 42 && b[3] == 0)
            || (b[0] == (byte)'M' && b[1] == (byte)'M' && b[2] == 0 && b[3] == 42)))
            return "TIFF";
        return "unrecognised";
    }

    /// <summary>The JFIF density in dots per inch; 72 (one pixel per point) when absent.</summary>
    private static (double X, double Y) JpegDensity(byte[] jpeg)
    {
        // APP0 "JFIF\0": version(2) units(1) Xdensity(2) Ydensity(2), right after SOI.
        if (jpeg.Length >= 18 && jpeg[2] == 0xFF && jpeg[3] == 0xE0
            && jpeg[6] == (byte)'J' && jpeg[7] == (byte)'F' && jpeg[8] == (byte)'I' && jpeg[9] == (byte)'F' && jpeg[10] == 0)
        {
            int units = jpeg[13];
            double x = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(14));
            double y = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(16));
            double scale = units switch { 1 => 1, 2 => 2.54, _ => 0 };
            if (scale > 0 && x > 0 && y > 0)
                return (x * scale, y * scale);
        }
        return (72, 72);
    }

    /// <summary>
    /// Where an image of natural size <paramref name="w"/> x <paramref name="h"/> points sits
    /// in <paramref name="area"/> under XFA's <c>aspect</c>, anchored top-left as pdf.js
    /// anchors it: <c>fit</c> (the default) scales uniformly to fit, <c>width</c> and
    /// <c>height</c> scale uniformly to fill that dimension, <c>none</c> stretches to the
    /// area, and <c>actual</c> keeps the natural size.
    /// </summary>
    public static XfaRect Place(XfaRect area, double w, double h, string aspect)
    {
        if (w <= 0 || h <= 0)
            return area with { W = 0, H = 0 };
        double sx = area.W / w, sy = area.H / h;
        return aspect switch
        {
            "none" => area,
            "actual" => area with { W = w, H = h },
            "width" => area with { W = w * sx, H = h * sx },
            "height" => area with { W = w * sy, H = h * sy },
            _ => area with { W = w * Math.Min(sx, sy), H = h * Math.Min(sx, sy) },
        };
    }

    /// <summary>The <c>&lt;image&gt;</c> inside <paramref name="owner"/>'s <c>&lt;value&gt;</c>.</summary>
    public static XElement? ValueImage(XElement owner) => owner.Child("value")?.Child("image");
}
