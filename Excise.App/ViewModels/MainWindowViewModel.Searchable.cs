using Microsoft.Extensions.Logging;
using Excise.Ocr;
using ReactiveUI;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Excise.App.ViewModels;

/// <summary>
/// "Make Searchable" GUI wiring (#658): OCRs the current document and
/// writes recognized words back as an invisible, searchable text layer.
/// The engine (<see cref="PdfSearchableConverter"/>) and its CLI
/// (<c>excise make-searchable</c>) already shipped in #627 — this partial
/// is only the View → ViewModel → Service orchestration, matching how
/// <c>MainWindowViewModel.Redaction.cs</c> orchestrates
/// <c>Excise.Core</c>'s redaction engine.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>
    /// Opens the "Make Searchable" dialog. Instantiates
    /// <see cref="PdfOcrService"/> directly here (mirrors
    /// <c>MainWindowViewModel.HiddenText.cs</c>'s
    /// <c>AddRasterizedHiddenTextHighlights</c>, which does the same rather
    /// than threading an OCR service through the constructor) rather than
    /// adding a new constructor-injected service just for a one-shot
    /// availability check and a background OCR call.
    /// </summary>
    private async Task MakeSearchableAsync()
    {
        if (!_documentService.IsDocumentLoaded)
        {
            _logger.LogWarning("Make Searchable requested with no document loaded");
            await _dialogService.ShowMessageAsync("Make Searchable", "Open a PDF before running OCR.");
            return;
        }

        var owner = GetMainWindow();
        if (owner == null)
        {
            _logger.LogWarning("Could not get main window for Make Searchable dialog");
            return;
        }

        var tesseractAvailable = new PdfOcrService().IsAvailable();

        var dialogViewModel = new MakeSearchableDialogViewModel(
            tesseractAvailable,
            RunMakeSearchableAsync);
        dialogViewModel.Completed += (_, result) => _ = OnMakeSearchableCompletedAsync(result);

        var window = new Views.MakeSearchableDialog
        {
            DataContext = dialogViewModel,
        };

        await window.ShowDialog(owner);
    }

    /// <summary>
    /// Runs the OCR pass on a background thread against the live in-memory
    /// document, so the modal dialog's progress bar and Cancel button stay
    /// responsive. The dialog is modal, so no concurrent GUI-driven mutation
    /// of the same document can race this.
    /// </summary>
    /// <remarks>
    /// Internal (not private) so integration tests can drive the real
    /// mutate → mark-dirty → refresh path end-to-end with a real
    /// PdfDocument and real tesseract, without needing a desktop
    /// <c>ApplicationLifetime</c> to satisfy <see cref="GetMainWindow"/>
    /// (headless tests have none — see <c>Excise.App.csproj</c>'s
    /// <c>InternalsVisibleTo</c> for <c>Excise.App.Tests</c>).
    /// </remarks>
    internal Task<SearchableDocumentResult> RunMakeSearchableAsync(
        string language,
        bool force,
        IProgress<(int Done, int Total)> progress,
        CancellationToken cancellationToken)
    {
        var document = _documentService.GetCurrentDocument();
        if (document == null)
            throw new InvalidOperationException("No document loaded.");

        var effectiveLanguage = string.IsNullOrWhiteSpace(language) ? "eng" : language.Trim();
        var ocrService = new PdfOcrService(language: effectiveLanguage);
        var converter = new PdfSearchableConverter(ocrService);

        // PdfSearchableConverter.MakeSearchable throws OperationCanceledException at the top of its
        // per-page loop, AFTER flushing invisible-text layers onto the pages it already did. Those
        // pages stay written in the live document, so a cancelled run is not a no-op: it gets the
        // same bookkeeping as a finished one (marked changed, history cleared) and tells the user
        // how many pages were written (#1692, #1812). Each page's own flush is self-consistent, so
        // the document is not corrupt; the user can close without saving to discard it.
        var pagesDone = 0;
        var tracking = new InlineProgress<(int Done, int Total)>(p =>
        {
            pagesDone = p.Done;
            progress.Report(p);
        });

        return Task.Run(async () =>
        {
            try
            {
                return converter.MakeSearchable(document, force, tracking, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => OnMakeSearchableCancelledAsync(pagesDone));
                throw;
            }
        }, CancellationToken.None);
    }

    /// <summary>Synchronous <see cref="IProgress{T}"/>: <c>Progress&lt;T&gt;</c> marshals asynchronously, which loses the last report at cancel.</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>
    /// A run cancelled after some pages were already written. Same bookkeeping as a finished run,
    /// plus a toast saying what was left behind. Nothing to do when no page was written.
    /// </summary>
    internal async Task OnMakeSearchableCancelledAsync(int pagesDone)
    {
        if (pagesDone <= 0)
            return;

        await OnMakeSearchableCompletedAsync(new SearchableDocumentResult(pagesDone, 0, 0, 0, Array.Empty<SearchablePageResult>()));
        _toastService.ShowWarning("Make Searchable cancelled",
            $"{pagesDone} page(s) already have a searchable text layer. The document is marked as changed; " +
            "close without saving to discard it.");
    }

    /// <summary>
    /// Mirrors <c>ApplyRedactionAsync</c>'s post-mutation bookkeeping: mark
    /// the in-memory document dirty and refresh the bound viewer/thumbnails
    /// so the (invisible) new text layer is reflected immediately. A no-op
    /// run (nothing processed, nothing written) skips the reload — there is
    /// nothing to refresh and it would just cost a render.
    /// </summary>
    internal async Task OnMakeSearchableCompletedAsync(SearchableDocumentResult result)
    {
        try
        {
            _logger.LogInformation(
                "Make Searchable complete: {Processed} processed, {Skipped} skipped, {Words} word(s) written, {SkippedEncoding} word(s) skipped (encoding)",
                result.PagesProcessed, result.PagesSkipped, result.TotalWordsWritten, result.TotalWordsSkippedEncoding);

            if (result.PagesProcessed == 0 && result.TotalWordsWritten == 0)
                return;

            // A baked text layer structurally rewrites the document; prior
            // undo entries no longer apply cleanly (#782), and this operation itself cannot be
            // undone, so it clears the history instead of joining it (#1812).
            ClearEditHistory();

            // The document now differs from the file on disk. This used to be missing, so a
            // searchable copy that was never saved read as "no unsaved changes" and closing it
            // discarded the OCR layer without a prompt.
            FileState.PageEditsCount++;
            this.RaisePropertyChanged(nameof(SaveButtonText));
            this.RaisePropertyChanged(nameof(StatusBarText));

            await RefreshAfterDocumentMutationAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing document after Make Searchable");
            _toastService.ShowError("Make Searchable", $"Document was updated, but the view could not be refreshed: {ex.Message}");
        }
    }
}
