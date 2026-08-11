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
    private int _renderCacheMax = 20;
    private Excise.Avalonia.Services.ReadingOrderStrategy _readingOrderStrategy =
        Excise.Avalonia.Services.ReadingOrderStrategy.ColumnAware;
    private Excise.Avalonia.Services.WhitespaceMode _whitespaceMode =
        Excise.Avalonia.Services.WhitespaceMode.Smart;

    public PreferencesViewModel()
    {
        SaveCommand = ReactiveCommand.Create(Save);
        CancelCommand = ReactiveCommand.Create(Cancel);
        ResetToDefaultsCommand = ReactiveCommand.Create(ResetToDefaults);
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

    // Rendering Properties
    public int RenderCacheMax
    {
        get => _renderCacheMax;
        set => this.RaiseAndSetIfChanged(ref _renderCacheMax, value);
    }

    // Text-selection reading-order strategy (#774).
    // Enum.GetValues<T>() rather than the Type overload: the latter carries
    // [RequiresDynamicCode] and warns IL3050 under AOT, because it may have to
    // build the array type at runtime. The generic form is resolved statically
    // and is what ships now that macOS and Linux publish Native AOT (#906).
    public Excise.Avalonia.Services.ReadingOrderStrategy[] ReadingOrderStrategyOptions { get; } =
        System.Enum.GetValues<Excise.Avalonia.Services.ReadingOrderStrategy>();

    public Excise.Avalonia.Services.ReadingOrderStrategy SelectedReadingOrderStrategy
    {
        get => _readingOrderStrategy;
        set => this.RaiseAndSetIfChanged(ref _readingOrderStrategy, value);
    }

    // Copied-text whitespace mode (paragraph/list-aware Smart vs LineFaithful).
    // Enum.GetValues<T>() rather than the Type overload: the latter carries
    // [RequiresDynamicCode] and warns IL3050 under AOT, because it may have to
    // build the array type at runtime. The generic form is resolved statically
    // and is what ships now that macOS and Linux publish Native AOT (#906).
    public Excise.Avalonia.Services.WhitespaceMode[] WhitespaceModeOptions { get; } =
        System.Enum.GetValues<Excise.Avalonia.Services.WhitespaceMode>();

    public Excise.Avalonia.Services.WhitespaceMode SelectedWhitespaceMode
    {
        get => _whitespaceMode;
        set => this.RaiseAndSetIfChanged(ref _whitespaceMode, value);
    }

    // Commands
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetToDefaultsCommand { get; }

    public bool DialogResult { get; private set; }

    private void Save()
    {
        DialogResult = true;
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
        RenderCacheMax = 20;
        SelectedReadingOrderStrategy = Excise.Avalonia.Services.ReadingOrderStrategy.ColumnAware;
        SelectedWhitespaceMode = Excise.Avalonia.Services.WhitespaceMode.Smart;
    }

    private void CloseWindow()
    {
        // This will be handled by the window
    }

    public void LoadFromMainViewModel(MainWindowViewModel mainViewModel)
    {
        RenderCacheMax = mainViewModel.RenderCacheMax;
        SelectedReadingOrderStrategy = mainViewModel.ReadingOrderStrategy;
        SelectedWhitespaceMode = mainViewModel.WhitespaceMode;
    }

    public void SaveToMainViewModel(MainWindowViewModel mainViewModel)
    {
        mainViewModel.RenderCacheMax = RenderCacheMax;
        mainViewModel.ReadingOrderStrategy = SelectedReadingOrderStrategy;
        mainViewModel.WhitespaceMode = SelectedWhitespaceMode;
    }
}
