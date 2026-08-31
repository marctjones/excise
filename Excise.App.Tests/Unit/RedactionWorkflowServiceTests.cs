using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Services;
using Excise.Core.Document;
using Excise.Core.Editing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.Unit;

public sealed class RedactionWorkflowServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"excise-redaction-workflow-{Guid.NewGuid():N}");

    public RedactionWorkflowServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void CaptureMark_ExtractsPreviewWithoutOwningPendingUiState()
    {
        var sourcePath = Path.Combine(_tempDir, "preview.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "PREVIEWSECRET1288");
        var pageArea = PdfPageRect.FromContentPoints(
            1,
            new PdfRectangle(0, 0, 612, 792));

        var result = CreateWorkflow().CaptureMark(
            new RedactionMarkRequest(sourcePath, 0, pageArea));

        result.PageArea.Should().Be(pageArea);
        Assert.Contains("PREVIEWSECRET1288", result.PreviewText);
    }

    [Fact]
    public void RequestCapture_CopiesMutablePendingState()
    {
        var pending = new PendingRedaction
        {
            PageNumber = 1,
            PageArea = PdfPageRect.FromContentPoints(1, new PdfRectangle(10, 20, 30, 40)),
            PreviewText = "original"
        };

        using var document = PdfDocument.CreateNew();
        document.Pages.AddBlank();
        var request = RedactionApplicationRequest.Capture(
            document,
            new[] { pending },
            Array.Empty<PdfTypewriterTextOperation>());
        pending.PageNumber = 2;
        pending.PreviewText = "changed";

        var transaction = request.Redactions.Single();
        transaction.PageNumber.Should().Be(1);
        Assert.Equal("original", transaction.PreviewText);
    }

    [Fact]
    public void ApplyToDocument_ReportsInvalidPagesInsteadOfIndexingOutsideDocument()
    {
        var sourcePath = Path.Combine(_tempDir, "invalid-page.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "SURVIVES");
        using var document = PdfDocument.Open(sourcePath);
        var invalid = new RedactionAreaTransaction(
            99,
            PdfPageRect.FromContentPoints(99, new PdfRectangle(0, 0, 100, 100)),
            "missing page");

        var result = CreateWorkflow().ApplyToDocument(
            new RedactionApplicationRequest(
                document,
                new[] { invalid },
                Array.Empty<PdfTypewriterTextOperation>()));

        result.AppliedRedactionCount.Should().Be(0);
        result.SkippedRedactionCount.Should().Be(1);
        result.SafetyReport.Warnings.Should().ContainSingle(
            warning => warning.Contains("skipped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateRedactedCopy_AppliesAreasAndPendingTypewriterBeforeSafeSave()
    {
        var sourcePath = Path.Combine(_tempDir, "source.pdf");
        var outputPath = Path.Combine(_tempDir, "redacted.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "REMOVESECRET1288");
        using var document = PdfDocument.Open(sourcePath);
        var redaction = new RedactionAreaTransaction(
            1,
            PdfPageRect.FromContentPoints(1, new PdfRectangle(0, 0, 612, 792)),
            "REMOVESECRET1288");
        var typewriter = PdfTypewriterTextOperation.Create(
            1,
            new PdfRectangle(72, 620, 300, 660),
            "TYPEWRITER1288");

        var result = CreateWorkflow().CreateRedactedCopy(
            new RedactedCopyRequest(
                new RedactionApplicationRequest(
                    document,
                    new[] { redaction },
                    new[] { typewriter }),
                outputPath,
                EncryptionOptions: null));

        result.Application.AppliedRedactionCount.Should().Be(1);
        result.Application.SkippedRedactionCount.Should().Be(0);
        result.Application.AppliedTypewriterOperationCount.Should().Be(1);
        File.Exists(outputPath).Should().BeTrue();
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(sourcePath), "REMOVESECRET1288")
            .Should().NotBeEmpty("the independent scanner's negative control must detect the source secret");
        SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(outputPath), "REMOVESECRET1288")
            .Should().BeEmpty("no saved carrier may retain the redacted secret");
        using var reopened = PdfDocument.Open(outputPath);
        reopened.GetPage(1).Text.Should().NotContain("REMOVESECRET1288");
        reopened.GetPage(1).Text.Should().Contain("TYPEWRITER1288");
    }

    public void Dispose()
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

    private static RedactionWorkflowService CreateWorkflow()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        return new RedactionWorkflowService(
            new RedactionService(NullLogger<RedactionService>.Instance, loggerFactory),
            new PdfTextExtractionService(NullLogger<PdfTextExtractionService>.Instance),
            NullLogger<RedactionWorkflowService>.Instance);
    }
}
