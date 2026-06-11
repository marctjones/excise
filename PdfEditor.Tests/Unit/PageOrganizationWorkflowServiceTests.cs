using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PdfEditor.Services;
using PdfEditor.Tests.Utilities;
using Pdfe.Core.Document;
using Pdfe.Core.Text;
using System.Collections.Generic;
using Xunit;

namespace PdfEditor.Tests.Unit;

public sealed class PageOrganizationWorkflowServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"pdfe-page-workflow-service-{Guid.NewGuid():N}");

    public PageOrganizationWorkflowServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task MovePageAsync_ReordersLoadedDocument()
    {
        var sourcePath = Path.Combine(_tempDir, "source.pdf");
        var outputPath = Path.Combine(_tempDir, "moved.pdf");
        TestPdfGenerator.CreateMultiPagePdf(sourcePath, pageCount: 3);
        var documentService = CreateLoadedDocumentService(sourcePath);
        var dialog = new RecordingDialogService();
        var workflow = CreateWorkflow(documentService, dialog);

        var result = await workflow.MovePageAsync(fromIndex: 0, toIndex: 2);

        result.DidChange.Should().BeTrue();
        result.CurrentPageIndex.Should().Be(2);
        dialog.Messages.Should().BeEmpty();

        documentService.SaveDocument(outputPath);
        using var reopened = PdfDocument.Open(File.ReadAllBytes(outputPath));
        new TextExtractor(reopened.GetPage(3)).ExtractText().Should().Contain("Page 1 Content");
    }

    [Fact]
    public async Task RemovePageAsync_AdjustsCurrentPageToLastRemainingPage()
    {
        var sourcePath = Path.Combine(_tempDir, "source.pdf");
        TestPdfGenerator.CreateMultiPagePdf(sourcePath, pageCount: 2);
        var documentService = CreateLoadedDocumentService(sourcePath);
        var workflow = CreateWorkflow(documentService, new RecordingDialogService());

        var result = await workflow.RemovePageAsync(pageIndex: 1);

        result.DidChange.Should().BeTrue();
        result.CurrentPageIndex.Should().Be(0);
        documentService.PageCount.Should().Be(1);
    }

    [Fact]
    public async Task InsertPagesFromFileAsync_ShowsPreservationWarningForAcroFormDocument()
    {
        var targetPath = Path.Combine(_tempDir, "target.pdf");
        var insertPath = Path.Combine(_tempDir, "insert.pdf");
        CreatePdfWithTextField(targetPath);
        TestPdfGenerator.CreateSimpleTextPdf(insertPath, "Inserted");
        var documentService = CreateLoadedDocumentService(targetPath);
        var dialog = new RecordingDialogService();
        var workflow = CreateWorkflow(documentService, dialog);

        var result = await workflow.InsertPagesFromFileAsync(insertPath, insertAtIndex: 1);

        result.DidChange.Should().BeTrue();
        documentService.PageCount.Should().Be(2);
        dialog.Messages.Should().ContainSingle();
        dialog.Messages[0].Title.Should().Be("Page Organization");
        dialog.Messages[0].Message.Should().Contain("AcroForm");
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

    private static PdfDocumentService CreateLoadedDocumentService(string path)
    {
        var service = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        service.LoadDocument(path);
        return service;
    }

    private static PageOrganizationWorkflowService CreateWorkflow(
        PdfDocumentService documentService,
        IUserDialogService dialog) =>
        new(
            documentService,
            dialog,
            NullLogger<PageOrganizationWorkflowService>.Instance);

    private static void CreatePdfWithTextField(string path)
    {
        using var document = PdfDocument.CreateNew();
        document.Pages.AddBlank();
        document.AddTextField(1, new PdfRectangle(72, 700, 300, 720), "Name");
        document.Save(path);
    }

    private sealed class RecordingDialogService : IUserDialogService
    {
        public List<(string Title, string Message)> Messages { get; } = new();

        public Task ShowMessageAsync(string title, string message)
        {
            Messages.Add((title, message));
            return Task.CompletedTask;
        }
    }
}
