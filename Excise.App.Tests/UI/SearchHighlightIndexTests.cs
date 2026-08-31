using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Behaviour-preservation guard for the #601 per-page search-match index.
/// UpdateSearchHighlights used to scan all SearchMatches on every page
/// navigation (SearchMatches.Where(m => m.PageIndex == current)); it now reads
/// a per-page index. These tests pin that the index returns EXACTLY the same
/// matches, per page, that the linear scan would — the optimization must not
/// change which highlights a page shows.
/// </summary>
[Collection("AvaloniaTests")]
public class SearchHighlightIndexTests
{
    [FixedAvaloniaFact]
    public async Task PerPageIndex_MatchesLinearScan_ForEveryPage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-search-index-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 20);

        try
        {
            var vm = MainWindowViewModelTestFactory.Create();
            await vm.LoadDocumentAsync(path);

            vm.SearchText = "Page";
            vm.FindNow();

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && (vm.SearchMatches.Count == 0 || vm.IsSearching))
                await Task.Delay(50);

            vm.SearchMatches.Should().NotBeEmpty("the generated pages all contain 'Page'");

            var index = vm.MatchesByPageIndexForBenchmark;

            // Every page's indexed set must equal the linear-scan set.
            for (int page = 0; page < vm.TotalPages; page++)
            {
                var expected = vm.SearchMatches.Where(m => m.PageIndex == page).ToList();
                index.TryGetValue(page, out var actual);
                (actual ?? new System.Collections.Generic.List<SearchMatch>())
                    .Should().Equal(expected,
                        $"the per-page index for page {page} must contain exactly the matches a linear scan finds");
            }

            // The index must account for every match exactly once, no extras.
            index.Values.Sum(v => v.Count).Should().Be(vm.SearchMatches.Count);
            index.Keys.Should().OnlyContain(k => k >= 0 && k < vm.TotalPages);
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    [FixedAvaloniaFact]
    public async Task ClearingSearch_EmptiesPerPageIndex()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-search-index-clear-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 8);

        try
        {
            var vm = MainWindowViewModelTestFactory.Create();
            await vm.LoadDocumentAsync(path);

            vm.SearchText = "Page";
            vm.FindNow();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && (vm.SearchMatches.Count == 0 || vm.IsSearching))
                await Task.Delay(50);
            vm.MatchesByPageIndexForBenchmark.Count.Should().BeGreaterThan(0);

            // Clearing the search text schedules ClearSearch on the debounce path.
            vm.SearchText = string.Empty;
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && vm.SearchMatches.Count > 0)
                await Task.Delay(50);

            vm.SearchMatches.Should().BeEmpty();
            vm.MatchesByPageIndexForBenchmark.Count.Should().Be(0,
                "clearing the search must also empty the per-page index so stale highlights cannot appear");
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(path);
        }
    }
}
