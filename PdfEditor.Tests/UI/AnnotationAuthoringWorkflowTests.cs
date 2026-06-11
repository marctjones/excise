using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PdfEditor.Services;
using PdfEditor.ViewModels;
using Pdfe.Core.Document;
using Xunit;

namespace PdfEditor.Tests.UI;

public class AnnotationAuthoringWorkflowTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"pdfe-annotation-authoring-{Guid.NewGuid():N}");

    public AnnotationAuthoringWorkflowTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void BasicAnnotationAuthoring_CreatesRealPdfAnnotations()
    {
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.AddTextAnnotation(1, new PdfRectangle(72, 700, 108, 736), "Office note");
            doc.AddHighlightAnnotation(1, new PdfRectangle(100, 650, 260, 670), "Office highlight");
            saved = doc.SaveToBytes();
        }

        using var reopened = PdfDocument.Open(saved);
        var annotations = reopened.GetPage(1).GetAnnotations();

        annotations.Should().Contain(a => a.Subtype == PdfAnnotationSubtype.Text && a.Contents == "Office note");
        annotations.Should().Contain(a => a.Subtype == PdfAnnotationSubtype.Highlight && a.Contents == "Office highlight");
    }

    [Fact]
    public void AnnotationCommands_AreAvailableForToolbarAndMenuCoverage()
    {
        var vm = new MainWindowViewModel();

        vm.AddHighlightAnnotationFromSelectionCommand.Should().NotBeNull();
        vm.AddStickyNoteAnnotationCommand.Should().NotBeNull();
    }

    [Fact]
    public async Task AddHighlightAnnotationFromSelectionAsync_CreatesPersistableHighlightAndRefreshesViewerDocument()
    {
        var filePath = CreateBlankPdf("highlight-source.pdf");
        var outputPath = Path.Combine(_tempDir, "highlight-output.pdf");
        var documentService = CreateLoadedDocumentService(filePath);
        var vm = CreateViewModel(documentService, filePath);
        using var viewerDocument = PdfDocument.Open(filePath);
        vm.PdfCoreDocument = viewerDocument;
        vm.CurrentTextSelectionPageArea = PdfPageRect.ViewerDips(
            1,
            x: 120,
            y: 120,
            width: 180,
            height: 30,
            renderDpi: MainWindowViewModel.DefaultViewerRenderDpi);
        vm.SelectedText = "Selected clause";

        await vm.AddHighlightAnnotationFromSelectionAsync();

        vm.FileState.AnnotationEditsCount.Should().Be(1);
        vm.FileState.HasUnsavedChanges.Should().BeTrue();
        viewerDocument.GetPage(1).GetAnnotations()
            .Should().Contain(a => a.Subtype == PdfAnnotationSubtype.Highlight && a.Contents == "Selected clause");

        documentService.SaveDocument(outputPath);
        using var reopened = PdfDocument.Open(File.ReadAllBytes(outputPath));
        reopened.GetPage(1).GetAnnotations()
            .Should().Contain(a => a.Subtype == PdfAnnotationSubtype.Highlight && a.Contents == "Selected clause");
    }

    [Fact]
    public async Task AddStickyNoteAnnotationAsync_CreatesPersistableStickyNote()
    {
        var filePath = CreateBlankPdf("sticky-source.pdf");
        var outputPath = Path.Combine(_tempDir, "sticky-output.pdf");
        var documentService = CreateLoadedDocumentService(filePath);
        var vm = CreateViewModel(documentService, filePath);

        await vm.AddStickyNoteAnnotationAsync("Office review note");

        vm.FileState.AnnotationEditsCount.Should().Be(1);
        vm.FileState.HasUnsavedChanges.Should().BeTrue();

        documentService.SaveDocument(outputPath);
        using var reopened = PdfDocument.Open(File.ReadAllBytes(outputPath));
        reopened.GetPage(1).GetAnnotations()
            .Should().Contain(a => a.Subtype == PdfAnnotationSubtype.Text && a.Contents == "Office review note");
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

    private string CreateBlankPdf(string fileName)
    {
        var path = Path.Combine(_tempDir, fileName);
        using var document = PdfDocument.CreateNew();
        document.Pages.AddBlank();
        document.Save(path);
        return path;
    }

    private static PdfDocumentService CreateLoadedDocumentService(string path)
    {
        var service = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        service.LoadDocument(path);
        return service;
    }

    private static MainWindowViewModel CreateViewModel(PdfDocumentService documentService, string filePath)
    {
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
        return vm;
    }
}
