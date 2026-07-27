using Microsoft.Extensions.Logging;
using Excise.Core.Document;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Reactive;
using System.Threading.Tasks;

namespace Excise.App.ViewModels;

/// <summary>
/// App-wide, in-session undo/redo (#782). Reversible pre-commit editing state —
/// type-over boxes, annotation authoring, and page reorder/rotate/delete — is
/// routed through <see cref="_history"/>; content already flattened/baked into
/// the content stream on save is irreversible by design and is never recorded
/// (the stack is cleared on every open, close, and save).
/// </summary>
public partial class MainWindowViewModel
{
    private readonly Services.EditHistoryService _history = new();

    public ReactiveCommand<Unit, Unit> UndoCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> RedoCommand { get; private set; } = null!;

    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;

    /// <summary>Menu label including the pending action's description (e.g. "_Undo Rotate page left").</summary>
    public string UndoMenuHeader =>
        _history.CanUndo && !string.IsNullOrEmpty(_history.UndoDescription)
            ? $"_Undo {_history.UndoDescription}"
            : "_Undo";

    public string RedoMenuHeader =>
        _history.CanRedo && !string.IsNullOrEmpty(_history.RedoDescription)
            ? $"_Redo {_history.RedoDescription}"
            : "_Redo";

    private void InitializeHistory()
    {
        _history.Changed += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(CanUndo));
            this.RaisePropertyChanged(nameof(CanRedo));
            this.RaisePropertyChanged(nameof(UndoMenuHeader));
            this.RaisePropertyChanged(nameof(RedoMenuHeader));
        };

        // Executability is surfaced to the UI through the CanUndo/CanRedo
        // bindings (menu IsEnabled); the commands themselves stay always-
        // invocable and no-op when the stack is empty. A WhenAnyValue gate is
        // deliberately avoided — it forces ReactiveUI's global initialization,
        // which the rest of this VM (and the headless test host) does not rely
        // on, and an empty undo/redo is a harmless no-op regardless.
        UndoCommand = ReactiveCommand.CreateFromTask(_history.UndoAsync);
        RedoCommand = ReactiveCommand.CreateFromTask(_history.RedoAsync);

        UndoCommand.ThrownExceptions.Subscribe(ex => _logger.LogError(ex, "UndoCommand threw exception"));
        RedoCommand.ThrownExceptions.Subscribe(ex => _logger.LogError(ex, "RedoCommand threw exception"));
    }

    /// <summary>
    /// Drop all undo/redo history. Invoked on document open/close and after a
    /// successful save (once edits are flattened they are no longer reversible).
    /// </summary>
    private void ClearEditHistory() => _history.Clear();

    // ── Annotation authoring (#782) ─────────────────────────────────────────

    /// <summary>
    /// Record a just-added annotation so it can be reverted. Undo removes it
    /// from the authoritative save document and rebuilds the viewer through the
    /// standard reload path; redo re-adds it (a new annotation object, tracked
    /// through the shared <paramref name="reAdd"/> holder since RemoveAnnotation
    /// matches by reference).
    /// </summary>
    private void RecordAnnotationAdd(
        string description,
        int pageNumber,
        PdfAnnotation added,
        Func<PdfAnnotation> reAdd)
    {
        var current = added;
        _history.Push(
            description,
            undo: async () =>
            {
                _documentService.GetCurrentDocument()?.RemoveAnnotation(pageNumber, current);
                AdjustAnnotationBookkeeping(-1);
                await RefreshAfterDocumentMutationAsync();
            },
            redo: async () =>
            {
                current = reAdd();
                AdjustAnnotationBookkeeping(1);
                await RefreshAfterDocumentMutationAsync();
            });
    }

    private void AdjustAnnotationBookkeeping(int delta)
    {
        FileState.AnnotationEditsCount = Math.Max(0, FileState.AnnotationEditsCount + delta);
        _hasInMemoryModifications = true;
        this.RaisePropertyChanged(nameof(SaveButtonText));
        this.RaisePropertyChanged(nameof(StatusBarText));
        AnnotationsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Page operations (#782) — undo/redo apply clean inverses through the ──
    // low-level document mutators directly, never the recording command paths,
    // so replay cannot re-enter the history or surface confirmation dialogs.

    private async Task ApplyPageRotationAsync(int pageIndex, int degrees)
    {
        _documentService.RotatePage(pageIndex, degrees);
        MarkPageOrganizationChanged();
        await RefreshAfterDocumentMutationAsync();
    }

    private async Task MovePageInternalAsync(int fromIndex, int toIndex)
    {
        var newCurrent = RemapCurrentPageAfterSingleMove(CurrentPageIndex, fromIndex, toIndex);
        _documentService.MovePage(fromIndex, toIndex);
        CurrentPageIndex = newCurrent;
        MarkPageOrganizationChanged();
        await RefreshAfterDocumentMutationAsync();
    }

    private async Task MoveSelectedPagesInternalAsync(IReadOnlyList<int> indices, int delta)
    {
        var newPositions = _documentService.MovePages(indices, delta);
        CurrentPageIndex = Math.Clamp(CurrentPageIndex, 0, Math.Max(0, _documentService.PageCount - 1));
        MarkPageOrganizationChanged();
        await RefreshAfterDocumentMutationAsync();
        RestoreSelectedPages(newPositions);
    }

    private async Task ReinsertPagesAsync(IReadOnlyList<(int Index, PdfPage Page)> pages)
    {
        var document = _documentService.GetCurrentDocument();
        if (document == null)
            return;

        // Ascending order: each insert at its original index makes room for the next.
        foreach (var (index, page) in pages)
            document.Pages.Insert(Math.Clamp(index, 0, document.PageCount), page);

        CurrentPageIndex = Math.Clamp(pages.Count > 0 ? pages[0].Index : CurrentPageIndex,
            0, Math.Max(0, _documentService.PageCount - 1));
        MarkPageOrganizationChanged();
        await RefreshAfterDocumentMutationAsync();
    }

    private async Task RemovePagesInternalAsync(IReadOnlyList<int> indices)
    {
        _documentService.RemovePages(indices);
        CurrentPageIndex = Math.Clamp(CurrentPageIndex, 0, Math.Max(0, _documentService.PageCount - 1));
        MarkPageOrganizationChanged(removedPage: true, removedPageCount: indices.Count);
        await RefreshAfterDocumentMutationAsync();
    }

    /// <summary>
    /// Snapshot the pages at <paramref name="indices"/> (original order) so a
    /// deletion can be undone by re-inserting them. RemoveAt keeps the removed
    /// page's object graph in the store, so Insert can clone it back verbatim.
    /// </summary>
    private IReadOnlyList<(int Index, PdfPage Page)> CapturePages(IReadOnlyList<int> indices)
    {
        var document = _documentService.GetCurrentDocument();
        var captured = new List<(int, PdfPage)>();
        if (document == null)
            return captured;

        foreach (var index in indices)
        {
            if (index >= 0 && index < document.PageCount)
                captured.Add((index, document.GetPage(index + 1)));
        }
        return captured;
    }
}
