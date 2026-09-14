using System;
using Avalonia;
using Avalonia.Controls;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Immutable diagnostic snapshot of the viewer's active viewport.
/// </summary>
/// <remarks>
/// This deliberately carries values rather than the underlying
/// <see cref="ScrollViewer"/>. Hosts and automation can observe scroll state
/// without depending on the viewer's XAML template or taking ownership of its
/// controls.
/// </remarks>
public readonly struct PdfViewerViewportDiagnostics
{
    /// <summary>Create a viewport diagnostic snapshot.</summary>
    public PdfViewerViewportDiagnostics(
        PdfViewMode viewMode,
        Size extent,
        Size viewport,
        Vector offset,
        bool isAvailable)
    {
        ViewMode = viewMode;
        Extent = extent;
        Viewport = viewport;
        Offset = offset;
        IsAvailable = isAvailable;
    }

    /// <summary>The view mode whose active viewport was sampled.</summary>
    public PdfViewMode ViewMode { get; }

    /// <summary>Total laid-out content size in DIPs.</summary>
    public Size Extent { get; }

    /// <summary>Visible viewport size in DIPs.</summary>
    public Size Viewport { get; }

    /// <summary>Current viewport offset in DIPs.</summary>
    public Vector Offset { get; }

    /// <summary>Whether the active viewport is initialized and available.</summary>
    public bool IsAvailable { get; }
}

/// <summary>
/// Immutable diagnostic snapshot of the two interactive render caches owned by
/// <see cref="PdfViewerControl"/>. Thumbnail and image-export lifetimes are
/// intentionally absent because they belong to their App workflows.
/// </summary>
public readonly struct PdfViewerRenderDiagnostics
{
    /// <summary>Create a render-cache diagnostic snapshot.</summary>
    public PdfViewerRenderDiagnostics(
        PdfViewMode viewMode,
        int singlePageEntryCount,
        int singlePageCapacity,
        long singlePageHits,
        long singlePageMisses,
        int continuousEntryCount,
        long continuousResidentBytes,
        long continuousByteBudget,
        int continuousHits,
        int continuousInFlightCount)
    {
        ViewMode = viewMode;
        SinglePageEntryCount = singlePageEntryCount;
        SinglePageCapacity = singlePageCapacity;
        SinglePageHits = singlePageHits;
        SinglePageMisses = singlePageMisses;
        ContinuousEntryCount = continuousEntryCount;
        ContinuousResidentBytes = continuousResidentBytes;
        ContinuousByteBudget = continuousByteBudget;
        ContinuousHits = continuousHits;
        ContinuousInFlightCount = continuousInFlightCount;
    }

    /// <summary>View mode active when the caches were sampled.</summary>
    public PdfViewMode ViewMode { get; }

    /// <summary>Single-page bitmaps currently retained by its LRU.</summary>
    public int SinglePageEntryCount { get; }

    /// <summary>Maximum number of bitmaps in the single-page LRU.</summary>
    public int SinglePageCapacity { get; }

    /// <summary>Single-page LRU hits since this viewer was constructed.</summary>
    public long SinglePageHits { get; }

    /// <summary>Single-page LRU misses since this viewer was constructed.</summary>
    public long SinglePageMisses { get; }

    /// <summary>Continuous-view tiles currently retained by its LRU.</summary>
    public int ContinuousEntryCount { get; }

    /// <summary>Estimated resident bytes retained by continuous-view tiles.</summary>
    public long ContinuousResidentBytes { get; }

    /// <summary>Continuous-view tile-cache byte budget.</summary>
    public long ContinuousByteBudget { get; }

    /// <summary>Continuous tile-cache hits since this viewer was constructed.</summary>
    public int ContinuousHits { get; }

    /// <summary>Continuous tile renders currently in flight.</summary>
    public int ContinuousInFlightCount { get; }
}

public partial class PdfViewerControl
{
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

    private long _singlePagePublishCount;

    /// <summary>
    /// Times a finished single-page render (fresh or cached) was bound to the
    /// page Image. A placeholder does not count. Report-only, for the
    /// edit-mode switch measurements.
    /// </summary>
    internal long SinglePagePublishCount => _singlePagePublishCount;

    /// <summary>Bytes held by the single-page LRU (BGRA, 4 bytes per pixel).</summary>
    internal long SinglePageCacheResidentBytes() =>
        _singlePageRenderLifetime.ResidentBytes(b => (long)b.PixelSize.Width * b.PixelSize.Height * 4);

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
        var single = _singlePageRenderLifetime.GetCacheDiagnostics();
        return new PdfViewerRenderDiagnostics(
            ViewMode,
            single.EntryCount,
            single.Capacity,
            single.Hits,
            single.Misses,
            _continuousCache.Count,
            ContinuousCacheResidentBytes(),
            ContinuousCacheByteBudget,
            ContinuousRenderCacheHitCount,
            _continuousInFlight.Count);
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

    /// <summary>#1479 snapshot; see <see cref="MeasureContinuousBitmapOverlap"/>.</summary>
    internal readonly record struct ContinuousBitmapOverlap(
        int Tiles, long TileBytes, int BakedTiles, long BakedTileBytes, long CompositeBytes);

    // #1491 gauge sources. ViewerMetrics reads these from the listener's thread,
    // so the byte totals (which walk UI-thread collections) are mirrors refreshed
    // on the UI thread; the counts are single int reads.
    private long _metricsContinuousTileBytes;
    private long _metricsContinuousCompositeBytes;

    /// <summary>This viewer's <c>viewer</c> tag on the per-viewer gauges (#1491).</summary>
    internal int MetricsViewerId { get; }

    internal long MetricsContinuousTileBytes => Volatile.Read(ref _metricsContinuousTileBytes);
    internal long MetricsContinuousCompositeBytes => Volatile.Read(ref _metricsContinuousCompositeBytes);
    internal int MetricsContinuousTileCount => _continuousCache.Count;
    internal int MetricsContinuousInFlightCount => _continuousInFlight.Count;
    internal int MetricsContinuousCacheHits => ContinuousRenderCacheHitCount;

    internal SinglePageRenderLifetime<global::Avalonia.Media.Imaging.WriteableBitmap>.CacheDiagnostics
        MetricsSinglePageCache() => _singlePageRenderLifetime.GetCacheDiagnostics();

    /// <summary>
    /// Refresh the continuous byte mirrors after the tile cache or the slot
    /// composites change. UI thread only; a no-op unless a byte gauge is enabled.
    /// </summary>
    private void RefreshContinuousByteMirrors()
    {
        if (!ViewerMetrics.ByteGaugesEnabled) return;
        Volatile.Write(ref _metricsContinuousTileBytes, ContinuousCacheResidentBytes());
        Volatile.Write(ref _metricsContinuousCompositeBytes, ContinuousCompositeResidentBytes());
    }

    private ScrollViewer? ActiveViewportScrollViewer() =>
        ViewMode == PdfViewMode.Continuous ? _continuousScrollViewer : _scrollViewer;

    private static void SetVerticalOffset(ScrollViewer viewport, double requestedY)
    {
        var maximum = Math.Max(0, viewport.Extent.Height - viewport.Viewport.Height);
        var target = Math.Clamp(requestedY, 0, maximum);
        viewport.Offset = new Vector(viewport.Offset.X, target);
    }
}
