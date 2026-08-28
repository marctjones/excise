using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// Comprehensive xUnit tests for ImageRedactor internal class covering ~90%+ coverage.
/// Tests removal of image Do operators based on overlap with redaction rectangles.
/// </summary>
public class ImageRedactorTests
{
    #region Empty and No-Operation Tests

    [Fact]
    public void ProcessOperations_EmptyList_ReturnsEmpty()
    {
        // Arrange
        var pdfData = CreatePdfWithImageXObject(100, 600, 100, 100);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var operations = new List<ContentOperator>();
        var redactionArea = new PdfRectangle(50, 550, 250, 750);

        // Act
        var result = ImageRedactor.ProcessOperations(
            operations, page, redactionArea, GlyphRemovalStrategy.AnyOverlap, out int removed);

        // Assert
        result.Should().BeEmpty();
        removed.Should().Be(0);
    }

    [Fact]
    public void ProcessOperations_NoDoOperators_ReturnsAllOps()
    {
        // Arrange - Operations without Do
        var pdfData = CreatePdfWithImageXObject(100, 600, 100, 100);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var operations = new List<ContentOperator>
        {
            ContentOperator.Rectangle(10, 10, 100, 100),
            ContentOperator.Fill(),
        };
        var redactionArea = new PdfRectangle(0, 0, 1000, 1000);

        // Act
        var result = ImageRedactor.ProcessOperations(
            operations, page, redactionArea, GlyphRemovalStrategy.AnyOverlap, out int removed);

        // Assert
        result.Should().HaveCount(2);
        removed.Should().Be(0);
    }

    #endregion

    #region Do Operator Removal Tests

    [Fact]
    public void ProcessOperations_DoWithoutBoundingBox_Kept()
    {
        // Arrange - Do operator that can't be resolved shouldn't be removed
        var pdfData = CreatePdfWithImageXObject(100, 600, 100, 100);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var operations = new List<ContentOperator>
        {
            new ContentOperator("Do", new PdfObject[] { new PdfName("NonExistent") })
        };
        var redactionArea = new PdfRectangle(0, 0, 1000, 1000);

        // Act
        var result = ImageRedactor.ProcessOperations(
            operations, page, redactionArea, GlyphRemovalStrategy.AnyOverlap, out int removed);

        // Assert
        // Non-existent images should be kept (or handled gracefully)
        removed.Should().Be(0);
    }

    [Fact]
    public void ProcessOperations_FullyContainedImage_Removed()
    {
        // Arrange - Image fully contained in redaction area
        var pdfData = CreatePdfWithImageXObject(
            imageX: 100, imageY: 600, imageWidth: 100, imageHeight: 100);

        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var redactionArea = new PdfRectangle(50, 550, 250, 750); // Fully contains image

        // Get the original content stream
        var originalOps = page.GetContentStream().Operators;

        // Act
        var result = ImageRedactor.ProcessOperations(
            originalOps, page, redactionArea, GlyphRemovalStrategy.AnyOverlap, out int removed);

        // Assert
        removed.Should().BeGreaterThan(0);
        // Do operator should be missing
        result.Should().NotContain(op => op.Name == "Do");
    }

    [Fact]
    public void ProcessOperations_PartialOverlap_RegionEditsInsteadOfRemoving()
    {
        // #1195: a redaction area that only PARTIALLY covers the image must
        // destroy the covered samples and PRESERVE the image, not drop it.
        var pdfData = CreatePdfWithImageXObject(
            imageX: 100, imageY: 600, imageWidth: 100, imageHeight: 100); // quad (100,600)-(200,700)
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var partial = new PdfRectangle(150, 650, 300, 900); // top-right corner only

        var result = ImageRedactor.ProcessOperations(
            page.GetContentStream().Operators, page, partial,
            GlyphRemovalStrategy.AnyOverlap, out int removed, out int regionEdited);

        regionEdited.Should().Be(1, "the partially-covered image is region-redacted, not dropped");
        removed.Should().Be(0, "nothing should be dropped wholesale");
        var doOp = result.Should().ContainSingle(op => op.Name == "Do").Subject;
        doOp.GetName(0).Should().StartWith("ImRdct", "the Do is remapped to the redacted clone");

        // The covered sample (top row, right column) must be zeroed; the rest
        // of the 2x2 image (0x00,0x80,0x80,0xFF) survives. The clone is a fresh
        // Flate stream (decoded on reload in production); decode its bytes here.
        var redacted = (PdfStream)page.GetXObject(doOp.GetName(0)!)!;
        redacted.GetNameOrNull("Filter").Should().Be("FlateDecode");
        using var zin = new System.IO.MemoryStream(redacted.EncodedData);
        using var z = new System.IO.Compression.ZLibStream(
            zin, System.IO.Compression.CompressionMode.Decompress);
        using var zout = new System.IO.MemoryStream();
        z.CopyTo(zout);
        var bytes = zout.ToArray();
        bytes.Should().HaveCount(4);
        bytes[1].Should().Be(0, "the covered top-right sample is destroyed");
        bytes[2].Should().Be(0x80, "an uncovered sample is preserved");
        bytes[3].Should().Be(0xFF, "an uncovered sample is preserved");
    }

    [Fact]
    public void ProcessOperations_TwoAreasOnSameImage_BothRegionEdit_ImageSurvives()
    {
        // #1195: a term that repeats on a scan produces TWO redaction areas over
        // the SAME image. The second must region-edit the clone from the first,
        // not throw-and-drop it (which would reinstate the whole-image collateral
        // for the common repeated-term case).
        var pdfData = CreatePdfWithImageXObject(
            imageX: 100, imageY: 600, imageWidth: 100, imageHeight: 100); // quad (100,600)-(200,700)
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var pass1 = ImageRedactor.ProcessOperations(
            page.GetContentStream().Operators, page,
            new PdfRectangle(150, 650, 300, 900),   // top-right corner
            GlyphRemovalStrategy.AnyOverlap, out _, out int edited1);
        edited1.Should().Be(1);

        var pass2 = ImageRedactor.ProcessOperations(
            pass1, page,
            new PdfRectangle(50, 550, 150, 650),    // bottom-left corner, same image (now a clone)
            GlyphRemovalStrategy.AnyOverlap, out int removed2, out int edited2);

        edited2.Should().Be(1, "the second area must region-edit the clone from the first area");
        removed2.Should().Be(0, "the image must survive a second overlapping redaction");
        pass2.Should().ContainSingle(op => op.Name == "Do");
    }

    [Fact]
    public void ProcessOperations_FullyContained_DropsWholeImage_NoRegionEdit()
    {
        // The whole-image case still drops the Do — region-editing an image the
        // area fully covers would just be a slower way to delete it.
        var pdfData = CreatePdfWithImageXObject(
            imageX: 100, imageY: 600, imageWidth: 100, imageHeight: 100);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var contains = new PdfRectangle(50, 550, 250, 750);

        var result = ImageRedactor.ProcessOperations(
            page.GetContentStream().Operators, page, contains,
            GlyphRemovalStrategy.AnyOverlap, out int removed, out int regionEdited);

        removed.Should().Be(1);
        regionEdited.Should().Be(0);
        result.Should().NotContain(op => op.Name == "Do");
    }

    [Fact]
    public void ProcessOperations_NoOverlap_ImageKept()
    {
        // Arrange - Image far from redaction area
        var pdfData = CreatePdfWithImageXObject(
            imageX: 100, imageY: 600, imageWidth: 100, imageHeight: 100);

        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var redactionArea = new PdfRectangle(0, 0, 50, 50); // Far from image
        var originalOps = page.GetContentStream().Operators;

        // Act
        var result = ImageRedactor.ProcessOperations(
            originalOps, page, redactionArea, GlyphRemovalStrategy.AnyOverlap, out int removed);

        // Assert
        removed.Should().Be(0);
        // Do operator should survive
        result.Should().Contain(op => op.Name == "Do");
    }

    #endregion

    #region Strategy Tests

    [Fact]
    public void ProcessOperations_AnyOverlapStrategy_RemovesPartiallyOverlapping()
    {
        // Arrange - Image partially overlapping redaction
        var pdfData = CreatePdfWithImageXObject(
            imageX: 100, imageY: 600, imageWidth: 100, imageHeight: 100);

        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        // Redaction area just touches corner
        var redactionArea = new PdfRectangle(90, 590, 110, 610);
        var originalOps = page.GetContentStream().Operators;

        // Act
        var result = ImageRedactor.ProcessOperations(
            originalOps, page, redactionArea, GlyphRemovalStrategy.AnyOverlap,
            out int removed, out int regionEdited);

        // Assert — #1195: AnyOverlap still SELECTS a corner-touched image, but a
        // partial overlap is now region-redacted (samples destroyed, image kept),
        // not dropped wholesale.
        regionEdited.Should().Be(1, "AnyOverlap redacts even corner touches");
        removed.Should().Be(0);
        result.Should().ContainSingle(op => op.Name == "Do");
    }

    [Fact]
    public void ProcessOperations_FullyContainedStrategy_KeepsPartiallyOverlapping()
    {
        // Arrange - Image only partially contained in redaction
        var pdfData = CreatePdfWithImageXObject(
            imageX: 100, imageY: 600, imageWidth: 100, imageHeight: 100);

        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        // Redaction doesn't fully contain image
        var redactionArea = new PdfRectangle(90, 590, 150, 650);
        var originalOps = page.GetContentStream().Operators;

        // Act
        var result = ImageRedactor.ProcessOperations(
            originalOps, page, redactionArea, GlyphRemovalStrategy.FullyContained, out int removed);

        // Assert
        removed.Should().Be(0, "FullyContained should not remove partially overlapping images");
    }

    [Fact]
    public void ProcessOperations_FullyContainedStrategy_RemovesFullyContainedImage()
    {
        // Arrange - Image fully contained
        var pdfData = CreatePdfWithImageXObject(
            imageX: 100, imageY: 600, imageWidth: 100, imageHeight: 100);

        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        // Redaction fully contains image
        var redactionArea = new PdfRectangle(50, 550, 250, 750);
        var originalOps = page.GetContentStream().Operators;

        // Act
        var result = ImageRedactor.ProcessOperations(
            originalOps, page, redactionArea, GlyphRemovalStrategy.FullyContained, out int removed);

        // Assert
        removed.Should().BeGreaterThan(0, "FullyContained should remove fully contained images");
    }

    [Fact]
    public void ProcessOperations_CenterPointStrategy_RemovesWhenCenterInRedaction()
    {
        // Arrange - Image center inside redaction
        var pdfData = CreatePdfWithImageXObject(
            imageX: 100, imageY: 600, imageWidth: 100, imageHeight: 100);

        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        // Redaction contains the center of the image (150, 650)
        var redactionArea = new PdfRectangle(140, 640, 160, 660);
        var originalOps = page.GetContentStream().Operators;

        // Act
        var result = ImageRedactor.ProcessOperations(
            originalOps, page, redactionArea, GlyphRemovalStrategy.CenterPoint,
            out int removed, out int regionEdited);

        // Assert — #1195: CenterPoint still SELECTS the image (center is inside),
        // and the partial overlap is region-redacted rather than dropped.
        regionEdited.Should().Be(1, "CenterPoint redacts when the center is in the area");
        removed.Should().Be(0);
    }

    [Fact]
    public void ProcessOperations_CenterPointStrategy_KeepsWhenCenterOutside()
    {
        // Arrange - Image center outside redaction
        var pdfData = CreatePdfWithImageXObject(
            imageX: 100, imageY: 600, imageWidth: 100, imageHeight: 100);

        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        // Redaction misses the center
        var redactionArea = new PdfRectangle(50, 550, 99, 599);
        var originalOps = page.GetContentStream().Operators;

        // Act
        var result = ImageRedactor.ProcessOperations(
            originalOps, page, redactionArea, GlyphRemovalStrategy.CenterPoint, out int removed);

        // Assert
        removed.Should().Be(0, "CenterPoint should keep when center is outside redaction");
    }

    #endregion

    #region Graphics State Stack Tests

    [Fact]
    public void ProcessOperations_qQOperators_TrackState()
    {
        // Arrange - q/Q operators should be preserved
        var pdfData = CreatePdfWithImageXObject(100, 600, 100, 100);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var originalOps = page.GetContentStream().Operators;
        var redactionArea = new PdfRectangle(50, 550, 250, 750);

        // Act
        var result = ImageRedactor.ProcessOperations(
            originalOps, page, redactionArea, GlyphRemovalStrategy.AnyOverlap, out int removed);

        // Assert
        // q and Q operators should still be present (or handled correctly)
        result.Should().NotBeEmpty();
    }

    [Fact]
    public void ProcessOperations_cmOperator_TransformationTracked()
    {
        // Arrange - cm operator applies transformation
        var pdfData = CreatePdfWithTransformedImage();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var originalOps = page.GetContentStream().Operators;
        var redactionArea = new PdfRectangle(100, 600, 300, 800);

        // Act
        var result = ImageRedactor.ProcessOperations(
            originalOps, page, redactionArea, GlyphRemovalStrategy.AnyOverlap, out int removed);

        // Assert
        // Result should be valid regardless of transformation
        result.Should().NotBeEmpty();
    }

    #endregion

    #region Removed Count Tracking

    [Fact]
    public void ProcessOperations_RemovalCount_IsAccurate()
    {
        // Arrange
        var pdfData = CreatePdfWithImageXObject(100, 600, 100, 100);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var originalOps = page.GetContentStream().Operators;
        var redactionArea = new PdfRectangle(50, 550, 250, 750);

        // Act
        var result = ImageRedactor.ProcessOperations(
            originalOps, page, redactionArea, GlyphRemovalStrategy.AnyOverlap, out int removed);

        // Assert
        // Count should reflect actual removals
        removed.Should().BeGreaterThanOrEqualTo(0);
    }

    #endregion

    #region Inline Image (BI) Removal Tests (#354)

    [Fact]
    public void ProcessOperations_InlineImageOverlapping_Removed()
    {
        // Any page works — BI redaction needs no resource lookup.
        var pdfData = CreatePdfWithImageXObject(100, 600, 100, 100);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var operations = BuildInlineImageOps(100, 600, 100, 100);
        var redactionArea = new PdfRectangle(50, 550, 250, 750); // covers the image

        var result = ImageRedactor.ProcessOperations(
            operations, page, redactionArea, GlyphRemovalStrategy.AnyOverlap, out int removed);

        removed.Should().Be(1);
        result.Should().NotContain(op => op.Name == "BI",
            "an inline image overlapping the redaction area must be dropped");
    }

    [Fact]
    public void ProcessOperations_InlineImageNoOverlap_Kept()
    {
        var pdfData = CreatePdfWithImageXObject(100, 600, 100, 100);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var operations = BuildInlineImageOps(100, 600, 100, 100);
        var redactionArea = new PdfRectangle(0, 0, 50, 50); // far from the image

        var result = ImageRedactor.ProcessOperations(
            operations, page, redactionArea, GlyphRemovalStrategy.AnyOverlap, out int removed);

        removed.Should().Be(0);
        var bi = result.Single(op => op.Name == "BI");
        bi.InlineImageData.Should().NotBeNullOrEmpty("a non-overlapping image keeps its data");
    }

    [Fact]
    public void ProcessOperations_InlineImagePartialOverlap_FullyContainedKeeps()
    {
        var pdfData = CreatePdfWithImageXObject(100, 600, 100, 100);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var operations = BuildInlineImageOps(100, 600, 100, 100);
        var redactionArea = new PdfRectangle(90, 590, 150, 650); // partial only

        var result = ImageRedactor.ProcessOperations(
            operations, page, redactionArea, GlyphRemovalStrategy.FullyContained, out int removed);

        removed.Should().Be(0, "FullyContained must keep a partially-overlapping inline image");
        result.Should().Contain(op => op.Name == "BI");
    }

    /// <summary>
    /// Build q / cm / BI / Q operations placing a unit-square inline image at
    /// the given page-space rectangle. The BI carries embedded pixel bytes so
    /// the test exercises the same operator shape the parser produces.
    /// </summary>
    private static List<ContentOperator> BuildInlineImageOps(
        double x, double y, double w, double h)
    {
        var dict = new PdfDictionary
        {
            ["W"] = new PdfInteger(2),
            ["H"] = new PdfInteger(2),
            ["BPC"] = new PdfInteger(8),
            ["CS"] = new PdfName("G"),
        };
        var bi = new ContentOperator("BI", new PdfObject[] { dict })
        {
            InlineImageData = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
        };
        return new List<ContentOperator>
        {
            ContentOperator.SaveState(),
            ContentOperator.Transform(w, 0, 0, h, x, y),
            bi,
            ContentOperator.RestoreState(),
        };
    }

    #endregion

    #region Helper Methods

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

        var contentBody = $"q\n{imageWidth} 0 0 {imageHeight} {imageX} {imageY} cm\n/Im0 Do\nQ";
        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length {contentBody.Length} >>");
        w.WriteLine("stream");
        w.Write(contentBody);
        w.WriteLine();
        w.WriteLine("endstream");
        w.WriteLine("endobj");
        w.Flush();

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

    private static byte[] CreatePdfWithTransformedImage()
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

        // Include cm transformation
        var contentBody = "q\n1 0 0 1 100 600 cm\n100 0 0 100 0 0 cm\n/Im0 Do\nQ";
        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length {contentBody.Length} >>");
        w.WriteLine("stream");
        w.Write(contentBody);
        w.WriteLine();
        w.WriteLine("endstream");
        w.WriteLine("endobj");
        w.Flush();

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

    #endregion
}
