using Microsoft.Extensions.Logging;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.App.Services;
using ReactiveUI;
using System;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Excise.Core.Security;
using Excise.Ocr;

namespace Excise.App.ViewModels;

/// <summary>
/// Scripting API for MainWindowViewModel.
/// These commands expose GUI functionality to Roslyn C# scripts for automation and testing.
/// </summary>
internal partial class MainWindowViewModel
{
    // Scripting Properties (exposed to Roslyn scripts)

    /// <summary>
    /// Timeout for loading documents in scripting mode.
    /// Set to 0 or negative to disable timeout (default: 30 seconds).
    /// Issue #93: Prevents hangs on malformed PDFs in corpus tests.
    /// </summary>
    public int LoadDocumentTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Wrapper for the currently loaded PDF document that provides scripting-friendly properties.
    /// Scripts can check if CurrentDocument != null to verify a document is loaded.
    /// </summary>
    public CurrentDocumentInfo? CurrentDocument => _documentService?.IsDocumentLoaded == true
        ? new CurrentDocumentInfo(_documentService.GetCurrentDocument(), _currentFilePath, _documentService.PageCount)
        : null;

    /// <summary>
    /// The file path of the currently loaded document.
    /// </summary>
    public string FilePath => _currentFilePath;

    /// <summary>
    /// Collection of pending redaction areas (marked but not yet applied).
    /// Scripts can check PendingRedactions.Count to verify redactions were marked.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<Excise.App.Models.PendingRedaction> PendingRedactions =>
        RedactionWorkflow.PendingRedactions;

    /// <summary>
    /// What the last scripted text save or flatten reported, one result per
    /// term (#1501): the verified count, survivors, wrapped-term candidates and
    /// NOT SCRUBBED carriers, exactly as <c>excise redact</c> reports them.
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<TermRedactionResult> LastTextRedactionResults
    {
        // No initializer: `= []` would make the constructor instantiate a collection of an
        // Excise.Ocr type and load the OCR assembly (it shells out to tesseract) while the view
        // model is built. The empty list is created only when a script reads the property.
        get => _lastTextRedactionResults ?? System.Array.Empty<TermRedactionResult>();
        private set => _lastTextRedactionResults = value;
    }

    private System.Collections.Generic.IReadOnlyList<TermRedactionResult>? _lastTextRedactionResults;

    // Scripting Commands (exposed to Roslyn scripts as Task-based wrappers)
    // Note: These are NOT ReactiveCommands - they're simple Task-returning methods for scripting

    /// <summary>
    /// Parse a document for a Roslyn script, WITHOUT driving the viewer
    /// (#1540).
    /// </summary>
    /// <remarks>
    /// <para>⚠️ This is not what opening a document in the GUI does. It calls
    /// the document service directly — no thumbnails, no rendering, no layout —
    /// deliberately, so a script does not block on operations that need a
    /// dispatcher. The GUI open is
    /// <see cref="LoadDocumentAsync"/>.</para>
    /// <para>The distinction is not cosmetic: #1497's performance runner used
    /// the old name to open documents and produced a complete, plausible,
    /// entirely meaningless measurement — 9 steps, 0 failures, every viewer
    /// counter 0, "scrolled" 17 pages in 245 ms because nothing was laid out.
    /// It read as a clean answer to #1461. Switching this one call to
    /// <c>LoadDocumentAsync</c> moved the peak from 391 MB to 1555 MB, inside
    /// #1461's recorded band. The failure is silent and looks like good
    /// news.</para>
    /// </remarks>
    public Task LoadDocumentHeadlessAsync(string filePath) => LoadDocumentViaScriptAsync(filePath);

    /// <summary>
    /// Former name of <see cref="LoadDocumentHeadlessAsync"/>, kept so existing
    /// <c>.csx</c> scripts keep running (#1540).
    /// </summary>
    /// <remarks>
    /// The name was the defect: it is public, awaitable, takes a path, needs no
    /// dialog, sits beside <see cref="RedactTextCommand"/> and
    /// <see cref="SaveDocumentCommand"/> which DO perform the user-facing
    /// operation — and it is not a <c>ReactiveCommand</c> at all, unlike every
    /// real command on this view model. Everything about it invited use as "the
    /// way to open a document".
    /// </remarks>
    [System.Obsolete("Renamed to LoadDocumentHeadlessAsync: this parses the file without driving the viewer. " +
                     "For a real GUI open — thumbnails, rendering, layout — use LoadDocumentAsync (#1540).")]
    public Task LoadDocumentCommand(string filePath) => LoadDocumentHeadlessAsync(filePath);

    /// <summary>
    /// Redact all occurrences of text (for Roslyn scripts).
    /// Returns a Task that completes when redactions are marked.
    /// </summary>
    public Task RedactTextCommand(string text) => RedactTextViaScriptAsync(text);

    /// <summary>
    /// Write an explicitly image-only OCR-redacted copy. Unlike
    /// <see cref="RedactTextCommand"/>, this is terminal and immediate: the
    /// result is a new document without selectable text or hidden carriers.
    /// </summary>
    public Task FlattenOcrRedactCommand(string outputPath, string text) =>
        FlattenOcrRedactViaScriptAsync(outputPath, text);

    /// <summary>
    /// Apply all pending redactions (for Roslyn scripts).
    /// Returns a Task that completes when redactions are applied.
    /// </summary>
    public Task ApplyRedactionsCommand() => ApplyRedactionsViaScriptAsync();

    /// <summary>
    /// Save the document (for Roslyn scripts).
    /// Returns a Task that completes when the document is saved.
    /// </summary>
    public Task SaveDocumentCommand(string filePath) => SaveDocumentViaScriptAsync(filePath);

    /// <summary>
    /// Extract all text from the currently loaded PDF (for Roslyn scripts).
    /// Returns a string containing all text from all pages concatenated.
    ///
    /// #642: gated on the document's /P copy/extract permission (bit 5).
    /// Pass <paramref name="forAccessibility"/> = true to invoke the bit 10
    /// extract-for-accessibility carve-out (honoured when bit 5 is denied
    /// but bit 10 is granted), or set
    /// <see cref="IgnoreDocumentPermissions"/> for the unconditional
    /// override (mirrors the CLI's <c>--ignore-permissions</c>).
    /// </summary>
    public string ExtractAllText(bool forAccessibility = false) => ExtractAllTextViaScript(forAccessibility);

    /// <summary>
    /// Initialize scripting commands (call from main constructor)
    /// </summary>
    private void InitializeScriptingCommands()
    {
        // Scripting commands are now simple Task-returning methods (not ReactiveCommands)
        // No initialization needed
    }

    /// <summary>
    /// Test seam for #1501 item 4: <c>PdfDocumentService.LoadDocument</c> has
    /// no cancellation-aware overload (production code outside the scripting
    /// harness), so there is no way to make a real load hang predictably and
    /// on demand. A test can substitute a delegate that blocks past the
    /// configured timeout to prove the wait is actually bounded, without
    /// touching document-service production code. Null (the only value
    /// outside tests) runs the real load.
    /// </summary>
    internal Func<string, Task>? LoadDocumentOverrideForTests { get; set; }

    private Task StartLoadDocument(string filePath) =>
        LoadDocumentOverrideForTests is { } overrideLoad
            ? overrideLoad(filePath)
            : Task.Run(() => _documentService.LoadDocument(filePath));

    /// <summary>
    /// Load a document (for Roslyn scripts).
    /// Usage: <c>await LoadDocumentCommand("/path/to/file.pdf")</c>
    /// Issue #93: Includes configurable timeout to prevent hangs on malformed PDFs.
    /// </summary>
    private async Task LoadDocumentViaScriptAsync(string filePath)
    {
        _logger.LogInformation("[SCRIPT] LoadDocumentCommand.Execute('{FilePath}')", filePath);

        if (string.IsNullOrWhiteSpace(filePath))
        {
            _logger.LogWarning("[SCRIPT] LoadDocumentCommand: File path is empty");
            throw new ArgumentException("File path cannot be empty", nameof(filePath));
        }

        if (!System.IO.File.Exists(filePath))
        {
            _logger.LogError("[SCRIPT] LoadDocumentCommand: File not found: {FilePath}", filePath);
            throw new System.IO.FileNotFoundException($"File not found: {filePath}", filePath);
        }

        // For scripting, we need headless document loading (no thumbnails/rendering)
        // This avoids blocking on UI operations that require a dispatcher
        _currentFilePath = filePath;
        FileState.SetDocument(filePath);
        RedactionWorkflow.Reset();
        _pendingTextRedactions.Clear();  // Issue #190: Clear pending text redactions

        // Issue #93: Use timeout to prevent hangs on malformed PDFs
        if (LoadDocumentTimeoutSeconds > 0)
        {
            // #1501 item 4: a CancellationToken passed to Task.Run only stops
            // the task if it fires BEFORE the delegate starts running on the
            // pool thread. LoadDocument does not observe a token once it is
            // executing, so the old `Task.Run(..., cts.Token)` await simply
            // waited out however long the load actually took - the timeout
            // never bounded anything in practice. WaitAsync bounds the wait
            // itself regardless of what the delegate is doing.
            var loadTask = StartLoadDocument(filePath);

            try
            {
                await loadTask.WaitAsync(TimeSpan.FromSeconds(LoadDocumentTimeoutSeconds));
            }
            catch (TimeoutException)
            {
                _logger.LogError("[SCRIPT] LoadDocumentCommand: Timeout after {Seconds}s loading '{FilePath}'",
                    LoadDocumentTimeoutSeconds, filePath);

                // PdfDocumentService.LoadDocument takes no CancellationToken
                // (production code outside the scripting harness), so the
                // abandoned load cannot be torn down here - only observed.
                // Log its eventual outcome instead of leaving it as an
                // unobserved task exception or a silent, late mutation of
                // the document service's current document.
                _ = loadTask.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        _logger.LogWarning(t.Exception, "[SCRIPT] Abandoned load of '{FilePath}' failed after its timeout had already fired", filePath);
                    else
                        _logger.LogWarning("[SCRIPT] Abandoned load of '{FilePath}' finished after its timeout had already fired; the document service's current document may not be the one this script expects", filePath);
                }, TaskScheduler.Default);

                throw new TimeoutException($"Loading PDF timed out after {LoadDocumentTimeoutSeconds} seconds: {filePath}");
            }
        }
        else
        {
            // No timeout - original behavior
            _documentService.LoadDocument(filePath);
        }

        // #1878: every other path that replaces the service's document also
        // points PdfCoreDocument at it (open, every save, close). This one
        // didn't, so the view model kept the PREVIOUS document - which the
        // service had just disposed - until something else happened to call
        // ReloadPdfCoreDocumentFromCurrentDocumentAsync. One document, one
        // instance: nothing left to mirror.
        PdfCoreDocument = _documentService.GetCurrentDocument();

        this.RaisePropertyChanged(nameof(DocumentName));
        this.RaisePropertyChanged(nameof(StatusBarText));
        this.RaisePropertyChanged(nameof(TotalPages));
        this.RaisePropertyChanged(nameof(IsDocumentLoaded));

        AddToRecentFiles(filePath);

        _logger.LogInformation("[SCRIPT] LoadDocumentCommand completed successfully (headless mode - no thumbnails)");
    }

    /// <summary>
    /// List of text redaction requests for the current document.
    /// These are applied as a batch when ApplyRedactionsCommand is called.
    /// Issue #190: uses the file-based redaction API to bypass coordinate conversion issues.
    /// </summary>
    private readonly List<string> _pendingTextRedactions = new();

    /// <summary>
    /// Redact all occurrences of the specified text on all pages (for Roslyn scripts).
    /// Usage: <c>await RedactTextCommand("SECRET")</c>
    ///
    /// Issue #190 FIX: this now uses the file-based redaction API (like the CLI) instead
    /// of the coordinate-based workflow. The coordinate conversion was causing failures
    /// on corpus PDFs that work fine with CLI redaction.
    /// </summary>
    private async Task RedactTextViaScriptAsync(string text)
    {
        _logger.LogInformation("[SCRIPT] RedactTextCommand.Execute('{Text}')", text);

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("[SCRIPT] RedactTextCommand: Text is empty");
            throw new ArgumentException("Text to redact cannot be empty", nameof(text));
        }

        if (!_documentService.IsDocumentLoaded)
        {
            _logger.LogError("[SCRIPT] RedactTextCommand: No document loaded");
            throw new InvalidOperationException("No document loaded. Call LoadDocumentCommand first.");
        }

        // Issue #190 FIX: Add to pending text redactions list
        // Applied via PdfDocument.RedactText (Excise.Core) when ApplyRedactions is called.
        _pendingTextRedactions.Add(text);

        // Also add a placeholder to PendingRedactions for tracking/display purposes.
        // This is a dummy content-space area because text redaction itself uses
        // the file-based text pipeline, not a viewer-coordinate rectangle.
        RedactionWorkflow.MarkArea(
            PdfPageRect.FromContentPoints(1, new PdfRectangle(0, 0, 1, 1)),
            text);

        FileState.PendingRedactionsCount = RedactionWorkflow.PendingCount;
        this.RaisePropertyChanged(nameof(SaveButtonText));
        this.RaisePropertyChanged(nameof(StatusBarText));

        _logger.LogInformation("[SCRIPT] RedactTextCommand: Queued '{Text}' for text-based redaction (bypasses coordinate conversion)",
            text);

        await Task.CompletedTask;
    }

    private async Task FlattenOcrRedactViaScriptAsync(string outputPath, string text)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output file path cannot be empty", nameof(outputPath));
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text to redact cannot be empty", nameof(text));
        if (!_documentService.IsDocumentLoaded || string.IsNullOrWhiteSpace(_currentFilePath))
            throw new InvalidOperationException("No document loaded. Call LoadDocumentCommand first.");

        // #1501: the CLI's workflow, with the document's own password.
        var result = TermRedactionRunner.Execute(new TermRedactionRequest(
            _currentFilePath, outputPath, text, RedactionPreferences.ToOptions() with { CaseSensitive = false })
        {
            Password = _documentService.CurrentUserPassword,
            FlattenOcr = true,
        });
        LastTextRedactionResults = [result];

        _logger.LogInformation("[SCRIPT] FlattenOcrRedactCommand wrote image-only copy with {Count} redactions", result.Count);
        await LoadDocumentAsync(outputPath);
    }

    /// <summary>
    /// Apply all pending redactions to the in-memory document (for Roslyn scripts).
    /// This modifies the document but does not save it. Use SaveDocumentCommand to save.
    /// Usage: <c>await ApplyRedactionsCommand()</c>
    ///
    /// Issue #190 FIX: text redactions use the file-based Excise.Core redaction API.
    /// </summary>
    private async Task ApplyRedactionsViaScriptAsync()
    {
        _logger.LogInformation("[SCRIPT] ApplyRedactionsCommand.Execute()");

        if (!_documentService.IsDocumentLoaded)
        {
            _logger.LogError("[SCRIPT] ApplyRedactionsCommand: No document loaded");
            throw new InvalidOperationException("No document loaded. Call LoadDocumentCommand first.");
        }

        if (_pendingTextRedactions.Count == 0 && RedactionWorkflow.PendingCount == 0)
        {
            _logger.LogWarning("[SCRIPT] ApplyRedactionsCommand: No pending redactions to apply");
            return;
        }

        if (_pendingTextRedactions.Count == 0)
        {
            var document = _documentService.GetCurrentDocument()
                ?? throw new InvalidOperationException("Document is null");
            SyncPendingRedactionPages();
            var application = _redactionWorkflowService.ApplyToDocument(
                RedactionApplicationRequest.Capture(
                    document,
                    RedactionWorkflow.PendingRedactions,
                    Array.Empty<Excise.Core.Editing.PdfTypewriterTextOperation>(),
                    RedactionPreferences.ToOptions()));

            RedactionWorkflow.MoveToApplied();
            FileState.PendingRedactionsCount = 0;
            // #1501 item 2: the document was just changed in memory with no
            // save to follow, so the dirty counters must say so — a plain
            // PendingRedactionsCount = 0 makes HasUnsavedChanges false while
            // the loaded document already differs from the file on disk.
            FileState.AppliedRedactionsCount = RedactionWorkflow.AppliedCount;
            this.RaisePropertyChanged(nameof(SaveButtonText));
            this.RaisePropertyChanged(nameof(StatusBarText));

            _logger.LogInformation(
                "[SCRIPT] ApplyRedactionsCommand completed - coordinate redactions applied in memory; safety report warnings: {HasWarnings}",
                application.SafetyReport.HasWarnings);
            await Task.CompletedTask;
            return;
        }

        // Issue #190 FIX: text redactions use the file-based Excise.Core redaction API.
        // The actual text redaction happens in SaveDocumentViaScriptAsync.
        _logger.LogInformation("[SCRIPT] ApplyRedactionsCommand: {Count} text redactions queued, will be applied on save",
            _pendingTextRedactions.Count);

        RedactionWorkflow.MoveToApplied();
        FileState.PendingRedactionsCount = 0;
        // #1501 item 2: queued but not yet written to disk - still dirty.
        FileState.AppliedRedactionsCount = RedactionWorkflow.AppliedCount;
        this.RaisePropertyChanged(nameof(SaveButtonText));
        this.RaisePropertyChanged(nameof(StatusBarText));

        _logger.LogInformation("[SCRIPT] ApplyRedactionsCommand completed - text redactions will be applied on save");

        await Task.CompletedTask;
    }

    /// <summary>
    /// Save the document to the specified path (for Roslyn scripts).
    /// If redactions are pending, apply them first, then save.
    /// Usage: <c>await SaveDocumentCommand("/path/to/output.pdf")</c>
    ///
    /// Issue #190 FIX: text redactions use the file-based Excise.Core redaction API.
    /// </summary>
    private async Task SaveDocumentViaScriptAsync(string filePath)
    {
        _logger.LogInformation("[SCRIPT] SaveDocumentCommand.Execute('{FilePath}')", filePath);

        if (string.IsNullOrWhiteSpace(filePath))
        {
            _logger.LogWarning("[SCRIPT] SaveDocumentCommand: File path is empty");
            throw new ArgumentException("Output file path cannot be empty", nameof(filePath));
        }

        if (!_documentService.IsDocumentLoaded)
        {
            _logger.LogError("[SCRIPT] SaveDocumentCommand: No document loaded");
            throw new InvalidOperationException("No document loaded. Call LoadDocumentCommand first.");
        }

        try
        {
            // Issue #190 FIX: Apply text redactions using file-based API
            // This bypasses the coordinate conversion that was causing failures
            if (_pendingTextRedactions.Count > 0)
            {
                _logger.LogInformation("[SCRIPT] Applying {Count} text redactions using file-based API",
                    _pendingTextRedactions.Count);

                // Use sequential file-based redaction (like CLI)
                var currentInput = _currentFilePath;
                var tempDir = System.IO.Path.GetTempPath();
                // #1501 item 3: every intermediate this loop creates is a
                // partially-redacted copy of the document. The old cleanup
                // only deleted the PREVIOUS intermediate on a non-final pass,
                // so the last one (N >= 2 terms) always survived, and a pass
                // that threw left every intermediate written so far behind
                // too. Track them all and delete them in a finally.
                var intermediatePaths = new List<string>();
                var results = new List<TermRedactionResult>();

                try
                {
                    for (int i = 0; i < _pendingTextRedactions.Count; i++)
                    {
                        var text = _pendingTextRedactions[i];
                        var isLast = (i == _pendingTextRedactions.Count - 1);
                        var currentOutput = isLast ? filePath : System.IO.Path.Combine(tempDir, $"excise_script_redact_{i}_{Guid.NewGuid():N}.pdf");
                        if (!isLast)
                            intermediatePaths.Add(currentOutput);

                        _logger.LogInformation("[SCRIPT] Redacting '{Text}' ({Current}/{Total})",
                            text, i + 1, _pendingTextRedactions.Count);

                        // #1501: the same workflow `excise redact` runs, so the
                        // result carries the same survivors, wrapped-term
                        // candidates and NOT SCRUBBED carriers.
                        var result = TermRedactionRunner.Execute(new TermRedactionRequest(
                            currentInput, currentOutput, text,
                            RedactionPreferences.ToOptions() with { CaseSensitive = false })
                        {
                            Password = _documentService.CurrentUserPassword,
                        });
                        results.Add(result);

                        // #1052: log WHICH RULE RAN, not just the count.
                        _logger.LogInformation(
                            "[SCRIPT] Redacted {Count} occurrences of '{Text}' (wholeWord={WholeWord})",
                            result.Count, text, result.WholeWord);
                        foreach (var warning in result.Diagnostics.Concat(result.CarrierNotes))
                            _logger.LogWarning("[SCRIPT] Redaction warning for '{Text}': {Warning}", text, warning);

                        currentInput = currentOutput;
                    }
                }
                finally
                {
                    foreach (var intermediatePath in intermediatePaths)
                    {
                        if (System.IO.File.Exists(intermediatePath))
                        {
                            try { System.IO.File.Delete(intermediatePath); } catch { }
                        }
                    }
                }

                LastTextRedactionResults = results;

                // Clear pending text redactions
                _pendingTextRedactions.Clear();

                // Clear workflow tracking
                RedactionWorkflow.MoveToApplied();
                FileState.PendingRedactionsCount = 0;
            }
            else
            {
                // No text redactions - save document directly
                var document = _documentService.GetCurrentDocument();
                if (document == null)
                {
                    _logger.LogError("[SCRIPT] SaveDocumentCommand: Document is null");
                    throw new InvalidOperationException("Document is null");
                }

                if (RedactionWorkflow.PendingCount > 0)
                {
                    var result = _redactionWorkflowService.CreateRedactedCopy(
                        RedactedCopyRequest.Capture(
                            document,
                            RedactionWorkflow.PendingRedactions,
                            Array.Empty<Excise.Core.Editing.PdfTypewriterTextOperation>(),
                            filePath,
                            _documentService.GetReEncryptionOptions(),
                            RedactionPreferences.ToOptions()));
                    RedactionWorkflow.MoveToApplied();
                    FileState.PendingRedactionsCount = 0;

                    _logger.LogInformation(
                        "[SCRIPT] Safe-share scrub and verification completed for coordinate redaction output; warnings: {HasWarnings}",
                        result.Application.SafetyReport.HasWarnings);
                }
                else
                {
                    _logger.LogInformation("[SCRIPT] Saving PDF to: {Path}", filePath);
                    // #643: an encrypted source saves encrypted with the same
                    // parameters and password.
                    document.Save(filePath, _documentService.GetReEncryptionOptions());
                }
            }

            // Update current file path to point to the saved file
            // This ensures subsequent operations (like text extraction) use the new file
            _currentFilePath = filePath;
            FileState.SetDocument(filePath);
            this.RaisePropertyChanged(nameof(DocumentName));
            this.RaisePropertyChanged(nameof(FilePath));
            this.RaisePropertyChanged(nameof(SaveButtonText));
            this.RaisePropertyChanged(nameof(StatusBarText));

            _logger.LogInformation("[SCRIPT] SaveDocumentCommand completed successfully, current path updated to: {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SCRIPT] SaveDocumentCommand failed");
            throw;
        }
    }

    /// <summary>
    /// Extract all text from the currently loaded PDF (for Roslyn scripts).
    /// </summary>
    private string ExtractAllTextViaScript(bool forAccessibility = false)
    {
        _logger.LogInformation("[SCRIPT] ExtractAllText()");

        if (!_documentService.IsDocumentLoaded || _textExtractionService == null)
        {
            _logger.LogError("[SCRIPT] ExtractAllText: No document loaded");
            throw new InvalidOperationException("No document loaded. Call LoadDocumentCommand first.");
        }

        // #642: scripted extraction is user-initiated export — gate on /P
        // bit 5 with the bit 10 accessibility carve-out, like the CLI.
        var permissions = CurrentDocumentPermissions;
        var extractionAllowed = permissions.Allows(DocumentAction.Extract)
            || (forAccessibility && permissions.Allows(DocumentAction.ExtractForAccessibility));
        if (!extractionAllowed && !IgnoreDocumentPermissions)
        {
            _logger.LogWarning("[SCRIPT] ExtractAllText blocked by document permissions ({Permissions})", permissions);
            var accessibilityHint = permissions.Allows(DocumentAction.ExtractForAccessibility) && !forAccessibility
                ? " Extraction in support of accessibility is permitted (/P bit 10): call ExtractAllText(forAccessibility: true)."
                : string.Empty;
            throw new InvalidOperationException(
                $"Blocked by document permissions: text extraction requires {DocumentAction.Extract.Requirement()}, " +
                $"which this document denies ({permissions}).{accessibilityHint} If you are " +
                "the document owner, set IgnoreDocumentPermissions = true to override — permissions bind " +
                "user-password opens only, and excise cannot yet verify owner passwords (#324).");
        }
        if (!extractionAllowed && IgnoreDocumentPermissions)
        {
            _logger.LogWarning(
                "[SCRIPT] Overriding document permissions ({Permissions}): ExtractAllText proceeds " +
                "because IgnoreDocumentPermissions is set", permissions);
        }
        else if (!permissions.Allows(DocumentAction.Extract) && forAccessibility)
        {
            _logger.LogInformation(
                "[SCRIPT] ExtractAllText proceeding under the extract-for-accessibility permission (/P bit 10)");
        }

        try
        {
            var allText = _textExtractionService.ExtractAllText(_currentFilePath);
            _logger.LogInformation("[SCRIPT] ExtractAllText: Extracted {Length} characters", allText.Length);
            return allText;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SCRIPT] ExtractAllText failed");
            throw;
        }
    }
}

/// <summary>
/// Wrapper class that provides scripting-friendly access to document information.
/// </summary>
internal class CurrentDocumentInfo
{
    private readonly Excise.Core.Document.PdfDocument? _document;

    public CurrentDocumentInfo(Excise.Core.Document.PdfDocument? document, string filePath, int pageCount)
    {
        _document = document;
        FilePath = filePath;
        PageCount = pageCount;
    }

    /// <summary>
    /// The file path of the loaded document.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// The number of pages in the document.
    /// </summary>
    public int PageCount { get; }

    /// <summary>
    /// The underlying PdfDocument (for advanced scenarios).
    /// </summary>
    public Excise.Core.Document.PdfDocument? Document => _document;
}
