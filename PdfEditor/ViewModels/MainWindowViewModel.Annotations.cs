using Microsoft.Extensions.Logging;
using Pdfe.Core.Document;
using ReactiveUI;
using System;
using System.Threading.Tasks;

namespace PdfEditor.ViewModels;

public partial class MainWindowViewModel
{
    internal const string DefaultStickyNoteText = "Review note";

    public event EventHandler? AnnotationsChanged;

    public async Task AddHighlightAnnotationFromSelectionAsync()
    {
        if (!_documentService.IsDocumentLoaded)
            return;

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
            _annotationWorkflow.AddHighlight(pageNumber, contentRect, contents);
            AddHighlightToViewerDocument(pageNumber, contentRect, contents);
            await MarkAnnotationChangedAsync("Highlight added");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding highlight annotation");
            _toastService.ShowError("Failed to add highlight", ex.Message);
        }
    }

    public async Task AddStickyNoteAnnotationAsync(string? contentsOverride = null)
    {
        if (!_documentService.IsDocumentLoaded)
            return;

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
            _annotationWorkflow.AddTextNote(pageNumber, contentRect, trimmedContents);
            AddTextNoteToViewerDocument(pageNumber, contentRect, trimmedContents);
            await MarkAnnotationChangedAsync("Sticky note added");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding sticky-note annotation");
            _toastService.ShowError("Failed to add sticky note", ex.Message);
        }
    }

    private bool TryGetCurrentTextSelectionContentRect(out int pageNumber, out PdfRectangle contentRect)
    {
        pageNumber = 0;
        contentRect = default;

        if (CurrentTextSelectionPageArea is not { Width: > 0, Height: > 0 } selectionArea)
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

    private void AddTextNoteToViewerDocument(int pageNumber, PdfRectangle contentRect, string contents)
    {
        var saveDocument = _documentService.GetCurrentDocument();
        if (_pdfCoreDocument == null || ReferenceEquals(saveDocument, _pdfCoreDocument))
            return;
        if (pageNumber < 1 || pageNumber > _pdfCoreDocument.PageCount)
            return;

        _pdfCoreDocument.AddTextAnnotation(pageNumber, contentRect, contents);
    }

    private async Task MarkAnnotationChangedAsync(string toastMessage)
    {
        FileState.AnnotationEditsCount++;
        _hasInMemoryModifications = true;
        this.RaisePropertyChanged(nameof(SaveButtonText));
        this.RaisePropertyChanged(nameof(StatusBarText));
        AnnotationsChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            await RenderCurrentPageAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Annotation was added, but the current page did not rerender immediately");
        }

        _toastService.ShowSuccess(toastMessage);
    }

    private void ClearCurrentTextSelection()
    {
        CurrentTextSelectionArea = new Avalonia.Rect();
        CurrentTextSelectionPageArea = null;
        SelectedText = string.Empty;
    }
}
