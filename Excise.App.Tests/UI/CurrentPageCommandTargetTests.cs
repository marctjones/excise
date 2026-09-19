using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Which page a "current page" command acts on (#1650).
///
/// <para>Reported live: Remove Current Page, used just after inserting a page,
/// deleted a page the reader was not looking at. The viewer's
/// <c>CurrentPage</c> is the page owning the TOP EDGE of the viewport — its job
/// is to be the scroll anchor — so two pixels of the previous page peeking in
/// at the top made it "current" while the reader filled their screen with the
/// next one.</para>
///
/// <para>The fix is a second, separate question — <c>MostVisiblePage</c> — and
/// this pins the difference rather than the fix: the test scrolls to a position
/// where the two answers DIVERGE and then checks the command followed the
/// visible one. Making CurrentPage itself most-visible was tried and reverted:
/// it broke the mode-switch reading-position carry and the zoom anchor, because
/// a zoom at a fixed offset/extent ratio legitimately changes which page
/// dominates.</para>
/// </summary>
[Collection("AvaloniaTests")]
public sealed class CurrentPageCommandTargetTests
{
    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task RemoveCurrentPage_RemovesThePageFillingTheViewport_NotTheSliverAtItsTop()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-target-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3);
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 760 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(path);
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            vm.ViewMode = PdfViewMode.Continuous;
            var scroller = viewer.FindControl<ScrollViewer>("ContinuousScrollViewer")!;
            await PumpUntilAsync(window, () => scroller.Extent.Height > scroller.Viewport.Height + 100);

            var slots = viewer.FindControl<ItemsControl>("ContinuousItems")!
                .ItemsSource!.Cast<PdfPageSlot>().ToArray();
            slots.Should().HaveCount(3);

            // Leave 2 dip of page 1 at the top of the viewport; page 2 fills
            // the rest. This is the geometry where the two rules disagree.
            var offset = slots[0].TopDip + slots[0].DisplayHeight - 2;
            scroller.Offset = new Vector(scroller.Offset.X, offset);
            await PumpUntilAsync(window, () => Math.Abs(scroller.Offset.Y - offset) < 1.5);
            for (var i = 0; i < 10; i++) { await Task.Delay(50); window.UpdateLayout(); }

            vm.CurrentPage.Should().Be(1,
                "the anchor is the page owning the top edge — that is its job and it is unchanged");
            viewer.MostVisiblePage.Should().Be(2,
                "page 2 fills the viewport; page 1 has 2 dip of it");

            await vm.RemoveCurrentPageCommand.Execute().ToTask();
            await PumpUntilAsync(window, () => vm.TotalPages == 2);

            // Page 2 is the one that must be gone. Read the surviving text from
            // the document rather than trusting the page count: a count of 2
            // would also be satisfied by removing page 1.
            var remaining = Enumerable.Range(1, vm.TotalPages)
                .Select(n => vm.PdfCoreDocument!.GetPage(n).Text ?? string.Empty)
                .ToArray();
            remaining.Should().HaveCount(2);
            string.Join("\n", remaining).Should().NotContain("Page 2 Content",
                "the page filling the viewport is the one the reader meant");
            string.Join("\n", remaining).Should().Contain("Page 1 Content",
                "the 2-dip sliver at the top of the viewport was not what the reader meant");
            string.Join("\n", remaining).Should().Contain("Page 3 Content");
        }
        finally
        {
            window.Close();
            try { File.Delete(path); } catch { }
        }
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task WithNoViewerBound_TheCommandTargetFallsBackToTheAnchor()
    {
        // A headless view model with no window has no viewport, so there is no
        // "most visible" page. Falling back to the anchor is the old behaviour
        // and still the best answer available; returning page 1 regardless
        // would be a new bug in every view-model test.
        var path = Path.Combine(Path.GetTempPath(), $"excise-target-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3);
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        try
        {
            await vm.LoadDocumentAsync(path);
            vm.CurrentPageIndex = 2;
            vm.CommandTargetPageIndex.Should().Be(2);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static async Task PumpUntilAsync(Window window, Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            if (condition())
                return;
            await Task.Delay(50);
        }
        throw new TimeoutException("condition never became true");
    }
}
