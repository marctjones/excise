using AwesomeAssertions;
using Pdfe.Core.Content;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;
using System.Collections.Generic;
using System.Text;
using Xunit;

namespace Pdfe.Core.Tests.Document;

/// <summary>
/// Unit tests for PdfPage.
/// Tests content streams, resources, annotations, and page properties.
/// </summary>
public class PdfPageTests
{
    #region Helper Methods

    /// <summary>
    /// Creates a minimal valid PDF with a single page and content stream.
    /// </summary>
    private static byte[] CreateMinimalPdf(string content = "BT /F1 12 Tf 100 700 Td (Hello) Tj ET")
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[6];

        // Object 1: Catalog
        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 2: Pages
        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 3: Page
        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 4: Content stream
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 5: Font
        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;

        writer.WriteLine("xref");
        writer.WriteLine("0 6");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{offsets[1]:D10} 00000 n ");
        writer.WriteLine($"{offsets[2]:D10} 00000 n ");
        writer.WriteLine($"{offsets[3]:D10} 00000 n ");
        writer.WriteLine($"{offsets[4]:D10} 00000 n ");
        writer.WriteLine($"{offsets[5]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 6 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] CreatePdfWithMalformedFilteredContent(string content = "BT /F1 12 Tf 100 700 Td (BadFilter) Tj ET")
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[6];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Filter /Flatedecode /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;

        writer.WriteLine("xref");
        writer.WriteLine("0 6");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{offsets[1]:D10} 00000 n ");
        writer.WriteLine($"{offsets[2]:D10} 00000 n ");
        writer.WriteLine($"{offsets[3]:D10} 00000 n ");
        writer.WriteLine($"{offsets[4]:D10} 00000 n ");
        writer.WriteLine($"{offsets[5]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 6 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] CreatePdfWithImageOnlyJbig2ContentStream()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[7];
        const string validContent = "BT /F1 12 Tf 100 700 Td (Visible) Tj ET";
        const string invalidContent = "NotPdfContent";

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents [4 0 R 5 0 R] /Resources << /Font << /F1 6 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {validContent.Length} >>");
        writer.WriteLine("stream");
        writer.Write(validContent);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine($"<< /Filter /JBIG2Decode /Length {invalidContent.Length} >>");
        writer.WriteLine("stream");
        writer.Write(invalidContent);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[6] = ms.Position;
        writer.WriteLine("6 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;

        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 6; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 7 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    /// <summary>
    /// Creates a PDF with multiple content streams in an array.
    /// </summary>
    private static byte[] CreatePdfWithMultipleContentStreams()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[7];
        var content1 = "BT /F1 12 Tf 100 700 Td (First) Tj ET";
        var content2 = "BT /F1 12 Tf 100 650 Td (Second) Tj ET";

        // Object 1: Catalog
        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 2: Pages
        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 3: Page with Contents array
        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents [4 0 R 5 0 R] /Resources << /Font << /F1 6 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 4: Content stream 1
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content1.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content1);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 5: Content stream 2
        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine($"<< /Length {content2.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content2);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 6: Font
        offsets[6] = ms.Position;
        writer.WriteLine("6 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;

        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{offsets[1]:D10} 00000 n ");
        writer.WriteLine($"{offsets[2]:D10} 00000 n ");
        writer.WriteLine($"{offsets[3]:D10} 00000 n ");
        writer.WriteLine($"{offsets[4]:D10} 00000 n ");
        writer.WriteLine($"{offsets[5]:D10} 00000 n ");
        writer.WriteLine($"{offsets[6]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 7 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    /// <summary>
    /// Creates a PDF without any content stream.
    /// </summary>
    private static byte[] CreatePdfWithoutContent()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[4];

        // Object 1: Catalog
        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 2: Pages
        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 3: Page (no Contents)
        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;

        writer.WriteLine("xref");
        writer.WriteLine("0 4");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{offsets[1]:D10} 00000 n ");
        writer.WriteLine($"{offsets[2]:D10} 00000 n ");
        writer.WriteLine($"{offsets[3]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 4 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    #endregion

    #region Page Properties Tests

    [Fact]
    public void PageNumber_ReturnsCorrectValue()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        page.PageNumber.Should().Be(1);
    }

    [Fact]
    public void Width_ReturnsMediaBoxWidth()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        page.Width.Should().Be(612);
    }

    [Fact]
    public void Height_ReturnsMediaBoxHeight()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        page.Height.Should().Be(792);
    }

    [Fact]
    public void Document_ReturnsParentDocument()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        page.Document.Should().Be(doc);
    }

    [Fact]
    public void Dictionary_ReturnsPageDictionary()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        page.Dictionary.Should().NotBeNull();
        page.Dictionary.Should().BeOfType<PdfDictionary>();
    }

    [Fact]
    public void ToString_FormatsCorrectly()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var str = page.ToString();

        str.Should().Contain("Page 1");
        str.Should().Contain("612");
        str.Should().Contain("792");
    }

    #endregion

    #region Rotation Tests

    [Fact]
    public void Rotation_DefaultValue_Zero()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        page.Rotation.Should().Be(0);
    }

    [Fact]
    public void Rotation_SetValid_Stores()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        page.Rotation = 90;

        page.Rotation.Should().Be(90);
    }

    [Fact]
    public void Rotation_Set180_Valid()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        page.Rotation = 180;

        page.Rotation.Should().Be(180);
    }

    [Fact]
    public void Rotation_Set270_Valid()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        page.Rotation = 270;

        page.Rotation.Should().Be(270);
    }

    [Fact]
    public void Rotation_SetZeroAfterRotation_RemovesEntry()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        page.Rotation = 90;
        page.Rotation = 0;

        page.Dictionary.ContainsKey("Rotate").Should().BeFalse();
    }

    [Fact]
    public void Rotation_InvalidValue_ThrowsException()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var act = () => page.Rotation = 45;

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rotation_NormalizeNegative_Valid()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        page.Rotation = -90;

        page.Rotation.Should().Be(270);
    }

    [Fact]
    public void Rotation_NormalizeOver360_Valid()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        page.Rotation = 450;

        page.Rotation.Should().Be(90);
    }

    #endregion

    #region Content Stream Tests

    [Fact]
    public void GetContentStreamBytes_ValidStream_ReturnsData()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Test) Tj ET";
        var pdfData = CreateMinimalPdf(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var bytes = page.GetContentStreamBytes();

        bytes.Should().NotBeEmpty();
        Encoding.ASCII.GetString(bytes).Should().Contain("Test");
    }

    [Fact]
    public void GetContentStreamBytes_NoContent_ReturnsEmpty()
    {
        var pdfData = CreatePdfWithoutContent();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var bytes = page.GetContentStreamBytes();

        bytes.Should().BeEmpty();
    }

    [Fact]
    public void GetContentStreamBytes_MultipleStreams_Concatenates()
    {
        var pdfData = CreatePdfWithMultipleContentStreams();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var bytes = page.GetContentStreamBytes();
        var content = Encoding.ASCII.GetString(bytes);

        content.Should().Contain("First");
        content.Should().Contain("Second");
    }

    [Fact]
    public void TryGetContentStreamBytes_MalformedFilteredStream_SkipsWithoutWeakeningStrictRead()
    {
        var pdfData = CreatePdfWithMalformedFilteredContent();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var strictRead = () => page.GetContentStreamBytes();
        strictRead.Should().Throw<InvalidOperationException>()
            .WithMessage("Stream has not been decoded*");

        page.TryGetContentStreamBytes(out var bytes).Should().BeFalse();
        bytes.Should().BeEmpty();
    }

    [Fact]
    public void TryGetContentStreamBytes_ImageOnlyJbig2ContentStream_SkipsWithoutWeakeningStrictRead()
    {
        var pdfData = CreatePdfWithImageOnlyJbig2ContentStream();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var strictRead = () => page.GetContentStreamBytes();
        strictRead.Should().Throw<InvalidDataException>()
            .WithMessage("*JBIG2Decode*image XObjects*");

        page.TryGetContentStreamBytes(out var bytes, out var warnings).Should().BeFalse();
        var content = Encoding.ASCII.GetString(bytes);
        content.Should().Contain("Visible");
        content.Should().NotContain("NotPdfContent");
        warnings.Should().ContainSingle()
            .Which.Code.Should().Be(ContentStreamReadWarning.ImageOnlyFilterInContentStreamCode);
    }

    [Fact]
    public void SetContentStreamBytes_ReplacesSingleStream()
    {
        var pdfData = CreateMinimalPdf("BT (Original) Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var newContent = Encoding.ASCII.GetBytes("BT (Modified) Tj ET");
        page.SetContentStreamBytes(newContent);

        var retrieved = page.GetContentStreamBytes();
        Encoding.ASCII.GetString(retrieved).Should().Contain("Modified");
    }

    [Fact]
    public void SetContentStreamBytes_NoExistingContent_CreatesStream()
    {
        var pdfData = CreatePdfWithoutContent();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var newContent = Encoding.ASCII.GetBytes("BT (NewContent) Tj ET");
        page.SetContentStreamBytes(newContent);

        var retrieved = page.GetContentStreamBytes();
        Encoding.ASCII.GetString(retrieved).Should().Contain("NewContent");
    }

    [Fact]
    public void GetContentStream_ValidStream_Parsed()
    {
        var pdfData = CreateMinimalPdf("BT /F1 12 Tf (Hello) Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var stream = page.GetContentStream();

        stream.Should().NotBeNull();
        stream.Operators.Should().NotBeEmpty();
    }

    [Fact]
    public void GetContentStream_EmptyStream_ReturnsEmptyContentStream()
    {
        var pdfData = CreatePdfWithoutContent();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var stream = page.GetContentStream();

        stream.Should().NotBeNull();
        stream.Operators.Should().BeEmpty();
    }

    [Fact]
    public void SetContentStream_RoundTrip_Preserves()
    {
        var pdfData = CreateMinimalPdf("BT /F1 12 Tf (Original) Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var originalStream = page.GetContentStream();
        page.SetContentStream(originalStream);
        var retrievedStream = page.GetContentStream();

        retrievedStream.Operators.Count.Should().Be(originalStream.Operators.Count);
    }

    #endregion

    #region Resources Tests

    [Fact]
    public void GetFont_ExistingFont_ReturnsFont()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var font = page.GetFont("F1");

        font.Should().NotBeNull();
    }

    [Fact]
    public void GetFont_NonexistentFont_ReturnsNull()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var font = page.GetFont("F999");

        font.Should().BeNull();
    }

    [Fact]
    public void GetFont_NoResources_ReturnsNull()
    {
        var pdfData = CreatePdfWithoutContent();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var font = page.GetFont("F1");

        font.Should().BeNull();
    }

    [Fact]
    public void GetXObject_NoResources_ReturnsNull()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var xobj = page.GetXObject("Image1");

        xobj.Should().BeNull();
    }

    [Fact]
    public void GetExtGState_NoResources_ReturnsNull()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var extGState = page.GetExtGState("gs1");

        extGState.Should().BeNull();
    }

    [Fact]
    public void GetShading_NoResources_ReturnsNull()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var shading = page.GetShading("sh1");

        shading.Should().BeNull();
    }

    [Fact]
    public void GetColorSpaceObject_NoResources_ReturnsNull()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var colorSpace = page.GetColorSpaceObject("DeviceRGB");

        colorSpace.Should().BeNull();
    }

    #endregion

    #region Boxes Tests

    [Fact]
    public void MediaBox_ReturnsPageDimensions()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var mediaBox = page.MediaBox;

        mediaBox.Width.Should().Be(612);
        mediaBox.Height.Should().Be(792);
    }

    [Fact]
    public void CropBox_DefaultsToMediaBox()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var cropBox = page.CropBox;

        cropBox.Should().Be(page.MediaBox);
    }

    [Fact]
    public void BleedBox_DefaultsToCropBox()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var bleedBox = page.BleedBox;

        bleedBox.Should().Be(page.CropBox);
    }

    [Fact]
    public void TrimBox_DefaultsToCropBox()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var trimBox = page.TrimBox;

        trimBox.Should().Be(page.CropBox);
    }

    [Fact]
    public void ArtBox_DefaultsToCropBox()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var artBox = page.ArtBox;

        artBox.Should().Be(page.CropBox);
    }

    #endregion

    #region Text Extraction Tests

    [Fact]
    public void Text_ExtractsTextFromPage()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Hello World) Tj ET";
        var pdfData = CreateMinimalPdf(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var text = page.Text;

        text.Should().NotBeEmpty();
    }

    [Fact]
    public void Text_CacheReturnsSameStringOnSubsequentCalls()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Hello World) Tj ET";
        var pdfData = CreateMinimalPdf(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var text1 = page.Text;
        var text2 = page.Text;

        text1.Should().Be(text2);
        // Both should return the same cached string object (reference equality)
        text1.Should().BeSameAs(text2);
    }

    [Fact]
    public void Letters_CacheReturnsSameListOnSubsequentCalls()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Test) Tj ET";
        var pdfData = CreateMinimalPdf(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var letters1 = page.Letters;
        var letters2 = page.Letters;

        letters1.Should().HaveSameCount(letters2);
        // Both should return the same cached list object
        letters1.Should().BeSameAs(letters2);
    }

    [Fact]
    public void GetWords_CacheReturnsSameListOnSubsequentCalls()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Hello World) Tj ET";
        var pdfData = CreateMinimalPdf(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var words1 = page.GetWords();
        var words2 = page.GetWords();

        words1.Should().HaveSameCount(words2);
        // Both should return the same cached list object
        words1.Should().BeSameAs(words2);
    }

    /* Disabled - Letter and Word types not yet implemented
    [Fact]
    public void Letters_ReturnsLetterList()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Test) Tj ET";
        var pdfData = CreateMinimalPdf(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var letters = page.Letters;

        letters.Should().BeOfType<List<Letter>>();
    }


    public void GetWords_ReturnsWords()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Hello World) Tj ET";
        var pdfData = CreateMinimalPdf(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var words = page.GetWords();

        words.Should().BeOfType<List<Word>>();
    }
    */

    #endregion

    #region Annotations Tests

    [Fact]
    public void GetAnnotations_ReturnsAnnotationList()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var annotations = page.GetAnnotations();

        annotations.Should().BeAssignableTo<IReadOnlyList<PdfAnnotation>>();
    }

    [Fact]
    public void GetLinks_ReturnsLinkList()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var links = page.GetLinks();

        links.Should().BeAssignableTo<IReadOnlyList<PdfLink>>();
    }

    #endregion

    #region Graphics Context Tests

    /* Disabled - PdfGraphics type not yet implemented
    public void GetGraphics_ReturnsPdfGraphics()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var graphics = page.GetGraphics();

        graphics.Should().NotBeNull();
        graphics.Should().BeOfType<PdfGraphics>();
    }
    */

    [Fact]
    public void GetGraphics_ReturnsFreshInstanceEachCall()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var graphics1 = page.GetGraphics();
        var graphics2 = page.GetGraphics();

        graphics1.Should().NotBeSameAs(graphics2);
    }

    #endregion

    #region GetWords Tests

    [Fact]
    public void GetWords_ExtractsWordsFromPage()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Hello World) Tj ET";
        var pdfData = CreateMinimalPdf(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var words = page.GetWords();

        // GetWords should return a list of words
        words.Should().NotBeNull();
        words.Count.Should().BeGreaterThanOrEqualTo(0);
    }

    #endregion

    #region SetContentStreamBytes Tests

    [Fact]
    public void SetContentStreamBytes_WithoutExistingStream_CreatesNewStream()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();

        var newContent = Encoding.ASCII.GetBytes("BT (Test) Tj ET");
        page.SetContentStreamBytes(newContent);

        var retrieved = page.GetContentStreamBytes();
        retrieved.Should().Equal(newContent);
    }

    [Fact]
    public void SetContentStreamBytes_WithExistingStream_UpdatesContent()
    {
        var pdfData = CreateMinimalPdf("BT (Original) Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var newContent = Encoding.ASCII.GetBytes("BT (Updated) Tj ET");
        page.SetContentStreamBytes(newContent);

        var retrieved = page.GetContentStreamBytes();
        retrieved.Should().Equal(newContent);
    }

    [Fact]
    public void SetContentStreamBytes_WithArrayOfStreams_UpdatesFirstStream()
    {
        var pdfData = CreatePdfWithMultipleContentStreams();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var newContent = Encoding.ASCII.GetBytes("BT (New) Tj ET");
        page.SetContentStreamBytes(newContent);

        var retrieved = page.GetContentStreamBytes();
        // GetContentStreamBytes may append a trailing newline; strip it for comparison
        var trimmed = retrieved.TakeWhile((b, i) => i < newContent.Length || b != (byte)'\n').ToArray();
        trimmed.Should().StartWith(newContent);
    }

    #endregion

    #region AddFont Tests

    /* Disabled - PdfFont is not exposed in public API
    [Fact]
    public void AddFont_AddsNewFont_ReturnsFontName()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();

        var font = new PdfFont("TestFont", "/Helvetica");
        var fontName = page.AddFont(font);

        fontName.Should().NotBeNullOrEmpty();
        fontName.Should().StartWith("F");
    }

    [Fact]
    public void AddFont_ExistingFont_ReturnsSameName()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();

        var font = new PdfFont("TestFont", "/Helvetica");
        var firstName = page.AddFont(font);
        var secondName = page.AddFont(font);

        secondName.Should().Be(firstName);
    }

    [Fact]
    public void AddFont_CreatesResourcesDictionary_WhenMissing()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();

        page.Resources.Should().NotBeNull();

        var font = new PdfFont("TestFont", "/Helvetica");
        var fontName = page.AddFont(font);

        page.Resources!.ContainsKey("Font").Should().BeTrue();
    }
    */

    #endregion

    #region GetFont and GetFonts Tests

    [Fact]
    public void GetFont_WithExistingFont_ReturnsFont()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var font = page.GetFont("F1");

        font.Should().NotBeNull();
        font!.GetName("BaseFont").Should().Be("Helvetica");
    }

    [Fact]
    public void GetFont_WithNonexistentFont_ReturnsNull()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var font = page.GetFont("NonexistentFont");

        font.Should().BeNull();
    }

    [Fact]
    public void GetFonts_ReturnsAllFonts()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var fonts = page.GetFonts().ToList();

        fonts.Count.Should().BeGreaterThan(0);
        fonts.Should().AllSatisfy(f => f.Name.Should().NotBeNullOrEmpty());
    }

    #endregion

    #region EnsureResources Tests

    [Fact]
    public void Resources_CreatesWhenMissing()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank();

        var resources = page.Resources;

        resources.Should().NotBeNull();
    }

    [Fact]
    public void Resources_ExistingResources_ReturnsDictionary()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var resources = page.Resources;

        resources.Should().NotBeNull();
    }

    #endregion

    #region GetShading Tests

    [Fact]
    public void GetShading_WithoutShading_ReturnsNull()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var shading = page.GetShading("NonexistentShading");

        shading.Should().BeNull();
    }

    #endregion

    #region GetColorSpaceObject Tests

    [Fact]
    public void GetColorSpaceObject_WithoutColorSpace_ReturnsNull()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var colorSpace = page.GetColorSpaceObject("NonexistentColorSpace");

        colorSpace.Should().BeNull();
    }

    #endregion

    #region GetXObject Tests

    [Fact]
    public void GetXObject_WithoutXObject_ReturnsNull()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var xobject = page.GetXObject("NonexistentXObject");

        xobject.Should().BeNull();
    }

    #endregion

    #region GetExtGState Tests

    [Fact]
    public void GetExtGState_WithoutExtGState_ReturnsNull()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        var extGState = page.GetExtGState("NonexistentExtGState");

        extGState.Should().BeNull();
    }

    #endregion
}
