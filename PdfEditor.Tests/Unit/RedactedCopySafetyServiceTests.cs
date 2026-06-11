using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Pdfe.Core.Document;
using PdfEditor.Models;
using PdfEditor.Services;
using PdfEditor.Tests.Utilities;
using System;
using System.IO;
using System.Text;
using Xunit;

namespace PdfEditor.Tests.Unit;

public class RedactedCopySafetyServiceTests : IDisposable
{
    private readonly RedactedCopySafetyService _service =
        new(NullLogger<RedactedCopySafetyService>.Instance);
    private readonly RedactionService _redactionService =
        new(NullLogger<RedactionService>.Instance, NullLoggerFactory.Instance);
    private readonly string _tempDir;

    public RedactedCopySafetyServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pdfe-redacted-copy-safety-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void PrepareRedactedCopy_AfterGlyphRedaction_VerifiesContentWithoutEchoingPreviewText()
    {
        var inputPath = Path.Combine(_tempDir, "redact.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(inputPath, "PUBLIC SECRET");

        using var document = PdfDocument.Open(File.ReadAllBytes(inputPath));
        var page = document.GetPage(1);
        _redactionService.RedactArea(
            page,
            PdfPageRect.FromContentPoints(1, new PdfRectangle(0, 0, page.Width, page.Height)));

        var pending = new[]
        {
            new PendingRedaction
            {
                PageNumber = 1,
                PageArea = PdfPageRect.FromContentPoints(1, new PdfRectangle(0, 0, page.Width, page.Height)),
                PreviewText = "SECRET"
            }
        };

        var report = _service.PrepareRedactedCopy(document, pending);
        var dialog = _service.FormatForDialog(Path.Combine(_tempDir, "redacted.pdf"), report);

        report.ContentVerificationStatus.Should().Be(RedactedContentVerificationStatus.Verified);
        report.RemainingSelectionPreviewCount.Should().Be(0);
        report.HiddenTextAuditStatus.Should().Be(RedactedContentVerificationStatus.Verified);
        page.Text.Should().NotContain("SECRET");
        dialog.Should().NotContain("SECRET");
    }

    [Fact]
    public void PrepareRedactedCopy_WhenPreviewTextStillExtracts_ReturnsWarningWithoutEchoingPreviewText()
    {
        var inputPath = Path.Combine(_tempDir, "warning.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(inputPath, "PUBLIC SECRET");

        using var document = PdfDocument.Open(File.ReadAllBytes(inputPath));
        var pending = new[]
        {
            new PendingRedaction
            {
                PageNumber = 1,
                PageArea = PdfPageRect.FromContentPoints(1, new PdfRectangle(0, 0, 1, 1)),
                PreviewText = "SECRET"
            }
        };

        var report = _service.PrepareRedactedCopy(document, pending);
        var dialog = _service.FormatForDialog(Path.Combine(_tempDir, "redacted.pdf"), report);

        report.ContentVerificationStatus.Should().Be(RedactedContentVerificationStatus.Warning);
        report.RemainingSelectionPreviewCount.Should().Be(1);
        report.Warnings.Should().Contain(w => w.Contains("captured selection preview"));
        dialog.Should().NotContain("SECRET");
    }

    [Fact]
    public void PrepareRedactedCopy_ScrubsInfoXmpAndEmbeddedFilesBeforeSave()
    {
        using var document = PdfDocument.Open(BuildPdfWithMetadataXmpAndEmbeddedFile(
            title: "SECRET title",
            xmpBody: "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">SECRET</x:xmpmeta>",
            embeddedFileName: "secret.xml",
            embeddedContent: "<secret>SECRET</secret>"));

        document.Title.Should().Contain("SECRET");
        document.GetXmpMetadata().Should().NotBeNull();
        document.GetEmbeddedFiles().Should().ContainSingle();

        var report = _service.PrepareRedactedCopy(document, Array.Empty<PendingRedaction>());
        var outputPath = Path.Combine(_tempDir, "scrubbed.pdf");
        document.Save(outputPath);

        report.MetadataScrubbed.Should().BeTrue();
        report.InfoFieldsScrubbed.Should().Be(1);
        report.HadXmpMetadata.Should().BeTrue();
        report.AttachmentsScrubbed.Should().BeTrue();
        report.EmbeddedFileCountBefore.Should().Be(1);

        using var reopened = PdfDocument.Open(File.ReadAllBytes(outputPath));
        reopened.Title.Should().BeNull();
        reopened.GetXmpMetadata().Should().BeNull();
        reopened.GetEmbeddedFiles().Should().BeEmpty();
    }

    void IDisposable.Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private static byte[] BuildPdfWithMetadataXmpAndEmbeddedFile(
        string title,
        string xmpBody,
        string embeddedFileName,
        string embeddedContent)
    {
        var sb = new StringBuilder();
        var offsets = new long[10];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");

        Mark(1);
        sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R/Names 4 0 R/Metadata 9 0 R>> endobj\n");

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
        sb.Append($"7 0 obj <</Type/EmbeddedFile/Length {fileBytes.Length}>>\nstream\n");
        sb.Append(embeddedContent);
        sb.Append("\nendstream endobj\n");

        Mark(8);
        sb.Append($"8 0 obj <</Title({title})>> endobj\n");

        Mark(9);
        var xmpBytes = Encoding.UTF8.GetBytes(xmpBody);
        sb.Append($"9 0 obj <</Type/Metadata/Subtype/XML/Length {xmpBytes.Length}>>\nstream\n");
        sb.Append(xmpBody);
        sb.Append("\nendstream endobj\n");

        var xrefPos = sb.Length;
        sb.Append("xref\n0 10\n0000000000 65535 f \n");
        for (var i = 1; i <= 9; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");

        sb.Append("trailer <</Size 10/Root 1 0 R/Info 8 0 R>>\nstartxref\n")
            .Append(xrefPos)
            .Append("\n%%EOF\n");

        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
