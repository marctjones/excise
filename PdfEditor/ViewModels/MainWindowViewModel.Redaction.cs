using Avalonia;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Pdfe.Core.Document;
using PdfEditor.Models;
using PdfEditor.Services;
using ReactiveUI;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace PdfEditor.ViewModels;

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

        string previewText = string.Empty;
        if (!string.IsNullOrEmpty(_currentFilePath))
        {
            try
            {
                previewText = _textExtractionService.ExtractTextFromArea(
                    _currentFilePath,
                    CurrentPageIndex,
                    pageArea);
                _logger.LogInformation("Preview text extracted: '{Text}'", previewText);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not extract preview text");
            }
        }

        RedactionWorkflow.MarkArea(pageArea, previewText);
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

            var mainWindow = GetMainWindow();
            if (mainWindow == null)
            {
                _logger.LogError("Could not get main window for dialog");
                return;
            }

            var saveFile = await ShowSaveRedactedFileDialog(mainWindow, suggestedPath);
            if (saveFile == null)
            {
                _logger.LogInformation("User cancelled save file picker");
                return;
            }

            var saveFilePath = saveFile.Path.LocalPath;
            _logger.LogInformation("Applying {Count} redactions to create: {Path}", RedactionWorkflow.PendingCount, saveFilePath);

            var document = _documentService.GetCurrentDocument();
            if (document == null)
            {
                _logger.LogError("Document is null");
                return;
            }

            if (!await ConfirmEncryptionLossIfNeededAsync(document.IsEncrypted))
            {
                _logger.LogInformation("User declined to save a copy that would drop source encryption");
                return;
            }

            var requestedRedactions = RedactionWorkflow.PendingRedactions.ToList();
            var skippedRedactionCount = ApplyPendingAreaRedactions(document);
            ApplyPendingTypewriterText(document);
            var report = _redactedCopySafetyService.PrepareRedactedCopy(
                document,
                requestedRedactions,
                skippedRedactionCount);

            _logger.LogInformation("Saving redacted PDF to: {Path}", saveFilePath);
            document.Save(saveFilePath);
            _hasInMemoryModifications = false;

            RedactionWorkflow.MoveToApplied();
            FileState.PendingRedactionsCount = 0;
            ClearPendingTypewriterText();
            this.RaisePropertyChanged(nameof(SaveButtonText));
            this.RaisePropertyChanged(nameof(StatusBarText));

            _logger.LogInformation("Redacted PDF saved successfully");

            if (IsRedactionMode)
            {
                ToggleRedactionMode();
            }

            _logger.LogInformation("Reloading saved document: {Path}", saveFilePath);
            await LoadDocumentAsync(saveFilePath);

            await _dialogService.ShowMessageAsync("Success", _redactedCopySafetyService.FormatForDialog(saveFilePath, report));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying all redactions");
            await _dialogService.ShowMessageAsync("Error", $"Failed to apply redactions: {ex.Message}");
        }
    }

    /// <summary>
    /// Apply redaction immediately (legacy immediate-apply workflow)
    /// See issue #19: Implement "Apply All Redactions" button for mark-then-apply workflow
    /// </summary>
    private async Task ApplyRedactionAsync()
    {
        _logger.LogInformation(">>> ApplyRedactionAsync START. IsRedactionMode={Mode}, Area=({X:F2},{Y:F2},{W:F2}x{H:F2})",
            IsRedactionMode, CurrentRedactionArea.X, CurrentRedactionArea.Y, CurrentRedactionArea.Width, CurrentRedactionArea.Height);

        if (IsRedactionMode && TryGetCurrentRedactionPageArea(out _))
        {
            MarkRedactionArea();
            return;
        }

        if (!IsRedactionMode || !TryGetCurrentRedactionPageArea(out var areaToRedact))
        {
            _logger.LogWarning("ApplyRedactionAsync returning early: IsRedactionMode={Mode}, Width={W}, Height={H}",
                IsRedactionMode, CurrentRedactionArea.Width, CurrentRedactionArea.Height);
            return;
        }

        try
        {
            var document = _documentService.GetCurrentDocument();
            if (document == null)
            {
                _logger.LogWarning("ApplyRedactionAsync: document is null");
                return;
            }

            string redactedText = string.Empty;
            if (!string.IsNullOrEmpty(_currentFilePath))
            {
                try
                {
                    redactedText = _textExtractionService.ExtractTextFromArea(
                        _currentFilePath,
                        CurrentPageIndex,
                        areaToRedact);
                    _logger.LogInformation("Text to be redacted: '{Text}'", redactedText);
                }
                catch (Exception textEx)
                {
                    _logger.LogWarning(textEx, "Could not extract text before redaction");
                }
            }

            _logger.LogInformation("Applying redaction (selection area: {X:F2},{Y:F2},{W:F2}x{H:F2})",
                areaToRedact.X, areaToRedact.Y, areaToRedact.Width, areaToRedact.Height);

            var page = document.Pages[CurrentPageIndex];
            _redactionService.RedactArea(page, areaToRedact);
            _hasInMemoryModifications = true;

            if (!string.IsNullOrWhiteSpace(redactedText))
            {
                var clipboardEntry = new ClipboardEntry
                {
                    Text = redactedText,
                    Timestamp = DateTime.Now,
                    PageNumber = CurrentPageIndex + 1,
                    IsRedacted = true
                };

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ClipboardHistory.Insert(0, clipboardEntry);

                    while (ClipboardHistory.Count > 20)
                    {
                        ClipboardHistory.RemoveAt(ClipboardHistory.Count - 1);
                    }
                });

                _logger.LogInformation("Added redacted text to clipboard history: '{Text}'", redactedText);
            }

            _logger.LogInformation("Redaction applied to in-memory document, refreshing bound viewer document...");
            await ReloadPdfCoreDocumentFromCurrentDocumentAsync();

            _logger.LogInformation("Redaction complete - draw another selection or click 'Redact Mode' to exit.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying redaction");
            _toastService.ShowError("Redaction failed", ex.Message);
        }
        finally
        {
            CurrentRedactionPageArea = null;
            _logger.LogInformation("<<< ApplyRedactionAsync END. Selection cleared, ready for next redaction.");
        }
    }

    private int ApplyPendingAreaRedactions(Pdfe.Core.Document.PdfDocument document)
    {
        var skippedCount = 0;

        foreach (var pending in RedactionWorkflow.PendingRedactions.ToList())
        {
            if (pending.PageNumber < 1 || pending.PageNumber > document.PageCount)
            {
                _logger.LogWarning(
                    "Skipping pending redaction for invalid page {Page}. Document has {PageCount} pages.",
                    pending.PageNumber,
                    document.PageCount);
                skippedCount++;
                continue;
            }

            _logger.LogInformation(
                "Applying redaction on page {Page} from {Space}",
                pending.PageNumber,
                pending.PageArea.Space);

            var page = document.Pages[pending.PageNumber - 1];
            _redactionService.RedactArea(page, pending.PageArea);
        }

        return skippedCount;
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
