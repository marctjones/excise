using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PdfEditor.Services;
using PdfEditor.Tests.Utilities;
using PdfEditor.ViewModels;
using Pdfe.Core.Document;
using Pdfe.Core.Text;
using Xunit;

namespace PdfEditor.Tests.UI;

public class PageOrganizationWorkflowTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"pdfe-page-workflow-{Guid.NewGuid():N}");

    public PageOrganizationWorkflowTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task MoveCurrentPageAsync_ReordersDocumentAndMarksDirty()
    {
        var filePath = Path.Combine(_tempDir, "source.pdf");
        TestPdfGenerator.CreateMultiPagePdf(filePath, pageCount: 3);

        var documentService = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        documentService.LoadDocument(filePath);
        var loggerFactory = NullLoggerFactory.Instance;
        var vm = new MainWindowViewModel(
            NullLogger<MainWindowViewModel>.Instance,
            loggerFactory,
            documentService,
            new PdfRenderService(NullLogger<PdfRenderService>.Instance),
            new RedactionService(NullLogger<RedactionService>.Instance, loggerFactory),
            new PdfTextExtractionService(NullLogger<PdfTextExtractionService>.Instance),
            new PdfSearchService(NullLogger<PdfSearchService>.Instance),
            new SignatureVerificationService(NullLogger<SignatureVerificationService>.Instance),
            new FilenameSuggestionService(),
            new ToastService(),
            dialogService: new NullUserDialogService());
        vm.FileState.SetDocument(filePath);

        await vm.MoveCurrentPageAsync(1);

        vm.FileState.HasUnsavedChanges.Should().BeTrue();
        documentService.SaveDocument(Path.Combine(_tempDir, "reordered.pdf"));
        using var reopened = PdfDocument.Open(File.ReadAllBytes(Path.Combine(_tempDir, "reordered.pdf")));
        new TextExtractor(reopened.GetPage(1)).ExtractText().Should().Contain("Page 2 Content");
    }

    [Fact]
    public void PageOrganizationCommands_AreAvailableForMenuAndKeyboardCoverage()
    {
        var vm = new MainWindowViewModel();

        vm.InsertPagesBeforeCurrentCommand.Should().NotBeNull();
        vm.InsertPagesAfterCurrentCommand.Should().NotBeNull();
        vm.ExtractCurrentPageCommand.Should().NotBeNull();
        vm.ExtractSelectedPagesCommand.Should().NotBeNull();
        vm.RemoveSelectedPagesCommand.Should().NotBeNull();
        vm.MoveSelectedPagesEarlierCommand.Should().NotBeNull();
        vm.MoveSelectedPagesLaterCommand.Should().NotBeNull();
        vm.ClearSelectedPagesCommand.Should().NotBeNull();
        vm.MoveCurrentPageEarlierCommand.Should().NotBeNull();
        vm.MoveCurrentPageLaterCommand.Should().NotBeNull();
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
}
