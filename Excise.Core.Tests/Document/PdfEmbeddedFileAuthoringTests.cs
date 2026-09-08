using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Tests for the embedded-file (attachment) authoring API added for #1413 —
/// PdfEmbeddedFileParser/document.GetEmbeddedFiles() previously only read
/// /Catalog/Names/EmbeddedFiles, there was no way to add an attachment.
/// </summary>
public class PdfEmbeddedFileAuthoringTests
{
    private static byte[] BarePdf()
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

    [Fact]
    public void AddEmbeddedFile_OnPdfWithoutAttachments_CreatesNameTreeAndFile()
    {
        using var doc = PdfDocument.Open(BarePdf());
        doc.HasEmbeddedFiles.Should().BeFalse("baseline: no attachments yet");

        doc.AddEmbeddedFile("notes.txt", Encoding.UTF8.GetBytes("hello attachment"), mimeType: "text/plain");

        doc.HasEmbeddedFiles.Should().BeTrue();
        var files = doc.GetEmbeddedFiles();
        files.Should().HaveCount(1);
        files[0].FileName.Should().Be("notes.txt");
        files[0].MimeType.Should().Be("text/plain");
        Encoding.UTF8.GetString(files[0].Bytes!).Should().Be("hello attachment");
    }

    [Fact]
    public void AddEmbeddedFile_Twice_KeepsBothAndSortsByName()
    {
        using var doc = PdfDocument.Open(BarePdf());

        doc.AddEmbeddedFile("zebra.txt", Encoding.UTF8.GetBytes("z"));
        doc.AddEmbeddedFile("alpha.txt", Encoding.UTF8.GetBytes("a"));

        var files = doc.GetEmbeddedFiles();
        files.Should().HaveCount(2, "adding a second attachment must not discard the first");
        files.Select(f => f.FileName).Should().Equal("alpha.txt", "zebra.txt");
    }

    [Fact]
    public void AddEmbeddedFile_WithDescription_SurvivesASaveAndReload()
    {
        using var doc = PdfDocument.Open(BarePdf());
        doc.AddEmbeddedFile("invoice.xml", Encoding.UTF8.GetBytes("<invoice/>"),
            mimeType: "application/xml", description: "ZUGFeRD invoice");

        var saved = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(saved);
        var files = reopened.GetEmbeddedFiles();

        files.Should().HaveCount(1);
        files[0].FileName.Should().Be("invoice.xml");
        files[0].Description.Should().Be("ZUGFeRD invoice");
        files[0].MimeType.Should().Be("application/xml");
        Encoding.UTF8.GetString(files[0].Bytes!).Should().Be("<invoice/>");
    }

    [Fact]
    public void AddEmbeddedFile_OnPdfWithExistingAttachment_AppendsRatherThanReplacing()
    {
        using var doc = PdfDocument.Open(BarePdf());
        doc.AddEmbeddedFile("first.txt", Encoding.UTF8.GetBytes("1"));

        doc.AddEmbeddedFile("second.txt", Encoding.UTF8.GetBytes("2"));

        var saved = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(saved);
        reopened.GetEmbeddedFiles().Should().HaveCount(2);
    }

    [Fact]
    public void AddEmbeddedFile_ThenScrub_RemovesIt()
    {
        using var doc = PdfDocument.Open(BarePdf());
        doc.AddEmbeddedFile("secret.txt", Encoding.UTF8.GetBytes("shh"));
        doc.HasEmbeddedFiles.Should().BeTrue();

        doc.ScrubEmbeddedFiles();

        doc.HasEmbeddedFiles.Should().BeFalse();
        doc.GetEmbeddedFiles().Should().BeEmpty();
    }
}
