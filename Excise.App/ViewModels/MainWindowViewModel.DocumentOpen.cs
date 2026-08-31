using Avalonia;
using Avalonia.Platform.Storage;
using Excise.App.Services;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using PdfCoreDocument = Excise.Core.Document.PdfDocument;

namespace Excise.App.ViewModels;

public sealed record DocumentOpenTiming(
    string FilePath,
    int PageCount,
    long DocumentInstancesLoadedElapsedMs,
    long FirstPageVisibleElapsedMs,
    long ThumbnailPlaceholdersReadyElapsedMs,
    long OutlineReadyElapsedMs,
    long SearchIndexStartedElapsedMs,
    long TotalLoadElapsedMs);

public partial class MainWindowViewModel
{
    private sealed class DocumentOpenStageTimings
    {
        public long DocumentInstancesLoadedElapsedMs { get; set; }
        public long FirstPageVisibleElapsedMs { get; set; }
        public long ThumbnailPlaceholdersReadyElapsedMs { get; set; }
        public long OutlineReadyElapsedMs { get; set; }
        public long SearchIndexStartedElapsedMs { get; set; }
    }

    private async Task OpenFileAsync()
    {
        _logger.LogInformation("Open file command triggered");

        var storageProvider = GetStorageProvider();
        if (storageProvider == null)
        {
            _logger.LogWarning("Storage provider unavailable, cannot show Open dialog");
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open PDF File",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("PDF Files")
                {
                    Patterns = ["*.pdf"]
                }
            ]
        });

        if (files.Count == 0)
        {
            _logger.LogInformation("Open dialog cancelled");
            return;
        }

        var filePath = files[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            _logger.LogWarning("Selected file has no local path");
            return;
        }

        await LoadDocumentAsync(filePath);
    }

    public async Task LoadDocumentAsync(string filePath)
    {
        ValidateDocumentPath(filePath);

        _logger.LogInformation(">>> STEP 1: LoadDocumentAsync START for: {FilePath}", filePath);
        var stopwatch = Stopwatch.StartNew();
        var timings = new DocumentOpenStageTimings();

        try
        {
            PrepareDocumentOpen(filePath);
            PdfCoreDocument = await AcquireDocumentAsync(filePath);
            timings.DocumentInstancesLoadedElapsedMs = stopwatch.ElapsedMilliseconds;

            await ActivateDocumentAsync(filePath, stopwatch, timings);
            await CompleteDocumentOpenAsync(filePath, stopwatch, timings);
        }
        catch (Exception ex)
        {
            await HandleDocumentOpenFailureAsync(filePath, ex);
        }
    }

    private static void ValidateDocumentPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"PDF file not found: {filePath}", filePath);
    }

    private void PrepareDocumentOpen(string filePath)
    {
        _logger.LogInformation(">>> STEP 2: Clearing previous document state");
        LastDocumentOpenTiming = null;
        _textIndexSession.Cancel();
        CurrentRedactionArea = new Rect();
        ClearCurrentTextSelection();
        RedactionWorkflow.Reset();
        ClearPendingTypewriterText();
        ClearEditHistory();
        ClipboardHistory.Clear();

        ResetThumbnailSession();
        OutlineNodes.Clear();
        this.RaisePropertyChanged(nameof(HasOutline));

        PdfCoreDocument = null;

        IsRedactionMode = false;
        IsTypewriterMode = false;

        _logger.LogInformation(">>> STEP 3: Setting current file and document state");
        _currentFilePath = filePath;
        FileState.SetDocument(filePath);
        this.RaisePropertyChanged(nameof(DocumentName));
        this.RaisePropertyChanged(nameof(StatusBarText));
        OperationStatus = "Opening PDF…";
    }

    private async Task<PdfCoreDocument> AcquireDocumentAsync(string filePath)
    {
        _logger.LogInformation(">>> STEP 5: Loading Excise.Core document");
        try
        {
            return await LoadDocumentInstanceAsync(filePath, userPassword: null);
        }
        catch (Excise.Core.Parsing.PdfEncryptionNotSupportedException ex)
            when (IsPasswordVerificationFailure(ex))
        {
            return await AcquirePasswordProtectedDocumentAsync(filePath);
        }
    }

    private async Task<PdfCoreDocument> AcquirePasswordProtectedDocumentAsync(string filePath)
    {
        // #643: first retry with the password used by the previous document.
        var rememberedPassword = _documentService.CurrentUserPassword;
        if (!string.IsNullOrEmpty(rememberedPassword))
        {
            try
            {
                return await LoadDocumentInstanceAsync(filePath, rememberedPassword);
            }
            catch (Excise.Core.Parsing.PdfEncryptionNotSupportedException ex)
                when (IsPasswordVerificationFailure(ex))
            {
                // Different document, different password: ask the user.
            }
        }

        OperationStatus = "Password required…";
        var password = await _dialogService.PromptPasswordAsync(
            "Password Required",
            "Enter the user password for this PDF.");
        if (password == null)
        {
            throw new Excise.Core.Parsing.PdfEncryptionNotSupportedException(
                "Password is required to open this PDF.");
        }

        OperationStatus = "Opening PDF…";
        return await LoadDocumentInstanceAsync(filePath, password);
    }

    private async Task ActivateDocumentAsync(
        string filePath,
        Stopwatch stopwatch,
        DocumentOpenStageTimings timings)
    {
        _logger.LogInformation(">>> STEP 5: Document instance loaded");
        CurrentPageIndex = 0;
        timings.FirstPageVisibleElapsedMs = stopwatch.ElapsedMilliseconds;
        ReapplyFitModeIfNeeded();

        await StartThumbnailSessionAsync(filePath);
        timings.ThumbnailPlaceholdersReadyElapsedMs = stopwatch.ElapsedMilliseconds;
        RefreshCurrentPageBindings();

        LoadDocumentOutline();
        timings.OutlineReadyElapsedMs = stopwatch.ElapsedMilliseconds;

        StartDocumentTextIndex();
        timings.SearchIndexStartedElapsedMs = stopwatch.ElapsedMilliseconds;
    }

    private Task StartThumbnailSessionAsync(string filePath)
    {
        _logger.LogInformation(">>> STEP 8: Creating thumbnail placeholders (lazy load)");
        StartThumbnailSession(filePath, PdfCoreDocument!);
        return Task.CompletedTask;
    }

    private void LoadDocumentOutline()
    {
        try
        {
            var outline = Excise.Core.Document.PdfOutlineParser.Parse(PdfCoreDocument!);
            OutlineNodes.Clear();
            foreach (var item in outline)
                OutlineNodes.Add(Models.OutlineNode.From(item));
            this.RaisePropertyChanged(nameof(HasOutline));
            _logger.LogInformation(">>> STEP 8b: Outline parsed — {Count} top-level entries", outline.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse document outline");
            OutlineNodes.Clear();
            this.RaisePropertyChanged(nameof(HasOutline));
        }
    }

    private void StartDocumentTextIndex()
    {
        Services.DocumentTextIndex? indexGeneration = null;
        var indexProgress = new Progress<(int Done, int Total)>(progress =>
        {
            if (!ReferenceEquals(TextIndex, indexGeneration))
                return;

            if (string.IsNullOrEmpty(OperationStatus) || OperationStatus.StartsWith("Indexing"))
            {
                OperationStatus = progress.Done < progress.Total
                    ? $"Indexing for search… {progress.Done}/{progress.Total}"
                    : string.Empty;
            }
        });
        indexGeneration = _textIndexSession.Start(PdfCoreDocument!, indexProgress);
    }

    private async Task CompleteDocumentOpenAsync(
        string filePath,
        Stopwatch stopwatch,
        DocumentOpenStageTimings timings)
    {
        this.RaisePropertyChanged(nameof(TotalPages));
        RefreshRedactAnnotationCount();
        this.RaisePropertyChanged(nameof(IsDocumentLoaded));

        AddToRecentFiles(filePath);
        await RestoreDocumentStateAsync(filePath);

        if (OperationStatus == "Opening PDF…")
            OperationStatus = string.Empty;

        stopwatch.Stop();
        LastDocumentOpenTiming = new DocumentOpenTiming(
            filePath,
            TotalPages,
            timings.DocumentInstancesLoadedElapsedMs,
            timings.FirstPageVisibleElapsedMs,
            timings.ThumbnailPlaceholdersReadyElapsedMs,
            timings.OutlineReadyElapsedMs,
            timings.SearchIndexStartedElapsedMs,
            stopwatch.ElapsedMilliseconds);

        _logger.LogInformation(
            ">>> STEP 13: LoadDocumentAsync COMPLETE. Total pages: {PageCount}. Timings: docLoad={DocLoadMs}ms firstPage={FirstPageMs}ms thumbnails={ThumbnailsMs}ms outline={OutlineMs}ms indexStart={IndexStartMs}ms total={TotalMs}ms",
            TotalPages,
            LastDocumentOpenTiming.DocumentInstancesLoadedElapsedMs,
            LastDocumentOpenTiming.FirstPageVisibleElapsedMs,
            LastDocumentOpenTiming.ThumbnailPlaceholdersReadyElapsedMs,
            LastDocumentOpenTiming.OutlineReadyElapsedMs,
            LastDocumentOpenTiming.SearchIndexStartedElapsedMs,
            LastDocumentOpenTiming.TotalLoadElapsedMs);
        ResponsivenessReportWriter.TryWriteDocumentOpenReportFromEnvironment(
            LastDocumentOpenTiming,
            _logger);
    }

    private async Task HandleDocumentOpenFailureAsync(string filePath, Exception exception)
    {
        _logger.LogError(exception, "!!! ERROR in LoadDocumentAsync: {FilePath}", filePath);
        _logger.LogError("!!! Exception Type: {ExceptionType}", exception.GetType().Name);
        _logger.LogError("!!! Exception Message: {Message}", exception.Message);

        _currentFilePath = string.Empty;
        FileState.Reset();
        _textIndexSession.Cancel();
        _documentService.CloseDocument();
        PdfCoreDocument = null;
        ResetThumbnailSession();
        OutlineNodes.Clear();
        OperationStatus = string.Empty;

        this.RaisePropertyChanged(nameof(DocumentName));
        this.RaisePropertyChanged(nameof(StatusBarText));
        this.RaisePropertyChanged(nameof(TotalPages));
        this.RaisePropertyChanged(nameof(IsDocumentLoaded));
        this.RaisePropertyChanged(nameof(HasOutline));

        var userMessage = GetDocumentOpenFailureMessage(exception);
        await ShowErrorDialogAsync("Cannot Open PDF", userMessage);
    }

    private string GetDocumentOpenFailureMessage(Exception exception)
    {
        if (IsPasswordVerificationFailure(exception)
            || exception.Message.Contains("owner password", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("password is required", StringComparison.OrdinalIgnoreCase))
        {
            _toastService.ShowError("Cannot Open PDF", "Password required or rejected.");
            return "This PDF requires a user password. The password was not provided, was rejected, or the file uses an unsupported owner-password-only mode.";
        }

        if (exception.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
        {
            _toastService.ShowError(
                "Cannot Open PDF",
                "File is encrypted. Please provide an unencrypted version.");
            return "This PDF is encrypted and cannot be opened.";
        }

        _toastService.ShowError("Cannot Open PDF", exception.Message);
        return $"Failed to open PDF:\n\n{exception.Message}";
    }

    /// <summary>
    /// Open the file once and share the service-owned, byte-backed instance
    /// between saving and viewing. See #917.
    /// </summary>
    private async Task<PdfCoreDocument> LoadDocumentInstanceAsync(string filePath, string? userPassword)
    {
        await Task.Run(() => _documentService.LoadDocument(filePath, userPassword));
        return _documentService.GetCurrentDocument()
            ?? throw new InvalidOperationException(
                $"Document service reported no document after loading {filePath}.");
    }

    private static bool IsPasswordVerificationFailure(Exception exception)
        => exception.Message.Contains("password verification failed", StringComparison.OrdinalIgnoreCase)
           || exception.Message.Contains("requires a non-empty user password", StringComparison.OrdinalIgnoreCase);
}
