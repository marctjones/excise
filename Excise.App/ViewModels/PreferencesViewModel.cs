using Excise.App.Models;
using ReactiveUI;
using System;
using System.Reactive;
using System.Threading.Tasks;

namespace Excise.App.ViewModels;

internal class PreferencesViewModel : ViewModelBase
{
    private Excise.Core.Text.ReadingOrderStrategy _readingOrderStrategy =
        Excise.Core.Text.ReadingOrderStrategy.ColumnAware;
    private Excise.Core.Text.WhitespaceMode _whitespaceMode =
        Excise.Core.Text.WhitespaceMode.Smart;
    private RedactionPreferences _redactionPreferences = new();
    private Excise.App.Services.Printing.PrintScalingMode _printScaling =
        Excise.App.Services.Printing.PrintScalingMode.ShrinkOversized;

    // Performance (Balanced until loaded).
    private PerformancePreset _performancePreset = PerformancePreset.Balanced;
    private int _tileCacheBudgetMb;
    private int _singlePageCachedPages;
    private bool _thumbnailPrewarm;
    private int _thumbnailKeepMargin;
    private bool _softCacheTrims;
    private int _idleTrimSeconds;
    private int _renderThreads;
    private bool _syncingPerformance;
    private string _workingSetText = "—";
    private string _managedHeapText = "—";
    private string _tileCacheText = "—";

    public PreferencesViewModel()
    {
        CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        ResetToDefaultsCommand = ReactiveCommand.CreateFromTask(ResetToDefaultsConfirmedAsync);
        SetPerformanceFields(PerformanceSettings.Balanced);
    }

    // ── Documents (#1463) ────────────────────────────────────────────────────

    private DocumentOpenMode _documentOpenMode = DocumentOpenMode.Automatic;

    /// <summary>Where a document opens when the window already shows one.</summary>
    public DocumentOpenMode[] DocumentOpenModeOptions { get; } =
    [
        DocumentOpenMode.Automatic,
        DocumentOpenMode.NewWindow,
        DocumentOpenMode.NewTab,
        DocumentOpenMode.ReplaceCurrent,
    ];

    public DocumentOpenMode SelectedDocumentOpenMode
    {
        get => _documentOpenMode;
        set => this.RaiseAndSetIfChanged(ref _documentOpenMode, value);
    }

    // ── Appearance (#2002) ───────────────────────────────────────────────────

    private AppearanceMode _appearance = AppearanceMode.System;

    public AppearanceMode[] AppearanceOptions { get; } =
    [
        AppearanceMode.System,
        AppearanceMode.Light,
        AppearanceMode.Dark,
    ];

    public AppearanceMode SelectedAppearance
    {
        get => _appearance;
        set => this.RaiseAndSetIfChanged(ref _appearance, value);
    }

    // ── Forms (#1570) ────────────────────────────────────────────────────────

    private bool _runFormCalc = true;

    /// <summary>Run a dynamic XFA form's FormCalc calculations when it opens.</summary>
    public bool RunFormCalc
    {
        get => _runFormCalc;
        set => this.RaiseAndSetIfChanged(ref _runFormCalc, value);
    }

    // ── Performance ──────────────────────────────────────────────────────────

    /// <summary>AOT-safe preset list; see the note on <see cref="ReadingOrderStrategyOptions"/>.</summary>
    public PerformancePreset[] PerformancePresetOptions { get; } = Enum.GetValues<PerformancePreset>();

    /// <summary>
    /// The preset shown. Choosing a named preset overwrites every Advanced
    /// field; choosing Custom changes nothing. Editing a field re-derives it,
    /// so it reads Custom unless the values still equal a named preset.
    /// </summary>
    public PerformancePreset SelectedPerformancePreset
    {
        get => _performancePreset;
        set
        {
            this.RaiseAndSetIfChanged(ref _performancePreset, value);
            if (!_syncingPerformance && value != PerformancePreset.Custom)
                SetPerformanceFields(PerformanceSettings.For(value));
        }
    }

    public int TileCacheBudgetMb
    {
        get => _tileCacheBudgetMb;
        set => SetPerformanceField(ref _tileCacheBudgetMb, value);
    }

    public int SinglePageCachedPages
    {
        get => _singlePageCachedPages;
        set => SetPerformanceField(ref _singlePageCachedPages, value);
    }

    public bool ThumbnailPrewarm
    {
        get => _thumbnailPrewarm;
        set => SetPerformanceField(ref _thumbnailPrewarm, value);
    }

    public int ThumbnailKeepMargin
    {
        get => _thumbnailKeepMargin;
        set => SetPerformanceField(ref _thumbnailKeepMargin, value);
    }

    public bool SoftCacheTrims
    {
        get => _softCacheTrims;
        set => SetPerformanceField(ref _softCacheTrims, value);
    }

    public int IdleTrimSeconds
    {
        get => _idleTrimSeconds;
        set => SetPerformanceField(ref _idleTrimSeconds, value);
    }

    public int RenderThreads
    {
        get => _renderThreads;
        set => SetPerformanceField(ref _renderThreads, value);
    }

    /// <summary>Upper bound for <see cref="RenderThreads"/> on this machine.</summary>
    public int MaxRenderThreads => PerformanceSettings.MaxRenderThreads;

    /// <summary>The dialog's performance values, clamped to their ranges.</summary>
    public PerformanceSettings BuildPerformanceSettings() => new PerformanceSettings(
        TileCacheBudgetMb, SinglePageCachedPages, ThumbnailPrewarm, ThumbnailKeepMargin,
        SoftCacheTrims, IdleTrimSeconds, RenderThreads).Clamped();

    public string WorkingSetText
    {
        get => _workingSetText;
        private set => this.RaiseAndSetIfChanged(ref _workingSetText, value);
    }

    public string ManagedHeapText
    {
        get => _managedHeapText;
        private set => this.RaiseAndSetIfChanged(ref _managedHeapText, value);
    }

    public string TileCacheText
    {
        get => _tileCacheText;
        private set => this.RaiseAndSetIfChanged(ref _tileCacheText, value);
    }

    /// <summary>Where the viewer's tile-cache bytes come from; null when no viewer is attached.</summary>
    internal Func<long?>? TileCacheBytesSource { get; set; }

    /// <summary>
    /// Sample the live memory readout once. The Preferences window calls this
    /// from a timer that exists only while the dialog is open.
    /// </summary>
    public void RefreshMemoryReadout()
    {
        using (var process = System.Diagnostics.Process.GetCurrentProcess())
            WorkingSetText = FormatMegabytes(process.WorkingSet64);
        ManagedHeapText = FormatMegabytes(GC.GetTotalMemory(forceFullCollection: false));
        var tiles = TileCacheBytesSource?.Invoke();
        TileCacheText = tiles is { } bytes ? FormatMegabytes(bytes) : "no viewer";
    }

    private static string FormatMegabytes(long bytes) =>
        string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024.0):N0} MB");

    private void SetPerformanceField<T>(
        ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value))
            return;
        // Forward the caller's name: RaiseAndSetIfChanged's own [CallerMemberName]
        // would otherwise name this helper, and no binding would ever update.
        this.RaiseAndSetIfChanged(ref field, value, propertyName);
        if (_syncingPerformance)
            return;
        _syncingPerformance = true;
        try
        {
            SelectedPerformancePreset = new PerformanceSettings(
                TileCacheBudgetMb, SinglePageCachedPages, ThumbnailPrewarm, ThumbnailKeepMargin,
                SoftCacheTrims, IdleTrimSeconds, RenderThreads).DetectPreset();
        }
        finally
        {
            _syncingPerformance = false;
        }
    }

    private void SetPerformanceFields(PerformanceSettings settings)
    {
        _syncingPerformance = true;
        try
        {
            TileCacheBudgetMb = settings.TileCacheBudgetMb;
            SinglePageCachedPages = settings.SinglePageCachedPages;
            ThumbnailPrewarm = settings.ThumbnailPrewarm;
            ThumbnailKeepMargin = settings.ThumbnailKeepMargin;
            SoftCacheTrims = settings.SoftCacheTrims;
            IdleTrimSeconds = settings.IdleTrimSeconds;
            RenderThreads = settings.RenderThreads;
            SelectedPerformancePreset = settings.DetectPreset();
        }
        finally
        {
            _syncingPerformance = false;
        }
    }

    // Text-selection reading-order strategy (#774).
    // Enum.GetValues<T>() rather than the Type overload: the latter carries
    // [RequiresDynamicCode] and warns IL3050 under AOT, because it may have to
    // build the array type at runtime. The generic form is resolved statically
    // and is what ships now that macOS and Linux publish Native AOT (#906).
    public Excise.Core.Text.ReadingOrderStrategy[] ReadingOrderStrategyOptions { get; } =
        System.Enum.GetValues<Excise.Core.Text.ReadingOrderStrategy>();

    public Excise.Core.Text.ReadingOrderStrategy SelectedReadingOrderStrategy
    {
        get => _readingOrderStrategy;
        set => this.RaiseAndSetIfChanged(ref _readingOrderStrategy, value);
    }

    // Copied-text whitespace mode (paragraph/list-aware Smart vs LineFaithful).
    // Enum.GetValues<T>() rather than the Type overload: the latter carries
    // [RequiresDynamicCode] and warns IL3050 under AOT, because it may have to
    // build the array type at runtime. The generic form is resolved statically
    // and is what ships now that macOS and Linux publish Native AOT (#906).
    public Excise.Core.Text.WhitespaceMode[] WhitespaceModeOptions { get; } =
        System.Enum.GetValues<Excise.Core.Text.WhitespaceMode>();

    public Excise.Core.Text.WhitespaceMode SelectedWhitespaceMode
    {
        get => _whitespaceMode;
        set => this.RaiseAndSetIfChanged(ref _whitespaceMode, value);
    }

    // Per-carrier redaction policy (#1188/#1169). The same AOT-safe
    // Enum.GetValues<T>() form as the two above.
    public Excise.Core.Operations.CarrierScrubMode[] CarrierScrubModeOptions { get; } =
        System.Enum.GetValues<Excise.Core.Operations.CarrierScrubMode>();

    // Redaction output profile (#1586). AOT-safe Enum.GetValues<T>().
    public Excise.Core.Text.Segmentation.RedactionProfile[] RedactionProfileOptions { get; } =
        System.Enum.GetValues<Excise.Core.Text.Segmentation.RedactionProfile>();

    // Redaction width / box policy (#1189). AOT-safe Enum.GetValues<T>().
    public Excise.Core.Text.Segmentation.WidthPolicy[] WidthPolicyOptions { get; } =
        System.Enum.GetValues<Excise.Core.Text.Segmentation.WidthPolicy>();

    /// <summary>
    /// The dialog's own copy of the redaction preferences (#1840): the controls
    /// bind into it, and Save hands the main view model a copy of it.
    /// </summary>
    public RedactionPreferences RedactionPreferences
    {
        get => _redactionPreferences;
        set
        {
            this.RaiseAndSetIfChanged(ref _redactionPreferences, value);
            RaiseRedactionFields();
        }
    }

    // #2000: the controls bind these flat properties, not RedactionPreferences.X.
    // The record raises nothing when a field changes, so with changes applied as
    // they are made a binding into it would never tell anyone; a switch to the
    // Maximum profile would then wait for the close flush alone. Each setter
    // writes into the same record and raises, so the change applies at once.
    public Excise.Core.Text.Segmentation.RedactionProfile RedactionProfile
    {
        get => _redactionPreferences.Profile;
        set { if (_redactionPreferences.Profile == value) return; _redactionPreferences.Profile = value; this.RaisePropertyChanged(); }
    }

    public bool RedactionWholeWord
    {
        get => _redactionPreferences.WholeWord;
        set { if (_redactionPreferences.WholeWord == value) return; _redactionPreferences.WholeWord = value; this.RaisePropertyChanged(); }
    }

    public bool RedactionKeepAttachments
    {
        get => _redactionPreferences.KeepAttachments;
        set { if (_redactionPreferences.KeepAttachments == value) return; _redactionPreferences.KeepAttachments = value; this.RaisePropertyChanged(); }
    }

    public Excise.Core.Text.Segmentation.WidthPolicy RedactionWidth
    {
        get => _redactionPreferences.Width;
        set { if (_redactionPreferences.Width == value) return; _redactionPreferences.Width = value; this.RaisePropertyChanged(); }
    }

    public Excise.Core.Operations.CarrierScrubMode RedactionLinkUriPolicy
    {
        get => _redactionPreferences.LinkUriPolicy;
        set { if (_redactionPreferences.LinkUriPolicy == value) return; _redactionPreferences.LinkUriPolicy = value; this.RaisePropertyChanged(); }
    }

    public Excise.Core.Operations.CarrierScrubMode RedactionMetadataPolicy
    {
        get => _redactionPreferences.MetadataPolicy;
        set { if (_redactionPreferences.MetadataPolicy == value) return; _redactionPreferences.MetadataPolicy = value; this.RaisePropertyChanged(); }
    }

    private void RaiseRedactionFields()
    {
        this.RaisePropertyChanged(nameof(RedactionProfile));
        this.RaisePropertyChanged(nameof(RedactionWholeWord));
        this.RaisePropertyChanged(nameof(RedactionKeepAttachments));
        this.RaisePropertyChanged(nameof(RedactionWidth));
        this.RaisePropertyChanged(nameof(RedactionLinkUriPolicy));
        this.RaisePropertyChanged(nameof(RedactionMetadataPolicy));
    }

    // Print page scaling (#1545). AOT-safe Enum.GetValues<T>().
    public Excise.App.Services.Printing.PrintScalingMode[] PrintScalingOptions { get; } =
        System.Enum.GetValues<Excise.App.Services.Printing.PrintScalingMode>();

    public Excise.App.Services.Printing.PrintScalingMode SelectedPrintScaling
    {
        get => _printScaling;
        set => this.RaiseAndSetIfChanged(ref _printScaling, value);
    }

    // Commands
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetToDefaultsCommand { get; }

    /// <summary>Raised by <see cref="CloseCommand"/>; the window closes, flushing first.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Applies and persists every value, on the UI thread (#2000: changes apply
    /// as they are made). Wired to MainWindowViewModel.ApplySavedPreferences: the
    /// one path the old Save button used, so what is written is unchanged; only
    /// when it is written changed.
    /// </summary>
    internal Action? ApplyRequested { get; set; }

    /// <summary>Asks before Reset to Defaults; null resets without asking (tests).</summary>
    internal Func<Task<bool>>? ConfirmReset { get; set; }

    /// <summary>Apply now: the window calls this after a change settles, and always on close.</summary>
    internal void ApplyNow() => ApplyRequested?.Invoke();

    /// <summary>Properties that report state rather than hold a preference; changing them applies nothing.</summary>
    internal static bool IsReadout(string? propertyName) =>
        propertyName is nameof(WorkingSetText) or nameof(ManagedHeapText) or nameof(TileCacheText) or nameof(MaxRenderThreads);

    private async Task ResetToDefaultsConfirmedAsync()
    {
        if (ConfirmReset != null && !await ConfirmReset())
            return;
        ResetToDefaults();
    }

    private void ResetToDefaults()
    {
        SelectedReadingOrderStrategy = Excise.Core.Text.ReadingOrderStrategy.ColumnAware;
        SelectedWhitespaceMode = Excise.Core.Text.WhitespaceMode.Smart;
        RedactionPreferences = new();
        SelectedPrintScaling = Excise.App.Services.Printing.PrintScalingMode.ShrinkOversized;
        SetPerformanceFields(PerformanceSettings.Balanced);
        SelectedDocumentOpenMode = DocumentOpenMode.Automatic;
        SelectedAppearance = AppearanceMode.System;
        RunFormCalc = true;
    }

    public void LoadFromMainViewModel(MainWindowViewModel mainViewModel)
    {
        SelectedReadingOrderStrategy = mainViewModel.ReadingOrderStrategy;
        SelectedWhitespaceMode = mainViewModel.WhitespaceMode;
        RedactionPreferences = mainViewModel.RedactionPreferences with { };
        SelectedPrintScaling = mainViewModel.PrintScaling;
        SetPerformanceFields(mainViewModel.PerformanceSettings);
        TileCacheBytesSource = mainViewModel.ViewerTileCacheResidentBytesProvider;
        SelectedDocumentOpenMode = mainViewModel.DocumentOpenMode;
        SelectedAppearance = mainViewModel.Appearance;
        RunFormCalc = mainViewModel.RunFormCalc;
    }

    public void SaveToMainViewModel(MainWindowViewModel mainViewModel)
    {
        mainViewModel.ReadingOrderStrategy = SelectedReadingOrderStrategy;
        mainViewModel.WhitespaceMode = SelectedWhitespaceMode;
        mainViewModel.RedactionPreferences = RedactionPreferences with { };
        mainViewModel.PrintScaling = SelectedPrintScaling;
        mainViewModel.ApplyPerformanceSettings(BuildPerformanceSettings());
        mainViewModel.DocumentOpenMode = SelectedDocumentOpenMode;
        mainViewModel.Appearance = SelectedAppearance;
        mainViewModel.RunFormCalc = RunFormCalc;
    }
}
