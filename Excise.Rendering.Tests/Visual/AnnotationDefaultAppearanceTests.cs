using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Visual;

public class AnnotationDefaultAppearanceTests
{
    [Fact]
    public void RenderPage_HighlightAnnotationWithoutAppearance_DrawsMarkup()
    {
        var pdf = CreatePdfWithAnnotation(
            "<< /Type /Annot /Subtype /Highlight /Rect [40 90 160 110] " +
            "/QuadPoints [40 110 160 110 40 90 160 90] /C [1 1 0] /CA 1 >>");

        using var doc = PdfDocument.Open(pdf);
        doc.GetPage(1).GetAnnotations()
            .Should().ContainSingle(a => a.Subtype == PdfAnnotationSubtype.Highlight);
        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, AntiAlias = false });

        CountYellowishPixels(bitmap).Should().BeGreaterThan(50,
            "a no-/AP highlight annotation should synthesize a visible default appearance");
        CountStrongYellowPixels(bitmap).Should().BeGreaterThan(50,
            "highlight opacity should honor /CA rather than always using a pale fallback alpha");
    }

    /// <summary>
    /// /Border [0 0 112] on a 150x20 annotation: the width is more than half
    /// the rect's smaller side, so it is refused (row
    /// link.width-exceeds-half-the-rect in tests/annotation-synthesis-policy.json).
    ///
    /// The test name and its old reason — "a no-/AP link annotation is an
    /// interactive hit region, not page content to paint into the bitmap" — no
    /// longer describe why it passes: since #987 a link with NO /Border and no
    /// /BS does get the §12.5.6.5 default 1 pt border, because poppler and
    /// Ghostscript both stroke one. What this fixture pins is the absurd-width
    /// rung, which is unchanged.
    /// </summary>
    [Fact]
    public void RenderPage_LinkAnnotationWithABorderWiderThanItsRect_DrawsNothing()
    {
        var pdf = CreatePdfWithAnnotation(
            "<< /Type /Annot /Subtype /Link /Rect [5 25 155 45] " +
            "/Border [0 0 112] /C [0 0 1] /A << /S /URI /URI (http://www.example.org) >> >>");

        using var doc = PdfDocument.Open(pdf);
        doc.GetPage(1).GetAnnotations()
            .Should().ContainSingle(a => a.Subtype == PdfAnnotationSubtype.Link);
        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, AntiAlias = false, BackgroundColor = SKColors.White });

        CountStrongBluePixels(bitmap).Should().Be(0,
            "a 112 pt border on a 20 pt-tall annotation is not an outline, it is a slab over " +
            "the page; the two engines that draw it do not agree on its geometry");
    }

    private static int CountYellowishPixels(SKBitmap bitmap)
    {
        var count = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red > 180 && pixel.Green > 180 && pixel.Blue < 240)
                    count++;
            }
        }
        return count;
    }

    private static int CountStrongYellowPixels(SKBitmap bitmap)
    {
        var count = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red > 240 && pixel.Green > 240 && pixel.Blue < 40)
                    count++;
            }
        }
        return count;
    }

    private static int CountStrongBluePixels(SKBitmap bitmap)
    {
        var count = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Blue > 200 && pixel.Red < 80 && pixel.Green < 80)
                    count++;
            }
        }
        return count;
    }

    private static byte[] CreatePdfWithAnnotation(string annotation)
    {
        var content = "";
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");
        var o1 = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");
        var o2 = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");
        var o3 = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Annots [5 0 R] >>");
        sb.AppendLine("endobj");
        var o4 = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine($"<< /Length {content.Length} >>");
        sb.AppendLine("stream");
        sb.AppendLine(content);
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");
        var o5 = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine(annotation);
        sb.AppendLine("endobj");
        var xref = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{o1:D10} 00000 n ");
        sb.AppendLine($"{o2:D10} 00000 n ");
        sb.AppendLine($"{o3:D10} 00000 n ");
        sb.AppendLine($"{o4:D10} 00000 n ");
        sb.AppendLine($"{o5:D10} 00000 n ");
        sb.AppendLine("trailer << /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xref.ToString());
        sb.AppendLine("%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
