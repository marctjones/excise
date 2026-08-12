using Microsoft.Extensions.Logging;
using Excise.Core.Document;
using ReactiveUI;
using System;
using System.Threading.Tasks;
using PdfCoreDocument = Excise.Core.Document.PdfDocument;

namespace Excise.App.ViewModels;

public partial class MainWindowViewModel
{
    internal const string DefaultStickyNoteText = "Review note";

    public event EventHandler? AnnotationsChanged;

    public async Task AddHighlightAnnotationFromSelectionAsync()
    {
        if (!_documentService.IsDocumentLoaded)
            return;

        // #642: /P bit 6 gates adding/modifying annotations.
        if (!EnsureDocumentPermission(p => p.CanAnnotate,
            "Adding a highlight annotation", "adding or modifying annotations (/P bit 6)"))
        {
            return;
        }

        if (!TryGetCurrentTextSelectionContentRect(out var pageNumber, out var contentRect))
        {
            await _dialogService.ShowMessageAsync(
                "Add Highlight",
                "Select text before adding a highlight.");
            return;
        }

        var contents = string.IsNullOrWhiteSpace(SelectedText)
            ? "Highlight"
            : SelectedText.Trim();

        try
        {
            var annotation = _annotationWorkflow.AddHighlight(pageNumber, contentRect, contents);
            AddHighlightToViewerDocument(pageNumber, contentRect, contents);
            await MarkAnnotationChangedAsync("Highlight added");
            RecordAnnotationAdd("Add highlight", pageNumber, annotation,
                () => _annotationWorkflow.AddHighlight(pageNumber, contentRect, contents));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding highlight annotation");
            _toastService.ShowError("Failed to add highlight", ex.Message);
        }
    }

    /// <summary>
    /// Underline, StrikeOut and Squiggly from the current text selection (#912).
    ///
    /// Core has been able to author all three since the annotation-authoring
    /// work landed; nothing in the app could reach them, so 13 of 15 authorable
    /// subtypes were unreachable. These three are the cheap ones: they reuse the
    /// SAME gesture Highlight already uses — select text, invoke — so no new
    /// input handling is involved.
    ///
    /// THE VIEWER MIRROR IS NOT OPTIONAL. A document is open TWICE — the
    /// save document (<c>_documentService</c>) and the viewer document
    /// (<c>_pdfCoreDocument</c>), loaded as two separate PdfDocument instances
    /// by LoadDocumentInstancesAsync. Authoring onto the save document alone
    /// puts the annotation in the saved FILE while leaving the screen
    /// unchanged: the user clicks "Add Underline", sees nothing happen, and
    /// only finds the underline after saving and reopening. So each of these
    /// takes a second delegate that repeats the authoring on the viewer
    /// document, exactly as the Highlight path has always done.
    ///
    /// A file-only test cannot see that gap — both halves save identically.
    /// TextMarkupAnnotationCommandTests therefore asserts on
    /// <see cref="PdfCoreDocument"/> BEFORE saving.
    /// </summary>
    private async Task AddTextMarkupFromSelectionAsync(
        string kind,
        Func<int, PdfRectangle, string, PdfAnnotation> add,
        Func<PdfCoreDocument, int, PdfRectangle, string, PdfAnnotation> mirrorToViewer)
    {
        if (!_documentService.IsDocumentLoaded)
            return;

        // #642: /P bit 6 gates adding/modifying annotations — same gate as
        // Highlight, and it must not be skipped just because this path is new.
        if (!EnsureDocumentPermission(p => p.CanAnnotate,
            $"Adding a {kind} annotation", "adding or modifying annotations (/P bit 6)"))
        {
            return;
        }

        if (!TryGetCurrentTextSelectionContentRect(out var pageNumber, out var contentRect))
        {
            await _dialogService.ShowMessageAsync(
                $"Add {kind}",
                $"Select text before adding a {kind.ToLowerInvariant()}.");
            return;
        }

        var contents = string.IsNullOrWhiteSpace(SelectedText) ? kind : SelectedText.Trim();

        try
        {
            var annotation = add(pageNumber, contentRect, contents);
            AddTextMarkupToViewerDocument(pageNumber, contentRect, contents, mirrorToViewer);
            await MarkAnnotationChangedAsync($"{kind} added");
            RecordAnnotationAdd($"Add {kind.ToLowerInvariant()}", pageNumber, annotation,
                () => add(pageNumber, contentRect, contents));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding {Kind} annotation", kind);
            _toastService.ShowError($"Failed to add {kind.ToLowerInvariant()}", ex.Message);
        }
    }

    public Task AddUnderlineAnnotationFromSelectionAsync() =>
        AddTextMarkupFromSelectionAsync("Underline", _annotationWorkflow.AddUnderline,
            static (d, p, r, c) => d.AddUnderlineAnnotation(p, r, c));

    public Task AddStrikeOutAnnotationFromSelectionAsync() =>
        AddTextMarkupFromSelectionAsync("StrikeOut", _annotationWorkflow.AddStrikeOut,
            static (d, p, r, c) => d.AddStrikeOutAnnotation(p, r, c));

    public Task AddSquigglyAnnotationFromSelectionAsync() =>
        AddTextMarkupFromSelectionAsync("Squiggly", _annotationWorkflow.AddSquiggly,
            static (d, p, r, c) => d.AddSquigglyAnnotation(p, r, c));

    /// <summary>
    /// Square and Circle from the drag rectangle (#912's second row).
    ///
    /// Core has been able to author both since the annotation-authoring work
    /// landed, and `AnnotationWorkflowService` already exposed them — only the
    /// command wiring was missing, exactly as with the text-markup row.
    ///
    /// The gesture is the REDACTION BOX drag, reused. That is deliberate: it is
    /// the one rectangle gesture the app already implements, so no new input
    /// handling is involved. A user draws a box and chooses what it becomes.
    ///
    /// The viewer mirror is mandatory here for the same reason as the text
    /// markup — see AddTextMarkupFromSelectionAsync. Without it the shape lands
    /// in the saved file and never appears on screen.
    /// </summary>
    private async Task AddShapeFromDragAsync(
        string kind,
        Func<int, PdfRectangle, string, PdfAnnotation> add,
        Func<PdfCoreDocument, int, PdfRectangle, string, PdfAnnotation> mirrorToViewer)
    {
        if (!_documentService.IsDocumentLoaded)
            return;

        // #642: /P bit 6 gates adding or modifying annotations.
        if (!EnsureDocumentPermission(p => p.CanAnnotate,
            $"Adding a {kind} annotation", "adding or modifying annotations (/P bit 6)"))
        {
            return;
        }

        if (!TryGetCurrentShapeContentRect(out var pageNumber, out var contentRect))
        {
            await _dialogService.ShowMessageAsync(
                $"Add {kind}",
                $"Drag a box on the page before adding a {kind.ToLowerInvariant()}.");
            return;
        }

        try
        {
            var annotation = add(pageNumber, contentRect, kind);
            AddTextMarkupToViewerDocument(pageNumber, contentRect, kind, mirrorToViewer);
            await MarkAnnotationChangedAsync($"{kind} added");
            RecordAnnotationAdd($"Add {kind.ToLowerInvariant()}", pageNumber, annotation,
                () => add(pageNumber, contentRect, kind));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding {Kind} annotation", kind);
            _toastService.ShowError($"Failed to add {kind.ToLowerInvariant()}", ex.Message);
        }
    }

    public Task AddSquareAnnotationFromDragAsync() =>
        AddShapeFromDragAsync("Square", _annotationWorkflow.AddSquare,
            static (d, p, r, c) => d.AddSquareAnnotation(p, r, c));

    public Task AddCircleAnnotationFromDragAsync() =>
        AddShapeFromDragAsync("Circle", _annotationWorkflow.AddCircle,
            static (d, p, r, c) => d.AddCircleAnnotation(p, r, c));

    public async Task AddStickyNoteAnnotationAsync(string? contentsOverride = null)
    {
        if (!_documentService.IsDocumentLoaded)
            return;

        // #642: /P bit 6 gates adding/modifying annotations.
        if (!EnsureDocumentPermission(p => p.CanAnnotate,
            "Adding a sticky note", "adding or modifying annotations (/P bit 6)"))
        {
            return;
        }

        var contents = contentsOverride;
        if (contents == null)
        {
            var defaultText = string.IsNullOrWhiteSpace(SelectedText)
                ? DefaultStickyNoteText
                : SelectedText.Trim();

            contents = await _dialogService.PromptTextAsync(
                "Add Sticky Note",
                "Enter note text:",
                defaultText);
        }

        if (string.IsNullOrWhiteSpace(contents))
            return;

        try
        {
            var pageNumber = CurrentPageIndex + 1;
            var contentRect = TryGetCurrentTextSelectionContentRect(out var selectionPageNumber, out var selectionRect)
                ? selectionRect
                : GetDefaultStickyNoteRect(pageNumber);

            if (selectionPageNumber > 0)
                pageNumber = selectionPageNumber;

            var trimmedContents = contents.Trim();
            var notePageNumber = pageNumber;
            var noteRect = contentRect;
            var annotation = _annotationWorkflow.AddTextNote(notePageNumber, noteRect, trimmedContents);
            AddTextNoteToViewerDocument(notePageNumber, noteRect, trimmedContents);
            await MarkAnnotationChangedAsync("Sticky note added");
            RecordAnnotationAdd("Add sticky note", notePageNumber, annotation,
                () => _annotationWorkflow.AddTextNote(notePageNumber, noteRect, trimmedContents));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding sticky-note annotation");
            _toastService.ShowError("Failed to add sticky note", ex.Message);
        }
    }

    /// <summary>
    /// Shape annotations (#912) come from a DRAG, not a text selection — the
    /// same gesture the redaction box already uses. Same conversion as the
    /// text-selection path, different source rectangle.
    /// </summary>
    private bool TryGetCurrentShapeContentRect(out int pageNumber, out PdfRectangle contentRect)
        => TryGetContentRect(CurrentRedactionPageArea, out pageNumber, out contentRect);

    private bool TryGetCurrentTextSelectionContentRect(out int pageNumber, out PdfRectangle contentRect)
        => TryGetContentRect(CurrentTextSelectionPageArea, out pageNumber, out contentRect);

    private bool TryGetContentRect(PdfPageRect? source, out int pageNumber, out PdfRectangle contentRect)
    {
        pageNumber = 0;
        contentRect = default;

        if (source is not { Width: > 0, Height: > 0 } selectionArea)
            return false;

        var document = _documentService.GetCurrentDocument();
        if (document == null ||
            selectionArea.PageNumber < 1 ||
            selectionArea.PageNumber > document.PageCount)
        {
            return false;
        }

        var page = document.GetPage(selectionArea.PageNumber);
        var normalized = PdfCoordinateMapper
            .ToContentPoints(page, selectionArea)
            .ToPdfRectangle()
            .Normalize();

        if (normalized.Width <= 0 || normalized.Height <= 0)
            return false;

        pageNumber = selectionArea.PageNumber;
        contentRect = normalized;
        return true;
    }

    private PdfRectangle GetDefaultStickyNoteRect(int pageNumber)
    {
        var document = _documentService.GetCurrentDocument()
            ?? throw new InvalidOperationException("No document loaded");

        if (pageNumber < 1 || pageNumber > document.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));

        var page = document.GetPage(pageNumber);
        var left = Math.Max(18, page.MediaBox.Normalize().Left + 48);
        var top = page.MediaBox.Normalize().Top - 48;
        return new PdfRectangle(left, top - 36, left + 36, top).Normalize();
    }

    private void AddHighlightToViewerDocument(int pageNumber, PdfRectangle contentRect, string contents)
    {
        var saveDocument = _documentService.GetCurrentDocument();
        if (_pdfCoreDocument == null || ReferenceEquals(saveDocument, _pdfCoreDocument))
            return;
        if (pageNumber < 1 || pageNumber > _pdfCoreDocument.PageCount)
            return;

        _pdfCoreDocument.AddHighlightAnnotation(pageNumber, contentRect, contents);
    }

    /// <summary>
    /// The generalised form of <see cref="AddHighlightToViewerDocument"/> (#912):
    /// same guards, the subtype supplied by the caller. See
    /// AddTextMarkupFromSelectionAsync for why skipping this makes the feature
    /// look broken on screen while the saved file is correct.
    /// </summary>
    private void AddTextMarkupToViewerDocument(
        int pageNumber, PdfRectangle contentRect, string contents,
        Func<PdfCoreDocument, int, PdfRectangle, string, PdfAnnotation> add)
    {
        var saveDocument = _documentService.GetCurrentDocument();
        if (_pdfCoreDocument == null || ReferenceEquals(saveDocument, _pdfCoreDocument))
            return;
        if (pageNumber < 1 || pageNumber > _pdfCoreDocument.PageCount)
            return;

        add(_pdfCoreDocument, pageNumber, contentRect, contents);
    }

    private void AddTextNoteToViewerDocument(int pageNumber, PdfRectangle contentRect, string contents)
    {
        var saveDocument = _documentService.GetCurrentDocument();
        if (_pdfCoreDocument == null || ReferenceEquals(saveDocument, _pdfCoreDocument))
            return;
        if (pageNumber < 1 || pageNumber > _pdfCoreDocument.PageCount)
            return;

        _pdfCoreDocument.AddTextAnnotation(pageNumber, contentRect, contents);
    }

    private Task MarkAnnotationChangedAsync(string toastMessage)
    {
        FileState.AnnotationEditsCount++;
        this.RaisePropertyChanged(nameof(SaveButtonText));
        this.RaisePropertyChanged(nameof(StatusBarText));
        AnnotationsChanged?.Invoke(this, EventArgs.Empty);
        RequestViewerRenderRefresh();

        _toastService.ShowSuccess(toastMessage);
        return Task.CompletedTask;
    }

    private void ClearCurrentTextSelection()
    {
        CurrentTextSelectionArea = new global::Avalonia.Rect();
        CurrentTextSelectionPageArea = null;
        SelectedText = string.Empty;
    }
}
