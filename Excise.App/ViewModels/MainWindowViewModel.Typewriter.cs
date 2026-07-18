using Microsoft.Extensions.Logging;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.Core.Editing;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Excise.App.ViewModels;

public partial class MainWindowViewModel
{
    private bool _isTypewriterMode;

    public ObservableCollection<PdfTypewriterTextOperation> TypewriterTextOperations { get; } = new();

    public bool IsTypewriterMode
    {
        get => _isTypewriterMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _isTypewriterMode, value);
            if (value)
            {
                ViewMode = PdfViewMode.SinglePage;
                if (IsRedactionMode) IsRedactionMode = false;
                if (IsTextSelectionMode) IsTextSelectionMode = false;
                if (IsFormAuthoringMode) IsFormAuthoringMode = false;
            }
            else
            {
                RestoreViewModeFromPreference();
            }

            this.RaisePropertyChanged(nameof(CurrentModeText));
            this.RaisePropertyChanged(nameof(InteractionMode));
        }
    }

    private void ToggleTypewriterMode()
    {
        // #642: adding text modifies the document — /P bit 4. Block on
        // entering the mode (clear feedback up front); leaving it is free.
        if (!IsTypewriterMode && !EnsureDocumentPermission(p => p.CanModify,
            "Adding text (typewriter)", "modifying the document (/P bit 4)"))
        {
            return;
        }

        IsTypewriterMode = !IsTypewriterMode;
    }

    public void OnTypewriterTextCreated(PdfRectangle rect, int pageNumber)
    {
        if (_pdfCoreDocument == null)
            return;

        // Defence in depth for callers that bypass the mode toggle (#642).
        if (!EnsureDocumentPermission(p => p.CanModify,
            "Adding text (typewriter)", "modifying the document (/P bit 4)"))
        {
            return;
        }

        TypewriterTextOperations.Add(PdfTypewriterTextOperation.Create(
            pageNumber,
            rect,
            string.Empty));
        RefreshTypewriterEditState();
        _logger.LogInformation("Added typewriter text box on page {Page}", pageNumber);
    }

    public void OnTypewriterTextEdited(Guid operationId, string text, int pageNumber)
    {
        var index = IndexOfTypewriterOperation(operationId);
        if (index < 0)
            return;

        TypewriterTextOperations[index] = TypewriterTextOperations[index].WithText(text);
        RefreshTypewriterEditState();
        _logger.LogDebug("Edited typewriter text on page {Page}", pageNumber);
    }

    public void OnTypewriterTextBoundsChanged(Guid operationId, PdfRectangle rect, int pageNumber)
    {
        var index = IndexOfTypewriterOperation(operationId);
        if (index < 0)
            return;

        TypewriterTextOperations[index] = TypewriterTextOperations[index].WithPageAndBounds(pageNumber, rect);
        RefreshTypewriterEditState();
        _logger.LogDebug("Moved/resized typewriter text on page {Page}", pageNumber);
    }

    public void OnTypewriterTextDeleted(Guid operationId)
    {
        var index = IndexOfTypewriterOperation(operationId);
        if (index < 0)
            return;

        TypewriterTextOperations.RemoveAt(index);
        RefreshTypewriterEditState();
        _logger.LogInformation("Deleted pending typewriter text");
    }

    private int IndexOfTypewriterOperation(Guid operationId)
    {
        for (var i = 0; i < TypewriterTextOperations.Count; i++)
        {
            if (TypewriterTextOperations[i].Id == operationId)
                return i;
        }

        return -1;
    }

    private bool ApplyPendingTypewriterText(PdfDocument document)
    {
        var pending = TypewriterTextOperations
            .Where(operation => operation.IsPending && operation.HasText)
            .ToList();
        if (pending.Count == 0)
            return false;

        PdfTypewriterTextApplier.Apply(document, pending);
        _logger.LogInformation("Flattened {Count} typewriter text edit(s)", pending.Count);
        return true;
    }

    private void ClearPendingTypewriterText()
    {
        if (TypewriterTextOperations.Count > 0)
            TypewriterTextOperations.Clear();
        RefreshTypewriterEditState();
    }

    private void RefreshTypewriterEditState()
    {
        FileState.TypewriterEditsCount = TypewriterTextOperations.Count(o => o.IsPending && o.HasText);
        this.RaisePropertyChanged(nameof(SaveButtonText));
        this.RaisePropertyChanged(nameof(StatusBarText));
    }

    private async Task ReloadPdfCoreDocumentAfterSaveAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        var pageIndex = Math.Clamp(CurrentPageIndex, 0, Math.Max(0, _documentService.PageCount - 1));

        PdfCoreDocument?.Dispose();
        // #643: a preserving save writes encrypted output; reopen it with the
        // password the document was opened with (null = empty password).
        PdfCoreDocument = PdfDocument.Open(filePath, _documentService.CurrentUserPassword);
        CurrentPageIndex = pageIndex;
        _hasInMemoryModifications = false;
        _renderService.ClearCache();
        ResetThumbnailLoadTracking();

        _thumbnailCache?.Dispose();
        _thumbnailCache = new Services.ThumbnailCacheService(
            filePath,
            PdfCoreDocument!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        _indexBuildCts?.Cancel();
        _indexBuildCts = new CancellationTokenSource();
        TextIndex = new Services.DocumentTextIndex(
            PdfCoreDocument!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        StartSearchIndexBuild(TextIndex, _indexBuildCts);

        this.RaisePropertyChanged(nameof(TotalPages));
        this.RaisePropertyChanged(nameof(CurrentPage));
        this.RaisePropertyChanged(nameof(CurrentPageFormFields));
        this.RaisePropertyChanged(nameof(StatusText));
        await LoadPageThumbnailsAsync();
    }
}
