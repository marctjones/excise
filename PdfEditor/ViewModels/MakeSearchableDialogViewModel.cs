using Pdfe.Ocr;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;

namespace PdfEditor.ViewModels;

/// <summary>
/// ViewModel for the "Make Searchable" dialog (#658). Collects the OCR
/// language and force-reprocess option, then drives the run through an
/// injected delegate so this class stays decoupled from Pdfe.Ocr /
/// PdfDocument and is unit-testable without a real tesseract install or
/// an open document — see <c>MainWindowViewModel.Searchable.cs</c> for the
/// delegate that wires this up to the real <see cref="PdfSearchableConverter"/>.
/// </summary>
public sealed class MakeSearchableDialogViewModel : ReactiveObject
{
    /// <summary>
    /// A short list of common tesseract language codes, offered as presets.
    /// Not a discovered list of installed language packs (#658 explicitly
    /// scopes that out as overkill) — free text in <see cref="Language"/>
    /// is always accepted, e.g. "eng+spa".
    /// </summary>
    public static IReadOnlyList<string> LanguagePresets { get; } = new[]
    {
        "eng", "deu", "fra", "spa", "ita", "por", "eng+spa", "eng+deu", "eng+fra",
    };

    /// <summary>
    /// Matches the CLI's exact install-hint text
    /// (<c>Pdfe.Cli/Program.cs</c>, <c>CreateMakeSearchableCommand</c>) so
    /// the GUI and CLI never drift on how they tell the user to fix this.
    /// </summary>
    public const string TesseractMissingMessage =
        "tesseract CLI not found on PATH. Install with `apt install tesseract-ocr` " +
        "(or your platform's equivalent).";

    private readonly Func<string, bool, IProgress<(int Done, int Total)>, CancellationToken, Task<SearchableDocumentResult>> _runOcr;
    private readonly BehaviorSubject<bool> _canStart;
    private CancellationTokenSource? _cts;

    public bool TesseractAvailable { get; }

    private string _language = "eng";
    public string Language
    {
        get => _language;
        set => this.RaiseAndSetIfChanged(ref _language, value);
    }

    private bool _force;
    public bool Force
    {
        get => _force;
        set => this.RaiseAndSetIfChanged(ref _force, value);
    }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isRunning, value);
            this.RaisePropertyChanged(nameof(CanEditOptions));
            this.RaisePropertyChanged(nameof(CanStart));
            this.RaisePropertyChanged(nameof(CancelButtonText));
            UpdateCanStart();
        }
    }

    private bool _isDone;
    public bool IsDone
    {
        get => _isDone;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isDone, value);
            this.RaisePropertyChanged(nameof(CanEditOptions));
            this.RaisePropertyChanged(nameof(CanStart));
            UpdateCanStart();
        }
    }

    /// <summary>True while neither running nor finished — language/force are still editable.</summary>
    public bool CanEditOptions => !IsRunning && !IsDone;

    /// <summary>
    /// Explicit, bindable mirror of <see cref="StartCommand"/>'s canExecute
    /// state. Avalonia's <c>Button.IsEnabled</c> is not reliably driven by
    /// <c>ICommand.CanExecute</c> alone in this codebase's usage — every
    /// other gated command in the app binds <c>IsEnabled</c> directly (see
    /// e.g. <c>MainWindow.axaml</c>'s <c>IsDocumentLoaded</c> bindings) — so
    /// this follows the same convention rather than relying on implicit
    /// command-source wiring.
    /// </summary>
    public bool CanStart => !IsRunning && !IsDone && TesseractAvailable;

    /// <summary>"Cancel" while an OCR run is in flight, "Close" otherwise.</summary>
    public string CancelButtonText => IsRunning ? "Cancel" : "Close";

    private int _progressDone;
    public int ProgressDone
    {
        get => _progressDone;
        private set
        {
            this.RaiseAndSetIfChanged(ref _progressDone, value);
            this.RaisePropertyChanged(nameof(ProgressText));
            this.RaisePropertyChanged(nameof(ProgressFraction));
        }
    }

    private int _progressTotal;
    public int ProgressTotal
    {
        get => _progressTotal;
        private set
        {
            this.RaiseAndSetIfChanged(ref _progressTotal, value);
            this.RaisePropertyChanged(nameof(ProgressText));
            this.RaisePropertyChanged(nameof(ProgressFraction));
        }
    }

    public string ProgressText => ProgressTotal <= 0 ? string.Empty : $"Page {ProgressDone} of {ProgressTotal}";

    public double ProgressFraction => ProgressTotal <= 0 ? 0 : Math.Clamp((double)ProgressDone / ProgressTotal, 0, 1);

    private string? _resultSummary;
    public string? ResultSummary
    {
        get => _resultSummary;
        private set => this.RaiseAndSetIfChanged(ref _resultSummary, value);
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    /// <summary>The most recent completed run's result, if any. Null until a run finishes successfully.</summary>
    public SearchableDocumentResult? LastResult { get; private set; }

    public ReactiveCommand<Unit, Unit> StartCommand { get; }

    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>Raised when the dialog should close (Cancel/Close clicked while not running).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised once a run finishes successfully, carrying the engine's result summary.</summary>
    public event EventHandler<SearchableDocumentResult>? Completed;

    public MakeSearchableDialogViewModel(
        bool tesseractAvailable,
        Func<string, bool, IProgress<(int Done, int Total)>, CancellationToken, Task<SearchableDocumentResult>> runOcr)
    {
        TesseractAvailable = tesseractAvailable;
        _runOcr = runOcr ?? throw new ArgumentNullException(nameof(runOcr));

        // BehaviorSubject driving canExecute, not ReactiveUI's WhenAnyValue —
        // its Expression-based member chain is evaluated via reflection
        // (IL2026/IL3050, not trim/AOT-safe). See SaveRedactedVersionDialogViewModel
        // for the established precedent of this workaround in this codebase.
        _canStart = new BehaviorSubject<bool>(tesseractAvailable);
        StartCommand = ReactiveCommand.CreateFromTask(StartAsync, _canStart);
        CancelCommand = ReactiveCommand.Create(CancelOrClose);
    }

    private void UpdateCanStart() => _canStart.OnNext(!IsRunning && !IsDone && TesseractAvailable);

    private async Task StartAsync()
    {
        ErrorMessage = null;
        ResultSummary = null;
        ProgressDone = 0;
        ProgressTotal = 0;
        IsRunning = true;

        _cts = new CancellationTokenSource();
        // Constructed on the UI thread (this command runs there), so
        // Progress<T>.Report marshals its callback back to the UI thread
        // via the captured SynchronizationContext — same pattern as
        // MainWindowViewModel's indexProgress for the search-index build.
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            ProgressDone = p.Done;
            ProgressTotal = p.Total;
        });

        try
        {
            var result = await _runOcr(Language, Force, progress, _cts.Token).ConfigureAwait(true);
            LastResult = result;
            ResultSummary = FormatSummary(result);
            IsDone = true;
            Completed?.Invoke(this, result);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Make Searchable was cancelled.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Make Searchable failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void CancelOrClose()
    {
        if (IsRunning)
        {
            _cts?.Cancel();
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    internal static string FormatSummary(SearchableDocumentResult result)
    {
        var summary = $"{result.PagesProcessed} page(s) processed, {result.PagesSkipped} skipped " +
            $"(already searchable). {result.TotalWordsWritten} word(s) written.";
        if (result.TotalWordsSkippedEncoding > 0)
            summary += $" {result.TotalWordsSkippedEncoding} word(s) skipped (characters outside the " +
                "supported font — non-Latin OCR text isn't embedded yet, see #627).";
        return summary;
    }
}
