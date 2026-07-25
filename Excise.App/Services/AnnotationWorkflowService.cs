using Microsoft.Extensions.Logging;
using Excise.Core.Document;
using System;

namespace Excise.App.Services;

public sealed class AnnotationWorkflowService
{
    private readonly PdfDocumentService _documentService;
    private readonly ILogger<AnnotationWorkflowService> _logger;

    public AnnotationWorkflowService(
        PdfDocumentService documentService,
        ILogger<AnnotationWorkflowService> logger)
    {
        ArgumentNullException.ThrowIfNull(documentService);
        ArgumentNullException.ThrowIfNull(logger);

        _documentService = documentService;
        _logger = logger;
    }

    public PdfAnnotation AddTextNote(int pageNumber, PdfRectangle rect, string contents)
    {
        var document = GetLoadedDocument();
        var annotation = document.AddTextAnnotation(pageNumber, rect, contents);

        _logger.LogInformation("Added text annotation to page {PageNumber}", pageNumber);
        return annotation;
    }

    public PdfAnnotation AddHighlight(int pageNumber, PdfRectangle rect, string contents)
    {
        var document = GetLoadedDocument();
        var annotation = document.AddHighlightAnnotation(pageNumber, rect, contents);

        _logger.LogInformation("Added highlight annotation to page {PageNumber}", pageNumber);
        return annotation;
    }

    /// <summary>
    /// Add a Square shape annotation with a baked appearance stream (#626).
    /// </summary>
    public PdfAnnotation AddSquare(int pageNumber, PdfRectangle rect, string? contents = null)
    {
        var document = GetLoadedDocument();
        var annotation = document.AddSquareAnnotation(pageNumber, rect, contents);

        _logger.LogInformation("Added square annotation to page {PageNumber}", pageNumber);
        return annotation;
    }

    /// <summary>
    /// Add a Circle shape annotation with a baked appearance stream (#626).
    /// </summary>
    public PdfAnnotation AddCircle(int pageNumber, PdfRectangle rect, string? contents = null)
    {
        var document = GetLoadedDocument();
        var annotation = document.AddCircleAnnotation(pageNumber, rect, contents);

        _logger.LogInformation("Added circle annotation to page {PageNumber}", pageNumber);
        return annotation;
    }

    /// <summary>
    /// Add a FreeText (text box) annotation with a baked appearance stream (#626).
    /// </summary>
    public PdfAnnotation AddFreeText(
        int pageNumber, PdfRectangle rect, string text, double fontSize = 12)
    {
        var document = GetLoadedDocument();
        var annotation = document.AddFreeTextAnnotation(pageNumber, rect, text, fontSize: fontSize);

        _logger.LogInformation("Added free-text annotation to page {PageNumber}", pageNumber);
        return annotation;
    }

    private PdfDocument GetLoadedDocument() =>
        _documentService.GetCurrentDocument()
        ?? throw new InvalidOperationException("No document loaded");
}
