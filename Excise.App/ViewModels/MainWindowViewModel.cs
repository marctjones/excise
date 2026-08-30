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
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using PdfCoreDocument = Excise.Core.Document.PdfDocument;

namespace Excise.App.ViewModels;

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
    private readonly DocumentTextIndexSession _textIndexSession;

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
    private Excise.Core.Text.ReadingOrderStrategy _readingOrderStrategy =
        Excise.Core.Text.ReadingOrderStrategy.ColumnAware;
    private Excise.Core.Text.WhitespaceMode _whitespaceMode =
        Excise.Core.Text.WhitespaceMode.Smart;
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
    private ObservableCollection<PdfPageRect> _currentPageSearchHighlights = new();
    private int _renderCacheMax = 20;
    private string _operationStatus = string.Empty;
    private readonly DocumentViewportSession _viewportSession = new();
    private readonly ThumbnailSidebarSession _thumbnailSession;
    internal Services.DocumentTextIndex? TextIndex => _textIndexSession.Current;

    private bool _isThumbnailsSidebarVisible = true;
    private bool _areAnnotationsVisible = true;
    private bool _areCommentAnnotationsVisible = true;
    private bool _areFieldAndLinkAnnotationsVisible = true;
    private bool _isAnnotationAuditModeEnabled;
    private bool _areFormFieldsHighlighted;
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
        _textIndexSession = new DocumentTextIndexSession(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentTextIndexSession>.Instance);
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
        _thumbnailSession = new ThumbnailSidebarSession(_logger);

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
        _textIndexSession = new DocumentTextIndexSession(
            loggerFactory.CreateLogger<DocumentTextIndexSession>());
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
        _thumbnailSession = new ThumbnailSidebarSession(_logger);

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
    public ObservableCollection<PageThumbnail> PageThumbnails => _thumbnailSession.Items;

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
        get => _viewportSession.ViewMode;
        set
        {
            if (!_viewportSession.SetViewMode(value))
                return;

            this.RaisePropertyChanged(nameof(ViewMode));
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

    public bool ContinuousScrollPreference => _viewportSession.ContinuousScrollPreference;

    public void ApplyContinuousScrollPreference(bool enabled)
    {
        if (_viewportSession.SetContinuousScrollPreference(enabled))
        {
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
        if (!ContinuousScrollPreference || IsEditingModeActive)
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
        get => _viewportSession.CurrentPageIndex;
        set
        {
            if (!_viewportSession.SetCurrentPageIndex(value))
                return;

            this.RaisePropertyChanged(nameof(CurrentPageIndex));
            RefreshCurrentPageBindings();
        }
    }

    /// <summary>
    /// Publishes state derived from the current page after a real navigation or
    /// after the backing document changed without changing the page number.
    /// Same-page viewer feedback must not call this: it is not a transition and
    /// clearing selection in that path loses user state.
    /// </summary>
    private void RefreshCurrentPageBindings()
    {
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

    public int TotalPages => _documentService.PageCount;

    private int _redactAnnotationCount;

    /// <summary>
    /// How many <c>/Redact</c> annotations the open document carries — regions
    /// somebody has MARKED for redaction but not applied (§12.5.6.23).
    /// </summary>
    /// <remarks>
    /// ⚠️ Reported, never acted on (#1021). A <c>/Redact</c> annotation is an
    /// instruction to a processor, and applying somebody else's marks is
    /// destructive and irreversible — excise will not do it silently or
    /// otherwise. Surfacing the count is the "surface, don't guess" carrier
    /// policy: the reviewer learns the marks exist and decides.
    /// </remarks>
    public int RedactAnnotationCount
    {
        get => _redactAnnotationCount;
        private set => this.RaiseAndSetIfChanged(ref _redactAnnotationCount, value);
    }

    /// <summary>
    /// A sentence for the UI when the document carries redaction marks, or null
    /// when it does not. Null rather than an empty string so a binding can hide
    /// the whole notice.
    /// </summary>
    public string? RedactAnnotationNotice => RedactAnnotationCount <= 0
        ? null
        : $"This document contains {RedactAnnotationCount} redaction mark" +
          (RedactAnnotationCount == 1 ? "" : "s") +
          " that have not been applied. excise does not apply them for you.";

    /// <summary>
    /// Recount <c>/Redact</c> annotations. Cheap (a dictionary read per
    /// annotation, no rendering) but not free on a large document, so it runs
    /// when the document changes rather than on every property read.
    /// </summary>
    private void RefreshRedactAnnotationCount()
    {
        var count = 0;
        try
        {
            var doc = _documentService.GetCurrentDocument();
            if (doc != null)
            {
                for (var p = 1; p <= doc.PageCount; p++)
                    foreach (var a in doc.GetPage(p).GetAnnotations())
                        if (a.Subtype == Excise.Core.Document.PdfAnnotationSubtype.Redact) count++;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A document excise cannot fully parse must not break opening it;
            // the notice is information, not a gate.
            count = 0;
        }

        RedactAnnotationCount = count;
        this.RaisePropertyChanged(nameof(RedactAnnotationNotice));
    }


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
    /// Annotation text shown while the pointer hovers a note, set via
    /// <see cref="SetHoveredAnnotationInfo"/> (#1074). Before this, no
    /// annotation's /Contents was reachable anywhere in the GUI — and for a
    /// /Text annotation, whose icon is all it draws, that meant the note was
    /// unreadable while the redaction scrubber could see it perfectly well.
    /// </summary>
    private string? _hoveredAnnotationInfo;

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
            // Below the link, above the counts: hovering is a deliberate act
            // and should win over ambient state, but a Link already owns the
            // pointer when both are under it (the viewer suppresses the
            // annotation hover in that case, so this is belt and braces).
            if (!string.IsNullOrEmpty(_hoveredAnnotationInfo))
                return _hoveredAnnotationInfo;
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
        get => _viewportSession.ZoomLevel;
        set => ApplyZoomTransition(_viewportSession.SetManualZoom(value));
    }

    private void ApplyZoomTransition(ZoomTransition transition)
    {
        if (!transition.ZoomChanged)
            return;

        this.RaisePropertyChanged(nameof(ZoomLevel));
        if (transition.ShouldPersist)
            SaveZoomPreference();
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

    /// <summary>
    /// Whether the page's annotations are drawn. Default true, which is what
    /// every conforming viewer does (§12.5) and what five of the six reference
    /// renderers do by default.
    ///
    /// Off is not a fidelity setting — it answers a different question: what is
    /// IN the page content stream, versus what is overlaid on top of it. That
    /// distinction is the whole point for a redaction tool, because a FreeText
    /// annotation looks like page content and is not, and a Widget's value is
    /// real text living outside the content stream entirely.
    /// </summary>
    public bool AreAnnotationsVisible
    {
        get => _areAnnotationsVisible;
        set => this.RaiseAndSetIfChanged(ref _areAnnotationsVisible, value);
    }

    public void ToggleAnnotationsVisible() =>
        AreAnnotationsVisible = !AreAnnotationsVisible;

    /// <summary>
    /// Show COMMENT annotations — notes, FreeText, text markup, shapes, Ink,
    /// Stamp, FileAttachment, Caret (#1021).
    /// </summary>
    /// <remarks>
    /// Two groups rather than one switch or twenty-three: for a redaction tool
    /// the split that matters is "content I must decide about" against "review
    /// markup I may want out of the way". A field's VALUE is content; a
    /// reviewer's sticky note is not.
    /// </remarks>
    public bool AreCommentAnnotationsVisible
    {
        get => _areCommentAnnotationsVisible;
        set => this.RaiseAndSetIfChanged(ref _areCommentAnnotationsVisible, value);
    }

    public void ToggleCommentAnnotationsVisible() =>
        AreCommentAnnotationsVisible = !AreCommentAnnotationsVisible;

    /// <summary>Show form fields and links (#1021).</summary>
    public bool AreFieldAndLinkAnnotationsVisible
    {
        get => _areFieldAndLinkAnnotationsVisible;
        set => this.RaiseAndSetIfChanged(ref _areFieldAndLinkAnnotationsVisible, value);
    }

    public void ToggleFieldAndLinkAnnotationsVisible() =>
        AreFieldAndLinkAnnotationsVisible = !AreFieldAndLinkAnnotationsVisible;

    /// <summary>
    /// AUDIT MODE: reveal annotations that <c>/F</c> Hidden or NoView
    /// suppresses (§12.5.3).
    /// </summary>
    /// <remarks>
    /// ⚠️ OFF by default and deliberately NOT one of the visibility toggles: it
    /// renders what no conforming viewer shows. It exists because "there is
    /// something here the viewer is not showing you" is what a person redacting
    /// a document needs to know. It must never affect an export.
    /// </remarks>
    public bool IsAnnotationAuditModeEnabled
    {
        get => _isAnnotationAuditModeEnabled;
        set => this.RaiseAndSetIfChanged(ref _isAnnotationAuditModeEnabled, value);
    }

    public void ToggleAnnotationAuditMode() =>
        IsAnnotationAuditModeEnabled = !IsAnnotationAuditModeEnabled;

    /// <summary>
    /// Tint fillable form fields — Acrobat's "Highlight Existing Fields".
    /// </summary>
    /// <remarks>
    /// ⚠️ OFF by default. Viewer chrome, not page content: a redaction tool must
    /// be able to show the page as it really is, and this must never reach an
    /// exported raster (#1005).
    /// </remarks>
    public bool AreFormFieldsHighlighted
    {
        get => _areFormFieldsHighlighted;
        set => this.RaiseAndSetIfChanged(ref _areFormFieldsHighlighted, value);
    }

    public void ToggleFormFieldHighlighting() =>
        AreFormFieldsHighlighted = !AreFormFieldsHighlighted;

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
            if (_pdfCoreDocument == null || CurrentPageIndex < 0 || CurrentPageIndex >= TotalPages)
                return string.Empty;

            try
            {
                var text = _textExtractionService.ExtractTextFromPage(_currentFilePath, CurrentPageIndex);
                return text ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract text from page {PageIndex}", CurrentPageIndex);
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
        get => _viewportSession.ViewportWidth;
        set
        {
            var transition = _viewportSession.UpdateViewport(value, ViewportHeight);
            if (!transition.WidthChanged)
                return;
            this.RaisePropertyChanged(nameof(ViewportWidth));
            ReapplyFitModeIfNeeded();
        }
    }

    public double ViewportHeight
    {
        get => _viewportSession.ViewportHeight;
        set
        {
            var transition = _viewportSession.UpdateViewport(ViewportWidth, value);
            if (!transition.HeightChanged)
                return;
            this.RaisePropertyChanged(nameof(ViewportHeight));
            ReapplyFitModeIfNeeded();
        }
    }

    private void ReapplyFitModeIfNeeded()
    {
        if (PdfCoreDocument == null) return;
        switch (_viewportSession.FitMode)
        {
            case ZoomFitMode.FitWidth:
                ZoomFitWidthInternal();
                break;
            case ZoomFitMode.FitPage:
                ZoomFitPageInternal();
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
        RefreshRedactAnnotationCount();
            RefreshRedactAnnotationCount();
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
        this.RaisePropertyChanged(nameof(TotalPages));
        RefreshRedactAnnotationCount();
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
        if (!string.IsNullOrWhiteSpace(_currentFilePath))
        {
            StartThumbnailSession(
                _currentFilePath,
                PdfCoreDocument!,
                cacheSalt: $"memory-version-{mutationVersion}");
        }
        else
        {
            ResetThumbnailSession();
        }

        _textIndexSession.Start(PdfCoreDocument!);

        RefreshCurrentPageBindings();
        return Task.CompletedTask;
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
        ZoomLevel = Math.Min(ZoomLevel * 1.25, DocumentViewportSession.MaximumZoom);
    }

    private void ZoomOut()
    {
        ZoomLevel = Math.Max(ZoomLevel / 1.25, DocumentViewportSession.MinimumZoom);
    }

    private void ZoomActualSize()
    {
        _logger.LogInformation("Setting zoom to actual size (100%)");
        ZoomLevel = 1.0;
    }

    /// <summary>
    /// Clamp a viewer-originated zoom to the supported range. Both this method
    /// and direct <see cref="ZoomLevel"/> assignments explicitly end fit mode.
    /// </summary>
    internal void SetManualZoom(double zoom)
    {
        ZoomLevel = Math.Clamp(
            zoom,
            DocumentViewportSession.MinimumZoom,
            DocumentViewportSession.MaximumZoom);
    }

    private void ZoomFitWidth() => ZoomFitWidthInternal();
    private void ZoomFitPage() => ZoomFitPageInternal();

    /// <summary>
    /// Resize zoom to fit the page width and latch that fit mode so subsequent
    /// viewport-size changes re-apply it until the user manually zooms.
    /// </summary>
    private void ZoomFitWidthInternal()
    {
        _logger.LogInformation("Setting zoom to fit width");
        double zoom;
        if (TryGetMaxPageDimensionsInViewerDips(out var pageW, out _) &&
            ViewportWidth > 0)
        {
            // Tiny gutter so the page edge does not touch the scrollbar or
            // central-pane border.
            const double margin = 8;
            var target = Math.Max(1.0, ViewportWidth - margin);
            zoom = Math.Clamp(
                target / pageW,
                DocumentViewportSession.MinimumZoom,
                DocumentViewportSession.MaximumZoom);
            _logger.LogDebug("Fit width: viewport={Viewport}, page={Page}, zoom={Zoom:P0}",
                ViewportWidth, pageW, zoom);
        }
        else
        {
            zoom = 1.0;
        }

        ApplyZoomTransition(_viewportSession.SetAutomaticFitZoom(ZoomFitMode.FitWidth, zoom));
    }

    private void ZoomFitPageInternal()
    {
        _logger.LogInformation("Setting zoom to fit page");
        double zoom;
        if (TryGetMaxPageDimensionsInViewerDips(out var pageW, out var pageH) &&
            ViewportWidth > 0 && ViewportHeight > 0)
        {
            const double marginH = 8;
            const double marginV = 8;
            var targetW = Math.Max(1.0, ViewportWidth - marginH);
            var targetH = Math.Max(1.0, ViewportHeight - marginV);
            // Whichever dimension is the binding constraint wins.
            zoom = Math.Clamp(
                Math.Min(targetW / pageW, targetH / pageH),
                DocumentViewportSession.MinimumZoom,
                DocumentViewportSession.MaximumZoom);
            _logger.LogDebug("Fit page: vp=({Vw}x{Vh}), pg=({Pw}x{Ph}), zoom={Zoom:P0}",
                ViewportWidth, ViewportHeight, pageW, pageH, zoom);
        }
        else
        {
            zoom = 1.0;
        }

        ApplyZoomTransition(_viewportSession.SetAutomaticFitZoom(ZoomFitMode.FitPage, zoom));
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
        _textIndexSession.Cancel();

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
            ResetThumbnailSession();
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

            // Reset display zoom without overwriting the user's preference.
            ApplyZoomTransition(_viewportSession.ResetZoomWithoutPersisting());

            // Notify UI of all state changes
            this.RaisePropertyChanged(nameof(DocumentName));
            this.RaisePropertyChanged(nameof(TotalPages));
            RefreshRedactAnnotationCount();
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

    /// <summary>
    /// Status-bar hover feedback for the annotation under the pointer, or null
    /// when not hovering one (#1074).
    /// </summary>
    public void SetHoveredAnnotationInfo(string? info)
    {
        if (_hoveredAnnotationInfo == info) return;
        _hoveredAnnotationInfo = info;
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
                    if (savedZoom >= DocumentViewportSession.MinimumZoom &&
                        savedZoom <= DocumentViewportSession.MaximumZoom)
                    {
                        _viewportSession.LoadZoomPreference(savedZoom);
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
                if (docState.ZoomLevel > 0 &&
                    docState.ZoomLevel <= DocumentViewportSession.MaximumZoom)
                {
                    ApplyZoomTransition(_viewportSession.RestoreManualZoom(docState.ZoomLevel));
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

}
