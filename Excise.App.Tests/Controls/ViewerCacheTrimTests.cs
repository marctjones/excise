using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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
/// #1478: <see cref="PdfViewerControl.TrimCaches"/> on Skia with real renders.
/// Each level must drop what it names and dispose it (#1467: native pixels are
/// not returned by GC pressure), must never dispose a bitmap an Image still
/// shows (#1466: that throws ObjectDisposedException on the next measure), and
/// what it dropped must come back correct when it is needed again.
/// </summary>
[Collection("AvaloniaTests")]
public class ViewerCacheTrimTests
{
    private readonly ITestOutputHelper _out;
    public ViewerCacheTrimTests(ITestOutputHelper output) => _out = output;

    [FixedAvaloniaFact]
    public async Task BackgroundAndWarn_KeepOnlyTheRequiredBand_DisposeTheRest_AndScrollingBackRebuildsTheSamePage()
    {
        const int pageCount = 8;
        var (window, viewer, items) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(pageCount);
        var dispatcherErrors = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler onError = (_, e) => dispatcherErrors.Add(e.Exception);
        Dispatcher.UIThread.UnhandledException += onError;
        try
        {
            var firstPage = await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            var reference = PixelCopy.Of(firstPage);
            reference.InkFraction().Should().BeGreaterThan(0.0005,
                "fixture: the page must show content, or a blank rebuild would compare equal");
            for (int page = 2; page <= pageCount; page++)
            {
                viewer.CurrentPage = page;
                await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, page);
            }

            var slots = items.ItemsSource!.Cast<PdfPageSlot>().ToList();
            foreach (var level in new[] { PdfViewerCacheTrimLevel.Background, PdfViewerCacheTrimLevel.Warn })
            {
                var before = viewer.ContinuousCacheEntriesForTests();
                var required = viewer.ContinuousRequiredKeysForTests;
                required.Should().NotBeEmpty("fixture: the settled page has a band");
                if (level == PdfViewerCacheTrimLevel.Background)
                {
                    before.Should().Contain(e => !required.Contains(e.Key),
                        "fixture: paging must leave scroll-back tiles outside the band, or the trim has nothing to drop");
                }
                var composites = slots.Where(s => s.Bitmap != null).Select(s => (Slot: s, Bitmap: s.Bitmap!)).ToList();
                composites.Should().NotBeEmpty("fixture: the visible page shows a composite");

                viewer.TrimCaches(level);

                var after = viewer.ContinuousCacheEntriesForTests();
                after.Select(e => e.Key).Should().OnlyContain(k => required.Contains(k),
                    $"{level} keeps only the tiles of the current bands");
                long residentBytes = after.Sum(e => PdfViewerControl.ContinuousTileByteSize(e.Bitmap.PixelSize.Width, e.Bitmap.PixelSize.Height));
                residentBytes.Should().BeLessThanOrEqualTo(RequiredBandUpperBoundBytes(viewer, required),
                    $"after {level}, resident tile bytes are bounded by the required band, not by the 200 MiB budget");

                var kept = new HashSet<WriteableBitmap>(after.Select(e => e.Bitmap), ReferenceEqualityComparer.Instance);
                var evicted = before.Where(e => !kept.Contains(e.Bitmap)).ToList();
                evicted.Should().OnlyContain(e => IsDisposed(e.Bitmap), "a trimmed tile's pixels are released at once (#1467)");
                kept.Should().OnlyContain(b => !IsDisposed(b));
                viewer.LastCacheTrim.Tiles.Should().Be(evicted.Count);

                RenderFrames(window, viewer);
                Dispatcher.UIThread.RunJobs();
                RenderFrames(window, viewer);
                foreach (var (slot, bitmap) in composites)
                {
                    slot.Bitmap.Should().BeSameAs(bitmap, $"{level} never touches a composite");
                    IsDisposed(bitmap).Should().BeFalse($"{level} never disposes a composite an Image shows (#1466)");
                }
                _out.WriteLine($"{level}: tiles {before.Count} -> {after.Count}, released {viewer.LastCacheTrim.TotalBytes} B, " +
                               $"resident {residentBytes} B, required cells {required.Count}");
            }

            // Page 1's tiles were scroll-back tiles, so returning to it must
            // render again, and the rebuilt composite must show the same page.
            int startsBefore = viewer.ContinuousRenderStartCount;
            viewer.CurrentPage = 1;
            var rebuilt = await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            viewer.ContinuousRenderStartCount.Should().BeGreaterThan(startsBefore,
                "the trim dropped page 1's tiles, so scrolling back cannot be a cache hit");

            var actual = PixelCopy.Of(rebuilt);
            actual.Width.Should().Be(reference.Width, "same band, same DPI");
            actual.Height.Should().Be(reference.Height, "same band, same DPI");
            double mismatch = reference.MismatchFraction(actual, channelTolerance: 32);
            _out.WriteLine($"scroll-back composite {actual.Width}x{actual.Height}px mismatch={mismatch:P3}");
            mismatch.Should().BeLessThan(0.005, "a page re-rendered after a trim must match the page before it");

            RenderFrames(window, viewer);
            dispatcherErrors.Should().BeEmpty("no dispatcher job may throw while trimmed caches refill");
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= onError;
            window.Close();
            viewer.Document?.Dispose();
        }
    }

    [FixedAvaloniaFact]
    public async Task Critical_ReleasesEveryTileAndOffViewportComposites_KeepsTheVisibleComposite_AndTheBandRebuilds()
    {
        var (window, viewer, items) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(pageCount: 3);
        var dispatcherErrors = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler onError = (_, e) => dispatcherErrors.Add(e.Exception);
        Dispatcher.UIThread.UnhandledException += onError;
        try
        {
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            var scroll = viewer.FindControl<ScrollViewer>("ContinuousScrollViewer")!;
            var slots = items.ItemsSource!.Cast<PdfPageSlot>().ToList();
            var (page1, page2) = (slots[0], slots[1]);

            // Straddle pages 1 and 2 so both are visible and both composite.
            double straddle = page2.TopDip - scroll.Viewport.Height / 2;
            scroll.Offset = new Vector(0, straddle);
            var page2Straddling = await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 2);
            await WaitUntilAsync(window, () => page1.Bitmap != null && viewer.ContinuousInFlightCount == 0, "page 1 composite while straddling");
            var reference = PixelCopy.Of(page2Straddling);

            // Scroll page 1 out of the viewport and trim before the next render
            // pass runs: page 1 still holds the composite of a band that no
            // longer intersects the viewport, the "outside (2)" case of #1466.
            var page1Composite = page1.Bitmap!;
            scroll.Offset = new Vector(0, page2.TopDip + 64);
            PdfViewerControl.SlotIntersectsViewport(page1, scroll.Offset, scroll.Viewport).Should().BeFalse("fixture");
            PdfViewerControl.SlotIntersectsViewport(page2, scroll.Offset, scroll.Viewport).Should().BeTrue("fixture");
            viewer.ContinuousCacheEntriesForTests().Should().NotBeEmpty("fixture: the band's tiles are cached");

            viewer.TrimCaches(PdfViewerCacheTrimLevel.Critical);

            viewer.ContinuousCacheEntriesForTests().Should().BeEmpty("Critical releases every tile, baked ones included");
            page1.Bitmap.Should().BeNull("Critical clears a composite whose band left the viewport");
            page2.Bitmap.Should().BeSameAs(page2Straddling, "a visible page keeps its composite");
            viewer.LastCacheTrim.Composites.Should().Be(1);
            IsDisposed(page1Composite).Should().BeFalse("a cleared composite is released only after the binding moved");

            RenderFrames(window, viewer);
            Dispatcher.UIThread.RunJobs();
            IsDisposed(page1Composite).Should().BeTrue("once the dispatcher has run, the cleared composite is released");
            // Running the dispatcher also runs the render pass the scroll
            // scheduled, which may already have published page 2's new band and
            // released the straddling composite the normal way. So the rule is
            // the #1466 one: a composite is disposed only once no slot shows it.
            page2.Bitmap.Should().NotBeNull("the visible page still shows a composite");
            IsDisposed(page2.Bitmap!).Should().BeFalse("Critical never disposes a composite an Image shows");
            IsDisposed(page2Straddling).Should().Be(!ReferenceEquals(page2.Bitmap, page2Straddling),
                "the straddling composite is released only after page 2's binding moved off it");
            RenderFrames(window, viewer);

            // Back to the straddling band: its tiles are gone, so it re-renders
            // and must match what it showed before the trim.
            int startsBefore = viewer.ContinuousRenderStartCount;
            scroll.Offset = new Vector(0, straddle);
            var rebuilt = await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(
                window, viewer, items, pageNumber: 2, notThis: page2Straddling);
            viewer.ContinuousRenderStartCount.Should().BeGreaterThan(startsBefore, "Critical dropped the band's tiles");
            var actual = PixelCopy.Of(rebuilt);
            actual.Width.Should().Be(reference.Width, "same band, same DPI");
            actual.Height.Should().Be(reference.Height, "same band, same DPI");
            double mismatch = reference.MismatchFraction(actual, channelTolerance: 32);
            _out.WriteLine($"critical rebuild {actual.Width}x{actual.Height}px mismatch={mismatch:P3} released={viewer.LastCacheTrim}");
            mismatch.Should().BeLessThan(0.005);

            RenderFrames(window, viewer);
            dispatcherErrors.Should().BeEmpty("no dispatcher job may throw after a Critical trim");
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= onError;
            window.Close();
            viewer.Document?.Dispose();
        }
    }

    [FixedAvaloniaFact]
    public async Task SinglePage_EveryLevelKeepsTheShownPageAtItsDpi_AndDisposesTheOtherPages()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-trim-single-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, 3);
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);

        var viewer = new PdfViewerControl();
        var window = new Window { Content = viewer, Width = 900, Height = 700 };
        var dispatcherErrors = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler onError = (_, e) => dispatcherErrors.Add(e.Exception);
        Dispatcher.UIThread.UnhandledException += onError;
        window.Show();
        try
        {
            var image = viewer.FindControl<Image>("PdfImage")!;
            viewer.Document = PdfCoreDocument.Open(bytes);
            var shown = new WriteableBitmap[3];
            for (int page = 1; page <= 3; page++)
            {
                viewer.CurrentPage = page;
                await WaitUntilAsync(window, () => !viewer.IsLoading && image.Source is WriteableBitmap &&
                    viewer.GetRenderDiagnostics().SinglePageEntryCount >= page, $"page {page} rendered");
                shown[page - 1] = (WriteableBitmap)image.Source!;
            }
            viewer.CurrentPage = 2;
            await WaitUntilAsync(window, () => ReferenceEquals(image.Source, shown[1]), "page 2 from the cache");
            viewer.GetRenderDiagnostics().SinglePageEntryCount.Should().Be(3, "fixture: three pages cached");

            foreach (var level in Enum.GetValues<PdfViewerCacheTrimLevel>())
            {
                viewer.TrimCaches(level);
                image.Source.Should().BeSameAs(shown[1], $"{level} keeps the page on screen");
                IsDisposed(shown[1]).Should().BeFalse($"{level} never disposes the bitmap PdfImage shows");
                RenderFrames(window, viewer);
            }
            IsDisposed(shown[0]).Should().BeTrue("#1478: a trimmed page bitmap is disposed");
            IsDisposed(shown[2]).Should().BeTrue();
            viewer.GetRenderDiagnostics().SinglePageEntryCount.Should().Be(1);

            // The kept entry is the current page at its current device DPI: a
            // render elsewhere and a return to page 2 hits the cache.
            viewer.CurrentPage = 1;
            await WaitUntilAsync(window, () => !viewer.IsLoading && image.Source is WriteableBitmap b && !ReferenceEquals(b, shown[1]), "page 1 re-rendered");
            long hits = viewer.GetRenderDiagnostics().SinglePageHits;
            viewer.CurrentPage = 2;
            await WaitUntilAsync(window, () => ReferenceEquals(image.Source, shown[1]), "page 2 from the cache after the trim");
            viewer.GetRenderDiagnostics().SinglePageHits.Should().BeGreaterThan(hits);

            RenderFrames(window, viewer);
            dispatcherErrors.Should().BeEmpty();
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= onError;
            window.Close();
            viewer.Document?.Dispose();
        }
    }

    // Every required cell at its largest possible bitmap: a full quantum at the
    // band's pixel density, ceiled, plus the one-pixel ceil overhang.
    private static long RequiredBandUpperBoundBytes(PdfViewerControl viewer, IReadOnlySet<PdfViewerControl.ContinuousTileKey> required)
    {
        double pxPerDip = viewer.ContinuousEffectiveRenderDpi / (96.0 * viewer.ZoomLevel);
        int cellPx = (int)Math.Ceiling(PdfViewerControl.ContinuousTileQuantumDip * pxPerDip) + 1;
        return required.Count * PdfViewerControl.ContinuousTileByteSize(cellPx, cellPx);
    }

    private static async Task WaitUntilAsync(Window window, Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(60))
                throw new TimeoutException($"timed out waiting for {what}");
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
    }

    private static void RenderFrames(Window window, PdfViewerControl viewer)
    {
        window.UpdateLayout();
        using (window.CaptureRenderedFrame()) { }

        var w = Math.Max(1, (int)viewer.Bounds.Width);
        var h = Math.Max(1, (int)viewer.Bounds.Height);
        using var target = new RenderTargetBitmap(new PixelSize(w, h));
        target.Render(viewer);
    }

    private static bool IsDisposed(Bitmap bitmap)
    {
        try
        {
            _ = bitmap.PixelSize;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }
}
