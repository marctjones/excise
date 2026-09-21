using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Excise.App.Services.Printing;
using ReactiveUI;

namespace Excise.App.ViewModels;

/// <summary>
/// ViewModel for the Linux printer chooser (#1710). Linux gives excise no
/// system print dialog — Avalonia has no printing support and GTK's and Qt's
/// dialogs belong to their own toolkits — so this is the whole print UI:
/// which queue, how many copies, which pages.
/// </summary>
/// <remarks>
/// It touches no document and starts no process: it validates what the user
/// typed and hands back a <see cref="LinuxPrintTicket"/>, the same decoupling
/// <see cref="ReduceFileSizeDialogViewModel"/> uses. The scaling mode is
/// deliberately absent — it is a Preferences setting on every platform and the
/// printer maps it onto CUPS itself.
/// </remarks>
internal sealed class LinuxPrintDialogViewModel : ReactiveObject
{
    private CupsPrintQueue? _selectedQueue;
    private string _copiesText = "1";
    private string _pageRangeText = string.Empty;
    private bool _collate = true;
    private string? _validationError;

    internal LinuxPrintDialogViewModel(IReadOnlyList<CupsPrintQueue> queues, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(queues);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);

        Queues = queues.ToArray();
        PageCount = pageCount;
        // The list is already ordered default-first, so the default is
        // preselected without a second search.
        _selectedQueue = Queues.FirstOrDefault(queue => queue.IsDefault) ?? Queues.FirstOrDefault();
    }

    /// <summary>Every queue CUPS reported, the default first.</summary>
    public IReadOnlyList<CupsPrintQueue> Queues { get; }

    /// <summary>How many pages the document being printed has.</summary>
    public int PageCount { get; }

    /// <summary>What the page-range box's placeholder says.</summary>
    public string PageRangePlaceholder =>
        PageCount == 1 ? "All pages" : $"All pages (1-{PageCount.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>The queue the job goes to. Never null while <see cref="Queues"/> is not empty.</summary>
    public CupsPrintQueue? SelectedQueue
    {
        get => _selectedQueue;
        set => this.RaiseAndSetIfChanged(ref _selectedQueue, value);
    }

    /// <summary>Copies, as typed. Validated on confirm, not on every keystroke.</summary>
    public string CopiesText
    {
        get => _copiesText;
        set => this.RaiseAndSetIfChanged(ref _copiesText, value);
    }

    /// <summary>The page range, as typed. Empty means every page.</summary>
    public string PageRangeText
    {
        get => _pageRangeText;
        set => this.RaiseAndSetIfChanged(ref _pageRangeText, value);
    }

    /// <summary>Whether multiple copies come out 1,2,3,1,2,3 rather than 1,1,2,2,3,3.</summary>
    public bool Collate
    {
        get => _collate;
        set => this.RaiseAndSetIfChanged(ref _collate, value);
    }

    /// <summary>Why the last <see cref="TryConfirm"/> was refused, or null.</summary>
    public string? ValidationError
    {
        get => _validationError;
        private set => this.RaiseAndSetIfChanged(ref _validationError, value);
    }

    /// <summary>The ticket the printer uses, set once <see cref="TryConfirm"/> succeeds.</summary>
    internal LinuxPrintTicket? Ticket { get; private set; }

    /// <summary>
    /// Validate what the user typed and build the ticket. Returns false with
    /// <see cref="ValidationError"/> set — and the window stays open — when
    /// anything is wrong, so a typo never turns into a silently different
    /// print job.
    /// </summary>
    internal bool TryConfirm()
    {
        var queue = SelectedQueue;
        if (queue == null || string.IsNullOrWhiteSpace(queue.Name))
        {
            ValidationError = "Choose a printer.";
            return false;
        }

        if (!int.TryParse(CopiesText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int copies) ||
            copies < 1 || copies > PrintPageSequence.MaxCopies)
        {
            ValidationError = $"Copies must be a whole number from 1 to {PrintPageSequence.MaxCopies}.";
            return false;
        }

        if (!PrintPageRangeText.TryParse(PageRangeText, PageCount, out var ranges, out var rangeError))
        {
            ValidationError = rangeError;
            return false;
        }

        ValidationError = null;
        Ticket = LinuxPrintTicket.Print(queue.Name, ranges, copies, Collate);
        return true;
    }
}
