using System;
using System.Collections.Generic;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Threading;

namespace Excise.Avalonia.Controls;

/// <summary>
/// How much of its bitmap caches <see cref="PdfViewerControl.TrimCaches"/>
/// releases (#1478). Each level includes everything the levels below it drop.
/// </summary>
public enum PdfViewerCacheTrimLevel
{
    /// <summary>
    /// The app is in the background or idle. Drops the continuous view's
    /// render-ahead tiles (#1564) and scroll-back tiles (every tile outside the
    /// current bands) and every single-page bitmap except the one on screen,
    /// render-ahead pages included. Render-ahead is not restarted until the
    /// reader moves. What is visible stays
    /// cached, so returning to the window costs nothing; scrolling back to a
    /// dropped band re-renders it.
    /// </summary>
    Background = 0,

    /// <summary>
    /// The OS reports memory pressure. Inside the viewer this drops exactly
    /// what <see cref="Background"/> does: the viewer owns no other cache that
    /// can be released without re-rendering what is on screen.
    /// </summary>
    Warn = 1,

    /// <summary>
    /// The OS reports critical memory pressure. Also releases the composites
    /// of pages whose band no longer intersects the viewport and every
    /// remaining tile, including those baked into the visible composites, so
    /// the next scroll step re-renders its band. A render can cost seconds on
    /// a heavy page (Altona p1 ~3.8 s), which is why this is its own level.
    /// </summary>
    Critical = 2,
}

/// <summary>
/// #1478: release cached bitmaps on request instead of holding the fixed
/// budgets (<see cref="ContinuousCacheByteBudget"/>, the single-page LRU)
/// until the document changes. The control only offers the mechanism; the
/// host decides when to call it (OS memory pressure, window deactivation,
/// idle), so nothing here is platform-specific or runs on a timer.
/// </summary>
/// <remarks>
/// Lifetime rules, unchanged from #1466/#1467:
/// <list type="bullet">
/// <item>Tiles are never an Image source, so an evicted tile is disposed at
/// once. That is only safe on the UI thread, where compositing runs; hence
/// <see cref="Dispatcher.VerifyAccess"/>.</item>
/// <item>A composite is bound to an Image, so it is never disposed here. It
/// is released through <see cref="PdfPageSlot.ClearComposite"/>, which moves
/// the binding first and disposes later.</item>
/// <item>The bitmap the single-page Image shows is never dropped.</item>
/// <item>In-flight cell renders are not cancelled. Their keys are not in the
/// tile cache until they land, so dropping cache entries cannot touch them,
/// and they still check <see cref="_continuousRequiredKeys"/> when they
/// run.</item>
/// </list>
/// </remarks>
public partial class PdfViewerControl
{
    /// <summary>How many times <see cref="TrimCaches"/> has run on this viewer.</summary>
    internal int CacheTrimCount { get; private set; }

    /// <summary>What the most recent <see cref="TrimCaches"/> released.</summary>
    internal CacheTrimResult LastCacheTrim { get; private set; }

    /// <summary>
    /// Release cached page bitmaps down to <paramref name="level"/> (#1478).
    /// Must be called on the UI thread. Whatever is on screen stays on screen;
    /// what was dropped is re-rendered when it is needed again.
    /// </summary>
    public void TrimCaches(PdfViewerCacheTrimLevel level)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!Enum.IsDefined(level))
            throw new ArgumentOutOfRangeException(nameof(level), level, "unknown cache trim level");

        // #1564: render-ahead goes first. A look-ahead render the current bands
        // do not need is cancelled, and none is planned again until the reader
        // moves: a trim must not cause a render (#1478), and re-rendering what
        // was just released would undo the trim.
        SuppressContinuousLookAheadAfterTrim();
        SuppressSinglePageLookAheadAfterTrim();

        // Background and Warn keep the current bands' tiles; Critical keeps
        // none. _continuousRequiredKeys is the set the last render pass
        // computed, and the one a queued render checks, so it is the band the
        // pipeline itself considers current.
        var (tiles, tileBytes, lookAheadTiles) = TrimContinuousTiles(
            level == PdfViewerCacheTrimLevel.Critical ? null : _continuousRequiredKeys);

        int composites = 0;
        long compositeBytes = 0;
        if (level == PdfViewerCacheTrimLevel.Critical)
            (composites, compositeBytes) = ClearCompositesOutsideViewport();

        // Reference identity with the Image's source IS the never-drop rule.
        // When the page has settled, that bitmap is the current page at the
        // current device DPI; while a render is in flight it is whatever the
        // user still sees. In continuous view the hidden Image has no source
        // (#1473), so every entry goes.
        var shown = _pdfImage?.Source as WriteableBitmap;
        var (singlePage, singlePageBytes) = _singlePageRenderLifetime.Trim(
            bitmap => ReferenceEquals(bitmap, shown),
            static bitmap => ContinuousTileByteSize(bitmap.PixelSize.Width, bitmap.PixelSize.Height));

        // #1492: decoded image samples of pages outside the current bands, at
        // every level. Stricter than the render pass, which keeps every
        // REALIZED page: a realized page scrolled out of its band keeps its
        // samples there, but a trim lets them go (a later band render of that
        // page decodes again). Pages with a render in flight are always kept.
        var bandPages = new HashSet<int>();
        foreach (var key in _continuousRequiredKeys)
            bandPages.Add(key.Page);
        var (sampleStreams, sampleBytes) = ReleaseContinuousImageSamples(
            bandPages, ViewerMetrics.DecodedSampleReleaseTrim);

        RefreshContinuousByteMirrors();

        var result = new CacheTrimResult(level, tiles, tileBytes, composites, compositeBytes, singlePage, singlePageBytes,
            sampleStreams, sampleBytes, lookAheadTiles);
        LastCacheTrim = result;
        CacheTrimCount++;
        ViewerMetrics.RecordCacheTrim(level, tileBytes, compositeBytes, singlePageBytes);
        if (TraceEnabled)
            Trace($"TrimCaches {result}");
    }

    /// <summary>
    /// Unlink and dispose every tile whose key is not in <paramref name="keep"/>
    /// (all of them when it is null). Sized before disposal: a disposed bitmap
    /// throws on PixelSize. <c>LookAhead</c> counts the render-ahead tiles
    /// among them (#1564); a look-ahead tile is never in the bands, so every
    /// level releases all of them.
    /// </summary>
    private (int Count, long Bytes, int LookAhead) TrimContinuousTiles(IReadOnlySet<ContinuousTileKey>? keep)
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
    private (int Count, long Bytes) ClearCompositesOutsideViewport()
    {
        if (_continuousSlots == null)
            return default;

        var viewport = _continuousScrollViewer?.Viewport ?? default;
        var offset = _continuousScrollViewer?.Offset ?? default;
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

    /// <summary>
    /// A page intersects the viewport exactly when it requires grid cells —
    /// the same test RecomposeSlotCore uses to keep a page's last composite.
    /// </summary>
    internal static bool SlotIntersectsViewport(PdfPageSlot slot, global::Avalonia.Vector offset, global::Avalonia.Size viewport) =>
        RequiredTileCells(slot.DisplayWidth, slot.DisplayHeight, slot.TopDip,
            offset, viewport, ContinuousTileQuantumDip, ContinuousTileOverscanDip).Count > 0;

    /// <summary>Snapshot of the tile LRU, most recent first (tests only).</summary>
    internal IReadOnlyList<(ContinuousTileKey Key, WriteableBitmap Bitmap)> ContinuousCacheEntriesForTests() =>
        new List<(ContinuousTileKey, WriteableBitmap)>(_continuousCache);

    /// <summary>The band keys the last continuous render pass required (tests only).</summary>
    internal IReadOnlySet<ContinuousTileKey> ContinuousRequiredKeysForTests => _continuousRequiredKeys;

    /// <summary>
    /// What one <see cref="TrimCaches"/> call released, by cache, plus the
    /// decoded image samples it released (#1492).
    /// </summary>
    internal readonly record struct CacheTrimResult(
        PdfViewerCacheTrimLevel Level,
        int Tiles, long TileBytes,
        int Composites, long CompositeBytes,
        int SinglePageBitmaps, long SinglePageBytes,
        int DecodedSampleStreams = 0, long DecodedSampleBytes = 0,
        int LookAheadTiles = 0)
    {
        /// <summary>
        /// Bitmap bytes only (native pixels). Decoded samples are managed
        /// memory and are reported separately in <see cref="DecodedSampleBytes"/>.
        /// </summary>
        public long TotalBytes => TileBytes + CompositeBytes + SinglePageBytes;
    }
}
