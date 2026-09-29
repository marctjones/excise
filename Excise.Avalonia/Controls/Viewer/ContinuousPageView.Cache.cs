using System;
using System.Collections.Generic;
using System.Threading;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Threading;
using static Excise.Avalonia.Controls.PdfViewerControl;

namespace Excise.Avalonia.Controls;

/// <summary>
/// The continuous view's tile-cache budget, eviction, trims and gauges (#1478, #1491, #1551).
/// The viewer composes these with the single-page cache into its public cache API.
/// </summary>
internal sealed partial class ContinuousPageView
{
    /// <summary>The host-configured tile budget, behind the viewer's <c>ContinuousTileCacheByteBudget</c>.</summary>
    internal long ContinuousCacheByteBudgetSetting
    {
        get => _continuousCacheByteBudget;
        set => _continuousCacheByteBudget = value;
    }

    /// <summary>How many band renders may run at once, behind the viewer's <c>ContinuousRenderConcurrency</c>.</summary>
    internal int ContinuousRenderConcurrency
    {
        get => _continuousRenderConcurrency;
        set
        {
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

    /// <summary>Tiles in the LRU now.</summary>
    internal int ContinuousCacheCount => _continuousCache.Count;

    /// <summary>The page slots, for the single-page placeholder's composite copy (#1473).</summary>
    internal List<PdfPageSlot>? ContinuousSlots => _continuousSlots;

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
        if (_viewer.SharedTileBudget is not { } shared || ContinuousCacheByteBudgetOverride != null)
            return own;
        long resident = ContinuousCacheResidentBytes();
        return Math.Min(own, shared.BudgetFor(_viewer, resident, ContinuousCacheProtectedBytes()));
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

    /// <summary>
    /// Unlink and dispose every tile whose key is not in <paramref name="keep"/>
    /// (all of them when it is null). Sized before disposal: a disposed bitmap
    /// throws on PixelSize. <c>LookAhead</c> counts the render-ahead tiles
    /// among them (#1564); a look-ahead tile is never in the bands, so every
    /// level releases all of them.
    /// </summary>
    internal (int Count, long Bytes, int LookAhead) TrimContinuousTiles(IReadOnlySet<ContinuousTileKey>? keep)
    {
        int count = 0;
        long bytes = 0;
        int lookAhead = 0;
        var node = _continuousCache.First;
        while (node != null)
        {
            var next = node.Next;
            if (keep == null || !keep.Contains(node.Value.Key))
            {
                var bitmap = node.Value.Bitmap;
                bytes += ContinuousTileByteSize(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                _continuousCache.Remove(node);
                if (_continuousLookAheadTiles.Remove(node.Value.Key))
                    lookAhead++;
                bitmap.Dispose();
                count++;
            }
            node = next;
        }
        return (count, bytes, lookAhead);
    }

    /// <summary>
    /// Clear the composite of every page whose band no longer intersects the
    /// viewport — the realized slot outside the #1466 bound, which keeps its
    /// last composite until its container is recycled. Visible pages keep
    /// theirs. Released via <see cref="PdfPageSlot.ClearComposite"/>, never
    /// disposed directly.
    /// </summary>
    internal (int Count, long Bytes) ClearCompositesOutsideViewport()
    {
        if (_continuousSlots == null)
            return default;

        var viewport = ContinuousScrollViewer?.Viewport ?? default;
        var offset = ContinuousScrollViewer?.Offset ?? default;
        int count = 0;
        long bytes = 0;
        foreach (var slot in _continuousSlots)
        {
            if (slot.Bitmap is not { } composite || SlotIntersectsViewport(slot, offset, viewport))
                continue;
            bytes += ContinuousTileByteSize(composite.PixelSize.Width, composite.PixelSize.Height);
            slot.ClearComposite();
            count++;
        }
        return (count, bytes);
    }

    /// <summary>Snapshot of the tile LRU, most recent first (tests only).</summary>
    internal IReadOnlyList<(ContinuousTileKey Key, WriteableBitmap Bitmap)> ContinuousCacheEntriesForTests() =>
        new List<(ContinuousTileKey, WriteableBitmap)>(_continuousCache);

    /// <summary>The band keys the last continuous render pass required (tests only).</summary>
    internal IReadOnlySet<ContinuousTileKey> ContinuousRequiredKeysForTests => _continuousRequiredKeys;

    /// <summary>
    /// #1479 measurement: how many continuous-view tile bytes are also baked into
    /// a live page composite. A tile counts when its page's slot shows a composite
    /// built at the same DPI and page DIP size, and its grid cell lies inside that
    /// composite's band. RecomposeSlotCore publishes only once every cell of the
    /// band is cached, so at publish time this is ~all of the composite's bytes;
    /// it falls only as the LRU evicts. Report-only; internal for tests.
    /// </summary>
    internal ContinuousBitmapOverlap MeasureContinuousBitmapOverlap()
    {
        long tileBytes = 0, bakedBytes = 0;
        int baked = 0;
        int q = ContinuousTileQuantumDip;
        foreach (var (key, bitmap) in _continuousCache)
        {
            long bytes = ContinuousTileByteSize(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            tileBytes += bytes;
            if (_continuousSlots == null || key.Page < 1 || key.Page > _continuousSlots.Count) continue;
            var slot = _continuousSlots[key.Page - 1];
            var composite = slot.CompositeKey;
            if (slot.Bitmap == null || composite.Dpi != key.Dpi ||
                composite.PageWidthDip != key.PageWidthDip || composite.PageHeightDip != key.PageHeightDip)
                continue;
            int lastCol = (int)Math.Floor((slot.TileDisplayX + slot.TileDisplayWidth - 0.5) / q);
            int lastRow = (int)Math.Floor((slot.TileDisplayY + slot.TileDisplayHeight - 0.5) / q);
            if (key.Col < composite.Col || key.Col > lastCol || key.Row < composite.Row || key.Row > lastRow)
                continue;
            bakedBytes += bytes;
            baked++;
        }
        return new ContinuousBitmapOverlap(
            _continuousCache.Count, tileBytes, baked, bakedBytes, ContinuousCompositeResidentBytes());
    }

    // #1491 gauge sources. ViewerMetrics reads these from the listener's thread,
    // so the byte totals (which walk UI-thread collections) are mirrors refreshed
    // on the UI thread; the counts are single int reads.
    private long _metricsContinuousTileBytes;
    private long _metricsContinuousCompositeBytes;
    internal long MetricsContinuousTileBytes => Volatile.Read(ref _metricsContinuousTileBytes);
    internal long MetricsContinuousCompositeBytes => Volatile.Read(ref _metricsContinuousCompositeBytes);
    internal int MetricsContinuousTileCount => _continuousCache.Count;
    internal int MetricsContinuousInFlightCount => _continuousInFlight.Count;
    internal int MetricsContinuousCacheHits => ContinuousRenderCacheHitCount;

    /// <summary>
    /// Refresh the continuous byte mirrors after the tile cache or the slot
    /// composites change. UI thread only; a no-op unless a byte gauge is enabled.
    /// </summary>
    internal void RefreshContinuousByteMirrors()
    {
        if (!ViewerMetrics.ByteGaugesEnabled) return;
        Volatile.Write(ref _metricsContinuousTileBytes, ContinuousCacheResidentBytes());
        Volatile.Write(ref _metricsContinuousCompositeBytes, ContinuousCompositeResidentBytes());
    }

}
