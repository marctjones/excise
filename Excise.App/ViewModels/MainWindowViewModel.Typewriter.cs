using Microsoft.Extensions.Logging;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.Core.Editing;
using ReactiveUI;
using System;
using System.Collections.Generic;
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
                ClearActiveTypewriterOperation(); // #781: hide the style inspector on exit
                // #831: restore selection only when returning to reading, not
                // when switching into another editing mode (see IsRedactionMode).
                if (!IsEditingModeActive) IsTextSelectionMode = true;
            }

            this.RaisePropertyChanged(nameof(CurrentModeText));
            this.RaisePropertyChanged(nameof(InteractionMode));
            this.RaisePropertyChanged(nameof(IsTypewriterStyleInspectorVisible));
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

        // #781: seed the box with the current inspector style so "set the
        // style, then draw" carries the chosen look onto the new box, and make
        // the fresh box the inspector's active target.
        var created = PdfTypewriterTextOperation.Create(
            pageNumber,
            rect,
            string.Empty,
            BuildInspectorStyle());

        RecordTypewriterEdit("Add text box", () =>
        {
            TypewriterTextOperations.Add(created);
            RefreshTypewriterEditState();
        });
        SetActiveTypewriterOperation(created.Id);
        _logger.LogInformation("Added typewriter text box on page {Page}", pageNumber);
    }

    public void OnTypewriterTextEdited(Guid operationId, string text, int pageNumber)
    {
        if (IndexOfTypewriterOperation(operationId) < 0)
            return;

        RecordTypewriterEdit("Edit text", () =>
        {
            var index = IndexOfTypewriterOperation(operationId);
            if (index < 0)
                return;
            TypewriterTextOperations[index] = TypewriterTextOperations[index].WithText(text);
            RefreshTypewriterEditState();
        });
        SetActiveTypewriterOperation(operationId); // #781: typing targets the inspector
        _logger.LogDebug("Edited typewriter text on page {Page}", pageNumber);
    }

    public void OnTypewriterTextBoundsChanged(Guid operationId, PdfRectangle rect, int pageNumber)
    {
        if (IndexOfTypewriterOperation(operationId) < 0)
            return;

        RecordTypewriterEdit("Move text box", () =>
        {
            var index = IndexOfTypewriterOperation(operationId);
            if (index < 0)
                return;
            TypewriterTextOperations[index] = TypewriterTextOperations[index].WithPageAndBounds(pageNumber, rect);
            RefreshTypewriterEditState();
        });
        SetActiveTypewriterOperation(operationId); // #781: moving targets the inspector
        _logger.LogDebug("Moved/resized typewriter text on page {Page}", pageNumber);
    }

    public void OnTypewriterTextDeleted(Guid operationId)
    {
        if (IndexOfTypewriterOperation(operationId) < 0)
            return;

        RecordTypewriterEdit("Delete text box", () =>
        {
            var index = IndexOfTypewriterOperation(operationId);
            if (index < 0)
                return;
            TypewriterTextOperations.RemoveAt(index);
            RefreshTypewriterEditState();
        });
        if (_activeTypewriterOperationId == operationId)
            ClearActiveTypewriterOperation(); // #781: don't point the inspector at a dead box
        _logger.LogInformation("Deleted pending typewriter text");
    }

    /// <summary>
    /// Run a mutation of the pending type-over collection and record a snapshot
    /// memento (#782). The ops are immutable records, so a shallow before/after
    /// copy of the collection is a complete, exact reversal. Undo/redo restore
    /// the collection directly — never through the OnTypewriter* handlers — so
    /// replay cannot re-enter the history.
    /// </summary>
    private void RecordTypewriterEdit(string description, Action mutate)
    {
        var before = TypewriterTextOperations.ToList();
        mutate();
        var after = TypewriterTextOperations.ToList();
        _history.Push(
            description,
            () => RestoreTypewriterOperations(before),
            () => RestoreTypewriterOperations(after));
    }

    private void RestoreTypewriterOperations(List<PdfTypewriterTextOperation> snapshot)
    {
        TypewriterTextOperations.Clear();
        foreach (var operation in snapshot)
            TypewriterTextOperations.Add(operation);
        RefreshTypewriterEditState();
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
        ClearActiveTypewriterOperation(); // #781
        RefreshTypewriterEditState();
    }

    /// <summary>
    /// True while any type-over edit is pending (with text). Drives the
    /// discard / next-edit affordances so a user can never lose track of
    /// edits that would otherwise flatten silently on the next save (#780).
    /// </summary>
    public bool HasPendingTypewriterEdits => FileState.TypewriterEditsCount > 0;

    /// <summary>
    /// The ONLY non-saving way to clear pending type-over edits (#780).
    /// Exiting typewriter mode deliberately does NOT discard — that was the
    /// surprising silent-loss path — so this explicit, user-invoked command
    /// is the sanctioned "I changed my mind" exit.
    /// </summary>
    public void DiscardPendingTypewriterEdits()
    {
        if (TypewriterTextOperations.Count == 0)
            return;

        var discarded = TypewriterTextOperations.Count;
        ClearPendingTypewriterText();
        _logger.LogInformation("Discarded {Count} pending typewriter edit(s)", discarded);
    }

    /// <summary>
    /// Navigate to the next page that carries a pending type-over edit (#780).
    /// Off-page pending edits are otherwise invisible — the layer only renders
    /// ops on the current page — yet they still flatten on save. This lets a
    /// user reach every one before committing. Cyclic, starting after the
    /// current page.
    /// </summary>
    public void GoToNextPendingTypewriterEdit()
    {
        var pages = TypewriterTextOperations
            .Where(o => o.IsPending && o.HasText)
            .Select(o => o.PageNumber)
            .Distinct()
            .OrderBy(p => p)
            .ToList();
        if (pages.Count == 0)
            return;

        var current = CurrentPage; // 1-based
        var next = pages.FirstOrDefault(p => p > current);
        if (next == 0)
            next = pages[0]; // wrap around

        CurrentPageIndex = Math.Clamp(next - 1, 0, Math.Max(0, TotalPages - 1));
    }

    private void RefreshTypewriterEditState()
    {
        FileState.TypewriterEditsCount = TypewriterTextOperations.Count(o => o.IsPending && o.HasText);
        this.RaisePropertyChanged(nameof(SaveButtonText));
        this.RaisePropertyChanged(nameof(StatusBarText));
        this.RaisePropertyChanged(nameof(HasPendingTypewriterEdits));
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
