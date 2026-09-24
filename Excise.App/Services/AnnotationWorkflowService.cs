using Microsoft.Extensions.Logging;
using Excise.Core.Document;
using System;
using System.Linq;

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

    /// <summary>
    /// Image stamp (#934 row C) — a Stamp whose appearance is a picture, which
    /// is how a scanned signature or a letterhead gets onto a page. Core wants
    /// raw RGB; decoding is the caller's job.
    /// </summary>
    public PdfAnnotation AddImageStamp(
        int pageNumber, PdfRectangle rect, byte[] rgbPixels, int pixelWidth, int pixelHeight,
        string? contents = null,
        PdfDocument? viewerDocument = null)
    {
        var saveDocument = GetLoadedDocument();
        var annotation = saveDocument.AddImageStampAnnotation(
            pageNumber, rect, rgbPixels, pixelWidth, pixelHeight, contents);
        if (viewerDocument is not null &&
            !ReferenceEquals(saveDocument, viewerDocument) &&
            pageNumber >= 1 &&
            pageNumber <= viewerDocument.PageCount)
        {
            viewerDocument.AddImageStampAnnotation(
                pageNumber, rect, rgbPixels, pixelWidth, pixelHeight, contents);
        }
        _logger.LogInformation(
            "Added {W}x{H} image stamp to page {PageNumber}", pixelWidth, pixelHeight, pageNumber);
        return annotation;
    }

    /// <summary>
    /// Apply one rectangle-based annotation transaction to the authoritative
    /// save document and, when separate, the viewer document. Keeping both
    /// writes behind one operation prevents newly wired subtypes from becoming
    /// file-only features that appear only after save and reopen. See #1286.
    /// </summary>
    internal AnnotationRectResult AddRect(
        AnnotationRectRequest request,
        PdfDocument? viewerDocument = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var saveDocument = GetLoadedDocument();
        var annotation = AddRectToDocument(saveDocument, request);
        if (viewerDocument is not null &&
            !ReferenceEquals(saveDocument, viewerDocument) &&
            request.PageNumber >= 1 &&
            request.PageNumber <= viewerDocument.PageCount)
        {
            AddRectToDocument(viewerDocument, request);
        }

        var (successMessage, historyDescription) = RectMessages(request);
        _logger.LogInformation(
            "Added {Kind} annotation to page {PageNumber}",
            request.Kind,
            request.PageNumber);
        return new AnnotationRectResult(
            request,
            annotation,
            successMessage,
            historyDescription);
    }

    internal PdfAnnotation ReplayRect(AnnotationRectRequest request) =>
        AddRect(request).Annotation;

    /// <summary>
    /// Update an already-placed sticky note's text and/or its popup's open
    /// state (#1788 — the click-to-place / click-to-reopen interactive
    /// popup). Unlike <see cref="AddRect"/>, there is no separate viewer
    /// mirror write here: the caller resyncs the viewer document from the
    /// save document afterward (<c>RefreshAfterDocumentMutationAsync</c>,
    /// the same path <c>Undo</c> already uses), which sidesteps needing a
    /// stable identity for the SAME annotation across two independently
    /// mutated <c>PdfDocument</c> instances.
    /// </summary>
    /// <param name="pageNumber">1-based page the note lives on.</param>
    /// <param name="rect">
    /// The note's own /Rect, exactly as read off the annotation the caller
    /// hit-tested (from the VIEWER document). Rect values are bit-identical
    /// between the save and viewer documents for any given note — either
    /// because both were parsed from the same file bytes, or because
    /// <see cref="AddRect"/> passed the SAME <see cref="PdfRectangle"/> to
    /// both when the note was created this session — so matching on it
    /// finds the corresponding annotation in the SAVE document without
    /// needing to thread an identifier through the dual-write.
    /// </param>
    /// <param name="contents">New /Contents, or null to leave the existing text unchanged.</param>
    /// <param name="open">New /Open for the note and its popup, or null to leave it unchanged.</param>
    internal PdfAnnotation UpdateTextNote(
        int pageNumber, PdfRectangle rect, string? contents, bool? open)
    {
        var saveDocument = GetLoadedDocument();
        var saveAnnotation = FindTextAnnotationAt(saveDocument, pageNumber, rect)
            ?? throw new InvalidOperationException(
                $"No sticky note found at the given rect on page {pageNumber}.");

        var effectiveContents = contents ?? saveAnnotation.Contents ?? string.Empty;
        var updated = saveDocument.UpdateTextAnnotation(pageNumber, saveAnnotation, effectiveContents, open);

        _logger.LogInformation("Updated sticky note on page {PageNumber}", pageNumber);
        return updated;
    }

    /// <summary>
    /// The text, open state and card rectangle of the sticky note at <paramref name="rect"/> as they
    /// are now, or null when there is none. Read BEFORE an edit or a move so it can be undone (#1810).
    /// </summary>
    internal (string Contents, bool Open, PdfRectangle? PopupRect)? GetTextNoteState(int pageNumber, PdfRectangle rect)
    {
        var annotation = FindTextAnnotationAt(GetLoadedDocument(), pageNumber, rect);
        return annotation == null ? null : (annotation.Contents ?? string.Empty, annotation.IsOpen, annotation.PopupRect);
    }

    /// <summary>
    /// Drag-to-move a note's CARD (#1797), not the note itself: repositions
    /// only the linked <c>/Popup</c>'s <c>/Rect</c>. <paramref name="iconRect"/>
    /// identifies the note by its own, never-moving <c>/Rect</c> — the same
    /// identity <see cref="UpdateTextNote"/> uses
    /// — so a drag can never accidentally match the wrong note just because
    /// its card happened to be dragged near another one's.
    /// </summary>
    /// <param name="pageNumber">1-based page the note lives on.</param>
    /// <param name="iconRect">The note's own /Rect — its identity, unaffected by this call.</param>
    /// <param name="newPopupRect">The card's new position/size.</param>
    internal PdfAnnotation MoveTextNotePopup(int pageNumber, PdfRectangle iconRect, PdfRectangle newPopupRect)
    {
        var saveDocument = GetLoadedDocument();
        var saveAnnotation = FindTextAnnotationAt(saveDocument, pageNumber, iconRect)
            ?? throw new InvalidOperationException(
                $"No sticky note found at the given rect on page {pageNumber}.");

        var moved = saveDocument.MoveTextAnnotationPopup(saveAnnotation, newPopupRect);

        _logger.LogInformation("Moved sticky note's card on page {PageNumber}", pageNumber);
        return moved;
    }

    /// <summary>
    /// Rect-match tolerance for <see cref="UpdateTextNote"/>. Values placed by
    /// this session are bit-identical, and values round-tripped through a
    /// save/reload are still exact doubles — a tiny epsilon only guards
    /// against a future producer emitting a rounded /Rect.
    /// </summary>
    private const double RectMatchTolerance = 0.01;

    private static PdfAnnotation? FindTextAnnotationAt(PdfDocument document, int pageNumber, PdfRectangle rect) =>
        document.GetPage(pageNumber).GetAnnotations().FirstOrDefault(a =>
            a.Subtype == PdfAnnotationSubtype.Text &&
            Math.Abs(a.Rect.Left - rect.Left) < RectMatchTolerance &&
            Math.Abs(a.Rect.Bottom - rect.Bottom) < RectMatchTolerance &&
            Math.Abs(a.Rect.Right - rect.Right) < RectMatchTolerance &&
            Math.Abs(a.Rect.Top - rect.Top) < RectMatchTolerance);

    private static PdfAnnotation AddRectToDocument(
        PdfDocument document,
        AnnotationRectRequest request) => request.Kind switch
        {
            AnnotationRectKind.TextNote => document.AddTextAnnotation(
                request.PageNumber, request.Rect, request.Value ?? string.Empty,
                open: request.Open, withPopup: true, popupRect: request.PopupRect),
            AnnotationRectKind.Highlight => document.AddHighlightAnnotation(
                request.PageNumber, request.Rect, request.Value ?? string.Empty),
            AnnotationRectKind.Underline => document.AddUnderlineAnnotation(
                request.PageNumber, request.Rect, request.Value ?? string.Empty),
            AnnotationRectKind.StrikeOut => document.AddStrikeOutAnnotation(
                request.PageNumber, request.Rect, request.Value ?? string.Empty),
            AnnotationRectKind.Squiggly => document.AddSquigglyAnnotation(
                request.PageNumber, request.Rect, request.Value ?? string.Empty),
            AnnotationRectKind.Square => document.AddSquareAnnotation(
                request.PageNumber, request.Rect, request.Value),
            AnnotationRectKind.Circle => document.AddCircleAnnotation(
                request.PageNumber, request.Rect, request.Value),
            AnnotationRectKind.FreeText => document.AddFreeTextAnnotation(
                request.PageNumber,
                request.Rect,
                request.Value ?? string.Empty,
                fontSize: request.FontSize),
            AnnotationRectKind.Stamp => document.AddStampAnnotation(
                request.PageNumber,
                request.Rect,
                request.Value ?? throw new ArgumentException("Stamp name is required.", nameof(request)),
                request.Contents),
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };

    private static (string SuccessMessage, string HistoryDescription) RectMessages(
        AnnotationRectRequest request) => request.Kind switch
        {
            AnnotationRectKind.TextNote => ("Sticky note added", "Add sticky note"),
            AnnotationRectKind.Highlight => ("Highlight added", "Add highlight"),
            AnnotationRectKind.Underline => ("Underline added", "Add underline"),
            AnnotationRectKind.StrikeOut => ("StrikeOut added", "Add strikeout"),
            AnnotationRectKind.Squiggly => ("Squiggly added", "Add squiggly"),
            AnnotationRectKind.Square => ("Square added", "Add square"),
            AnnotationRectKind.Circle => ("Circle added", "Add circle"),
            AnnotationRectKind.FreeText => ("Text box added", "Add text box"),
            AnnotationRectKind.Stamp => ($"{request.Value} stamp added", $"Add {request.Value} stamp"),
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };

    /// <summary>
    /// Validate and apply one path-capture transaction to the authoritative
    /// save document and, when it is a separate instance, the viewer document.
    /// The viewer remains a PDF-agnostic capture surface; this workflow owns the
    /// one dispatch from gesture kind to PDF annotation type. See #1286.
    /// </summary>
    internal AnnotationPathResult AddPath(
        AnnotationPathRequest request,
        PdfDocument? viewerDocument = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var usableStrokes = request.Strokes?
            .Where(stroke => stroke is { Count: >= 2 })
            .Select(stroke => (IReadOnlyList<(double X, double Y)>)stroke.ToArray())
            .ToArray()
            ?? [];
        if (usableStrokes.Length == 0)
            return AnnotationPathResult.Rejected(request, validationMessage: null);

        var normalizedRequest = request with { Strokes = usableStrokes };
        if (normalizedRequest.Kind == AnnotationPathKind.Polygon &&
            usableStrokes[0].Count < 3)
        {
            return AnnotationPathResult.Rejected(
                normalizedRequest,
                "A polygon needs at least three points.");
        }

        var saveDocument = GetLoadedDocument();
        var annotation = AddPathToDocument(saveDocument, normalizedRequest);
        if (viewerDocument is not null &&
            !ReferenceEquals(saveDocument, viewerDocument) &&
            normalizedRequest.PageNumber >= 1 &&
            normalizedRequest.PageNumber <= viewerDocument.PageCount)
        {
            AddPathToDocument(viewerDocument, normalizedRequest);
        }

        var (successMessage, historyDescription) = PathMessages(normalizedRequest.Kind);
        _logger.LogInformation(
            "Added {Kind} annotation to page {PageNumber}",
            normalizedRequest.Kind,
            normalizedRequest.PageNumber);
        return AnnotationPathResult.Added(
            normalizedRequest,
            annotation,
            successMessage,
            historyDescription);
    }

    internal PdfAnnotation ReplayPath(AnnotationPathRequest request)
    {
        var result = AddPath(request);
        return result.Annotation ?? throw new InvalidOperationException(
            result.ValidationMessage ?? "The annotation path is no longer valid.");
    }

    private static PdfAnnotation AddPathToDocument(
        PdfDocument document,
        AnnotationPathRequest request)
    {
        var firstStroke = request.Strokes[0];
        var start = firstStroke[0];
        var end = firstStroke[^1];
        return request.Kind switch
        {
            AnnotationPathKind.Line => document.AddLineAnnotation(
                request.PageNumber, start.X, start.Y, end.X, end.Y, request.Contents),
            AnnotationPathKind.Arrow => document.AddArrowAnnotation(
                request.PageNumber, start.X, start.Y, end.X, end.Y, request.Contents),
            AnnotationPathKind.Polygon => document.AddPolygonAnnotation(
                request.PageNumber, firstStroke, request.Contents),
            AnnotationPathKind.PolyLine => document.AddPolyLineAnnotation(
                request.PageNumber, firstStroke, request.Contents),
            _ => document.AddInkAnnotation(
                request.PageNumber, request.Strokes, request.Contents)
        };
    }

    private static (string SuccessMessage, string HistoryDescription) PathMessages(
        AnnotationPathKind kind) => kind switch
        {
            AnnotationPathKind.Line => ("Line added", "Add line"),
            AnnotationPathKind.Arrow => ("Arrow added", "Add arrow"),
            AnnotationPathKind.Polygon => ("Polygon added", "Add polygon"),
            AnnotationPathKind.PolyLine => ("PolyLine added", "Add polyline"),
            _ => ("Ink annotation added", "Add ink")
        };

    private PdfDocument GetLoadedDocument() =>
        _documentService.GetCurrentDocument()
        ?? throw new InvalidOperationException("No document loaded");
}

internal enum AnnotationPathKind
{
    Ink,
    Line,
    Arrow,
    Polygon,
    PolyLine
}

internal enum AnnotationRectKind
{
    TextNote,
    Highlight,
    Underline,
    StrikeOut,
    Squiggly,
    Square,
    Circle,
    FreeText,
    Stamp
}

internal sealed record AnnotationRectRequest(
    AnnotationRectKind Kind,
    int PageNumber,
    PdfRectangle Rect,
    string? Value = null,
    string? Contents = null,
    double FontSize = 12,
    bool Open = false,
    // #1797: TextNote only — the linked /Popup's own /Rect (the draggable
    // CARD), independent of Rect (the note's true, never-moving anchor).
    // Null defaults to Rect itself: the popup starts where the note is placed.
    PdfRectangle? PopupRect = null);

internal sealed record AnnotationRectResult(
    AnnotationRectRequest Request,
    PdfAnnotation Annotation,
    string SuccessMessage,
    string HistoryDescription);

internal sealed record AnnotationPathRequest(
    AnnotationPathKind Kind,
    int PageNumber,
    IReadOnlyList<IReadOnlyList<(double X, double Y)>> Strokes,
    string? Contents = null);

internal sealed record AnnotationPathResult(
    AnnotationPathRequest Request,
    PdfAnnotation? Annotation,
    string? ValidationMessage,
    string SuccessMessage,
    string HistoryDescription)
{
    public bool WasAdded => Annotation is not null;

    public static AnnotationPathResult Rejected(
        AnnotationPathRequest request,
        string? validationMessage) =>
        new(request, null, validationMessage, string.Empty, string.Empty);

    public static AnnotationPathResult Added(
        AnnotationPathRequest request,
        PdfAnnotation annotation,
        string successMessage,
        string historyDescription) =>
        new(request, annotation, null, successMessage, historyDescription);
}
