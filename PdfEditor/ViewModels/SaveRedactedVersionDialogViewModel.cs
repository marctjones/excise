using System;
using System.IO;
using System.Reactive;
using System.Reactive.Subjects;
using ReactiveUI;

namespace PdfEditor.ViewModels;

/// <summary>
/// ViewModel for the Save Redacted Version dialog.
/// Prompts user to choose filename for redacted PDF.
/// </summary>
public class SaveRedactedVersionDialogViewModel : ReactiveObject
{
    private string _saveFilePath;
    private int _pendingCount;

    // Drives SaveCommand's canExecute. Updated from the SaveFilePath setter
    // instead of ReactiveUI's WhenAnyValue, whose Expression-based member
    // chain is evaluated via reflection (IL2026/IL3050 — not trim/AOT-safe).
    private readonly BehaviorSubject<bool> _canSave;

    /// <summary>
    /// The path where the redacted PDF will be saved
    /// </summary>
    public string SaveFilePath
    {
        get => _saveFilePath;
        set
        {
            this.RaiseAndSetIfChanged(ref _saveFilePath, value);
            _canSave?.OnNext(IsSavePathValid(value));
        }
    }

    /// <summary>
    /// Number of pending redactions to be applied
    /// </summary>
    public int PendingCount
    {
        get => _pendingCount;
        set
        {
            this.RaiseAndSetIfChanged(ref _pendingCount, value);
            this.RaisePropertyChanged(nameof(PendingCountText));
        }
    }

    /// <summary>
    /// Text showing count of redactions (e.g., "3 areas will be redacted")
    /// </summary>
    public string PendingCountText => PendingCount == 1
        ? "1 area will be redacted"
        : $"{PendingCount} areas will be redacted";

    /// <summary>
    /// Command to open file browser for save location
    /// </summary>
    public ReactiveCommand<Unit, string?> BrowseCommand { get; }

    /// <summary>
    /// Command to confirm and save
    /// </summary>
    public ReactiveCommand<Unit, string?> SaveCommand { get; }

    /// <summary>
    /// Command to cancel
    /// </summary>
    public ReactiveCommand<Unit, string?> CancelCommand { get; }

    public SaveRedactedVersionDialogViewModel(string suggestedFilePath, int pendingCount)
    {
        _saveFilePath = suggestedFilePath;
        _pendingCount = pendingCount;

        // Browse command - will be handled by view to open file picker
        BrowseCommand = ReactiveCommand.Create<string?>(() => null);

        // Save command - enabled only when path is valid. Seed the subject with
        // the initial path's validity so the button reflects state immediately.
        _canSave = new BehaviorSubject<bool>(IsSavePathValid(_saveFilePath));
        SaveCommand = ReactiveCommand.Create<string?>(() => SaveFilePath, _canSave);

        // Cancel command - always enabled
        CancelCommand = ReactiveCommand.Create<string?>(() => null);
    }

    private bool IsSavePathValid(string? path)
        => !string.IsNullOrWhiteSpace(path) && IsValidPath(path);

    private bool IsValidPath(string path)
    {
        try
        {
            // Check if directory exists
            var dir = Path.GetDirectoryName(path);
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
        }
        catch
        {
            return false;
        }
    }
}
