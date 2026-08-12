using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Excise.Avalonia.Controls;
using Excise.App.Models;
using Excise.Core.Document;
using Excise.App.Services;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
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

public partial class MainWindowViewModel : ViewModelBase
{
    internal const int DefaultViewerRenderDpi = 120;

    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly PdfDocumentService _documentService;
    private readonly PdfRenderService _renderService;
    private readonly RedactionService _redactionService;
    private readonly RedactedCopySafetyService _redactedCopySafetyService;
    private readonly PdfTextExtractionService _textExtractionService;
    private readonly SignatureVerificationWorkflowService _signatureWorkflowService;
    private readonly PageOrganizationWorkflowService _pageOrganizationWorkflow;
    private readonly AnnotationWorkflowService _annotationWorkflow;
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

    public DocumentOpenTiming? LastDocumentOpenTiming
    {
        get => _lastDocumentOpenTiming;
        private set => this.RaiseAndSetIfChanged(ref _lastDocumentOpenTiming, value);
    }

    private string _currentFilePath = string.Empty;
    private Bitmap? _currentPageImage;
    private PdfCoreDocument? _pdfCoreDocument;
    private int _currentPageIndex;
    private PdfViewMode _viewMode = PdfViewMode.Continuous;
    private bool _continuousScrollPreference = true;
    private Excise.Core.Text.ReadingOrderStrategy _readingOrderStrategy =
        Excise.Core.Text.ReadingOrderStrategy.ColumnAware;
    private Excise.Core.Text.WhitespaceMode _whitespaceMode =
        Excise.Core.Text.WhitespaceMode.Smart;
    private double _zoomLevel = 1.0;
    private bool _skipZoomSave; // Flag to skip zoom save during auto-reset
    private bool _isRedactionMode;
    private PdfPageRect? _currentRedactionPageArea;
    // Text selection is the resting affordance of the reading view (#831):
    // like every PDF reader, a drag selects text by default — no mode toggle
    // needed. Editing modes turn it off on entry and restore it on exit.
    private bool _isTextSelectionMode = true;
    private Rect _currentTextSelectionArea;
    private PdfPageRect? _currentTextSelectionPageArea;
    private string _selectedText = string.Empty;
    private ObservableCollection<string> _recentFiles = new();
    private double _viewportWidth = 800;
    private double _viewportHeight = 600;
    private ObservableCollection<PdfPageRect> _currentPageSearchHighlights = new();
    private int _renderCacheMax = 20;
    private string _operationStatus = string.Empty;
    private Services.ThumbnailCacheService? _thumbnailCache;
    internal Services.DocumentTextIndex? TextIndex;
    private System.Threading.CancellationTokenSource? _indexBuildCts;
    private const int SearchIndexBackgroundStartDelayMs = 750;
    private readonly Dictionary<int, Task> _thumbnailLoadTasks = new();
    private readonly object _thumbnailLoadLock = new();
    private long _thumbnailLoadGeneration;

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
    private DocumentOpenTiming? _lastDocumentOpenTiming;
    private long _renderVersion;
    private long _documentMutationVersion;

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
        _redactedCopySafetyService = new RedactedCopySafetyService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RedactedCopySafetyService>.Instance);
        _textExtractionService = new PdfTextExtractionService(Microsoft.Extensions.Logging.Abstractions.NullLogger<PdfTextExtractionService>.Instance);
        _searchService = new PdfSearchService(Microsoft.Extensions.Logging.Abstractions.NullLogger<PdfSearchService>.Instance);
        _filenameSuggestionService = new FilenameSuggestionService();
        _toastService = new ToastService();
        _dialogService = new NullUserDialogService();
        _signatureWorkflowService = CreateSignatureWorkflowService(
            new SignatureVerificationService(Microsoft.Extensions.Logging.Abstractions.NullLogger<SignatureVerificationService>.Instance),
            _dialogService,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SignatureVerificationWorkflowService>.Instance);
        _pageOrganizationWorkflow = new PageOrganizationWorkflowService(
            _documentService,
            _dialogService,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PageOrganizationWorkflowService>.Instance);
        _annotationWorkflow = new AnnotationWorkflowService(
            _documentService,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AnnotationWorkflowService>.Instance);

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
        SignatureVerificationWorkflowService? signatureWorkflowService = null,
        PageOrganizationWorkflowService? pageOrganizationWorkflow = null,
        AnnotationWorkflowService? annotationWorkflow = null,
        RedactedCopySafetyService? redactedCopySafetyService = null)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _documentService = documentService;
        _renderService = renderService;
        _redactionService = redactionService;
        _redactedCopySafetyService = redactedCopySafetyService ?? new RedactedCopySafetyService(
            loggerFactory.CreateLogger<RedactedCopySafetyService>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RedactedCopySafetyService>.Instance);
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
        _pageOrganizationWorkflow = pageOrganizationWorkflow ?? new PageOrganizationWorkflowService(
            documentService,
            _dialogService,
            loggerFactory.CreateLogger<PageOrganizationWorkflowService>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PageOrganizationWorkflowService>.Instance);
        _annotationWorkflow = annotationWorkflow ?? new AnnotationWorkflowService(
            documentService,
            loggerFactory.CreateLogger<AnnotationWorkflowService>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AnnotationWorkflowService>.Instance);

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

    public int SelectedPageCount => GetSelectedPageIndices().Count;
    public bool HasSelectedPages => SelectedPageCount > 0;
    public bool CanRemoveSelectedPages => HasSelectedPages && SelectedPageCount < TotalPages;
    public bool CanMoveSelectedPagesEarlier => GetSelectedPageIndices().Any(i => i > 0);
    public bool CanMoveSelectedPagesLater => GetSelectedPageIndices().Any(i => i < TotalPages - 1);
    public string PageSelectionSummary =>
        SelectedPageCount == 0
            ? "No pages selected"
            : $"{SelectedPageCount} selected";

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
    /// Bound to <see cref="global::Avalonia.Controls.TreeView.SelectedItem"/>. Setting
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
                // Text selection survives the switch to continuous (#815): it now
                // works in the reading view. The draw/edit modes still do not.
                if (_isRedactionMode) IsRedactionMode = false;
                if (_isFormAuthoringMode) IsFormAuthoringMode = false;
                if (_isTypewriterMode) IsTypewriterMode = false;
            }

            this.RaisePropertyChanged(nameof(IsContinuousView));
            this.RaisePropertyChanged(nameof(CurrentModeText));
        }
    }

    public bool IsContinuousView => ViewMode == PdfViewMode.Continuous;

    /// <summary>
    /// Reading-order strategy for text selection/copy (#774), bound two-way to
    /// the viewer control's <c>ReadingOrderStrategy</c>. Persisted with the
    /// window settings via <see cref="ApplyReadingOrderStrategyPreference"/>.
    /// Default is <see cref="Excise.Core.Text.ReadingOrderStrategy.ColumnAware"/>.
    /// </summary>
    public Excise.Core.Text.ReadingOrderStrategy ReadingOrderStrategy
    {
        get => _readingOrderStrategy;
        set => this.RaiseAndSetIfChanged(ref _readingOrderStrategy, value);
    }

    /// <summary>Apply a persisted reading-order strategy on startup (#774).</summary>
    public void ApplyReadingOrderStrategyPreference(Excise.Core.Text.ReadingOrderStrategy strategy)
    {
        ReadingOrderStrategy = strategy;
    }

    /// <summary>
    /// Copied-text whitespace mode, bound two-way to the viewer control's
    /// <c>WhitespaceMode</c>. Persisted with the window settings. Default is
    /// paragraph/list-aware <see cref="Excise.Core.Text.WhitespaceMode.Smart"/>.
    /// </summary>
    public Excise.Core.Text.WhitespaceMode WhitespaceMode
    {
        get => _whitespaceMode;
        set => this.RaiseAndSetIfChanged(ref _whitespaceMode, value);
    }

    /// <summary>Apply a persisted whitespace mode on startup.</summary>
    public void ApplyWhitespaceModePreference(Excise.Core.Text.WhitespaceMode mode)
    {
        WhitespaceMode = mode;
    }

    public bool ContinuousScrollPreference => _continuousScrollPreference;

    public void ApplyContinuousScrollPreference(bool enabled)
    {
        if (_continuousScrollPreference != enabled)
        {
            _continuousScrollPreference = enabled;
            this.RaisePropertyChanged(nameof(ContinuousScrollPreference));
        }

        ViewMode = enabled ? PdfViewMode.Continuous : PdfViewMode.SinglePage;
    }

    /// <summary>
    /// True while a draw/edit mode owns the viewport. These modes are single-page
    /// only, so each one forces <see cref="PdfViewMode.SinglePage"/> on entry.
    /// Text selection is excluded (#815): it now works in the continuous reading
    /// view, so it neither forces single-page nor blocks restoring continuous.
    /// </summary>
    private bool IsEditingModeActive =>
        _isRedactionMode || _isFormAuthoringMode || _isTypewriterMode || _isPathAnnotationMode;

    /// <summary>
    /// Re-applies the saved continuous-scroll preference once the last editing mode
    /// turns off. Without this the preference is a one-way valve: entering redaction
    /// (or select-text / forms / typewriter) forces single-page, and leaving it would
    /// strand the session in single-page for the rest of its life even though the
    /// user's saved preference — and the state we persist on close — still says
    /// continuous. Every editing-mode setter calls this on exit.
    /// </summary>
    private void RestoreViewModeFromPreference()
    {
        if (!_continuousScrollPreference || IsEditingModeActive)
            return;

        ViewMode = PdfViewMode.Continuous;
    }

    public long RenderVersion
    {
        get => _renderVersion;
        private set => this.RaiseAndSetIfChanged(ref _renderVersion, value);
    }

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
            ClearCurrentTextSelection();
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

    /// <summary>Link target shown while the pointer hovers a link, set via <see cref="SetHoveredLinkTarget"/> (#625).</summary>
    private string? _hoveredLinkTarget;

    /// <summary>
    /// Status bar text showing pending redaction count and file type.
    /// Updates dynamically as user marks/applies redactions. Link-hover
    /// target (#625) takes priority when present — it's transient,
    /// pointer-driven feedback the user is actively looking at, same as a
    /// browser's status-bar link preview.
    /// </summary>
    public string StatusBarText
    {
        get
        {
            if (!string.IsNullOrEmpty(_hoveredLinkTarget))
                return _hoveredLinkTarget;
            if (RedactionWorkflow.PendingRedactions.Count > 0)
                return $"{RedactionWorkflow.PendingRedactions.Count} areas marked";
            if (FileState.TypewriterEditsCount > 0)
                return $"{FileState.TypewriterEditsCount} typewriter edit(s) pending";
            if (FileState.FormFieldEditsCount > 0)
                return $"{FileState.FormFieldEditsCount} form edit(s) pending";
            if (FileState.AnnotationEditsCount > 0)
                return $"{FileState.AnnotationEditsCount} annotation edit(s) pending";
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
            else
            {
                RestoreViewModeFromPreference();
                // #831: selection is the resting mode — but only restore it when
                // we're truly returning to reading, NOT when this exit is part of
                // switching INTO another editing mode (which sets its flag first,
                // then turns this one off). Restoring then would cascade through
                // the selection setter and disable the mode being entered.
                if (!IsEditingModeActive) IsTextSelectionMode = true;
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
        get => CurrentRedactionPageArea is { } area
            ? ToAvaloniaRect(ToViewerRedactionArea(area))
            : default;
        set => CurrentRedactionPageArea = value.Width > 0 && value.Height > 0
            ? PdfPageRect.ViewerDips(
                Math.Max(CurrentPageIndex + 1, 1),
                value.X,
                value.Y,
                value.Width,
                value.Height,
                CurrentRedactionRenderDpi)
            : null;
    }

    public int CurrentRedactionRenderDpi
    {
        get => CurrentRedactionPageArea is { Space: PdfCoordinateSpace.ViewerDips } area
            ? (int)Math.Round(area.Dpi)
            : DefaultViewerRenderDpi;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Render DPI must be positive.");

            if (_currentRedactionPageArea is { Space: PdfCoordinateSpace.ViewerDips } area)
            {
                SetCurrentRedactionPageArea(
                    PdfPageRect.ViewerDips(
                        area.PageNumber,
                        area.X,
                        area.Y,
                        area.Width,
                        area.Height,
                        value),
                    notifyCompatibilityProperties: true);
            }
        }
    }

    public PdfPageRect? CurrentRedactionPageArea
    {
        get => _currentRedactionPageArea;
        set => SetCurrentRedactionPageArea(value, notifyCompatibilityProperties: true);
    }

    private void SetCurrentRedactionPageArea(PdfPageRect? area, bool notifyCompatibilityProperties)
    {
        this.RaiseAndSetIfChanged(ref _currentRedactionPageArea, area);

        if (!notifyCompatibilityProperties)
            return;

        this.RaisePropertyChanged(nameof(CurrentRedactionArea));
        this.RaisePropertyChanged(nameof(CurrentRedactionRenderDpi));
    }

    private PdfPageRect ToViewerRedactionArea(PdfPageRect area)
    {
        if (area.Space == PdfCoordinateSpace.ViewerDips &&
            Math.Abs(area.Dpi - DefaultViewerRenderDpi) < 0.000001)
        {
            return area;
        }

        if (_pdfCoreDocument == null ||
            area.PageNumber < 1 ||
            area.PageNumber > _pdfCoreDocument.PageCount)
        {
            return area;
        }

        return PdfCoordinateMapper.ToViewerDips(
            _pdfCoreDocument.GetPage(area.PageNumber),
            area,
            DefaultViewerRenderDpi);
    }

    private static Rect ToAvaloniaRect(PdfPageRect area) =>
        new(area.X, area.Y, area.Width, area.Height);

    public bool IsTextSelectionMode
    {
        get => _isTextSelectionMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _isTextSelectionMode, value);
            // Text selection does NOT change the view mode (#815): it works in
            // both single-page and the continuous reading view. Turning off the
            // draw/edit modes below may restore continuous via their own setters.
            // Turn off the draw/edit modes when entering text selection mode.
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

    public PdfPageRect? CurrentTextSelectionPageArea
    {
        get => _currentTextSelectionPageArea;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentTextSelectionPageArea, value);
            this.RaisePropertyChanged(nameof(HasTextSelection));
        }
    }

    public string SelectedText
    {
        get => _selectedText;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedText, value);
            this.RaisePropertyChanged(nameof(HasTextSelection));
        }
    }

    public bool HasTextSelection =>
        CurrentTextSelectionPageArea is { Width: > 0, Height: > 0 } &&
        !string.IsNullOrWhiteSpace(SelectedText);

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
        // The in-app selection itself stays available (it powers highlight
        // annotations and search); what /P bit 5 gates is putting the text
        // on the OS clipboard (#642).
        SelectedText = text;
        if (!EnsureDocumentPermission(p => p.CanCopy,
            "Copying selected text", "copying or extracting content (/P bit 5)"))
        {
            return;
        }

        await PublishToClipboardAndHistoryAsync(text);
    }

    public int RenderCacheMax
    {
        get => _renderCacheMax;
        set
        {
            this.RaiseAndSetIfChanged(ref _renderCacheMax, value);
            _renderService.MaxCacheEntries = Math.Max(1, value);
            this.RaisePropertyChanged(nameof(RenderCacheStats));
        }
    }

    public PdfRenderService.CacheStatistics RenderCacheStats => _renderService.GetCacheStats();

    internal bool AdjacentPagePrefetchEnabled { get; set; } = true;

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

    public ObservableCollection<global::Avalonia.Controls.MenuItem> RecentFileMenuItems
    {
        get
        {
            var items = new ObservableCollection<global::Avalonia.Controls.MenuItem>();

            if (RecentFiles.Count == 0)
            {
                // Show placeholder when no recent files
                var noFilesItem = new global::Avalonia.Controls.MenuItem
                {
                    Header = "No recent files",
                    IsEnabled = false
                };
                items.Add(noFilesItem);
                return items;
            }

            foreach (var filePath in RecentFiles)
            {
                var menuItem = new global::Avalonia.Controls.MenuItem
                {
                    Header = System.IO.Path.GetFileName(filePath), // Show filename only
                    Command = LoadRecentFileCommand,
                    CommandParameter = filePath
                };
                // Set tooltip to show full path
                global::Avalonia.Controls.ToolTip.SetTip(menuItem, filePath);
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

    // Search highlight rectangles for current page. Stored in PDF content coordinates;
    // PdfViewerControl converts them to viewer DIPs when drawing overlays.
    public ObservableCollection<PdfPageRect> CurrentPageSearchHighlights
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
        var openSw = Stopwatch.StartNew();
        long documentInstancesLoadedElapsedMs = 0;
        long firstPageVisibleElapsedMs = 0;
        long thumbnailPlaceholdersReadyElapsedMs = 0;
        long outlineReadyElapsedMs = 0;
        long searchIndexStartedElapsedMs = 0;

        try
        {
            _logger.LogInformation(">>> STEP 2: Clearing previous document state");
            // Clear ALL state from previous document before loading new one
            LastDocumentOpenTiming = null;
            CurrentRedactionArea = new Rect();
            ClearCurrentTextSelection();
            RedactionWorkflow.Reset();
            ClearPendingTypewriterText();
            ClearEditHistory();
            ClipboardHistory.Clear();
            ResetThumbnailLoadTracking();
            PageThumbnails.Clear();
            _renderService.ClearCache();
            this.RaisePropertyChanged(nameof(RenderCacheStats));

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
            _logger.LogInformation(">>> STEP 5: Loading Excise.Core document (parallel parse)");
            try
            {
                PdfCoreDocument = await LoadDocumentInstancesAsync(filePath, userPassword: null);
            }
            catch (Excise.Core.Parsing.PdfEncryptionNotSupportedException ex) when (IsPasswordVerificationFailure(ex))
            {
                // #643: a preserving save writes encrypted output, and flows
                // like "apply redactions → reload the redacted copy" land
                // here. Try the password the previous document was opened
                // with before bothering the user for it again.
                var rememberedPassword = _documentService.CurrentUserPassword;
                var openedWithRememberedPassword = false;
                if (!string.IsNullOrEmpty(rememberedPassword))
                {
                    try
                    {
                        PdfCoreDocument = await LoadDocumentInstancesAsync(filePath, rememberedPassword);
                        openedWithRememberedPassword = true;
                    }
                    catch (Excise.Core.Parsing.PdfEncryptionNotSupportedException ex2) when (IsPasswordVerificationFailure(ex2))
                    {
                        // Different document, different password — fall
                        // through to the prompt.
                    }
                }

                if (!openedWithRememberedPassword)
                {
                    OperationStatus = "Password required…";
                    var password = await _dialogService.PromptPasswordAsync(
                        "Password Required",
                        "Enter the user password for this PDF.");
                    if (password == null)
                        throw new Excise.Core.Parsing.PdfEncryptionNotSupportedException(
                            "Password is required to open this PDF.");

                    OperationStatus = "Opening PDF…";
                    PdfCoreDocument = await LoadDocumentInstancesAsync(filePath, password);
                }
            }
            _logger.LogInformation(">>> STEP 5: Both document instances loaded");
            documentInstancesLoadedElapsedMs = openSw.ElapsedMilliseconds;

            _logger.LogInformation(">>> STEP 6: Setting CurrentPageIndex = 0");
            CurrentPageIndex = 0;

            // The bound PdfViewerControl owns display rendering. At this point
            // it has enough state (Document + CurrentPage) to render page 1;
            // avoid the legacy VM render path, which produced an unbound
            // CurrentPageImage and duplicated raster work.
            _logger.LogInformation(">>> STEP 7: Current page render scheduled in viewer");
            firstPageVisibleElapsedMs = openSw.ElapsedMilliseconds;

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
            thumbnailPlaceholdersReadyElapsedMs = openSw.ElapsedMilliseconds;

            // Idle pre-warm (#689): populate the disk cache for every page in
            // the background so the first full sidebar scroll (and every
            // future open of this file) is all cache hits. First-open only —
            // the mutation path re-keys its cache per edit, and re-sweeping
            // every page after every redaction would be wasted work.
            QueueThumbnailPrewarm(_thumbnailCache);

            // Parse the document's table-of-contents outline (if any).
            // Cheap — just a tree walk over the catalog's /Outlines, no
            // text extraction needed. Populates the left-sidebar tree.
            try
            {
                var outline = Excise.Core.Document.PdfOutlineParser.Parse(PdfCoreDocument!);
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
            outlineReadyElapsedMs = openSw.ElapsedMilliseconds;

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
            StartSearchIndexBuild(TextIndex, indexCts, indexProgress);
            searchIndexStartedElapsedMs = openSw.ElapsedMilliseconds;

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

            if (OperationStatus == "Opening PDF…")
                OperationStatus = string.Empty;

            openSw.Stop();
            LastDocumentOpenTiming = new DocumentOpenTiming(
                filePath,
                TotalPages,
                documentInstancesLoadedElapsedMs,
                firstPageVisibleElapsedMs,
                thumbnailPlaceholdersReadyElapsedMs,
                outlineReadyElapsedMs,
                searchIndexStartedElapsedMs,
                openSw.ElapsedMilliseconds);
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
                _renderService.GetCacheStats(),
                _logger);
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
            if (IsPasswordVerificationFailure(ex) || ex.Message.Contains("owner password", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("password is required", StringComparison.OrdinalIgnoreCase))
            {
                userMessage = "This PDF requires a user password. The password was not provided, was rejected, or the file uses an unsupported owner-password-only mode.";
                _toastService.ShowError("Cannot Open PDF", "Password required or rejected.");
            }
            else if (ex.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Open the file ONCE and use the single instance for both the save path
    /// and the viewer (#917).
    ///
    /// This used to open the same file twice in parallel — a byte-backed
    /// document for saving and a file-backed one for the viewer — which cost
    /// far more than the second parse:
    ///
    ///   * every mutation had to be applied to both by hand, and forgetting
    ///     was invisible: a correct saved file and an unchanged screen (this
    ///     bit #912, and every row of #934 had to re-fix it);
    ///   * keeping them in sync meant serialising the whole document and
    ///     reparsing it after each mutation — that round trip IS #922's
    ///     1401ms, and it disappears when there is only one document;
    ///   * the viewer's copy was file-backed, so it held the file open and
    ///     saving over it failed on Windows (#926).
    ///
    /// The service's instance is byte-backed and deliberately does not hold
    /// the file open, so it is the one to keep. Ownership stays with the
    /// service — this ViewModel must never dispose what it returns.
    /// </summary>
    private async Task<PdfCoreDocument> LoadDocumentInstancesAsync(string filePath, string? userPassword)
    {
        await Task.Run(() => _documentService.LoadDocument(filePath, userPassword));
        return _documentService.GetCurrentDocument()
            ?? throw new InvalidOperationException(
                $"Document service reported no document after loading {filePath}.");
    }

    /// <summary>
    /// Dispose the viewer document only when it is NOT the service's instance.
    ///
    /// Since #917 they are normally the same object and the service owns it;
    /// disposing here would hand the rest of the app a disposed document. The
    /// check is not belt-and-braces — the typewriter save path still opens a
    /// separate instance transiently, so both cases are live.
    /// </summary>
    /// <summary>
    /// Test seam for #917's invariant: the viewer document and the save
    /// document must be the SAME instance. Exposed because reference identity
    /// is the only way to assert it, and it is the property that makes every
    /// hand-written mirror unnecessary.
    /// </summary>
    internal PdfCoreDocument? SaveDocumentForTests => _documentService.GetCurrentDocument();

    private void DisposeViewerDocumentIfNotShared()
    {
        var owned = _documentService.GetCurrentDocument();
        if (_pdfCoreDocument != null && !ReferenceEquals(owned, _pdfCoreDocument))
            _pdfCoreDocument.Dispose();
    }

    private static bool IsPasswordVerificationFailure(Exception ex)
        => ex.Message.Contains("password verification failed", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("requires a non-empty user password", StringComparison.OrdinalIgnoreCase);

    private async Task ShowErrorDialogAsync(string title, string message)
    {
        try
        {
            var mainWindow = global::Avalonia.Application.Current?.ApplicationLifetime is
                global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            if (mainWindow != null)
            {
                var dialog = new global::Avalonia.Controls.Window
                {
                    Title = title,
                    Width = 450,
                    Height = 200,
                    WindowStartupLocation = global::Avalonia.Controls.WindowStartupLocation.CenterOwner,
                    CanResize = false,
                    Content = new global::Avalonia.Controls.StackPanel
                    {
                        Margin = new global::Avalonia.Thickness(20),
                        Spacing = 15,
                        Children =
                        {
                            new global::Avalonia.Controls.TextBlock
                            {
                                Text = message,
                                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap
                            },
                            new global::Avalonia.Controls.Button
                            {
                                Content = "OK",
                                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                                Width = 80
                            }
                        }
                    }
                };

                // Wire up the OK button to close the dialog
                if (dialog.Content is global::Avalonia.Controls.StackPanel panel)
                {
                    var button = panel.Children.OfType<global::Avalonia.Controls.Button>().FirstOrDefault();
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

    // #638's "Encryption Will Be Removed" confirmation gate used to live
    // here. It is gone on purpose: since #643, every save path preserves the
    // source's encryption (same algorithm/permissions, same password) via
    // PdfDocumentService.GetReEncryptionOptions(), so there is no loss to
    // confirm. Dropping protection is only possible through the Security
    // dialog's explicit Remove Protection action (#641).

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
            SyncAllFormFieldValuesToServiceDocument();
            var document = _documentService.GetCurrentDocument();
            var flattenedTypewriter = document != null && ApplyPendingTypewriterText(document);

            _documentService.SaveDocument();
            if (flattenedTypewriter)
            {
                ClearPendingTypewriterText();
                if (!string.IsNullOrWhiteSpace(_currentFilePath))
                    await ReloadPdfCoreDocumentAfterSaveAsync(_currentFilePath);
            }
            // Saved edits are committed to the file; nothing before this point
            // remains reversible in-session (#782).
            ClearEditHistory();
            FileState.MarkSaved();
            this.RaisePropertyChanged(nameof(SaveButtonText));
            this.RaisePropertyChanged(nameof(StatusBarText));

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
            RequestPreserveReadingPosition(); // #846: snapshot reading position before the page count/order changes
            var removedIndex = CurrentPageIndex;
            var capturedPages = CapturePages(new[] { removedIndex });
            var result = await _pageOrganizationWorkflow.RemovePageAsync(removedIndex);
            if (!result.DidChange)
                return;

            MarkPageOrganizationChanged(removedPage: true);
            _history.Push("Remove page",
                () => ReinsertPagesAsync(capturedPages),
                () => RemovePagesInternalAsync(new[] { removedIndex }));

            if (result.CurrentPageIndex.HasValue)
            {
                CurrentPageIndex = result.CurrentPageIndex.Value;
                _logger.LogDebug("Adjusted current page index to {PageIndex}", CurrentPageIndex);
            }

            _logger.LogDebug("Reloading bound document and thumbnails after page removal");
            await RefreshAfterDocumentMutationAsync();

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

        var files = await PickPdfFilesAsync("Select PDF to Add Pages From", allowMultiple: false);
        if (files.Count == 0)
        {
            _logger.LogInformation("Add pages dialog cancelled");
            return;
        }

        await AddPagesFromFileAsync(files[0]);
    }

    public async Task AddPagesFromFileAsync(string sourcePdfPath)
        => await InsertPagesFromFileAsync(sourcePdfPath, TotalPages);

    private async Task InsertPagesBeforeCurrentAsync()
    {
        if (!_documentService.IsDocumentLoaded)
            return;

        var path = await PickPdfForPageInsertionAsync("Select PDF to Insert Before Current Page");
        if (!string.IsNullOrWhiteSpace(path))
            await InsertPagesFromFileAsync(path, CurrentPageIndex);
    }

    private async Task InsertPagesAfterCurrentAsync()
    {
        if (!_documentService.IsDocumentLoaded)
            return;

        var path = await PickPdfForPageInsertionAsync("Select PDF to Insert After Current Page");
        if (!string.IsNullOrWhiteSpace(path))
            await InsertPagesFromFileAsync(path, CurrentPageIndex + 1);
    }

    public async Task InsertPagesFromFileAsync(string sourcePdfPath, int insertAtIndex)
    {
        if (!_documentService.IsDocumentLoaded)
            return;

        try
        {
            var result = await _pageOrganizationWorkflow.InsertPagesFromFileAsync(sourcePdfPath, insertAtIndex);
            if (!result.DidChange)
                return;

            MarkPageOrganizationChanged();
            await RefreshAfterDocumentMutationAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inserting pages");
        }
    }

    private async Task CombineDocumentsAsync()
    {
        _logger.LogInformation("Combine documents command triggered");

        var sourcePaths = await PickPdfFilesAsync("Select PDFs to Combine", allowMultiple: true);
        if (sourcePaths.Count == 0)
        {
            _logger.LogInformation("Combine Documents dialog cancelled");
            return;
        }

        var outputPath = await PickSavePdfPathAsync("Save Combined PDF", "combined.pdf");
        if (string.IsNullOrWhiteSpace(outputPath))
            return;

        try
        {
            await _pageOrganizationWorkflow.MergeDocumentsAsync(sourcePaths, outputPath);
            _toastService.ShowSuccess($"Combined {sourcePaths.Count} document(s) into {Path.GetFileName(outputPath)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error combining documents");
            _toastService.ShowError("Failed to combine documents", ex.Message);
        }
    }

    private async Task SplitDocumentAsync()
    {
        _logger.LogInformation("Split document command triggered");

        if (!_documentService.IsDocumentLoaded)
        {
            _logger.LogWarning("Cannot split: No document loaded");
            return;
        }

        var response = await _dialogService.PromptTextAsync(
            "Split Document",
            "How should the document be split?\n\n" +
            "- A number (e.g. \"5\"): every N pages per file\n" +
            "- \"single\": one page per file\n" +
            "- \"bookmarks\": split at each top-level bookmark\n" +
            "- Comma-separated page numbers (e.g. \"1,5,10\"): start a new file at each",
            "1");

        if (string.IsNullOrWhiteSpace(response))
            return;

        response = response.Trim();

        SplitMode mode;
        int pagesPerChunk = 1;
        IReadOnlyList<int>? boundaries = null;

        if (string.Equals(response, "single", StringComparison.OrdinalIgnoreCase))
        {
            mode = SplitMode.SinglePages;
        }
        else if (string.Equals(response, "bookmarks", StringComparison.OrdinalIgnoreCase))
        {
            mode = SplitMode.Bookmarks;
        }
        else if (response.Contains(','))
        {
            var parsed = response
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var n) ? n - 1 : -1)
                .Where(n => n >= 0)
                .ToList();

            if (parsed.Count == 0)
            {
                await _dialogService.ShowMessageAsync("Split Document", $"Could not parse page numbers from \"{response}\".");
                return;
            }

            mode = SplitMode.PageBoundaries;
            boundaries = parsed;
        }
        else if (int.TryParse(response, out var everyN) && everyN > 0)
        {
            mode = SplitMode.EveryNPages;
            pagesPerChunk = everyN;
        }
        else
        {
            await _dialogService.ShowMessageAsync("Split Document", $"Could not understand \"{response}\".");
            return;
        }

        var folderPath = await PickFolderAsync("Select Folder for Split PDFs");
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            _logger.LogInformation("Split Document dialog cancelled");
            return;
        }

        try
        {
            var paths = await _pageOrganizationWorkflow.SplitDocumentAsync(folderPath, mode, pagesPerChunk, boundaries);
            _toastService.ShowSuccess($"Split into {paths.Count} file(s)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error splitting document");
            _toastService.ShowError("Failed to split document", ex.Message);
        }
    }

    private async Task ExtractCurrentPageAsync()
    {
        if (!_documentService.IsDocumentLoaded)
            return;

        var suggestedName = string.IsNullOrWhiteSpace(DocumentName)
            ? $"page-{DisplayPageNumber}.pdf"
            : $"{Path.GetFileNameWithoutExtension(DocumentName)}_page{DisplayPageNumber}.pdf";

        var path = await PickSavePdfPathAsync("Extract Current Page", suggestedName);
        if (!string.IsNullOrWhiteSpace(path))
            await ExtractPagesToFileAsync(path, new[] { CurrentPageIndex });
    }

    public async Task ExtractPagesToFileAsync(string outputPath, IEnumerable<int> pageIndices)
    {
        if (!_documentService.IsDocumentLoaded)
            return;

        try
        {
            await _pageOrganizationWorkflow.ExtractPagesToFileAsync(outputPath, pageIndices);
            _toastService.ShowSuccess("Page extracted");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting pages");
            _toastService.ShowError("Failed to extract pages", ex.Message);
        }
    }

    private async Task ExtractSelectedPagesAsync()
    {
        var selected = GetSelectedPageIndices();
        if (!_documentService.IsDocumentLoaded || selected.Count == 0)
            return;

        var suggestedName = string.IsNullOrWhiteSpace(DocumentName)
            ? "selected-pages.pdf"
            : $"{Path.GetFileNameWithoutExtension(DocumentName)}_selected_pages.pdf";

        var path = await PickSavePdfPathAsync("Extract Selected Pages", suggestedName);
        if (!string.IsNullOrWhiteSpace(path))
            await ExtractPagesToFileAsync(path, selected);
    }

    private async Task RemoveSelectedPagesAsync()
    {
        var selected = GetSelectedPageIndices();
        if (!_documentService.IsDocumentLoaded || selected.Count == 0 || selected.Count >= TotalPages)
            return;

        try
        {
            RequestPreserveReadingPosition(); // #846
            var removedIndices = selected.OrderBy(i => i).ToList();
            var capturedPages = CapturePages(removedIndices);
            var result = await _pageOrganizationWorkflow.RemovePagesAsync(selected, CurrentPageIndex);
            if (!result.DidChange)
                return;

            MarkPageOrganizationChanged(removedPage: true, removedPageCount: selected.Count);

            if (result.CurrentPageIndex.HasValue)
                CurrentPageIndex = result.CurrentPageIndex.Value;

            await RefreshAfterDocumentMutationAsync();
            _history.Push("Remove pages",
                () => ReinsertPagesAsync(capturedPages),
                () => RemovePagesInternalAsync(removedIndices));
            _toastService.ShowSuccess($"{selected.Count} page(s) removed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing selected pages");
            _toastService.ShowError("Failed to remove selected pages", ex.Message);
        }
    }

    private async Task MoveCurrentPageEarlierAsync()
    {
        if (CurrentPageIndex <= 0)
            return;

        await MoveCurrentPageAsync(CurrentPageIndex - 1);
    }

    private async Task MoveCurrentPageLaterAsync()
    {
        if (CurrentPageIndex >= TotalPages - 1)
            return;

        await MoveCurrentPageAsync(CurrentPageIndex + 1);
    }

    public async Task MoveCurrentPageAsync(int toIndex)
        => await MovePageAsync(CurrentPageIndex, toIndex);

    public async Task MovePageAsync(int fromIndex, int toIndex)
    {
        if (!_documentService.IsDocumentLoaded)
            return;
        if (fromIndex < 0 || fromIndex >= TotalPages || toIndex < 0 || toIndex >= TotalPages || fromIndex == toIndex)
            return;

        try
        {
            RequestPreserveReadingPosition(); // #846
            var newCurrentPageIndex = RemapCurrentPageAfterSingleMove(CurrentPageIndex, fromIndex, toIndex);
            var result = await _pageOrganizationWorkflow.MovePageAsync(fromIndex, toIndex);
            if (!result.DidChange)
                return;

            CurrentPageIndex = newCurrentPageIndex;
            MarkPageOrganizationChanged();
            await RefreshAfterDocumentMutationAsync();
            _history.Push("Reorder page",
                () => MovePageInternalAsync(toIndex, fromIndex),
                () => MovePageInternalAsync(fromIndex, toIndex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error moving page");
            _toastService.ShowError("Failed to move page", ex.Message);
        }
    }

    private static int RemapCurrentPageAfterSingleMove(int currentPageIndex, int fromIndex, int toIndex)
    {
        if (currentPageIndex == fromIndex)
            return toIndex;
        if (fromIndex < toIndex && currentPageIndex > fromIndex && currentPageIndex <= toIndex)
            return currentPageIndex - 1;
        if (fromIndex > toIndex && currentPageIndex >= toIndex && currentPageIndex < fromIndex)
            return currentPageIndex + 1;
        return currentPageIndex;
    }

    public async Task MoveSelectedPagesAsync(int delta)
    {
        var selected = GetSelectedPageIndices();
        if (!_documentService.IsDocumentLoaded || selected.Count == 0)
            return;

        try
        {
            RequestPreserveReadingPosition(); // #846
            var result = await _pageOrganizationWorkflow.MovePagesAsync(selected, delta, CurrentPageIndex);
            if (!result.DidChange)
                return;

            if (result.CurrentPageIndex.HasValue)
                CurrentPageIndex = result.CurrentPageIndex.Value;

            MarkPageOrganizationChanged();
            await RefreshAfterDocumentMutationAsync();
            RestoreSelectedPages(result.SelectedPageIndices);

            var movedFrom = selected;
            var movedTo = result.SelectedPageIndices;
            _history.Push("Reorder pages",
                () => MoveSelectedPagesInternalAsync(movedTo, -delta),
                () => MoveSelectedPagesInternalAsync(movedFrom, delta));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error moving selected pages");
            _toastService.ShowError("Failed to move selected pages", ex.Message);
        }
    }

    private async Task<string?> PickPdfForPageInsertionAsync(string title)
    {
        var files = await PickPdfFilesAsync(title, allowMultiple: false);
        return files.Count == 0 ? null : files[0];
    }

    private void MarkPageOrganizationChanged(bool removedPage = false, int removedPageCount = 1)
    {
        if (removedPage)
            FileState.RemovedPagesCount += Math.Max(1, removedPageCount);
        else
            FileState.PageEditsCount++;

        this.RaisePropertyChanged(nameof(SaveButtonText));
        this.RaisePropertyChanged(nameof(StatusBarText));
    }

    private async Task RefreshAfterDocumentMutationAsync()
    {
        await ReloadPdfCoreDocumentFromCurrentDocumentAsync();
        // #846: a rotate can change which page is WIDEST (portrait->landscape),
        // so a latched fit is now stale — the rotated page overflows the viewport,
        // a horizontal scrollbar appears, and mixed-width pages (each centered
        // within the now-wider stack) shift horizontally as the reader scrolls
        // past them ("the center of the content shifts on the visible page").
        // Re-fit against the new widest page (#847) so every page fits and centres.
        ReapplyFitModeIfNeeded();
        await LoadPageThumbnailsAsync();
        this.RaisePropertyChanged(nameof(TotalPages));
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(StatusBarText));
    }

    /// <summary>
    /// Re-point the viewer at the current document and invalidate everything
    /// derived from it (caches, thumbnails, the text index).
    ///
    /// This USED to serialise the whole document with `SaveToBytes()` and
    /// reparse it, on every mutation including undo — that round trip is
    /// #922's 1401ms, and it existed only because the viewer held a SECOND
    /// document that went stale whenever the save document changed (#917).
    ///
    /// With one document there is nothing to re-sync: the object the viewer
    /// renders from is the object the mutation was applied to. What still has
    /// to happen is the invalidation below — rendered pages, thumbnails and
    /// the search index are all derived state and are still stale.
    /// </summary>
    private Task ReloadPdfCoreDocumentFromCurrentDocumentAsync()
    {
        var current = _documentService.GetCurrentDocument();
        if (current == null)
            return Task.CompletedTask;

        // Normally a no-op (they are the same instance). It is a real
        // re-point only on the paths that still hand the viewer a separate
        // document, and those dispose the old one here rather than leak it.
        if (!ReferenceEquals(current, _pdfCoreDocument))
        {
            DisposeViewerDocumentIfNotShared();
            PdfCoreDocument = current;
        }

        // THE SIGNAL, which used to be a side effect of the reparse.
        //
        // The viewer rebuilds its continuous page layout from
        // `DocumentProperty.Changed`, and an Avalonia styled property only
        // raises that when the VALUE changes. The old serialize-and-reparse
        // handed it a NEW PdfDocument instance on every mutation, so the
        // rebuild came for free — the 1401ms round trip was load-bearing, just
        // not for the reason it looked like. With one document the reference
        // never changes and the continuous view would keep the pre-mutation
        // page order (caught by ContinuousRotateReadingAnchorTests, the single
        // failure in a 1326-test run).
        //
        // A RenderVersion bump is the wrong instrument here: it also disposes
        // the page bitmap the viewer is currently showing and re-renders
        // asynchronously, which leaves layout touching a disposed bitmap. Page
        // CONTENT did not change — only the page order — so ask for the layout
        // rebuild alone.
        DocumentStructureChanged?.Invoke(this, EventArgs.Empty);

        CurrentPageIndex = Math.Clamp(CurrentPageIndex, 0, Math.Max(0, _documentService.PageCount - 1));
        _renderService.ClearCache();
        var mutationVersion = System.Threading.Interlocked.Increment(ref _documentMutationVersion);
        ResetThumbnailLoadTracking();

        _thumbnailCache?.Dispose();
        _thumbnailCache = !string.IsNullOrWhiteSpace(_currentFilePath)
            ? new Services.ThumbnailCacheService(
                _currentFilePath,
                PdfCoreDocument!,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                cacheSalt: $"memory-version-{mutationVersion}")
            : null;

        _indexBuildCts?.Cancel();
        _indexBuildCts = new System.Threading.CancellationTokenSource();
        TextIndex = new Services.DocumentTextIndex(
            PdfCoreDocument!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        StartSearchIndexBuild(TextIndex, _indexBuildCts);

        this.RaisePropertyChanged(nameof(CurrentPage));
        this.RaisePropertyChanged(nameof(CurrentPageFormFields));
        return Task.CompletedTask;
    }

    private void StartSearchIndexBuild(
        Services.DocumentTextIndex index,
        System.Threading.CancellationTokenSource indexCts,
        IProgress<(int Done, int Total)>? progress = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SearchIndexBackgroundStartDelayMs, indexCts.Token)
                    .ConfigureAwait(false);
                await index.BuildAsync(progress, indexCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the document changes before the idle index starts.
            }
        }, CancellationToken.None);
    }

    private void RequestViewerRenderRefresh()
    {
        RenderVersion++;
    }

    private void ToggleTextSelectionMode()
    {
        _logger.LogInformation("Toggle text selection mode. Current: {Current}", IsTextSelectionMode);
        IsTextSelectionMode = !IsTextSelectionMode;

        if (!IsTextSelectionMode)
        {
            // Clear selection when exiting mode
            ClearCurrentTextSelection();
        }
    }

    private void ToggleContinuousView()
    {
        ApplyContinuousScrollPreference(!IsContinuousView);
    }

    private async Task CopyTextAsync()
    {
        _logger.LogInformation("Copy text command triggered");

        if (!_documentService.IsDocumentLoaded || string.IsNullOrEmpty(_currentFilePath))
        {
            _logger.LogWarning("Cannot copy text: No document loaded");
            return;
        }

        // #642: user-initiated copy is gated on /P bit 5. Internal
        // extraction (search, accessibility tree) is deliberately not.
        if (!EnsureDocumentPermission(p => p.CanCopy,
            "Copying text", "copying or extracting content (/P bit 5)"))
        {
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
            var topLevel = global::Avalonia.Application.Current?.ApplicationLifetime is
                global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
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

    /// <summary>
    /// Set an explicit zoom level through the same path every user-initiated
    /// zoom takes: latch <see cref="ZoomFitMode.Manual"/> so a latched
    /// auto-fit (the default is FitWidth) cannot asynchronously re-apply
    /// over it on the next viewport change. Tests that assign
    /// <see cref="ZoomLevel"/> directly bypass the latch and race the refit.
    /// </summary>
    internal void SetManualZoom(double zoom)
    {
        _zoomFitMode = ZoomFitMode.Manual;
        ZoomLevel = Math.Clamp(zoom, 0.25, 5.0);
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
        if (TryGetMaxPageDimensionsInViewerDips(out var pageW, out _) &&
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
        if (TryGetMaxPageDimensionsInViewerDips(out var pageW, out var pageH) &&
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
    /// Page dimensions in DISPLAYED dips at zoom 1.0. Since the #693 fix,
    /// both view modes show a page at pt × 96/72 × zoom on screen — the
    /// continuous slots lay out at 96-dpi dips, and the single-page
    /// ZoomHost applies a 96/renderDpi correction to its 120-dpi layout.
    /// Fit-width/fit-page therefore compute zoom against 96-dpi dips;
    /// using the 120-dpi internal size here made continuous fit-width
    /// silently underfill the viewport by 20%. Reads page size from the
    /// parsed PdfCoreDocument so we don't depend on the legacy
    /// <c>_currentPageImage</c> being populated.
    /// </summary>
    private bool TryGetMaxPageDimensionsInViewerDips(out double widthDip, out double heightDip)
    {
        // Largest page width/height across the WHOLE document (rotation-aware via
        // VisualWidth/VisualHeight). Fit-Width / Fit-Page use the MAX, not the
        // current page: the continuous view shares one zoom across all pages and
        // centres each page horizontally, so fitting only the current page left
        // any wider page (e.g. a landscape/rotated page in an otherwise portrait
        // document) overflowing the viewport and shifted off-centre. Fitting the
        // widest page makes every page fit and centre consistently (#847).
        widthDip = 0; heightDip = 0;
        var doc = PdfCoreDocument;
        if (doc == null || doc.PageCount < 1) return false;
        for (int pageNumber = 1; pageNumber <= doc.PageCount; pageNumber++)
        {
            var page = doc.GetPage(pageNumber);
            var viewerRect = PdfCoordinateMapper.ToViewerDips(
                page,
                PdfPageRect.VisualPoints(pageNumber, 0, 0, page.VisualWidth, page.VisualHeight),
                96);
            if (viewerRect.Width > widthDip) widthDip = viewerRect.Width;
            if (viewerRect.Height > heightDip) heightDip = viewerRect.Height;
        }
        return widthDip > 0 && heightDip > 0;
    }

    private Task NextPageAsync()
    {
        if (CurrentPageIndex < TotalPages - 1)
        {
            CurrentPageIndex++;
        }

        return Task.CompletedTask;
    }

    private Task PreviousPageAsync()
    {
        if (CurrentPageIndex > 0)
        {
            CurrentPageIndex--;
        }

        return Task.CompletedTask;
    }

    private Task GoToPageAsync(int pageIndex)
    {
        _logger.LogInformation("Navigating to page {PageIndex}", pageIndex);

        if (pageIndex >= 0 && pageIndex < TotalPages && pageIndex != CurrentPageIndex)
        {
            CurrentPageIndex = pageIndex;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Raised by a structural page mutation, BEFORE the document is reloaded, so
    /// the continuous view can snapshot the reader's position (page + intra-page
    /// fraction) and restore it after the rebuild rather than jumping to the top of
    /// the page (#846). Opening/closing a document does NOT raise it — those
    /// intentionally reset the reading position. Raised by rotate, remove, and
    /// move; page identity across remove/move is handled by anchoring to
    /// CurrentPage (which the VM already remaps for the reader's content).
    /// </summary>
    public event EventHandler? PreserveReadingPositionRequested;

    /// <summary>
    /// Raised when the document's page structure changed but the document
    /// INSTANCE did not (#917). The viewer rebuilds its continuous layout from
    /// the Document property changing, and an Avalonia styled property does not
    /// raise that for an unchanged reference — so with one document this event
    /// is the only thing that tells it to re-lay-out.
    /// </summary>
    public event EventHandler? DocumentStructureChanged;

    private void RequestPreserveReadingPosition() =>
        PreserveReadingPositionRequested?.Invoke(this, EventArgs.Empty);

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
            var rotatedIndex = CurrentPageIndex;
            _documentService.RotatePageLeft(rotatedIndex);
            MarkPageOrganizationChanged();
            _history.Push("Rotate page left",
                () => ApplyPageRotationAsync(rotatedIndex, 90),
                () => ApplyPageRotationAsync(rotatedIndex, 270));
            _logger.LogInformation("Page {PageIndex} rotated left successfully", rotatedIndex);

            RequestPreserveReadingPosition();
            await RefreshAfterDocumentMutationAsync();
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
            var rotatedIndex = CurrentPageIndex;
            _documentService.RotatePageRight(rotatedIndex);
            MarkPageOrganizationChanged();
            _history.Push("Rotate page right",
                () => ApplyPageRotationAsync(rotatedIndex, 270),
                () => ApplyPageRotationAsync(rotatedIndex, 90));
            _logger.LogInformation("Page {PageIndex} rotated right successfully", rotatedIndex);

            RequestPreserveReadingPosition();
            await RefreshAfterDocumentMutationAsync();
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
            var rotatedIndex = CurrentPageIndex;
            _documentService.RotatePage180(rotatedIndex);
            MarkPageOrganizationChanged();
            _history.Push("Rotate page 180°",
                () => ApplyPageRotationAsync(rotatedIndex, 180),
                () => ApplyPageRotationAsync(rotatedIndex, 180));
            _logger.LogInformation("Page {PageIndex} rotated 180 degrees successfully", rotatedIndex);

            RequestPreserveReadingPosition();
            await RefreshAfterDocumentMutationAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rotating page 180 degrees");
        }
    }

    private void UpdateThumbnailSelection()
    {
        foreach (var thumbnail in PageThumbnails)
        {
            thumbnail.IsSelected = (thumbnail.PageIndex == CurrentPageIndex);
        }
    }

    public void MarkPageForOperation(int pageIndex, bool isSelected)
    {
        if (pageIndex < 0 || pageIndex >= PageThumbnails.Count)
            return;

        PageThumbnails[pageIndex].IsMarkedForPageOperation = isSelected;
    }

    private IReadOnlyList<int> GetSelectedPageIndices() =>
        PageThumbnails
            .Where(t => t.IsMarkedForPageOperation)
            .Select(t => t.PageIndex)
            .OrderBy(i => i)
            .ToList();

    private void ClearSelectedPages()
    {
        foreach (var thumbnail in PageThumbnails)
            thumbnail.IsMarkedForPageOperation = false;

        RaiseSelectedPagePropertiesChanged();
    }

    private void RestoreSelectedPages(IEnumerable<int> pageIndices)
    {
        var selected = pageIndices.ToHashSet();
        foreach (var thumbnail in PageThumbnails)
            thumbnail.IsMarkedForPageOperation = selected.Contains(thumbnail.PageIndex);

        RaiseSelectedPagePropertiesChanged();
    }

    private void AttachPageSelectionTracking(PageThumbnail thumbnail)
    {
        thumbnail.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PageThumbnail.IsMarkedForPageOperation))
                RaiseSelectedPagePropertiesChanged();
        };
    }

    private void RaiseSelectedPagePropertiesChanged()
    {
        this.RaisePropertyChanged(nameof(SelectedPageCount));
        this.RaisePropertyChanged(nameof(HasSelectedPages));
        this.RaisePropertyChanged(nameof(CanRemoveSelectedPages));
        this.RaisePropertyChanged(nameof(CanMoveSelectedPagesEarlier));
        this.RaisePropertyChanged(nameof(CanMoveSelectedPagesLater));
        this.RaisePropertyChanged(nameof(PageSelectionSummary));
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
            {
                var thumbnail = new PageThumbnail { PageNumber = i + 1, PageIndex = i };
                AttachPageSelectionTracking(thumbnail);
                PageThumbnails.Add(thumbnail);
            }
            UpdateThumbnailSelection();
            RaiseSelectedPagePropertiesChanged();
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

    private void ResetThumbnailLoadTracking()
    {
        System.Threading.Interlocked.Increment(ref _thumbnailLoadGeneration);
        lock (_thumbnailLoadLock)
        {
            _thumbnailLoadTasks.Clear();
        }
        // Stop viewport-window work targeting the outgoing document/state
        // (prefetch chain, idle pre-warm, visible-set tracking — #687/#688/#689).
        CancelThumbnailWindowWork();
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
        var thumbnailCache = _thumbnailCache;
        if (thumbnailCache == null) return; // no doc loaded yet
        if (PageThumbnails[pageIndex].ThumbnailImage != null) return; // already loaded

        var generation = System.Threading.Volatile.Read(ref _thumbnailLoadGeneration);
        Task loadTask;
        lock (_thumbnailLoadLock)
        {
            if (PageThumbnails[pageIndex].ThumbnailImage != null) return;
            if (!_thumbnailLoadTasks.TryGetValue(pageIndex, out loadTask!))
            {
                loadTask = LoadThumbnailCoreAsync(pageIndex, generation, thumbnailCache, cancellationToken);
                _thumbnailLoadTasks[pageIndex] = loadTask;
                _ = loadTask.ContinueWith(
                    _ =>
                    {
                        lock (_thumbnailLoadLock)
                        {
                            if (_thumbnailLoadTasks.TryGetValue(pageIndex, out var current) &&
                                ReferenceEquals(current, loadTask))
                            {
                                _thumbnailLoadTasks.Remove(pageIndex);
                            }
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        try
        {
            await loadTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected when a viewport notification is canceled because the
            // item scrolled away or the document changed.
        }
    }

    private async Task LoadThumbnailCoreAsync(
        int pageIndex,
        long generation,
        Services.ThumbnailCacheService thumbnailCache,
        System.Threading.CancellationToken cancellationToken)
    {
        try
        {
            if (pageIndex < 0 || pageIndex >= PageThumbnails.Count) return;
            var thumb = PageThumbnails[pageIndex];
            if (thumb.ThumbnailImage != null) return;

            using var sk = await thumbnailCache.GetThumbnailAsync(pageIndex, cancellationToken);
            if (sk == null) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                if (generation != System.Threading.Volatile.Read(ref _thumbnailLoadGeneration)) return;
                if (pageIndex < 0 || pageIndex >= PageThumbnails.Count) return;
                if (thumb.ThumbnailImage != null) return;

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

        // Route through the shared picker helper rather than reaching for the
        // storage provider inline. It is behaviourally identical (same title,
        // extension, file-type patterns) but honours PickSavePdfPathOverride,
        // the #816 test seam that Combine/Extract already use.
        //
        // Without this, SaveAsCommand — the Save As a user actually clicks —
        // was unreachable from a headless test: GetStorageProvider() returns
        // null there, so the command returned having written nothing. Every GUI
        // test that wanted to save therefore called SaveFileAsAsync(path)
        // directly, which cannot catch the command being mis-wired.
        var filePath = await PickSavePdfPathAsync(
            "Save PDF As",
            string.IsNullOrWhiteSpace(DocumentName) ? "document.pdf" : DocumentName);

        if (string.IsNullOrWhiteSpace(filePath))
        {
            _logger.LogInformation("Save As dialog cancelled or unavailable");
            return;
        }

        await SaveFileAsAsync(filePath);
    }

    public async Task SaveFileAsAsync(string filePath)
    {
        _logger.LogInformation("Saving document to: {FilePath}", filePath);

        try
        {
            SyncAllFormFieldValuesToServiceDocument();
            var document = _documentService.GetCurrentDocument();
            var flattenedTypewriter = document != null && ApplyPendingTypewriterText(document);

            _documentService.SaveDocument(filePath);
            _currentFilePath = filePath;
            FileState.UpdateCurrentPath(filePath);
            if (flattenedTypewriter)
                ClearPendingTypewriterText();
            ClearEditHistory();
            FileState.MarkSaved();
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
            ResetThumbnailLoadTracking();
            PageThumbnails.Clear();
            _renderService.ClearCache();

            // Clear redaction state (FIX: These were persisting!)
            CurrentRedactionArea = new Rect();
            ClearCurrentTextSelection();
            RedactionWorkflow.Reset();
            ClearPendingTypewriterText();
            ClearEditHistory();
            ClipboardHistory.Clear();

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
            this.RaisePropertyChanged(nameof(CurrentTextSelectionPageArea));
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

        var lifetime = global::Avalonia.Application.Current?.ApplicationLifetime
            as global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;

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

        if (!EnsureDocumentPermission(p => p.CanCopy,
            "Exporting the page as an image", "copying or extracting content (/P bit 5)"))
        {
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

        // Public entry point (also scripting-reachable) — gate here too so
        // no caller path bypasses the /P bit 5 check (#642).
        if (!EnsureDocumentPermission(p => p.CanCopy,
            "Exporting the page as an image", "copying or extracting content (/P bit 5)"))
        {
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

        if (!EnsureDocumentPermission(p => p.CanCopy,
            "Exporting pages as images", "copying or extracting content (/P bit 5)"))
        {
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

        // Intentionally not implemented — see #621. Avalonia ships no print API,
        // and a real cross-platform pipeline (CUPS on macOS/Linux,
        // System.Drawing.Printing on Windows, plus a print-options dialog) is a
        // lot of platform-specific surface to build and maintain for a workflow
        // most users reach a dedicated PDF viewer for, not an editor. This is a
        // permanent decision, not a "coming soon" placeholder — say so plainly.
        const string message = "excise doesn't print directly — this is a deliberate choice, not a missing feature (see #621). " +
            "Use Export Current Page or Export All Pages as Images from the Document menu, then print the image from your OS's own viewer.";
        _logger.LogInformation("Print command: {Message}", message);
        await _dialogService.ShowMessageAsync("Print", message);
    }

    /// <summary>
    /// http/https/mailto schemes excise will navigate to after confirmation
    /// (#625). Kept in sync with <c>PdfLinkParser.AllowedUriSchemes</c> —
    /// that gate decides what reaches this method at all, this one is
    /// defense-in-depth: a link-click handler that trusts a single
    /// upstream filter for something security-relevant is exactly the
    /// pattern this codebase avoids everywhere else (see CLAUDE.md's
    /// no-self-oracle / defense-in-depth threads on the redaction path).
    /// </summary>
    private static readonly HashSet<string> AllowedExternalLinkSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" };

    /// <summary>
    /// External (http/https/mailto) link click (#625). PDFs are a phishing
    /// vector — a reader that opens arbitrary URLs on click without showing
    /// them first is a liability, so this always confirms with the actual
    /// target URL visible before navigating, and never opens anything if the
    /// scheme isn't allowlisted (should be unreachable given the parser
    /// already filtered it, but a click handler for a security-sensitive
    /// action doesn't get to assume its only caller is trustworthy).
    /// </summary>
    private async Task OpenExternalLinkAsync(string uri)
    {
        _logger.LogInformation("External link clicked: {Uri}", uri);

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
            !AllowedExternalLinkSchemes.Contains(parsed.Scheme))
        {
            _logger.LogWarning("Refusing external link with disallowed/malformed scheme: {Uri}", uri);
            await _dialogService.ShowMessageAsync(
                "Link Blocked",
                $"excise won't open this link — its scheme isn't one of the ones considered safe to navigate to automatically (http, https, mailto):\n\n{uri}");
            return;
        }

        var confirmed = await _dialogService.ShowConfirmAsync(
            "Open Link?",
            $"This will open the following link in your default browser:\n\n{uri}\n\n" +
            "Only continue if you trust this destination — PDFs are a common phishing vector.");
        if (!confirmed)
        {
            _logger.LogInformation("User declined to open external link: {Uri}", uri);
            return;
        }

        Services.UrlOpener.Open(uri);
    }

    /// <summary>
    /// Click on a link excise refuses to run (#625) — /Launch (launches an
    /// external app/file), /GoToE (embedded-file destination), /GoToR
    /// (remote-file destination), or a URI action with a disallowed scheme.
    /// All are malware/exfiltration vectors PDF readers have historically
    /// been abused through; refusing with a clear message (instead of
    /// silently doing nothing, the pre-#625 behavior) is the point.
    /// </summary>
    private async Task ShowDangerousLinkRefusalAsync(string actionType)
    {
        _logger.LogWarning("Refused dangerous link action: {ActionType}", actionType);
        var reason = actionType switch
        {
            "Launch" => "it launches an external application or file",
            "GoToE" => "it navigates into an embedded file",
            "GoToR" => "it navigates into a remote file",
            _ when actionType.StartsWith("URI:", StringComparison.Ordinal) =>
                $"its link scheme ('{actionType["URI:".Length..]}') isn't one excise considers safe to open automatically",
            _ => "it's a link action type excise doesn't run automatically",
        };
        await _dialogService.ShowMessageAsync(
            "Link Blocked",
            $"excise blocked this link because {reason}. This kind of action is a common malware vector in PDFs.");
    }

    /// <summary>Status-bar hover feedback for the link under the pointer, or null when not hovering one (#625).</summary>
    public void SetHoveredLinkTarget(string? target)
    {
        if (_hoveredLinkTarget == target) return;
        _hoveredLinkTarget = target;
        this.RaisePropertyChanged(nameof(StatusBarText));
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

    /// <summary>
    /// Seam for #816: the real path calls <c>FAContentDialog.ShowAsync()</c>,
    /// which is not a <see cref="global::Avalonia.Controls.Window"/> (it
    /// renders into an overlay layer rather than
    /// <c>Window.OwnedWindows</c>) and awaits until the user dismisses it —
    /// there is no way for a headless test to observe or close it without
    /// driving overlay/adorner internals that no other test in this suite
    /// touches. Test-settable so <see cref="ShowShortcutsCommand"/> can be
    /// executed for real and the constructed dialog inspected without
    /// actually presenting/blocking on the overlay. Defaults to the real
    /// show so production behaviour is unchanged.
    /// </summary>
    internal Action<FluentAvalonia.UI.Controls.FAContentDialog> KeyboardShortcutsDialogRequested { get; set; } =
        dialog => _ = dialog.ShowAsync();

    private void ShowKeyboardShortcuts()
    {
        _logger.LogInformation("Keyboard shortcuts dialog requested");

        var window = GetMainWindow();
        if (window == null) return;

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

        KeyboardShortcutsDialogRequested(messageBox);
    }

    /// <summary>
    /// Seam for #816: the real path shells out to the OS default handler
    /// (via <see cref="Services.UrlOpener"/>), which is not an observable
    /// effect a headless test can assert on without actually launching a
    /// real external app/browser. Test-settable so
    /// <see cref="ShowDocumentationCommand"/> can be executed for real and
    /// the open request asserted without that side effect. Defaults to the
    /// real opener so production behaviour is unchanged.
    /// </summary>
    internal Action<string> DocumentationOpener { get; set; } = Services.UrlOpener.Open;

    private void ShowDocumentation()
    {
        _logger.LogInformation("Documentation requested");

        try
        {
            var readmePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "README.md");

            DocumentationOpener(System.IO.File.Exists(readmePath)
                ? readmePath
                : "https://github.com/marctjones/excise");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open documentation");
        }
    }

    /// <summary>
    /// Seam for #816: dialog commands (Security/Preferences/About/…) resolve
    /// their owner window through this, normally reading
    /// <c>Application.Current.ApplicationLifetime</c>. Avalonia allows
    /// <c>ApplicationLifetime</c> to be set exactly once — a headless test
    /// cannot stand up a desktop lifetime after <c>SetupWithoutStarting</c>
    /// has already initialized the app (attempting it throws
    /// <see cref="InvalidOperationException"/>) — so without this seam the
    /// real owner-window path is unreachable from a test and these
    /// commands' dialogs go untested (see <see cref="GetMainWindow"/>'s
    /// previous no-desktop-lifetime-in-headless-tests comment history).
    /// Test-settable so the real command can be executed and the real
    /// dialog window observed. Defaults to the real lookup so production
    /// behaviour is unchanged.
    /// </summary>
    internal Func<global::Avalonia.Controls.Window?> MainWindowResolver { get; set; } = DefaultMainWindowResolver;

    private static global::Avalonia.Controls.Window? DefaultMainWindowResolver()
    {
        var lifetime = global::Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime;

        return lifetime?.MainWindow;
    }

    /// <summary>
    /// Test seam (#816): the headless test host runs with no classic desktop
    /// lifetime (<c>SetupWithoutStarting</c>), so <see cref="GetMainWindow"/>
    /// always returns null and every file/save/export command that goes
    /// through <see cref="GetStorageProvider"/> was previously untestable via
    /// its actual command — only via the underlying method it eventually
    /// calls with an already-known path. Setting this lets a test execute the
    /// real ReactiveCommand end to end. Production code never sets it; the
    /// real <see cref="GetMainWindow"/> path is used unless a test overrides it.
    /// </summary>
    public IStorageProvider? StorageProviderOverride { get; set; }

    private global::Avalonia.Controls.Window? GetMainWindow() => MainWindowResolver();

    private IStorageProvider? GetStorageProvider()
    {
        return StorageProviderOverride ?? GetMainWindow()?.StorageProvider;
    }

    // ── Test seams for the file/folder-picker page-organization commands (#816).
    //    Headless tests have no desktop ApplicationLifetime (GetMainWindow →
    //    null) AND Avalonia's IStorageProvider/IStorageFile are sealed against
    //    user implementation, so a fake provider is impossible. Instead these
    //    delegates intercept at the picked-PATH boundary: when set, the picker
    //    helpers below return the injected paths and skip the real dialog,
    //    letting a test drive the actual COMMAND end-to-end. All null in
    //    production — the real storage provider is always used. ────────────────
    internal Func<bool, Task<IReadOnlyList<string>>>? PickPdfFilesOverride { get; set; }
    internal Func<Task<string?>>? PickSavePdfPathOverride { get; set; }
    internal Func<Task<string?>>? PickFolderOverride { get; set; }

    /// <summary>
    /// Shows the "open PDF(s)" picker and returns the selected local paths, or
    /// an empty list if cancelled / unavailable. Honors
    /// <see cref="PickPdfFilesOverride"/> in tests.
    /// </summary>
    private async Task<IReadOnlyList<string>> PickPdfFilesAsync(string title, bool allowMultiple)
    {
        if (PickPdfFilesOverride != null)
            return await PickPdfFilesOverride(allowMultiple);

        var storageProvider = GetStorageProvider();
        if (storageProvider == null)
        {
            _logger.LogWarning("Storage provider unavailable, cannot show open-PDF dialog");
            return Array.Empty<string>();
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PDF Files")
                {
                    Patterns = new[] { "*.pdf" }
                }
            }
        });

        return files
            .Select(f => f.Path.LocalPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
    }

    /// <summary>
    /// Shows the "save PDF as" picker and returns the chosen local path, or
    /// null if cancelled / unavailable. Honors
    /// <see cref="PickSavePdfPathOverride"/> in tests.
    /// </summary>
    private async Task<string?> PickSavePdfPathAsync(string title, string suggestedName)
    {
        if (PickSavePdfPathOverride != null)
            return await PickSavePdfPathOverride();

        var storageProvider = GetStorageProvider();
        if (storageProvider == null)
        {
            _logger.LogWarning("Storage provider unavailable, cannot show save-PDF dialog");
            return null;
        }

        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            DefaultExtension = "pdf",
            SuggestedFileName = suggestedName,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PDF Files")
                {
                    Patterns = new[] { "*.pdf" }
                }
            }
        });

        var path = file?.Path.LocalPath;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    /// <summary>
    /// Shows the folder picker and returns the chosen local path, or null if
    /// cancelled / unavailable. Honors <see cref="PickFolderOverride"/> in tests.
    /// </summary>
    private async Task<string?> PickFolderAsync(string title)
    {
        if (PickFolderOverride != null)
            return await PickFolderOverride();

        var storageProvider = GetStorageProvider();
        if (storageProvider == null)
        {
            _logger.LogWarning("Storage provider unavailable, cannot show folder dialog");
            return null;
        }

        var folder = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        if (folder.Count == 0)
            return null;

        var path = folder[0].Path.LocalPath;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private async Task<IStorageFile?> ShowSaveRedactedFileDialog(global::Avalonia.Controls.Window mainWindow, string suggestedPath)
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

    // OCR removed in the pure-Excise.Core migration. Reintroduce later
    // as a excise CLI subcommand if needed.

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
    /// Converts an SKBitmap to an global::Avalonia.Media.Imaging.Bitmap.
    /// </summary>
    /// <param name="skBitmap">The SKBitmap to convert.</param>
    /// <returns>An global::Avalonia.Media.Imaging.Bitmap, or null if conversion fails.</returns>
    private global::Avalonia.Media.Imaging.Bitmap? ToAvaloniaBitmap(SKBitmap? skBitmap)
    {
        // Direct pixel copy via WriteableBitmap — replaces a per-render
        // PNG encode + decode round-trip that ate ~150-300ms on every
        // page render. See Excise.Avalonia.Imaging.SkiaInterop for the rationale.
        try
        {
            return Excise.Avalonia.Imaging.SkiaInterop.ToAvaloniaBitmap(skBitmap);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting SKBitmap to global::Avalonia.Media.Imaging.Bitmap");
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
