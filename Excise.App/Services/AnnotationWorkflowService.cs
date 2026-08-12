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
    /// The other three text-markup subtypes (#912). Same shape as
    /// <see cref="AddHighlight"/> and the same selection gesture — Core has been
    /// able to author Underline, StrikeOut and Squiggly all along, and nothing
    /// in the app could reach them.
    /// </summary>
    public PdfAnnotation AddUnderline(int pageNumber, PdfRectangle rect, string contents)
    {
        var document = GetLoadedDocument();
        var annotation = document.AddUnderlineAnnotation(pageNumber, rect, contents);
        _logger.LogInformation("Added underline annotation to page {PageNumber}", pageNumber);
        return annotation;
    }

    public PdfAnnotation AddStrikeOut(int pageNumber, PdfRectangle rect, string contents)
    {
        var document = GetLoadedDocument();
        var annotation = document.AddStrikeOutAnnotation(pageNumber, rect, contents);
        _logger.LogInformation("Added strikeout annotation to page {PageNumber}", pageNumber);
        return annotation;
    }

    public PdfAnnotation AddSquiggly(int pageNumber, PdfRectangle rect, string contents)
    {
        var document = GetLoadedDocument();
        var annotation = document.AddSquigglyAnnotation(pageNumber, rect, contents);
        _logger.LogInformation("Added squiggly annotation to page {PageNumber}", pageNumber);
        return annotation;
    }

    /// <summary>
    /// Standard rubber stamp (#934 row B). The 15 names in
    /// <see cref="PdfAnnotationAuthoring.StandardStampNames"/> are the ones
    /// ISO 32000-1 Table 181 defines; Core rejects anything else, which is why
    /// the GUI offers a fixed menu rather than free text.
    /// </summary>
    public PdfAnnotation AddStamp(int pageNumber, PdfRectangle rect, string stampName, string? contents = null)
    {
        var document = GetLoadedDocument();
        var annotation = document.AddStampAnnotation(pageNumber, rect, stampName, contents);
        _logger.LogInformation("Added {StampName} stamp annotation to page {PageNumber}", stampName, pageNumber);
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

    /// <summary>
    /// Add an Ink (freehand) annotation with a baked appearance stream (#626).
    /// Each stroke is a polyline of at least two (x, y) points in PDF page
    /// coordinates (Y-up).
    /// </summary>
    public PdfAnnotation AddInk(
        int pageNumber,
        IReadOnlyList<IReadOnlyList<(double X, double Y)>> strokes,
        string? contents = null)
    {
        var document = GetLoadedDocument();
        var annotation = document.AddInkAnnotation(pageNumber, strokes, contents);

        _logger.LogInformation("Added ink annotation to page {PageNumber}", pageNumber);
        return annotation;
    }

    private PdfDocument GetLoadedDocument() =>
        _documentService.GetCurrentDocument()
        ?? throw new InvalidOperationException("No document loaded");
}
