using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// #1467: continuous-view grid-cell tiles are owned by the LRU and disposed when
/// they leave it, instead of being left for the finalizer.
/// </summary>
/// <remarks>
/// Runs on the shared headless session without a Skia backend. Its
/// <c>WriteableBitmap</c> stub allocates pixel memory only inside
/// <c>Lock()</c>, so a 4096x4096 tile costs nothing here while still counting
/// 64 MiB against the real 200 MiB byte budget. Pixel CONTENT is not retained by
/// that stub, so pixel correctness of composites is asserted in
/// Excise.App.Tests (ContinuousTileEvictionCompositeTests), which runs on Skia.
/// Disposal is observable here: Avalonia 12's <c>Bitmap.PixelSize</c> reads
/// through the released <c>IRef</c> and throws <see cref="ObjectDisposedException"/>.
/// </remarks>
public class ContinuousTileCacheDisposalTests
{
    private const int TileSide = 4096; // 4096 * 4096 * 4 = 64 MiB of budget

    private static Task<T> OnUiThread<T>(Func<T> body) =>
        HeadlessSessionGuard.Session().Dispatch(body, CancellationToken.None);

    private static WriteableBitmap NewTile() =>
        new(new PixelSize(TileSide, TileSide), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    private static PdfViewerControl.ContinuousTileKey Key(int col) =>
        new(Page: 1, Dpi: 120, PageWidthDip: 816, PageHeightDip: 1056, Col: col, Row: 0);

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

    [Fact]
    public async Task IsDisposedProbe_DistinguishesALiveBitmapFromADisposedOne()
    {
        // Guards the probe itself: if Avalonia stopped throwing after Dispose,
        // every "was disposed" assertion below would silently read as "alive".
        await OnUiThread(() =>
        {
            var live = NewTile();
            var dead = NewTile();
            dead.Dispose();

            IsDisposed(live).Should().BeFalse();
            IsDisposed(dead).Should().BeTrue();
            live.Dispose();
            return true;
        });
    }

    [Fact]
    public async Task EvictingPastTheByteBudget_DisposesEveryEvictedTile_AndOnlyThose()
    {
        await OnUiThread(() =>
        {
            var viewer = new PdfViewerControl();
            var tiles = Enumerable.Range(0, 5).Select(_ => NewTile()).ToArray();

            for (int i = 0; i < tiles.Length; i++)
                viewer.AddToContinuousCache(Key(i), tiles[i]);

            var diagnostics = viewer.GetRenderDiagnostics();
            diagnostics.ContinuousResidentBytes.Should().BeLessThanOrEqualTo(diagnostics.ContinuousByteBudget,
                "eviction must bring the cache back under its byte budget");

            // 5 x 64 MiB against 200 MiB: the two least-recently-added tiles go.
            int retained = diagnostics.ContinuousEntryCount;
            retained.Should().Be(3, "fixture: 3 x 64 MiB is the most that fits a 200 MiB budget");

            var evicted = tiles.Take(tiles.Length - retained).ToArray();
            var kept = tiles.Skip(tiles.Length - retained).ToArray();

            evicted.Should().OnlyContain(t => IsDisposed(t),
                "an evicted tile is never an Image.Source (#848), so it is released now, not by the finalizer");
            kept.Should().OnlyContain(t => !IsDisposed(t),
                "tiles still in the LRU must stay usable for compositing");
            return true;
        });
    }

    [Fact]
    public async Task ReAddingAKey_DisposesTheReplacedTile_ButNeverTheInstanceBeingAdded()
    {
        await OnUiThread(() =>
        {
            var viewer = new PdfViewerControl();
            var first = NewTile();
            var second = NewTile();

            viewer.AddToContinuousCache(Key(0), first);
            viewer.AddToContinuousCache(Key(0), second);

            IsDisposed(first).Should().BeTrue("a tile replaced under the same key has left the cache");
            IsDisposed(second).Should().BeFalse();
            viewer.GetRenderDiagnostics().ContinuousEntryCount.Should().Be(1);

            // Same key, same instance: nothing left the cache, so nothing is disposed.
            viewer.AddToContinuousCache(Key(0), second);

            IsDisposed(second).Should().BeFalse(
                "re-adding the very instance already cached must not dispose the bitmap being inserted");
            viewer.GetRenderDiagnostics().ContinuousEntryCount.Should().Be(1);
            return true;
        });
    }
}
