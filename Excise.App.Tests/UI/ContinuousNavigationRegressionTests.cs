using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Avalonia.Controls;
using Avalonia.Threading;
using Excise.Avalonia.Controls;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Programmatic page navigation must survive continuous mode.
///
/// Found by the v2.28.0 release gate. Making continuous scroll the default
/// exposed a latent bug in the viewer:
///
///   ScrollToPageContinuous sets ScrollViewer.Offset. A ScrollViewer CLAMPS
///   Offset to its extent, and before layout has run the extent is 0 — so the
///   assignment silently becomes Offset.Y = 0. The scroll handler then computes
///   "topmost visible page = 1" and OVERWRITES CurrentPage back to 1, swallowing
///   the navigation. Worse, when the document had only just loaded, the slots
///   were still null and the navigation was dropped before it was even attempted.
///
/// For a user, that is: open a document, immediately click an outline entry / type
/// a page number / jump to a search hit — and land on page 1 with no feedback.
///
/// It was invisible while single-page was the default, which is exactly why the
/// default change had to be gated. These tests pin the behaviour so it cannot
/// regress the next time the continuous scroll pipeline is touched.
/// </summary>
[Collection("AvaloniaTests")]
public class ContinuousNavigationRegressionTests
{
    private static string TempPdf(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "excise-nav-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task NavigatingImmediatelyAfterOpen_IsNotSwallowedByTheScrollSync()
    {
        var path = TempPdf("continuous-nav.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 8);

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        await vm.LoadDocumentAsync(path);

        vm.IsContinuousView.Should().BeTrue("continuous scroll is the default view mode");

        // The instant after open — before layout has necessarily settled. This is
        // the exact window in which the navigation used to be lost.
        vm.CurrentPageIndex = 4;

        await KeyboardTestHelpers.FlushDispatcherAsync();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.CurrentPageIndex.Should().Be(4,
            "a page set programmatically must stick. The ScrollViewer clamps Offset to a " +
            "not-yet-computed extent, and the scroll handler then derives CurrentPage from " +
            "that stale offset and snaps the user back to page 1.");
    }

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task ScrollDrivenPageSync_StillWorksAfterAPendingNavigationResolves()
    {
        // The inverse guard. The fix suppresses the scroll->CurrentPage sync while a
        // programmatic jump is in flight. If that suppression ever failed to clear,
        // the page number would freeze while the user scrolls — trading a lost jump
        // for a dead page counter.
        var path = TempPdf("continuous-nav-sync.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 8);

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        await vm.LoadDocumentAsync(path);

        vm.CurrentPageIndex = 3;
        await KeyboardTestHelpers.FlushDispatcherAsync();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.CurrentPageIndex.Should().Be(3);

        // And a subsequent navigation still lands — proving the pending-page latch
        // was released rather than left permanently engaged.
        vm.CurrentPageIndex = 6;
        await KeyboardTestHelpers.FlushDispatcherAsync();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.CurrentPageIndex.Should().Be(6,
            "the pending-navigation latch must clear once the jump lands, or every later " +
            "scroll/navigation is silently ignored");
    }

    /// <summary>
    /// #1842 step 0 (c): the scroll → page → scroll loop. A scroll derives
    /// <c>CurrentPage</c> from the offset (the top-edge anchor, #1650); that write must
    /// not come back as a "go to page" that snaps the reader to the page's top. The
    /// guard is <c>_syncingPageFromScroll</c>, set around the write and read by
    /// <c>OnCurrentPageChanged</c>; the split puts the write in the continuous view
    /// and the reaction in the facade, which is exactly where such a guard gets lost.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task ScrollDerivedPageChange_DoesNotScrollBackToThePageTop()
    {
        var path = TempPdf("continuous-scroll-anchor.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 8);

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(path);
            vm.IsContinuousView.Should().BeTrue("continuous scroll is the default view mode");

            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            var cont = viewer.ContinuousScrollViewer!;
            var items = viewer.ContinuousItems!;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && !(items.ItemsSource != null && cont.Extent.Height > cont.Viewport.Height * 3))
            {
                await KeyboardTestHelpers.FlushDispatcherAsync();
                window.UpdateLayout();
            }
            viewer.CurrentPage.Should().Be(1, "fixture: the document opened on page 1");

            var page3 = items.ItemsSource!.Cast<PdfPageSlot>().Single(s => s.PageNumber == 3);
            double target = page3.TopDip + 0.4 * page3.DisplayHeight;
            cont.Offset = new global::Avalonia.Vector(cont.Offset.X, target);
            for (int i = 0; i < 3; i++)
            {
                await KeyboardTestHelpers.FlushDispatcherAsync();
                window.UpdateLayout();
            }

            viewer.CurrentPage.Should().Be(3, "the scroll moved page 3 to the viewport top");
            vm.CurrentPageIndex.Should().Be(2);
            cont.Offset.Y.Should().BeApproximately(target, 1.0,
                "a page change the scroll itself produced must not scroll the reader to that page's top");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// #1842 step 0 (f), the "dead branch" of <c>OnCurrentPageChanged</c>
    /// (<c>PdfViewerControl.axaml.cs</c>, the trailing
    /// <c>if (ViewMode == Continuous &amp;&amp; !_syncingPageFromScroll) ScrollToPageContinuous(CurrentPage)</c>).
    /// It is NOT unreachable: the single-page branch awaits the page render and then
    /// reads <c>ViewMode</c> again, so a switch to continuous view during that await
    /// runs it. Pinned as it behaves today (#1930 decides whether to keep it): the
    /// late scroll drops the reading fraction the mode switch carried and leaves the
    /// reader at the page's top.
    ///
    /// <para>Deterministic, not a race: the page-2 render-ahead is held on its render
    /// thread, so the page turn joins it and stays inside the await while the view
    /// switches and settles; releasing the hold lets the turn finish.</para>
    /// </summary>
    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task PageTurnStillRenderingWhenTheViewSwitchesToContinuous_EndsAtThePageTop()
    {
        var path = TempPdf("continuous-late-turn.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 4);
        var bytes = File.ReadAllBytes(path);

        using var hold = new System.Threading.ManualResetEventSlim(false);
        var viewer = new PdfViewerControl();
        viewer.SinglePageLookAheadStartingForTests = page =>
        {
            if (page == 2) hold.Wait(TimeSpan.FromSeconds(30));
        };
        var window = new Window { Content = viewer, Width = 900, Height = 700 };
        window.Show();
        viewer.Document = Excise.Core.Document.PdfDocument.Open(bytes);
        try
        {
            var single = viewer.FindControl<ScrollViewer>("PdfScrollViewer")!;
            var cont = viewer.ContinuousScrollViewer!;
            var items = viewer.ContinuousItems!;
            await PumpUntilAsync(window, () => viewer.SinglePageLookAheadInFlight && !viewer.IsLoading
                && single.Extent.Height > single.Viewport.Height + 100, "page 1 shown and page 2 rendering ahead");

            const double fraction = 0.2;
            single.Offset = new global::Avalonia.Vector(single.Offset.X, fraction * single.Extent.Height);
            await PumpUntilAsync(window, () => Math.Abs(single.Offset.Y / single.Extent.Height - fraction) < 0.001,
                "the single-page scroller at the probe fraction");

            int pageChanged = 0;
            viewer.PageChanged += (_, _) => pageChanged++;
            viewer.CurrentPage = 2;
            viewer.IsLoading.Should().BeTrue("fixture: the turn joined the held render-ahead and is still awaiting it");
            viewer.ViewMode = PdfViewMode.Continuous;

            PdfPageSlot Page2() => items.ItemsSource!.Cast<PdfPageSlot>().Single(s => s.PageNumber == 2);
            await PumpUntilAsync(window, () => items.ItemsSource != null && cont.Extent.Height > 0
                && Math.Abs(cont.Offset.Y - (Page2().TopDip + fraction * Page2().DisplayHeight)) < 1,
                "continuous view at the carried fraction of page 2");
            pageChanged.Should().Be(0, "fixture: the page turn has not finished yet");

            hold.Set();
            await PumpUntilAsync(window, () => pageChanged > 0, "the page turn to finish");
            for (int i = 0; i < 5; i++) await PumpOnceAsync(window);

            viewer.CurrentPage.Should().Be(2);
            cont.Offset.Y.Should().BeApproximately(Page2().TopDip, 1.0,
                "today the finishing turn scrolls continuous view to the page top, dropping the carried fraction");
        }
        finally
        {
            hold.Set();
            viewer.SinglePageLookAheadStartingForTests = null;
            window.Close();
            Dispatcher.UIThread.RunJobs();
            viewer.Document?.Dispose();
            File.Delete(path);
        }
    }

    private static async Task PumpOnceAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(
            () => { if (window.IsVisible) window.UpdateLayout(); }, DispatcherPriority.Background);
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(25);
    }

    private static async Task PumpUntilAsync(Window window, Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"timed out waiting for {what}");
            await PumpOnceAsync(window);
        }
    }
}
