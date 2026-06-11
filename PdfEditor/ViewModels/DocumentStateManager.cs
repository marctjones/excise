using System;
using System.IO;
using ReactiveUI;

namespace PdfEditor.ViewModels;

/// <summary>
/// Manages document file state and path tracking.
/// Determines if file is original, redacted version, or modified copy.
/// </summary>
public class DocumentStateManager : ReactiveObject
{
    private string _currentFilePath = string.Empty;
    private string _originalFilePath = string.Empty;
    private int _pendingRedactionsCount;
    private int _removedPagesCount;
    private int _pageEditsCount;
    private int _formFieldEditsCount;
    private int _typewriterEditsCount;
    private int _annotationEditsCount;

    /// <summary>
    /// Path to the currently open file
    /// </summary>
    public string CurrentFilePath
    {
        get => _currentFilePath;
        private set => this.RaiseAndSetIfChanged(ref _currentFilePath, value);
    }

    /// <summary>
    /// Path to the original file that was first opened
    /// </summary>
    public string OriginalFilePath
    {
        get => _originalFilePath;
        private set => this.RaiseAndSetIfChanged(ref _originalFilePath, value);
    }

    /// <summary>
    /// Number of pending (not yet applied) redactions
    /// </summary>
    public int PendingRedactionsCount
    {
        get => _pendingRedactionsCount;
        set => this.RaiseAndSetIfChanged(ref _pendingRedactionsCount, value);
    }

    /// <summary>
    /// Number of removed pages (not yet saved)
    /// </summary>
    public int RemovedPagesCount
    {
        get => _removedPagesCount;
        set => this.RaiseAndSetIfChanged(ref _removedPagesCount, value);
    }

    /// <summary>
    /// Number of non-removal page organization edits not yet saved.
    /// </summary>
    public int PageEditsCount
    {
        get => _pageEditsCount;
        set => this.RaiseAndSetIfChanged(ref _pageEditsCount, value);
    }

    /// <summary>
    /// Number of AcroForm field edits not yet saved.
    /// </summary>
    public int FormFieldEditsCount
    {
        get => _formFieldEditsCount;
        set => this.RaiseAndSetIfChanged(ref _formFieldEditsCount, value);
    }

    /// <summary>
    /// Number of flat typewriter text edits not yet saved.
    /// </summary>
    public int TypewriterEditsCount
    {
        get => _typewriterEditsCount;
        set => this.RaiseAndSetIfChanged(ref _typewriterEditsCount, value);
    }

    /// <summary>
    /// Number of sticky-note/highlight annotation edits not yet saved.
    /// </summary>
    public int AnnotationEditsCount
    {
        get => _annotationEditsCount;
        set => this.RaiseAndSetIfChanged(ref _annotationEditsCount, value);
    }

    /// <summary>
    /// True if current file is the same as original (not saved as different file)
    /// </summary>
    public bool IsOriginalFile
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentFilePath) || string.IsNullOrEmpty(OriginalFilePath))
                return false;

            // Normalize paths for comparison
            var currentPath = Path.GetFullPath(CurrentFilePath);
            var originalPath = Path.GetFullPath(OriginalFilePath);

            return string.Equals(currentPath, originalPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// True if current file appears to be a redacted version (contains _REDACTED in name)
    /// </summary>
    public bool IsRedactedVersion
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentFilePath))
                return false;

            var filename = Path.GetFileName(CurrentFilePath);
            return filename.Contains("_REDACTED", StringComparison.OrdinalIgnoreCase) ||
                   filename.Contains("_redacted", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// True if there are any unsaved changes (redactions or page modifications)
    /// </summary>
    public bool HasUnsavedChanges =>
        PendingRedactionsCount > 0
        || RemovedPagesCount > 0
        || PageEditsCount > 0
        || FormFieldEditsCount > 0
        || TypewriterEditsCount > 0
        || AnnotationEditsCount > 0;

    /// <summary>
    /// User-friendly description of file type
    /// </summary>
    public string FileType
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentFilePath))
                return "No document";

            if (IsOriginalFile && !HasUnsavedChanges)
                return "Original";

            if (IsOriginalFile && HasUnsavedChanges)
                return "Original (unsaved changes)";

            if (IsRedactedVersion)
                return "Redacted version";

            return "Modified version";
        }
    }

    /// <summary>
    /// Initialize state when loading a new document
    /// </summary>
    public void SetDocument(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentException("File path cannot be empty", nameof(filePath));

        CurrentFilePath = filePath;
        OriginalFilePath = filePath;
        PendingRedactionsCount = 0;
        RemovedPagesCount = 0;
        PageEditsCount = 0;
        FormFieldEditsCount = 0;
        TypewriterEditsCount = 0;
        AnnotationEditsCount = 0;
    }

    /// <summary>
    /// Update current file path (e.g., after Save As)
    /// Preserves original file path
    /// </summary>
    public void UpdateCurrentPath(string newPath)
    {
        if (string.IsNullOrEmpty(newPath))
            throw new ArgumentException("File path cannot be empty", nameof(newPath));

        CurrentFilePath = newPath;
    }

    /// <summary>
    /// Reset all state (e.g., when closing document)
    /// </summary>
    public void Reset()
    {
        CurrentFilePath = string.Empty;
        OriginalFilePath = string.Empty;
        MarkSaved();
    }

    /// <summary>
    /// Clear dirty counters after the current document state has been
    /// persisted.
    /// </summary>
    public void MarkSaved()
    {
        PendingRedactionsCount = 0;
        RemovedPagesCount = 0;
        PageEditsCount = 0;
        FormFieldEditsCount = 0;
        TypewriterEditsCount = 0;
        AnnotationEditsCount = 0;
    }

    /// <summary>
    /// Get suggested save button text based on current state
    /// </summary>
    public string GetSaveButtonText()
    {
        if (!HasUnsavedChanges)
            return "Save"; // Will be disabled

        if (IsOriginalFile && PendingRedactionsCount > 0)
            return "Save Redacted Version";

        if (IsOriginalFile && FormFieldEditsCount > 0)
            return "Save Filled Copy";

        if (IsOriginalFile && AnnotationEditsCount > 0)
            return "Save Annotated Copy";

        if (IsOriginalFile)
            return "Save a Copy";

        return "Save";
    }
}
