using Avalonia;
using Avalonia.Platform.Storage;
using Excise.App.Services;
using Excise.App.Services.Host;
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

        // #1463: in a multi-document workspace the file may open in another
        // window or tab, and then nothing here is discarded.
        var host = SessionHost;
        var opensElsewhere = host?.OpensDocumentsElsewhere == true;

        // #1233: replacing the open document discards its unsaved edits just
        // as surely as closing the window does. Ask BEFORE the picker, so a
        // user who decides to keep the current document isn't made to choose a
        // file first and then be told it was pointless.
        if (!opensElsewhere && !await ConfirmDiscardUnsavedChangesAsync("open a different document"))
            return;

        var files = await _filePicker.OpenFilesAsync(new OpenFilesRequest
        {
            Title = "Open PDF File",
            AllowMultiple = opensElsewhere,
            Filters = [FilePickerFilters.Pdf],
        });

        if (files.Count == 0)
        {
            _logger.LogInformation("Open dialog cancelled");
            return;
        }

        if (host != null)
        {
            await host.OpenDocumentsAsync(files, replaceConfirmed: !opensElsewhere);
            return;
        }

        var filePath = files[0];

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
        // #1563: the previous document's attachments must not stay listed
        // while (or if) this one loads. Not RefreshAttachments(): the previous
        // document is still current here and would be re-listed.
        ClearAttachments();
        ClearAttachmentsNotice();
        ClearXfaNotice();

        PdfCoreDocument = null;

        IsRedactionMode = false;
        IsTypewriterMode = false;
        _hasWarnedAboutSignedDocumentThisSession = false;

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
        // #1547: a laid-out XFA form's pages are not the pages in the file, so
        // they must not share (or reuse) the file's cached thumbnails.
        StartThumbnailSession(
            filePath,
            PdfCoreDocument!,
            cacheSalt: _documentService.XfaLayout is { ShowsForm: true } ? "xfa-layout-v1" : null);
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

    /// <summary>
    /// Minimum gap between two "Indexing for search…" status updates (#1565).
    /// </summary>
    /// <remarks>
    /// The build reports once per page, so a 126-page document redrew the
    /// status bar 126 times over ~2 s — every one of them a frame the launch
    /// measurement counts as the window still changing. The final report
    /// (which clears the text) is never throttled.
    /// </remarks>
    private static readonly TimeSpan TextIndexProgressInterval = TimeSpan.FromMilliseconds(250);

    private void StartDocumentTextIndex()
    {
        Services.DocumentTextIndex? indexGeneration = null;
        var lastReport = System.Diagnostics.Stopwatch.StartNew();
        var firstReport = true;
        var indexProgress = new Progress<(int Done, int Total)>(progress =>
        {
            if (!ReferenceEquals(TextIndex, indexGeneration))
                return;

            // #1565: indexing is work on this document, so the thumbnail
            // pre-warm's quiet period starts again and the two never overlap.
            _thumbnailSession.NotifyActivity();

            var isFinal = progress.Done >= progress.Total;
            if (!isFinal && !firstReport && lastReport.Elapsed < TextIndexProgressInterval)
                return;
            firstReport = false;
            lastReport.Restart();

            if (string.IsNullOrEmpty(OperationStatus) || OperationStatus.StartsWith("Indexing"))
            {
                OperationStatus = isFinal
                    ? string.Empty
                    : $"Indexing for search… {progress.Done}/{progress.Total}";
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

        // #1414: attachments are invisible in the page view, so a document can
        // carry a full XML copy of itself (ZUGFeRD/Factur-X) with nothing on
        // screen to say so. List them on open and WARN, per the capability's
        // "warn when their presence is not otherwise obvious".
        RefreshAttachments();
        ShowAttachmentsNoticeOnOpen();

        // #1547: a dynamic XFA form shows only a placeholder page here; say why.
        RefreshXfaNotice();

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
        AppMetrics.RecordDocumentOpen(LastDocumentOpenTiming);

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
        // #1563: a failed open used to leave the previous list on screen. The
        // document service is closed above, so this empties it.
        RefreshAttachments();
        ClearAttachmentsNotice();
        ClearXfaNotice();
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
