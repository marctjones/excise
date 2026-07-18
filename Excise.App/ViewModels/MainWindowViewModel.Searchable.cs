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

        // Known limitation: PdfSearchableConverter.MakeSearchable throws
        // OperationCanceledException at the top of its per-page loop, after
        // already having flushed invisible-text layers onto any pages
        // processed before the cancellation. Because the exception means
        // OnMakeSearchableCompletedAsync never runs, those already-written
        // pages stay mutated in the live document but the app doesn't mark
        // itself dirty for them (no doc-mutation "Completed" event fires).
        // Each page's own flush is self-consistent (not corrupt), and on
        // the common "cancel and discard" path this is arguably the right
        // behavior anyway — but a "cancel then Save" sequence would silently
        // keep the partial OCR layer without prompting a save. Not
        // reworked here (would need copy-then-swap semantics); flag if it
        // becomes a real workflow.
        return Task.Run(
            () => converter.MakeSearchable(document, force, progress, cancellationToken),
            cancellationToken);
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

            _hasInMemoryModifications = true;
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
