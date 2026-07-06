using Microsoft.Extensions.Logging;
using Pdfe.Core.Document;
using PdfEditor.Services;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;

namespace PdfEditor.ViewModels;

/// <summary>
/// Search-related functionality for MainWindowViewModel
/// </summary>
public partial class MainWindowViewModel
{
    private readonly PdfSearchService? _searchService;
    private string _searchText = string.Empty;
    private bool _searchCaseSensitive = false;
    private bool _searchWholeWords = false;
    private bool _searchUseRegex = false;
    private int _currentSearchMatchIndex = -1;
    private ObservableCollection<PdfSearchService.SearchMatch> _searchMatches = new();
    private bool _isSearchVisible = false;

    // Debounce + cancellation for incremental ("search-as-you-type")
    // queries. Pre-fix every keystroke kicked off a fresh search that
    // re-opened and re-parsed the PDF; on a 455-page book each one took
    // ~30 s, so by the time the user finished typing they had a queue of
    // overlapping searches and the foreground felt unresponsive.
    private CancellationTokenSource? _searchCts;
    // Pause-after-typing window before kicking a search. 300 ms felt
    // sluggish; 150 ms is short enough to feel "live" but still cancels
    // intermediate keystrokes when typing a multi-letter word at speed.
    private const int SearchDebounceMs = 150;

    private bool _isSearching;
    private string _searchProgressText = string.Empty;

    /// <summary>True while a search is in flight. Drives the inline spinner.</summary>
    public bool IsSearching
    {
        get => _isSearching;
        private set => this.RaiseAndSetIfChanged(ref _isSearching, value);
    }

    /// <summary>
    /// "Searching page 47 of 455 — 12 matches so far" while a search is
    /// running, empty otherwise. Drives the inline progress text in the
    /// search bar.
    /// </summary>
    public string SearchProgressText
    {
        get => _searchProgressText;
        private set => this.RaiseAndSetIfChanged(ref _searchProgressText, value);
    }

    // Search Properties
    public string SearchText
    {
        get => _searchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchText, value);
            ScheduleSearchDebounced();
        }
    }

    public bool SearchCaseSensitive
    {
        get => _searchCaseSensitive;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchCaseSensitive, value);
            ScheduleSearchDebounced();
        }
    }

    public bool SearchWholeWords
    {
        get => _searchWholeWords;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchWholeWords, value);
            ScheduleSearchDebounced();
        }
    }

    public bool SearchUseRegex
    {
        get => _searchUseRegex;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchUseRegex, value);
            ScheduleSearchDebounced();
        }
    }

    /// <summary>
    /// Cancel any pending/in-flight search, then schedule a new one
    /// after a short debounce delay. If the user keeps typing, the
    /// delay timer resets so we only actually run once they pause.
    /// </summary>
    private void ScheduleSearchDebounced()
    {
        _searchCts?.Cancel();
        if (string.IsNullOrWhiteSpace(_searchText))
        {
            ClearSearch();
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var token = cts.Token;
        OperationStatus = "Searching…";

        // Capture the search inputs at scheduling time so a later
        // keystroke doesn't change them out from under us.
        var query = _searchText;
        var caseSensitive = _searchCaseSensitive;
        var wholeWords = _searchWholeWords;
        var useRegex = _searchUseRegex;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SearchDebounceMs, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                PerformSearch(query, caseSensitive, wholeWords, useRegex, token);
            }
            catch (OperationCanceledException) { /* superseded */ }
        });
    }

    public ObservableCollection<PdfSearchService.SearchMatch> SearchMatches
    {
        get => _searchMatches;
        set => this.RaiseAndSetIfChanged(ref _searchMatches, value);
    }

    public int CurrentSearchMatchIndex
    {
        get => _currentSearchMatchIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentSearchMatchIndex, value);
            this.RaisePropertyChanged(nameof(SearchResultText));
        }
    }

    public string SearchResultText
    {
        get
        {
            if (SearchMatches.Count == 0)
                return "No matches";

            if (CurrentSearchMatchIndex >= 0 && CurrentSearchMatchIndex < SearchMatches.Count)
                return $"{CurrentSearchMatchIndex + 1} of {SearchMatches.Count}";

            return $"{SearchMatches.Count} matches";
        }
    }

    public bool IsSearchVisible
    {
        get => _isSearchVisible;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSearchVisible, value);
            // Right-sidebar panel selection depends on this flag.
            this.RaisePropertyChanged(nameof(ShowSearchResultsPanel));
            this.RaisePropertyChanged(nameof(ShowPendingRedactionsPanel));
            this.RaisePropertyChanged(nameof(ShowClipboardHistoryPanel));
        }
    }

    /// <summary>
    /// Sidebar mode selectors. The right sidebar shows exactly one panel
    /// at a time; computing the booleans here keeps the XAML readable
    /// (no MultiBinding gymnastics) and keeps invariants in one place.
    /// </summary>
    public bool ShowSearchResultsPanel => IsSearchVisible;
    public bool ShowPendingRedactionsPanel => IsRedactionMode && !IsSearchVisible;
    public bool ShowClipboardHistoryPanel => !IsRedactionMode && !IsSearchVisible;

    // Search Commands
    public ReactiveCommand<Unit, Unit>? ToggleSearchCommand { get; private set; }
    public ReactiveCommand<Unit, Unit>? FindNextCommand { get; private set; }
    public ReactiveCommand<Unit, Unit>? FindPreviousCommand { get; private set; }
    public ReactiveCommand<Unit, Unit>? CloseSearchCommand { get; private set; }
    public ReactiveCommand<Unit, Unit>? FindCommand { get; private set; }
    public ReactiveCommand<PdfSearchService.SearchMatch, Unit>? JumpToSearchMatchCommand { get; private set; }

    /// <summary>
    /// Initialize search commands (call from main constructor)
    /// </summary>
    private void InitializeSearchCommands()
    {
        ToggleSearchCommand = ReactiveCommand.Create(ToggleSearch);
        FindNextCommand = ReactiveCommand.Create(FindNext);
        FindPreviousCommand = ReactiveCommand.Create(FindPrevious);
        CloseSearchCommand = ReactiveCommand.Create(CloseSearch);
        // Manual "Find" trigger — same code path as type-and-pause but
        // bypasses the debounce. Bound to the Find button and to the
        // Enter key in the search box (handled in MainWindow.axaml.cs).
        FindCommand = ReactiveCommand.Create(FindNow);
        // Click on a row in the search-results sidebar.
        JumpToSearchMatchCommand =
            ReactiveCommand.Create<PdfSearchService.SearchMatch>(JumpToSearchMatch);
    }

    /// <summary>
    /// Run a search immediately (no debounce). Cancels any pending
    /// debounced search first so we don't double-search.
    /// </summary>
    public void FindNow()
    {
        _searchCts?.Cancel();
        if (string.IsNullOrWhiteSpace(_searchText))
        {
            ClearSearch();
            return;
        }
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var token = cts.Token;
        OperationStatus = "Searching…";
        var query = _searchText;
        var caseSensitive = _searchCaseSensitive;
        var wholeWords = _searchWholeWords;
        var useRegex = _searchUseRegex;
        Task.Run(() => PerformSearch(query, caseSensitive, wholeWords, useRegex, token));
    }

    /// <summary>
    /// Toggle search bar visibility
    /// </summary>
    private void ToggleSearch()
    {
        IsSearchVisible = !IsSearchVisible;

        if (IsSearchVisible)
        {
            _logger.LogInformation("Search activated");
        }
        else
        {
            CloseSearch();
        }
    }

    /// <summary>
    /// Close search and clear results
    /// </summary>
    private void CloseSearch()
    {
        IsSearchVisible = false;
        ClearSearch();
        _logger.LogInformation("Search closed");
    }

    /// <summary>
    /// Perform a search and publish matches to the UI. Reuses the
    /// already-open <see cref="PdfCoreDocument"/> so we don't pay the
    /// ~30 s parse cost per keystroke. The token lets a debounce-
    /// superseding query abort us mid-walk.
    /// </summary>
    private void PerformSearch(string query, bool caseSensitive, bool wholeWords, bool useRegex,
        CancellationToken token)
    {
        if (_searchService == null) return;

        var doc = PdfCoreDocument;
        // Fall back to file-path-based search only when the in-memory
        // document isn't available (e.g. legacy code paths in tests).
        try
        {
            _logger.LogInformation(
                "Searching for '{Query}' (CaseSensitive={CaseSensitive}, WholeWords={WholeWords}, UseRegex={UseRegex})",
                query, caseSensitive, wholeWords, useRegex);

            // Show the spinner + progress text immediately. Both run on
            // the UI thread to avoid cross-thread RaisePropertyChanged.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsSearching = true;
                SearchProgressText = "Searching…";
            });

            // Progress reports come on the search worker thread; marshal
            // each one to the UI thread to update the bound property.
            // The service throttles the callback rate so we don't flood
            // the dispatcher.
            var progress = new Progress<PdfSearchService.SearchProgress>(p =>
            {
                if (token.IsCancellationRequested) return;
                SearchProgressText = p.PagesScanned == 0
                    ? $"Searching… 0 of {p.TotalPages} pages"
                    : $"Searching… page {p.PagesScanned} of {p.TotalPages} — " +
                      $"{p.MatchesFound} match{(p.MatchesFound == 1 ? "" : "es")} so far";
            });

            System.Collections.Generic.List<PdfSearchService.SearchMatch> matches;
            // Prefer the pre-built text index when available — searches
            // become near-instant because per-page extraction has already
            // happened. Fall back to live extraction while the index is
            // still being built or for unindexed docs.
            var idx = TextIndex;
            if (idx != null && idx.IsReady)
            {
                matches = _searchService.Search(idx, query, caseSensitive, wholeWords,
                    useRegex: useRegex, cancellationToken: token, progress: progress);
            }
            else if (doc != null)
            {
                matches = _searchService.Search(doc, query, caseSensitive, wholeWords,
                    useRegex: useRegex, cancellationToken: token, progress: progress);
            }
            else if (!string.IsNullOrEmpty(_currentFilePath))
            {
                matches = _searchService.Search(_currentFilePath, query,
                    caseSensitive, wholeWords, useRegex: useRegex, progress: progress);
            }
            else return;

            if (token.IsCancellationRequested) return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested) return;

                SearchMatches.Clear();
                foreach (var match in matches)
                    SearchMatches.Add(match);

                if (SearchMatches.Count > 0)
                {
                    CurrentSearchMatchIndex = 0;
                    NavigateToSearchMatch(SearchMatches[0]);
                }
                else
                {
                    CurrentSearchMatchIndex = -1;
                }

                this.RaisePropertyChanged(nameof(SearchResultText));
                IsSearching = false;
                SearchProgressText = string.Empty;
                OperationStatus = string.Empty;
            });

            _logger.LogInformation("Found {MatchCount} matches", matches.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error performing search: {Message}", ex.Message);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsSearching = false;
                SearchProgressText = string.Empty;
                OperationStatus = string.Empty;
            });
        }
    }

    /// <summary>
    /// Navigate to next search match
    /// </summary>
    private void FindNext()
    {
        if (SearchMatches.Count == 0)
            return;

        CurrentSearchMatchIndex = (CurrentSearchMatchIndex + 1) % SearchMatches.Count;
        NavigateToSearchMatch(SearchMatches[CurrentSearchMatchIndex]);

        _logger.LogDebug("Navigated to next match: {Index} of {Total}",
            CurrentSearchMatchIndex + 1, SearchMatches.Count);
    }

    /// <summary>
    /// Navigate to previous search match
    /// </summary>
    private void FindPrevious()
    {
        if (SearchMatches.Count == 0)
            return;

        CurrentSearchMatchIndex = CurrentSearchMatchIndex <= 0
            ? SearchMatches.Count - 1
            : CurrentSearchMatchIndex - 1;

        NavigateToSearchMatch(SearchMatches[CurrentSearchMatchIndex]);

        _logger.LogDebug("Navigated to previous match: {Index} of {Total}",
            CurrentSearchMatchIndex + 1, SearchMatches.Count);
    }

    /// <summary>
    /// Public entry-point used by the search-results sidebar. Jumps the
    /// viewer to the page containing <paramref name="match"/> and selects
    /// it so the prev/next buttons resume from there.
    /// </summary>
    public void JumpToSearchMatch(PdfSearchService.SearchMatch match)
    {
        if (match == null) return;
        var index = SearchMatches.IndexOf(match);
        if (index < 0) return;
        CurrentSearchMatchIndex = index;
        NavigateToSearchMatch(match);
    }

    /// <summary>
    /// Navigate to a specific search match
    /// </summary>
    private void NavigateToSearchMatch(PdfSearchService.SearchMatch match)
    {
        // Navigate to the page containing the match
        if (match.PageIndex != CurrentPageIndex)
        {
            CurrentPageIndex = match.PageIndex;
        }

        // Update search highlights for the current page
        UpdateSearchHighlights();

        _logger.LogInformation("Navigated to match on page {PageIndex}: '{Text}'",
            match.PageIndex + 1, match.MatchedText);
    }

    /// <summary>
    /// Update search highlight rectangles for the current page.
    /// Updates current-page search highlights in PDF content coordinates.
    /// </summary>
    public void UpdateSearchHighlights()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            CurrentPageSearchHighlights.Clear();

            if (SearchMatches.Count == 0 || _documentService == null)
                return;

            // Get matches for current page
            var pageMatches = SearchMatches.Where(m => m.PageIndex == CurrentPageIndex).ToList();

            if (pageMatches.Count == 0)
                return;

            foreach (var match in pageMatches)
            {
                var contentRect = new PdfRectangle(
                    match.X,
                    match.Y,
                    match.X + match.Width,
                    match.Y + match.Height);
                CurrentPageSearchHighlights.Add(
                    PdfPageRect.FromContentPoints(CurrentPageIndex + 1, contentRect));
            }

            _logger.LogDebug("Updated {Count} search highlights for page {Page}",
                CurrentPageSearchHighlights.Count, CurrentPageIndex + 1);
        });
    }

    /// <summary>
    /// Clear search results
    /// </summary>
    private void ClearSearch()
    {
        SearchMatches.Clear();
        CurrentSearchMatchIndex = -1;
        this.RaisePropertyChanged(nameof(SearchResultText));
    }
}
