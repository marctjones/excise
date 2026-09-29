using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Cache governance and diagnostics (#1842): the host-adjustable limits, the
/// trims and the telemetry, composed from the two views. Each view owns its
/// caches and the rule that protects what it shows; this partial routes and
/// adds up, and holds no cache state of its own.
/// </summary>
/// <remarks>
/// <para>
/// Limits. Host-adjustable cache and render limits, so an application can trade
/// memory and CPU against scroll-back speed while a document is open. Each
/// setter takes effect at once and follows the same lifetime rules as
/// <see cref="TrimCaches"/>: UI thread only, the visible band's tiles and the
/// bitmap on screen are never dropped, and in-flight renders are not cancelled.
/// </para>
/// <para>
/// Trims. #1478: release cached bitmaps on request instead of holding the fixed
/// budgets (<see cref="ContinuousCacheByteBudget"/>, the single-page LRU)
/// until the document changes. The control only offers the mechanism; the
/// host decides when to call it (OS memory pressure, window deactivation,
/// idle), so nothing here is platform-specific or runs on a timer.
/// </para>
/// Lifetime rules, unchanged from #1466/#1467:
/// <list type="bullet">
/// <item>Tiles are never an Image source, so an evicted tile is disposed at
/// once. That is only safe on the UI thread, where compositing runs; hence
/// <see cref="Dispatcher.VerifyAccess"/>.</item>
/// <item>A composite is bound to an Image, so a trim never disposes it. It
/// is released through <see cref="PdfPageSlot.ClearComposite"/>, which moves
/// the binding first and disposes later.</item>
/// <item>The bitmap the single-page Image shows is never dropped.</item>
/// <item>In-flight cell renders are not cancelled. Their keys are not in the
/// tile cache until they land, so dropping cache entries cannot touch them,
/// and they still check the continuous view's required keys when they
/// run.</item>
/// </list>
/// </remarks>
public partial class PdfViewerControl
{
    // ── limits ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Byte budget of the continuous view's tile cache (default 200 MiB). UI
    /// thread only. Lowering it evicts least-recently-used tiles at once until
    /// the cache fits, but never a tile of the current bands: if those alone
    /// exceed the new budget the cache stays above it until the bands move.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public long ContinuousTileCacheByteBudget
    {
        get => ContinuousPart.ContinuousCacheByteBudgetSetting;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            ContinuousPart.ContinuousCacheByteBudgetSetting = value;
            ContinuousPart.EnforceContinuousCacheBudget();
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
            ContinuousPart.EnforceContinuousCacheBudget();
        }
    }

    private PdfViewerTileBudget? _sharedTileBudget;

    /// <summary>
    /// A shared budget needs <paramref name="bytesWanted"/> for another viewer: the
    /// continuous view gives up tiles, least valuable first (see its own summary).
    /// </summary>
    internal (int Tiles, long Bytes) EvictTilesForSharedBudget(long bytesWanted, bool includeBands) =>
        ContinuousPart.EvictTilesForSharedBudget(bytesWanted, includeBands);

    /// <summary>Evict the continuous view's tiles down to its budget; returns how many.</summary>
    internal int EnforceContinuousCacheBudget() => ContinuousPart.EnforceContinuousCacheBudget();


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
            return ContinuousPart.ContinuousCacheResidentBytes();
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
        get => SinglePagePart.CacheCapacity;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            SinglePagePart.CacheCapacity = value;
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
        get => ContinuousPart.ContinuousRenderConcurrency;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            ContinuousPart.ContinuousRenderConcurrency = value;
        }
    }

    /// <summary>
    /// Whether the viewer renders the next and previous page ahead (#1564).
    /// Default on. Tests turn it off to compare a cold page turn with a
    /// pre-rendered one. Each view keeps its own copy of the switch and cancels
    /// its own look-ahead when it is turned off.
    /// </summary>
    internal bool RenderAheadEnabled
    {
        get => SinglePagePart.RenderAheadEnabled;
        set
        {
            ContinuousPart.SetRenderAheadEnabled(value);
            SinglePagePart.SetRenderAheadEnabled(value);
        }
    }

    // ── trims ────────────────────────────────────────────────────────────────

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
        ContinuousPart.SuppressContinuousLookAheadAfterTrim();
        SinglePagePart.SuppressSinglePageLookAheadAfterTrim();

        // Background and Warn keep the current bands' tiles; Critical keeps
        // none. _continuousRequiredKeys is the set the last render pass
        // computed, and the one a queued render checks, so it is the band the
        // pipeline itself considers current.
        var (tiles, tileBytes, lookAheadTiles) = ContinuousPart.TrimContinuousTiles(
            level == PdfViewerCacheTrimLevel.Critical ? null : ContinuousPart.ContinuousRequiredKeysForTests);

        int composites = 0;
        long compositeBytes = 0;
        if (level == PdfViewerCacheTrimLevel.Critical)
            (composites, compositeBytes) = ContinuousPart.ClearCompositesOutsideViewport();

        // Every single-page bitmap except the one on screen (the view's TrimCache).
        var (singlePage, singlePageBytes) = SinglePagePart.TrimCache();

        // #1492: decoded image samples of pages outside the current bands, at
        // every level. Stricter than the render pass, which keeps every
        // REALIZED page: a realized page scrolled out of its band keeps its
        // samples there, but a trim lets them go (a later band render of that
        // page decodes again). Pages with a render in flight are always kept.
        var bandPages = new HashSet<int>();
        foreach (var key in ContinuousPart.ContinuousRequiredKeysForTests)
            bandPages.Add(key.Page);
        var (sampleStreams, sampleBytes) = ContinuousPart.ReleaseContinuousImageSamples(
            bandPages, ViewerMetrics.DecodedSampleReleaseTrim);

        ContinuousPart.RefreshContinuousByteMirrors();

        var result = new CacheTrimResult(level, tiles, tileBytes, composites, compositeBytes, singlePage, singlePageBytes,
            sampleStreams, sampleBytes, lookAheadTiles);
        LastCacheTrim = result;
        CacheTrimCount++;
        ViewerMetrics.RecordCacheTrim(level, tileBytes, compositeBytes, singlePageBytes);
        if (TraceEnabled)
            Trace($"TrimCaches {result}");
    }

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

    // ── diagnostics ──────────────────────────────────────────────────────────

    /// <summary>
    /// Capture the active single-page or continuous viewport without exposing
    /// template implementation details.
    /// </summary>
    public PdfViewerViewportDiagnostics GetViewportDiagnostics()
    {
        var viewport = ActiveViewportScrollViewer();
        return viewport == null
            ? new PdfViewerViewportDiagnostics(ViewMode, default, default, default, false)
            : new PdfViewerViewportDiagnostics(
                ViewMode,
                viewport.Extent,
                viewport.Viewport,
                viewport.Offset,
                true);
    }

    /// <summary>
    /// Times a finished single-page render (fresh or cached) was bound to the
    /// page Image. A placeholder does not count. Report-only, for the
    /// edit-mode switch measurements.
    /// </summary>
    internal long SinglePagePublishCount => SinglePagePart.SinglePagePublishCount;

    /// <summary>Bytes held by the single-page LRU (BGRA, 4 bytes per pixel).</summary>
    internal long SinglePageCacheResidentBytes() => SinglePagePart.SinglePageCacheResidentBytes();

    /// <summary>
    /// True while single-page work that the viewer started on its own is still
    /// running, so a measurement can wait for the viewer to go quiet.
    /// </summary>
    internal bool HasPendingSinglePageWork => IsLoading;

    /// <summary>
    /// Capture explicit telemetry for the viewer-owned single-page and
    /// continuous render caches. The two caches remain separate because their
    /// keys, retention budgets, and invalidation lifetimes differ.
    /// </summary>
    public PdfViewerRenderDiagnostics GetRenderDiagnostics()
    {
        var single = SinglePagePart.CacheDiagnostics();
        return new PdfViewerRenderDiagnostics(
            ViewMode,
            single.EntryCount,
            single.Capacity,
            single.Hits,
            single.Misses,
            ContinuousPart.ContinuousCacheCount,
            ContinuousPart.ContinuousCacheResidentBytes(),
            ContinuousPart.ContinuousCacheByteBudgetSetting,
            ContinuousPart.ContinuousRenderCacheHitCount,
            ContinuousPart.ContinuousInFlightCount);
    }

    /// <summary>
    /// Request a vertical scroll by <paramref name="deltaY"/> DIPs in the
    /// active viewport. Returns false when the viewer template is unavailable.
    /// </summary>
    public bool TryScrollViewportBy(double deltaY)
    {
        if (!double.IsFinite(deltaY))
            throw new ArgumentOutOfRangeException(nameof(deltaY), "Scroll delta must be finite.");

        var viewport = ActiveViewportScrollViewer();
        if (viewport == null)
            return false;

        SetVerticalOffset(viewport, viewport.Offset.Y + deltaY);
        return true;
    }

    /// <summary>
    /// Request a vertical position as a fraction of the active viewport's
    /// scrollable range: 0 is the top and 1 is the bottom. Returns false when
    /// the viewer template is unavailable.
    /// </summary>
    public bool TrySetViewportVerticalFraction(double fraction)
    {
        if (!double.IsFinite(fraction) || fraction < 0 || fraction > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fraction),
                "Viewport fraction must be finite and between 0 and 1.");
        }

        var viewport = ActiveViewportScrollViewer();
        if (viewport == null)
            return false;

        var maximum = Math.Max(0, viewport.Extent.Height - viewport.Viewport.Height);
        SetVerticalOffset(viewport, maximum * fraction);
        return true;
    }

    /// <summary>#1479 snapshot; see <see cref="MeasureContinuousBitmapOverlap"/>.</summary>
    internal readonly record struct ContinuousBitmapOverlap(
        int Tiles, long TileBytes, int BakedTiles, long BakedTileBytes, long CompositeBytes);


    /// <summary>This viewer's <c>viewer</c> tag on the per-viewer gauges (#1491).</summary>
    internal int MetricsViewerId { get; }


    internal SinglePageRenderLifetime<global::Avalonia.Media.Imaging.WriteableBitmap>.CacheDiagnostics
        MetricsSinglePageCache() => SinglePagePart.CacheDiagnostics();

    // #1491 gauge sources, owned by the continuous view (its mirrors are refreshed
    // on the UI thread; ViewerMetrics reads them from the listener's thread).
    internal long MetricsContinuousTileBytes => ContinuousPart.MetricsContinuousTileBytes;
    internal long MetricsContinuousCompositeBytes => ContinuousPart.MetricsContinuousCompositeBytes;
    internal int MetricsContinuousTileCount => ContinuousPart.MetricsContinuousTileCount;
    internal int MetricsContinuousInFlightCount => ContinuousPart.MetricsContinuousInFlightCount;
    internal int MetricsContinuousCacheHits => ContinuousPart.MetricsContinuousCacheHits;

    /// <summary>
    /// The ScrollViewer that currently owns the viewport — the continuous
    /// stack in Continuous mode, otherwise the single-page scroller. The
    /// wheel/pan handlers and the viewport diagnostics share it.
    /// </summary>
    private ScrollViewer? ActiveViewportScrollViewer() =>
        ViewMode == PdfViewMode.Continuous ? ContinuousScrollViewer : PdfScrollViewer;

    private static void SetVerticalOffset(ScrollViewer viewport, double requestedY)
    {
        var maximum = Math.Max(0, viewport.Extent.Height - viewport.Viewport.Height);
        var target = Math.Clamp(requestedY, 0, maximum);
        viewport.Offset = new Vector(viewport.Offset.X, target);
    }
}
