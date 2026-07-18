using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Visual;

public class OptionalContentRenderingTests
{
    [Fact]
    public void RenderPage_OffOptionalContentGroup_SuppressesPaintByObjectReference()
    {
        var pdf = CreatePdfWithDuplicateNamedOptionalContentGroups();

        using var doc = PdfDocument.Open(pdf);
        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, AntiAlias = false });

        CountBlackPixels(bitmap).Should().BeGreaterThan(500,
            "the visible OCG should still paint even when another OCG has the same display name");
        CountGreenPixels(bitmap).Should().BeLessThan(20,
            "the OCG listed in /OCProperties /D /OFF should not paint");
    }

    [Fact]
    public void RenderPage_OffOptionalContentGroupOnFormXObject_SuppressesXObjectPaint()
    {
        var pdf = CreatePdfWithOptionalContentFormXObjects();

        using var doc = PdfDocument.Open(pdf);
        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, AntiAlias = false });

        CountBlackPixels(bitmap).Should().BeGreaterThan(500,
            "the visible form XObject should still paint");
        CountGreenPixels(bitmap).Should().BeLessThan(20,
            "a form XObject with /OC pointing to an off OCG should not paint");
    }

    private static int CountBlackPixels(SKBitmap bitmap)
    {
        var count = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 30 && pixel.Green < 30 && pixel.Blue < 30)
                    count++;
            }
        }
        return count;
    }

    private static int CountGreenPixels(SKBitmap bitmap)
    {
        var count = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 40 && pixel.Green > 180 && pixel.Blue < 40)
                    count++;
            }
        }
        return count;
    }

    private static byte[] CreatePdfWithDuplicateNamedOptionalContentGroups()
    {
        var content = string.Join('\n',
            "q",
            "/OC /Visible BDC",
            "0 0 0 rg",
            "10 10 30 30 re f",
            "EMC",
            "/OC /Hidden BDC",
            "0 1 0 rg",
            "50 10 30 30 re f",
            "EMC",
            "Q",
            "");

        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        var o1 = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R");
        sb.AppendLine("   /OCProperties <<");
        sb.AppendLine("     /OCGs [5 0 R 6 0 R]");
        sb.AppendLine("     /D << /ON [5 0 R] /OFF [6 0 R] >>");
        sb.AppendLine("   >>");
        sb.AppendLine(">>");
        sb.AppendLine("endobj");

        var o2 = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        var o3 = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100]");
        sb.AppendLine("   /Resources << /Properties << /Visible 5 0 R /Hidden 6 0 R >> >>");
        sb.AppendLine("   /Contents 4 0 R");
        sb.AppendLine(">>");
        sb.AppendLine("endobj");

        var o4 = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine($"<< /Length {Encoding.Latin1.GetByteCount(content)} >>");
        sb.AppendLine("stream");
        sb.Append(content);
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        var o5 = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /Type /OCG /Name (Layer) >>");
        sb.AppendLine("endobj");

        var o6 = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine("<< /Type /OCG /Name (Layer) >>");
        sb.AppendLine("endobj");

        var xref = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 7");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{o1:D10} 00000 n ");
        sb.AppendLine($"{o2:D10} 00000 n ");
        sb.AppendLine($"{o3:D10} 00000 n ");
        sb.AppendLine($"{o4:D10} 00000 n ");
        sb.AppendLine($"{o5:D10} 00000 n ");
        sb.AppendLine($"{o6:D10} 00000 n ");
        sb.AppendLine("trailer << /Size 7 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xref.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] CreatePdfWithOptionalContentFormXObjects()
    {
        var pageContent = string.Join('\n',
            "q 1 0 0 1 10 10 cm /VisibleForm Do Q",
            "q 1 0 0 1 50 10 cm /HiddenForm Do Q",
            "");
        var visibleFormContent = "0 0 0 rg 0 0 30 30 re f\n";
        var hiddenFormContent = "0 1 0 rg 0 0 30 30 re f\n";

        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        var o1 = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R");
        sb.AppendLine("   /OCProperties <<");
        sb.AppendLine("     /OCGs [5 0 R 6 0 R]");
        sb.AppendLine("     /D << /ON [5 0 R] /OFF [6 0 R] >>");
        sb.AppendLine("   >>");
        sb.AppendLine(">>");
        sb.AppendLine("endobj");

        var o2 = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        var o3 = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100]");
        sb.AppendLine("   /Resources << /XObject << /VisibleForm 7 0 R /HiddenForm 8 0 R >> >>");
        sb.AppendLine("   /Contents 4 0 R");
        sb.AppendLine(">>");
        sb.AppendLine("endobj");

        var o4 = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine($"<< /Length {Encoding.Latin1.GetByteCount(pageContent)} >>");
        sb.AppendLine("stream");
        sb.Append(pageContent);
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        var o5 = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /Type /OCG /Name (Layer) >>");
        sb.AppendLine("endobj");

        var o6 = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine("<< /Type /OCG /Name (Layer) >>");
        sb.AppendLine("endobj");

        var o7 = sb.Length;
        sb.AppendLine("7 0 obj");
        sb.AppendLine($"<< /Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 30 30] /OC 5 0 R /Length {Encoding.Latin1.GetByteCount(visibleFormContent)} >>");
        sb.AppendLine("stream");
        sb.Append(visibleFormContent);
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        var o8 = sb.Length;
        sb.AppendLine("8 0 obj");
        sb.AppendLine($"<< /Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 30 30] /OC 6 0 R /Length {Encoding.Latin1.GetByteCount(hiddenFormContent)} >>");
        sb.AppendLine("stream");
        sb.Append(hiddenFormContent);
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        var xref = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 9");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{o1:D10} 00000 n ");
        sb.AppendLine($"{o2:D10} 00000 n ");
        sb.AppendLine($"{o3:D10} 00000 n ");
        sb.AppendLine($"{o4:D10} 00000 n ");
        sb.AppendLine($"{o5:D10} 00000 n ");
        sb.AppendLine($"{o6:D10} 00000 n ");
        sb.AppendLine($"{o7:D10} 00000 n ");
        sb.AppendLine($"{o8:D10} 00000 n ");
        sb.AppendLine("trailer << /Size 9 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xref.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
