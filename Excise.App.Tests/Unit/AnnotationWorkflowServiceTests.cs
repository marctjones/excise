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

        var annotation = workflow.AddRect(new AnnotationRectRequest(
            AnnotationRectKind.TextNote,
            1,
            new PdfRectangle(72, 700, 108, 736),
            "Review note")).Annotation;

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

        var annotation = workflow.AddRect(new AnnotationRectRequest(
            AnnotationRectKind.Highlight,
            1,
            new PdfRectangle(100, 650, 260, 670),
            "Review highlight")).Annotation;

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

        var act = () => workflow.AddRect(new AnnotationRectRequest(
            AnnotationRectKind.TextNote,
            1,
            new PdfRectangle(72, 700, 108, 736),
            "Review note"));

        act.Should().Throw<InvalidOperationException>().WithMessage("No document loaded");
    }

    [Fact]
    public void AddPath_InkFiltersShortStrokesAndMirrorsTheViewerDocument()
    {
        var sourcePath = CreateBlankPdf("ink-source.pdf");
        var documentService = CreateLoadedDocumentService(sourcePath);
        var workflow = CreateWorkflow(documentService);
        using var viewerDocument = PdfDocument.Open(sourcePath);
        var request = new AnnotationPathRequest(
            AnnotationPathKind.Ink,
            1,
            [
                [(10d, 10d)],
                [(20d, 20d), (40d, 40d), (60d, 20d)]
            ]);

        var result = workflow.AddPath(request, viewerDocument);

        result.WasAdded.Should().BeTrue();
        result.Request.Strokes.Should().ContainSingle(
            "a click-only stroke must not reach Core as an invalid InkList entry");
        result.Annotation!.Subtype.Should().Be(PdfAnnotationSubtype.Ink);
        documentService.GetCurrentDocument()!.GetPage(1).GetAnnotations()
            .Should().ContainSingle(a => a.Subtype == PdfAnnotationSubtype.Ink);
        viewerDocument.GetPage(1).GetAnnotations()
            .Should().ContainSingle(a => a.Subtype == PdfAnnotationSubtype.Ink,
                "the separate viewer document must reflect the transaction immediately");
    }

    [Fact]
    public void AddPath_PolygonWithTwoPointsReturnsValidationWithoutMutatingEitherDocument()
    {
        var sourcePath = CreateBlankPdf("polygon-source.pdf");
        var documentService = CreateLoadedDocumentService(sourcePath);
        var workflow = CreateWorkflow(documentService);
        using var viewerDocument = PdfDocument.Open(sourcePath);
        var request = new AnnotationPathRequest(
            AnnotationPathKind.Polygon,
            1,
            [[(20d, 20d), (40d, 40d)]]);

        var result = workflow.AddPath(request, viewerDocument);

        result.WasAdded.Should().BeFalse();
        result.ValidationMessage.Should().Be("A polygon needs at least three points.");
        documentService.GetCurrentDocument()!.GetPage(1).GetAnnotations().Should().BeEmpty();
        viewerDocument.GetPage(1).GetAnnotations().Should().BeEmpty();
    }

    [Fact]
    public void AddPath_ArrowUsesFirstAndLastSamplesAndReturnsReplayMetadata()
    {
        var sourcePath = CreateBlankPdf("arrow-source.pdf");
        var documentService = CreateLoadedDocumentService(sourcePath);
        var workflow = CreateWorkflow(documentService);
        var request = new AnnotationPathRequest(
            AnnotationPathKind.Arrow,
            1,
            [[(100d, 200d), (150d, 225d), (300d, 250d)]]);

        var result = workflow.AddPath(request);

        result.Annotation!.Subtype.Should().Be(PdfAnnotationSubtype.Line);
        result.Annotation.LineEndpoints.Should().Be((100d, 200d, 300d, 250d));
        result.Annotation.LineEndings!.Value.End.Should().Be("ClosedArrow");
        result.SuccessMessage.Should().Be("Arrow added");
        result.HistoryDescription.Should().Be("Add arrow");
    }

    [Fact]
    public void AddRect_UnderlineMutatesBothDocumentsAndReturnsPublicationMetadata()
    {
        var sourcePath = CreateBlankPdf("underline-source.pdf");
        var documentService = CreateLoadedDocumentService(sourcePath);
        var workflow = CreateWorkflow(documentService);
        using var viewerDocument = PdfDocument.Open(sourcePath);
        var request = new AnnotationRectRequest(
            AnnotationRectKind.Underline,
            1,
            new PdfRectangle(72, 600, 220, 620),
            "Important clause");

        var result = workflow.AddRect(request, viewerDocument);

        result.Annotation.Subtype.Should().Be(PdfAnnotationSubtype.Underline);
        result.SuccessMessage.Should().Be("Underline added");
        result.HistoryDescription.Should().Be("Add underline");
        documentService.GetCurrentDocument()!.GetPage(1).GetAnnotations()
            .Should().ContainSingle(a => a.Subtype == PdfAnnotationSubtype.Underline);
        viewerDocument.GetPage(1).GetAnnotations()
            .Should().ContainSingle(a => a.Subtype == PdfAnnotationSubtype.Underline);
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
