using Microsoft.Extensions.Logging;
using Excise.App.Models;
using Excise.Core.Document;
using Excise.App.Services;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;

namespace Excise.App.ViewModels;

/// <summary>
/// Search-related functionality for MainWindowViewModel
/// </summary>
public partial class MainWindowViewModel
{
    private readonly DocumentSearchSession _searchSession;
    private string _searchText = string.Empty;
    private bool _searchCaseSensitive = false;
    private bool _searchWholeWords = false;
    private bool _searchUseRegex = false;
    private int _currentSearchMatchIndex = -1;
    private ObservableCollection<SearchMatch> _searchMatches = new();
    private bool _isSearchVisible = false;

    // Debounce incremental ("search-as-you-type") queries. Pre-fix every
    // keystroke kicked off a fresh search that
    // re-opened and re-parsed the PDF; on a 455-page book each one took
    // ~30 s, so by the time the user finished typing they had a queue of
    // overlapping searches and the foreground felt unresponsive.
    // Pause-after-typing window before kicking a search. 300 ms felt
    // sluggish; 150 ms is short enough to feel "live" but still cancels
    // intermediate keystrokes when typing a multi-letter word at speed.
    private const int SearchDebounceMs = 150;
    private bool _isSearching;
    private string _searchProgressText = string.Empty;

    internal long LastSearchWorkerElapsedMs { get; private set; }
    internal long LastSearchUiQueueElapsedMs { get; private set; }
    internal long LastSearchUiPublishElapsedMs { get; private set; }
    internal long LastSearchTotalElapsedMs { get; private set; }

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
        StartSearch(TimeSpan.FromMilliseconds(SearchDebounceMs));
    }

    // Per-page index of the current SearchMatches, rebuilt whenever the
    // collection is reassigned. UpdateSearchHighlights runs on every page
    // navigation; a linear SearchMatches.Where(m => m.PageIndex == ...) scan
    // there is O(total matches) per page flip, which grows with document size
    // (a dense search on a large book — thousands of matches — turns each page
    // flip into a scan of all of them). The index makes the per-navigation
    // lookup O(matches on the target page). See #601.
    private readonly System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<SearchMatch>> _matchesByPage = new();

    public ObservableCollection<SearchMatch> SearchMatches
    {
        get => _searchMatches;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchMatches, value);
            RebuildMatchesByPageIndex();
        }
    }

    /// <summary>Test hook (#601 benchmark): the per-page match index.</summary>
    internal System.Collections.Generic.IReadOnlyDictionary<int, System.Collections.Generic.List<SearchMatch>> MatchesByPageIndexForBenchmark
        => _matchesByPage;

    private void RebuildMatchesByPageIndex()
    {
        _matchesByPage.Clear();
        foreach (var match in _searchMatches)
        {
            if (!_matchesByPage.TryGetValue(match.PageIndex, out var list))
            {
                list = new System.Collections.Generic.List<SearchMatch>();
                _matchesByPage[match.PageIndex] = list;
            }
            list.Add(match);
        }
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
    public ReactiveCommand<SearchMatch, Unit>? JumpToSearchMatchCommand { get; private set; }

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
            ReactiveCommand.Create<SearchMatch>(JumpToSearchMatch);
    }

    /// <summary>
    /// Run a search immediately (no debounce). Cancels any pending
    /// debounced search first so we don't double-search.
    /// </summary>
    public void FindNow()
    {
        StartSearch(TimeSpan.Zero);
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
        _searchSession.Cancel();
        IsSearchVisible = false;
        ClearSearch();
        _logger.LogInformation("Search closed");
    }

    /// <summary>
    /// Start one immutable search request. The session owns replacement,
    /// cancellation identity, and source precedence; this ViewModel remains
    /// the UI-thread publication adapter. See #1285.
    /// </summary>
    private void StartSearch(TimeSpan debounceDelay)
    {
        if (string.IsNullOrWhiteSpace(_searchText))
        {
            _searchSession.Cancel();
            ClearSearch();
            return;
        }

        var request = _searchSession.Begin(new DocumentSearchQuery(
            _searchText,
            _searchCaseSensitive,
            _searchWholeWords,
            _searchUseRegex));
        OperationStatus = "Searching…";
        _ = Task.Run(() => ExecuteSearchAsync(request, debounceDelay));
    }

    private async Task ExecuteSearchAsync(
        DocumentSearchRequest request,
        TimeSpan debounceDelay)
    {
        try
        {
            if (debounceDelay > TimeSpan.Zero)
                await Task.Delay(debounceDelay, request.CancellationToken).ConfigureAwait(false);
            if (!_searchSession.IsCurrent(request)) return;

            var searchStartedTimestamp = Stopwatch.GetTimestamp();
            _logger.LogInformation(
                "Searching for '{Query}' (CaseSensitive={CaseSensitive}, WholeWords={WholeWords}, UseRegex={UseRegex})",
                request.Query.Text,
                request.Query.CaseSensitive,
                request.Query.WholeWords,
                request.Query.UseRegex);

            PostSearchStarted(request);
            var result = _searchSession.Execute(
                request,
                TextIndex,
                PdfCoreDocument,
                _currentFilePath,
                CreateSearchProgress(request));
            if (!result.HasSource)
            {
                PostClearSearchStatus(request);
                return;
            }

            QueueSearchResults(request, result, searchStartedTimestamp);
            _logger.LogInformation("Found {MatchCount} matches", result.Matches.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Search cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error performing search: {Message}", ex.Message);
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_searchSession.IsCurrent(request))
                    ClearSearchStatus();
            });
        }
    }

    private void PostSearchStarted(DocumentSearchRequest request)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_searchSession.IsCurrent(request)) return;
            IsSearching = true;
            SearchProgressText = "Searching…";
        });
    }

    private IProgress<PdfSearchService.SearchProgress> CreateSearchProgress(
        DocumentSearchRequest request) =>
        new Progress<PdfSearchService.SearchProgress>(progress =>
        {
            if (!_searchSession.IsCurrent(request)) return;
            var progressText = FormatSearchProgress(progress);
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_searchSession.IsCurrent(request))
                    SearchProgressText = progressText;
            }, global::Avalonia.Threading.DispatcherPriority.Background);
        });

    private static string FormatSearchProgress(PdfSearchService.SearchProgress progress) =>
        progress.PagesScanned == 0
            ? $"Searching… 0 of {progress.TotalPages} pages"
            : $"Searching… page {progress.PagesScanned} of {progress.TotalPages} — " +
              $"{progress.MatchesFound} match{(progress.MatchesFound == 1 ? "" : "es")} so far";

    private void QueueSearchResults(
        DocumentSearchRequest request,
        DocumentSearchResult result,
        long searchStartedTimestamp)
    {
        LastSearchWorkerElapsedMs = result.WorkerElapsedMilliseconds;
        var publishQueuedTimestamp = Stopwatch.GetTimestamp();
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() => PublishSearchResults(
            request,
            result,
            searchStartedTimestamp,
            publishQueuedTimestamp));
    }

    private void PublishSearchResults(
        DocumentSearchRequest request,
        DocumentSearchResult result,
        long searchStartedTimestamp,
        long publishQueuedTimestamp)
    {
        if (!_searchSession.IsCurrent(request)) return;
        LastSearchUiQueueElapsedMs = ElapsedMillisecondsSince(publishQueuedTimestamp);
        var publishStartedTimestamp = Stopwatch.GetTimestamp();

        SearchMatches = new ObservableCollection<SearchMatch>(result.Matches);
        CurrentSearchMatchIndex = SearchMatches.Count > 0 ? 0 : -1;
        this.RaisePropertyChanged(nameof(SearchResultText));
        ClearSearchStatus();
        LastSearchUiPublishElapsedMs = ElapsedMillisecondsSince(publishStartedTimestamp);
        LastSearchTotalElapsedMs = ElapsedMillisecondsSince(searchStartedTimestamp);

        if (SearchMatches.Count > 0)
        {
            var firstMatch = SearchMatches[0];
            global::Avalonia.Threading.Dispatcher.UIThread.Post(
                () => NavigateToSearchMatch(firstMatch),
                global::Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private static long ElapsedMillisecondsSince(long startTimestamp) =>
        (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

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
    public void JumpToSearchMatch(SearchMatch match)
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
    private void NavigateToSearchMatch(SearchMatch match)
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
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            CurrentPageSearchHighlights.Clear();

            if (SearchMatches.Count == 0 || _documentService == null)
                return;

            // Get matches for current page from the per-page index (#601):
            // O(matches on this page) rather than an O(total matches) scan on
            // every page navigation.
            if (!_matchesByPage.TryGetValue(CurrentPageIndex, out var pageMatches) || pageMatches.Count == 0)
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
        _matchesByPage.Clear();
        CurrentSearchMatchIndex = -1;
        ClearSearchStatus();
        this.RaisePropertyChanged(nameof(SearchResultText));
    }

    private void PostClearSearchStatus(DocumentSearchRequest request)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_searchSession.IsCurrent(request))
                ClearSearchStatus();
        });
    }

    private void ClearSearchStatus()
    {
        IsSearching = false;
        SearchProgressText = string.Empty;
        if (IsSearchOperationStatus(OperationStatus))
            OperationStatus = string.Empty;
    }

    private static bool IsSearchOperationStatus(string status) =>
        status.StartsWith("Searching", StringComparison.Ordinal);
}
