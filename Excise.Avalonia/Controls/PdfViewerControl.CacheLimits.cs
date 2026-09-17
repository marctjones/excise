using System;
using System.Threading;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Threading;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Host-adjustable cache and render limits, so an application can trade
/// memory and CPU against scroll-back speed while a document is open. Each
/// setter takes effect at once and follows the same lifetime rules as
/// <see cref="TrimCaches"/>: UI thread only, the visible band's tiles and the
/// bitmap on screen are never dropped, and in-flight renders are not cancelled.
/// </summary>
public partial class PdfViewerControl
{
    /// <summary>
    /// Byte budget of the continuous view's tile cache (default 200 MiB). UI
    /// thread only. Lowering it evicts least-recently-used tiles at once until
    /// the cache fits, but never a tile of the current bands: if those alone
    /// exceed the new budget the cache stays above it until the bands move.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public long ContinuousTileCacheByteBudget
    {
        get => _continuousCacheByteBudget;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _continuousCacheByteBudget = value;
            EnforceContinuousCacheBudget();
        }
    }

    /// <summary>
    /// Resident bytes the continuous view's tile cache holds now (4 bytes per
    /// pixel). UI thread only. Computed on read, so it does not depend on a
    /// metrics listener being enabled.
    /// </summary>
    public long ContinuousTileCacheResidentBytes
    {
        get
        {
            Dispatcher.UIThread.VerifyAccess();
            return ContinuousCacheResidentBytes();
        }
    }

    /// <summary>
    /// How many rendered pages the single-page view keeps for instant
    /// back-navigation (default 6). UI thread only. Lowering it disposes the
    /// least-recently-used bitmaps at once, never the one on screen.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int SinglePageCacheCapacity
    {
        get => _singlePageRenderLifetime.GetCacheDiagnostics().Capacity;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            var shown = _pdfImage?.Source as WriteableBitmap;
            _singlePageRenderLifetime.SetCapacity(value, bitmap => ReferenceEquals(bitmap, shown));
        }
    }

    /// <summary>
    /// How many continuous-view band renders may run at once (default
    /// clamp(CPU count - 1, 2, 6)). UI thread only. Renders that start after
    /// the change use the new width; renders already waiting or running finish
    /// under the old one.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int ContinuousRenderConcurrency
    {
        get => _continuousRenderConcurrency;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            if (value == _continuousRenderConcurrency)
                return;
            // The old semaphore is not disposed: renders still waiting on it or
            // holding a slot release it themselves (see the capture in the band
            // render), and SemaphoreSlim owns no handle until AvailableWaitHandle
            // is read, which nothing here does.
            _continuousRenderGate = new SemaphoreSlim(value);
            _continuousRenderConcurrency = value;
        }
    }

    /// <summary>
    /// Evict tiles until the cache fits
    /// <see cref="EffectiveContinuousCacheByteBudget"/>, skipping every tile of
    /// the current bands: render-ahead tiles first (#1564), then
    /// least-recently-used ones. Unlike <see cref="TrimContinuousTiles"/> this
    /// stops as soon as the budget is met. Returns how many tiles were disposed.
    /// </summary>
    internal int EnforceContinuousCacheBudget()
    {
        long budget = EffectiveContinuousCacheByteBudget;
        long resident = ContinuousCacheResidentBytes();
        int evicted = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            bool lookAheadOnly = pass == 0;
            var node = _continuousCache.Last;
            while (node != null && resident > budget && _continuousCache.Count > ContinuousCacheMinEntries)
            {
                var previous = node.Previous;
                var key = node.Value.Key;
                if (!_continuousRequiredKeys.Contains(key)
                    && (!lookAheadOnly || _continuousLookAheadTiles.Contains(key)))
                {
                    var bitmap = node.Value.Bitmap;
                    // Sized before disposal: a disposed bitmap throws on PixelSize.
                    resident -= ContinuousTileByteSize(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                    _continuousCache.Remove(node);
                    _continuousLookAheadTiles.Remove(key);
                    bitmap.Dispose();
                    evicted++;
                }
                node = previous;
            }
        }
        if (evicted > 0)
            RefreshContinuousByteMirrors();
        return evicted;
    }
}
