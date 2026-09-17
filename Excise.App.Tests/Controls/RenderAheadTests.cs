using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.Avalonia.Controls;
using Xunit;
using PdfCoreDocument = Excise.Core.Document.PdfDocument;
using PixelCopy = Excise.App.Tests.Controls.ContinuousTileEvictionCompositeTests.PixelCopy;

namespace Excise.App.Tests.Controls;

/// <summary>
/// #1564: the viewer renders the page a turn would show next (then the one
/// before it) once the visible page has drawn, so the turn is a cache hit.
/// On Skia with real renders. The properties pinned here are the ones the
/// change could break: it starts only after the visible band, it is cancelled
/// when the reader moves elsewhere (and NOT when the reader moves onto it), it
/// never renders what is cached, trims drop it first and do not restart it,
/// and a pre-rendered page is pixel-identical to the same page rendered cold.
/// </summary>
[Collection("AvaloniaTests")]
public class RenderAheadTests
{
    private readonly ITestOutputHelper _out;
    public RenderAheadTests(ITestOutputHelper output) => _out = output;

    // ---- Continuous view --------------------------------------------------

    [FixedAvaloniaFact]
    public async Task Continuous_TurnToAPreRenderedPage_IsACacheHit_AndPixelIdenticalToAColdTurn()
    {
        const int pageCount = 4;
        var (window, viewer, items) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(pageCount);
        var (coldWindow, coldViewer, coldItems) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(pageCount);
        coldViewer.RenderAheadEnabled = false;
        try
        {
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            await WaitLookAheadIdleAsync(window, viewer);
            viewer.ContinuousLookAheadCompletedCount.Should().Be(1, "page 2 is rendered ahead; page 0 does not exist");
            viewer.ContinuousLookAheadTilesForTests.Should().NotBeEmpty()
                .And.OnlyContain(k => k.Page == 2, "the only page a turn from page 1 would show is page 2");

            int visibleStarts = viewer.ContinuousRenderStartCount;
            viewer.CurrentPage = 2;
            var warm = await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 2);
            viewer.ContinuousRenderStartCount.Should().Be(visibleStarts,
                "every cell of the turned-to page was rendered ahead, so the turn renders nothing");
            viewer.ContinuousLookAheadTilesForTests.Should().NotContain(k => k.Page == 2,
                "a look-ahead tile the visible pass used is an ordinary tile from then on");

            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(coldWindow, coldViewer, coldItems, pageNumber: 1);
            int coldStarts = coldViewer.ContinuousRenderStartCount;
            coldViewer.CurrentPage = 2;
            var cold = await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(coldWindow, coldViewer, coldItems, pageNumber: 2);
            coldViewer.ContinuousRenderStartCount.Should().BeGreaterThan(coldStarts, "fixture: the cold turn renders");
            coldViewer.ContinuousLookAheadStartCount.Should().Be(0, "fixture: render-ahead is off on the cold viewer");

            var a = PixelCopy.Of(warm);
            var b = PixelCopy.Of(cold);
            b.InkFraction().Should().BeGreaterThan(0.0005, "fixture: page 2 must show content");
            a.Width.Should().Be(b.Width);
            a.Height.Should().Be(b.Height);
            double mismatch = a.MismatchFraction(b, channelTolerance: 0);
            _out.WriteLine($"page 2 composite {a.Width}x{a.Height}px, pre-rendered vs cold mismatch={mismatch:P4}");
            mismatch.Should().Be(0, "render-ahead changes when a page is rendered, never its pixels");
        }
        finally
        {
            Close(window, viewer);
            Close(coldWindow, coldViewer);
        }
    }

    [FixedAvaloniaFact]
    public async Task Continuous_TurnToAPreRenderedPage_ShowsItInTheSameLayoutPass_WithoutRunningTheDispatcher()
    {
        var (window, viewer, items) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(pageCount: 3);
        try
        {
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            await WaitLookAheadIdleAsync(window, viewer);
            var page2 = items.ItemsSource!.Cast<PdfPageSlot>().Single(s => s.PageNumber == 2);
            page2.Bitmap.Should().BeNull("fixture: page 2 is not realized, so it has no composite yet");

            // The frame that shows the new offset: set the page, lay out, and
            // nothing else. The posted render pass has NOT run.
            viewer.CurrentPage = 2;
            viewer.RecomposeFromCacheOnLayoutPending.Should().BeTrue("the turn scrolled");
            window.UpdateLayout();

            viewer.RecomposeFromCacheOnLayoutPending.Should().BeFalse("the layout pass consumed it");
            var sameFrame = page2.Bitmap;
            sameFrame.Should().NotBeNull(
                "every tile of page 2 was rendered ahead, so its composite is published in the frame that scrolls to it");

            var settled = await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 2);
            settled.Should().BeSameAs(sameFrame, "the posted pass finds the band already composited and leaves it");
        }
        finally
        {
            Close(window, viewer);
        }
    }

    [FixedAvaloniaFact]
    public async Task Continuous_LookAheadStartsOnlyAfterTheVisibleBandHasLanded()
    {
        var (window, viewer, items) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(pageCount: 3);
        using var hold = new ManualResetEventSlim(false);
        var started = new ConcurrentQueue<int>();
        viewer.ContinuousBandRenderStartingForTests = page =>
        {
            started.Enqueue(page);
            if (page == 1) hold.Wait(TimeSpan.FromSeconds(30));
        };
        try
        {
            await WaitUntilAsync(window, () => started.Contains(1), "page 1's band render to start");
            await PumpAsync(window, TimeSpan.FromMilliseconds(500));
            viewer.ContinuousLookAheadStartCount.Should().Be(0, "page 1 has not drawn yet");
            started.Should().NotContain(2, "nothing renders ahead while the visible band is in flight");

            hold.Set();
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            await WaitLookAheadIdleAsync(window, viewer);
            viewer.ContinuousLookAheadCompletedCount.Should().Be(1);
            started.Should().Equal(new[] { 1, 2 }, "the visible band first, then the next page");
        }
        finally
        {
            hold.Set();
            viewer.ContinuousBandRenderStartingForTests = null;
            Close(window, viewer);
        }
    }

    [FixedAvaloniaFact]
    public async Task Continuous_LookAheadIsCancelledWhenTheReaderGoesElsewhere()
    {
        var (window, viewer, items) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(pageCount: 5);
        using var holds = new RenderHolds();
        var holdPage2 = holds.Hold(2);
        viewer.ContinuousBandRenderStartingForTests = holds.Wait;
        try
        {
            await WaitUntilAsync(window, () => viewer.ContinuousLookAheadInFlight, "page 2 to be rendered ahead");
            viewer.ContinuousLookAheadCancellationRequested.Should().BeFalse();

            viewer.CurrentPage = 4;
            await WaitUntilAsync(window, () => viewer.ContinuousLookAheadCancellationRequested,
                "the jump to page 4 to cancel page 2's render-ahead");
            holdPage2.Set();
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 4);
            await WaitLookAheadIdleAsync(window, viewer);
            viewer.ContinuousLookAheadCancellationCount.Should().Be(1);
            viewer.ContinuousCacheEntriesForTests().Should().NotContain(e => e.Key.Page == 2,
                "the cancelled render cached nothing");
            viewer.ContinuousLookAheadTilesForTests.Should().NotBeEmpty()
                .And.OnlyContain(k => k.Page == 3 || k.Page == 5, "from page 4 the neighbours are rendered ahead instead");

            // Zoom: the plan's DPI and page sizes change, so its cells are dead.
            var holdPage5 = holds.Hold(5);
            viewer.ZoomLevel = 1.25;
            await WaitUntilAsync(window, () => viewer.ContinuousLookAheadInFlight, "page 5 to be rendered ahead at zoom 1.25");
            int dpiAtStart = viewer.ContinuousEffectiveRenderDpi;
            viewer.ZoomLevel = 1.5;
            await WaitUntilAsync(window, () => viewer.ContinuousLookAheadCancellationRequested,
                "the zoom change to cancel the render-ahead");
            holds.Release(5);
            holdPage5.Set();
            await WaitLookAheadIdleAsync(window, viewer);
            viewer.ContinuousLookAheadCancellationCount.Should().Be(2);
            viewer.ContinuousEffectiveRenderDpi.Should().NotBe(dpiAtStart, "fixture");
            viewer.ContinuousCacheEntriesForTests().Should().NotContain(e => e.Key.Dpi == dpiAtStart && e.Key.Page == 5,
                "a render-ahead cancelled by zoom caches nothing at the old zoom");

            // Close: the detach cancels whatever is in flight. (Page 3 is cached
            // at this zoom already — page 4 rendered it ahead — so jump to page 1
            // and hold page 2, which was only ever rendered at zoom 1.)
            var holdPage2Again = holds.Hold(2);
            holdPage2Again.Reset();
            viewer.CurrentPage = 1;
            await WaitUntilAsync(window, () => viewer.ContinuousLookAheadInFlight, "page 2 to be rendered ahead at zoom 1.5");
            window.Close();
            viewer.ContinuousLookAheadCancellationRequested.Should().BeTrue("closing the window cancels render-ahead");
            holdPage2Again.Set();
            await WaitUntilAsync(window, () => viewer.ContinuousLookAheadCancellationCount == 3,
                "the cancelled render to report in");
        }
        finally
        {
            holds.ReleaseAll();
            viewer.ContinuousBandRenderStartingForTests = null;
            Close(window, viewer);
        }
    }

    [FixedAvaloniaFact]
    public async Task Continuous_TurningOntoThePageBeingRenderedAhead_KeepsThatRender()
    {
        var (window, viewer, items) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(pageCount: 3);
        using var holds = new RenderHolds();
        var hold = holds.Hold(2);
        viewer.ContinuousBandRenderStartingForTests = holds.Wait;
        try
        {
            await WaitUntilAsync(window, () => viewer.ContinuousLookAheadInFlight, "page 2 to be rendered ahead");
            int visibleStarts = viewer.ContinuousRenderStartCount;

            viewer.CurrentPage = 2;
            await PumpAsync(window, TimeSpan.FromMilliseconds(300));
            viewer.ContinuousLookAheadCancellationRequested.Should().BeFalse(
                "the page turned to is the page being rendered: throwing that render away would make the turn slower");
            viewer.ContinuousRenderCoalescedRequestCount.Should().BeGreaterThan(0,
                "the visible pass waits on the render-ahead cells instead of rendering them again");

            hold.Set();
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 2);
            viewer.ContinuousRenderStartCount.Should().Be(visibleStarts, "page 2 came from the render-ahead");
        }
        finally
        {
            holds.ReleaseAll();
            viewer.ContinuousBandRenderStartingForTests = null;
            Close(window, viewer);
        }
    }

    [FixedAvaloniaFact]
    public async Task Continuous_NothingCached_IsRenderedAgain_AndAnIdleViewerStaysIdle()
    {
        var (window, viewer, items) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(pageCount: 4);
        try
        {
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            await WaitLookAheadIdleAsync(window, viewer);
            viewer.ContinuousLookAheadStartCount.Should().Be(1, "page 2 only");

            viewer.CurrentPage = 2;
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 2);
            await WaitLookAheadIdleAsync(window, viewer);
            viewer.ContinuousLookAheadStartCount.Should().Be(2,
                "from page 2 only page 3 is rendered ahead: page 1 is still cached from when it was read");

            // A pass at the same position (a structural refresh rebuilds the
            // slots and re-plans) finds everything cached and starts nothing.
            int visibleStarts = viewer.ContinuousRenderStartCount;
            viewer.RefreshContinuousLayout();
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 2);
            await WaitLookAheadIdleAsync(window, viewer);
            await PumpAsync(window, TimeSpan.FromMilliseconds(500));
            viewer.ContinuousLookAheadStartCount.Should().Be(2, "no render-ahead for cells already cached");
            viewer.ContinuousRenderStartCount.Should().Be(visibleStarts);
        }
        finally
        {
            Close(window, viewer);
        }
    }

    [FixedAvaloniaFact]
    public async Task Continuous_TrimsAndBudgetCutsDropLookAheadFirst_AndDoNotRestartIt()
    {
        var (window, viewer, items) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(pageCount: 6);
        try
        {
            foreach (var level in new[] { PdfViewerCacheTrimLevel.Background, PdfViewerCacheTrimLevel.Warn })
            {
                int page = level == PdfViewerCacheTrimLevel.Background ? 1 : 4;
                viewer.CurrentPage = page;
                await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, page);
                await WaitLookAheadIdleAsync(window, viewer);
                int lookAheadTiles = viewer.ContinuousLookAheadTilesForTests.Count;
                lookAheadTiles.Should().BeGreaterThan(0, "fixture: a neighbour was rendered ahead");
                var required = viewer.ContinuousRequiredKeysForTests;
                int starts = viewer.ContinuousLookAheadStartCount;

                viewer.TrimCaches(level);

                viewer.LastCacheTrim.LookAheadTiles.Should().Be(lookAheadTiles, $"{level} drops every render-ahead tile");
                viewer.ContinuousLookAheadTilesForTests.Should().BeEmpty();
                viewer.ContinuousCacheEntriesForTests().Select(e => e.Key).Should().OnlyContain(k => required.Contains(k),
                    $"{level} keeps the current bands");
                await PumpAsync(window, TimeSpan.FromMilliseconds(500));
                viewer.ContinuousLookAheadStartCount.Should().Be(starts,
                    $"a {level} trim must not re-render what it just released (#1478)");
            }

            // Budget cut: with scroll-back AND look-ahead tiles cached, a budget
            // just under the resident size evicts look-ahead tiles first.
            viewer.CurrentPage = 1;
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            await WaitLookAheadIdleAsync(window, viewer);
            var lookAheadKeys = viewer.ContinuousLookAheadTilesForTests.ToHashSet();
            lookAheadKeys.Should().NotBeEmpty("fixture");
            var entries = viewer.ContinuousCacheEntriesForTests();
            var scrollBack = entries.Where(e => !lookAheadKeys.Contains(e.Key)
                && !viewer.ContinuousRequiredKeysForTests.Contains(e.Key)).Select(e => e.Key).ToList();
            scrollBack.Should().NotBeEmpty("fixture: page 4's tiles are scroll-back now");
            var oneLookAhead = entries.First(e => lookAheadKeys.Contains(e.Key)).Bitmap;
            long resident = viewer.ContinuousTileCacheResidentBytes;

            viewer.ContinuousTileCacheByteBudget = resident -
                PdfViewerControl.ContinuousTileByteSize(oneLookAhead.PixelSize.Width, oneLookAhead.PixelSize.Height) + 1;

            var after = viewer.ContinuousCacheEntriesForTests().Select(e => e.Key).ToHashSet();
            after.Should().Contain(scrollBack, "the least-recently-used scroll-back tiles outlive render-ahead tiles");
            lookAheadKeys.Count(k => !after.Contains(k)).Should().BeGreaterThan(0, "a render-ahead tile went first");
        }
        finally
        {
            Close(window, viewer);
        }
    }

    [FixedAvaloniaFact]
    public async Task Continuous_APageRenderedAheadKeepsItsDecodedSamples_UntilTheReaderMovesOrATrim()
    {
        const int pageCount = 6;
        var (window, viewer, items) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(
            ContinuousImageSampleReleaseTests.ImageDocument(pageCount));
        try
        {
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            await WaitLookAheadIdleAsync(window, viewer);
            var doc = viewer.Document!;
            var own2 = ContinuousImageSampleReleaseTests.XObject(doc, 2, "Own");
            ContinuousImageSampleReleaseTests.RealizedPages(items).Should().NotContain(2, "fixture: page 2 is below the viewport");
            viewer.ContinuousLookAheadSamplePagesForTests.Should().Equal(new[] { 2 });
            own2.IsDecoded.Should().BeTrue(
                "the page a turn lands on keeps its samples, or the first scroll past its pre-rendered band decodes again (#1492)");

            // The turn is served from the tiles; the page is realized from then on.
            int visibleStarts = viewer.ContinuousRenderStartCount;
            viewer.CurrentPage = 2;
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 2);
            await WaitLookAheadIdleAsync(window, viewer);
            viewer.ContinuousRenderStartCount.Should().Be(visibleStarts);
            own2.IsDecoded.Should().BeTrue("page 2 is realized and never re-rendered");
            viewer.ContinuousLookAheadSamplePagesForTests.Should().Equal(new[] { 3 }, "the plan moved on to page 3");
            var own3 = ContinuousImageSampleReleaseTests.XObject(doc, 3, "Own");
            own3.IsDecoded.Should().BeTrue();

            // A far jump drops the old neighbours' samples.
            viewer.CurrentPage = 5;
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 5);
            await WaitLookAheadIdleAsync(window, viewer);
            viewer.ContinuousLookAheadSamplePagesForTests.Should().BeEquivalentTo(new[] { 6, 4 });
            own3.IsDecoded.Should().BeFalse("page 3 is neither realized nor a neighbour of page 5");

            // A trim drops the neighbours' samples too.
            var own6 = ContinuousImageSampleReleaseTests.XObject(doc, 6, "Own");
            own6.IsDecoded.Should().BeTrue("fixture");
            viewer.TrimCaches(PdfViewerCacheTrimLevel.Background);
            viewer.ContinuousLookAheadSamplePagesForTests.Should().BeEmpty();
            own6.IsDecoded.Should().BeFalse("a trim releases what render-ahead kept");
        }
        finally
        {
            Close(window, viewer);
        }
    }

    /// <summary>
    /// A closed document must be collectable whatever render-ahead did before
    /// the close. The trim case is the one the #1543 bench caught on develop
    /// beed1e8b: the idle trim ran 3 s before Close Document, and Altona's
    /// footprint stayed at 705 MB after close instead of ~326 MB, because
    /// <c>TrimCaches</c> stored the open document in the single-page
    /// look-ahead anchor (in either view) and nothing cleared it on close.
    /// </summary>
    [FixedAvaloniaTheory]
    [InlineData(PdfViewMode.Continuous, false)]
    [InlineData(PdfViewMode.Continuous, true)]
    [InlineData(PdfViewMode.SinglePage, false)]
    [InlineData(PdfViewMode.SinglePage, true)]
    public async Task ClosingTheDocument_LeavesNothingThatKeepsItAlive(PdfViewMode mode, bool trimBeforeClose)
    {
        Window window;
        PdfViewerControl viewer;
        if (mode == PdfViewMode.Continuous)
        {
            ItemsControl items;
            (window, viewer, items) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(pageCount: 4);
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            await WaitLookAheadIdleAsync(window, viewer);
            viewer.CurrentPage = 2;
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 2);
            await WaitLookAheadIdleAsync(window, viewer);
            viewer.ContinuousLookAheadStartCount.Should().BeGreaterThan(0, "fixture");
        }
        else
        {
            Image image;
            (window, viewer, image) = ShowSinglePageViewer(MultiPagePdf(4));
            viewer.CurrentPage = 2;
            await WaitUntilAsync(window, () => !viewer.IsLoading && image.Source is WriteableBitmap, "page 2 rendered");
            await WaitSinglePageLookAheadIdleAsync(window, viewer, expectedStarts: 2);
        }

        try
        {
            if (trimBeforeClose)
                viewer.TrimCaches(PdfViewerCacheTrimLevel.Background);

            var weak = DetachDocument(viewer);
            await PumpAsync(window, TimeSpan.FromMilliseconds(500));
            for (int i = 0; i < 5 && weak.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await PumpAsync(window, TimeSpan.FromMilliseconds(100));
            }
            weak.IsAlive.Should().BeFalse("a closed document must be collectable; render-ahead state must not hold it");
        }
        finally
        {
            Close(window, viewer);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference DetachDocument(PdfViewerControl viewer)
    {
        var doc = viewer.Document!;
        viewer.Document = null;
        doc.Dispose();
        return new WeakReference(doc);
    }

    // ---- Single-page view -------------------------------------------------

    [FixedAvaloniaFact]
    public async Task SinglePage_RendersNeighboursIntoTheLru_AndTheTurnIsAPixelIdenticalCacheHit()
    {
        var bytes = MultiPagePdf(4);
        var (window, viewer, image) = ShowSinglePageViewer(bytes);
        var (coldWindow, coldViewer, coldImage) = ShowSinglePageViewer(bytes, renderAhead: false);
        try
        {
            viewer.CurrentPage = 2;
            await WaitUntilAsync(window, () => !viewer.IsLoading && image.Source is WriteableBitmap, "page 2 rendered");
            await WaitSinglePageLookAheadIdleAsync(window, viewer, expectedStarts: 2);
            viewer.SinglePageCacheContainsForTests(3).Should().BeTrue("the next page is rendered ahead");
            viewer.SinglePageCacheContainsForTests(1).Should().BeTrue("then the previous one");
            viewer.SinglePageCacheContainsForTests(4).Should().BeFalse("only N±1");

            var diag = viewer.GetRenderDiagnostics();
            viewer.CurrentPage = 3;
            Dispatcher.UIThread.RunJobs();
            viewer.GetRenderDiagnostics().SinglePageHits.Should().Be(diag.SinglePageHits + 1, "the turn is a cache hit");
            viewer.GetRenderDiagnostics().SinglePageMisses.Should().Be(diag.SinglePageMisses);
            var warm = (WriteableBitmap)image.Source!;

            coldViewer.CurrentPage = 3;
            await WaitUntilAsync(coldWindow, () => !coldViewer.IsLoading && coldImage.Source is WriteableBitmap, "cold page 3 rendered");
            coldViewer.SinglePageLookAheadStartCount.Should().Be(0, "fixture: render-ahead is off on the cold viewer");
            var a = PixelCopy.Of(warm);
            var b = PixelCopy.Of((WriteableBitmap)coldImage.Source!);
            b.InkFraction().Should().BeGreaterThan(0.0005, "fixture: page 3 must show content");
            a.Width.Should().Be(b.Width);
            a.Height.Should().Be(b.Height);
            image.Width.Should().Be(coldImage.Width, "same layout size");
            image.Height.Should().Be(coldImage.Height);
            a.MismatchFraction(b, channelTolerance: 0).Should().Be(0, "render-ahead never changes a page's pixels");

            // From page 3, page 4 is rendered ahead; page 2 is still cached.
            await WaitSinglePageLookAheadIdleAsync(window, viewer, expectedStarts: 3);
            viewer.SinglePageCacheContainsForTests(4).Should().BeTrue();

            // A trim drops the neighbours and does not render them again.
            viewer.TrimCaches(PdfViewerCacheTrimLevel.Background);
            viewer.SinglePageCacheContainsForTests(2).Should().BeFalse();
            viewer.SinglePageCacheContainsForTests(4).Should().BeFalse();
            viewer.SinglePageCacheContainsForTests(3).Should().BeTrue("the page on screen stays");
            await PumpAsync(window, TimeSpan.FromMilliseconds(500));
            viewer.SinglePageLookAheadStartCount.Should().Be(3, "a trim must not re-render what it released (#1478)");
        }
        finally
        {
            Close(window, viewer);
            Close(coldWindow, coldViewer);
        }
    }

    [FixedAvaloniaFact]
    public async Task SinglePage_NavigatingElsewhereCancels_NavigatingOntoItJoins_AndCloseCancels()
    {
        var (window, viewer, image) = ShowSinglePageViewer(MultiPagePdf(5));
        using var holds = new RenderHolds();
        var hold2 = holds.Hold(2);
        var hold5 = holds.Hold(5);
        var hold3 = holds.Hold(3);
        viewer.SinglePageLookAheadStartingForTests = holds.Wait;
        try
        {
            // Page 1 renders, then page 2 is rendered ahead and held.
            await WaitUntilAsync(window, () => viewer.SinglePageLookAheadInFlight, "page 2 to be rendered ahead");
            viewer.CurrentPage = 4;
            hold2.Set();
            await WaitUntilAsync(window, () => viewer.SinglePageLookAheadCancellationCount == 1,
                "the jump to page 4 to cancel page 2's render-ahead");
            viewer.SinglePageCacheContainsForTests(2).Should().BeFalse("the cancelled render cached nothing");

            // From page 4, page 5 is rendered ahead (held); turn onto it.
            await WaitUntilAsync(window, () => !viewer.IsLoading && viewer.SinglePageLookAheadInFlight,
                "page 4 shown and page 5 rendering ahead");
            var misses = viewer.GetRenderDiagnostics().SinglePageMisses;
            viewer.CurrentPage = 5;
            viewer.IsLoading.Should().BeTrue("the turn waits for the render already under way");
            hold5.Set();
            await WaitUntilAsync(window, () => !viewer.IsLoading && viewer.SinglePageCacheContainsForTests(5)
                && image.Source is WriteableBitmap, "page 5 shown");
            viewer.SinglePageLookAheadJoinCount.Should().Be(1);
            viewer.SinglePageLookAheadCancellationCount.Should().Be(1, "joining is not cancelling");
            viewer.GetRenderDiagnostics().SinglePageMisses.Should().Be(misses, "page 5 was served from the cache");

            // Page 2 (not cached) renders, page 3 is rendered ahead (held); close.
            viewer.CurrentPage = 2;
            await WaitUntilAsync(window, () => !viewer.IsLoading && viewer.SinglePageLookAheadInFlight,
                "page 2 shown and page 3 rendering ahead");
            window.Close();
            hold3.Set();
            await WaitUntilAsync(window, () => !viewer.SinglePageLookAheadInFlight, "the render-ahead to finish");
            viewer.SinglePageLookAheadCancellationCount.Should().Be(2, "closing the window cancels render-ahead");
            viewer.SinglePageCacheContainsForTests(3).Should().BeFalse();
        }
        finally
        {
            holds.ReleaseAll();
            viewer.SinglePageLookAheadStartingForTests = null;
            Close(window, viewer);
        }
    }

    // ---- helpers ----------------------------------------------------------

    private static byte[] MultiPagePdf(int pageCount)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-render-ahead-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount);
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return bytes;
    }

    private static (Window, PdfViewerControl, Image) ShowSinglePageViewer(byte[] bytes, bool renderAhead = true)
    {
        var viewer = new PdfViewerControl { RenderAheadEnabled = renderAhead };
        var window = new Window { Content = viewer, Width = 900, Height = 700 };
        window.Show();
        viewer.Document = PdfCoreDocument.Open(bytes);
        return (window, viewer, viewer.FindControl<Image>("PdfImage")!);
    }

    /// <summary>Per-page gates a render-thread hook waits on, so a test can hold one render in flight.</summary>
    private sealed class RenderHolds : IDisposable
    {
        private readonly ConcurrentDictionary<int, ManualResetEventSlim> _holds = new();

        public ManualResetEventSlim Hold(int page) => _holds.GetOrAdd(page, _ => new ManualResetEventSlim(false));

        public void Release(int page)
        {
            if (_holds.TryGetValue(page, out var hold)) hold.Set();
        }

        public void Wait(int page)
        {
            if (_holds.TryGetValue(page, out var hold)) hold.Wait(TimeSpan.FromSeconds(30));
        }

        public void ReleaseAll()
        {
            foreach (var hold in _holds.Values) hold.Set();
        }

        public void Dispose()
        {
            ReleaseAll();
            foreach (var hold in _holds.Values) hold.Dispose();
        }
    }

    private static void Close(Window window, PdfViewerControl viewer)
    {
        if (window.IsVisible)
            window.Close();
        Dispatcher.UIThread.RunJobs();
        viewer.Document?.Dispose();
    }

    private static async Task WaitLookAheadIdleAsync(Window window, PdfViewerControl viewer)
    {
        int quiet = 0;
        var sw = Stopwatch.StartNew();
        while (quiet < 6)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { if (window.IsVisible) window.UpdateLayout(); }, DispatcherPriority.Background);
            Dispatcher.UIThread.RunJobs();
            quiet = !viewer.ContinuousLookAheadInFlight && viewer.ContinuousInFlightCount == 0 ? quiet + 1 : 0;
            if (sw.Elapsed > TimeSpan.FromSeconds(60))
                throw new TimeoutException("render-ahead did not go idle. " + viewer.ContinuousDiagnostics());
            await Task.Delay(25);
        }
    }

    private static async Task WaitSinglePageLookAheadIdleAsync(Window window, PdfViewerControl viewer, int expectedStarts)
    {
        await WaitUntilAsync(window, () => viewer.SinglePageLookAheadStartCount >= expectedStarts
            && !viewer.SinglePageLookAheadInFlight && !viewer.IsLoading,
            $"{expectedStarts} single-page render-aheads (started {viewer.SinglePageLookAheadStartCount})");
        await PumpAsync(window, TimeSpan.FromMilliseconds(200));
        viewer.SinglePageLookAheadStartCount.Should().Be(expectedStarts);
        viewer.SinglePageLookAheadInFlight.Should().BeFalse();
    }

    private static async Task PumpAsync(Window window, TimeSpan duration)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { if (window.IsVisible) window.UpdateLayout(); }, DispatcherPriority.Background);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
    }

    private static async Task WaitUntilAsync(Window window, Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(60))
                throw new TimeoutException($"timed out waiting for {what}");
            await Dispatcher.UIThread.InvokeAsync(() => { if (window.IsVisible) window.UpdateLayout(); }, DispatcherPriority.Background);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
    }
}
