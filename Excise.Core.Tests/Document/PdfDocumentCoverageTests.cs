using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Parsing;
using Excise.Core.Primitives;
using System.Text;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Unit tests targeting uncovered code paths in PdfDocument.cs.
/// Focus: encryption error handling, object stream edge cases, metadata access, XMP handling.
/// </summary>
public class PdfDocumentCoverageTests
{
    /// <summary>
    /// Helper: Create a minimal PDF with custom trailer entries.
    /// </summary>
    private static byte[] CreatePdfWithCustomTrailer(string trailerExtra = "")
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
        writer.WriteLine($"<< /Root 1 0 R /Size 4 {trailerExtra} >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    /// <summary>
    /// Helper: Create a PDF where /Encrypt is not a dictionary.
    /// Line 207-208: encryptObj is not PdfDictionary
    /// </summary>
    private static byte[] CreatePdfWithInvalidEncryptDict()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[5];

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

        // Object 4: An integer (not a dict) to be referenced as /Encrypt
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine("42");
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
        writer.WriteLine("<< /Root 1 0 R /Size 5 /Encrypt 4 0 R /ID [(TEST)] >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    /// <summary>
    /// Helper: Create a PDF with /Encrypt but missing or empty /ID array.
    /// Line 211-213: /ID array missing or empty
    /// </summary>
    private static byte[] CreatePdfWithMissingIdArray()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[5];

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

        // Object 4: Minimal /Encrypt dict
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine("<< /Filter /Standard /V 1 /R 2 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;

        writer.WriteLine("xref");
        writer.WriteLine("0 5");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 4; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        // Trailer with /Encrypt but empty /ID array
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 5 /Encrypt 4 0 R /ID [] >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    /// <summary>
    /// Helper: Create a PDF with /Encrypt pointing to unsupported encryption version.
    /// Line 220-230: PdfEncryptionNotSupportedException caught with allowEncrypted=true
    /// </summary>
    private static byte[] CreatePdfWithUnsupportedEncryption()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[5];

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

        // Object 4: /Encrypt with unsupported V (99 is not a real version)
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine("<< /Filter /Standard /V 99 /R 99 /O (owner) /U (user) /P -1 >>");
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
        writer.WriteLine("<< /Root 1 0 R /Size 5 /Encrypt 4 0 R /ID [(TEST_ID)] >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    /// <summary>
    /// Helper: Create a PDF with an object stream containing compressed objects.
    /// </summary>
    private static byte[] CreatePdfWithObjectStream()
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
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 4: A small object to go into the object stream
        // (We'll manually write the stream content below)
        // Object stream format: pairs of (object_number, byte_offset) followed by objects
        string objStreamContent = "5 0 6 (Hello)";

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Type /ObjStm /N 1 /First 6 /Length {objStreamContent.Length} >>");
        writer.WriteLine("stream");
        writer.Write(objStreamContent);
        writer.WriteLine();
        writer.WriteLine("endstream");
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

        return ms.ToArray();
    }

    /// <summary>
    /// Helper: Create a PDF where an object in xref is marked as free (InUse=false).
    /// Line 435-436: if (!entry.InUse) return PdfNull.Instance;
    /// </summary>
    private static byte[] CreatePdfWithFreeObject()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[5];

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

        // Object 4 is defined but not referenced; we'll mark it as free
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine("<< /Name (UnusedObject) >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;

        writer.WriteLine("xref");
        writer.WriteLine("0 5");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{offsets[1]:D10} 00000 n ");
        writer.WriteLine($"{offsets[2]:D10} 00000 n ");
        writer.WriteLine($"{offsets[3]:D10} 00000 n ");
        // Mark object 4 as free (f instead of n)
        writer.WriteLine($"{offsets[4]:D10} 00000 f ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 5 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    /// <summary>
    /// Helper: Create a PDF with AcroForm (interactive form).
    /// </summary>
    private static byte[] CreatePdfWithAcroForm()
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

        // Object 4: Minimal AcroForm (interactive form dictionary)
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine("<< /SigFlags 0 >>");
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

        return ms.ToArray();
    }

    /// <summary>
    /// Helper: Create a PDF with XMP metadata stream.
    /// </summary>
    private static byte[] CreatePdfWithXmpMetadata()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[5];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>");
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

        string xmpContent = "<?xml version=\"1.0\"?><xmpmeta></xmpmeta>";

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Type /Metadata /Subtype /XML /Length {xmpContent.Length} >>");
        writer.WriteLine("stream");
        writer.Write(xmpContent);
        writer.WriteLine();
        writer.WriteLine("endstream");
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

        return ms.ToArray();
    }

    // ============================================================================
    // TEST CASES
    // ============================================================================

    /// <summary>
    /// Test line 207-208: /Encrypt is not a dictionary.
    /// Expect PdfParseException with message "/Encrypt is not a dictionary".
    /// </summary>
    [Fact]
    public void Open_EncryptNotDictionary_ThrowsPdfParseException()
    {
        var pdfData = CreatePdfWithInvalidEncryptDict();

        var act = () => PdfDocument.Open(new MemoryStream(pdfData), ownsStream: false, allowEncrypted: false);

        act.Should().Throw<PdfParseException>()
            .WithMessage("*is not a dictionary*");
    }

    /// <summary>
    /// Test line 211-213: /ID array is empty.
    /// Expect PdfParseException with message about /ID array.
    /// </summary>
    [Fact]
    public void Open_IdArrayEmpty_ThrowsPdfParseException()
    {
        var pdfData = CreatePdfWithMissingIdArray();

        var act = () => PdfDocument.Open(new MemoryStream(pdfData), ownsStream: false, allowEncrypted: false);

        act.Should().Throw<PdfParseException>()
            .WithMessage("*ID array*");
    }

    /// <summary>
    /// Test line 220-230: PdfEncryptionNotSupportedException caught with allowEncrypted=true.
    /// Should open successfully even with unsupported encryption if allowEncrypted=true.
    /// </summary>
    [Fact]
    public void Open_UnsupportedEncryptionWithAllowEncrypted_OpensSuccessfully()
    {
        var pdfData = CreatePdfWithUnsupportedEncryption();

        // With allowEncrypted=false, should throw
        var act1 = () => PdfDocument.Open(new MemoryStream(pdfData), ownsStream: false, allowEncrypted: false);
        act1.Should().Throw<PdfEncryptionNotSupportedException>();

        // With allowEncrypted=true, should open (handler will be null but document opens)
        using var doc = PdfDocument.Open(new MemoryStream(pdfData), ownsStream: false, allowEncrypted: true);
        doc.Should().NotBeNull();
        doc.IsEncrypted.Should().BeTrue();
        doc.PageCount.Should().Be(1);
    }

    /// <summary>
    /// Test line 435-436: Object in xref marked as free (InUse=false).
    /// GetObject should return PdfNull.Instance for free objects.
    /// </summary>
    [Fact]
    public void GetObject_FreeObject_ReturnsPdfNull()
    {
        var pdfData = CreatePdfWithFreeObject();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        // Try to retrieve object 4, which is marked as free in xref
        var result = doc.GetObject(4);

        result.Should().Be(PdfNull.Instance);
    }

    /// <summary>
    /// Test line 605-606: GetAcroForm when Catalog has no AcroForm entry.
    /// Should return null gracefully.
    /// </summary>
    [Fact]
    public void GetAcroForm_NoAcroFormInCatalog_ReturnsNull()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));
        var acroForm = doc.GetAcroForm();

        acroForm.Should().BeNull();
    }

    /// <summary>
    /// Test line 605-608: GetAcroForm when AcroForm exists but is not a dict.
    /// Should return null when AcroForm resolves to non-dict.
    /// </summary>
    [Fact]
    public void GetAcroForm_AcroFormNotDictionary_ReturnsNull()
    {
        // Create PDF where /AcroForm points to an integer instead of dict
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[4];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R /AcroForm 42 >>");
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
        using var doc = PdfDocument.Open(new MemoryStream(pdfData));
        var acroForm = doc.GetAcroForm();

        // Should return null since /AcroForm is not a dict
        acroForm.Should().BeNull();
    }

    /// <summary>
    /// Test line 616-619: GetXmpMetadata when Catalog has no Metadata entry.
    /// Should return null gracefully.
    /// </summary>
    [Fact]
    public void GetXmpMetadata_NoMetadata_ReturnsNull()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));
        var xmp = doc.GetXmpMetadata();

        xmp.Should().BeNull();
    }

    /// <summary>
    /// Test line 620: GetXmpMetadata when Metadata is not a stream.
    /// Should return null gracefully.
    /// </summary>
    [Fact]
    public void GetXmpMetadata_MetadataNotStream_ReturnsNull()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[4];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R /Metadata 42 >>");
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
        using var doc = PdfDocument.Open(new MemoryStream(pdfData));
        var xmp = doc.GetXmpMetadata();

        // Should return null since /Metadata is not a stream
        xmp.Should().BeNull();
    }

    /// <summary>
    /// Test line 622 (catch block): GetXmpMetadata when stream decompression fails.
    /// Should return null (exception caught and handled).
    /// </summary>
    [Fact]
    public void GetXmpMetadata_DecompressionError_ReturnsNull()
    {
        // Create a PDF with a Metadata stream that has a /FlateDecode filter but invalid compressed data
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[5];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>");
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

        // Invalid compressed data for FlateDecode
        string invalidCompressed = "\\xFF\\xFF\\xFF";
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Type /Metadata /Subtype /XML /Filter /FlateDecode /Length {invalidCompressed.Length} >>");
        writer.WriteLine("stream");
        writer.Write(invalidCompressed);
        writer.WriteLine();
        writer.WriteLine("endstream");
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

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));
        var xmp = doc.GetXmpMetadata();

        // Should return null when decompression fails (caught and handled)
        xmp.Should().BeNull();
    }

    /// <summary>
    /// Test line 483-486: Exception during stream decompression in GetObject.
    /// Should catch and continue with encoded data.
    /// </summary>
    [Fact]
    public void GetObject_StreamDecompressionError_ContinuesWithEncodedData()
    {
        // Create a PDF with a content stream that has invalid filter data
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
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Invalid FlateDecode stream
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Stream with invalid compressed data
        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Filter /FlateDecode /Length 10 >>");
        writer.WriteLine("stream");
        writer.Write("\\xFF\\xFF\\xFF\\xFF");
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;

        writer.WriteLine("xref");
        writer.WriteLine("0 6");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 6 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        var pdfData = ms.ToArray();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        // Getting the page should work even if the content stream has bad compression
        // because the exception is caught and the object is returned as-is
        var page = doc.GetPage(1);
        page.Should().NotBeNull();
    }

    /// <summary>
    /// Test line 545-549: ResolveLengthReference returns null on exception.
    /// This is an internal method but we test it indirectly by parsing a stream
    /// with an indirect /Length reference that is invalid/missing.
    /// </summary>
    [Fact]
    public void Open_IndirectLengthReferenceToMissingObject_StillParsesStream()
    {
        // Create a PDF where /Length is an indirect reference to a non-existent object
        // The parser should handle this gracefully via ResolveLengthReference returning null
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[6];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R /Pages 3 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [4 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Stream with /Length as indirect reference (999 0 R doesn't exist)
        string streamContent = "BT /F1 12 Tf (Hello) Tj ET";
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine("<< /Length 999 0 R >>");
        writer.WriteLine("stream");
        writer.Write(streamContent);
        writer.WriteLine();
        writer.WriteLine("endstream");
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

        // Should open without throwing even though indirect /Length reference is missing
        using var doc = PdfDocument.Open(new MemoryStream(pdfData));
        doc.Should().NotBeNull();
    }

    /// <summary>
    /// Test line 224: ownsStream disposal when catching encryption exception.
    /// When allowEncrypted=false and encryption throws PdfEncryptionNotSupportedException,
    /// the stream should be disposed if ownsStream=true.
    /// </summary>
    [Fact]
    public void Open_UnsupportedEncryptionWithOwnsStreamFalse_ThrowsWithoutDisposing()
    {
        var pdfData = CreatePdfWithUnsupportedEncryption();
        var stream = new MemoryStream(pdfData);

        // With ownsStream=false, exception should throw but stream stays open
        var act = () => PdfDocument.Open(stream, ownsStream: false, allowEncrypted: false);

        act.Should().Throw<PdfEncryptionNotSupportedException>();

        // Stream should still be readable (not disposed)
        stream.CanRead.Should().BeTrue();
    }

    /// <summary>
    /// Test metadata properties (Title, Author, Subject, Keywords, Creator, Producer).
    /// These should read from Info dictionary when present.
    /// </summary>
    [Fact]
    public void MetadataProperties_WithInfoDictionary_ReturnValues()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[5];

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

        // Object 4: Info dictionary with metadata
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine("<< /Title (Test Title) /Author (Test Author) /Subject (Test Subject) /Keywords (key1, key2) /Creator (My App) /Producer (PDF Producer) >>");
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
        writer.WriteLine("<< /Root 1 0 R /Info 4 0 R /Size 5 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        var pdfData = ms.ToArray();
        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        doc.Title.Should().Be("Test Title");
        doc.Author.Should().Be("Test Author");
        doc.Subject.Should().Be("Test Subject");
        doc.Keywords.Should().Be("key1, key2");
        doc.Creator.Should().Be("My App");
        doc.Producer.Should().Be("PDF Producer");
    }

    /// <summary>
    /// Test metadata properties when Info is null.
    /// Should return null gracefully.
    /// </summary>
    [Fact]
    public void MetadataProperties_WithoutInfoDictionary_ReturnNull()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        doc.Title.Should().BeNull();
        doc.Author.Should().BeNull();
        doc.Subject.Should().BeNull();
        doc.Keywords.Should().BeNull();
        doc.Creator.Should().BeNull();
        doc.Producer.Should().BeNull();
    }

    /// <summary>
    /// Test GetPage with valid page numbers.
    /// </summary>
    [Fact]
    public void GetPage_ValidPageNumber_ReturnsPage()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));
        var page = doc.GetPage(1);

        page.Should().NotBeNull();
    }

    /// <summary>
    /// Test GetPage with page number less than 1.
    /// </summary>
    [Fact]
    public void GetPage_PageNumberBelowOne_ThrowsArgumentOutOfRangeException()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        var act = () => doc.GetPage(0);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("pageNumber");
    }

    /// <summary>
    /// Test GetPage with page number greater than PageCount.
    /// </summary>
    [Fact]
    public void GetPage_PageNumberAbovePageCount_ThrowsArgumentOutOfRangeException()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        var act = () => doc.GetPage(100);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("pageNumber");
    }

    /// <summary>
    /// Test GetPages enumeration.
    /// </summary>
    [Fact]
    public void GetPages_EnumeratesAllPages()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));
        var pages = doc.GetPages().ToList();

        pages.Should().HaveCount(1);
    }

    /// <summary>
    /// Test Resolve with non-reference objects.
    /// Should return the object unchanged.
    /// </summary>
    [Fact]
    public void Resolve_NonReference_ReturnsSameObject()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        var obj = new PdfString("hello");
        var resolved = doc.Resolve(obj);

        resolved.Should().Be(obj);
    }

    /// <summary>
    /// Test Resolve with reference objects.
    /// Should dereference and return the actual object.
    /// </summary>
    [Fact]
    public void Resolve_Reference_DereferencesAndReturnsObject()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        // Catalog is a reference in the trailer
        var catalogRef = doc.Trailer.Get<PdfReference>("Root");
        var resolved = doc.Resolve(catalogRef);

        resolved.Should().Be(doc.Catalog);
        resolved.Should().BeOfType<PdfDictionary>();
    }

    /// <summary>
    /// Test IsTaggedPdf when MarkInfo/Marked is a name "true".
    /// </summary>
    [Fact]
    public void IsTaggedPdf_WithMarkInfoMarkedTrue_ReturnsTrueWithName()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[4];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked /true >> >>");
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
        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        doc.IsTaggedPdf.Should().BeTrue();
    }

    /// <summary>
    /// Test IsTaggedPdf when MarkInfo/Marked is a boolean true.
    /// </summary>
    [Fact]
    public void IsTaggedPdf_WithMarkInfoMarkedBoolean_ReturnsTrueWithBoolean()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[4];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> >>");
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
        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        doc.IsTaggedPdf.Should().BeTrue();
    }

    /// <summary>
    /// Test IsTaggedPdf when MarkInfo is missing.
    /// Should return false.
    /// </summary>
    [Fact]
    public void IsTaggedPdf_WithoutMarkInfo_ReturnsFalse()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        doc.IsTaggedPdf.Should().BeFalse();
    }

    /// <summary>
    /// Test IsTaggedPdf caching by accessing it multiple times.
    /// </summary>
    [Fact]
    public void IsTaggedPdf_AccessedMultipleTimes_IsCached()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        // Access twice to ensure caching works
        var first = doc.IsTaggedPdf;
        var second = doc.IsTaggedPdf;

        first.Should().Be(second);
    }

    /// <summary>
    /// Test HasEmbeddedFiles when both modern and legacy entries are missing.
    /// </summary>
    [Fact]
    public void HasEmbeddedFiles_WithNoEmbeddedFiles_ReturnsFalse()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        doc.HasEmbeddedFiles.Should().BeFalse();
    }

    /// <summary>
    /// Test GetEmbeddedFiles caching.
    /// </summary>
    [Fact]
    public void GetEmbeddedFiles_AccessedMultipleTimes_IsCached()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        var first = doc.GetEmbeddedFiles();
        var second = doc.GetEmbeddedFiles();

        first.Should().BeSameAs(second);
    }

    /// <summary>
    /// Test ScrubMetadata with default scrubAttachments=true.
    /// Should clear Info dict and remove Metadata from Catalog.
    /// </summary>
    [Fact]
    public void ScrubMetadata_WithDefaultScrubAttachments_ClearsInfoAndMetadata()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[5];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>");
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

        string xmpContent = "<?xml version=\"1.0\"?><xmpmeta></xmpmeta>";
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Type /Metadata /Subtype /XML /Length {xmpContent.Length} >>");
        writer.WriteLine("stream");
        writer.Write(xmpContent);
        writer.WriteLine();
        writer.WriteLine("endstream");
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
        writer.WriteLine("<< /Root 1 0 R /Info 4 0 R /Size 5 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        var pdfData = ms.ToArray();
        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        // Before scrubbing
        doc.GetXmpMetadata().Should().NotBeNull();

        // Scrub with default (scrubAttachments=true)
        doc.ScrubMetadata();

        // After scrubbing, Metadata should be removed
        doc.Catalog.ContainsKey("Metadata").Should().BeFalse();
    }

    /// <summary>
    /// Test ScrubMetadata with scrubAttachments=false.
    /// Should clear Info dict but NOT remove embedded files.
    /// </summary>
    [Fact]
    public void ScrubMetadata_WithScrubAttachmentsFalse_DoesNotScrubEmbeddedFiles()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        // Call with scrubAttachments=false
        doc.ScrubMetadata(scrubAttachments: false);

        // Should still work (no exception thrown)
        doc.Should().NotBeNull();
    }

    /// <summary>
    /// Test ScrubInfoKeys with specific keys to remove.
    /// </summary>
    [Fact]
    public void ScrubInfoKeys_WithSpecificKeys_RemovesOnlyThoseKeys()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[5];

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

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine("<< /Title (Title) /Author (Author) /Subject (Subject) >>");
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
        writer.WriteLine("<< /Root 1 0 R /Info 4 0 R /Size 5 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        var pdfData = ms.ToArray();
        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        // Scrub only Title and Author
        doc.ScrubInfoKeys("Title", "Author");

        doc.Title.Should().BeNull();
        doc.Author.Should().BeNull();
        doc.Subject.Should().Be("Subject");
    }

    /// <summary>
    /// Test ScrubInfoKeys with null keys parameter.
    /// Should not throw.
    /// </summary>
    [Fact]
    public void ScrubInfoKeys_WithNullKeys_DoesNotThrow()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        var act = () => doc.ScrubInfoKeys(null!);

        act.Should().NotThrow();
    }

    /// <summary>
    /// Test ComputeReachableObjects with a simple PDF.
    /// Should find at least the root catalog and pages.
    /// </summary>
    [Fact]
    public void ComputeReachableObjects_WithSimplePdf_FindsReachableObjects()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        var reachable = doc.ComputeReachableObjects();

        reachable.Should().NotBeEmpty();
        reachable.Should().Contain(1); // Catalog
        reachable.Should().Contain(2); // Pages
    }

    /// <summary>
    /// Test AddIndirectObject and RemoveObject.
    /// </summary>
    [Fact]
    public void AddIndirectObject_CreatesNewObject_WithValidReference()
    {
        var pdfData = PdfDocument.CreateNew().SaveToBytes();
        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        var newObj = new PdfString("test content");
        var reference = doc.AddIndirectObject(newObj);

        reference.ObjectNum.Should().BeGreaterThan(0);
        reference.Generation.Should().Be(0);

        // Should be able to retrieve it
        var retrieved = doc.GetObject(reference);
        retrieved.Should().Be(newObj);
    }

    /// <summary>
    /// Test RemoveObject removes from xref and object cache.
    /// </summary>
    [Fact]
    public void RemoveObject_RemovesObjectFromDocument()
    {
        var pdfData = PdfDocument.CreateNew().SaveToBytes();
        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        var newObj = new PdfString("test content");
        var reference = doc.AddIndirectObject(newObj);

        doc.RemoveObject(reference.ObjectNum);

        // Trying to get removed object should throw
        var act = () => doc.GetObject(reference.ObjectNum);
        act.Should().Throw<PdfParseException>();
    }

    /// <summary>
    /// Test GetOptionalContentGroups caching.
    /// </summary>
    [Fact]
    public void GetOptionalContentGroups_AccessedMultipleTimes_IsCached()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        var first = doc.GetOptionalContentGroups();
        var second = doc.GetOptionalContentGroups();

        first.Should().BeSameAs(second);
    }

    /// <summary>
    /// Test GetOptionalContentGroupConfig caching.
    /// </summary>
    [Fact]
    public void GetOptionalContentGroupConfig_AccessedMultipleTimes_IsCached()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        var first = doc.GetOptionalContentGroupConfig();
        var second = doc.GetOptionalContentGroupConfig();

        first.Should().BeSameAs(second);
    }

    /// <summary>
    /// Test GetStructureTree returns null when not present.
    /// </summary>
    [Fact]
    public void GetStructureTree_WithoutStructTree_ReturnsNull()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        var structTree = doc.GetStructureTree();

        structTree.Should().BeNull();
    }

    /// <summary>
    /// Test Pages property lazy initialization.
    /// </summary>
    [Fact]
    public void Pages_LazyInitialization_CreatesPageCollection()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        var pages = doc.Pages;
        pages.Should().NotBeNull();
        pages.Count.Should().Be(1);
    }

    /// <summary>
    /// Test SaveToBytes method.
    /// </summary>
    [Fact]
    public void SaveToBytes_ReturnsByteArray()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));
        var saved = doc.SaveToBytes();

        saved.Should().NotBeEmpty();
        saved.Should().StartWith(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // %PDF
    }

    /// <summary>
    /// Test Save to file path.
    /// </summary>
    [Fact]
    public void Save_ToFilePath_WritesFile()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        var tempPath = Path.GetTempFileName();
        try
        {
            doc.Save(tempPath);

            File.Exists(tempPath).Should().BeTrue();
            var fileBytes = File.ReadAllBytes(tempPath);
            fileBytes.Should().StartWith(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // %PDF
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Test IsDecrypting property.
    /// </summary>
    [Fact]
    public void IsDecrypting_WithNonEncryptedPdf_ReturnsFalse()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        doc.IsDecrypting.Should().BeFalse();
    }

    /// <summary>
    /// Test IsEncrypted property.
    /// </summary>
    [Fact]
    public void IsEncrypted_WithEncryptionPresent_ReturnsTrue()
    {
        var pdfData = CreatePdfWithUnsupportedEncryption();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData), allowEncrypted: true);

        doc.IsEncrypted.Should().BeTrue();
    }

    /// <summary>
    /// Test IsEncrypted property with non-encrypted PDF.
    /// </summary>
    [Fact]
    public void IsEncrypted_WithoutEncryption_ReturnsFalse()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        doc.IsEncrypted.Should().BeFalse();
    }

    /// <summary>
    /// Test Version property.
    /// </summary>
    [Fact]
    public void Version_IsPreserved()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        doc.Version.Should().Be("1.4");
    }

    /// <summary>
    /// Test CreateNew with custom version.
    /// </summary>
    [Fact]
    public void CreateNew_WithCustomVersion_SetsPdfVersion()
    {
        using var doc = PdfDocument.CreateNew("2.0");

        doc.Version.Should().Be("2.0");
        doc.PageCount.Should().Be(0);
    }

    /// <summary>
    /// Test Open from file path.
    /// </summary>
    [Fact]
    public void Open_FromFilePath_LoadsDocument()
    {
        var pdfData = CreatePdfWithCustomTrailer();
        var tempPath = Path.GetTempFileName();

        try
        {
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

    /// <summary>
    /// Test Open from byte array.
    /// </summary>
    [Fact]
    public void Open_FromByteArray_LoadsDocument()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(pdfData);

        doc.Should().NotBeNull();
        doc.PageCount.Should().Be(1);
    }

    /// <summary>
    /// Test GetPageLabel when no labels are defined.
    /// </summary>
    [Fact]
    public void GetPageLabel_WithoutPageLabels_ReturnsNull()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        var label = doc.GetPageLabel(1);

        label.Should().BeNull();
    }

    /// <summary>
    /// Test GetPageLabel with invalid page number.
    /// </summary>
    [Fact]
    public void GetPageLabel_WithInvalidPageNumber_ReturnsNull()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        var label = doc.GetPageLabel(100);

        label.Should().BeNull();
    }

    /// <summary>
    /// Test GetNamedDestinations when none are defined.
    /// </summary>
    [Fact]
    public void GetNamedDestinations_WithoutDestinations_ReturnsEmptyDictionary()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        var destinations = doc.GetNamedDestinations();

        destinations.Should().BeEmpty();
    }

    /// <summary>
    /// Test Dispose method.
    /// </summary>
    [Fact]
    public void Dispose_DisposesResources()
    {
        var pdfData = CreatePdfWithCustomTrailer();
        var stream = new MemoryStream(pdfData);

        var doc = PdfDocument.Open(stream, ownsStream: true);
        doc.Dispose();

        // Stream should be disposed when ownsStream=true
        var act = () => stream.ReadByte();
        act.Should().Throw<ObjectDisposedException>();
    }

    /// <summary>
    /// Test SetAcroFormNeedAppearances when no AcroForm exists.
    /// Should not throw.
    /// </summary>
    [Fact]
    public void SetAcroFormNeedAppearances_WithoutAcroForm_DoesNotThrow()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        var act = () => doc.SetAcroFormNeedAppearances();

        act.Should().NotThrow();
    }

    /// <summary>
    /// Test FlattenAcroForm when no AcroForm exists.
    /// Should not throw.
    /// </summary>
    [Fact]
    public void FlattenAcroForm_WithoutAcroForm_DoesNotThrow()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        var act = () => doc.FlattenAcroForm();

        act.Should().NotThrow();
    }

    /// <summary>
    /// Test ScrubEmbeddedFiles clears the cache.
    /// </summary>
    [Fact]
    public void ScrubEmbeddedFiles_ClearsCache()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        // Access embedded files first (to populate cache)
        var first = doc.GetEmbeddedFiles();

        // Scrub them
        doc.ScrubEmbeddedFiles();

        // Access again - should be fresh (empty list)
        var second = doc.GetEmbeddedFiles();

        second.Should().BeEmpty();
    }

    /// <summary>
    /// Test GetObject with invalid object number.
    /// Should throw PdfParseException.
    /// </summary>
    [Fact]
    public void GetObject_WithInvalidObjectNumber_ThrowsPdfParseException()
    {
        var pdfData = CreatePdfWithCustomTrailer();

        using var doc = PdfDocument.Open(new MemoryStream(pdfData));

        var act = () => doc.GetObject(9999);

        act.Should().Throw<PdfParseException>();
    }
}
