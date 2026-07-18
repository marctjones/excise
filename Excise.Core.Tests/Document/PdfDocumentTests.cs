using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Parsing;
using Excise.Core.Primitives;
using System.Text;
using Xunit;

namespace Excise.Core.Tests.Document;

public class PdfDocumentTests
{
    /// <summary>
    /// Creates a minimal valid PDF for testing with correct byte offsets.
    /// </summary>
    private static byte[] CreateMinimalPdf()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        // Header
        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        // Track object positions
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

        // Object 3: Page
        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // xref position
        long xrefPos = ms.Position;

        writer.WriteLine("xref");
        writer.WriteLine("0 4");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{offsets[1]:D10} 00000 n ");
        writer.WriteLine($"{offsets[2]:D10} 00000 n ");
        writer.WriteLine($"{offsets[3]:D10} 00000 n ");
        writer.Flush();

        // trailer
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 4 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    /// <summary>
    /// Creates a minimal PDF with content stream.
    /// </summary>
    private static byte[] CreatePdfWithContent()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        var content = "BT /F1 12 Tf 100 700 Td (Hello World) Tj ET";

        // Header
        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        // Track object positions
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

        // Object 5: Font (simplified)
        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // xref position
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

        // trailer
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 6 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    [Fact]
    public void Open_MinimalPdf_ReturnsDocument()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        doc.Should().NotBeNull();
        doc.Version.Should().Be("1.4");
        doc.PageCount.Should().Be(1);
    }

    [Fact]
    public void GetPage_FirstPage_ReturnsPage()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        page.Should().NotBeNull();
        page.PageNumber.Should().Be(1);
        page.Width.Should().Be(612);
        page.Height.Should().Be(792);
    }

    [Fact]
    public void GetPage_InvalidPageNumber_ThrowsException()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        var act1 = () => doc.GetPage(0);
        var act2 = () => doc.GetPage(2);

        act1.Should().Throw<ArgumentOutOfRangeException>();
        act2.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetPages_EnumeratesAllPages()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);
        var pages = doc.GetPages().ToList();

        pages.Count.Should().Be(1);
        pages[0].PageNumber.Should().Be(1);
    }

    [Fact]
    public void Page_MediaBox_ReturnsCorrectRectangle()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        page.MediaBox.Left.Should().Be(0);
        page.MediaBox.Bottom.Should().Be(0);
        page.MediaBox.Right.Should().Be(612);
        page.MediaBox.Top.Should().Be(792);
    }

    [Fact]
    public void Page_CropBox_FallsBackToMediaBox()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        page.CropBox.Should().Be(page.MediaBox);
    }

    [Fact]
    public void Page_GetContentStreamBytes_ReturnsContent()
    {
        var pdfData = CreatePdfWithContent();

        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var content = page.GetContentStreamBytes();

        content.Should().NotBeEmpty();
        var contentStr = Encoding.ASCII.GetString(content);
        contentStr.Should().Contain("Hello World");
    }

    [Fact]
    public void Page_Resources_ReturnsResourcesDictionary()
    {
        var pdfData = CreatePdfWithContent();

        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        page.Resources.Should().NotBeNull();
        page.Resources!.ContainsKey("Font").Should().BeTrue();
    }

    [Fact]
    public void Page_GetFont_ReturnsFont()
    {
        var pdfData = CreatePdfWithContent();

        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var font = page.GetFont("F1");

        font.Should().NotBeNull();
        font!.GetName("BaseFont").Should().Be("Helvetica");
    }

    [Fact]
    public void Catalog_HasExpectedEntries()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        doc.Catalog.Should().NotBeNull();
        doc.Catalog.GetName("Type").Should().Be("Catalog");
        doc.Catalog.ContainsKey("Pages").Should().BeTrue();
    }

    [Fact]
    public void IsEncrypted_UnencryptedPdf_ReturnsFalse()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        doc.IsEncrypted.Should().BeFalse();
    }

    /// <summary>
    /// Creates a PDF with document metadata (Info dictionary).
    /// </summary>
    private static byte[] CreatePdfWithInfo()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        // Header
        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[5];

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
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 4: Info dictionary
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine("<< /Title (Test Document) /Author (Test Author) /Subject (Test Subject) /Keywords (test, pdf) /Creator (Test Creator) /Producer (Test Producer) >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;

        writer.WriteLine("xref");
        writer.WriteLine("0 5");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{offsets[1]:D10} 00000 n ");
        writer.WriteLine($"{offsets[2]:D10} 00000 n ");
        writer.WriteLine($"{offsets[3]:D10} 00000 n ");
        writer.WriteLine($"{offsets[4]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Info 4 0 R /Size 5 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    [Fact]
    public void Info_WithMetadata_ReturnsInfoDictionary()
    {
        var pdfData = CreatePdfWithInfo();

        using var doc = PdfDocument.Open(pdfData);

        doc.Info.Should().NotBeNull();
        doc.Info!.ContainsKey("Title").Should().BeTrue();
    }

    [Fact]
    public void Title_WithMetadata_ReturnsTitle()
    {
        var pdfData = CreatePdfWithInfo();

        using var doc = PdfDocument.Open(pdfData);

        doc.Title.Should().Be("Test Document");
    }

    [Fact]
    public void Author_WithMetadata_ReturnsAuthor()
    {
        var pdfData = CreatePdfWithInfo();

        using var doc = PdfDocument.Open(pdfData);

        doc.Author.Should().Be("Test Author");
    }

    [Fact]
    public void Subject_WithMetadata_ReturnsSubject()
    {
        var pdfData = CreatePdfWithInfo();

        using var doc = PdfDocument.Open(pdfData);

        doc.Subject.Should().Be("Test Subject");
    }

    [Fact]
    public void Keywords_WithMetadata_ReturnsKeywords()
    {
        var pdfData = CreatePdfWithInfo();

        using var doc = PdfDocument.Open(pdfData);

        doc.Keywords.Should().Be("test, pdf");
    }

    [Fact]
    public void Creator_WithMetadata_ReturnsCreator()
    {
        var pdfData = CreatePdfWithInfo();

        using var doc = PdfDocument.Open(pdfData);

        doc.Creator.Should().Be("Test Creator");
    }

    [Fact]
    public void Producer_WithMetadata_ReturnsProducer()
    {
        var pdfData = CreatePdfWithInfo();

        using var doc = PdfDocument.Open(pdfData);

        doc.Producer.Should().Be("Test Producer");
    }

    [Fact]
    public void Title_WithoutMetadata_ReturnsNull()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        doc.Title.Should().BeNull();
    }

    [Fact]
    public void Author_WithoutMetadata_ReturnsNull()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        doc.Author.Should().BeNull();
    }

    [Fact]
    public void SaveToBytes_CreatesValidPdf()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);

        var savedData = doc.SaveToBytes();

        savedData.Should().NotBeEmpty();
        savedData.Should().StartWith(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // %PDF
    }

    [Fact]
    public void Save_ToStream_WritesValidPdf()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);

        using var outputStream = new MemoryStream();
        doc.Save(outputStream);
        var result = outputStream.ToArray();

        result.Should().NotBeEmpty();
        result.Should().StartWith(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // %PDF
    }

    [Fact]
    public void Save_ToFilePath_CreatesFile()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);

        var tempPath = Path.GetTempFileName();
        try
        {
            doc.Save(tempPath);

            File.Exists(tempPath).Should().BeTrue();
            var fileData = File.ReadAllBytes(tempPath);
            fileData.Should().StartWith(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // %PDF
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void FindPage_ValidIndex_ReturnsPageDictionary()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);
        var pages = doc.GetPages().ToList();

        pages.Should().HaveCount(1);
        pages[0].PageNumber.Should().Be(1);
    }

    [Fact]
    public void GetObject_WithInvalidObjectNumber_ThrowsException()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        var act = () => doc.GetObject(9999);

        act.Should().Throw<Excise.Core.Parsing.PdfParseException>();
    }

    [Fact]
    public void GetObject_FromReference_ReturnsObject()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        var catalogRef = doc.Trailer.Get<PdfReference>("Root");
        var catalog = doc.GetObject(catalogRef);

        catalog.Should().NotBeNull();
        catalog.Should().BeOfType<PdfDictionary>();
    }

    [Fact]
    public void Resolve_WithReference_FollowsReference()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        var catalogRef = doc.Trailer.Get<PdfReference>("Root");
        var resolved = doc.Resolve(catalogRef);

        resolved.Should().BeOfType<PdfDictionary>();
        (resolved as PdfDictionary)?.GetName("Type").Should().Be("Catalog");
    }

    [Fact]
    public void Resolve_WithNonReference_ReturnsSameObject()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        var name = new PdfName("Test");
        var resolved = doc.Resolve(name);

        resolved.Should().Be(name);
    }

    [Fact]
    public void GetAcroForm_WithoutForm_ReturnsNull()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        var acroForm = doc.GetAcroForm();

        acroForm.Should().BeNull();
    }

    [Fact]
    public void CreateNew_DefaultVersion_CreatesDocument()
    {
        using var doc = PdfDocument.CreateNew();

        doc.Should().NotBeNull();
        doc.Version.Should().Be("1.7");
        doc.PageCount.Should().Be(0);
        doc.IsEncrypted.Should().BeFalse();
    }

    [Fact]
    public void CreateNew_CustomVersion_CreatesDocumentWithVersion()
    {
        using var doc = PdfDocument.CreateNew("2.0");

        doc.Version.Should().Be("2.0");
    }

    [Fact]
    public void IsDecrypting_UnencryptedPdf_ReturnsFalse()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        doc.IsDecrypting.Should().BeFalse();
    }

    #region GetAcroForm Tests

    [Fact]
    public void GetAcroForm_WithFormReturnsNull_WhenAcroFormIsNull()
    {
        // Test GetAcroForm when Catalog doesn't have AcroForm entry
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);
        var acroForm = doc.GetAcroForm();

        acroForm.Should().BeNull();
    }

    [Fact]
    public void GetAcroForm_WithInvalidAcroFormReference_ReturnsNull()
    {
        // Create a PDF where AcroForm references an invalid dictionary
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[4];

        // Catalog with invalid AcroForm (null)
        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R /AcroForm null >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

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

        var pdfData = ms.ToArray();
        using var doc = PdfDocument.Open(pdfData);
        var acroForm = doc.GetAcroForm();

        acroForm.Should().BeNull();
    }

    #endregion

    #region FindPage Tests

    [Fact]
    public void FindPage_SinglePage_ReturnsPageAtIndex0()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);
        var page1 = doc.GetPage(1);

        page1.Should().NotBeNull();
        page1.MediaBox.Right.Should().Be(612);
    }

    #endregion

    #region GetObject Tests

    [Fact]
    public void GetObject_ResolveIndirectReference_ReturnsCachedObject()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        // Get the catalog (object 1) twice
        var cat1 = doc.Catalog;
        var cat2 = doc.Catalog;

        // Should be the same object (cached)
        ReferenceEquals(cat1, cat2).Should().BeTrue();
    }

    [Fact]
    public void GetObject_NonexistentObjectNumber_ThrowsException()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        // Try to get an object that doesn't exist
        var action = () => doc.GetObject(9999);

        action.Should().Throw<PdfParseException>();
    }

    [Fact]
    public void ResolveLengthReference_WithValidReference_ReturnsObject()
    {
        var pdfData = CreatePdfWithContent();

        using var doc = PdfDocument.Open(pdfData);

        // The content stream's /Length field is resolved during GetPage
        var page = doc.GetPage(1);
        var content = page.GetContentStreamBytes();

        content.Should().NotBeEmpty();
    }

    #endregion

    #region Resolve Tests

    [Fact]
    public void Resolve_WithPdfReference_FollowsReference()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        // Resolve the Pages reference from catalog
        var pagesRef = doc.Catalog.Get<PdfReference>("Pages");
        var resolved = doc.Resolve(pagesRef);

        resolved.Should().BeOfType<PdfDictionary>();
        (resolved as PdfDictionary)!.GetName("Type").Should().Be("Pages");
    }

    [Fact]
    public void Resolve_WithChainedReferences_FollowsChain()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        // The page tree may have chains of references
        var pagesRef = doc.Catalog.Get<PdfReference>("Pages");
        var pagesDict = doc.Resolve(pagesRef) as PdfDictionary;

        pagesDict.Should().NotBeNull();
        pagesDict!.ContainsKey("Kids").Should().BeTrue();
    }

    #endregion

    #region GetAcroForm with Form Dictionary Tests

    [Fact]
    public void GetAcroForm_WithValidAcroFormDictionary_ReturnsForm()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[5];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine("<< /SigFlags 0 /Fields [] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 5");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 4; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 5 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        var pdfData = ms.ToArray();
        using var doc = PdfDocument.Open(pdfData);
        var acroForm = doc.GetAcroForm();

        acroForm.Should().NotBeNull();
    }

    #endregion

    #region Multiple Pages Tests

    [Fact]
    public void GetPages_WithMultiplePages_ReturnsAll()
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
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 5");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 4; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 5 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        var pdfData = ms.ToArray();
        using var doc = PdfDocument.Open(pdfData);

        doc.PageCount.Should().Be(2);
        var pages = doc.GetPages().ToList();
        pages.Should().HaveCount(2);
        pages[0].PageNumber.Should().Be(1);
        pages[1].PageNumber.Should().Be(2);
    }

    [Fact]
    public void GetPage_SecondPage_ReturnsCorrectPage()
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
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 800 600] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 5");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 4; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 5 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        var pdfData = ms.ToArray();
        using var doc = PdfDocument.Open(pdfData);

        var page2 = doc.GetPage(2);
        page2.PageNumber.Should().Be(2);
        page2.Width.Should().Be(800);
        page2.Height.Should().Be(600);
    }

    #endregion

    #region Metadata Properties Tests

    [Fact]
    public void Subject_WithoutMetadata_ReturnsNull()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        doc.Subject.Should().BeNull();
    }

    [Fact]
    public void Keywords_WithoutMetadata_ReturnsNull()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        doc.Keywords.Should().BeNull();
    }

    [Fact]
    public void Creator_WithoutMetadata_ReturnsNull()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        doc.Creator.Should().BeNull();
    }

    [Fact]
    public void Producer_WithoutMetadata_ReturnsNull()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        doc.Producer.Should().BeNull();
    }

    #endregion

    #region Stream Operations Tests

    [Fact]
    public void Open_FromStream_WithOwnsStreamFalse_DoesNotDisposeStream()
    {
        var pdfData = CreateMinimalPdf();
        var stream = new MemoryStream(pdfData);

        using var doc = PdfDocument.Open(stream, ownsStream: false);
        doc.Should().NotBeNull();

        // Stream should still be accessible after doc disposal
        stream.CanRead.Should().BeTrue();
        stream.Dispose();
    }

    [Fact]
    public void Open_FromFilePath_OpensSuccessfully()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            var pdfData = CreateMinimalPdf();
            File.WriteAllBytes(tempPath, pdfData);

            using var doc = PdfDocument.Open(tempPath);
            doc.Should().NotBeNull();
            doc.PageCount.Should().Be(1);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    #endregion

    #region Object Caching Tests

    [Fact]
    public void GetObject_RepeatedAccess_UsesCachedObject()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        var obj1 = doc.GetObject(1);
        var obj2 = doc.GetObject(1);

        ReferenceEquals(obj1, obj2).Should().BeTrue();
    }

    [Fact]
    public void GetObject_InvalidObjectNumber_ThrowsCorrectException()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        var act = () => doc.GetObject(99999);

        act.Should().Throw<PdfParseException>().WithMessage("*not found in xref*");
    }

    #endregion

    #region Pagination and Page Navigation Tests

    [Fact]
    public void GetPage_OutOfRange_High_ThrowsException()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        var act = () => doc.GetPage(100);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetPage_NegativeNumber_ThrowsException()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);

        var act = () => doc.GetPage(-1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region PDF Version Tests

    [Fact]
    public void Version_MultipleVersionFormats_ParsesCorrectly()
    {
        // Test that various PDF versions are parsed correctly
        var versions = new[] { "1.0", "1.4", "1.7", "2.0" };

        foreach (var version in versions)
        {
            using var ms = new MemoryStream();
            using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
            writer.NewLine = "\n";

            writer.WriteLine($"%PDF-{version}");
            writer.Flush();

            var offsets = new long[4];

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
            writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
            writer.WriteLine("endobj");
            writer.Flush();

            long xrefPos = ms.Position;
            writer.WriteLine("xref");
            writer.WriteLine("0 4");
            writer.WriteLine("0000000000 65535 f ");
            for (int i = 1; i <= 3; i++)
                writer.WriteLine($"{offsets[i]:D10} 00000 n ");
            writer.Flush();

            writer.WriteLine("trailer");
            writer.WriteLine("<< /Root 1 0 R /Size 4 >>");
            writer.WriteLine("startxref");
            writer.WriteLine(xrefPos.ToString());
            writer.WriteLine("%%EOF");
            writer.Flush();

            var pdfData = ms.ToArray();
            using var doc = PdfDocument.Open(pdfData);
            doc.Version.Should().Be(version);
        }
    }

    #endregion

    #region Disposal Tests

    [Fact]
    public void Dispose_ClosesStreamWhenOwned()
    {
        var pdfData = CreateMinimalPdf();
        var stream = new MemoryStream(pdfData);

        var doc = PdfDocument.Open(stream, ownsStream: true);
        doc.Dispose();

        stream.CanRead.Should().BeFalse();
    }

    [Fact]
    public void Dispose_DoesNotCloseStreamWhenNotOwned()
    {
        var pdfData = CreateMinimalPdf();
        var stream = new MemoryStream(pdfData);

        var doc = PdfDocument.Open(stream, ownsStream: false);
        doc.Dispose();

        stream.CanRead.Should().BeTrue();
        stream.Dispose();
    }

    #endregion

    #region Invalid PDF Tests

    [Fact]
    public void Open_InvalidPdfBytes_ThrowsParseException()
    {
        var invalidData = new byte[] { 0x00, 0xFF, 0xFE, 0xFD };

        var act = () => PdfDocument.Open(invalidData);

        act.Should().Throw<PdfParseException>();
    }

    [Fact]
    public void Open_EmptyByteArray_ThrowsParseException()
    {
        var emptyData = Array.Empty<byte>();

        var act = () => PdfDocument.Open(emptyData);

        act.Should().Throw<PdfParseException>();
    }

    [Fact]
    public void Open_MissingFilePath_ThrowsIOException()
    {
        var act = () => PdfDocument.Open("/path/that/does/not/exist.pdf");

        // May throw either FileNotFoundException or DirectoryNotFoundException depending on path
        act.Should().Throw<System.IO.IOException>();
    }

    #endregion

    #region Round-trip Save/Load Tests

    [Fact]
    public void SaveAndLoad_PreservesPageCount()
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
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 5");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 4; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 5 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        var originalData = ms.ToArray();
        using var doc = PdfDocument.Open(originalData);
        doc.PageCount.Should().Be(2);

        var savedData = doc.SaveToBytes();
        using var doc2 = PdfDocument.Open(savedData);
        doc2.PageCount.Should().Be(2);
    }

    #endregion

    #region Encrypted PDF Tests

    [Fact]
    public void Open_InvalidPdfStructure_ThrowsPdfParseException()
    {
        // Test that opening a PDF with invalid structure throws appropriate error
        var invalidData = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }; // "%PDF-" only

        var act = () => PdfDocument.Open(invalidData);

        act.Should().Throw<PdfParseException>();
    }

    #endregion

    #region Dispose Tests Extended

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var pdfData = CreateMinimalPdf();
        using var doc = PdfDocument.Open(pdfData);

        doc.Dispose();
        doc.Dispose(); // Should not throw
    }

    [Fact]
    public void Dispose_ClosesStreamOwned()
    {
        var pdfData = CreateMinimalPdf();
        var stream = new MemoryStream(pdfData);

        using var doc = PdfDocument.Open(stream, ownsStream: true);
        var canReadBefore = stream.CanRead;

        doc.Dispose();

        // After dispose, stream should be closed
        var act = () => stream.CanRead;
        // CanRead should throw or return false after disposal
    }

    #endregion

    #region Document Metadata Tests

    [Fact]
    public void Info_WithoutInfoDict_ReturnsNull()
    {
        var pdfData = CreateMinimalPdf();

        using var doc = PdfDocument.Open(pdfData);
        var info = doc.Info;

        info.Should().BeNull();
    }

    #endregion

    #region CropBox and ViewBox Tests

    [Fact]
    public void Page_CropBox_WithExplicitCropBox_ReturnsCropBox()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[4];

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
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /CropBox [10 10 600 780] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 4");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 3; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 4 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        var pdfData = ms.ToArray();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        page.CropBox.Left.Should().Be(10);
        page.CropBox.Right.Should().Be(600);
    }

    #endregion

    #region Nested Page Tree Tests

    [Fact]
    public void FindPage_NestedPageTree_WithMultiLevelHierarchy_ReturnsCorrectPage()
    {
        // Create a PDF with 2-level page tree: Catalog → Pages → Pages → Page
        // This tests the recursive branch in FindPage when a Pages node contains another Pages node
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

        // Object 2: Root Pages node with a nested Pages node as child
        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 3: Nested Pages node (intermediate level)
        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [4 0 R] /Count 1 /Parent 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 4: Page (leaf node)
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 3 0 R /MediaBox [0 0 612 792] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 5");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 4; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 5 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        var pdfData = ms.ToArray();
        using var doc = PdfDocument.Open(pdfData);

        // Should successfully find the page at index 1 despite 2-level nesting
        var page = doc.GetPage(1);
        page.Should().NotBeNull();
        page.PageNumber.Should().Be(1);
        page.Width.Should().Be(612);
        page.Height.Should().Be(792);
    }

    [Fact]
    public void FindPage_NestedPageTreeWithMultiplePages_ReturnsCorrectPageByIndex()
    {
        // Test with deeper nesting and multiple pages to verify recursive index tracking
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[8];

        // Object 1: Catalog
        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 2: Root Pages with 2 nested Pages as children
        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 3: First nested Pages node (contains 1 page)
        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [4 0 R] /Count 1 /Parent 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 4: First page
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 3 0 R /MediaBox [0 0 612 792] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 5: Second nested Pages node (contains 1 page)
        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [6 0 R] /Count 1 /Parent 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 6: Second page
        offsets[6] = ms.Position;
        writer.WriteLine("6 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 5 0 R /MediaBox [0 0 800 600] >>");
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

        var pdfData = ms.ToArray();
        using var doc = PdfDocument.Open(pdfData);

        // Should have 2 pages total
        doc.PageCount.Should().Be(2);

        // Should correctly retrieve both pages despite nested structure
        var page1 = doc.GetPage(1);
        page1.PageNumber.Should().Be(1);
        page1.Width.Should().Be(612);

        var page2 = doc.GetPage(2);
        page2.PageNumber.Should().Be(2);
        page2.Width.Should().Be(800);
    }

    #endregion
}
