using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.Avalonia.Controls;
using Xunit;
using PdfCoreDocument = Excise.Core.Document.PdfDocument;

namespace Excise.App.Tests.Controls;

/// <summary>
/// #1467: continuous-view tiles are disposed when the LRU evicts them. The risk
/// that change carries is a tile disposed while compositing still needs it — a
/// blit from a released bitmap, swallowed by RecomposeSlot, showing a stale or
/// blank page. This drives the real render pipeline on Skia, evicts every tile
/// of the visible band, and checks the composite rebuilt from re-rendered tiles
/// still shows the page.
/// </summary>
[Collection("AvaloniaTests")]
public class ContinuousTileEvictionCompositeTests
{
    private readonly ITestOutputHelper _out;
    public ContinuousTileEvictionCompositeTests(ITestOutputHelper output) => _out = output;

    [FixedAvaloniaFact]
    public async Task CompositeRebuiltAfterEvictingEveryVisibleTile_MatchesTheOriginal()
    {
        var (window, viewer, items) = ShowContinuousViewer(pageCount: 1);
        try
        {
            var original = await WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            var reference = PixelCopy.Of(original);
            reference.InkFraction().Should().BeGreaterThan(0.0005,
                "fixture: the page must show content, or a blank rebuild would compare equal");

            var before = viewer.GetRenderDiagnostics();
            before.ContinuousEntryCount.Should().BeGreaterThan(2,
                "fixture: the band must span more cells than ContinuousCacheMinEntries (2), " +
                "or the junk tiles below cannot push every page tile out");
            int rendersBefore = viewer.ContinuousRenderStartCount;

            // Under a zero budget the LRU keeps only its two newest entries, so two
            // unrelated tiles evict — and dispose — every tile of page 1.
            viewer.ContinuousCacheByteBudgetOverride = 0;
            viewer.AddToContinuousCache(JunkKey(0), NewJunkTile());
            viewer.AddToContinuousCache(JunkKey(1), NewJunkTile());
            viewer.GetRenderDiagnostics().ContinuousEntryCount.Should().Be(2);
            viewer.ContinuousCacheByteBudgetOverride = null;

            // Rebuild the slots (a structural refresh keeps the scroll offset and
            // therefore the band), so page 1 must be re-rendered and re-composited.
            viewer.RefreshContinuousLayout();

            var rebuilt = await WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            viewer.ContinuousRenderStartCount.Should().BeGreaterThan(rendersBefore,
                "every tile of the band was evicted, so the band had to be rendered again");
            ReferenceEquals(rebuilt, original).Should().BeFalse();

            var actual = PixelCopy.Of(rebuilt);
            actual.Width.Should().Be(reference.Width, "same band, same DPI");
            actual.Height.Should().Be(reference.Height, "same band, same DPI");
            double mismatch = reference.MismatchFraction(actual, channelTolerance: 32);
            _out.WriteLine($"composite {actual.Width}x{actual.Height}px mismatch={mismatch:P3} " +
                           $"ink ref={reference.InkFraction():P2} new={actual.InkFraction():P2}");
            mismatch.Should().BeLessThan(0.005,
                "a composite built after tile eviction must show the same page as before it");
        }
        finally
        {
            window.Close();
            viewer.Document?.Dispose();
        }
    }

    internal static (Window Window, PdfViewerControl Viewer, ItemsControl Items) ShowContinuousViewer(int pageCount)
    {
        // Bytes-based open, temp file deleted up front (see PdfViewerControlTests).
        var path = Path.Combine(Path.GetTempPath(), $"excise-cont-lifetime-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount);
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);

        var viewer = new PdfViewerControl();
        var window = new Window { Content = viewer, Width = 900, Height = 700 };
        window.Show();
        viewer.Document = PdfCoreDocument.Open(bytes);
        viewer.ViewMode = PdfViewMode.Continuous;
        var items = viewer.FindControl<ItemsControl>("ContinuousItems")!;
        return (window, viewer, items);
    }

    /// <summary>
    /// Waits until page <paramref name="pageNumber"/> has a composite, no cell
    /// render is in flight, and the composite instance has stayed the same across
    /// a few pumps (initial layout can recomposite more than once). With
    /// <paramref name="notThis"/>, that composite must also differ from it.
    /// </summary>
    internal static async Task<WriteableBitmap> WaitForSettledCompositeAsync(
        Window window, PdfViewerControl viewer, ItemsControl items, int pageNumber,
        WriteableBitmap? notThis = null)
    {
        var timeout = TimeSpan.FromSeconds(60);
        var sw = Stopwatch.StartNew();
        WriteableBitmap? last = null;
        int stable = 0;
        while (true)
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Dispatcher.UIThread.RunJobs();

            var bitmap = items.ItemsSource?.Cast<PdfPageSlot>()
                .FirstOrDefault(s => s.PageNumber == pageNumber)?.Bitmap;
            if (bitmap != null && !ReferenceEquals(bitmap, notThis)
                && viewer.ContinuousInFlightCount == 0 && ReferenceEquals(bitmap, last))
            {
                if (++stable >= 3) return bitmap;
            }
            else
            {
                stable = 0;
                last = bitmap;
            }

            if (sw.Elapsed > timeout)
                throw new TimeoutException(
                    $"Continuous page {pageNumber} did not settle within {timeout.TotalSeconds:0}s. " +
                    viewer.ContinuousDiagnostics());
            await Task.Delay(25);
        }
    }

    private static PdfViewerControl.ContinuousTileKey JunkKey(int col) =>
        new(Page: int.MaxValue, Dpi: 1, PageWidthDip: 1, PageHeightDip: 1, Col: col, Row: 0);

    private static WriteableBitmap NewJunkTile() =>
        new(new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    internal readonly record struct PixelCopy(int Width, int Height, byte[] Bgra)
    {
        public static PixelCopy Of(WriteableBitmap bitmap)
        {
            using var fb = bitmap.Lock();
            int w = fb.Size.Width, h = fb.Size.Height;
            var data = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Marshal.Copy(fb.Address + y * fb.RowBytes, data, y * w * 4, w * 4);
            return new PixelCopy(w, h, data);
        }

        public double InkFraction()
        {
            long ink = 0;
            for (int i = 0; i < Bgra.Length; i += 4)
                if (Bgra[i + 3] > 0 && Bgra[i] < 128 && Bgra[i + 1] < 128 && Bgra[i + 2] < 128)
                    ink++;
            return (double)ink / Math.Max(1, Width * Height);
        }

        public double MismatchFraction(PixelCopy other, int channelTolerance)
        {
            long differing = 0;
            for (int i = 0; i < Bgra.Length; i += 4)
            {
                for (int c = 0; c < 4; c++)
                {
                    if (Math.Abs(Bgra[i + c] - other.Bgra[i + c]) > channelTolerance)
                    {
                        differing++;
                        break;
                    }
                }
            }
            return (double)differing / Math.Max(1, Width * Height);
        }
    }
}
