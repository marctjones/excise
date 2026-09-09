using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Excise.App.Services;

namespace Excise.App.ViewModels;

/// <summary>
/// #1233 — the unsaved-changes guard.
/// </summary>
/// <remarks>
/// <para>
/// Before this existed, <c>MainWindow.Closing</c> persisted window geometry and
/// let the window go. Every pending redaction, page edit, form-field value,
/// typewriter box and annotation was discarded with no prompt, no toast and no
/// log line — the document was simply gone. <c>Ctrl+W</c>, quit, and opening a
/// second file had the same hole.
/// </para>
/// <para>
/// The decision lives here rather than in <c>MainWindow.axaml.cs</c> because it
/// is business logic (which edits count as dirty, how a save is routed so an
/// original is never overwritten, what happens when the save fails). The
/// code-behind keeps only the Avalonia-specific part it cannot delegate:
/// cancelling the synchronous <c>Closing</c> event and re-issuing the close.
/// </para>
/// </remarks>
public partial class MainWindowViewModel
{
    /// <summary>
    /// Test seam: set to <c>true</c> once a close has been approved so the
    /// re-entrant close does not prompt again. Owned by the view; see
    /// <c>MainWindow.OnWindowClosing</c>.
    /// </summary>
    internal bool UnsavedChangesPromptShownForTesting { get; private set; }

    /// <summary>
    /// Whether a destructive transition would lose work right now.
    /// </summary>
    /// <remarks>
    /// Exposed so the view's <c>Closing</c> handler can decide synchronously
    /// whether it must cancel the close at all — the prompt itself is async,
    /// but this question is not, and the overwhelmingly common case (nothing
    /// dirty) must close without a round trip through the dispatcher.
    /// Combines the document-loaded check with
    /// <c>DocumentStateManager.HasUnsavedChanges</c>, whose counters
    /// deliberately outlive a document close.
    /// </remarks>
    public bool HasUnsavedDocumentChanges =>
        _documentService.IsDocumentLoaded && FileState.HasUnsavedChanges;

    /// <summary>
    /// Gate a destructive transition (window close, application quit, document
    /// close, opening a different file) on the user's decision about unsaved
    /// changes.
    /// </summary>
    /// <param name="actionDescription">
    /// What the user asked for, in a form that completes the sentence
    /// "... before you &lt;actionDescription&gt;" — e.g. "close excise".
    /// </param>
    /// <returns>
    /// <c>true</c> when it is safe to proceed: there was nothing unsaved, the
    /// changes were saved successfully, or the user explicitly chose to discard
    /// them. <c>false</c> means ABANDON the transition and leave the document
    /// open and still dirty — the user cancelled, or the save did not happen.
    /// </returns>
    public async Task<bool> ConfirmDiscardUnsavedChangesAsync(string actionDescription)
    {
        if (!_documentService.IsDocumentLoaded || !FileState.HasUnsavedChanges)
            return true;

        UnsavedChangesPromptShownForTesting = true;

        var decision = await _dialogService.ShowUnsavedChangesAsync(
            "Unsaved Changes",
            BuildUnsavedChangesMessage(actionDescription),
            FileState.GetSaveButtonText());

        switch (decision)
        {
            case UnsavedChangesDecision.Discard:
                _logger.LogInformation(
                    "Unsaved changes discarded by explicit user choice before {Action}", actionDescription);
                return true;

            case UnsavedChangesDecision.Save:
                // Reuse the ordinary Save routing verbatim. That is what makes
                // "preserving originals by default" true here without
                // restating the rule: SaveFileAsync sends an ORIGINAL with
                // pending redactions through the redacted-copy workflow, an
                // original with any other edit through Save As, and only ever
                // writes in place when the open file is already a derived copy.
                await SaveFileAsync();

                // Do not trust the save to have happened. The picker can be
                // cancelled, the signed-document warning can be declined, and
                // SaveFileAsAsync swallows its own IO exceptions — in all three
                // the counters stay non-zero. Re-reading them is the only
                // honest completion signal available, and it fails toward
                // keeping the document open.
                if (FileState.HasUnsavedChanges)
                {
                    _logger.LogInformation(
                        "Save did not complete (cancelled, declined or failed) - {Action} abandoned, document left open and dirty",
                        actionDescription);
                    return false;
                }

                _logger.LogInformation("Unsaved changes saved before {Action}", actionDescription);
                return true;

            case UnsavedChangesDecision.Cancel:
            default:
                _logger.LogInformation("{Action} cancelled by the user at the unsaved-changes prompt", actionDescription);
                return false;
        }
    }

    /// <summary>
    /// Enumerate what is actually at risk, so the prompt names the work rather
    /// than saying "you have unsaved changes" and leaving the user to guess
    /// whether it is worth keeping.
    /// </summary>
    private string BuildUnsavedChangesMessage(string actionDescription)
    {
        var parts = new List<string>();
        void Add(int count, string singular, string plural)
        {
            if (count > 0)
                parts.Add($"{count} {(count == 1 ? singular : plural)}");
        }

        Add(FileState.PendingRedactionsCount, "pending redaction", "pending redactions");
        Add(FileState.RemovedPagesCount, "removed page", "removed pages");
        Add(FileState.PageEditsCount, "page edit", "page edits");
        Add(FileState.FormFieldEditsCount, "form field edit", "form field edits");
        Add(FileState.TypewriterEditsCount, "typewriter edit", "typewriter edits");
        Add(FileState.AnnotationEditsCount, "annotation edit", "annotation edits");

        var summary = parts.Count == 0
            ? "unsaved changes"
            : string.Join(", ", parts);

        var name = string.IsNullOrWhiteSpace(DocumentName) ? "This document" : DocumentName;

        var message = $"{name} has {summary} that have not been saved.";

        if (FileState.IsOriginalFile)
        {
            // Say this explicitly. A user who believes "Save" overwrites their
            // only copy of an original will pick Discard to protect it — and
            // lose the work for a reason that is not true.
            message += $"\n\nSaving writes a COPY — the original file is never overwritten.";
        }

        message += $"\n\nWhat would you like to do before you {actionDescription}?";
        return message;
    }
}
