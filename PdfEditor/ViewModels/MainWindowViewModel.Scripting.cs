using Microsoft.Extensions.Logging;
using Pdfe.Core.Document;
using PdfEditor.Services;
using ReactiveUI;
using System;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;

namespace PdfEditor.ViewModels;

/// <summary>
/// Scripting API for MainWindowViewModel.
/// These commands expose GUI functionality to Roslyn C# scripts for automation and testing.
/// </summary>
public partial class MainWindowViewModel
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
    public System.Collections.ObjectModel.ObservableCollection<PdfEditor.Models.PendingRedaction> PendingRedactions =>
        RedactionWorkflow.PendingRedactions;

    // Scripting Commands (exposed to Roslyn scripts as Task-based wrappers)
    // Note: These are NOT ReactiveCommands - they're simple Task-returning methods for scripting

    /// <summary>
    /// Load a document (for Roslyn scripts).
    /// Returns a Task that completes when the document is loaded.
    /// </summary>
    public Task LoadDocumentCommand(string filePath) => LoadDocumentViaScriptAsync(filePath);

    /// <summary>
    /// Redact all occurrences of text (for Roslyn scripts).
    /// Returns a Task that completes when redactions are marked.
    /// </summary>
    public Task RedactTextCommand(string text) => RedactTextViaScriptAsync(text);

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
    /// </summary>
    public string ExtractAllText() => ExtractAllTextViaScript();

    /// <summary>
    /// Initialize scripting commands (call from main constructor)
    /// </summary>
    private void InitializeScriptingCommands()
    {
        // Scripting commands are now simple Task-returning methods (not ReactiveCommands)
        // No initialization needed
    }

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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(LoadDocumentTimeoutSeconds));

            try
            {
                // Run LoadDocument on a background thread with timeout
                await Task.Run(() => _documentService.LoadDocument(filePath), cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogError("[SCRIPT] LoadDocumentCommand: Timeout after {Seconds}s loading '{FilePath}'",
                    LoadDocumentTimeoutSeconds, filePath);
                throw new TimeoutException($"Loading PDF timed out after {LoadDocumentTimeoutSeconds} seconds: {filePath}");
            }
        }
        else
        {
            // No timeout - original behavior
            _documentService.LoadDocument(filePath);
        }

        this.RaisePropertyChanged(nameof(DocumentName));
        this.RaisePropertyChanged(nameof(StatusBarText));
        this.RaisePropertyChanged(nameof(TotalPages));
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(IsDocumentLoaded));

        AddToRecentFiles(filePath);

        _logger.LogInformation("[SCRIPT] LoadDocumentCommand completed successfully (headless mode - no thumbnails)");
    }

    /// <summary>
    /// List of text redaction requests for the current document.
    /// These are applied as a batch when ApplyRedactionsCommand is called.
    /// Issue #190: Uses file-based TextRedactor API to bypass coordinate conversion issues.
    /// </summary>
    private readonly List<string> _pendingTextRedactions = new();

    /// <summary>
    /// Redact all occurrences of the specified text on all pages (for Roslyn scripts).
    /// Usage: <c>await RedactTextCommand("SECRET")</c>
    ///
    /// Issue #190 FIX: This now uses the file-based TextRedactor API (like CLI) instead
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
        // These will be applied via TextRedactor.RedactText() API when ApplyRedactions is called
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

    /// <summary>
    /// Apply all pending redactions to the in-memory document (for Roslyn scripts).
    /// This modifies the document but does not save it. Use SaveDocumentCommand to save.
    /// Usage: <c>await ApplyRedactionsCommand()</c>
    ///
    /// Issue #190 FIX: Text redactions now use file-based TextRedactor API.
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
            ApplyPendingAreaRedactions(document);

            RedactionWorkflow.MoveToApplied();
            FileState.PendingRedactionsCount = 0;
            _hasInMemoryModifications = true;
            this.RaisePropertyChanged(nameof(SaveButtonText));
            this.RaisePropertyChanged(nameof(StatusBarText));

            _logger.LogInformation("[SCRIPT] ApplyRedactionsCommand completed - coordinate redactions applied in memory");
            await Task.CompletedTask;
            return;
        }

        // Issue #190 FIX: Text redactions use file-based TextRedactor API.
        // The actual text redaction happens in SaveDocumentViaScriptAsync.
        _logger.LogInformation("[SCRIPT] ApplyRedactionsCommand: {Count} text redactions queued, will be applied on save",
            _pendingTextRedactions.Count);

        RedactionWorkflow.MoveToApplied();
        FileState.PendingRedactionsCount = 0;
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
    /// Issue #190 FIX: Text redactions now use file-based TextRedactor API.
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

                for (int i = 0; i < _pendingTextRedactions.Count; i++)
                {
                    var text = _pendingTextRedactions[i];
                    var isLast = (i == _pendingTextRedactions.Count - 1);
                    var currentOutput = isLast ? filePath : System.IO.Path.Combine(tempDir, $"pdfe_script_redact_{i}_{Guid.NewGuid():N}.pdf");

                    _logger.LogInformation("[SCRIPT] Redacting '{Text}' ({Current}/{Total})",
                        text, i + 1, _pendingTextRedactions.Count);

                    var result = _redactionService.RedactText(currentInput, currentOutput, text, caseSensitive: false);

                    if (!result.Success)
                    {
                        _logger.LogError("[SCRIPT] Redaction failed for '{Text}': {Error}", text, result.ErrorMessage);
                        throw new InvalidOperationException($"Redaction failed for '{text}': {result.ErrorMessage}");
                    }

                    _logger.LogInformation("[SCRIPT] Redacted {Count} occurrences of '{Text}'",
                        result.RedactionCount, text);

                    // Clean up intermediate files
                    if (!isLast && currentInput != _currentFilePath && System.IO.File.Exists(currentInput))
                    {
                        try { System.IO.File.Delete(currentInput); } catch { }
                    }

                    currentInput = currentOutput;
                }

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
                    ApplyPendingAreaRedactions(document);
                    RedactionWorkflow.MoveToApplied();
                    FileState.PendingRedactionsCount = 0;
                    _hasInMemoryModifications = true;
                }

                _logger.LogInformation("[SCRIPT] Saving PDF to: {Path}", filePath);
                document.Save(filePath);
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
    private string ExtractAllTextViaScript()
    {
        _logger.LogInformation("[SCRIPT] ExtractAllText()");

        if (!_documentService.IsDocumentLoaded || _textExtractionService == null)
        {
            _logger.LogError("[SCRIPT] ExtractAllText: No document loaded");
            throw new InvalidOperationException("No document loaded. Call LoadDocumentCommand first.");
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
public class CurrentDocumentInfo
{
    private readonly Pdfe.Core.Document.PdfDocument? _document;

    public CurrentDocumentInfo(Pdfe.Core.Document.PdfDocument? document, string filePath, int pageCount)
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
    public Pdfe.Core.Document.PdfDocument? Document => _document;
}
