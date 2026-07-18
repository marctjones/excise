using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
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
/// </summary>
[Collection("AvaloniaTests")]
public class SearchViewModelTests
{
    private readonly ITestOutputHelper _out;
    public SearchViewModelTests(ITestOutputHelper o) { _out = o; }

    private const string PragmaticBook =
        "/home/marc/Downloads/business-success-with-open-source_P1.0.pdf";

    [FixedAvaloniaFact]
    public async Task PragmaticBook_VmSearch_PopulatesSearchMatches()
    {
        if (!File.Exists(PragmaticBook)) return;

        var vm = new MainWindowViewModel();
        await vm.LoadDocumentAsync(PragmaticBook);

        // Setting SearchText schedules a debounced search (300 ms wait
        // + service walk + Dispatcher.UIThread.Post to publish results).
        vm.SearchText = "open source";

        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline && vm.SearchMatches.Count == 0)
            await Task.Delay(200);

        _out.WriteLine($"SearchMatches.Count = {vm.SearchMatches.Count}");
        if (vm.SearchMatches.Count > 0)
            _out.WriteLine(
                $"first match: page {vm.SearchMatches[0].PageIndex + 1}, " +
                $"text='{vm.SearchMatches[0].MatchedText}', " +
                $"box=({vm.SearchMatches[0].X:F1},{vm.SearchMatches[0].Y:F1}," +
                $"{vm.SearchMatches[0].Width:F1}×{vm.SearchMatches[0].Height:F1})");

        vm.SearchMatches.Should().NotBeEmpty(
            "the service finds 481 matches for 'open source' in this book — " +
            "if SearchMatches is empty the VM bridge is broken");
    }

    [FixedAvaloniaFact]
    public async Task PragmaticBook_VmSearch_ComputesPageHighlights()
    {
        if (!File.Exists(PragmaticBook)) return;

        var vm = new MainWindowViewModel();
        await vm.LoadDocumentAsync(PragmaticBook);

        vm.SearchText = "Open Source";
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline && vm.SearchMatches.Count == 0)
            await Task.Delay(200);

        // After NavigateToSearchMatch the VM jumps to the page with the
        // first match and UpdateSearchHighlights computes screenRects.
        await Task.Delay(800); // let the dispatcher post settle

        _out.WriteLine(
            $"After search: CurrentPageIndex={vm.CurrentPageIndex}, " +
            $"highlights={vm.CurrentPageSearchHighlights.Count}");

        vm.SearchMatches.Should().NotBeEmpty();
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
        if (!File.Exists(PragmaticBook)) return;

        var vm = new MainWindowViewModel();
        await vm.LoadDocumentAsync(PragmaticBook);

        vm.SearchText = "Brasseur";
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline && vm.SearchMatches.Count < 2)
            await Task.Delay(200);

        vm.SearchMatches.Should().HaveCountGreaterThan(1,
            "the book uses 'Brasseur' on multiple pages — we need at least " +
            "two matches to verify jumping moves between distinct locations");

        // First match is normally on page 3 (back-cover praise).
        vm.CurrentPageIndex.Should().Be(vm.SearchMatches[0].PageIndex,
            "VM auto-navigates to first match");
        vm.CurrentSearchMatchIndex.Should().Be(0);

        // Jump to a later match on a different page.
        var laterMatch = vm.SearchMatches.First(m => m.PageIndex != vm.SearchMatches[0].PageIndex);
        vm.JumpToSearchMatch(laterMatch);
        await Task.Delay(200);

        vm.CurrentPageIndex.Should().Be(laterMatch.PageIndex,
            "JumpToSearchMatch must navigate to that match's page");
        vm.CurrentSearchMatchIndex.Should().Be(vm.SearchMatches.IndexOf(laterMatch),
            "selected-match index updates so prev/next resume from here");
    }

    [FixedAvaloniaFact]
    public void RightSidebarPanelSelectors_AreMutuallyExclusive()
    {
        var vm = new MainWindowViewModel();

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
        if (!File.Exists(PragmaticBook)) return;

        var vm = new MainWindowViewModel();
        await vm.LoadDocumentAsync(PragmaticBook);

        vm.SearchText = "Brasseur";
        // Wait long enough to cover the 300 ms search debounce + the
        // service walk + dispatcher post; cross-test contention on the
        // shared headless dispatcher can stretch this on a busy run.
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline && vm.SearchMatches.Count == 0)
            await Task.Delay(200);
        await Task.Delay(800);

        var page = vm.PdfCoreDocument!.GetPage(vm.CurrentPageIndex + 1);
        vm.CurrentPageSearchHighlights.Should().NotBeEmpty(
            "Brasseur appears on multiple pages of the book");
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
