using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.App.Tests.Utilities;
using Xunit;
using PdfCoreDocument = Excise.Core.Document.PdfDocument;

namespace Excise.App.Tests.Controls;

/// <summary>
/// #846 orchestration oracle. Phase A proved the reading-anchor MATH preserves
/// (page, fraction) across a rotate; this asserts the WIRING does too end-to-end:
/// rotating the page the reader is on must keep that page at the same intra-page
/// fraction in the continuous view, not jump the reader to the top of the page (or
/// leave the viewport displaced as the rebuilt extent settles).
///
/// This exercises the offset/extent/anchor path, which settles headlessly even
/// though the tile bitmaps do not render — so it catches the defect the headless
/// host CAN observe (the scroll offset), which is exactly where #846's
/// "former top off-screen" lives. Before the fix RebuildContinuous restored the
/// page at fraction 0 (top); this asserts the snapshotted fraction is restored.
/// </summary>
[Collection("AvaloniaTests")]
public class ContinuousRotateReadingAnchorTests
{
    private readonly ITestOutputHelper _out;
    public ContinuousRotateReadingAnchorTests(ITestOutputHelper o) { _out = o; }

    [FixedAvaloniaFact]
    public async Task RotatingTheCurrentPage_KeepsTheReaderAtTheSameIntraPageFraction()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"excise-rotanchor-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 8);

        Excise.App.ViewModels.MainWindowViewModel vm = null!;
        Excise.App.Views.MainWindow window = null!;
        PdfViewerControl viewer = null!;
        ScrollViewer sv = null!;
        ItemsControl items = null!;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
            window = new Excise.App.Views.MainWindow { DataContext = vm, Width = 1100, Height = 900 };
            window.Show();
        });
        await Dispatcher.UIThread.InvokeAsync(async () => await vm.LoadDocumentAsync(path));

        // Wait for the continuous slots + a real scroll extent.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            bool ready = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                window.UpdateLayout();
                viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
                items = viewer?.FindControl<ItemsControl>("ContinuousItems")!;
                sv = viewer?.FindControl<ScrollViewer>("ContinuousScrollViewer")!;
                ready = sv != null && items?.ItemsSource != null
                        && items.ItemsSource.Cast<PdfPageSlot>().Count() == 8
                        && sv.Extent.Height > sv.Viewport.Height * 1.5;
            });
            if (ready) break;
            await Task.Delay(100);
        }
        sv.Should().NotBeNull("continuous view must build its scroll extent");

        // The page + fraction at the VIEWPORT TOP — the reader's actual position.
        (int Page, double Frac) TopAnchor()
        {
            var slots = items.ItemsSource!.Cast<PdfPageSlot>().OrderBy(s => s.PageNumber).ToList();
            double y = sv.Offset.Y;
            foreach (var s in slots)
                if (y >= s.TopDip && y <= s.TopDip + s.DisplayHeight)
                    return (s.PageNumber, s.DisplayHeight > 0 ? (y - s.TopDip) / s.DisplayHeight : 0);
            return (slots[0].PageNumber, 0);
        }

        // Navigate to page 4 FIRST (so CurrentPage is already 4), then nudge to a
        // mid-page offset. Setting the offset while CurrentPage changes triggers the
        // scroll->CurrentPage->scroll loop which snaps back to the page top; being
        // already on the page avoids that.
        int page = 0; double fractionBefore = 0;
        await Dispatcher.UIThread.InvokeAsync(() => vm.CurrentPageIndex = 3);
        await Pump(window, 10);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var p4 = items.ItemsSource!.Cast<PdfPageSlot>().Single(s => s.PageNumber == 4);
            sv.Offset = new Vector(0, p4.TopDip + 0.4 * p4.DisplayHeight);
        });
        await Pump(window, 4);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            (page, fractionBefore) = TopAnchor();
            _out.WriteLine($"before: offset={sv.Offset.Y:F1} page={page} frac={fractionBefore:F3}");
        });
        fractionBefore.Should().BeInRange(0.05, 0.95,
            "fixture sanity: the reader must be genuinely mid-page (not at the very top/bottom) or this test " +
            "can't tell a preserved fraction from a reset-to-top");

        // Rotate a page (the current page). The reading anchor must keep the reader's
        // VIEWPORT-TOP content in place regardless of which page re-lays out.
        await Dispatcher.UIThread.InvokeAsync(() => vm.RotatePageRightCommand.Execute().Subscribe());
        await Pump(window, 30); // let the rebuild + extent-settle anchor loop converge

        int pageAfter = 0; double fractionAfter = 0;
        await Dispatcher.UIThread.InvokeAsync(() => (pageAfter, fractionAfter) = TopAnchor());

        pageAfter.Should().Be(page, "the reader's viewport-top page must be unchanged after a rotate");
        fractionAfter.Should().BeApproximately(fractionBefore, 0.06,
            $"the reader was at fraction {fractionBefore:0.00} of page {page}; after the rotate they must still be " +
            "at ~that fraction, not reset to the top (#846). A large delta here is the 'former top off-screen' displacement.");

        await Dispatcher.UIThread.InvokeAsync(() => { window.Close(); vm.PdfCoreDocument?.Dispose(); });
        TestPdfGenerator.CleanupTestFile(path);
    }

    [FixedAvaloniaFact]
    public async Task MovingAnEarlierPage_KeepsReaderOnTheirContentAndFraction()
    {
        // #846 identity case: the reader is mid page 5; an EARLIER page is moved to
        // after them, so their content shifts to page 4. Anchoring to CurrentPage
        // (which the VM remaps to the reader's content) must keep them on that
        // content at the same fraction — not stranded on a stale page number.
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"excise-movanchor-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 8);

        Excise.App.ViewModels.MainWindowViewModel vm = null!;
        Excise.App.Views.MainWindow window = null!;
        PdfViewerControl viewer = null!;
        ScrollViewer sv = null!;
        ItemsControl items = null!;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
            window = new Excise.App.Views.MainWindow { DataContext = vm, Width = 1100, Height = 900 };
            window.Show();
        });
        await Dispatcher.UIThread.InvokeAsync(async () => await vm.LoadDocumentAsync(path));

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            bool ready = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                window.UpdateLayout();
                viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
                items = viewer?.FindControl<ItemsControl>("ContinuousItems")!;
                sv = viewer?.FindControl<ScrollViewer>("ContinuousScrollViewer")!;
                ready = sv != null && items?.ItemsSource != null
                        && items.ItemsSource.Cast<PdfPageSlot>().Count() == 8
                        && sv.Extent.Height > sv.Viewport.Height * 1.5;
            });
            if (ready) break;
            await Task.Delay(100);
        }

        (int Page, double Frac) TopAnchor()
        {
            var slots = items.ItemsSource!.Cast<PdfPageSlot>().OrderBy(s => s.PageNumber).ToList();
            double y = sv.Offset.Y;
            foreach (var s in slots)
                if (y >= s.TopDip && y <= s.TopDip + s.DisplayHeight)
                    return (s.PageNumber, s.DisplayHeight > 0 ? (y - s.TopDip) / s.DisplayHeight : 0);
            return (slots[0].PageNumber, 0);
        }
        string PageText(int p) => new Excise.Core.Text.TextExtractor(vm.PdfCoreDocument!.GetPage(p)).ExtractText();

        // Sit mid page 5.
        double frac = 0;
        await Dispatcher.UIThread.InvokeAsync(() => vm.CurrentPageIndex = 4);
        await Pump(window, 10);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var p5 = items.ItemsSource!.Cast<PdfPageSlot>().Single(s => s.PageNumber == 5);
            sv.Offset = new Vector(0, p5.TopDip + 0.4 * p5.DisplayHeight);
        });
        await Pump(window, 4);
        int page = 0;
        await Dispatcher.UIThread.InvokeAsync(() => (page, frac) = TopAnchor());
        page.Should().Be(5);
        frac.Should().BeApproximately(0.4, 0.06);
        PageText(5).Should().Contain("Page 5", "fixture: the reader's content is page 5");

        // Move page 2 (index 1) to after page 6 (index 6) — an earlier page relocates.
        await Dispatcher.UIThread.InvokeAsync(async () => await vm.MovePageAsync(1, 6));
        await Pump(window, 30);

        int pageAfter = 0; double fracAfter = 0;
        await Dispatcher.UIThread.InvokeAsync(() => (pageAfter, fracAfter) = TopAnchor());

        PageText(pageAfter).Should().Contain("Page 5",
            "the reader must stay on their CONTENT (old page 5, now shifted to a new page number) after an earlier page moves");
        fracAfter.Should().BeApproximately(frac, 0.06,
            "and at the same intra-page fraction, not reset to the top");

        await Dispatcher.UIThread.InvokeAsync(() => { window.Close(); vm.PdfCoreDocument?.Dispose(); });
        TestPdfGenerator.CleanupTestFile(path);
    }

    private static async Task Pump(Window window, int cycles)
    {
        for (int i = 0; i < cycles; i++)
        {
            await Task.Delay(70);
            await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout());
        }
    }
}
