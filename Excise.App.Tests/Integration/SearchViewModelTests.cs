using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Xunit;
namespace Excise.App.Tests.Integration;

/// <summary>
/// VM-level search integration. Exercises the exact path the GUI uses
/// (load doc → set SearchText → SearchMatches populated → highlights
/// computed for current page). Pre-fix the user reported that search
/// found nothing in the GUI even though the underlying service finds
/// hundreds of matches; these tests pin the VM bridge.
///
/// Uses [FixedAvaloniaFact] because <see cref="MainWindowViewModel"/>'s
/// search code publishes results via Dispatcher.UIThread.Post — without
/// a running Avalonia dispatcher the post never fires and SearchMatches
/// stays empty.
///
/// #1768: four of the five tests below returned at the top on a book path
/// that was the empty string. Ported onto a synthetic 5-page document —
/// <c>TestPdfGenerator.CreateMultiPagePdf</c> draws "Page N Content" and
/// "Secret on Page N" on every page, giving both a multi-hit-per-page term
/// ("Page") and a one-hit-per-page term across several pages ("Secret").
/// </summary>
[Collection("AvaloniaTests")]
public class SearchViewModelTests : IDisposable
{
    private const int PageCount = 5;
    private readonly ITestOutputHelper _out;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "excise-search-vm-" + Guid.NewGuid().ToString("N"));

    public SearchViewModelTests(ITestOutputHelper o) { _out = o; }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private string CreateDoc()
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "five-pages.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: PageCount);
        return path;
    }

    [FixedAvaloniaFact]
    public async Task PragmaticBook_VmSearch_PopulatesSearchMatches()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(CreateDoc());

        // Setting SearchText schedules a debounced search (300 ms wait
        // + service walk + Dispatcher.UIThread.Post to publish results).
        vm.SearchText = "Page";

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && vm.SearchMatches.Count == 0)
            await Task.Delay(50);

        _out.WriteLine($"SearchMatches.Count = {vm.SearchMatches.Count}");
        if (vm.SearchMatches.Count > 0)
            _out.WriteLine(
                $"first match: page {vm.SearchMatches[0].PageIndex + 1}, " +
                $"text='{vm.SearchMatches[0].MatchedText}', " +
                $"box=({vm.SearchMatches[0].X:F1},{vm.SearchMatches[0].Y:F1}," +
                $"{vm.SearchMatches[0].Width:F1}×{vm.SearchMatches[0].Height:F1})");

        vm.SearchMatches.Should().HaveCount(PageCount * 2,
            "'Page' appears twice per page ('Page N Content' and 'Secret on Page N') — " +
            "if SearchMatches is empty or short the VM bridge is broken");
    }

    [FixedAvaloniaFact]
    public async Task PragmaticBook_VmSearch_ComputesPageHighlights()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(CreateDoc());

        vm.SearchText = "Content";
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && vm.SearchMatches.Count == 0)
            await Task.Delay(50);

        // After NavigateToSearchMatch the VM jumps to the page with the
        // first match and UpdateSearchHighlights computes screenRects.
        for (int i = 0; i < 20 && vm.CurrentPageSearchHighlights.Count == 0; i++)
            await Task.Delay(50); // let the dispatcher post settle

        _out.WriteLine(
            $"After search: CurrentPageIndex={vm.CurrentPageIndex}, " +
            $"highlights={vm.CurrentPageSearchHighlights.Count}");

        vm.SearchMatches.Should().HaveCount(PageCount, "'Content' appears once per page");
        vm.CurrentPageSearchHighlights.Should().NotBeEmpty(
            "the current page should contain at least one highlight rectangle " +
            "after the VM auto-navigates to the first match");

        // Each highlight rect should be a positive-area box; otherwise the
        // overlay control draws nothing visible.
        foreach (var r in vm.CurrentPageSearchHighlights)
        {
            r.Space.Should().Be(Excise.Core.Document.PdfCoordinateSpace.ContentPoints,
                "search results should stay in PDF content coordinates until the viewer draws them");
            r.Width.Should().BeGreaterThan(0);
            r.Height.Should().BeGreaterThan(0);
        }
    }

    [FixedAvaloniaFact]
    public async Task PragmaticBook_JumpToSearchMatch_NavigatesToMatchPage()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(CreateDoc());

        vm.SearchText = "Secret";
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && vm.SearchMatches.Count < 2)
            await Task.Delay(50);

        vm.SearchMatches.Should().HaveCount(PageCount,
            "'Secret on Page N' appears once per page — we need at least " +
            "two matches to verify jumping moves between distinct locations");

        // First match is normally on page 1.
        vm.CurrentPageIndex.Should().Be(vm.SearchMatches[0].PageIndex,
            "VM auto-navigates to first match");
        vm.CurrentSearchMatchIndex.Should().Be(0);

        // Jump to a later match on a different page.
        var laterMatch = vm.SearchMatches.First(m => m.PageIndex != vm.SearchMatches[0].PageIndex);
        vm.JumpToSearchMatch(laterMatch);
        await Task.Delay(100);

        vm.CurrentPageIndex.Should().Be(laterMatch.PageIndex,
            "JumpToSearchMatch must navigate to that match's page");
        vm.CurrentSearchMatchIndex.Should().Be(vm.SearchMatches.IndexOf(laterMatch),
            "selected-match index updates so prev/next resume from here");
    }

    [FixedAvaloniaFact]
    public void RightSidebarPanelSelectors_AreMutuallyExclusive()
    {
        var vm = MainWindowViewModelTestFactory.Create();

        // Default: no document, no redaction mode, no search → clipboard.
        vm.ShowSearchResultsPanel.Should().BeFalse();
        vm.ShowPendingRedactionsPanel.Should().BeFalse();
        vm.ShowClipboardHistoryPanel.Should().BeTrue();

        vm.IsRedactionMode = true;
        vm.ShowPendingRedactionsPanel.Should().BeTrue("redaction mode → pending");
        vm.ShowClipboardHistoryPanel.Should().BeFalse();
        vm.ShowSearchResultsPanel.Should().BeFalse();

        vm.IsSearchVisible = true;
        vm.ShowSearchResultsPanel.Should().BeTrue(
            "search bar trumps redaction-mode for the right sidebar");
        vm.ShowPendingRedactionsPanel.Should().BeFalse();
        vm.ShowClipboardHistoryPanel.Should().BeFalse();

        vm.IsSearchVisible = false;
        vm.ShowPendingRedactionsPanel.Should().BeTrue(
            "closing search returns to redaction view");
    }

    [FixedAvaloniaFact]
    public async Task PragmaticBook_VmSearch_HighlightCoordsAreInBitmapDips()
    {
        // Highlights must be in the same DIP space the bitmap renders into
        // (120 DPI = page-points × 1.667). Pre-fix this used 150/72 which
        // pushed highlights ~25 % off the page.
        var vm = MainWindowViewModelTestFactory.Create();
        await vm.LoadDocumentAsync(CreateDoc());

        vm.SearchText = "Secret";
        // Wait long enough to cover the 300 ms search debounce + the
        // service walk + dispatcher post; cross-test contention on the
        // shared headless dispatcher can stretch this on a busy run.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && vm.SearchMatches.Count == 0)
            await Task.Delay(50);
        for (int i = 0; i < 20 && vm.CurrentPageSearchHighlights.Count == 0; i++)
            await Task.Delay(50);

        var page = vm.PdfCoreDocument!.GetPage(vm.CurrentPageIndex + 1);
        vm.CurrentPageSearchHighlights.Should().NotBeEmpty(
            "'Secret' appears on every page of the document");
        foreach (var r in vm.CurrentPageSearchHighlights)
        {
            r.Space.Should().Be(Excise.Core.Document.PdfCoordinateSpace.ContentPoints);
            r.X.Should().BeInRange(0, page.Width,
                $"highlight X must fit inside the {page.Width:F0}-point page; " +
                $"got X={r.X:F1}, W={r.Width:F1}");
            r.Y.Should().BeInRange(0, page.Height,
                $"highlight Y must fit inside the {page.Height:F0}-point page; " +
                $"got Y={r.Y:F1}, H={r.Height:F1}");
        }
    }
}
