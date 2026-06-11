using Microsoft.Extensions.Logging;
using Pdfe.Core.Document;
using System;

namespace PdfEditor.Services;

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

    private PdfDocument GetLoadedDocument() =>
        _documentService.GetCurrentDocument()
        ?? throw new InvalidOperationException("No document loaded");
}
