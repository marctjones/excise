using Xunit;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.ViewModels;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Tests for B1 search UX polish:
/// - Incremental search debounce (150 ms in the current implementation)
/// - Match counter display ("3 of 47")
/// </summary>
public class SearchDebounceTests
{
    /// <summary>
    /// The class is named for debounce, so it must contain one. The view model
    /// logs "Searching for '{Query}'" at the moment a request survives the
    /// debounce window and is about to run, which is the observable edge: a
    /// keystroke that is superseded inside the window must never reach it.
    /// </summary>
    [Fact]
    public async Task TypingFasterThanTheDebounceWindow_RunsOnlyTheLastQuery()
    {
        var log = new CapturingLogger();
        var vm = MainWindowViewModelTestFactory.Create(logger: log);

        // Each pause is far below the 150 ms debounce, so 'a' and 'ab' are
        // superseded before their delay elapses. Without a debounce 'a' would
        // start immediately and be logged well inside the first pause.
        vm.SearchText = "a";
        await Task.Delay(30);
        vm.SearchText = "ab";
        await Task.Delay(30);
        vm.SearchText = "abc";

        await WaitUntilAsync(() => log.SearchedFor.Contains("abc"));
        // Give a wrongly-surviving earlier request time to show up after the
        // last one; a superseded request can only ever log BEFORE it.
        await Task.Delay(100);

        log.SearchedFor.Should().Equal(new[] { "abc" },
            "'a' and 'ab' were typed inside the debounce window and must be cancelled, not run");
    }

    [Fact]
    public async Task ChangingASearchOption_IsDebouncedLikeTyping()
    {
        var log = new CapturingLogger();
        var vm = MainWindowViewModelTestFactory.Create(logger: log);
        vm.SearchText = "word";
        await WaitUntilAsync(() => log.SearchedFor.Count == 1);

        vm.SearchWholeWords = true;
        await Task.Delay(30);
        vm.SearchCaseSensitive = true;

        await WaitUntilAsync(() => log.SearchedFor.Count >= 2);
        await Task.Delay(100);

        log.SearchedFor.Should().HaveCount(2,
            "two option toggles inside one debounce window collapse into one re-search");
    }

    [Fact]
    public void SearchResultTextShowsMatchCounter()
    {
        var vm = MainWindowViewModelTestFactory.Create();

        // No matches initially
        vm.SearchResultText.Should().Be("No matches");

        // Add fake matches by manipulating the collection (in real tests, we load a PDF)
        vm.SearchMatches.Add(new SearchMatch { PageIndex = 0 });
        vm.SearchMatches.Add(new SearchMatch { PageIndex = 0 });
        vm.SearchMatches.Add(new SearchMatch { PageIndex = 0 });

        // Set current match index to first one
        vm.CurrentSearchMatchIndex = 0;

        // Should show "1 of 3"
        vm.SearchResultText.Should().Be("1 of 3");
    }

    [Fact]
    public void SearchResultTextUpdatesAsCurrentMatchChanges()
    {
        var vm = MainWindowViewModelTestFactory.Create();

        vm.SearchMatches.Add(new SearchMatch { PageIndex = 0 });
        vm.SearchMatches.Add(new SearchMatch { PageIndex = 0 });
        vm.SearchMatches.Add(new SearchMatch { PageIndex = 0 });

        vm.CurrentSearchMatchIndex = 0;
        vm.SearchResultText.Should().Be("1 of 3");

        vm.CurrentSearchMatchIndex = 1;
        vm.SearchResultText.Should().Be("2 of 3");

        vm.CurrentSearchMatchIndex = 2;
        vm.SearchResultText.Should().Be("3 of 3");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("the debounced search never started");
            await Task.Delay(10);
        }
    }

    /// <summary>Records the query of every "Searching for '…'" line.</summary>
    private sealed class CapturingLogger : ILogger<MainWindowViewModel>
    {
        private readonly ConcurrentQueue<string> _queries = new();

        public System.Collections.Generic.IReadOnlyCollection<string> SearchedFor => _queries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            const string prefix = "Searching for '";
            var message = formatter(state, exception);
            if (!message.StartsWith(prefix, StringComparison.Ordinal)) return;
            var end = message.IndexOf("' (", StringComparison.Ordinal);
            _queries.Enqueue(message.Substring(prefix.Length, end - prefix.Length));
        }
    }
}
