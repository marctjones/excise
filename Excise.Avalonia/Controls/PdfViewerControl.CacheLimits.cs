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
    /// A tile budget this viewer shares with other viewers, or null for none
    /// (the default). UI thread only. While set, this viewer's tile cache is
    /// bounded by <see cref="ContinuousTileCacheByteBudget"/> AND by what the
    /// shared budget leaves it; see <see cref="PdfViewerTileBudget"/> for who
    /// gives way. Setting it applies the budget at once. A host that discards
    /// the viewer sets it back to null, or the shared budget keeps the viewer
    /// (and counts its tiles) until then.
    /// </summary>
    public PdfViewerTileBudget? SharedTileBudget
    {
        get => _sharedTileBudget;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            if (ReferenceEquals(value, _sharedTileBudget))
                return;
            _sharedTileBudget?.Detach(this);
            _sharedTileBudget = value;
            value?.Attach(this);
            EnforceContinuousCacheBudget();
        }
    }

    private PdfViewerTileBudget? _sharedTileBudget;

    /// <summary>
    /// The byte budget the tile cache may fill right now. Without a shared
    /// budget this is <see cref="EffectiveContinuousCacheByteBudget"/>. With
    /// one, the shared budget first takes what it may from the other viewers,
    /// and the result is the smaller of the two. Call it once per eviction
    /// pass, not per tile: it may evict other viewers' tiles.
    /// </summary>
    private long ContinuousCacheBudgetNow()
    {
        long own = EffectiveContinuousCacheByteBudget;
        if (_sharedTileBudget is not { } shared || ContinuousCacheByteBudgetOverride != null)
            return own;
        long resident = ContinuousCacheResidentBytes();
        return Math.Min(own, shared.BudgetFor(this, resident, ContinuousCacheProtectedBytes()));
    }

    /// <summary>
    /// What a shared budget must leave this viewer: its current bands' tiles,
    /// plus the most recent tile when that one is not in a band (the tile being
    /// cached, which the LRU loop never evicts first).
    /// </summary>
    private long ContinuousCacheProtectedBytes()
    {
        long bytes = 0;
        bool first = true;
        foreach (var (key, bitmap) in _continuousCache)
        {
            if (first || _continuousRequiredKeys.Contains(key))
                bytes += ContinuousTileByteSize(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            first = false;
        }
        return bytes;
    }

    /// <summary>
    /// A shared budget needs <paramref name="bytesWanted"/> for another
    /// viewer: evict render-ahead tiles, then other tiles outside the current
    /// bands, least recently used first, and with
    /// <paramref name="includeBands"/> the band tiles last. Stops once enough
    /// is freed. Keeps <see cref="ContinuousCacheMinEntries"/>. Composites are
    /// never touched, so nothing on screen changes. UI thread only.
    /// </summary>
    internal (int Tiles, long Bytes) EvictTilesForSharedBudget(long bytesWanted, bool includeBands)
    {
        int tiles = 0;
        long freed = 0;
        for (int pass = 0; pass < 3 && freed < bytesWanted; pass++)
        {
            if (pass == 2 && !includeBands)
                break;
            var node = _continuousCache.Last;
            while (node != null && freed < bytesWanted && _continuousCache.Count > ContinuousCacheMinEntries)
            {
                var previous = node.Previous;
                var key = node.Value.Key;
                bool inBand = _continuousRequiredKeys.Contains(key);
                bool take = pass switch
                {
                    0 => !inBand && _continuousLookAheadTiles.Contains(key),
                    1 => !inBand,
                    _ => true,
                };
                if (take)
                {
                    var bitmap = node.Value.Bitmap;
                    freed += ContinuousTileByteSize(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                    _continuousCache.Remove(node);
                    _continuousLookAheadTiles.Remove(key);
                    bitmap.Dispose();
                    tiles++;
                }
                node = previous;
            }
        }
        if (tiles > 0)
        {
            RefreshContinuousByteMirrors();
            if (TraceEnabled)
                Trace($"SharedTileBudget evicted tiles={tiles} bytes={freed} bands={includeBands}");
        }
        return (tiles, freed);
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
    /// <see cref="ContinuousCacheBudgetNow"/>, skipping every tile of
    /// the current bands: render-ahead tiles first (#1564), then
    /// least-recently-used ones. Unlike <see cref="TrimContinuousTiles"/> this
    /// stops as soon as the budget is met. Returns how many tiles were disposed.
    /// </summary>
    internal int EnforceContinuousCacheBudget()
    {
        long budget = ContinuousCacheBudgetNow();
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
