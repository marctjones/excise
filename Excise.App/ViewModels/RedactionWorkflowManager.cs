using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Excise.App.Models;
using Excise.Core.Document;
using Excise.Core.Primitives;
using ReactiveUI;

namespace Excise.App.ViewModels;

/// <summary>
/// Manages the mark-then-apply redaction workflow.
/// Tracks pending and applied redactions.
/// </summary>
internal class RedactionWorkflowManager : ReactiveObject
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
    /// Looks up the page node currently at a 1-based page number, so a new mark can be tied to
    /// the page itself and not only to the number the page has today. Null: marks stay unanchored.
    /// </summary>
    public Func<int, PdfDictionary?>? PageIdentityProvider { get; set; }

    /// <summary>
    /// A drawn rectangle is in viewer coordinates, which mean something else once the page is
    /// rotated. Call just BEFORE rotating <paramref name="pageNumber"/>: the marks on it are
    /// kept in the page's own coordinates, so the rotation moves them with the text.
    /// </summary>
    public void FreezeToPageCoordinates(PdfDocument document, int pageNumber)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (pageNumber < 1 || pageNumber > document.PageCount)
            return;

        var page = document.Pages[pageNumber - 1];
        foreach (var pending in _pending)
        {
            if (pending.IsOnRemovedPage || pending.PageNumber != pageNumber ||
                pending.PageArea.Space == PdfCoordinateSpace.ContentPoints)
            {
                continue;
            }

            pending.PageArea = PdfCoordinateMapper.ToContentPoints(page, pending.PageArea);
        }
    }

    /// <summary>
    /// Re-read where each pending mark's page now sits. Call after anything that changes the page
    /// order or count (delete, move, insert, and the undo of each), so the marks, the red outlines
    /// and the Apply step all follow the pages.
    /// </summary>
    public void SyncPages(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        foreach (var pending in _pending)
        {
            var page = PendingRedactionPages.CurrentPageNumber(document, pending);
            pending.IsOnRemovedPage = page == 0;
            if (page == 0 || page == pending.PageNumber)
                continue;
            pending.PageNumber = page;
            pending.PageArea = PendingRedactionPages.OnPage(pending.PageArea, page);
        }

        this.RaisePropertyChanged(nameof(PendingRedactions)); // marks moved: redraw the outlines
    }

    /// <summary>
    /// Mark a page-scoped area for redaction (adds to pending list).
    /// </summary>
    public void MarkArea(PdfPageRect area, string previewText)
    {
        var pending = new PendingRedaction
        {
            PageNumber = area.PageNumber,
            PageIdentity = PageIdentityProvider?.Invoke(area.PageNumber),
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
        return _pending.Where(p => !p.IsOnRemovedPage && p.PageNumber == pageNumber);
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
