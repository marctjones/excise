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
    public Excise.Avalonia.Services.ReadingOrderStrategy[] ReadingOrderStrategyOptions { get; } =
        (Excise.Avalonia.Services.ReadingOrderStrategy[])
            System.Enum.GetValues(typeof(Excise.Avalonia.Services.ReadingOrderStrategy));

    public Excise.Avalonia.Services.ReadingOrderStrategy SelectedReadingOrderStrategy
    {
        get => _readingOrderStrategy;
        set => this.RaiseAndSetIfChanged(ref _readingOrderStrategy, value);
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
    }

    private void CloseWindow()
    {
        // This will be handled by the window
    }

    public void LoadFromMainViewModel(MainWindowViewModel mainViewModel)
    {
        RenderCacheMax = mainViewModel.RenderCacheMax;
        SelectedReadingOrderStrategy = mainViewModel.ReadingOrderStrategy;
    }

    public void SaveToMainViewModel(MainWindowViewModel mainViewModel)
    {
        mainViewModel.RenderCacheMax = RenderCacheMax;
        mainViewModel.ReadingOrderStrategy = SelectedReadingOrderStrategy;
    }
}
