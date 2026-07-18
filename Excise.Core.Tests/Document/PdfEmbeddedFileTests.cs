using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Tests for document-level embedded files (PDF 2.0 portfolios / associated files).
///
/// Why this matters: hybrid e-invoices (ZUGFeRD, Factur-X) bundle the same data as both
/// visible PDF content AND an embedded XML attachment. Legal exhibit packages bundle source
/// documents. Redacting the page leaves the attachment fully intact in /Catalog/Names/EmbeddedFiles.
/// We need read access to see what's bundled, and a scrub API to remove attachments during redaction.
/// </summary>
public class PdfEmbeddedFileTests
{
    [Fact]
    public void HasEmbeddedFiles_NoEmbeddedFilesPresent_ReturnsFalse()
    {
        var pdf = BuildPdfWithoutEmbeddedFiles();
        using var doc = PdfDocument.Open(pdf);

        doc.HasEmbeddedFiles.Should().BeFalse();
    }

    [Fact]
    public void GetEmbeddedFiles_NoEmbeddedFilesPresent_ReturnsEmptyList()
    {
        var pdf = BuildPdfWithoutEmbeddedFiles();
        using var doc = PdfDocument.Open(pdf);

        doc.GetEmbeddedFiles().Should().BeEmpty();
    }

    [Fact]
    public void GetEmbeddedFiles_SingleFileViaNameTree_ReturnsFileWithName()
    {
        var pdf = BuildPdfWithEmbeddedFile("invoice.xml", "<?xml version=\"1.0\"?><invoice/>");
        using var doc = PdfDocument.Open(pdf);

        var files = doc.GetEmbeddedFiles();
        files.Should().HaveCount(1);

        var file = files[0];
        file.Name.Should().Be("invoice.xml");
        file.FileName.Should().Be("invoice.xml");
        file.Bytes.Should().NotBeNull();
        Encoding.UTF8.GetString(file.Bytes!).Should().Contain("invoice");
    }

    [Fact]
    public void GetEmbeddedFiles_HasEmbeddedFiles_ReturnsTrue()
    {
        var pdf = BuildPdfWithEmbeddedFile("test.txt", "test data");
        using var doc = PdfDocument.Open(pdf);

        doc.HasEmbeddedFiles.Should().BeTrue();
    }

    [Fact]
    public void GetEmbeddedFiles_MultipleFiles_ReturnsAll()
    {
        var pdf = BuildPdfWithMultipleEmbeddedFiles(
            ("file1.txt", "content1"),
            ("file2.xml", "<data/>"),
            ("file3.pdf", "%PDF"));

        using var doc = PdfDocument.Open(pdf);

        var files = doc.GetEmbeddedFiles();
        files.Should().HaveCount(3);

        files[0].Name.Should().Be("file1.txt");
        files[1].Name.Should().Be("file2.xml");
        files[2].Name.Should().Be("file3.pdf");
    }

    [Fact]
    public void GetEmbeddedFiles_FileWithDescription_CapturesDescription()
    {
        var pdf = BuildPdfWithEmbeddedFileAndDescription(
            "document.pdf",
            "source document",
            "This is the original contract");

        using var doc = PdfDocument.Open(pdf);

        var files = doc.GetEmbeddedFiles();
        files.Should().HaveCount(1);
        files[0].Description.Should().Be("This is the original contract");
    }

    [Fact]
    public void GetEmbeddedFiles_FileWithMimeType_CapturesMimeType()
    {
        var pdf = BuildPdfWithEmbeddedFileAndMimeType(
            "data.xml",
            "<?xml version=\"1.0\"?>",
            "XML");

        using var doc = PdfDocument.Open(pdf);

        var files = doc.GetEmbeddedFiles();
        files.Should().HaveCount(1);
        files[0].MimeType.Should().Be("XML");
    }

    [Fact]
    public void GetEmbeddedFiles_FileWithCreationDate_CapturesDate()
    {
        var pdf = BuildPdfWithEmbeddedFileAndDates(
            "document.txt",
            "content",
            "D:20240115120000",
            "D:20240120140000");

        using var doc = PdfDocument.Open(pdf);

        var files = doc.GetEmbeddedFiles();
        files.Should().HaveCount(1);

        files[0].CreationDate.Should().NotBeNull();
        files[0].CreationDate!.Value.Year.Should().Be(2024);
        files[0].CreationDate!.Value.Month.Should().Be(1);
        files[0].CreationDate!.Value.Day.Should().Be(15);

        files[0].ModDate.Should().NotBeNull();
        files[0].ModDate!.Value.Day.Should().Be(20);
    }

    [Fact]
    public void GetEmbeddedFiles_NestedNameTree_ResolvesThroughKids()
    {
        // Build a name tree with /Kids (nested structure)
        var pdf = BuildPdfWithNestedNameTree("file1.txt", "file1content");
        using var doc = PdfDocument.Open(pdf);

        var files = doc.GetEmbeddedFiles();
        files.Should().HaveCount(1);
        files[0].Name.Should().Be("file1.txt");
    }

    [Fact]
    public void ScrubEmbeddedFiles_RemovesEmbeddedFiles()
    {
        var pdf = BuildPdfWithEmbeddedFile("secret.xml", "<secret/>");
        using var doc = PdfDocument.Open(pdf);

        doc.HasEmbeddedFiles.Should().BeTrue();
        doc.GetEmbeddedFiles().Should().HaveCount(1);

        doc.ScrubEmbeddedFiles();

        doc.GetEmbeddedFiles().Should().BeEmpty();
        doc.HasEmbeddedFiles.Should().BeFalse();
    }

    [Fact]
    public void ScrubEmbeddedFiles_IsIdempotent()
    {
        var pdf = BuildPdfWithEmbeddedFile("file.txt", "data");
        using var doc = PdfDocument.Open(pdf);

        doc.ScrubEmbeddedFiles();
        // Should not throw on second call
        doc.ScrubEmbeddedFiles();

        doc.GetEmbeddedFiles().Should().BeEmpty();
    }

    [Fact]
    public void ScrubEmbeddedFiles_OnDocumentWithoutEmbeddedFiles_DoesNotThrow()
    {
        var pdf = BuildPdfWithoutEmbeddedFiles();
        using var doc = PdfDocument.Open(pdf);

        var act = () => doc.ScrubEmbeddedFiles();
        act.Should().NotThrow();
    }

    [Fact]
    public void ScrubEmbeddedFiles_RoundTrip_SaveAndReload_StaysScrubbed()
    {
        var pdf = BuildPdfWithEmbeddedFile("secret.xml", "<confidential/>");

        byte[] scrubbed;
        using (var doc = PdfDocument.Open(pdf))
        {
            doc.ScrubEmbeddedFiles();
            using var ms = new MemoryStream();
            doc.Save(ms);
            scrubbed = ms.ToArray();
        }

        using var reopened = PdfDocument.Open(scrubbed);
        reopened.GetEmbeddedFiles().Should().BeEmpty();
        reopened.HasEmbeddedFiles.Should().BeFalse();
    }

    [Fact]
    public void GetEmbeddedFiles_Caching_SameInstanceReturned()
    {
        var pdf = BuildPdfWithEmbeddedFile("test.txt", "data");
        using var doc = PdfDocument.Open(pdf);

        var first = doc.GetEmbeddedFiles();
        var second = doc.GetEmbeddedFiles();

        first.Should().BeSameAs(second);
    }

    [Fact]
    public void ScrubMetadata_WithScrubAttachments_RemovesAttachments()
    {
        var pdf = BuildPdfWithMetadataAndEmbeddedFile(
            title: "Secret Title",
            embeddedFileName: "secret.xml",
            embeddedContent: "<secret/>");

        using var doc = PdfDocument.Open(pdf);

        doc.ScrubMetadata(scrubAttachments: true);

        doc.Title.Should().BeNull();
        doc.GetEmbeddedFiles().Should().BeEmpty();
    }

    [Fact]
    public void ScrubMetadata_WithoutScrubAttachments_LeavesAttachments()
    {
        var pdf = BuildPdfWithMetadataAndEmbeddedFile(
            title: "Secret Title",
            embeddedFileName: "keep.xml",
            embeddedContent: "<keep/>");

        using var doc = PdfDocument.Open(pdf);

        doc.ScrubMetadata(scrubAttachments: false);

        doc.Title.Should().BeNull();
        doc.GetEmbeddedFiles().Should().HaveCount(1);
        doc.GetEmbeddedFiles()[0].FileName.Should().Be("keep.xml");
    }

    [Fact]
    public void ScrubMetadata_DefaultParameter_ScrubsAttachments()
    {
        // Verify default behavior: ScrubMetadata() with no args scrubs attachments
        var pdf = BuildPdfWithEmbeddedFile("test.xml", "<data/>");
        using var doc = PdfDocument.Open(pdf);

        doc.ScrubMetadata(); // No parameter = default true

        doc.GetEmbeddedFiles().Should().BeEmpty();
    }

    [Fact]
    public void GetEmbeddedFiles_MalformedNameTree_DoesNotThrow()
    {
        // Build PDF with broken /Names/EmbeddedFiles (no /Names array in leaf)
        var pdf = BuildPdfWithMalformedNameTree();
        using var doc = PdfDocument.Open(pdf);

        var act = () => doc.GetEmbeddedFiles();
        act.Should().NotThrow();

        // Should return empty list gracefully
        doc.GetEmbeddedFiles().Should().BeEmpty();
    }

    [Fact]
    public void GetEmbeddedFiles_RawDictionary_IsPreserved()
    {
        var pdf = BuildPdfWithEmbeddedFile("file.txt", "content");
        using var doc = PdfDocument.Open(pdf);

        var files = doc.GetEmbeddedFiles();
        files.Should().HaveCount(1);

        // RawDictionary should contain the file specification dict
        files[0].RawDictionary.Should().NotBeNull();
        files[0].RawDictionary.GetStringOrNull("F").Should().Be("file.txt");
    }

    [Fact]
    public void GetEmbeddedFiles_LegacyCatalogAFArray_ExtractsFiles()
    {
        // Legacy PDF 1.7: /Catalog/AF array (not /Names/EmbeddedFiles)
        var pdf = BuildPdfWithLegacyAFArray("legacy.txt", "legacy content");
        using var doc = PdfDocument.Open(pdf);

        var files = doc.GetEmbeddedFiles();
        files.Should().HaveCount(1);
        files[0].Name.Should().Be("_AF_0");  // Generated name for legacy array
        files[0].FileName.Should().Be("legacy.txt");
        Encoding.UTF8.GetString(files[0].Bytes!).Should().Be("legacy content");
    }

    [Fact]
    public void GetEmbeddedFiles_LegacyNamesAFArray_ExtractsFiles()
    {
        // Legacy PDF 1.7: /Catalog/Names/AF array (falls back after /EmbeddedFiles check)
        var pdf = BuildPdfWithLegacyNamesAFArray("alt.xml", "<data/>");
        using var doc = PdfDocument.Open(pdf);

        var files = doc.GetEmbeddedFiles();
        files.Should().HaveCount(1);
        files[0].FileName.Should().Be("alt.xml");
    }

    [Fact]
    public void GetEmbeddedFiles_DateWithPositiveTimezoneOffset_ParsesCorrectly()
    {
        // Test date with +HH'mm' timezone offset format
        // D:20240115120000+05'30' = 2024-01-15 12:00:00 +05:30
        var pdf = BuildPdfWithEmbeddedFileAndDates(
            "document.txt",
            "content",
            "D:20240115120000+05'30'",  // +05:30 offset
            "D:20240120140000");

        using var doc = PdfDocument.Open(pdf);

        var files = doc.GetEmbeddedFiles();
        files.Should().HaveCount(1);

        files[0].CreationDate.Should().NotBeNull();
        var creationDate = files[0].CreationDate!.Value;
        creationDate.Year.Should().Be(2024);
        creationDate.Month.Should().Be(1);
        creationDate.Day.Should().Be(15);
        creationDate.Hour.Should().Be(12);
        creationDate.Offset.Should().Be(new TimeSpan(5, 30, 0));
    }

    [Fact]
    public void GetEmbeddedFiles_DateWithNegativeTimezoneOffset_ParsesCorrectly()
    {
        // Test date with -HH'mm' timezone offset format
        // D:20240120140000-08'00' = 2024-01-20 14:00:00 -08:00
        var pdf = BuildPdfWithEmbeddedFileAndDates(
            "document.txt",
            "content",
            "D:20240115120000",
            "D:20240120140000-08'00'");  // -08:00 offset

        using var doc = PdfDocument.Open(pdf);

        var files = doc.GetEmbeddedFiles();
        files.Should().HaveCount(1);

        files[0].ModDate.Should().NotBeNull();
        var modDate = files[0].ModDate!.Value;
        modDate.Year.Should().Be(2024);
        modDate.Month.Should().Be(1);
        modDate.Day.Should().Be(20);
        modDate.Offset.Should().Be(new TimeSpan(-8, 0, 0));
    }

    [Fact]
    public void GetEmbeddedFiles_DateWithoutTimezone_DefaultsToZero()
    {
        // Date without timezone should be treated as UTC (zero offset)
        var pdf = BuildPdfWithEmbeddedFileAndDates(
            "document.txt",
            "content",
            "D:20240115120000",  // No timezone
            "D:20240120140000");

        using var doc = PdfDocument.Open(pdf);

        var files = doc.GetEmbeddedFiles();
        files[0].CreationDate!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void GetEmbeddedFiles_DateTooShort_ReturnsNull()
    {
        // Date string shorter than YYYYMMDDHHMMSS should return null
        var pdf = BuildPdfWithEmbeddedFileAndDates(
            "document.txt",
            "content",
            "D:202401",  // Too short
            "D:20240120140000");

        using var doc = PdfDocument.Open(pdf);

        var files = doc.GetEmbeddedFiles();
        files[0].CreationDate.Should().BeNull();
    }

    [Fact]
    public void GetEmbeddedFiles_DateMissingDPrefix_StillParses()
    {
        // Date string without "D:" prefix should still parse (parser strips it)
        var pdf = BuildPdfWithEmbeddedFileAndDates(
            "document.txt",
            "content",
            "20240115120000",  // No "D:" prefix
            "D:20240120140000");

        using var doc = PdfDocument.Open(pdf);

        var files = doc.GetEmbeddedFiles();
        files[0].CreationDate.Should().NotBeNull();
        files[0].CreationDate!.Value.Year.Should().Be(2024);
    }

    // ────────────────────────────────────────────────────────────────────────

    private static byte[] BuildPdfWithoutEmbeddedFiles()
    {
        var sb = new StringBuilder();
        var offsets = new long[5];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");
        Mark(1);
        sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n");
        Mark(2);
        sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        Mark(3);
        sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<>>>> endobj\n");

        var xrefPos = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        for (int i = 1; i <= 3; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer <</Size 4/Root 1 0 R>>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithEmbeddedFile(string fileName, string content)
    {
        var sb = new StringBuilder();
        var offsets = new long[10];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");

        // Catalog with Names
        Mark(1);
        sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R/Names 4 0 R>> endobj\n");

        Mark(2);
        sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");

        Mark(3);
        sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<>>>> endobj\n");

        // Names dict with EmbeddedFiles
        Mark(4);
        sb.Append("4 0 obj <</EmbeddedFiles 5 0 R>> endobj\n");

        // EmbeddedFiles name tree (leaf)
        Mark(5);
        sb.Append("5 0 obj <</Names[");
        sb.Append($"({fileName}) 6 0 R");
        sb.Append("]>> endobj\n");

        // File specification dict
        Mark(6);
        sb.Append($"6 0 obj <</Type/Filespec/F({fileName})/EF<</F 7 0 R>>>> endobj\n");

        // Embedded file stream
        Mark(7);
        var fileBytes = Encoding.UTF8.GetBytes(content);
        sb.Append("7 0 obj <</Type/EmbeddedFile/Length ").Append(fileBytes.Length).Append(">>\nstream\n");
        sb.Append(content);
        sb.Append("\nendstream endobj\n");

        var xrefPos = sb.Length;
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        for (int i = 1; i <= 7; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");

        sb.Append("trailer <</Size 8/Root 1 0 R>>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithMultipleEmbeddedFiles(params (string name, string content)[] files)
    {
        var sb = new StringBuilder();
        var offsets = new long[20];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");

        Mark(1);
        sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R/Names 4 0 R>> endobj\n");

        Mark(2);
        sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");

        Mark(3);
        sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<>>>> endobj\n");

        Mark(4);
        sb.Append("4 0 obj <</EmbeddedFiles 5 0 R>> endobj\n");

        Mark(5);
        sb.Append("5 0 obj <</Names[");
        for (int i = 0; i < files.Length; i++)
        {
            if (i > 0) sb.Append(" ");
            sb.Append($"({files[i].name}) {6 + i} 0 R");
        }
        sb.Append("]>> endobj\n");

        // File specs and streams
        for (int i = 0; i < files.Length; i++)
        {
            Mark(6 + i);
            sb.Append($"{6 + i} 0 obj <</Type/Filespec/F({files[i].name})/EF<</F {6 + files.Length + i} 0 R>>>> endobj\n");
        }

        for (int i = 0; i < files.Length; i++)
        {
            Mark(6 + files.Length + i);
            var fileBytes = Encoding.UTF8.GetBytes(files[i].content);
            sb.Append($"{6 + files.Length + i} 0 obj <</Type/EmbeddedFile/Length {fileBytes.Length}>>\nstream\n");
            sb.Append(files[i].content);
            sb.Append("\nendstream endobj\n");
        }

        var xrefPos = sb.Length;
        int objCount = 6 + 2 * files.Length;
        sb.Append("xref\n0 ").Append(objCount).Append("\n0000000000 65535 f \n");
        for (int i = 1; i < objCount; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");

        sb.Append("trailer <</Size ").Append(objCount).Append("/Root 1 0 R>>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithEmbeddedFileAndDescription(
        string fileName, string content, string description)
    {
        var sb = new StringBuilder();
        var offsets = new long[10];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");

        Mark(1);
        sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R/Names 4 0 R>> endobj\n");

        Mark(2);
        sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");

        Mark(3);
        sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<>>>> endobj\n");

        Mark(4);
        sb.Append("4 0 obj <</EmbeddedFiles 5 0 R>> endobj\n");

        Mark(5);
        sb.Append($"5 0 obj <</Names[({fileName}) 6 0 R]>> endobj\n");

        Mark(6);
        sb.Append($"6 0 obj <</Type/Filespec/F({fileName})/Desc({description})/EF<</F 7 0 R>>>> endobj\n");

        Mark(7);
        var fileBytes = Encoding.UTF8.GetBytes(content);
        sb.Append("7 0 obj <</Type/EmbeddedFile/Length ").Append(fileBytes.Length).Append(">>\nstream\n");
        sb.Append(content);
        sb.Append("\nendstream endobj\n");

        var xrefPos = sb.Length;
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        for (int i = 1; i <= 7; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");

        sb.Append("trailer <</Size 8/Root 1 0 R>>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithEmbeddedFileAndMimeType(
        string fileName, string content, string mimeType)
    {
        var sb = new StringBuilder();
        var offsets = new long[10];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");

        Mark(1);
        sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R/Names 4 0 R>> endobj\n");

        Mark(2);
        sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");

        Mark(3);
        sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<>>>> endobj\n");

        Mark(4);
        sb.Append("4 0 obj <</EmbeddedFiles 5 0 R>> endobj\n");

        Mark(5);
        sb.Append($"5 0 obj <</Names[({fileName}) 6 0 R]>> endobj\n");

        Mark(6);
        sb.Append($"6 0 obj <</Type/Filespec/F({fileName})/EF<</F 7 0 R>>>> endobj\n");

        Mark(7);
        var fileBytes = Encoding.UTF8.GetBytes(content);
        // Subtype is a Name object in PDF (e.g., /XML, not a string)
        sb.Append("7 0 obj <</Type/EmbeddedFile/Subtype/").Append(mimeType).Append("/Length ")
          .Append(fileBytes.Length).Append(">>\nstream\n");
        sb.Append(content);
        sb.Append("\nendstream endobj\n");

        var xrefPos = sb.Length;
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        for (int i = 1; i <= 7; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");

        sb.Append("trailer <</Size 8/Root 1 0 R>>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithEmbeddedFileAndDates(
        string fileName, string content, string creationDate, string modDate)
    {
        var sb = new StringBuilder();
        var offsets = new long[10];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");

        Mark(1);
        sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R/Names 4 0 R>> endobj\n");

        Mark(2);
        sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");

        Mark(3);
        sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<>>>> endobj\n");

        Mark(4);
        sb.Append("4 0 obj <</EmbeddedFiles 5 0 R>> endobj\n");

        Mark(5);
        sb.Append($"5 0 obj <</Names[({fileName}) 6 0 R]>> endobj\n");

        Mark(6);
        sb.Append($"6 0 obj <</Type/Filespec/F({fileName})/EF<</F 7 0 R>>>> endobj\n");

        Mark(7);
        var fileBytes = Encoding.UTF8.GetBytes(content);
        sb.Append("7 0 obj <</Type/EmbeddedFile/Length ").Append(fileBytes.Length)
          .Append("/Params<</CreationDate(").Append(creationDate).Append(")/ModDate(").Append(modDate)
          .Append(")>>>>\nstream\n");
        sb.Append(content);
        sb.Append("\nendstream endobj\n");

        var xrefPos = sb.Length;
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        for (int i = 1; i <= 7; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");

        sb.Append("trailer <</Size 8/Root 1 0 R>>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithNestedNameTree(string fileName, string content)
    {
        var sb = new StringBuilder();
        var offsets = new long[12];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");

        Mark(1);
        sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R/Names 4 0 R>> endobj\n");

        Mark(2);
        sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");

        Mark(3);
        sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<>>>> endobj\n");

        Mark(4);
        sb.Append("4 0 obj <</EmbeddedFiles 5 0 R>> endobj\n");

        // Root of name tree with /Kids
        Mark(5);
        sb.Append("5 0 obj <</Kids[6 0 R]>> endobj\n");

        // Leaf of name tree with /Names
        Mark(6);
        sb.Append($"6 0 obj <</Names[({fileName}) 7 0 R]>> endobj\n");

        // File spec
        Mark(7);
        sb.Append($"7 0 obj <</Type/Filespec/F({fileName})/EF<</F 8 0 R>>>> endobj\n");

        // Embedded file stream
        Mark(8);
        var fileBytes = Encoding.UTF8.GetBytes(content);
        sb.Append("8 0 obj <</Type/EmbeddedFile/Length ").Append(fileBytes.Length).Append(">>\nstream\n");
        sb.Append(content);
        sb.Append("\nendstream endobj\n");

        var xrefPos = sb.Length;
        sb.Append("xref\n0 9\n0000000000 65535 f \n");
        for (int i = 1; i <= 8; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");

        sb.Append("trailer <</Size 9/Root 1 0 R>>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithMalformedNameTree()
    {
        var sb = new StringBuilder();
        var offsets = new long[10];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");

        Mark(1);
        sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R/Names 4 0 R>> endobj\n");

        Mark(2);
        sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");

        Mark(3);
        sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<>>>> endobj\n");

        Mark(4);
        sb.Append("4 0 obj <</EmbeddedFiles 5 0 R>> endobj\n");

        // Broken name tree: no /Names key (missing the array)
        Mark(5);
        sb.Append("5 0 obj <</Kids[6 0 R]>> endobj\n");

        // Another node without /Names (should just be skipped)
        Mark(6);
        sb.Append("6 0 obj <</SomeOtherKey 123>> endobj\n");

        var xrefPos = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        for (int i = 1; i <= 6; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");

        sb.Append("trailer <</Size 7/Root 1 0 R>>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithMetadataAndEmbeddedFile(
        string title, string embeddedFileName, string embeddedContent)
    {
        var sb = new StringBuilder();
        var offsets = new long[12];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");

        Mark(1);
        sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R/Names 4 0 R>> endobj\n");

        Mark(2);
        sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");

        Mark(3);
        sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<>>>> endobj\n");

        Mark(4);
        sb.Append("4 0 obj <</EmbeddedFiles 5 0 R>> endobj\n");

        Mark(5);
        sb.Append($"5 0 obj <</Names[({embeddedFileName}) 6 0 R]>> endobj\n");

        Mark(6);
        sb.Append($"6 0 obj <</Type/Filespec/F({embeddedFileName})/EF<</F 7 0 R>>>> endobj\n");

        Mark(7);
        var fileBytes = Encoding.UTF8.GetBytes(embeddedContent);
        sb.Append("7 0 obj <</Type/EmbeddedFile/Length ").Append(fileBytes.Length).Append(">>\nstream\n");
        sb.Append(embeddedContent);
        sb.Append("\nendstream endobj\n");

        // Info dict
        Mark(8);
        sb.Append($"8 0 obj <</Title({title})>> endobj\n");

        var xrefPos = sb.Length;
        sb.Append("xref\n0 9\n0000000000 65535 f \n");
        for (int i = 1; i <= 8; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");

        sb.Append("trailer <</Size 9/Root 1 0 R/Info 8 0 R>>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithLegacyAFArray(string fileName, string content)
    {
        var sb = new StringBuilder();
        var offsets = new long[10];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");

        // Catalog with legacy /AF array (not /Names/EmbeddedFiles)
        Mark(1);
        sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R/AF[4 0 R]>> endobj\n");

        Mark(2);
        sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");

        Mark(3);
        sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<>>>> endobj\n");

        // File specification dict
        Mark(4);
        sb.Append($"4 0 obj <</Type/Filespec/F({fileName})/EF<</F 5 0 R>>>> endobj\n");

        // Embedded file stream
        Mark(5);
        var fileBytes = Encoding.UTF8.GetBytes(content);
        sb.Append("5 0 obj <</Type/EmbeddedFile/Length ").Append(fileBytes.Length).Append(">>\nstream\n");
        sb.Append(content);
        sb.Append("\nendstream endobj\n");

        var xrefPos = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");

        sb.Append("trailer <</Size 6/Root 1 0 R>>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithLegacyNamesAFArray(string fileName, string content)
    {
        var sb = new StringBuilder();
        var offsets = new long[10];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");

        // Catalog with /Names dict containing /AF array
        Mark(1);
        sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R/Names 4 0 R>> endobj\n");

        Mark(2);
        sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");

        Mark(3);
        sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<>>>> endobj\n");

        // Names dict with /AF array (no /EmbeddedFiles, so parser falls back to /AF)
        Mark(4);
        sb.Append("4 0 obj <</AF[5 0 R]>> endobj\n");

        // File specification dict
        Mark(5);
        sb.Append($"5 0 obj <</Type/Filespec/F({fileName})/EF<</F 6 0 R>>>> endobj\n");

        // Embedded file stream
        Mark(6);
        var fileBytes = Encoding.UTF8.GetBytes(content);
        sb.Append("6 0 obj <</Type/EmbeddedFile/Length ").Append(fileBytes.Length).Append(">>\nstream\n");
        sb.Append(content);
        sb.Append("\nendstream endobj\n");

        var xrefPos = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        for (int i = 1; i <= 6; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");

        sb.Append("trailer <</Size 7/Root 1 0 R>>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
