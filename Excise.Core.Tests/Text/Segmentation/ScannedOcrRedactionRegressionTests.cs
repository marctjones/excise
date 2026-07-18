using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// Adversarial scanned/OCR redaction coverage for #585.
/// </summary>
public sealed class ScannedOcrRedactionRegressionTests
{
    [Fact]
    public void RedactText_HiddenOcrLayerUnderRaster_RemovesTextAndRasterBytesFromSavedPdf()
    {
        const string ocrSecret = "OCRLAYERSECRET";
        const string rasterMarker = "RASTER_MARKER_585";
        var pdf = BuildPdfWithImageXObject(
            content:
                $"BT /F1 20 Tf 100 700 Td ({ocrSecret}) Tj ET\n" +
                "q 260 0 0 80 80 660 cm /Im0 Do Q\n",
            rasterMarker,
            includeFont: true);

        Encoding.Latin1.GetString(pdf).Should().Contain(ocrSecret).And.Contain(rasterMarker);

        using var doc = PdfDocument.Open(pdf);
        HiddenTextDetector.ScanPage(doc.GetPage(1)).Should().ContainSingle(r =>
            r.Text == ocrSecret && r.HiddenBy == "image /Im0");

        doc.RedactText(ocrSecret, drawBlackRect: false).Should().Be(1);

        var saved = doc.SaveToBytes();
        Encoding.Latin1.GetString(saved).Should()
            .NotContain(ocrSecret)
            .And.NotContain(rasterMarker,
                "a scanned-page image overlapping the targeted OCR text must not remain reachable after save");

        using var reopened = PdfDocument.Open(saved);
        string.Concat(reopened.GetPage(1).Letters.Select(l => l.Value))
            .Should().NotContain(ocrSecret);
        HiddenTextDetector.ScanPage(reopened.GetPage(1)).Should().BeEmpty();
    }

    [Fact]
    public void RedactArea_ImageOnlySensitiveRegion_RemovesRasterObjectBytesFromSavedPdf()
    {
        const string rasterMarker = "SCANNEDIMAGESECRET";
        var pdf = BuildPdfWithImageXObject(
            content: "q 160 0 0 80 100 640 cm /Im0 Do Q\n",
            rasterMarker,
            includeFont: false);

        Encoding.Latin1.GetString(pdf).Should().Contain(rasterMarker);

        using var doc = PdfDocument.Open(pdf);
        doc.GetPage(1).RedactArea(new PdfRectangle(110, 650, 150, 680));

        var saved = doc.SaveToBytes();
        Encoding.Latin1.GetString(saved).Should().NotContain(rasterMarker,
            "image-only sensitive pixels must be redacted at object/reachability level, not only by dropping the Do operator");

        using var reopened = PdfDocument.Open(saved);
        reopened.GetPage(1).GetContentStream().Operators
            .Should().NotContain(op => op.Name == "Do" && op.GetName(0) == "Im0");
    }

    private static byte[] BuildPdfWithImageXObject(string content, string imageMarker, bool includeFont)
    {
        var contentBytes = Encoding.Latin1.GetBytes(content);
        var imageBytes = Encoding.Latin1.GetBytes(imageMarker);
        var objectCount = includeFont ? 6 : 5;

        using var ms = new MemoryStream();
        void Write(string value)
        {
            var bytes = Encoding.Latin1.GetBytes(value);
            ms.Write(bytes, 0, bytes.Length);
        }

        Write("%PDF-1.7\n");
        var offsets = new long[objectCount + 1];

        offsets[1] = ms.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = ms.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        var fontResources = includeFont ? " /Font << /F1 6 0 R >>" : string.Empty;
        offsets[3] = ms.Position;
        Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
              "/Contents 4 0 R /Resources <<" +
              " /XObject << /Im0 5 0 R >>" +
              fontResources +
              " >> >>\nendobj\n");

        offsets[4] = ms.Position;
        Write($"4 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
        ms.Write(contentBytes, 0, contentBytes.Length);
        Write("\nendstream\nendobj\n");

        offsets[5] = ms.Position;
        Write("5 0 obj\n<< /Type /XObject /Subtype /Image " +
              $"/Width {imageBytes.Length} /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 " +
              $"/Length {imageBytes.Length} >>\nstream\n");
        ms.Write(imageBytes, 0, imageBytes.Length);
        Write("\nendstream\nendobj\n");

        if (includeFont)
        {
            offsets[6] = ms.Position;
            Write("6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        }

        var xref = ms.Position;
        Write($"xref\n0 {objectCount + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= objectCount; i++)
            Write($"{offsets[i]:D10} 00000 n \n");

        Write($"trailer\n<< /Root 1 0 R /Size {objectCount + 1} >>\nstartxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }
}
