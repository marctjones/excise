using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.App.Models;
using Excise.App.Services.Host;
using Excise.App.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

namespace Excise.App.Views;

public partial class MainWindow : Window
{
    private PdfViewerControl? _pdfViewerControl;
    private readonly ISettingsStore _settingsStore;
    private readonly WindowSettings _windowSettings;
    // #1551: one native menu per document session, built on first show and
    // reused when the same session is shown again. Weak keys, so a closed
    // session's menu goes with it.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<MainWindowViewModel, NativeMenu> _nativeMenus = new();
    private NativeMenu? _nativeMenu;
    private bool _isNativeWindowOpened;
    private bool _nativeMenuAttachScheduled;
    private int _nativeMenuAttachAttempts;
    private int? _draggedThumbnailPageIndex;
    private const int MaxNativeMenuAttachAttempts = 40;
    private static readonly TimeSpan NativeMenuAttachRetryDelay = TimeSpan.FromMilliseconds(50);

    // Single reusable UI-thread timer for toast auto-dismiss. A DispatcherTimer
    // (vs System.Timers.Timer) ticks on the dispatcher itself, so it stops when
    // the dispatcher stops and never marshals a callback (Dispatcher.InvokeAsync)
    // into a torn-down/foreign dispatcher. The old per-toast wall-clock timer
    // posted its dismiss continuation 5s later from a ThreadPool thread — often
    // after the owning test had finished — which deadlocked the headless
    // dispatcher under --blame-hang-timeout and made GUI tests (e.g.
    // CtrlS_SavesFile) flaky. See the KeyboardShortcutTests quarantine.
    private DispatcherTimer? _toastTimer;

    /// <summary>
    /// The viewer and the #1478 cache-trim policy from this window's settings.
    /// App wires the trim coordinator; the window only reports what to wire.
    /// </summary>
    internal (PdfViewerControl? Viewer, Excise.App.Services.CacheTrimPolicy Policy) CacheTrimTarget() =>
        (_pdfViewerControl ??= this.FindControl<PdfViewerControl>("PdfViewerControl"),
         CacheTrimPolicyFor(_performanceSettings));

    /// <summary>
    /// The performance settings last applied to this window's viewer. Starts as
    /// the persisted values so <see cref="CacheTrimTarget"/> is right even when
    /// it is read before a DataContext is set.
    /// </summary>
    private PerformanceSettings _performanceSettings = PerformanceSettings.Balanced;

    /// <summary>
    /// Raised on the UI thread when Preferences → Performance changes the soft
    /// cache-trim policy; App forwards it to the live trim coordinator.
    /// </summary>
    internal event Action<Excise.App.Services.CacheTrimPolicy>? CacheTrimPolicyChanged;

    private Excise.App.Services.CacheTrimPolicy CacheTrimPolicyFor(PerformanceSettings settings) =>
        new(_windowSettings.CacheTrimOnMemoryPressure,
            settings.SoftCacheTrims,
            TimeSpan.FromSeconds(Math.Max(1, settings.IdleTrimSeconds)));

    /// <summary>
    /// Push performance settings into the viewer (UI thread): tile budget,
    /// single-page cache, render concurrency; then the trim policy to App.
    /// </summary>
    private void OnPerformanceSettingsApplied(object? sender, PerformanceSettings settings)
    {
        _performanceSettings = settings;
        _pdfViewerControl ??= this.FindControl<PdfViewerControl>("PdfViewerControl");
        if (_pdfViewerControl != null)
        {
            _pdfViewerControl.ContinuousTileCacheByteBudget = settings.TileCacheBudgetMb * 1024L * 1024L;
            _pdfViewerControl.SinglePageCacheCapacity = settings.SinglePageCachedPages;
            _pdfViewerControl.ContinuousRenderConcurrency = settings.RenderThreads;
        }
        CacheTrimPolicyChanged?.Invoke(CacheTrimPolicyFor(settings));
    }

    /// <summary>
    /// Window geometry capture and apply is view state, so it stays here — but
    /// through <see cref="ISettingsStore"/> since #1500 step 2, so a caller can
    /// supply an in-memory store and the window stops writing
    /// <c>window.json</c>. The parameterless constructor keeps the production
    /// (and current test) behaviour: there are 266 <c>new MainWindow { … }</c>
    /// sites, so the file-backed store has to remain the default.
    /// </summary>
    public MainWindow() : this(new FileSettingsStore())
    {
    }

    internal MainWindow(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));

        InitializeComponent();

        // Thumbnail drag-to-reorder is driven from the thumbnails ItemsControl,
        // not from per-item Button event attributes: a Button marks its
        // PointerPressed/Released Handled, which would skip a normal instance
        // handler. handledEventsToo:true lets these observe the events anyway
        // so a drag actually reorders (#827). Click-to-navigate remains on the
        // Button's Command binding.
        //
        // Tunnel is load-bearing here (do NOT reduce to Bubble): on Tunnel the
        // drop runs and sets e.Handled BEFORE the Button's bubble class handler
        // raises Click, so a real drag reorders WITHOUT also navigating. On
        // Bubble-only the Button would Click first, so every drop would both
        // navigate and reorder. A test can't cleanly guard this — after a 0→1
        // move RemapCurrentPageAfterSingleMove sets CurrentPageIndex to 1, the
        // same index a stray navigate would produce — so this comment is the guard.
        var thumbStrip = this.FindControl<ItemsControl>("ThumbnailsItemsControl");
        if (thumbStrip != null)
        {
            thumbStrip.AddHandler(PointerPressedEvent, OnThumbnailPointerPressed,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            thumbStrip.AddHandler(PointerReleasedEvent, OnThumbnailPointerReleased,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        }

        if (OperatingSystem.IsMacOS())
        {
            MainMenuBar.IsVisible = false;
            TitleBarAppLabel.Margin = new Thickness(86, 0, 10, 0);
        }

        // Load and apply window settings (Issue #23)
        _windowSettings = _settingsStore.Load();
        _windowSettings.ApplyTo(this);
        _performanceSettings = PerformanceSettings.FromWindowSettings(_windowSettings);

        // Save settings on close, and guard unsaved document changes (#1233)
        this.Closing += OnWindowClosing;

        // Drag-and-drop a PDF onto the window to open it (#1002). Registered
        // in code rather than as XAML attributes so the DragOver handler that
        // ADVERTISES the drop (without it the OS shows a "no entry" cursor and
        // never delivers a Drop) cannot be separated from the Drop handler
        // itself.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Add keyboard handler for Ctrl+C
        this.KeyDown += MainWindow_KeyDown;

        // #1554: the tab strip binds to the window's tabs, never to the
        // session the window shows; a local null stops it inheriting one.
        DocumentTabStripHost.DataContext = null;
        // Tunnel: Ctrl+Tab must reach the tabs before keyboard navigation
        // treats Tab as a focus move.
        AddHandler(KeyDownEvent, OnTabSwitchKeyDown, RoutingStrategies.Tunnel);

        // Subscribe to search highlights changes
        this.DataContextChanged += OnDataContextChanged;
        // #1551: a closed window lets go of its session, so the session's
        // memory can be released with it.
        this.Closed += (_, _) => UnbindViewModel();
        this.Opened += (_, _) =>
        {
            _isNativeWindowOpened = true;
            SchedulePlatformMenuConfigure();
        };
    }

    /// <summary>
    /// Set once the user has answered the unsaved-changes prompt and chosen to
    /// proceed, so the programmatic re-close does not ask again. Without it the
    /// re-issued <see cref="Window.Close"/> would re-enter this handler and
    /// prompt forever.
    /// </summary>
    private bool _closeApproved;

    /// <summary>
    /// #1233. <see cref="Window.Closing"/> is synchronous and the prompt is
    /// not, so the only workable shape is: cancel this close, ask, and re-issue
    /// the close if the answer allows it.
    /// </summary>
    /// <remarks>
    /// This stays in code-behind because cancelling a routed window event and
    /// re-invoking <c>Close()</c> is view mechanics that a ViewModel has no
    /// handle on. Every DECISION — whether anything is dirty, how a save is
    /// routed so an original is preserved, what a failed save means — belongs
    /// to <see cref="MainWindowViewModel.ConfirmDiscardUnsavedChangesAsync"/>
    /// and is tested there.
    /// </remarks>
    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_closeApproved && HostedViewModels().Any(vm => vm.HasUnsavedDocumentChanges))
        {
            e.Cancel = true;

            // Fire-and-forget deliberately: the handler must return
            // synchronously with Cancel set, and the continuation re-enters
            // Close() on the UI thread once the user has answered.
            _ = PromptThenCloseAsync();
            return;
        }

        // #1546: a Windows print job still being sent is aborted, best
        // effort, rather than left for the process exit to cut short.
        (DataContext as MainWindowViewModel)?.CancelPrintInProgress();
        PersistWindowStateOnClose();
    }

    /// <summary>
    /// Every session this window holds: all of its tabs (#1554), or the one
    /// session it shows.
    /// </summary>
    private IReadOnlyList<MainWindowViewModel> HostedViewModels()
    {
        // A window whose tabs were all merged elsewhere holds nothing, even
        // though it still shows the last of them until it closes.
        if (_documentTabs is { } tabs)
            return tabs.Tabs.Select(t => t.Session.ViewModel).ToArray();
        return DataContext is MainWindowViewModel vm ? new[] { vm } : Array.Empty<MainWindowViewModel>();
    }

    private async System.Threading.Tasks.Task PromptThenCloseAsync()
    {
        try
        {
            foreach (var viewModel in HostedViewModels())
            {
                if (!viewModel.HasUnsavedDocumentChanges)
                    continue;

                // Show the tab being asked about.
                if (_documentTabs?.Tabs.FirstOrDefault(t => ReferenceEquals(t.Session.ViewModel, viewModel)) is { } tab)
                    _documentTabs.SelectedTab = tab;

                var proceed = await viewModel.ConfirmDiscardUnsavedChangesAsync("close this window");
                if (!proceed)
                    return;
            }

            _closeApproved = true;
            Close();
        }
        catch (Exception ex)
        {
            // Nothing awaits this continuation, so an escaping exception would
            // be an unobserved task: the window would silently stay open with
            // no diagnostic. Fail toward keeping the document (do NOT set
            // _closeApproved) but say why.
            System.Diagnostics.Debug.WriteLine($"Unsaved-changes close prompt failed: {ex}");
        }
    }

    private void PersistWindowStateOnClose()
    {
        // Through the store, never by saving _windowSettings: that snapshot is
        // from startup, so saving it would revert a Preferences save and every
        // document state written since.
        var viewModel = DataContext as MainWindowViewModel;
        _settingsStore.Update(settings =>
        {
            if (viewModel != null)
            {
                settings.ContinuousScrollEnabled = viewModel.ContinuousScrollPreference;
                settings.AttachmentsSidebarVisible = viewModel.IsAttachmentsSidebarVisible;
                viewModel.WritePreferencesTo(settings);
            }
            settings.CaptureFrom(this);
        });
        // Cancel any pending toast auto-dismiss so nothing is left queued on
        // the dispatcher when the window/test tears down.
        _toastTimer?.Stop();
    }

    /// <summary>
    /// Advertise that a file drag is acceptable (#1002).
    /// </summary>
    /// <remarks>
    /// Required, not optional: with no DragOver handler setting an effect, the
    /// platform treats the drag as rejected, shows a "not allowed" cursor and
    /// never raises Drop — the feature would look unimplemented while the Drop
    /// handler sat there correctly written.
    /// </remarks>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Adapter only: turn the drag payload into storage items and hand them to
    /// the ViewModel, which owns every decision (#1002).
    /// </summary>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (DataContext is not MainWindowViewModel viewModel)
            return;

        var files = e.DataTransfer.TryGetFiles();
        if (files == null)
            return;

        _ = OpenDroppedFilesSafeAsync(viewModel, [.. files]);
    }

    private static async System.Threading.Tasks.Task OpenDroppedFilesSafeAsync(
        MainWindowViewModel viewModel,
        System.Collections.Generic.IReadOnlyList<global::Avalonia.Platform.Storage.IStorageItem> files)
    {
        try
        {
            await viewModel.OpenDroppedFilesAsync(files);
        }
        catch (Exception ex)
        {
            // Drop delivers no place to await, so an escaping exception would
            // be an unobserved task and the drop would look ignored.
            System.Diagnostics.Debug.WriteLine($"Drop-to-open failed: {ex}");
        }
    }

    // #1551: the session this window is bound to, and how to let go of it.
    // Every subscription made in BindViewModel is undone in UnbindViewModel,
    // so a window can show another session (a tab switch) and a closed
    // session is not kept alive by its window's handlers.
    private MainWindowViewModel? _boundViewModel;
    private readonly List<Action> _viewModelUnsubscribers = new();
    private bool _hasBoundViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Get reference to PdfViewerControl
        _pdfViewerControl ??= this.FindControl<PdfViewerControl>("PdfViewerControl");

        var next = DataContext as MainWindowViewModel;
        if (ReferenceEquals(next, _boundViewModel))
            return;

        UnbindViewModel();
        if (next != null)
            BindViewModel(next);
    }

    private void BindViewModel(MainWindowViewModel viewModel)
    {
        var rebinding = _hasBoundViewModel;
        _hasBoundViewModel = true;
        _boundViewModel = viewModel;

        if (!viewModel.WindowPreferencesApplied)
        {
            // The first session shown by this window takes the settings loaded
            // when the window was built, exactly as before #1551. A session
            // shown later reads them again, because a Preferences save since
            // then changed them.
            ApplyPersistedPreferences(viewModel, rebinding ? _settingsStore.Load() : _windowSettings);
        }
        else
        {
            Subscribe(viewModel);
            OnPerformanceSettingsApplied(viewModel, viewModel.PerformanceSettings);
        }

        viewModel.ViewerTileCacheResidentBytesProvider = TileCacheResidentBytes;
        SchedulePlatformMenuConfigure();

        // Push the viewer's *visible* viewport (inside-the-scrollbars)
        // into the VM so Fit Width / Fit Page fit against what the user
        // actually sees, not the outer control bounds. Using outer
        // bounds gave a result ~16-20 DIPs too big — exactly the strip
        // a vertical scrollbar reserves — which made Fit Width pop a
        // horizontal scrollbar that then stole more space and broke
        // the fit recursively.
        if (_pdfViewerControl != null)
        {
            var initial = _pdfViewerControl.GetVisibleViewportSize();
            viewModel.ViewportWidth = initial.Width;
            viewModel.ViewportHeight = initial.Height;
        }

        if (rebinding)
        {
            // The overlays and the toast belonged to the session shown before.
            CloseToast();
            UpdateSearchHighlightsCanvas();
            UpdateRedactionOverlays();
        }
    }

    private void ApplyPersistedPreferences(MainWindowViewModel viewModel, WindowSettings settings)
    {
        viewModel.ApplyContinuousScrollPreference(settings.ContinuousScrollEnabled);
        viewModel.ApplyAttachmentsPanePreference(settings.AttachmentsSidebarVisible);
        if (Enum.TryParse<Excise.Core.Text.ReadingOrderStrategy>(
                settings.ReadingOrderStrategy, out var strategy))
            viewModel.ApplyReadingOrderStrategyPreference(strategy);
        if (Enum.TryParse<Excise.Core.Text.WhitespaceMode>(
                settings.WhitespaceMode, out var whitespaceMode))
            viewModel.ApplyWhitespaceModePreference(whitespaceMode);
        viewModel.ApplyRedactionPolicyPreferences(
            settings.RedactionWholeWord,
            settings.RedactionWidthPolicy,
            settings.LinkUriCarrierPolicy,
            settings.MetadataCarrierPolicy,
            settings.RedactionKeepAttachments);
        viewModel.ApplyPrintScalingPreference(settings.PrintScaling);
        viewModel.ApplyDocumentOpenModePreference(settings.DocumentOpenMode);
        // Preferences → Performance: subscribe first so the restore below
        // reaches the viewer through the same path a Save does.
        Subscribe(viewModel);
        viewModel.ApplyPerformanceSettings(
            PerformanceSettings.FromWindowSettings(settings), fromPersistedStartup: true);
        viewModel.WindowPreferencesApplied = true;
    }

    private void Subscribe(MainWindowViewModel viewModel)
    {
        viewModel.PerformanceSettingsApplied += OnPerformanceSettingsApplied;
        _viewModelUnsubscribers.Add(() => viewModel.PerformanceSettingsApplied -= OnPerformanceSettingsApplied);

        // Subscribe to toast notifications
        var toasts = viewModel.ToastService;
        toasts.ToastRequested += OnToastRequested;
        _viewModelUnsubscribers.Add(() => toasts.ToastRequested -= OnToastRequested);

        // Subscribe to search highlights collection changes
        var highlights = viewModel.CurrentPageSearchHighlights;
        highlights.CollectionChanged += OnSearchHighlightsChanged;
        _viewModelUnsubscribers.Add(() => highlights.CollectionChanged -= OnSearchHighlightsChanged);

        // Subscribe to redaction collection changes
        var pending = viewModel.RedactionWorkflow.PendingRedactions;
        var applied = viewModel.RedactionWorkflow.AppliedRedactions;
        pending.CollectionChanged += OnRedactionsChanged;
        applied.CollectionChanged += OnRedactionsChanged;
        _viewModelUnsubscribers.Add(() =>
        {
            pending.CollectionChanged -= OnRedactionsChanged;
            applied.CollectionChanged -= OnRedactionsChanged;
        });

        viewModel.AnnotationsChanged += OnAnnotationsChanged;
        _viewModelUnsubscribers.Add(() => viewModel.AnnotationsChanged -= OnAnnotationsChanged);

        // #1563: Document ▸ Attachments moves keyboard focus into the pane.
        viewModel.AttachmentsPaneFocusRequested += OnAttachmentsPaneFocusRequested;
        _viewModelUnsubscribers.Add(() => viewModel.AttachmentsPaneFocusRequested -= OnAttachmentsPaneFocusRequested);

        // #846: before a structural mutation reloads the document, let the
        // continuous view snapshot the reader's position so the rebuild
        // restores it instead of jumping to the top of the page.
        EventHandler preserveReadingPosition = (_, _) =>
            _pdfViewerControl?.PreserveContinuousReadingPositionOnNextRebuild();
        viewModel.PreserveReadingPositionRequested += preserveReadingPosition;
        _viewModelUnsubscribers.Add(() => viewModel.PreserveReadingPositionRequested -= preserveReadingPosition);

        // #917: one document means the viewer's Document reference no
        // longer changes on a structural mutation, so nothing tells the
        // continuous view to re-lay-out. This does.
        EventHandler structureChanged = (_, _) => _pdfViewerControl?.RefreshContinuousLayout();
        viewModel.DocumentStructureChanged += structureChanged;
        _viewModelUnsubscribers.Add(() => viewModel.DocumentStructureChanged -= structureChanged);

        // Subscribe to page changes to update redaction overlays
        System.ComponentModel.PropertyChangedEventHandler pageChanged = (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.CurrentPageIndex))
            {
                UpdateRedactionOverlays();
            }

            // #1552/#1553: the title names the document (and says when it has
            // unsaved edits); dirty-state changes raise SaveButtonText.
            if (args.PropertyName is null
                or nameof(viewModel.DocumentName)
                or nameof(viewModel.IsDocumentLoaded)
                or nameof(viewModel.SaveButtonText))
            {
                UpdateTitle(viewModel);
            }
        };
        UpdateTitle(viewModel);
        viewModel.PropertyChanged += pageChanged;
        _viewModelUnsubscribers.Add(() => viewModel.PropertyChanged -= pageChanged);

        if (_pdfViewerControl != null)
        {
            var viewer = _pdfViewerControl;
            EventHandler<Size> viewportChanged = (_, size) =>
            {
                viewModel.ViewportWidth = size.Width;
                viewModel.ViewportHeight = size.Height;
            };
            viewer.VisibleViewportChanged += viewportChanged;
            _viewModelUnsubscribers.Add(() => viewer.VisibleViewportChanged -= viewportChanged);
        }
    }

    private void UnbindViewModel()
    {
        var viewModel = _boundViewModel;
        if (viewModel == null)
            return;

        _boundViewModel = null;
        foreach (var unsubscribe in _viewModelUnsubscribers)
            unsubscribe();
        _viewModelUnsubscribers.Clear();

        // Only clear the provider if it is still this window's.
        if (viewModel.ViewerTileCacheResidentBytesProvider == (Func<long?>)TileCacheResidentBytes)
            viewModel.ViewerTileCacheResidentBytesProvider = null;
    }

    private void UpdateTitle(MainWindowViewModel viewModel)
    {
        Title = Excise.App.Workspace.DocumentWindowTitle.For(
            viewModel.IsDocumentLoaded ? viewModel.DocumentName : null,
            viewModel.HasUnsavedDocumentChanges);
    }

    // ── #1554: in-app document tabs ─────────────────────────────────────────

    private ViewModels.DocumentTabsViewModel? _documentTabs;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<MainWindowViewModel, System.Runtime.CompilerServices.StrongBox<double>> _scrollFractions = new();
    private bool _switchingSession;
    private int _switchGeneration;

    /// <summary>
    /// The tabs this window holds. The window shows the selected tab's
    /// session; set by the workspace.
    /// </summary>
    internal ViewModels.DocumentTabsViewModel? DocumentTabs
    {
        get => _documentTabs;
        set
        {
            if (ReferenceEquals(value, _documentTabs))
                return;
            if (_documentTabs != null)
                _documentTabs.PropertyChanged -= OnDocumentTabsPropertyChanged;

            _documentTabs = value;
            DocumentTabStripHost.DataContext = value;
            DocumentTabStripHost.IsVisible = value != null;
            if (value != null)
            {
                value.PropertyChanged += OnDocumentTabsPropertyChanged;
                ShowSelectedTab();
            }
        }
    }

    private void OnDocumentTabsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.DocumentTabsViewModel.SelectedTab))
            ShowSelectedTab();
    }

    private void ShowSelectedTab()
    {
        var next = _documentTabs?.SelectedTab?.Session.ViewModel;
        // No selection means the last tab is leaving and the window is about
        // to close: keep what is shown.
        if (next == null || ReferenceEquals(next, DataContext))
            return;

        SwitchSession(next);
    }

    /// <summary>
    /// Show <paramref name="next"/> in this window. The outgoing session's
    /// scroll position is remembered and the incoming one's restored once the
    /// viewer has laid the document out again.
    /// </summary>
    private void SwitchSession(MainWindowViewModel next)
    {
        if (DataContext is MainWindowViewModel current && _pdfViewerControl != null)
        {
            var viewport = _pdfViewerControl.GetViewportDiagnostics();
            if (viewport.IsAvailable)
                _scrollFractions.AddOrUpdate(current, new System.Runtime.CompilerServices.StrongBox<double>(VerticalFraction(viewport)));
        }

        var generation = ++_switchGeneration;
        _switchingSession = true;
        DataContext = next;

        double? saved = _scrollFractions.TryGetValue(next, out var box) ? box.Value : null;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _switchGeneration)
                return;
            try
            {
                if (saved is double fraction && ReferenceEquals(DataContext, next))
                    _pdfViewerControl?.TrySetViewportVerticalFraction(fraction);
            }
            finally
            {
                _switchingSession = false;
            }
        }, DispatcherPriority.ContextIdle);
    }

    internal static double VerticalFraction(PdfViewerViewportDiagnostics viewport)
    {
        var range = viewport.Extent.Height - viewport.Viewport.Height;
        if (!(range > 0))
            return 0;
        return Math.Clamp(viewport.Offset.Y / range, 0, 1);
    }

    /// <summary>
    /// Ctrl+Tab / Ctrl+Shift+Tab and Ctrl+PageDown / Ctrl+PageUp switch tabs;
    /// on macOS also Cmd+Shift+] / Cmd+Shift+[. Only with more than one tab,
    /// so Tab keeps moving focus everywhere else.
    /// </summary>
    private void OnTabSwitchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_documentTabs is not { Tabs.Count: > 1 } tabs)
            return;

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var meta = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        int step = 0;

        if (control && e.Key == Key.Tab)
            step = shift ? -1 : +1;
        else if (control && e.Key == Key.PageDown)
            step = +1;
        else if (control && e.Key == Key.PageUp)
            step = -1;
        else if (OperatingSystem.IsMacOS() && meta && shift && e.Key == Key.OemCloseBrackets)
            step = +1;
        else if (OperatingSystem.IsMacOS() && meta && shift && e.Key == Key.OemOpenBrackets)
            step = -1;

        if (step == 0)
            return;

        e.Handled = true;
        (step > 0 ? tabs.SelectNextTabCommand : tabs.SelectPreviousTabCommand).Execute().Subscribe();
    }

    private long? TileCacheResidentBytes() => _pdfViewerControl?.ContinuousTileCacheResidentBytes;

    private void ConfigurePlatformMenu(MainWindowViewModel viewModel)
    {
        // NativeMenu caches the platform exporter lookup. Attaching before the
        // native window/exporter exists can cache a null exporter and leave
        // macOS with only its default app menu, so wait until the exporter is
        // actually available before setting the attached menu property.
        if (!OperatingSystem.IsMacOS() || !_isNativeWindowOpened)
        {
            return;
        }

        if (PlatformImpl?.TryGetFeature<ITopLevelNativeMenuExporter>() is null)
        {
            if (_nativeMenuAttachAttempts++ < MaxNativeMenuAttachAttempts)
                SchedulePlatformMenuConfigure(NativeMenuAttachRetryDelay);
            return;
        }

        _nativeMenuAttachAttempts = 0;
        if (!_nativeMenus.TryGetValue(viewModel, out var menu))
        {
            menu = MacNativeMenuBuilder.Create(viewModel);
            _nativeMenus.Add(viewModel, menu);
        }
        _nativeMenu = menu;

        // The application (app-name) menu is owned by App and set on the
        // Application before the first window exists (#834) — it must precede
        // Avalonia's one-shot app-menu exporter, which a window-side set cannot.
        // Here we only attach the window (menu-bar) menu; the TopLevel exporter
        // does subscribe to changes, so setting it after the window opens works.
        NativeMenu.SetMenu(this, _nativeMenu);
    }

    private void SchedulePlatformMenuConfigure(TimeSpan? delay = null)
    {
        if (!OperatingSystem.IsMacOS() || _nativeMenuAttachScheduled)
            return;

        _nativeMenuAttachScheduled = true;

        void Configure()
        {
            _nativeMenuAttachScheduled = false;
            if (DataContext is MainWindowViewModel viewModel)
                ConfigurePlatformMenu(viewModel);
        }

        if (delay is { } retryDelay)
            DispatcherTimer.RunOnce(Configure, retryDelay, DispatcherPriority.Background);
        else
            Dispatcher.UIThread.Post(Configure, DispatcherPriority.ApplicationIdle);
    }

    private void OnSearchHighlightsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateSearchHighlightsCanvas();
    }

    /// <summary>
    /// Document ▸ Attachments (#1563): the ViewModel has already shown the
    /// pane; move keyboard focus into it once layout has made it visible.
    /// </summary>
    private void OnAttachmentsPaneFocusRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // The ListBox itself is not focusable; its rows are. Focus the
            // selected row, else the first, so arrow keys work straight away.
            // An empty pane has nothing to focus and is left alone.
            if (!AttachmentsList.IsEffectivelyVisible || AttachmentsList.ItemCount == 0)
                return;
            var index = AttachmentsList.SelectedIndex >= 0 ? AttachmentsList.SelectedIndex : 0;
            AttachmentsList.ScrollIntoView(index);
            AttachmentsList.UpdateLayout();
            AttachmentsList.ContainerFromIndex(index)?.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Theme-independent click handler for the outline TreeView. Walks
    /// up from the click target to find the nearest TreeViewItem and
    /// invokes JumpToOutline on its OutlineNode DataContext. Avoids the
    /// FluentAvalonia-specific chevron/content hit-test boundary that
    /// makes the SelectedItem path silently swallow clicks.
    /// </summary>
    private void OnOutlineTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var src = e.Source as Control;
        while (src != null)
        {
            if (src is global::Avalonia.Controls.TreeViewItem tvi &&
                tvi.DataContext is Excise.App.Models.OutlineNode node)
            {
                vm.JumpToOutline(node);
                return;
            }
            src = src.Parent as Control;
        }
    }

    /// <summary>
    /// Fires whenever a thumbnail Image's effective visible area changes
    /// (item virtualisation, scrolling the strip, sidebar toggling). When
    /// the area is non-empty we ask the VM to ensure that page's thumbnail
    /// is loaded — VM dedupes already-loaded pages and coalesces concurrent
    /// requests, so we can call this freely.
    /// </summary>
    private void OnThumbnailViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (sender is not Image img) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (img.Tag is not int pageIndex) return;

        var visible = e.EffectiveViewport.Width > 0 && e.EffectiveViewport.Height > 0;

        // Both transitions feed the VM's viewport window: visible items anchor
        // the ±prefetch window (#688), items scrolled far out get their decoded
        // bitmap released (#687).
        vm.NotifyThumbnailViewport(pageIndex, visible);
        if (!visible) return;

        // Fire-and-forget — VM handles its own dispatching to the UI thread.
        _ = vm.EnsureThumbnailLoadedAsync(pageIndex);
    }

    private void OnThumbnailPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // The batch-select CheckBox lives inside the thumbnail — a press there
        // toggles selection and must not start a page drag.
        if (e.Source is CheckBox)
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        // sender is the thumbnails ItemsControl (handlers are attached there,
        // see the constructor), so resolve the pressed thumbnail from the
        // pointer position rather than from sender's DataContext.
        _draggedThumbnailPageIndex = ThumbnailUnderPointer(e)?.PageIndex;
    }

    private void OnThumbnailPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_draggedThumbnailPageIndex is not int fromIndex)
            return;

        _draggedThumbnailPageIndex = null;

        if (DataContext is not MainWindowViewModel vm)
            return;

        // Resolve the DROP target from where the pointer actually came up, not
        // from `sender`: a Button captures the pointer on PointerPressed, so the
        // PointerReleased is routed back to the SOURCE thumbnail's Button. Using
        // `sender` therefore made toIndex == fromIndex on every real drag, so
        // drag-to-reorder silently did nothing. Hit-test the release position
        // instead. See #827.
        var dropThumbnail = ThumbnailUnderPointer(e);
        if (dropThumbnail is null)
            return;

        var toIndex = dropThumbnail.PageIndex;
        if (fromIndex == toIndex)
            return;

        e.Handled = true;
        _ = vm.MovePageAsync(fromIndex, toIndex);
    }

    /// <summary>
    /// The <see cref="PageThumbnail"/> whose visual sits under the pointer, or
    /// null if the release landed outside the thumbnail strip. Used so a drag's
    /// drop target follows the pointer even though the source Button captured it.
    /// </summary>
    private PageThumbnail? ThumbnailUnderPointer(PointerEventArgs e)
    {
        var hit = this.InputHitTest(e.GetPosition(this)) as Visual;
        while (hit is not null)
        {
            if (hit is Control { DataContext: PageThumbnail thumbnail })
                return thumbnail;
            hit = hit.GetVisualParent();
        }
        return null;
    }

    /// <summary>
    /// Enter inside the search box triggers an immediate search (skips
    /// the debounce). Escape closes the search bar.
    /// </summary>
    private void OnSearchTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        if (e.Key == Key.Enter)
        {
            vm.FindCommand?.Execute().Subscribe();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CloseSearchCommand?.Execute().Subscribe();
            e.Handled = true;
        }
    }

    private void UpdateSearchHighlightsCanvas()
    {
        if (_pdfViewerControl == null)
            return;

        _pdfViewerControl.ClearSearchHighlights();

        if (DataContext is not MainWindowViewModel viewModel)
            return;

        foreach (var rect in viewModel.CurrentPageSearchHighlights)
        {
            _pdfViewerControl.AddSearchHighlight(rect);
        }
    }

    private void OnRedactionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateRedactionOverlays();
    }

    private void OnAnnotationsChanged(object? sender, EventArgs e)
    {
        _pdfViewerControl ??= this.FindControl<PdfViewerControl>("PdfViewerControl");
        if (_pdfViewerControl?.Document == null ||
            _pdfViewerControl.CurrentPage < 1 ||
            _pdfViewerControl.CurrentPage > _pdfViewerControl.Document.PageCount)
        {
            if (_pdfViewerControl != null)
                _pdfViewerControl.Annotations = null;
            return;
        }

        _pdfViewerControl.Annotations = _pdfViewerControl.Document
            .GetPage(_pdfViewerControl.CurrentPage)
            .GetAnnotations();
    }

    private void UpdateRedactionOverlays()
    {
        if (_pdfViewerControl == null)
            return;

        _pdfViewerControl.ClearPendingRedactions();
        _pdfViewerControl.ClearAppliedRedactions();

        if (DataContext is not MainWindowViewModel viewModel)
            return;

        var currentPage = viewModel.CurrentPageIndex + 1; // DisplayPageNumber is 1-based

        // Draw pending redactions (red dashed border)
        foreach (var pending in viewModel.RedactionWorkflow.GetPendingForPage(currentPage))
        {
            _pdfViewerControl.AddPendingRedaction(pending.PageArea);
        }

        // Draw applied redactions (black solid rectangle)
        foreach (var applied in viewModel.RedactionWorkflow.GetAppliedForPage(currentPage))
        {
            _pdfViewerControl.AddAppliedRedaction(applied.PageArea);
        }
    }

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        // Sidebar toggles. These Ctrl+Shift combos are checked FIRST, before the
        // plain Ctrl+O handler below (which doesn't exclude Shift and would
        // otherwise swallow Ctrl+Shift+O). (#369)
        // Ctrl+Shift+O: toggle the outline / bookmarks sidebar
        if (e.Key == Key.O && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            viewModel.ToggleOutlineCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+T: toggle the page-previews / thumbnails sidebar
        if (e.Key == Key.T && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            viewModel.ToggleThumbnailsCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+O: Open file
        if (e.Key == Key.O && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.OpenFileCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+S: Save file
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            viewModel.SaveFileCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+S: Save As
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            viewModel.SaveAsCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+W: Close document
        if (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.CloseDocumentCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+P: Print
        if (e.Key == Key.P && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.PrintCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+E: Export current page (menu advertises InputGesture="Ctrl+E"; the
        // key was previously unwired — display-only — so it did nothing). (#827)
        if (e.Key == Key.E && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.ExportCurrentPageCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+, : Preferences (menu advertises InputGesture="Ctrl+,"; the key was
        // previously unwired — display-only — so it did nothing). (#827)
        if (e.Key == Key.OemComma && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.ShowPreferencesCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // F1: Show keyboard shortcuts
        if (e.Key == Key.F1)
        {
            viewModel.ShowShortcutsCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+F: Toggle search bar (and put focus in the input so the
        // user can type immediately). Without the focus hop the search
        // bar appears but keystrokes go to whatever was focused before
        // — which looks like "search doesn't do anything".
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.ToggleSearchCommand?.Execute().Subscribe();
            if (viewModel.IsSearchVisible)
            {
                var searchBox = this.FindControl<TextBox>("SearchTextBox");
                // The Border that hosts the TextBox just toggled
                // IsVisible — wait for the layout pass to finish before
                // we try to focus it.
                Dispatcher.UIThread.Post(() =>
                {
                    searchBox?.Focus();
                    searchBox?.SelectAll();
                }, DispatcherPriority.Background);
            }
            e.Handled = true;
            return;
        }

        // F3: Find next
        if (e.Key == Key.F3 && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            viewModel.FindNextCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Shift+F3: Find previous
        if (e.Key == Key.F3 && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            viewModel.FindPreviousCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Escape: Close search if visible
        if (e.Key == Key.Escape && viewModel.IsSearchVisible)
        {
            viewModel.CloseSearchCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Enter: Apply (mark) the current redaction (menu advertises
        // InputGesture="Enter"; the key was previously unwired — ApplyRedaction
        // only ever fired from a pointer draw). Guarded so it never steals Enter
        // from text entry (search box, form fields set Handled first, but a
        // focused editor here is a hard skip) and only acts in redaction mode,
        // keeping Enter a no-op everywhere else. (#827)
        if ((e.Key == Key.Enter || e.Key == Key.Return) &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Shift) &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Alt) &&
            viewModel.IsRedactionMode)
        {
            if (FocusManager.GetFocusedElement() is TextBox or ComboBox)
                return;

            viewModel.ApplyRedactionCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+L: Rotate page left
        if (e.Key == Key.L && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.RotatePageLeftCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // R (unmodified): Toggle redaction mode (B2 keyboard shortcut)
        if (e.Key == Key.R && !e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            viewModel.ToggleRedactionModeCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+R: Rotate page right
        if (e.Key == Key.R && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.RotatePageRightCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+0: Actual size (100%)
        if (e.Key == Key.D0 && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.ZoomActualSizeCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+1: Fit width
        if (e.Key == Key.D1 && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.ZoomFitWidthCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+2: Fit page
        if (e.Key == Key.D2 && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.ZoomFitPageCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl++: Zoom in
        if ((e.Key == Key.OemPlus || e.Key == Key.Add) && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.ZoomInCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+-: Zoom out
        if ((e.Key == Key.OemMinus || e.Key == Key.Subtract) && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            viewModel.ZoomOutCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // T (unmodified): Toggle text selection mode
        if (e.Key == Key.T && !e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            // Skip if TextBox is focused (e.g., search box)
            var focusedElement = FocusManager.GetFocusedElement();
            if (focusedElement is TextBox)
                return;

            viewModel.ToggleTextSelectionModeCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Page Down / Down Arrow: Next page
        if (e.Key == Key.PageDown || (e.Key == Key.Down && !e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            viewModel.NextPageCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Page Up / Up Arrow: Previous page
        if (e.Key == Key.PageUp || (e.Key == Key.Up && !e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            viewModel.PreviousPageCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Home: First page
        if (e.Key == Key.Home)
        {
            viewModel.GoToPageCommand?.Execute(0).Subscribe();
            e.Handled = true;
            return;
        }

        // End: Last page
        if (e.Key == Key.End)
        {
            var lastPage = viewModel.TotalPages - 1;
            if (lastPage >= 0)
            {
                viewModel.GoToPageCommand?.Execute(lastPage).Subscribe();
            }
            e.Handled = true;
            return;
        }

        // Ctrl+Z: Undo. The Edit menu advertises InputGesture="Ctrl+Z", which in
        // Avalonia is DISPLAY TEXT ONLY — every working shortcut in this window
        // is duplicated here by hand, and these three never were, so the menu
        // named a key that did nothing. Same defect class as #827's Ctrl+E /
        // Ctrl+, / Enter. (#1170)
        //
        // Guarded on a focused text editor and deliberately NOT marked handled
        // in that case: a window-level Ctrl+Z would otherwise swallow the
        // TextBox's own native undo in the search box and every dialog field.
        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (FocusManager.GetFocusedElement() is TextBox)
                return;

            viewModel.UndoCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+Y: Redo (the gesture the Edit menu advertises on Windows/Linux;
        // macOS uses Cmd+Shift+Z through the native menu). (#1170)
        if (e.Key == Key.Y && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (FocusManager.GetFocusedElement() is TextBox)
                return;

            viewModel.RedoCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+C: toggle continuous scroll. (#1170)
        //
        // MUST precede the plain Ctrl+C branch below, which does not exclude
        // Shift — the same ordering hazard #369 documents at the top of this
        // handler for Ctrl+Shift+O vs Ctrl+O. The Ctrl+C branch now excludes
        // Shift explicitly as well, so the two cannot fight over the key even
        // if one is later moved.
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (FocusManager.GetFocusedElement() is TextBox)
                return;

            viewModel.ToggleContinuousViewCommand?.Execute().Subscribe();
            e.Handled = true;
            return;
        }

        // Ctrl+C: Copy text
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (viewModel.IsTextSelectionMode)
            {
                viewModel.CopyTextCommand.Execute().Subscribe();
                e.Handled = true;
            }
        }
    }

    // ==================================================================================
    // PDF VIEWER CONTROL EVENT HANDLERS
    // ==================================================================================

    private void OnRedactionDrawn(object? sender, RedactionDrawnEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        // The PdfViewerControl provides a page-scoped, tagged rectangle.
        // The view model backfills legacy Rect/DPI properties from this value.
        viewModel.CurrentRedactionPageArea = e.PageArea;

        // Automatically apply the redaction when selection is completed
        if (e.PageArea.Width > 5 && e.PageArea.Height > 5)
        {
            viewModel.ApplyRedactionCommand.Execute().Subscribe();
        }
    }

    /// <summary>
    /// Internal link clicked in the page area. The destination page is
    /// 1-based; the VM tracks 0-based CurrentPageIndex.
    /// </summary>
    private void OnLinkClicked(object? sender, LinkClickedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        var idx = e.PageNumber - 1;
        if (idx < 0 || idx >= viewModel.TotalPages) return;
        viewModel.CurrentPageIndex = idx;
    }

    /// <summary>External (http/https/mailto) link click (#625) — confirmation and navigation live in the VM.</summary>
    private void OnExternalLinkClicked(object? sender, ExternalLinkClickedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        viewModel.OpenExternalLinkCommand.Execute(e.Uri).Subscribe();
    }

    /// <summary>Click on a link excise refuses to run (#625) — /Launch, /GoToE, /GoToR, disallowed URI scheme.</summary>
    private void OnDangerousLinkClicked(object? sender, DangerousLinkClickedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        viewModel.ShowDangerousLinkRefusalCommand.Execute(e.ActionType).Subscribe();
    }

    /// <summary>Pointer hover over a link (#625) — status-bar target text.</summary>
    private void OnLinkHovered(object? sender, LinkHoveredEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        viewModel.SetHoveredLinkTarget(e.DisplayText);
    }


    /// <summary>
    /// #1074 — show the hovered annotation's author and /Contents. Mirrors
    /// <see cref="OnLinkHovered"/> exactly; the viewer has already decided that
    /// a Link takes precedence, so these two never both carry text.
    /// </summary>
    private void OnAnnotationHovered(object? sender, AnnotationHoveredEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        viewModel.SetHoveredAnnotationInfo(e.DisplayText);
    }

    private void OnTextSelected(object? sender, TextSelectedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        // Pre-fix this set CurrentTextSelectionArea (a 2D rect) and asked
        // the VM to re-extract text within the rect. The new text-line
        // selection path computes the actual text in the control via
        // letter hit-testing, so the event already carries the joined
        // string — feed it directly.
        viewModel.CurrentTextSelectionArea = e.Area;
        viewModel.CurrentTextSelectionPageArea =
            e.Area.Width > 0 && e.Area.Height > 0
                ? PdfPageRect.ViewerDips(
                    viewModel.CurrentPage,
                    e.Area.X,
                    e.Area.Y,
                    e.Area.Width,
                    e.Area.Height,
                    MainWindowViewModel.DefaultViewerRenderDpi)
                : null;
        if (!string.IsNullOrEmpty(e.Text))
        {
            _ = viewModel.SetSelectedTextAndCopyAsync(e.Text);
        }
        else
        {
            viewModel.SelectedText = string.Empty;
        }
    }

    private void OnPageChanged(object? sender, PageChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        // #1554: while the viewer swaps documents it reports pages of its own
        // rebuild; the incoming tab's page is the view model's, not those.
        if (_switchingSession)
            return;

        // Update ViewModel page index (convert from 1-based to 0-based)
        viewModel.CurrentPageIndex = e.PageNumber - 1;
    }

    /// <summary>
    /// Handle an AcroForm field edit. The viewer has already mutated the
    /// PdfField. We tell the VM so it can mark the document dirty and
    /// re-render the page so the appearance reflects the new value (the
    /// form field overlay sits on top, but if /NeedAppearances is honored
    /// by the renderer the bitmap underneath will refresh too).
    /// </summary>
    private void OnFormFieldEdited(object? sender, FormFieldEditedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        viewModel.OnFormFieldEdited(e.FieldName, e.NewValue);
    }

    /// <summary>
    /// User finished drag-defining a new form-field rect in authoring mode.
    /// </summary>
    private void OnFormFieldRectDrawn(object? sender, FormFieldRectDrawnEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        viewModel.OnFormFieldRectDrawn(e.Rect, e.PageNumber);
    }

    /// <summary>
    /// User finished a free-form stroke in draw mode — it becomes Ink (#934 D).
    /// </summary>
    private async void OnAnnotationPathDrawn(object? sender, AnnotationPathDrawnEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        await viewModel.OnAnnotationPathDrawnAsync(e.Strokes, e.PageNumber);
    }

    private void OnTypewriterTextCreated(object? sender, TypewriterTextCreatedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        viewModel.OnTypewriterTextCreated(e.Rect, e.PageNumber);
    }

    private void OnTypewriterTextEdited(object? sender, TypewriterTextEditedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        viewModel.OnTypewriterTextEdited(e.OperationId, e.Text, e.PageNumber);
    }

    private void OnTypewriterTextBoundsChanged(object? sender, TypewriterTextBoundsChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        viewModel.OnTypewriterTextBoundsChanged(e.OperationId, e.Rect, e.PageNumber);
    }

    private void OnTypewriterTextDeleted(object? sender, TypewriterTextDeletedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        viewModel.OnTypewriterTextDeleted(e.OperationId);
    }

    /// <summary>
    /// Toolbar combo selection — translate the selected ComboBoxItem to the
    /// corresponding PdfFieldType for the next drag.
    /// </summary>
    private void OnFormFieldTypeChanged(object? sender, global::Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item) return;
        var content = item.Content?.ToString() ?? "Text";
        viewModel.FormAuthoringFieldType = content switch
        {
            "Checkbox" => Excise.Core.Document.PdfFieldType.Button,
            "Choice"   => Excise.Core.Document.PdfFieldType.Choice,
            "Signature"=> Excise.Core.Document.PdfFieldType.Signature,
            _          => Excise.Core.Document.PdfFieldType.Text,
        };
    }

    /// <summary>
    /// Handle toast notifications from the ViewModel.
    /// Shows an InfoBar notification for 5 seconds, then auto-dismisses.
    /// </summary>
    private void OnToastRequested(object? sender, Excise.App.Services.ToastService.ToastEventArgs e)
    {
        try
        {
            var infoBar = this.FindControl<FluentAvalonia.UI.Controls.FAInfoBar>("ToastInfoBar");
            if (infoBar == null)
                return;

            // Set severity based on toast severity level
            infoBar.Severity = e.Severity switch
            {
                Excise.App.Services.ToastService.ToastSeverity.Error => FluentAvalonia.UI.Controls.FAInfoBarSeverity.Error,
                Excise.App.Services.ToastService.ToastSeverity.Warning => FluentAvalonia.UI.Controls.FAInfoBarSeverity.Warning,
                Excise.App.Services.ToastService.ToastSeverity.Success => FluentAvalonia.UI.Controls.FAInfoBarSeverity.Success,
                _ => FluentAvalonia.UI.Controls.FAInfoBarSeverity.Informational
            };

            // Set message and optional details
            infoBar.Title = e.Message;
            infoBar.Message = e.Details ?? string.Empty;

            // Show the InfoBar
            infoBar.IsOpen = true;

            // Auto-dismiss after 5 seconds using a single reusable UI-thread
            // timer. Restarting an existing timer (rather than spawning a new
            // System.Timers.Timer per toast) means at most one pending dismiss
            // exists, it runs on the dispatcher, and it cannot leak a callback
            // past the dispatcher's lifetime — which is what made headless GUI
            // tests flaky.
            _toastTimer ??= CreateToastTimer(infoBar);
            _toastTimer.Stop();
            _toastTimer.Start();
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"Error displaying toast: {ex.Message}");
        }
    }

    /// <summary>Close the toast now (it belonged to the session shown before).</summary>
    private void CloseToast()
    {
        _toastTimer?.Stop();
        if (this.FindControl<FluentAvalonia.UI.Controls.FAInfoBar>("ToastInfoBar") is { } infoBar)
            infoBar.IsOpen = false;
    }

    /// <summary>
    /// Build the reusable toast auto-dismiss timer. Ticks once (Stop() in the
    /// handler), on the UI thread, so it is inherently bound to the dispatcher's
    /// lifetime — no cross-thread marshaling, nothing left to drain at teardown.
    /// </summary>
    private DispatcherTimer CreateToastTimer(FluentAvalonia.UI.Controls.FAInfoBar infoBar)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (s, args) =>
        {
            timer.Stop();
            infoBar.IsOpen = false;
        };
        return timer;
    }
}
