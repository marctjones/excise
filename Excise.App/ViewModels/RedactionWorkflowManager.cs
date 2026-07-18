using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Excise.App.Models;
using Excise.Core.Document;
using ReactiveUI;

namespace Excise.App.ViewModels;

/// <summary>
/// Manages the mark-then-apply redaction workflow.
/// Tracks pending and applied redactions.
/// </summary>
public class RedactionWorkflowManager : ReactiveObject
{
    private readonly ObservableCollection<PendingRedaction> _pending = new();
    private readonly ObservableCollection<PendingRedaction> _applied = new();

    /// <summary>
    /// Redactions that have been marked but not yet applied
    /// </summary>
    public ObservableCollection<PendingRedaction> PendingRedactions => _pending;

    /// <summary>
    /// Redactions that have been applied and saved
    /// </summary>
    public ObservableCollection<PendingRedaction> AppliedRedactions => _applied;

    /// <summary>
    /// Number of pending redactions
    /// </summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// Whether there are any pending redactions (issue #19 - button enable state)
    /// </summary>
    public bool HasPendingRedactions => _pending.Count > 0;

    /// <summary>
    /// Number of applied redactions
    /// </summary>
    public int AppliedCount => _applied.Count;

    public RedactionWorkflowManager()
    {
    }

    /// <summary>
    /// Mark an area for redaction (adds to pending list)
    /// </summary>
    public void MarkArea(
        int pageNumber,
        Rect area,
        string previewText,
        int renderDpi = MainWindowViewModel.DefaultViewerRenderDpi)
    {
        MarkArea(
            PdfPageRect.ViewerDips(pageNumber, area.X, area.Y, area.Width, area.Height, renderDpi),
            previewText);
    }

    /// <summary>
    /// Mark a page-scoped area for redaction.
    /// </summary>
    public void MarkArea(PdfPageRect area, string previewText)
    {
        var pending = new PendingRedaction
        {
            PageNumber = area.PageNumber,
            PageArea = area,
            PreviewText = previewText,
            MarkedTime = DateTime.Now
        };

        _pending.Add(pending);
        this.RaisePropertyChanged(nameof(PendingCount));
        this.RaisePropertyChanged(nameof(HasPendingRedactions)); // Issue #19 button state
        this.RaisePropertyChanged(nameof(PendingRedactions)); // Force UI update
    }

    /// <summary>
    /// Remove a pending redaction by ID
    /// </summary>
    public bool RemovePending(Guid id)
    {
        var item = _pending.FirstOrDefault(p => p.Id == id);
        if (item != null)
        {
            _pending.Remove(item);
            this.RaisePropertyChanged(nameof(PendingCount));
            this.RaisePropertyChanged(nameof(HasPendingRedactions)); // Issue #19 button state
            return true;
        }
        return false;
    }

    /// <summary>
    /// Clear all pending redactions
    /// </summary>
    public void ClearPending()
    {
        _pending.Clear();
        this.RaisePropertyChanged(nameof(PendingCount));
        this.RaisePropertyChanged(nameof(HasPendingRedactions)); // Issue #19 button state
    }

    /// <summary>
    /// Move all pending redactions to applied (after successful save)
    /// </summary>
    public void MoveToApplied()
    {
        foreach (var pending in _pending)
        {
            _applied.Add(pending);
        }

        _pending.Clear();
        this.RaisePropertyChanged(nameof(PendingCount));
        this.RaisePropertyChanged(nameof(HasPendingRedactions)); // Issue #19 button state
        this.RaisePropertyChanged(nameof(AppliedCount));
    }

    /// <summary>
    /// Get pending redactions for a specific page
    /// </summary>
    public IEnumerable<PendingRedaction> GetPendingForPage(int pageNumber)
    {
        return _pending.Where(p => p.PageNumber == pageNumber);
    }

    /// <summary>
    /// Get applied redactions for a specific page
    /// </summary>
    public IEnumerable<PendingRedaction> GetAppliedForPage(int pageNumber)
    {
        return _applied.Where(a => a.PageNumber == pageNumber);
    }

    /// <summary>
    /// Clear all state (e.g., when closing document)
    /// </summary>
    public void Reset()
    {
        _pending.Clear();
        _applied.Clear();
        this.RaisePropertyChanged(nameof(PendingCount));
        this.RaisePropertyChanged(nameof(HasPendingRedactions)); // Issue #19 button state
        this.RaisePropertyChanged(nameof(AppliedCount));
    }
}
