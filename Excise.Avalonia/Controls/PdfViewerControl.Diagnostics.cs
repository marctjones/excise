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

    private ScrollViewer? ActiveViewportScrollViewer() =>
        ViewMode == PdfViewMode.Continuous ? _continuousScrollViewer : _scrollViewer;

    private static void SetVerticalOffset(ScrollViewer viewport, double requestedY)
    {
        var maximum = Math.Max(0, viewport.Extent.Height - viewport.Viewport.Height);
        var target = Math.Clamp(requestedY, 0, maximum);
        viewport.Offset = new Vector(viewport.Offset.X, target);
    }
}
