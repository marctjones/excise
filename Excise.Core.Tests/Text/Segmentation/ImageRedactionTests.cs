using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// End-to-end tests for image-XObject redaction through
/// <see cref="PdfPageRedactionExtensions.RedactArea"/>. Builds minimal
/// PDFs with a single image XObject positioned via a <c>cm</c>, redacts
/// an area that does (or doesn't) overlap the image, and verifies the
/// <c>Do</c> operator is correctly removed or preserved.
/// </summary>
public class ImageRedactionTests
{
    [Fact]
    public void RedactArea_OverlappingImage_DoOperatorIsRemoved()
    {
        // Image placed at (100, 600)–(200, 700). Redact an area that
        // fully contains it.
        var pdfBytes = CreatePdfWithImageXObject(
            imageX: 100, imageY: 600, imageWidth: 100, imageHeight: 100);

        using var doc = PdfDocument.Open(pdfBytes);
        var page = doc.GetPage(1);

        var before = Encoding.Latin1.GetString(page.GetContentStreamBytes());
        before.Should().Contain("/Im0 Do", "sanity: image is invoked in content stream");

        page.RedactArea(new PdfRectangle(50, 550, 250, 750));

        var after = Encoding.Latin1.GetString(page.GetContentStreamBytes());
        after.Should().NotContain("/Im0 Do",
            "image Do must be stripped when its bbox overlaps the redaction area");
    }

    [Fact]
    public void RedactArea_ImageOutsideArea_DoOperatorSurvives()
    {
        // Image at top of page, redaction at bottom. Should not touch.
        var pdfBytes = CreatePdfWithImageXObject(
            imageX: 100, imageY: 700, imageWidth: 100, imageHeight: 50);

        using var doc = PdfDocument.Open(pdfBytes);
        var page = doc.GetPage(1);

        page.RedactArea(new PdfRectangle(0, 0, 100, 100));

        var after = Encoding.Latin1.GetString(page.GetContentStreamBytes());
        after.Should().Contain("/Im0 Do", "image must survive when it doesn't overlap redaction");
    }

    [Fact]
    public void RedactArea_AreaTouchesImageCorner_AnyOverlapRemoves()
    {
        // Image at (100, 600)–(200, 700). Redaction area just clips the
        // bottom-left corner — AnyOverlap (default) should still remove it.
        var pdfBytes = CreatePdfWithImageXObject(
            imageX: 100, imageY: 600, imageWidth: 100, imageHeight: 100);

        using var doc = PdfDocument.Open(pdfBytes);
        var page = doc.GetPage(1);

        page.RedactArea(new PdfRectangle(90, 590, 110, 610));  // 10pt overlap

        var after = Encoding.Latin1.GetString(page.GetContentStreamBytes());
        after.Should().NotContain("/Im0 Do");
    }

    [Fact]
    public void RedactArea_PartialOverlap_FullyContainedStrategyKeepsImage()
    {
        // Same image, partial overlap — FullyContained requires the
        // whole image inside the rect, so it must NOT be removed.
        var pdfBytes = CreatePdfWithImageXObject(
            imageX: 100, imageY: 600, imageWidth: 100, imageHeight: 100);

        using var doc = PdfDocument.Open(pdfBytes);
        var page = doc.GetPage(1);

        page.RedactArea(
            new PdfRectangle(90, 590, 150, 650),
            GlyphRemovalStrategy.FullyContained);

        var after = Encoding.Latin1.GetString(page.GetContentStreamBytes());
        after.Should().Contain("/Im0 Do",
            "FullyContained must not strip an image that's only partially covered");
    }

    /// <summary>
    /// Build a minimal one-page PDF with one Image XObject (a 1×1 pixel
    /// grayscale JPEG-ish DCTDecode stub) placed at the requested page
    /// coordinates via a <c>q … cm /Im0 Do … Q</c> sequence in the
    /// content stream. The image data bytes are minimal — the renderer
    /// won't actually decode them, but the parser and our redactor only
    /// need the XObject dictionary (Subtype=Image) and the Do op.
    /// </summary>
    private static byte[] CreatePdfWithImageXObject(
        double imageX, double imageY, double imageWidth, double imageHeight)
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\n"
        };

        w.WriteLine("%PDF-1.4");
        w.Flush();

        var offsets = new long[6];

        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj");
        w.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        w.WriteLine("endobj");
        w.Flush();

        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj");
        w.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        w.WriteLine("endobj");
        w.Flush();

        offsets[3] = ms.Position;
        w.WriteLine("3 0 obj");
        w.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    "/Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>");
        w.WriteLine("endobj");
        w.Flush();

        // q <w> 0 0 <h> <x> <y> cm /Im0 Do Q
        var contentBody =
            $"q\n{imageWidth} 0 0 {imageHeight} {imageX} {imageY} cm\n/Im0 Do\nQ";
        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length {contentBody.Length} >>");
        w.WriteLine("stream");
        w.Write(contentBody);
        w.WriteLine();
        w.WriteLine("endstream");
        w.WriteLine("endobj");
        w.Flush();

        // Image XObject — 2×2 raw grayscale pixels (4 bytes) so the
        // parser has well-formed stream data, even if nothing actually
        // renders it.
        var imageData = new byte[] { 0x00, 0x80, 0x80, 0xFF };
        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj");
        w.WriteLine("<< /Type /XObject /Subtype /Image /Width 2 /Height 2 " +
                    "/ColorSpace /DeviceGray /BitsPerComponent 8 " +
                    $"/Length {imageData.Length} >>");
        w.WriteLine("stream");
        w.Flush();
        ms.Write(imageData, 0, imageData.Length);
        w.WriteLine();
        w.WriteLine("endstream");
        w.WriteLine("endobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref");
        w.WriteLine("0 6");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine("trailer");
        w.WriteLine("<< /Root 1 0 R /Size 6 >>");
        w.WriteLine("startxref");
        w.WriteLine(xrefPos.ToString());
        w.WriteLine("%%EOF");
        w.Flush();

        return ms.ToArray();
    }
}
