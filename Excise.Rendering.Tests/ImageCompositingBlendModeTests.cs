using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// image-compositing:BlendTransparency (test-pdfs/manifests/pdf-spec-registry/
/// sections/image-requirements.json) -- an image XObject painted under a
/// non-Normal /BM blend mode must actually composite by that mode's formula,
/// not fall back to simple over-compositing the way a renderer that only
/// wires blend modes into path fills/strokes could. SkiaRenderer.Images.cs
/// sets <c>imagePaint.BlendMode = _state.BlendMode</c> when drawing an image
/// Do, so this is a real, distinct code path, not a decorative dictionary key.
/// </summary>
public class ImageCompositingBlendModeTests
{
    private const int PageSize = 40;

    /// <summary>
    /// A solid green image drawn over a solid red backdrop: Normal compositing
    /// would show plain green (opaque source wins); Multiply must show black
    /// (255*0=0 in every channel), which only happens if the image draw itself
    /// consults the current blend mode.
    /// </summary>
    [Fact]
    public void GreenImage_MultiplyBlendOverRedBackdrop_CompositesToBlackNotPlainGreen()
    {
        var path = WriteTemp(BlendImagePdf("Multiply"));
        try
        {
            using var doc = PdfDocument.Open(path);
            using var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1),
                new RenderOptions { Dpi = 72, AntiAlias = false, BackgroundColor = SKColors.White });

            var c = bmp.GetPixel(PageSize / 2, PageSize / 2);
            c.Red.Should().BeLessThan(40, "Multiply(red=255, green=0) = 0 in the red channel");
            c.Green.Should().BeLessThan(40, "Multiply(red=0, green=255) = 0 in the green channel");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GreenImage_NormalBlendOverRedBackdrop_CompositesToPlainGreen()
    {
        var path = WriteTemp(BlendImagePdf("Normal"));
        try
        {
            using var doc = PdfDocument.Open(path);
            using var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1),
                new RenderOptions { Dpi = 72, AntiAlias = false, BackgroundColor = SKColors.White });

            var c = bmp.GetPixel(PageSize / 2, PageSize / 2);
            c.Red.Should().BeLessThan(40);
            c.Green.Should().BeGreaterThan(200, "an opaque image under Normal blending simply wins over the backdrop");
        }
        finally { File.Delete(path); }
    }

    /// <summary>Preserve: the /BM key on the image's ExtGState survives a save+reload.</summary>
    [Fact]
    public void ExtGStateBlendMode_SurvivesASaveAndReload_Unchanged()
    {
        var path = WriteTemp(BlendImagePdf("Multiply"));
        try
        {
            using var doc = PdfDocument.Open(path);
            var reopened = PdfDocument.Open(doc.SaveToBytes());
            using var _ = reopened;

            var gs = reopened.GetPage(1).GetExtGState("GS1");
            gs.Should().NotBeNull();
            gs!.GetName("BM").Should().Be("Multiply");
        }
        finally { File.Delete(path); }
    }

    private static byte[] BlendImagePdf(string blendMode)
    {
        var content = "1 0 0 rg 0 0 " + PageSize + " " + PageSize +
                      " re f /GS1 gs q " + PageSize + " 0 0 " + PageSize + " 0 0 cm /Im0 Do Q";
        var imageData = new byte[] { 0, 255, 0 }; // solid green, 1x1 RGB
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {PageSize} {PageSize}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R " +
            "/Resources << /ExtGState << /GS1 5 0 R >> /XObject << /Im0 6 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            $"5 0 obj\n<< /Type /ExtGState /BM /{blendMode} >>\nendobj\n",
            $"6 0 obj\n<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB " +
            $"/BitsPerComponent 8 /Length {imageData.Length} >>\nstream\n",
        };

        var sb = new StringBuilder();
        var offsets = new List<int>();
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("%PDF-1.7\n"));
        foreach (var o in objects)
        {
            offsets.Add(bytes.Count);
            bytes.AddRange(Encoding.ASCII.GetBytes(o));
            if (o.Contains("/Subtype /Image"))
            {
                bytes.AddRange(imageData);
                bytes.AddRange(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
            }
        }
        int xref = bytes.Count;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        bytes.AddRange(Encoding.ASCII.GetBytes(sb.ToString()));
        return bytes.ToArray();
    }

    private static string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-imgblend-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        return p;
    }
}
