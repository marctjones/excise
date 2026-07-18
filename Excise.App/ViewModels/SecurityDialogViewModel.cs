using Excise.Core.Security;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Subjects;
using System.Threading.Tasks;

namespace Excise.App.ViewModels;

/// <summary>
/// ViewModel for the "Document &gt; Security..." dialog (#641): set, change,
/// or remove password protection on the currently-open document. Mirrors
/// <see cref="MakeSearchableDialogViewModel"/>'s shape — state owned here,
/// the actual crypto/IO driven through injected delegates so this class is
/// unit-testable without a real window, storage provider, or PDF — see
/// <c>MainWindowViewModel.Security.cs</c> for the delegates that wire this
/// up to <see cref="Excise.Core.Writing.PdfDocumentWriter"/>.
///
/// Two distinct commands, deliberately not one "Apply" that infers intent
/// from blank fields: <see cref="ApplyCommand"/> always writes an encrypted
/// copy (with whatever passwords/algorithm are set, including blank
/// passwords — a valid "encrypted, no open prompt" configuration), and
/// <see cref="RemoveProtectionCommand"/> is the only path that writes an
/// unprotected copy. Clearing the password fields and clicking Apply on an
/// already-encrypted document must NOT silently strip protection — that is
/// exactly the #638 failure mode ("saving an encrypted source drops
/// encryption without asking") re-created on this new surface, and the
/// reason these are two separate, separately-gated commands rather than one
/// that guesses from field state.
/// </summary>
public sealed class SecurityDialogViewModel : ReactiveObject
{
    public static IReadOnlyList<PdfEncryptionAlgorithm> Algorithms { get; } = new[]
    {
        PdfEncryptionAlgorithm.Aes256,
        PdfEncryptionAlgorithm.Aes128,
    };

    private readonly Func<string?, bool> _verifyCurrentPassword;
    private readonly Func<string?, string?, PdfEncryptionAlgorithm, Task<string?>> _applyAsync;
    private readonly Func<Task<string?>> _removeProtectionAsync;
    private readonly BehaviorSubject<bool> _canApply;
    private readonly BehaviorSubject<bool> _canRemove;

    /// <summary>Whether the document was already encrypted when this dialog was opened.</summary>
    public bool IsEncrypted { get; }

    private string _currentPassword = string.Empty;
    /// <summary>Only meaningful (and only shown) when <see cref="IsEncrypted"/> is true.</summary>
    public string CurrentPassword
    {
        get => _currentPassword;
        set => this.RaiseAndSetIfChanged(ref _currentPassword, value);
    }

    private string _newUserPassword = string.Empty;
    public string NewUserPassword
    {
        get => _newUserPassword;
        set
        {
            this.RaiseAndSetIfChanged(ref _newUserPassword, value);
            UpdateCanExecute();
        }
    }

    private string _newOwnerPassword = string.Empty;
    public string NewOwnerPassword
    {
        get => _newOwnerPassword;
        set
        {
            this.RaiseAndSetIfChanged(ref _newOwnerPassword, value);
            UpdateCanExecute();
        }
    }

    private PdfEncryptionAlgorithm _algorithm = PdfEncryptionAlgorithm.Aes256;
    public PdfEncryptionAlgorithm Algorithm
    {
        get => _algorithm;
        set => this.RaiseAndSetIfChanged(ref _algorithm, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            this.RaisePropertyChanged(nameof(CanEditOptions));
            this.RaisePropertyChanged(nameof(CloseButtonText));
            UpdateCanExecute();
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
            UpdateCanExecute();
        }
    }

    /// <summary>True while neither busy nor finished — fields are still editable.</summary>
    public bool CanEditOptions => !IsBusy && !IsDone;

    /// <summary>
    /// Whether at least one new password field has content. On a document
    /// that ISN'T currently encrypted, this must be true before Apply does
    /// anything — otherwise there is nothing to protect and Apply would be
    /// a confusing no-op button.
    /// </summary>
    private bool HasAnyNewPassword => !string.IsNullOrEmpty(NewUserPassword) || !string.IsNullOrEmpty(NewOwnerPassword);

    /// <summary>
    /// Explicit, bindable mirror of <see cref="ApplyCommand"/>'s canExecute
    /// state — see <see cref="MakeSearchableDialogViewModel.CanStart"/>'s
    /// remarks for why this codebase binds <c>IsEnabled</c> directly rather
    /// than relying on ReactiveCommand's implicit CanExecute wiring.
    /// </summary>
    public bool CanApply => !IsBusy && !IsDone && (IsEncrypted || HasAnyNewPassword);

    /// <summary>Only meaningful on an already-encrypted document — nothing to remove otherwise.</summary>
    public bool CanRemove => !IsBusy && !IsDone && IsEncrypted;

    public string CloseButtonText => IsBusy ? "Working..." : "Close";

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    private string? _resultMessage;
    public string? ResultMessage
    {
        get => _resultMessage;
        private set => this.RaiseAndSetIfChanged(ref _resultMessage, value);
    }

    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }

    public ReactiveCommand<Unit, Unit> RemoveProtectionCommand { get; }

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    /// <summary>Raised when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised once Apply or Remove finishes successfully, carrying the output file path.</summary>
    public event EventHandler<string>? Completed;

    public SecurityDialogViewModel(
        bool isEncrypted,
        Func<string?, bool> verifyCurrentPassword,
        Func<string?, string?, PdfEncryptionAlgorithm, Task<string?>> applyAsync,
        Func<Task<string?>> removeProtectionAsync)
    {
        IsEncrypted = isEncrypted;
        _verifyCurrentPassword = verifyCurrentPassword ?? throw new ArgumentNullException(nameof(verifyCurrentPassword));
        _applyAsync = applyAsync ?? throw new ArgumentNullException(nameof(applyAsync));
        _removeProtectionAsync = removeProtectionAsync ?? throw new ArgumentNullException(nameof(removeProtectionAsync));

        // BehaviorSubject driving canExecute, not ReactiveUI's WhenAnyValue —
        // matches MakeSearchableDialogViewModel's established workaround for
        // IL2026/IL3050 trim/AOT-unsafe expression-tree member chains.
        _canApply = new BehaviorSubject<bool>(CanApply);
        _canRemove = new BehaviorSubject<bool>(CanRemove);
        ApplyCommand = ReactiveCommand.CreateFromTask(ApplyCoreAsync, _canApply);
        RemoveProtectionCommand = ReactiveCommand.CreateFromTask(RemoveCoreAsync, _canRemove);
        CloseCommand = ReactiveCommand.Create(CloseOrIgnore);
    }

    private void UpdateCanExecute()
    {
        _canApply.OnNext(CanApply);
        _canRemove.OnNext(CanRemove);
        // The XAML binds IsEnabled to CanApply/CanRemove directly (see
        // MakeSearchableDialogViewModel.CanStart for why), so the computed
        // properties must ALSO raise change notifications — the
        // BehaviorSubjects above only feed the ReactiveCommands. Missing
        // these raises left the Apply button permanently disabled in the
        // real window while every ViewModel-only test passed; caught by
        // SecurityDialogUiTests' real headless click.
        this.RaisePropertyChanged(nameof(CanApply));
        this.RaisePropertyChanged(nameof(CanRemove));
    }

    private async Task ApplyCoreAsync()
    {
        ErrorMessage = null;
        ResultMessage = null;

        if (IsEncrypted && !_verifyCurrentPassword(CurrentPassword))
        {
            ErrorMessage = "Current password is incorrect.";
            return;
        }

        IsBusy = true;
        try
        {
            var outputPath = await _applyAsync(
                NullIfEmpty(NewUserPassword), NullIfEmpty(NewOwnerPassword), Algorithm).ConfigureAwait(true);

            if (outputPath == null)
            {
                // User cancelled the save-target picker — not an error.
                return;
            }

            ResultMessage = $"Password protection applied. Saved to {outputPath}";
            IsDone = true;
            Completed?.Invoke(this, outputPath);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to apply password protection: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveCoreAsync()
    {
        ErrorMessage = null;
        ResultMessage = null;

        if (!_verifyCurrentPassword(CurrentPassword))
        {
            ErrorMessage = "Current password is incorrect.";
            return;
        }

        IsBusy = true;
        try
        {
            var outputPath = await _removeProtectionAsync().ConfigureAwait(true);

            if (outputPath == null)
            {
                // User cancelled the confirmation or the save-target picker.
                return;
            }

            ResultMessage = $"Password protection removed. Saved to {outputPath}";
            IsDone = true;
            Completed?.Invoke(this, outputPath);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to remove password protection: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CloseOrIgnore()
    {
        if (IsBusy) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
}
