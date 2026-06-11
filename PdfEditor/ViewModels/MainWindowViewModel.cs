using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Pdfe.Avalonia.Controls;
using PdfEditor.Models;
using Pdfe.Core.Document;
using PdfEditor.Services;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reactive;
using System.Threading.Tasks;
using SkiaSharp;
using PdfCoreDocument = Pdfe.Core.Document.PdfDocument;

namespace PdfEditor.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly PdfDocumentService _documentService;
    private readonly PdfRenderService _renderService;
    private readonly RedactionService _redactionService;
    private readonly PdfTextExtractionService _textExtractionService;
    private readonly SignatureVerificationWorkflowService _signatureWorkflowService;
    private readonly FilenameSuggestionService _filenameSuggestionService;
    private readonly ToastService _toastService;
    private readonly IUserDialogService _dialogService;

    // State managers
    public DocumentStateManager FileState { get; } = new();
    public RedactionWorkflowManager RedactionWorkflow { get; } = new();

    /// <summary>
    /// Toast notification service for displaying error/info messages.
    /// </summary>
    public ToastService ToastService => _toastService;

    private string _currentFilePath = string.Empty;
    private Bitmap? _currentPageImage;
    private PdfCoreDocument? _pdfCoreDocument;
    private int _currentPageIndex;
    private PdfViewMode _viewMode = PdfViewMode.SinglePage;
    private double _zoomLevel = 1.0;
    private bool _skipZoomSave; // Flag to skip zoom save during auto-reset
    private bool _isRedactionMode;
    private Rect _currentRedactionArea;
    private bool _isTextSelectionMode;
    private Rect _currentTextSelectionArea;
    private string _selectedText = string.Empty;
    private ObservableCollection<string> _recentFiles = new();
    private double _viewportWidth = 800;
    private double _viewportHeight = 600;
    private ObservableCollection<Rect> _currentPageSearchHighlights = new();
    private int _renderCacheMax = 20;
    private string _operationStatus = string.Empty;
    private bool _hasInMemoryModifications; // Tracks if document has been modified in-memory (e.g., redactions applied)
    private Services.ThumbnailCacheService? _thumbnailCache;
    internal Services.DocumentTextIndex? TextIndex;
    private System.Threading.CancellationTokenSource? _indexBuildCts;

    /// <summary>
    /// Tracks whether the user is in an "auto-fit" zoom state. When set to
    /// <see cref="ZoomFitMode.FitWidth"/> or <see cref="ZoomFitMode.FitPage"/>
    /// the renderer re-fits on viewport size changes; once the user manually
    /// zooms (Ctrl++/-/0) we drop into <see cref="ZoomFitMode.Manual"/> and
    /// stop auto-recomputing.
    /// </summary>
    private ZoomFitMode _zoomFitMode = ZoomFitMode.FitWidth;
    private bool _isThumbnailsSidebarVisible = true;
    private bool _isClipboardSidebarVisible = true;

    /// <summary>
    /// Parameterless constructor for testing and scripting scenarios.
    /// Creates a ViewModel with default (NullLogger) dependencies.
    /// </summary>
    public MainWindowViewModel()
    {
        var nullLoggerFactory = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        var nullLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<MainWindowViewModel>.Instance;
        _logger = nullLogger;
        _loggerFactory = nullLoggerFactory;
        _documentService = new PdfDocumentService(Microsoft.Extensions.Logging.Abstractions.NullLogger<PdfDocumentService>.Instance);
        _renderService = new PdfRenderService(Microsoft.Extensions.Logging.Abstractions.NullLogger<PdfRenderService>.Instance);
        _redactionService = new RedactionService(Microsoft.Extensions.Logging.Abstractions.NullLogger<RedactionService>.Instance, nullLoggerFactory);
        _textExtractionService = new PdfTextExtractionService(Microsoft.Extensions.Logging.Abstractions.NullLogger<PdfTextExtractionService>.Instance);
        _searchService = new PdfSearchService(Microsoft.Extensions.Logging.Abstractions.NullLogger<PdfSearchService>.Instance);
        _filenameSuggestionService = new FilenameSuggestionService();
        _toastService = new ToastService();
        _dialogService = new NullUserDialogService();
        _signatureWorkflowService = CreateSignatureWorkflowService(
            new SignatureVerificationService(Microsoft.Extensions.Logging.Abstractions.NullLogger<SignatureVerificationService>.Instance),
            _dialogService,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SignatureVerificationWorkflowService>.Instance);

        InitializeCommands();
        _logger.LogInformation("MainWindowViewModel initialized (test mode)");
        InitializeSessionState();
        _logger.LogDebug("MainWindowViewModel initialization complete (test mode)");
    }

    public MainWindowViewModel(
        ILogger<MainWindowViewModel> logger,
        ILoggerFactory loggerFactory,
        PdfDocumentService documentService,
        PdfRenderService renderService,
        RedactionService redactionService,
        PdfTextExtractionService textExtractionService,
        PdfSearchService searchService,
        SignatureVerificationService signatureService,
        FilenameSuggestionService filenameSuggestionService,
        ToastService toastService,
        SignatureVerificationSummaryFormatter? signatureSummaryFormatter = null,
        IUserDialogService? dialogService = null,
        SignatureVerificationWorkflowService? signatureWorkflowService = null)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _documentService = documentService;
        _renderService = renderService;
        _redactionService = redactionService;
        _textExtractionService = textExtractionService;
        _searchService = searchService;
        _filenameSuggestionService = filenameSuggestionService;
        _toastService = toastService;
        _dialogService = dialogService ?? new NullUserDialogService();
        _signatureWorkflowService = signatureWorkflowService ?? new SignatureVerificationWorkflowService(
            signatureService,
            signatureSummaryFormatter ?? new SignatureVerificationSummaryFormatter(),
            _dialogService,
            loggerFactory.CreateLogger<SignatureVerificationWorkflowService>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SignatureVerificationWorkflowService>.Instance);

        InitializeCommands();
        _logger.LogInformation("MainWindowViewModel initialized");
        InitializeSessionState();
        _logger.LogDebug("MainWindowViewModel initialization complete");
    }

    private void InitializeSessionState()
    {
        LoadRecentFiles();
        LoadZoomPreference(); // Issue #32: Persist zoom level
    }

    private static SignatureVerificationWorkflowService CreateSignatureWorkflowService(
        SignatureVerificationService signatureService,
        IUserDialogService dialogService,
        ILogger<SignatureVerificationWorkflowService> logger) =>
        new(signatureService, new SignatureVerificationSummaryFormatter(), dialogService, logger);

    // Properties
    public ObservableCollection<PageThumbnail> PageThumbnails { get; } = new();

    /// <summary>
    /// Top-level outline nodes (PDF table of contents). Empty when the
    /// document has no /Outlines entry. Each node carries its own children
    /// for nested chapters/sections; the View binds via TreeView.
    /// </summary>
    public ObservableCollection<Models.OutlineNode> OutlineNodes { get; } = new();

    /// <summary>True when the loaded document has at least one outline entry.</summary>
    public bool HasOutline => OutlineNodes.Count > 0;
    private bool _isOutlineSidebarVisible = true;
    public bool IsOutlineSidebarVisible
    {
        get => _isOutlineSidebarVisible;
        set
        {
            this.RaiseAndSetIfChanged(ref _isOutlineSidebarVisible, value);
            // The left sidebar Border and the inter-panel splitter are computed
            // from both visibility flags, so re-raise them. (#369)
            this.RaisePropertyChanged(nameof(IsLeftSidebarVisible));
            this.RaisePropertyChanged(nameof(IsSidebarSplitterVisible));
        }
    }

    private Models.OutlineNode? _selectedOutlineNode;
    /// <summary>
    /// Bound to <see cref="Avalonia.Controls.TreeView.SelectedItem"/>. Setting
    /// this — i.e. the user clicking an outline row — navigates the viewer
    /// to the node's destination page.
    /// </summary>
    public Models.OutlineNode? SelectedOutlineNode
    {
        get => _selectedOutlineNode;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedOutlineNode, value);
            if (value != null) JumpToOutline(value);
        }
    }
    public ObservableCollection<ClipboardEntry> ClipboardHistory { get; } = new();

    public Bitmap? CurrentPageImage
    {
        get => _currentPageImage;
        set => this.RaiseAndSetIfChanged(ref _currentPageImage, value);
    }

    public PdfCoreDocument? PdfCoreDocument
    {
        get => _pdfCoreDocument;
        set => this.RaiseAndSetIfChanged(ref _pdfCoreDocument, value);
    }

    public PdfViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (_viewMode == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _viewMode, value);
            if (value == PdfViewMode.Continuous)
            {
                if (_isRedactionMode) IsRedactionMode = false;
                if (_isTextSelectionMode) IsTextSelectionMode = false;
                if (_isFormAuthoringMode) IsFormAuthoringMode = false;
                if (_isTypewriterMode) IsTypewriterMode = false;
            }

            this.RaisePropertyChanged(nameof(IsContinuousView));
            this.RaisePropertyChanged(nameof(CurrentModeText));
        }
    }

    public bool IsContinuousView => ViewMode == PdfViewMode.Continuous;

    public int CurrentPage => CurrentPageIndex + 1; // 1-based for PdfViewerControl

    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentPageIndex, value);
            this.RaisePropertyChanged(nameof(DisplayPageNumber));
            // CurrentPage is computed (CurrentPageIndex + 1) and bound to
            // PdfViewerControl.CurrentPage in MainWindow.axaml. Without this
            // notification, thumbnail clicks updated the index but the viewer
            // stayed on the previous page.
            this.RaisePropertyChanged(nameof(CurrentPage));
            this.RaisePropertyChanged(nameof(CurrentPageFormFields));
            UpdateThumbnailSelection();
            UpdateSearchHighlights(); // Update highlights when page changes (fixes #310)
            RefreshHiddenTextHighlights();
        }
    }

    public int TotalPages => _documentService.PageCount;

    public int DisplayPageNumber => CurrentPageIndex + 1;

    /// <summary>
    /// Context-aware text for Save button.
    /// Shows "Save Redacted Version" when working on original file with changes.
    /// Shows "Save" when working on redacted version or when no changes.
    /// </summary>
    public string SaveButtonText => FileState.GetSaveButtonText();

    /// <summary>
    /// Status bar text showing pending redaction count and file type.
    /// Updates dynamically as user marks/applies redactions.
    /// </summary>
    public string StatusBarText
    {
        get
        {
            if (RedactionWorkflow.PendingRedactions.Count > 0)
                return $"{RedactionWorkflow.PendingRedactions.Count} areas marked";
            if (FileState.TypewriterEditsCount > 0)
                return $"{FileState.TypewriterEditsCount} typewriter edit(s) pending";
            if (FileState.IsOriginalFile)
                return "Ready";
            return FileState.FileType;
        }
    }

    public double ZoomLevel
    {
        get => _zoomLevel;
        set
        {
            var oldValue = _zoomLevel;
            this.RaiseAndSetIfChanged(ref _zoomLevel, value);
            // Issue #32: Save zoom preference on user change (skip during auto-reset)
            if (!_skipZoomSave && Math.Abs(oldValue - value) > 0.001)
            {
                SaveZoomPreference();
            }
        }
    }

    /// <summary>Visibility of the left thumbnail strip. Toggled from View menu / toolbar.</summary>
    public bool IsThumbnailsSidebarVisible
    {
        get => _isThumbnailsSidebarVisible;
        set
        {
            this.RaiseAndSetIfChanged(ref _isThumbnailsSidebarVisible, value);
            this.RaisePropertyChanged(nameof(IsLeftSidebarVisible));
            this.RaisePropertyChanged(nameof(IsSidebarSplitterVisible));
        }
    }

    /// <summary>
    /// The left sidebar host is shown when *either* the outline or the
    /// thumbnails panel is enabled — so the two can be toggled independently
    /// (previously the whole sidebar was gated on thumbnails alone). (#369)
    /// </summary>
    public bool IsLeftSidebarVisible => IsOutlineSidebarVisible || IsThumbnailsSidebarVisible;

    /// <summary>The outline/thumbnails splitter only makes sense when both panels show. (#369)</summary>
    public bool IsSidebarSplitterVisible => IsOutlineSidebarVisible && IsThumbnailsSidebarVisible;

    /// <summary>Visibility of the right clipboard / pending-redactions sidebar.</summary>
    public bool IsClipboardSidebarVisible
    {
        get => _isClipboardSidebarVisible;
        set => this.RaiseAndSetIfChanged(ref _isClipboardSidebarVisible, value);
    }

    public void ToggleThumbnailsSidebar() =>
        IsThumbnailsSidebarVisible = !IsThumbnailsSidebarVisible;

    public void ToggleClipboardSidebar() =>
        IsClipboardSidebarVisible = !IsClipboardSidebarVisible;

    public void ToggleOutlineSidebar() =>
        IsOutlineSidebarVisible = !IsOutlineSidebarVisible;

    /// <summary>
    /// Click handler for an outline tree row. Jumps to the node's page
    /// (1-based) if the destination resolved during parse; no-op otherwise.
    /// Bound via JumpToOutlineCommand on the TreeView item template.
    /// </summary>
    public void JumpToOutline(Models.OutlineNode? node)
    {
        if (node == null)
        {
            _logger.LogDebug("JumpToOutline: null node");
            return;
        }
        if (node.PageNumber == null)
        {
            _logger.LogInformation("JumpToOutline: '{Title}' has no resolvable page", node.Title);
            return;
        }
        var idx = node.PageNumber.Value - 1;
        if (idx < 0 || idx >= TotalPages)
        {
            _logger.LogWarning("JumpToOutline: page {Page} out of range", node.PageNumber);
            return;
        }
        _logger.LogInformation("JumpToOutline: '{Title}' → page {Page}", node.Title, node.PageNumber);
        CurrentPageIndex = idx;
    }

    public string DocumentName => string.IsNullOrEmpty(_currentFilePath)
        ? "No document open"
        : System.IO.Path.GetFileName(_currentFilePath);

    /// <summary>
    /// Gets the text content of the currently displayed page via the text extraction service.
    /// Returns empty string if no document is loaded or extraction fails.
    /// Used for testing: verifies that redacted text has been removed from the PDF structure.
    /// </summary>
    public string CurrentPageText
    {
        get
        {
            if (_pdfCoreDocument == null || _currentPageIndex < 0 || _currentPageIndex >= TotalPages)
                return string.Empty;

            try
            {
                var text = _textExtractionService.ExtractTextFromPage(_currentFilePath, _currentPageIndex);
                return text ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract text from page {PageIndex}", _currentPageIndex);
                return string.Empty;
            }
        }
    }

    public bool IsRedactionMode
    {
        get => _isRedactionMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _isRedactionMode, value);
            if (value)
            {
                ViewMode = PdfViewMode.SinglePage;
                if (_isTextSelectionMode) IsTextSelectionMode = false;
                if (_isFormAuthoringMode) IsFormAuthoringMode = false;
                if (_isTypewriterMode) IsTypewriterMode = false;
            }
            this.RaisePropertyChanged(nameof(CurrentModeText));
            this.RaisePropertyChanged(nameof(InteractionMode));
            // The right sidebar's panel selector depends on this flag.
            this.RaisePropertyChanged(nameof(ShowPendingRedactionsPanel));
            this.RaisePropertyChanged(nameof(ShowClipboardHistoryPanel));
        }
    }

    public Rect CurrentRedactionArea
    {
        get => _currentRedactionArea;
        set => this.RaiseAndSetIfChanged(ref _currentRedactionArea, value);
    }

    public bool IsTextSelectionMode
    {
        get => _isTextSelectionMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _isTextSelectionMode, value);
            if (value)
            {
                ViewMode = PdfViewMode.SinglePage;
            }
            // Turn off redaction mode when entering text selection mode
            if (value && _isRedactionMode)
                IsRedactionMode = false;
            if (value && _isFormAuthoringMode)
                IsFormAuthoringMode = false;
            if (value && _isTypewriterMode)
                IsTypewriterMode = false;
            this.RaisePropertyChanged(nameof(CurrentModeText));
            this.RaisePropertyChanged(nameof(InteractionMode));
        }
    }

    public Rect CurrentTextSelectionArea
    {
        get => _currentTextSelectionArea;
        set => this.RaiseAndSetIfChanged(ref _currentTextSelectionArea, value);
    }

    public string SelectedText
    {
        get => _selectedText;
        set => this.RaiseAndSetIfChanged(ref _selectedText, value);
    }

    /// <summary>
    /// Called by the View when the user finishes a text-line selection
    /// drag. The text is already known at the View layer (computed via
    /// letter hit-testing in PdfViewerControl), so we don't need to
    /// re-extract from the rect — just publish to SelectedText, copy to
    /// the clipboard, and add to history.
    /// </summary>
    public async Task SetSelectedTextAndCopyAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        SelectedText = text;
        await PublishToClipboardAndHistoryAsync(text);
    }

    public int RenderCacheMax
    {
        get => _renderCacheMax;
        set
        {
            this.RaiseAndSetIfChanged(ref _renderCacheMax, value);
            _renderService.MaxCacheEntries = Math.Max(1, value);
        }
    }

    public string StatusText => _documentService.IsDocumentLoaded
        ? $"Page {CurrentPageIndex + 1} of {TotalPages} - Zoom: {ZoomLevel:P0}"
        : "No document loaded";

    public string OperationStatus
    {
        get => _operationStatus;
        set => this.RaiseAndSetIfChanged(ref _operationStatus, value);
    }

    public ObservableCollection<string> RecentFiles
    {
        get => _recentFiles;
        set => this.RaiseAndSetIfChanged(ref _recentFiles, value);
    }

    public bool HasRecentFiles => RecentFiles.Count > 0;

    public ObservableCollection<Avalonia.Controls.MenuItem> RecentFileMenuItems
    {
        get
        {
            var items = new ObservableCollection<Avalonia.Controls.MenuItem>();

            if (RecentFiles.Count == 0)
            {
                // Show placeholder when no recent files
                var noFilesItem = new Avalonia.Controls.MenuItem
                {
                    Header = "No recent files",
                    IsEnabled = false
                };
                items.Add(noFilesItem);
                return items;
            }

            foreach (var filePath in RecentFiles)
            {
                var menuItem = new Avalonia.Controls.MenuItem
                {
                    Header = System.IO.Path.GetFileName(filePath), // Show filename only
                    Command = LoadRecentFileCommand,
                    CommandParameter = filePath
                };
                // Set tooltip to show full path
                Avalonia.Controls.ToolTip.SetTip(menuItem, filePath);
                items.Add(menuItem);
            }
            return items;
        }
    }

    // Viewport dimensions (set by View for accurate zoom calculations).
    // Re-applies the active fit mode when they change so window resizes
    // keep the page snapped to the viewport.
    public double ViewportWidth
    {
        get => _viewportWidth;
        set
        {
            if (Math.Abs(_viewportWidth - value) < 0.5) return;
            this.RaiseAndSetIfChanged(ref _viewportWidth, value);
            ReapplyFitModeIfNeeded();
        }
    }

    public double ViewportHeight
    {
        get => _viewportHeight;
        set
        {
            if (Math.Abs(_viewportHeight - value) < 0.5) return;
            this.RaiseAndSetIfChanged(ref _viewportHeight, value);
            ReapplyFitModeIfNeeded();
        }
    }

    private void ReapplyFitModeIfNeeded()
    {
        if (PdfCoreDocument == null) return;
        switch (_zoomFitMode)
        {
            case ZoomFitMode.FitWidth:
                ZoomFitWidthInternal(latch: true);
                break;
            case ZoomFitMode.FitPage:
                ZoomFitPageInternal(latch: true);
                break;
        }
    }

    // Search highlight rectangles for current page (in screen coordinates)
    public ObservableCollection<Rect> CurrentPageSearchHighlights
    {
        get => _currentPageSearchHighlights;
        set => this.RaiseAndSetIfChanged(ref _currentPageSearchHighlights, value);
    }

    // Document status property
    public bool IsDocumentLoaded => _documentService.IsDocumentLoaded;

    // Command Implementations
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
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PDF Files")
                {
                    Patterns = new[] { "*.pdf" }
                }
            }
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
        // Validate input
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be null or empty", nameof(filePath));
        }

        if (!System.IO.File.Exists(filePath))
        {
            throw new FileNotFoundException($"PDF file not found: {filePath}", filePath);
        }

        _logger.LogInformation(">>> STEP 1: LoadDocumentAsync START for: {FilePath}", filePath);

        try
        {
            _logger.LogInformation(">>> STEP 2: Clearing previous document state");
            // Clear ALL state from previous document before loading new one
            CurrentRedactionArea = new Rect();
            CurrentTextSelectionArea = new Rect();
            RedactionWorkflow.Reset();
            ClearPendingTypewriterText();
            ClipboardHistory.Clear();
            PageThumbnails.Clear();
            _renderService.ClearCache();
            _hasInMemoryModifications = false;

            // Exit redaction mode if active
            if (IsRedactionMode)
            {
                IsRedactionMode = false;
            }
            if (IsTypewriterMode)
            {
                IsTypewriterMode = false;
            }

            _logger.LogInformation(">>> STEP 3: Setting _currentFilePath and FileState");
            _currentFilePath = filePath;
            FileState.SetDocument(filePath);

            _logger.LogInformation(">>> STEP 4: RaisePropertyChanged(DocumentName)");
            this.RaisePropertyChanged(nameof(DocumentName));
            this.RaisePropertyChanged(nameof(StatusBarText));

            // Stage label for the status bar — empty string clears it. Rendering
            // text + the parser parse both happen off the UI thread, but the
            // user otherwise has no signal that anything is in progress.
            OperationStatus = "Opening PDF…";

            // Open the document on a worker thread (the parser walks the
            // entire xref + catalog up front; on a 455-page book that's
            // hundreds of milliseconds we shouldn't pay on the dispatcher).
            // Both services need their own instance — they have separate
            // ownership lifecycles — but the parses run in parallel.
            _logger.LogInformation(">>> STEP 5: Loading Pdfe.Core document (parallel parse)");
            var docServiceLoad = Task.Run(() => _documentService.LoadDocument(filePath));
            var coreDocLoad = Task.Run(() => Pdfe.Core.Document.PdfDocument.Open(filePath));
            await Task.WhenAll(docServiceLoad, coreDocLoad);
            PdfCoreDocument = await coreDocLoad;
            _logger.LogInformation(">>> STEP 5: Both document instances loaded");

            _logger.LogInformation(">>> STEP 6: Setting CurrentPageIndex = 0");
            CurrentPageIndex = 0;

            // Render the current page FIRST — the user wants to see
            // something. The 455 thumbnails for the sidebar can come
            // afterwards. Pre-fix the order was reversed and on a long
            // document we'd block on N×Task.Run thumbnail jobs before the
            // user saw page 1, saturating the threadpool the main render
            // also needed.
            OperationStatus = $"Rendering page 1 of {TotalPages}…";
            _logger.LogInformation(">>> STEP 7: Rendering current page");
            await RenderCurrentPageAsync();
            _logger.LogInformation(">>> STEP 7: Current page rendered successfully");

            // Auto-fit-width on document open so the page is never wider than
            // the central pane (otherwise it scrolls behind the right sidebar
            // on default windows). The fit-mode latch in the ZoomFit* path
            // also keeps it fitted on subsequent window resizes.
            ReapplyFitModeIfNeeded();

            // Build the on-disk thumbnail cache for this document; the
            // strip's items will pull from it on demand as they scroll
            // into view (no eager batch render any more — that fired
            // hundreds of renders for pages the user might never look at).
            _thumbnailCache?.Dispose();
            _thumbnailCache = new Services.ThumbnailCacheService(
                filePath, PdfCoreDocument!,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

            _logger.LogInformation(">>> STEP 8: Creating thumbnail placeholders (lazy load)");
            await LoadPageThumbnailsAsync();

            // Parse the document's table-of-contents outline (if any).
            // Cheap — just a tree walk over the catalog's /Outlines, no
            // text extraction needed. Populates the left-sidebar tree.
            try
            {
                var outline = Pdfe.Core.Document.PdfOutlineParser.Parse(PdfCoreDocument!);
                OutlineNodes.Clear();
                foreach (var item in outline)
                    OutlineNodes.Add(Models.OutlineNode.From(item));
                this.RaisePropertyChanged(nameof(HasOutline));
                _logger.LogInformation(">>> STEP 8b: Outline parsed — {Count} top-level entries", outline.Count);
            }
            catch (Exception outlineEx)
            {
                _logger.LogWarning(outlineEx, "Failed to parse document outline");
                OutlineNodes.Clear();
            }

            // Kick off the text index build in the background. First
            // search after this completes is sub-second instead of the
            // multi-second per-keystroke walk we used to do live.
            _indexBuildCts?.Cancel();
            _indexBuildCts = new System.Threading.CancellationTokenSource();
            TextIndex = new Services.DocumentTextIndex(PdfCoreDocument!,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            var indexCts = _indexBuildCts;
            var totalPagesForIndex = TotalPages;
            var indexProgress = new Progress<(int Done, int Total)>(p =>
            {
                if (indexCts.IsCancellationRequested) return;
                // Don't overwrite a "Searching…" / "Rendering…" status —
                // index building runs alongside other ops and shouldn't
                // hijack the bar. Only show indexing progress when nothing
                // else is in flight.
                if (string.IsNullOrEmpty(OperationStatus) ||
                    OperationStatus.StartsWith("Indexing"))
                {
                    OperationStatus = p.Done < p.Total
                        ? $"Indexing for search… {p.Done}/{p.Total}"
                        : string.Empty;
                }
            });
            _ = TextIndex.BuildAsync(indexProgress, indexCts.Token);

            _logger.LogInformation(">>> STEP 9: RaisePropertyChanged(TotalPages)");
            this.RaisePropertyChanged(nameof(TotalPages));

            _logger.LogInformation(">>> STEP 10: RaisePropertyChanged(StatusText)");
            this.RaisePropertyChanged(nameof(StatusText));

            _logger.LogInformation(">>> STEP 11: RaisePropertyChanged(IsDocumentLoaded)");
            this.RaisePropertyChanged(nameof(IsDocumentLoaded));

            _logger.LogInformation(">>> STEP 12: Adding to recent files");
            AddToRecentFiles(filePath);

            _logger.LogInformation(">>> STEP 12b: Restoring document state (zoom, page index)");
            await RestoreDocumentStateAsync(filePath);

            _logger.LogInformation(">>> STEP 13: LoadDocumentAsync COMPLETE. Total pages: {PageCount}", TotalPages);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "!!! ERROR in LoadDocumentAsync at some step: {FilePath}", filePath);
            _logger.LogError("!!! Exception Type: {ExceptionType}", ex.GetType().Name);
            _logger.LogError("!!! Exception Message: {Message}", ex.Message);

            // Clear document state on failure
            _currentFilePath = string.Empty;
            FileState.Reset();
            OperationStatus = string.Empty;

            // Determine user-friendly error message
            string userMessage;
            if (ex.Message.Contains("owner password") || ex.Message.Contains("password is required"))
            {
                userMessage = "This PDF is password-protected and cannot be opened for editing.";
                _toastService.ShowError("Cannot Open PDF", "Password-protected file. Please remove protection and try again.");
            }
            else if (ex.Message.Contains("encrypted"))
            {
                userMessage = "This PDF is encrypted and cannot be opened.";
                _toastService.ShowError("Cannot Open PDF", "File is encrypted. Please provide an unencrypted version.");
            }
            else
            {
                userMessage = $"Failed to open PDF:\n\n{ex.Message}";
                _toastService.ShowError("Cannot Open PDF", ex.Message);
            }

            // Show error dialog to user (StatusBarText will show "Ready" from FileState.Reset())
            this.RaisePropertyChanged(nameof(StatusBarText));
            await ShowErrorDialogAsync("Cannot Open PDF", userMessage);
        }
    }

    private async Task ShowErrorDialogAsync(string title, string message)
    {
        try
        {
            var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            if (mainWindow != null)
            {
                var dialog = new Avalonia.Controls.Window
                {
                    Title = title,
                    Width = 450,
                    Height = 200,
                    WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    Content = new Avalonia.Controls.StackPanel
                    {
                        Margin = new Avalonia.Thickness(20),
                        Spacing = 15,
                        Children =
                        {
                            new Avalonia.Controls.TextBlock
                            {
                                Text = message,
                                TextWrapping = Avalonia.Media.TextWrapping.Wrap
                            },
                            new Avalonia.Controls.Button
                            {
                                Content = "OK",
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                                Width = 80
                            }
                        }
                    }
                };

                // Wire up the OK button to close the dialog
                if (dialog.Content is Avalonia.Controls.StackPanel panel)
                {
                    var button = panel.Children.OfType<Avalonia.Controls.Button>().FirstOrDefault();
                    if (button != null)
                    {
                        button.Click += (s, e) => dialog.Close();
                    }
                }

                await dialog.ShowDialog(mainWindow);
            }
        }
        catch (Exception dialogEx)
        {
            _logger.LogError(dialogEx, "Failed to show error dialog");
        }
    }

    private async Task SaveFileAsync()
    {
        _logger.LogInformation("Save command triggered");

        if (!_documentService.IsDocumentLoaded)
        {
            _logger.LogWarning("Cannot save: No document loaded");
            return;
        }

        // CRITICAL: If working on the original with pending redactions, force
        // the redacted-copy workflow. Other edits still preserve the original,
        // but they use the normal Save As picker instead of the redaction dialog.
        if (FileState.IsOriginalFile && FileState.HasUnsavedChanges)
        {
            if (FileState.PendingRedactionsCount > 0)
            {
                _logger.LogInformation("Original file with pending redactions detected - triggering redacted-copy workflow");
                await ApplyAllRedactionsAsync();
            }
            else
            {
                _logger.LogInformation("Original file with non-redaction edits detected - triggering Save As workflow");
                await SaveAsAsync();
            }

            return;
        }

        // Safe to save directly - either redacted version or no changes
        try
        {
            var document = _documentService.GetCurrentDocument();
            var flattenedTypewriter = document != null && ApplyPendingTypewriterText(document);

            _documentService.SaveDocument();
            if (flattenedTypewriter)
            {
                ClearPendingTypewriterText();
                if (!string.IsNullOrWhiteSpace(_currentFilePath))
                    await ReloadPdfCoreDocumentAfterSaveAsync(_currentFilePath);
            }

            _logger.LogInformation("Document saved successfully");
            _toastService.ShowSuccess("Document saved");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving document");
            _toastService.ShowError("Failed to save document", ex.Message);
        }

        await Task.CompletedTask;
    }

    private async Task RemoveCurrentPageAsync()
    {
        _logger.LogInformation("Remove page command triggered. Current page: {PageIndex}", CurrentPageIndex);

        if (!_documentService.IsDocumentLoaded || TotalPages <= 1)
        {
            _logger.LogWarning("Cannot remove page: No document loaded or only one page remaining");
            return;
        }

        try
        {
            _documentService.RemovePage(CurrentPageIndex);

            // Adjust current page if needed
            if (CurrentPageIndex >= TotalPages)
            {
                CurrentPageIndex = TotalPages - 1;
                _logger.LogDebug("Adjusted current page index to {PageIndex}", CurrentPageIndex);
            }

            _logger.LogDebug("Reloading thumbnails and rendering current page");
            await LoadPageThumbnailsAsync();
            await RenderCurrentPageAsync();

            this.RaisePropertyChanged(nameof(TotalPages));
            this.RaisePropertyChanged(nameof(StatusText));

            _logger.LogInformation("Page removed successfully. Remaining pages: {PageCount}", TotalPages);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing page");
        }
    }

    private async Task AddPagesAsync()
    {
        _logger.LogInformation("Add pages command triggered");

        if (!_documentService.IsDocumentLoaded)
        {
            _logger.LogWarning("Cannot add pages: No document loaded");
            return;
        }

        var storageProvider = GetStorageProvider();
        if (storageProvider == null)
        {
            _logger.LogWarning("Storage provider unavailable, cannot show Add Pages dialog");
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select PDF to Add Pages From",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PDF Files")
                {
                    Patterns = new[] { "*.pdf" }
                }
            }
        });

        if (files.Count == 0)
        {
            _logger.LogInformation("Add pages dialog cancelled");
            return;
        }

        var filePath = files[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            _logger.LogWarning("Selected file has no local path");
            return;
        }

        await AddPagesFromFileAsync(filePath);
    }

    public async Task AddPagesFromFileAsync(string sourcePdfPath)
    {
        if (!_documentService.IsDocumentLoaded)
            return;

        try
        {
            _documentService.AddPagesFromPdf(sourcePdfPath);
            await LoadPageThumbnailsAsync();
            this.RaisePropertyChanged(nameof(TotalPages));
            this.RaisePropertyChanged(nameof(StatusText));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding pages");
        }
    }

    private void ToggleTextSelectionMode()
    {
        _logger.LogInformation("Toggle text selection mode. Current: {Current}", IsTextSelectionMode);
        IsTextSelectionMode = !IsTextSelectionMode;

        if (!IsTextSelectionMode)
        {
            // Clear selection when exiting mode
            CurrentTextSelectionArea = new Rect();
            SelectedText = string.Empty;
        }
    }

    private void ToggleContinuousView()
    {
        ViewMode = IsContinuousView ? PdfViewMode.SinglePage : PdfViewMode.Continuous;
    }

    private async Task CopyTextAsync()
    {
        _logger.LogInformation("Copy text command triggered");

        if (!_documentService.IsDocumentLoaded || string.IsNullOrEmpty(_currentFilePath))
        {
            _logger.LogWarning("Cannot copy text: No document loaded");
            return;
        }

        try
        {
            string textToCopy;

            // Letter-walk selection (PdfViewerControl.OnInteractionLayerPointerReleased)
            // already populated SelectedText with the exact text the user
            // dragged over. Use it directly. The earlier rect-based path
            // re-extracted from CurrentTextSelectionArea, which silently
            // grabbed extra glyphs from neighbouring lines/columns when
            // the bbox extended past the actual selection — the user's
            // "Ctrl+C copies wrong text" bug.
            if (!string.IsNullOrEmpty(SelectedText))
            {
                textToCopy = SelectedText;
            }
            else
            {
                // No live selection — fall back to whole-page extraction.
                _logger.LogInformation("No live selection; extracting all text from page {PageIndex}", CurrentPageIndex + 1);
                textToCopy = _textExtractionService.ExtractTextFromPage(_currentFilePath, CurrentPageIndex);
            }

            if (string.IsNullOrEmpty(textToCopy))
            {
                _logger.LogWarning("No text to copy");
                return;
            }

            SelectedText = textToCopy;
            await PublishToClipboardAndHistoryAsync(textToCopy);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error copying text");
            _toastService.ShowError("Failed to copy text", ex.Message);
        }
    }

    /// <summary>
    /// Copy the given text to the OS clipboard (best effort) AND record
    /// it in <see cref="ClipboardHistory"/>. Splitting these concerns
    /// keeps the in-app history correct even when the OS clipboard isn't
    /// reachable (headless tests, transient lifecycle states).
    /// </summary>
    private async Task PublishToClipboardAndHistoryAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var entry = new ClipboardEntry
        {
            Text = text,
            Timestamp = DateTime.Now,
            PageNumber = CurrentPageIndex + 1,
        };

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ClipboardHistory.Insert(0, entry);
            while (ClipboardHistory.Count > 20)
                ClipboardHistory.RemoveAt(ClipboardHistory.Count - 1);
        });

        try
        {
            var topLevel = Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(text);
                _logger.LogInformation("✓ Copied {Length} characters to clipboard", text.Length);
            }
            else
            {
                _logger.LogWarning("OS clipboard unavailable; text recorded in history only");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set OS clipboard");
        }
    }

    private void ZoomIn()
    {
        // User-initiated zoom drops out of any auto-fit mode so a window
        // resize doesn't immediately undo what they just clicked.
        _zoomFitMode = ZoomFitMode.Manual;
        ZoomLevel = Math.Min(ZoomLevel * 1.25, 5.0);
        this.RaisePropertyChanged(nameof(StatusText));
    }

    private void ZoomOut()
    {
        _zoomFitMode = ZoomFitMode.Manual;
        ZoomLevel = Math.Max(ZoomLevel / 1.25, 0.25);
        this.RaisePropertyChanged(nameof(StatusText));
    }

    private void ZoomActualSize()
    {
        _logger.LogInformation("Setting zoom to actual size (100%)");
        _zoomFitMode = ZoomFitMode.Manual;
        ZoomLevel = 1.0;
        this.RaisePropertyChanged(nameof(StatusText));
    }

    private void ZoomFitWidth() => ZoomFitWidthInternal(latch: true);
    private void ZoomFitPage() => ZoomFitPageInternal(latch: true);

    /// <summary>
    /// Resize zoom to fit the page width. When <paramref name="latch"/> is
    /// true the mode is recorded so subsequent viewport-size changes
    /// re-apply this fit until the user manually zooms.
    /// </summary>
    private void ZoomFitWidthInternal(bool latch)
    {
        _logger.LogInformation("Setting zoom to fit width");
        if (latch) _zoomFitMode = ZoomFitMode.FitWidth;
        if (TryGetPageDimensionsInViewerDips(out var pageW, out _) &&
            ViewportWidth > 0)
        {
            // Tiny gutter so the page edge doesn't kiss the scrollbar /
            // central-pane border. Now that the viewport measurement is
            // the *inside-the-scrollbars* width and zoom uses LayoutTransform,
            // the math doesn't need a 40-DIP fudge any more.
            const double margin = 8;
            var target = Math.Max(1.0, ViewportWidth - margin);
            ZoomLevel = Math.Clamp(target / pageW, 0.25, 5.0);
            _logger.LogDebug("Fit width: viewport={Viewport}, page={Page}, zoom={Zoom:P0}",
                ViewportWidth, pageW, ZoomLevel);
        }
        else
        {
            ZoomLevel = 1.0;
        }
        this.RaisePropertyChanged(nameof(StatusText));
    }

    private void ZoomFitPageInternal(bool latch)
    {
        _logger.LogInformation("Setting zoom to fit page");
        if (latch) _zoomFitMode = ZoomFitMode.FitPage;
        if (TryGetPageDimensionsInViewerDips(out var pageW, out var pageH) &&
            ViewportWidth > 0 && ViewportHeight > 0)
        {
            const double marginH = 8;
            const double marginV = 8;
            var targetW = Math.Max(1.0, ViewportWidth - marginH);
            var targetH = Math.Max(1.0, ViewportHeight - marginV);
            // Whichever dimension is the binding constraint wins.
            var zoom = Math.Min(targetW / pageW, targetH / pageH);
            ZoomLevel = Math.Clamp(zoom, 0.25, 5.0);
            _logger.LogDebug("Fit page: vp=({Vw}x{Vh}), pg=({Pw}x{Ph}), zoom={Zoom:P0}",
                ViewportWidth, ViewportHeight, pageW, pageH, ZoomLevel);
        }
        else
        {
            ZoomLevel = 1.0;
        }
        this.RaisePropertyChanged(nameof(StatusText));
    }

    /// <summary>
    /// Page dimensions in viewer DIPs at zoom 1.0. Reads page size from
    /// the parsed PdfCoreDocument (in PDF points) so we don't depend on
    /// the legacy <c>_currentPageImage</c> being populated, and converts
    /// to DIPs at the viewer's render DPI (the bitmap is tagged 96 DPI
    /// in WriteableBitmap so 1 bitmap-pixel = 1 DIP, and 1 page-point at
    /// our render DPI = render-DPI/72 bitmap-pixels).
    /// </summary>
    private bool TryGetPageDimensionsInViewerDips(out double widthDip, out double heightDip)
    {
        widthDip = 0; heightDip = 0;
        var doc = PdfCoreDocument;
        if (doc == null) return false;
        var pageNumber = CurrentPageIndex + 1;
        if (pageNumber < 1 || pageNumber > doc.PageCount) return false;
        var page = doc.GetPage(pageNumber);
        const double renderDpi = 120.0; // matches PdfViewerControl.DefaultRenderDpi
        widthDip  = page.Width  * (renderDpi / 72.0);
        heightDip = page.Height * (renderDpi / 72.0);
        return widthDip > 0 && heightDip > 0;
    }

    private async Task NextPageAsync()
    {
        if (CurrentPageIndex < TotalPages - 1)
        {
            CurrentPageIndex++;
            await RenderCurrentPageAsync();
        }
    }

    private async Task PreviousPageAsync()
    {
        if (CurrentPageIndex > 0)
        {
            CurrentPageIndex--;
            await RenderCurrentPageAsync();
        }
    }

    private async Task GoToPageAsync(int pageIndex)
    {
        _logger.LogInformation("Navigating to page {PageIndex}", pageIndex);

        if (pageIndex >= 0 && pageIndex < TotalPages && pageIndex != CurrentPageIndex)
        {
            CurrentPageIndex = pageIndex;
            await RenderCurrentPageAsync();
        }
    }

    private async Task RotatePageLeftAsync()
    {
        _logger.LogInformation("Rotating current page left (counter-clockwise)");

        if (!_documentService.IsDocumentLoaded)
        {
            _logger.LogWarning("Cannot rotate page: No document loaded");
            return;
        }

        try
        {
            _documentService.RotatePageLeft(CurrentPageIndex);
            _logger.LogInformation("Page {PageIndex} rotated left successfully", CurrentPageIndex);

            // Re-render the page to show the rotation
            await RenderCurrentPageAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rotating page left");
        }
    }

    private async Task RotatePageRightAsync()
    {
        _logger.LogInformation("Rotating current page right (clockwise)");

        if (!_documentService.IsDocumentLoaded)
        {
            _logger.LogWarning("Cannot rotate page: No document loaded");
            return;
        }

        try
        {
            _documentService.RotatePageRight(CurrentPageIndex);
            _logger.LogInformation("Page {PageIndex} rotated right successfully", CurrentPageIndex);

            // Re-render the page to show the rotation
            await RenderCurrentPageAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rotating page right");
        }
    }

    private async Task RotatePage180Async()
    {
        _logger.LogInformation("Rotating current page 180 degrees");

        if (!_documentService.IsDocumentLoaded)
        {
            _logger.LogWarning("Cannot rotate page: No document loaded");
            return;
        }

        try
        {
            _documentService.RotatePage180(CurrentPageIndex);
            _logger.LogInformation("Page {PageIndex} rotated 180 degrees successfully", CurrentPageIndex);

            // Re-render the page to show the rotation
            await RenderCurrentPageAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rotating page 180 degrees");
        }
    }

    private async Task RenderCurrentPageAsync()
    {
        _logger.LogInformation(">>> RenderCurrentPageAsync: START (hasInMemoryModifications={HasMods})", _hasInMemoryModifications);

        if (string.IsNullOrEmpty(_currentFilePath) || !_documentService.IsDocumentLoaded)
        {
            _logger.LogWarning(">>> RenderCurrentPageAsync: Skipping (no file or document not loaded)");
            return;
        }

        // Surface a stage label in the status bar so the user has feedback
        // during the render. Keep the existing label if a thumbnail batch
        // is still in flight (it'll overwrite this anyway as it ticks).
        var existingStatus = OperationStatus;
        var renderingStatus = $"Rendering page {DisplayPageNumber} of {TotalPages}…";
        OperationStatus = renderingStatus;

        try
        {
            SkiaSharp.SKBitmap? skBitmap = null;

            // If document has in-memory modifications (e.g., applied redactions not yet saved),
            // we must render from the in-memory stream, not the file on disk.
            // This fixes the bug where redacted text was still visible until file reopen.
            if (_hasInMemoryModifications)
            {
                _logger.LogInformation(">>> RenderCurrentPageAsync: Using in-memory stream (document has unsaved modifications)");
                var docStream = _documentService.GetCurrentDocumentAsStream();
                if (docStream != null)
                {
                    try
                    {
                        var memoryStream = new System.IO.MemoryStream();
                        await docStream.CopyToAsync(memoryStream);
                        memoryStream.Position = 0;
                        docStream.Dispose();
                        skBitmap = await _renderService.RenderPageFromStreamAsync(memoryStream, CurrentPageIndex);
                    }
                    catch (Exception streamEx)
                    {
                        _logger.LogWarning(streamEx, "Failed to render from in-memory stream, falling back to file");
                    }
                }
            }

            // Fallback to file-based rendering if in-memory rendering failed or wasn't needed
            if (skBitmap == null)
            {
                _logger.LogInformation(">>> RenderCurrentPageAsync: Calling _renderService.RenderPageAsync for page {PageIndex}", CurrentPageIndex);
                skBitmap = await _renderService.RenderPageAsync(_currentFilePath, CurrentPageIndex);
            }

            using (skBitmap)
            {
                _logger.LogInformation(">>> RenderCurrentPageAsync: Converting to Avalonia bitmap");
                var avaloniaBitmap = ToAvaloniaBitmap(skBitmap);

                _logger.LogInformation(">>> RenderCurrentPageAsync: Setting CurrentPageImage");
                CurrentPageImage = avaloniaBitmap;
            }

            _logger.LogInformation(">>> RenderCurrentPageAsync: COMPLETE");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "!!! ERROR in RenderCurrentPageAsync");
            _logger.LogError("!!! Exception Type: {ExceptionType}", ex.GetType().Name);
            _logger.LogError("!!! Exception Message: {Message}", ex.Message);
            throw;
        }
        finally
        {
            // Only clear if we set the rendering label AND nothing else
            // overwrote it during the render (the thumbnail batch keeps
            // updating as pages complete and should win).
            if (OperationStatus == renderingStatus)
                OperationStatus = existingStatus;
        }
    }

    private void UpdateThumbnailSelection()
    {
        foreach (var thumbnail in PageThumbnails)
        {
            thumbnail.IsSelected = (thumbnail.PageIndex == CurrentPageIndex);
        }
    }

    /// <summary>
    /// Lazy thumbnail strategy: create one PageThumbnail placeholder per
    /// page (so the sidebar shows the right number of slots immediately)
    /// and let the View trigger renders as items scroll into view via
    /// <c>EnsureThumbnailLoadedAsync</c>. Combined with the on-disk
    /// thumbnail cache (ThumbnailCacheService), reopening a book renders
    /// only the pages the user looks at, and re-opens hit a sub-ms WebP
    /// decode rather than re-rasterising.
    /// </summary>
    private Task LoadPageThumbnailsAsync()
    {
        _logger.LogInformation(">>> LoadPageThumbnailsAsync (lazy): START");
        try
        {
            PageThumbnails.Clear();
            var total = TotalPages;
            for (int i = 0; i < total; i++)
                PageThumbnails.Add(new PageThumbnail { PageNumber = i + 1, PageIndex = i });
            _logger.LogInformation(
                ">>> LoadPageThumbnailsAsync: created {Count} placeholders; loads happen on demand",
                total);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "!!! ERROR creating thumbnail placeholders");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Render or load the thumbnail for one page. Called by the View when
    /// the corresponding item scrolls into the visible viewport. Idempotent:
    /// repeated calls for an already-loaded page no-op; concurrent calls for
    /// the same page coalesce on a single in-flight Task in the cache service.
    /// </summary>
    public async Task EnsureThumbnailLoadedAsync(int pageIndex,
        System.Threading.CancellationToken cancellationToken = default)
    {
        if (pageIndex < 0 || pageIndex >= PageThumbnails.Count) return;
        var thumb = PageThumbnails[pageIndex];
        if (thumb.ThumbnailImage != null) return; // already loaded
        if (_thumbnailCache == null) return; // no doc loaded yet

        try
        {
            using var sk = await _thumbnailCache.GetThumbnailAsync(pageIndex, cancellationToken);
            if (sk == null) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                thumb.ThumbnailImage = ToAvaloniaBitmap(sk);
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { /* expected when scrolled away */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EnsureThumbnailLoadedAsync failed for page {Page}", pageIndex);
        }
    }

    // File Menu Commands

    private async Task SaveAsAsync()
    {
        _logger.LogInformation("Save As command triggered");

        if (!_documentService.IsDocumentLoaded)
        {
            _logger.LogWarning("Cannot save: No document loaded");
            return;
        }

        var storageProvider = GetStorageProvider();
        if (storageProvider == null)
        {
            _logger.LogWarning("Storage provider unavailable, cannot show Save As dialog");
            return;
        }

        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save PDF As",
            DefaultExtension = "pdf",
            SuggestedFileName = string.IsNullOrWhiteSpace(DocumentName) ? "document.pdf" : DocumentName,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PDF Files")
                {
                    Patterns = new[] { "*.pdf" }
                }
            }
        });

        if (file == null)
        {
            _logger.LogInformation("Save As dialog cancelled");
            return;
        }

        var filePath = file.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            _logger.LogWarning("Save As target has no local path");
            return;
        }

        await SaveFileAsAsync(filePath);
    }

    public async Task SaveFileAsAsync(string filePath)
    {
        _logger.LogInformation("Saving document to: {FilePath}", filePath);

        try
        {
            var document = _documentService.GetCurrentDocument();
            var flattenedTypewriter = document != null && ApplyPendingTypewriterText(document);

            _documentService.SaveDocument(filePath);
            _currentFilePath = filePath;
            FileState.UpdateCurrentPath(filePath);
            if (flattenedTypewriter)
                ClearPendingTypewriterText();
            this.RaisePropertyChanged(nameof(DocumentName));
            this.RaisePropertyChanged(nameof(SaveButtonText));
            this.RaisePropertyChanged(nameof(StatusBarText));
            await ReloadPdfCoreDocumentAfterSaveAsync(filePath);
            _logger.LogInformation("Document saved successfully to: {FilePath}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving document to: {FilePath}", filePath);
        }

        await Task.CompletedTask;
    }

    private void CloseDocument()
    {
        _logger.LogInformation("Close document command triggered");

        if (!_documentService.IsDocumentLoaded)
        {
            _logger.LogWarning("No document to close");
            return;
        }

        try
        {
            // Save document state before closing
            SaveDocumentState();

            // Close the PDF document
            _documentService.CloseDocument();

            // Clear file path
            _currentFilePath = string.Empty;

            // Clear visual state
            CurrentPageImage = null;
            PdfCoreDocument = null;
            PageThumbnails.Clear();
            _renderService.ClearCache();

            // Clear redaction state (FIX: These were persisting!)
            CurrentRedactionArea = new Rect();
            CurrentTextSelectionArea = new Rect();
            RedactionWorkflow.Reset();
            ClearPendingTypewriterText();
            ClipboardHistory.Clear();
            _hasInMemoryModifications = false;

            // Clear search state
            SearchText = string.Empty;
            SearchMatches.Clear();
            CurrentSearchMatchIndex = -1;
            IsSearchVisible = false;

            // Exit redaction mode if active
            if (IsRedactionMode)
            {
                IsRedactionMode = false;
            }
            if (IsTypewriterMode)
            {
                IsTypewriterMode = false;
            }

            // Reset navigation state
            CurrentPageIndex = 0;

            // Reset zoom to default (skip saving - user's preference should persist)
            _skipZoomSave = true;
            ZoomLevel = 1.0;
            _skipZoomSave = false;

            // Notify UI of all state changes
            this.RaisePropertyChanged(nameof(DocumentName));
            this.RaisePropertyChanged(nameof(TotalPages));
            this.RaisePropertyChanged(nameof(StatusText));
            this.RaisePropertyChanged(nameof(StatusBarText));
            this.RaisePropertyChanged(nameof(IsDocumentLoaded));
            this.RaisePropertyChanged(nameof(CurrentRedactionArea));
            this.RaisePropertyChanged(nameof(CurrentTextSelectionArea));
            this.RaisePropertyChanged(nameof(IsRedactionMode));
            this.RaisePropertyChanged(nameof(IsTypewriterMode));
            this.RaisePropertyChanged(nameof(SaveButtonText));

            _logger.LogInformation("Document closed successfully - all state cleared");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error closing document");
        }
    }

    private void Exit()
    {
        _logger.LogInformation("Exit command triggered");

        var lifetime = Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;

        if (lifetime != null)
        {
            lifetime.Shutdown();
        }
    }

    private async Task LoadRecentFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            _logger.LogWarning("Recent file path is empty");
            return;
        }

        if (!System.IO.File.Exists(filePath))
        {
            _logger.LogWarning("Recent file not found: {FilePath}", filePath);
            // Issue #25: Remove deleted file from recent files list
            RemoveFromRecentFiles(filePath);
            return;
        }

        await LoadDocumentAsync(filePath);
    }

    // Tools Menu Commands

    private async Task ExportCurrentPageAsync()
    {
        _logger.LogInformation("Export current page command triggered (page {PageNumber})", CurrentPageIndex + 1);

        if (!_documentService.IsDocumentLoaded || string.IsNullOrEmpty(_currentFilePath))
        {
            _logger.LogWarning("Cannot export: No document loaded");
            return;
        }

        var storageProvider = GetStorageProvider();
        if (storageProvider == null)
        {
            _logger.LogWarning("Storage provider unavailable, cannot show Save dialog");
            return;
        }

        var suggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_currentFilePath) +
                                $"_page{CurrentPageIndex + 1}.png";

        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Current Page",
            SuggestedFileName = suggestedFileName,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } },
                new FilePickerFileType("JPEG Image") { Patterns = new[] { "*.jpg", "*.jpeg" } }
            },
            DefaultExtension = "png"
        });

        if (file == null)
        {
            _logger.LogInformation("Export dialog cancelled");
            return;
        }

        var filePath = file.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            _logger.LogWarning("Export target file has no local path");
            return;
        }

        await ExportCurrentPageToImageAsync(filePath);
    }

    public async Task ExportCurrentPageToImageAsync(string outputPath, int dpi = 150)
    {
        _logger.LogInformation("Exporting current page {PageNumber} to: {Path}, DPI: {DPI}",
            CurrentPageIndex + 1, outputPath, dpi);

        if (!_documentService.IsDocumentLoaded || string.IsNullOrEmpty(_currentFilePath))
        {
            _logger.LogError("Cannot export: No document loaded");
            return;
        }

        try
        {
            var bitmap = await _renderService.RenderPageAsync(_currentFilePath, CurrentPageIndex, dpi);
            if (bitmap != null)
            {
                var extension = System.IO.Path.GetExtension(outputPath).ToLowerInvariant();
                SKEncodedImageFormat imageFormat = extension switch
                {
                    ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
                    _ => SKEncodedImageFormat.Png
                };

                using var image = SKImage.FromBitmap(bitmap);
                using var encodedData = image.Encode(imageFormat, 90);
                using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                encodedData.SaveTo(fileStream);

                _logger.LogInformation("Page {PageNumber} exported to: {FilePath}", CurrentPageIndex + 1, outputPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting current page");
        }
    }

    private async Task ExportPagesAsync()
    {
        _logger.LogInformation("Export pages command triggered");

        if (!_documentService.IsDocumentLoaded)
        {
            _logger.LogWarning("Cannot export: No document loaded");
            return;
        }

        var storageProvider = GetStorageProvider();
        if (storageProvider == null)
        {
            _logger.LogWarning("Storage provider unavailable, cannot show Export dialog");
            return;
        }

        var folder = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Folder for Exported Images",
            AllowMultiple = false
        });

        if (folder.Count == 0)
        {
            _logger.LogInformation("Export dialog cancelled");
            return;
        }

        var folderPath = folder[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            _logger.LogWarning("Export target folder has no local path");
            return;
        }

        await ExportPagesToImagesAsync(folderPath, "png", 150);
    }

    public async Task ExportPagesToImagesAsync(string outputFolder, string format = "png", int dpi = 150)
    {
        _logger.LogInformation("Exporting pages to: {Folder}, Format: {Format}, DPI: {DPI}",
            outputFolder, format, dpi);

        if (!_documentService.IsDocumentLoaded || string.IsNullOrEmpty(_currentFilePath))
        {
            _logger.LogError("Cannot export: No document loaded");
            return;
        }

        try
        {
            for (int i = 0; i < TotalPages; i++)
            {
                _logger.LogDebug("Exporting page {PageIndex}", i);

                var bitmap = await _renderService.RenderPageAsync(_currentFilePath, i, dpi);
                if (bitmap != null)
                {
                    var fileName = $"page_{i + 1:D3}.{format}";
                    var filePath = System.IO.Path.Combine(outputFolder, fileName);

                    // Determine the image format based on the 'format' parameter
                    SKEncodedImageFormat imageFormat = SKEncodedImageFormat.Png;
                    if (format.Equals("jpg", StringComparison.OrdinalIgnoreCase) || format.Equals("jpeg", StringComparison.OrdinalIgnoreCase))
                    {
                        imageFormat = SKEncodedImageFormat.Jpeg;
                    }

                    using var image = SKImage.FromBitmap(bitmap);
                    using var encodedData = image.Encode(imageFormat, 90); // 90% quality for JPG
                    using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
                    encodedData.SaveTo(fileStream);
                    _logger.LogDebug("Page {PageIndex} exported to: {FilePath}", i, filePath);
                }
            }

            _logger.LogInformation("All {Count} pages exported successfully", TotalPages);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting pages");
        }
    }

    private async Task PrintAsync()
    {
        _logger.LogInformation("Print command triggered");

        if (!_documentService.IsDocumentLoaded)
        {
            _logger.LogWarning("Cannot print: No document loaded");
            await _dialogService.ShowMessageAsync("Print", "Open a PDF before printing.");
            return;
        }

        const string message = "Printing is not available in this build. Use Export Current Page or Export All Pages as Images from the Document menu.";
        _logger.LogInformation("Print command unavailable: {Message}", message);
        await _dialogService.ShowMessageAsync("Print", message);
    }

    // Help Menu Commands

    private void ShowAbout()
    {
        _logger.LogInformation("About dialog requested");
        var owner = GetMainWindow();
        if (owner == null) return;

        // Pop the rich About window with the embedded third-party-license
        // manifest. Modal so it acts like a standard "About…" dialog.
        var dialog = new Views.AboutWindow();
        _ = dialog.ShowDialog(owner);
    }

    private async void ShowKeyboardShortcuts()
    {
        _logger.LogInformation("Keyboard shortcuts dialog requested");

        var window = GetMainWindow();
        if (window != null)
        {
            var messageBox = new FluentAvalonia.UI.Controls.FAContentDialog
            {
                Title = "Keyboard Shortcuts",
                Content = "File:\n" +
                          "  Ctrl+O - Open PDF\n" +
                          "  Ctrl+S - Save\n" +
                          "  Ctrl+Shift+S - Save As\n" +
                          "  Ctrl+W - Close Document\n\n" +
                          "Edit:\n" +
                          "  Ctrl+F - Find\n" +
                          "  F3 - Find Next\n" +
                          "  Shift+F3 - Find Previous\n" +
                          "  T - Toggle Text Selection Mode\n" +
                          "  R - Toggle Redaction Mode\n\n" +
                          "View:\n" +
                          "  Ctrl++ - Zoom In\n" +
                          "  Ctrl+- - Zoom Out\n" +
                          "  Ctrl+0 - Actual Size\n\n" +
                          "Navigation:\n" +
                          "  PgUp/PgDn - Previous/Next Page",
                CloseButtonText = "Close",
                DefaultButton = FluentAvalonia.UI.Controls.FAContentDialogButton.Close
            };

            await messageBox.ShowAsync();
        }
    }

    private void ShowDocumentation()
    {
        _logger.LogInformation("Documentation requested");

        try
        {
            var readmePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "README.md");

            if (System.IO.File.Exists(readmePath))
            {
                // Open README.md with default application
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = readmePath,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            else
            {
                // Fallback to GitHub repository
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://github.com/marctjones/pdfe",
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open documentation");
        }
    }

    private Avalonia.Controls.Window? GetMainWindow()
    {
        var lifetime = Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime;

        return lifetime?.MainWindow;
    }

    private IStorageProvider? GetStorageProvider()
    {
        return GetMainWindow()?.StorageProvider;
    }

    private async Task<IStorageFile?> ShowSaveRedactedFileDialog(Avalonia.Controls.Window mainWindow, string suggestedPath)
    {
        var storageProvider = mainWindow.StorageProvider;

        var options = new FilePickerSaveOptions
        {
            Title = $"Save Redacted PDF ({RedactionWorkflow.PendingCount} areas will be redacted)",
            DefaultExtension = "pdf",
            SuggestedFileName = System.IO.Path.GetFileName(suggestedPath),
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PDF Document")
                {
                    Patterns = new[] { "*.pdf" },
                    MimeTypes = new[] { "application/pdf" }
                }
            }
        };

        // Try to set the suggested directory
        try
        {
            var dir = System.IO.Path.GetDirectoryName(suggestedPath);
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
            {
                options.SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(dir);
            }
        }
        catch
        {
            // Ignore errors, will use default location
        }

        return await storageProvider.SaveFilePickerAsync(options);
    }

    // Recent Files Management

    private void LoadRecentFiles()
    {
        _logger.LogDebug("Loading recent files");

        try
        {
            // Use AppPaths for cross-platform correct paths (Issues #265, #266, #267)
            var recentFilesPath = AppPaths.RecentFilesPath;

            if (System.IO.File.Exists(recentFilesPath))
            {
                var lines = System.IO.File.ReadAllLines(recentFilesPath);
                foreach (var line in lines.Take(10)) // Keep max 10 recent files
                {
                    if (System.IO.File.Exists(line))
                    {
                        RecentFiles.Add(line);
                    }
                }

                this.RaisePropertyChanged(nameof(HasRecentFiles));
                this.RaisePropertyChanged(nameof(RecentFileMenuItems));
                _logger.LogInformation("Loaded {Count} recent files", RecentFiles.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading recent files");
        }
    }

    private void AddToRecentFiles(string filePath)
    {
        _logger.LogDebug("Adding to recent files: {FilePath}", filePath);

        try
        {
            // Remove if already exists
            if (RecentFiles.Contains(filePath))
            {
                RecentFiles.Remove(filePath);
            }

            // Add to beginning
            RecentFiles.Insert(0, filePath);

            // Keep max 10 files
            while (RecentFiles.Count > 10)
            {
                RecentFiles.RemoveAt(RecentFiles.Count - 1);
            }

            this.RaisePropertyChanged(nameof(HasRecentFiles));
            this.RaisePropertyChanged(nameof(RecentFileMenuItems));

            // Save to file
            SaveRecentFiles();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error adding to recent files");
        }
    }

    private void SaveRecentFiles()
    {
        try
        {
            // Use AppPaths for cross-platform correct paths (Issues #265, #266, #267)
            // AppPaths.DataDir ensures directory exists
            System.IO.File.WriteAllLines(AppPaths.RecentFilesPath, RecentFiles);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error saving recent files");
        }
    }

    /// <summary>
    /// Removes a file from the recent files list (Issue #25: handles deleted files).
    /// </summary>
    private void RemoveFromRecentFiles(string filePath)
    {
        try
        {
            if (RecentFiles.Contains(filePath))
            {
                RecentFiles.Remove(filePath);
                this.RaisePropertyChanged(nameof(HasRecentFiles));
                SaveRecentFiles();
                _logger.LogInformation("Removed deleted file from recent files: {FilePath}", filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error removing from recent files");
        }
    }

    // Zoom Level Persistence (Issue #32)

    private void LoadZoomPreference()
    {
        try
        {
            // Use AppPaths for cross-platform correct paths (Issues #265, #266, #267)
            var zoomFilePath = AppPaths.ZoomSettingsPath;

            if (System.IO.File.Exists(zoomFilePath))
            {
                var zoomStr = System.IO.File.ReadAllText(zoomFilePath).Trim();
                if (double.TryParse(zoomStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var savedZoom))
                {
                    // Validate range (25% to 500%)
                    if (savedZoom >= 0.25 && savedZoom <= 5.0)
                    {
                        _zoomLevel = savedZoom;
                        _logger.LogInformation("Loaded zoom preference: {Zoom:P0}", savedZoom);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading zoom preference");
        }
    }

    private void SaveZoomPreference()
    {
        try
        {
            // Use AppPaths for cross-platform correct paths (Issues #265, #266, #267)
            // AppPaths.ConfigDir ensures directory exists
            System.IO.File.WriteAllText(AppPaths.ZoomSettingsPath,
                ZoomLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error saving zoom preference");
        }
    }

    // OCR removed in the pure-Pdfe.Core migration. Reintroduce later
    // as a pdfe CLI subcommand if needed.

    // Signature Verification Command
    private async Task VerifySignaturesAsync()
    {
        await _signatureWorkflowService.VerifyAsync(_documentService.IsDocumentLoaded, _currentFilePath);
    }

    // Preferences Command
    private void ShowPreferences()
    {
        _logger.LogInformation("Show preferences dialog");

        var preferencesViewModel = new PreferencesViewModel();
        preferencesViewModel.LoadFromMainViewModel(this);

        var window = new Views.PreferencesWindow
        {
            DataContext = preferencesViewModel
        };

        // Get the main window
        var mainWindow = GetMainWindow();
        if (mainWindow != null)
        {
            window.ShowDialog(mainWindow).ContinueWith(task =>
            {
                if (preferencesViewModel.DialogResult)
                {
                    preferencesViewModel.SaveToMainViewModel(this);
                    _logger.LogInformation("Preferences saved");
                }
            });
        }
        else
        {
            _logger.LogWarning("Could not find main window to show preferences dialog");
        }
    }

    /// <summary>
    /// Restore document state (zoom level and last page index) from persisted settings.
    /// Called after a document is successfully loaded.
    /// </summary>
    private async Task RestoreDocumentStateAsync(string filePath)
    {
        try
        {
            var settings = Models.WindowSettings.Load();
            var docState = settings.DocumentStates.FirstOrDefault(d =>
                System.IO.Path.GetFullPath(d.FilePath) == System.IO.Path.GetFullPath(filePath));

            if (docState != null)
            {
                _logger.LogInformation("Restoring document state: ZoomLevel={Zoom}, LastPageIndex={Page}",
                    docState.ZoomLevel, docState.LastPageIndex);

                // Restore zoom level
                if (docState.ZoomLevel > 0 && docState.ZoomLevel <= 5.0) // Reasonable bounds
                {
                    _skipZoomSave = true;
                    ZoomLevel = docState.ZoomLevel;
                    _zoomFitMode = ZoomFitMode.Manual;
                    _skipZoomSave = false;
                    _logger.LogDebug("Zoom restored: {Zoom}", docState.ZoomLevel);
                }

                // Restore last page index
                if (docState.LastPageIndex >= 0 && docState.LastPageIndex < TotalPages)
                {
                    await GoToPageAsync(docState.LastPageIndex);
                    _logger.LogDebug("Page restored: {Page}", docState.LastPageIndex);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore document state for {FilePath}", filePath);
            // Don't fail document load if state restoration fails
        }
    }

    /// <summary>
    /// Save document state (zoom level and current page) to persistent settings.
    /// Called when the document is being closed.
    /// </summary>
    private void SaveDocumentState()
    {
        try
        {
            if (string.IsNullOrEmpty(_currentFilePath) || !_documentService.IsDocumentLoaded)
                return;

            var settings = Models.WindowSettings.Load();
            settings.UpdateDocumentState(_currentFilePath, ZoomLevel, CurrentPageIndex);
            settings.Save();
            _logger.LogDebug("Document state saved for {FilePath}: Zoom={Zoom}, Page={Page}",
                _currentFilePath, ZoomLevel, CurrentPageIndex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save document state");
        }
    }

    /// <summary>
    /// Converts an SKBitmap to an Avalonia.Media.Imaging.Bitmap.
    /// </summary>
    /// <param name="skBitmap">The SKBitmap to convert.</param>
    /// <returns>An Avalonia.Media.Imaging.Bitmap, or null if conversion fails.</returns>
    private Avalonia.Media.Imaging.Bitmap? ToAvaloniaBitmap(SKBitmap? skBitmap)
    {
        // Direct pixel copy via WriteableBitmap — replaces a per-render
        // PNG encode + decode round-trip that ate ~150-300ms on every
        // page render. See Pdfe.Avalonia.Imaging.SkiaInterop for the rationale.
        try
        {
            return Pdfe.Avalonia.Imaging.SkiaInterop.ToAvaloniaBitmap(skBitmap);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting SKBitmap to Avalonia.Media.Imaging.Bitmap");
            return null;
        }
    }
}

/// <summary>
/// Zoom-mode latching for the viewer. PDF readers traditionally let users
/// pick a fit mode (Width / Page) that survives window resizes — once the
/// user manually changes zoom (Ctrl++/-/0 or scroll-wheel zoom) we drop
/// to <see cref="Manual"/> and stop auto-recomputing.
/// </summary>
public enum ZoomFitMode
{
    Manual,
    FitWidth,
    FitPage,
}
