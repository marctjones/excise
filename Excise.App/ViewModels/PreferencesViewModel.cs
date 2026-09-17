using Excise.App.Models;
using ReactiveUI;
using System;
using System.Reactive;

namespace Excise.App.ViewModels;

public class PreferencesViewModel : ViewModelBase
{
    private string _ocrLanguages = "eng";
    private int _ocrBaseDpi = 350;
    private int _ocrHighDpi = 450;
    private double _ocrLowConfidence = 0.6;
    private bool _ocrPreprocess = true;
    private bool _ocrBinarize = true;
    private double _ocrDenoiseRadius = 0.8;
    private Excise.Core.Text.ReadingOrderStrategy _readingOrderStrategy =
        Excise.Core.Text.ReadingOrderStrategy.ColumnAware;
    private Excise.Core.Text.WhitespaceMode _whitespaceMode =
        Excise.Core.Text.WhitespaceMode.Smart;
    private Excise.Core.Operations.CarrierScrubMode _linkUriCarrierPolicy =
        Excise.Core.Operations.CarrierScrubMode.Strip;
    private Excise.Core.Operations.CarrierScrubMode _metadataCarrierPolicy =
        Excise.Core.Operations.CarrierScrubMode.Strip;
    private bool _redactionWholeWord;
    private Excise.App.Services.Printing.PrintScalingMode _printScaling =
        Excise.App.Services.Printing.PrintScalingMode.ShrinkOversized;
    private Excise.Core.Text.Segmentation.WidthPolicy _redactionWidthPolicy =
        Excise.Core.Text.Segmentation.WidthPolicy.CollapsePreserveLayout;

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
        SaveCommand = ReactiveCommand.Create(Save);
        CancelCommand = ReactiveCommand.Create(Cancel);
        ResetToDefaultsCommand = ReactiveCommand.Create(ResetToDefaults);
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

    // OCR Properties
    public string OcrLanguages
    {
        get => _ocrLanguages;
        set => this.RaiseAndSetIfChanged(ref _ocrLanguages, value);
    }

    public int OcrBaseDpi
    {
        get => _ocrBaseDpi;
        set => this.RaiseAndSetIfChanged(ref _ocrBaseDpi, value);
    }

    public int OcrHighDpi
    {
        get => _ocrHighDpi;
        set => this.RaiseAndSetIfChanged(ref _ocrHighDpi, value);
    }

    public double OcrLowConfidence
    {
        get => _ocrLowConfidence;
        set => this.RaiseAndSetIfChanged(ref _ocrLowConfidence, value);
    }

    public bool OcrPreprocess
    {
        get => _ocrPreprocess;
        set => this.RaiseAndSetIfChanged(ref _ocrPreprocess, value);
    }

    public bool OcrBinarize
    {
        get => _ocrBinarize;
        set => this.RaiseAndSetIfChanged(ref _ocrBinarize, value);
    }

    public double OcrDenoiseRadius
    {
        get => _ocrDenoiseRadius;
        set => this.RaiseAndSetIfChanged(ref _ocrDenoiseRadius, value);
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

    public Excise.Core.Operations.CarrierScrubMode SelectedLinkUriCarrierPolicy
    {
        get => _linkUriCarrierPolicy;
        set => this.RaiseAndSetIfChanged(ref _linkUriCarrierPolicy, value);
    }

    public Excise.Core.Operations.CarrierScrubMode SelectedMetadataCarrierPolicy
    {
        get => _metadataCarrierPolicy;
        set => this.RaiseAndSetIfChanged(ref _metadataCarrierPolicy, value);
    }

    /// <summary>Whole-word matching for text redaction (#1052).</summary>
    public bool RedactionWholeWord
    {
        get => _redactionWholeWord;
        set => this.RaiseAndSetIfChanged(ref _redactionWholeWord, value);
    }

    // Redaction width / box policy (#1189). AOT-safe Enum.GetValues<T>().
    public Excise.Core.Text.Segmentation.WidthPolicy[] WidthPolicyOptions { get; } =
        System.Enum.GetValues<Excise.Core.Text.Segmentation.WidthPolicy>();

    public Excise.Core.Text.Segmentation.WidthPolicy SelectedRedactionWidthPolicy
    {
        get => _redactionWidthPolicy;
        set => this.RaiseAndSetIfChanged(ref _redactionWidthPolicy, value);
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
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetToDefaultsCommand { get; }

    public bool DialogResult { get; private set; }

    /// <summary>
    /// Invoked by <see cref="SaveCommand"/> on the UI thread, before the window
    /// closes: apply and persist the values (see MainWindowViewModel.ApplySavedPreferences).
    /// </summary>
    internal Action? SaveRequested { get; set; }

    private void Save()
    {
        DialogResult = true;
        SaveRequested?.Invoke();
        CloseWindow();
    }

    private void Cancel()
    {
        DialogResult = false;
        CloseWindow();
    }

    private void ResetToDefaults()
    {
        OcrLanguages = "eng";
        OcrBaseDpi = 350;
        OcrHighDpi = 450;
        OcrLowConfidence = 0.6;
        OcrPreprocess = true;
        OcrBinarize = true;
        OcrDenoiseRadius = 0.8;
        SelectedReadingOrderStrategy = Excise.Core.Text.ReadingOrderStrategy.ColumnAware;
        SelectedWhitespaceMode = Excise.Core.Text.WhitespaceMode.Smart;
        SelectedLinkUriCarrierPolicy = Excise.Core.Operations.CarrierScrubMode.Strip;
        SelectedMetadataCarrierPolicy = Excise.Core.Operations.CarrierScrubMode.Strip;
        RedactionWholeWord = false;
        SelectedRedactionWidthPolicy = Excise.Core.Text.Segmentation.WidthPolicy.CollapsePreserveLayout;
        SelectedPrintScaling = Excise.App.Services.Printing.PrintScalingMode.ShrinkOversized;
        SetPerformanceFields(PerformanceSettings.Balanced);
        SelectedDocumentOpenMode = DocumentOpenMode.Automatic;
    }

    private void CloseWindow()
    {
        // This will be handled by the window
    }

    public void LoadFromMainViewModel(MainWindowViewModel mainViewModel)
    {
        SelectedReadingOrderStrategy = mainViewModel.ReadingOrderStrategy;
        SelectedWhitespaceMode = mainViewModel.WhitespaceMode;
        SelectedLinkUriCarrierPolicy = mainViewModel.LinkUriCarrierPolicy;
        SelectedMetadataCarrierPolicy = mainViewModel.MetadataCarrierPolicy;
        RedactionWholeWord = mainViewModel.RedactionWholeWord;
        SelectedRedactionWidthPolicy = mainViewModel.RedactionWidthPolicy;
        SelectedPrintScaling = mainViewModel.PrintScaling;
        SetPerformanceFields(mainViewModel.PerformanceSettings);
        TileCacheBytesSource = mainViewModel.ViewerTileCacheResidentBytesProvider;
        SelectedDocumentOpenMode = mainViewModel.DocumentOpenMode;
    }

    public void SaveToMainViewModel(MainWindowViewModel mainViewModel)
    {
        mainViewModel.ReadingOrderStrategy = SelectedReadingOrderStrategy;
        mainViewModel.WhitespaceMode = SelectedWhitespaceMode;
        mainViewModel.LinkUriCarrierPolicy = SelectedLinkUriCarrierPolicy;
        mainViewModel.MetadataCarrierPolicy = SelectedMetadataCarrierPolicy;
        mainViewModel.RedactionWholeWord = RedactionWholeWord;
        mainViewModel.RedactionWidthPolicy = SelectedRedactionWidthPolicy;
        mainViewModel.PrintScaling = SelectedPrintScaling;
        mainViewModel.ApplyPerformanceSettings(BuildPerformanceSettings());
        mainViewModel.DocumentOpenMode = SelectedDocumentOpenMode;
    }
}
