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
/// #1651: page add/move/remove appeared to be lost after scrolling away.
///
/// <para>The continuous view caches rendered tiles under
/// <c>ContinuousTileKey(Page, Dpi, PageWidthDip, PageHeightDip, Col, Row)</c> —
/// keyed by page NUMBER. A structural mutation changes which page HAS a given
/// number, so every cached tile for the affected numbers is stale. The
/// structural refresh path (<c>RefreshContinuousLayout</c>) rebuilt the slots
/// and deliberately did NOT invalidate that cache: the reasoning recorded at
/// the time was "page CONTENT did not change — only the page order", which is
/// true of the pages and false of the cache keys.</para>
///
/// <para>So scrolling back to a page number re-composed the pre-mutation
/// pixels, and the edit looked lost.</para>
/// </summary>
[Collection("AvaloniaTests")]
public sealed class StructuralMutationCacheTests
{
    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task AStructuralMutation_DropsTilesKeyedByPageNumber()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-stale-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 4);
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 760 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(path);
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            vm.ViewMode = PdfViewMode.Continuous;
            var scroller = viewer.FindControl<ScrollViewer>("ContinuousScrollViewer")!;
            await PumpUntilAsync(window, () => scroller.Extent.Height > scroller.Viewport.Height);

            // Render some tiles by scrolling through the document.
            foreach (var fraction in new[] { 0.0, 0.3, 0.6 })
            {
                scroller.Offset = new Vector(0, scroller.Extent.Height * fraction);
                await Settle(window);
            }
            await PumpUntilAsync(window, () => viewer.ContinuousTileCacheResidentBytes > 0, 8000);
            var cachedBefore = viewer.ContinuousTileCacheResidentBytes;
            cachedBefore.Should().BeGreaterThan(0, "the test needs tiles in the cache to say anything");

            // Move page 4 to the front: pages 1..4 all change identity.
            vm.CurrentPageIndex = 3;
            await vm.MoveCurrentPageAsync(0).ConfigureAwait(true);

            // Measured the moment the mutation returns, BEFORE the view has had
            // a chance to re-render: nothing rendered against the old page
            // order may survive. Settling first would measure the fresh tiles
            // the refresh renders and say nothing.
            viewer.ContinuousTileCacheResidentBytes.Should().Be(0,
                "every cached tile is keyed by page NUMBER, and the mutation changed which page "
                + "has which number — keeping them re-composes the pre-mutation pixels when the "
                + "reader scrolls back, which is what #1651 reported as the edit being lost");

            // And the view does come back: the cache refills with tiles
            // rendered against the NEW order. Without this half the fix could
            // be "never cache anything", which would be a performance
            // regression dressed as a correctness fix.
            await Settle(window);
            await PumpUntilAsync(window, () => viewer.ContinuousTileCacheResidentBytes > 0, 8000);
        }
        finally
        {
            window.Close();
            try { File.Delete(path); } catch { }
        }
    }

    private static async Task Settle(Window window)
    {
        for (var i = 0; i < 8; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            await Task.Delay(30);
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
            await Task.Delay(40);
        }
        throw new TimeoutException("condition never became true");
    }
}
