using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.Core.Document;
using Xunit;

namespace Excise.App.Tests.Unit;

public sealed class AnnotationWorkflowServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"excise-annotation-workflow-service-{Guid.NewGuid():N}");

    public AnnotationWorkflowServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void AddTextNote_CreatesPersistableStickyNoteAnnotation()
    {
        var sourcePath = CreateBlankPdf("source.pdf");
        var outputPath = Path.Combine(_tempDir, "text-note.pdf");
        var documentService = CreateLoadedDocumentService(sourcePath);
        var workflow = CreateWorkflow(documentService);

        var annotation = workflow.AddTextNote(1, new PdfRectangle(72, 700, 108, 736), "Review note");

        annotation.Subtype.Should().Be(PdfAnnotationSubtype.Text);
        annotation.Contents.Should().Be("Review note");

        documentService.SaveDocument(outputPath);
        using var reopened = PdfDocument.Open(File.ReadAllBytes(outputPath));
        reopened.GetPage(1).GetAnnotations()
            .Should().Contain(a => a.Subtype == PdfAnnotationSubtype.Text && a.Contents == "Review note");
    }

    [Fact]
    public void AddHighlight_CreatesPersistableHighlightAnnotation()
    {
        var sourcePath = CreateBlankPdf("source.pdf");
        var outputPath = Path.Combine(_tempDir, "highlight.pdf");
        var documentService = CreateLoadedDocumentService(sourcePath);
        var workflow = CreateWorkflow(documentService);

        var annotation = workflow.AddHighlight(1, new PdfRectangle(100, 650, 260, 670), "Review highlight");

        annotation.Subtype.Should().Be(PdfAnnotationSubtype.Highlight);
        annotation.Contents.Should().Be("Review highlight");

        documentService.SaveDocument(outputPath);
        using var reopened = PdfDocument.Open(File.ReadAllBytes(outputPath));
        reopened.GetPage(1).GetAnnotations()
            .Should().Contain(a => a.Subtype == PdfAnnotationSubtype.Highlight && a.Contents == "Review highlight");
    }

    [Fact]
    public void AddTextNote_WhenNoDocumentLoaded_Throws()
    {
        var documentService = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        var workflow = CreateWorkflow(documentService);

        var act = () => workflow.AddTextNote(1, new PdfRectangle(72, 700, 108, 736), "Review note");

        act.Should().Throw<InvalidOperationException>().WithMessage("No document loaded");
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

    private static AnnotationWorkflowService CreateWorkflow(PdfDocumentService documentService) =>
        new(
            documentService,
            NullLogger<AnnotationWorkflowService>.Instance);
}
