using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// #1821: an image inside a tiling pattern must be decoded for the size it is actually DRAWN at.
///
/// The decode target came from the PDF's own CTM alone, which knows nothing about the scale the
/// pattern matrix adds, so an image in a pattern was estimated about half its real size. That was
/// invisible while every JPEG fell back to a full-size decode; once JPEGs really decode reduced it
/// showed as a blurred logo. The reference is the SAME image drawn directly at the same device size.
/// </summary>
public sealed class ImageInTilingPatternResolutionTests
{
    private const int Source = 800;
    private const int PagePts = 200;

    private static byte[] StripedJpeg()
    {
        // 4 px dark / 4 px light vertical stripes: a 1/8 reduction averages them to grey,
        // a 1/4 reduction keeps them, so under-estimating the target is measurable.
        using var bitmap = new SKBitmap(Source, Source, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < Source; y++)
            for (var x = 0; x < Source; x++)
                bitmap.SetPixel(x, y, (x % 8) < 4 ? SKColors.Black : SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, 100).ToArray();
    }

    private static byte[] BuildPdf(string pageContent, string? patternStream, byte[] jpeg)
    {
        var resources = "/XObject << /Im0 5 0 R >>" + (patternStream != null ? " /Pattern << /P1 6 0 R >>" : "");
        var objects = new List<(string Head, byte[]? Stream)>
        {
            ("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n", null),
            ($"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {PagePts} {PagePts}] >>\nendobj\n", null),
            ($"3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << {resources} >> >>\nendobj\n", null),
            ($"4 0 obj\n<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream\nendobj\n", null),
            ($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {Source} /Height {Source} " +
             $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n", jpeg),
        };
        if (patternStream != null)
            objects.Add(("6 0 obj\n<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 " +
                         "/BBox [0 0 100 100] /XStep 100 /YStep 100 /Matrix [2 0 0 2 0 0] " +
                         "/Resources << /XObject << /Im0 5 0 R >> >> " +
                         $"/Length {patternStream.Length} >>\nstream\n{patternStream}\nendstream\nendobj\n", null));

        var bytes = new List<byte>(Encoding.ASCII.GetBytes("%PDF-1.7\n"));
        var offsets = new List<int>();
        foreach (var (head, stream) in objects)
        {
            offsets.Add(bytes.Count);
            bytes.AddRange(Encoding.ASCII.GetBytes(head));
            if (stream != null)
            {
                bytes.AddRange(stream);
                bytes.AddRange(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
            }
        }

        var xref = bytes.Count;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF");
        bytes.AddRange(Encoding.ASCII.GetBytes(sb.ToString()));
        return bytes.ToArray();
    }

    private static SKBitmap Render(byte[] pdf)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-patimg-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            using var doc = PdfDocument.Open(path);
            return new SkiaRenderer().RenderPage(doc.GetPage(1), new RenderOptions { Dpi = 72 });
        }
        finally { File.Delete(path); }
    }

    /// <summary>Contrast of the stripes: standard deviation of luminance over the page.</summary>
    private static double Contrast(SKBitmap bitmap)
    {
        var pixels = bitmap.Pixels;
        var lum = pixels.Select(p => (0.299 * p.Red) + (0.587 * p.Green) + (0.114 * p.Blue)).ToArray();
        var mean = lum.Average();
        return Math.Sqrt(lum.Sum(v => (v - mean) * (v - mean)) / lum.Length);
    }

    [Fact]
    public void ImageInsideATilingPattern_KeepsTheDetailOfTheSameImageDrawnDirectly()
    {
        var jpeg = StripedJpeg();
        using var direct = Render(BuildPdf($"q {PagePts} 0 0 {PagePts} 0 0 cm /Im0 Do Q", null, jpeg));
        // 100 pattern units x the matrix's 2 = the same 200 device pixels as the direct draw.
        using var patterned = Render(BuildPdf(
            $"/Pattern cs /P1 scn 0 0 {PagePts} {PagePts} re f", "q 100 0 0 100 0 0 cm /Im0 Do Q", jpeg));

        var directContrast = Contrast(direct);
        var patternContrast = Contrast(patterned);

        directContrast.Should().BeGreaterThan(60,
            "the reference render must actually show the stripes, or this test proves nothing");
        patternContrast.Should().BeGreaterThan(directContrast * 0.85,
            "an image in a tiling pattern must be decoded for its real device size; estimated from the " +
            "CTM alone it came out half size and its stripes averaged away");
    }
}
