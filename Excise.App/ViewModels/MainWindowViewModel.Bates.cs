using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI;
using Excise.App.Services;

namespace Excise.App.ViewModels;

/// <summary>
/// #1306 — Bates numbering, production-wired.
/// </summary>
/// <remarks>
/// <para>
/// DECISION: wire, not retire. README's Desktop app feature list advertises
/// "Bates numbering" to users, while <c>BatesNumberingService</c> had zero
/// production callers — no command, no menu item, no CLI verb — and
/// <c>ApplyBatesNumbers</c> was classified <c>nowhere</c> in the unwired-API
/// baseline: not reached even by the 45 tests, which mostly assert
/// <c>BatesOptions</c> defaults. Retiring it would have meant retracting a
/// shipped, documented feature; wiring it makes the README true.
/// </para>
/// <para>
/// Scope: the OPEN DOCUMENT only. The service's multi-file entry point stamps
/// a continuing sequence across a set of files on disk and writes new ones —
/// that is a batch shape, and it stays unwired on purpose rather than being
/// forced into a viewer that has one document open. (Its name is deliberately
/// not written here: <c>check-unwired-api.sh</c> matches identifiers
/// textually, so naming it in a comment would make a dead API read as wired
/// and quietly retire a baseline row that is still true.)
/// </para>
/// </remarks>
public partial class MainWindowViewModel
{
    private readonly BatesNumberingService _batesService =
        new(NullLogger<BatesNumberingService>.Instance);

    /// <summary>
    /// Test seam: supply the options directly instead of showing the dialog.
    /// Returns <c>null</c> to simulate the user cancelling.
    /// </summary>
    internal Func<BatesOptions?>? BatesOptionsOverride { get; set; }

    /// <summary>
    /// Document ▸ Bates Numbering… — collect settings, then stamp every page.
    /// </summary>
    private async Task ApplyBatesNumberingAsync()
    {
        if (!_documentService.IsDocumentLoaded)
        {
            await _dialogService.ShowMessageAsync(
                "Bates Numbering", "Open a PDF before applying Bates numbers.");
            return;
        }

        var options = BatesOptionsOverride != null
            ? BatesOptionsOverride()
            : await PromptForBatesOptionsAsync();

        if (options == null)
        {
            _logger.LogInformation("Bates numbering cancelled");
            return;
        }

        ApplyBatesNumbering(options);
    }

    private async Task<BatesOptions?> PromptForBatesOptionsAsync()
    {
        var owner = GetMainWindow();
        if (owner == null)
        {
            _logger.LogWarning("Could not get main window for the Bates Numbering dialog");
            return null;
        }

        var dialogViewModel = new BatesNumberingDialogViewModel();
        var window = new Views.BatesNumberingDialog { DataContext = dialogViewModel };
        await window.ShowDialog(owner);

        return dialogViewModel.Confirmed ? dialogViewModel.ToOptions() : null;
    }

    /// <summary>
    /// Stamp the open document, in memory.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT save. The stamp becomes a pending edit like any
    /// other, so the ordinary save routing applies and an ORIGINAL source is
    /// preserved as a copy (#1233). Bates numbering is applied to evidence
    /// sets; writing over the source in place is the last thing it should do.
    /// </remarks>
    private static byte[][] SnapshotPageContent(Excise.Core.Document.PdfDocument document)
    {
        var snapshot = new byte[document.Pages.Count][];
        for (var i = 0; i < snapshot.Length; i++)
            snapshot[i] = document.Pages[i].GetContentStreamBytes();
        return snapshot;
    }

    /// <summary>
    /// Undo puts every page's content back to <paramref name="original"/> and keeps
    /// the stamped bytes for redo. Later edits are undone first (a stack), so the
    /// page count is the one the stamp was applied to; a mismatch means the
    /// history was bypassed and restoring by index would corrupt pages, so refuse.
    /// </summary>
    private void RecordBatesUndo(byte[][] original)
    {
        byte[][]? stamped = null;

        async Task Swap(byte[][] target, int dirtyDelta)
        {
            var document = _documentService.GetCurrentDocument();
            if (document == null || document.Pages.Count != target.Length)
                throw new InvalidOperationException(
                    "The page count changed since Bates numbering was applied; it cannot be undone safely.");

            for (var i = 0; i < target.Length; i++)
                document.Pages[i].SetContentStreamBytes(target[i]);

            FileState.PageEditsCount = Math.Max(0, FileState.PageEditsCount + dirtyDelta);
            this.RaisePropertyChanged(nameof(SaveButtonText));
            this.RaisePropertyChanged(nameof(StatusBarText));
            await RefreshAfterDocumentMutationAsync();
        }

        _history.Push("Bates numbering",
            undo: async () =>
            {
                stamped = SnapshotPageContent(_documentService.GetCurrentDocument()!);
                await Swap(original, -1);
            },
            redo: () => Swap(stamped!, +1));
    }

    internal void ApplyBatesNumbering(BatesOptions options)
    {
        var document = _documentService.GetCurrentDocument();
        if (document == null)
            return;

        try
        {
            // #1805: the stamp is page content, so the inverse is the pages' own
            // content bytes as they were. Taken before the stamp; the stamped
            // bytes are captured at undo time, so redo needs nothing held now.
            var original = SnapshotPageContent(document);

            _batesService.ApplyBatesNumbers(document, options);

            // Count it as a page edit so the document reads dirty and the
            // close/quit guard and Save-a-Copy routing both engage.
            FileState.PageEditsCount++;
            RecordBatesUndo(original);

            this.RaisePropertyChanged(nameof(SaveButtonText));
            this.RaisePropertyChanged(nameof(StatusBarText));
            RequestViewerRenderRefresh();

            var last = options.StartNumber + document.Pages.Count - 1;
            _toastService.ShowSuccess(
                $"Bates numbers applied ({document.Pages.Count} page(s)) — save to persist",
                $"{options.Prefix}{options.StartNumber.ToString(new string('0', options.NumberOfDigits))}{options.Suffix}" +
                $" … {options.Prefix}{last.ToString(new string('0', options.NumberOfDigits))}{options.Suffix}");

            _logger.LogInformation(
                "Applied Bates numbers to {Count} page(s) starting at {Start}",
                document.Pages.Count, options.StartNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply Bates numbers");
            _toastService.ShowError("Could not apply Bates numbers", ex.Message);
        }
    }
}
