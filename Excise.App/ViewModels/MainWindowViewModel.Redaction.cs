using Avalonia;
using Microsoft.Extensions.Logging;
using Excise.Core.Document;
using Excise.App.Services;
using ReactiveUI;
using System;
using System.Threading.Tasks;

namespace Excise.App.ViewModels;

public partial class MainWindowViewModel
{
    private void ToggleRedactionMode()
    {
        IsRedactionMode = !IsRedactionMode;
        if (IsRedactionMode && _isTextSelectionMode)
            IsTextSelectionMode = false;
    }

    /// <summary>
    /// Mark a redaction area (mark-then-apply workflow) - adds to pending list
    /// </summary>
    private void MarkRedactionArea()
    {
        _logger.LogInformation(">>> MarkRedactionArea START. Area=({X:F2},{Y:F2},{W:F2}x{H:F2})",
            CurrentRedactionArea.X, CurrentRedactionArea.Y, CurrentRedactionArea.Width, CurrentRedactionArea.Height);

        if (!IsRedactionMode || !TryGetCurrentRedactionPageArea(out var pageArea))
        {
            _logger.LogWarning("MarkRedactionArea returning early: IsRedactionMode={Mode}, Width={W}, Height={H}",
                IsRedactionMode, CurrentRedactionArea.Width, CurrentRedactionArea.Height);
            return;
        }

        var mark = _redactionWorkflowService.CaptureMark(
            new RedactionMarkRequest(_currentFilePath, CurrentPageIndex, pageArea));
        _logger.LogInformation("Preview text extracted: '{Text}'", mark.PreviewText);

        RedactionWorkflow.MarkArea(mark.PageArea, mark.PreviewText);
        FileState.PendingRedactionsCount = RedactionWorkflow.PendingCount;
        this.RaisePropertyChanged(nameof(SaveButtonText));
        this.RaisePropertyChanged(nameof(StatusBarText));

        _logger.LogInformation("Redaction marked. Total pending: {Count}", RedactionWorkflow.PendingCount);
        _logger.LogInformation("DEBUG: RedactionWorkflow.PendingRedactions.Count = {Count}", RedactionWorkflow.PendingRedactions.Count);

        CurrentRedactionPageArea = null;
    }

    /// <summary>
    /// Remove a pending redaction by ID
    /// </summary>
    private void RemovePendingRedaction(Guid id)
    {
        _logger.LogInformation("Removing pending redaction: {Id}", id);

        if (RedactionWorkflow.RemovePending(id))
        {
            FileState.PendingRedactionsCount = RedactionWorkflow.PendingCount;
            this.RaisePropertyChanged(nameof(SaveButtonText));
            _logger.LogInformation("Pending redaction removed. Remaining: {Count}", RedactionWorkflow.PendingCount);
        }
        else
        {
            _logger.LogWarning("Could not find pending redaction with ID: {Id}", id);
        }
    }

    /// <summary>
    /// Clear all pending redactions
    /// </summary>
    private void ClearAllRedactions()
    {
        _logger.LogInformation("Clearing all pending redactions. Count: {Count}", RedactionWorkflow.PendingCount);

        RedactionWorkflow.ClearPending();
        FileState.PendingRedactionsCount = 0;
        this.RaisePropertyChanged(nameof(SaveButtonText));
        this.RaisePropertyChanged(nameof(StatusBarText));

        _logger.LogInformation("All pending redactions cleared");
    }

    /// <summary>
    /// Apply all pending redactions to create a redacted version of the PDF
    /// </summary>
    private async Task ApplyAllRedactionsAsync()
    {
        _logger.LogInformation("ApplyAllRedactionsAsync START. Pending count: {Count}", RedactionWorkflow.PendingCount);

        if (RedactionWorkflow.PendingCount == 0)
        {
            _logger.LogWarning("No pending redactions to apply");
            await _dialogService.ShowMessageAsync("No Redactions", "There are no pending redactions to apply.");
            return;
        }

        if (string.IsNullOrEmpty(_currentFilePath))
        {
            _logger.LogWarning("No document loaded");
            await _dialogService.ShowMessageAsync("No Document", "Please open a PDF document first.");
            return;
        }

        try
        {
            var suggestedPath = _filenameSuggestionService.SuggestRedactedFilename(_currentFilePath);

            var saveFilePath = await ResolveRedactedSavePathAsync(suggestedPath);
            if (saveFilePath == null)
            {
                _logger.LogInformation("User cancelled save file picker (or no destination available)");
                return;
            }

            _logger.LogInformation("Applying {Count} redactions to create: {Path}", RedactionWorkflow.PendingCount, saveFilePath);

            var document = _documentService.GetCurrentDocument();
            if (document == null)
            {
                _logger.LogError("Document is null");
                return;
            }

            var request = RedactedCopyRequest.Capture(
                document,
                RedactionWorkflow.PendingRedactions,
                TypewriterTextOperations,
                saveFilePath,
                _documentService.GetReEncryptionOptions());
            var result = _redactionWorkflowService.CreateRedactedCopy(request);
            await PublishRedactedCopySuccessAsync(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying all redactions");
            await _dialogService.ShowMessageAsync("Error", $"Failed to apply redactions: {ex.Message}");
        }
    }

    private async Task PublishRedactedCopySuccessAsync(RedactedCopyResult result)
    {
        RedactionWorkflow.MoveToApplied();
        FileState.PendingRedactionsCount = 0;
        ClearPendingTypewriterText();
        ClearEditHistory();
        this.RaisePropertyChanged(nameof(SaveButtonText));
        this.RaisePropertyChanged(nameof(StatusBarText));

        _logger.LogInformation("Redacted PDF saved successfully");
        if (IsRedactionMode)
            ToggleRedactionMode();

        _logger.LogInformation("Reloading saved document: {Path}", result.OutputPath);
        await LoadDocumentAsync(result.OutputPath);
        await _dialogService.ShowMessageAsync(
            "Success",
            _redactedCopySafetyService.FormatForDialog(
                result.OutputPath,
                result.Application.SafetyReport));
    }

    /// <summary>
    /// Capture the current selection as a pending redaction. Glyph removal is
    /// intentionally deferred to Apply All so there is one canonical engine
    /// transaction and one redacted-copy safety boundary.
    /// </summary>
    private Task MarkCurrentRedactionAsync()
    {
        _logger.LogInformation(">>> MarkCurrentRedactionAsync START. IsRedactionMode={Mode}, Area=({X:F2},{Y:F2},{W:F2}x{H:F2})",
            IsRedactionMode, CurrentRedactionArea.X, CurrentRedactionArea.Y, CurrentRedactionArea.Width, CurrentRedactionArea.Height);
        MarkRedactionArea();
        return Task.CompletedTask;
    }

    // Test seam mirroring AppPaths.OverrideForTests: headless UI tests have no
    // desktop-lifetime MainWindow and no interactive save picker, so the real
    // ApplyAllRedactionsCommand cannot resolve a destination and would bail
    // before the redaction pipeline runs. When set, this supplies the save
    // destination directly (return null to model a cancelled picker). Every
    // step after path resolution — PrepareRedactedCopy, document.Save,
    // MoveToApplied, reload — runs unchanged. Unset in production, so behaviour
    // is identical to the real save dialog. See RedactionAndSearchCommandTests.
    private Func<string, Task<string?>>? _redactedSavePathProviderForTests;

    internal void SetRedactedSavePathProviderForTests(Func<string, Task<string?>>? provider)
        => _redactedSavePathProviderForTests = provider;

    private async Task<string?> ResolveRedactedSavePathAsync(string suggestedPath)
    {
        if (_redactedSavePathProviderForTests != null)
            return await _redactedSavePathProviderForTests(suggestedPath);

        var mainWindow = GetMainWindow();
        if (mainWindow == null)
        {
            _logger.LogError("Could not get main window for dialog");
            return null;
        }

        var saveFile = await ShowSaveRedactedFileDialog(mainWindow, suggestedPath);
        return saveFile?.Path.LocalPath;
    }

    private bool TryGetCurrentRedactionPageArea(out PdfPageRect pageArea)
    {
        if (CurrentRedactionPageArea is { Width: > 0, Height: > 0 } current)
        {
            pageArea = current;
            return true;
        }

        if (CurrentRedactionArea.Width <= 0 || CurrentRedactionArea.Height <= 0)
        {
            pageArea = default;
            return false;
        }

        pageArea = PdfPageRect.ViewerDips(
            CurrentPageIndex + 1,
            CurrentRedactionArea.X,
            CurrentRedactionArea.Y,
            CurrentRedactionArea.Width,
            CurrentRedactionArea.Height,
            CurrentRedactionRenderDpi);
        return true;
    }
}
