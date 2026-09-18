using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Platform;
using global::Avalonia.Reactive;
using global::Avalonia.Threading;
using Excise.Rendering;
using SkiaSharp;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Continuous (reading) view mode for <see cref="PdfViewerControl"/> (#371 part 2).
/// A render-virtualized vertical scroll of every page: only the pages near the
/// viewport are rendered, bitmaps are bounded, and off-screen renders are
/// cancelled. This view is read-only — all editing happens in single-page mode
/// (entering an editing interaction auto-switches back), so none of the
/// security-critical redaction/selection overlays run here. Non-editing
/// ambient affordances DO run here: link click/hover hit-testing maps pointer
/// positions through the slot geometry below (#667, Interaction partial).
/// </summary>
public partial class PdfViewerControl
{
    private ScrollViewer? _continuousScrollViewer;
    private ItemsControl? _continuousItems;
    private List<PdfPageSlot>? _continuousSlots;

    // Grid-cell TILES, bounded by an LRU list and owned by it. A tile is disposed
    // the moment it leaves the list (eviction, or replacement under the same key)
    // and on document change (#1467).
    //
    // That is safe because, since #848, a tile is never an Image.Source: tiles are
    // only read by BlitCell while RecomposeSlotCore builds a composite, and the
    // only bitmap bound to an Image is the per-page COMPOSITE
    // (PdfPageSlot.Bitmap, PdfViewerControl.axaml). Eviction and compositing both
    // run on the UI thread, so a tile cannot be disposed mid-blit: the band render
    // awaits Task.Run without ConfigureAwait(false), and every dispatcher job runs
    // under AvaloniaSynchronizationContext, so SliceBandIntoCells ->
    // AddToContinuousCache and RecomposeSlot -> PeekContinuousCached -> BlitCell
    // all resume on the dispatcher. Keep it that way: an off-thread eviction would
    // race a blit.
    //
    // COMPOSITES are the opposite case and must NOT be disposed while bound:
    // Avalonia 12's Bitmap.Dispose releases its IRef<IBitmapImpl>, and an Image
    // that still points at it throws ObjectDisposedException on its next measure
    // or render. PdfPageSlot therefore releases a replaced or cleared composite
    // only after the binding has moved off it (ReleaseAfterBindingMoves, #1466);
    // composites are bounded separately, by ContinuousCompositeByteBound.
    private readonly LinkedList<(ContinuousTileKey Key, WriteableBitmap Bitmap)> _continuousCache = new();

    // Per-page PdfLink lists for continuous-mode link click/hover hit-testing
    // (#667). Populated lazily by GetContinuousPageLinks (Interaction partial);
    // cleared alongside the tile cache on document change / RenderVersion bump.
    private readonly Dictionary<int, IReadOnlyList<Excise.Core.Document.PdfLink>> _continuousPageLinks = new();

    // #848 grid render state. One document-wide CTS cancels every in-flight cell
    // render on a document/cache invalidation. In-flight keys coalesce duplicate
    // requests for the same cell; the required-key set (rebuilt each pass) lets a
    // queued render notice it has been scrolled past and bail before rendering.
    private CancellationTokenSource _continuousDocCts = new();
    private readonly HashSet<ContinuousTileKey> _continuousInFlight = new();
    private IReadOnlySet<ContinuousTileKey> _continuousRequiredKeys = new HashSet<ContinuousTileKey>();
    // #1492: the image and mask streams each page's band renders read, and the
    // document they belong to. A page's decoded samples stay pinned while it is
    // realized or has a render in flight, and are released once it is neither.
    private readonly DecodedImageSampleRetention _continuousImageSamples = new();
    private Excise.Core.Document.PdfDocument? _continuousImageSamplesDocument;
    // Cap concurrent cell renders: a grid multiplies the old per-page fan-out by
    // the visible cell count, and SkiaRenderer serializes typeface acquisition
    // process-wide (_typefaceLoadLock) — unbounded Task.Run just thrashes.
    //
    // Not readonly: ContinuousRenderConcurrency replaces the gate for renders
    // that start after the change. A render captures the gate it waited on and
    // releases that same instance, so in-flight renders finish on the old gate.
    private SemaphoreSlim _continuousRenderGate = new(DefaultContinuousRenderConcurrency);
    private int _continuousRenderConcurrency = DefaultContinuousRenderConcurrency;

    /// <summary>The render-gate width a new viewer starts with: clamp(CPU - 1, 2, 6).</summary>
    internal static int DefaultContinuousRenderConcurrency => Math.Clamp(Environment.ProcessorCount - 1, 2, 6);
    // #615/#848: the cache bounds total resident BYTES, not a flat entry count.
    // Under the content-addressed grid (#848), tiles are now UNIFORM — every
    // interior cell is a full ContinuousTileQuantumDip square, edge cells smaller
    // — so the old 10x per-tile spread is gone. Since #1472/#1480 a tile holds one
    // render pixel per device pixel at every zoom up to the MaxContinuousDpi cap
    // (fewer above it), so a worst-case tile is a full quantum cell at device
    // resolution: at ContinuousTileQuantumDip=256 that is 256x256 px (~0.25MB) at
    // dpr 1 and 512x512 px (~1MB) at dpr 2, Bgra8888 4 bytes/px (see
    // SkiaInterop.ToAvaloniaBitmap). Measurement lives in
    // Excise.Avalonia.Tests/ContinuousCacheMemoryTests.cs, which drives the real
    // CellToRequest + EffectiveContinuousDpi + ContinuousTileByteSize code paths.
    //
    // Budget: ~200MB peak resident bytes. That holds ~200 worst-case tiles at dpr
    // 2, i.e. the visible grid of the current page plus a generous scroll-back
    // buffer, so scrolling away and back is a cache hit rather than a re-render —
    // the reuse the grid was designed to make free. The budget was set when tiles
    // were 1.56x (and zoomed out up to 25x) larger; it was deliberately left
    // alone by #1480, which changed pixel density, not retention policy (#1478).
    // If tile geometry changes (quantum, overscan, or the DPI model) re-run
    // ContinuousCacheMemoryTests and reconsider this number -- don't just restate it.
    private const long ContinuousCacheByteBudget = 200L * 1024 * 1024;

    /// <summary>
    /// Test hook: replace <see cref="ContinuousCacheByteBudget"/> so a test can
    /// force tile eviction with real (Skia-backed) bitmaps without allocating
    /// hundreds of MB. Null in production.
    /// </summary>
    internal long? ContinuousCacheByteBudgetOverride { get; set; }

    /// <summary>
    /// The host-configured tile budget (<see cref="ContinuousTileCacheByteBudget"/>);
    /// starts at <see cref="ContinuousCacheByteBudget"/>.
    /// </summary>
    private long _continuousCacheByteBudget = ContinuousCacheByteBudget;

    private long EffectiveContinuousCacheByteBudget =>
        ContinuousCacheByteBudgetOverride ?? _continuousCacheByteBudget;

    // #1466: page COMPOSITES are not in the tile budget above, and cannot be: a
    // composite is bound to an Image while its page is shown, so an LRU could not
    // evict it, and counting it there would only evict more tiles. They get their
    // own bound instead, which leaves tile retention exactly as it was.
    //
    // Only a realized page slot holds a composite (PdfPageSlot.Bitmap), at most
    // one: its visible band — the viewport plus ContinuousTileOverscanDip, snapped
    // to the tile grid and clipped to the page — at the render DPI. Every render
    // pass clears the composites of slots that are no longer realized, and
    // RecomposeSlotCore never publishes for one; container recycling cannot do it
    // (see OnContinuousContainerClearing), and before this a page jump kept every
    // composite it ever published. A replaced or cleared composite is released
    // once the binding has moved off it (PdfPageSlot.ReleaseAfterBindingMoves), so
    // the total is set by the current viewport, not by scroll or zoom history.
    //
    // Since #1472/#1480 it is set by the viewport ALONE. The render DPI tracks the
    // display (EffectiveContinuousDpi), so a composite holds at most one pixel per
    // device pixel of its band, and the bands of all realized pages together span
    // at most the viewport plus ContinuousTileOverscanDip plus one grid quantum on
    // each side. Page size and zoom no longer enter it. (Under the old 120 x dpr
    // floor the band grew as zoom shrank: the worst case was ~164 MiB at zoom 0.25,
    // and a D-size sheet needed ~760 MiB there, outside any bound.)
    //
    // Bound: 138 MiB = that device-pixel ceiling for a 2560x1440 DIP viewport at
    // dpr 2: (2560 + 2x512) x (1440 + 2x512) DIP at up to 2.021 px/DIP (dpr plus
    // integer-DPI rounding at zoom 0.25), 4 bytes/px. ContinuousCacheMemoryTests
    // derives it and sweeps pages up to D-size, viewports up to 2560x1440, dpr up
    // to 2 and zoom 0.25-5 against the per-viewport ceiling; measured worst 121.1 MB
    // (landscape Tabloid, zoom 2.5, 2x 2560x1440). OUTSIDE it: (1) a viewport larger
    // than 2560x1440 DIP or a dpr above 2; (2) a realized slot whose page has
    // scrolled out of viewport + overscan keeps its last composite until the page
    // stops being realized (RecomposeSlotCore keeps it rather than blanking a page
    // the panel still shows). If tile geometry, overscan or the DPI model changes,
    // re-run ContinuousCacheMemoryTests and re-derive this number.
    //
    // RecomposeSlotCore checks the bound after every composite it publishes and
    // traces a warning when the slots' composites exceed it, so a viewport
    // outside the envelope (or a regression inside it) is visible in a traced
    // live session rather than only in the memory tests.
    internal const long ContinuousCompositeByteBound = 138L * 1024 * 1024;

    /// <summary>
    /// Test hook: replace <see cref="ContinuousCompositeByteBound"/> so a test can
    /// drive the over-bound warning with ordinary composites. Null in production.
    /// </summary>
    internal long? ContinuousCompositeByteBoundOverride { get; set; }

    private long EffectiveContinuousCompositeByteBound =>
        ContinuousCompositeByteBoundOverride ?? ContinuousCompositeByteBound;

    /// <summary>
    /// How many published composites have left the slots' composites above
    /// <see cref="EffectiveContinuousCompositeByteBound"/> (#1466). Reported in
    /// the over-bound warning itself.
    /// </summary>
    internal int ContinuousCompositeOverBoundCount { get; private set; }

    // Always keep at least this many entries, even if a single tile alone
    // exceeds the byte budget -- a single huge page must not defeat the LRU
    // entirely and force a full re-render on every scroll frame.
    private const int ContinuousCacheMinEntries = 2;

    internal const double PointsToDip = 96.0 / 72.0;
    internal const double PageGapDip = 12.0;   // matches the DataTemplate Border bottom margin
    internal const int ContinuousTileQuantumDip = 256;
    internal const int ContinuousTileOverscanDip = 256;

    // Sharp high-zoom (#371): render each continuous page at a DPI that scales
    // with zoom so it stays crisp instead of upscaling a fixed-DPI bitmap, capped
    // so deep zoom stays bounded. Realized pages render only the visible region
    // through RenderOptions.ClipRect rather than allocating a full-page bitmap.
    internal const int MaxContinuousDpi = 240;

    /// <summary>
    /// The continuous view's render DPI at zoom 1 on a 1× display (#1480): one
    /// render pixel per DIP. A slot lays a page point out at
    /// <see cref="PointsToDip"/> (96/72) DIP × zoom and an Avalonia DIP is 1/96
    /// inch, so <c>96 × zoom × dpr</c> DPI is exactly the display's device
    /// resolution. This is NOT <see cref="DefaultRenderDpi"/> (120): that is the
    /// single-page view's logical layout DPI, which the continuous view borrowed
    /// when it was created (#371) and which rendered 1.25× the display's linear
    /// resolution (1.56× the pixels) at every zoom.
    /// </summary>
    internal const int ContinuousBaseDpi = 96;

    /// <summary>
    /// Safety minimum for <see cref="EffectiveContinuousDpi"/>. Below the app's
    /// minimum zoom (0.25) it never binds on a real display; it only keeps a
    /// degenerate zoom from producing a zero-size raster. It is NOT a legibility
    /// floor: a floor above device resolution is what #1472 removed.
    /// </summary>
    internal const int MinContinuousDpi = 12;

    private int ContinuousRenderDpi =>
        EffectiveContinuousDpi(ContinuousBaseDpi, ZoomLevel, MaxContinuousDpi, EffectiveRenderScaling);

    /// <summary>
    /// The render DPI chosen for a given zoom and display device-pixel-ratio
    /// (pure; unit-tested): <c>baseDpi × zoom × dpr</c>, so a continuous tile has
    /// one render pixel per device pixel at every zoom, capped at
    /// <c>maxDpi × dpr</c> (#683) so deep zoom stays bounded. The tile is laid out
    /// by its DIP dimensions, so render pixels change only sharpness, never
    /// geometry.
    /// <para>
    /// History, measured rather than restated (2026-09-13). #682 multiplied by the
    /// device-pixel-ratio to stop HiDPI text upscaling. #682's own reasoning
    /// ("a page point occupies ~2.67 device pixels" on a 2× display) describes
    /// 192 DPI, but the code rendered at 120 × dpr = 240. It also floored the DPI
    /// at 120 × dpr regardless of zoom, so zoomed-out pages rendered up to
    /// 1/zoom² more pixels than the screen shows (#1472: 25× at zoom 0.25).
    /// </para>
    /// <para>
    /// The extra 1.25× was not buying crispness. Against an independent oracle
    /// (mutool at the device resolution, IRS 1040 instructions p10 and p47),
    /// excise rendered 1:1 at 192 DPI matched mutool's edge energy (1.00×,
    /// mean abs error 1.59 / 1.82). The 240-DPI render downscaled to the same
    /// pixels was softer with bilinear resampling (0.90×, error 1.88 / 2.61) and
    /// over-sharpened with Lanczos (1.13× / 1.05×, error 2.00 / 2.40). At dpr 1
    /// (96 vs 120 DPI) bilinear was 0.78×. The composite Image sets no
    /// BitmapInterpolationMode, so it resamples with Avalonia's default; both
    /// resamplers sit on the far side of 1:1.
    /// </para>
    /// </summary>
    internal static int EffectiveContinuousDpi(int baseDpi, double zoom, int maxDpi, double renderScaling)
    {
        double dpr = Math.Clamp(renderScaling <= 0 ? 1.0 : renderScaling, 1.0, 4.0);
        return (int)Math.Clamp(
            Math.Round(baseDpi * zoom * dpr),
            MinContinuousDpi,
            Math.Round(maxDpi * dpr));
    }

    /// <summary>
    /// The display's device-pixel-ratio (2.0 on a Retina/HiDPI screen, 1.0 on a
    /// standard one), or 1.0 before the control is attached to a visual root.
    /// </summary>
    private double EffectiveRenderScaling =>
        RenderScalingOverride ?? TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    /// <summary>
    /// Test hook: simulate a HiDPI display in the headless test host (which
    /// always reports RenderScaling 1.0). The #682/#683 device-resolution
    /// paths are a functional no-op at dpr=1, so without this override no
    /// automated test can exercise the code Retina users actually run.
    /// </summary>
    internal double? RenderScalingOverride { get; set; }

    // Guards the scroll -> CurrentPage -> scroll feedback loop.
    private bool _syncingPageFromScroll;

    /// <summary>
    /// Page a programmatic navigation is trying to reach but has not reached yet
    /// (the ScrollViewer clamps Offset to a not-yet-computed extent). While this
    /// is set, scroll events must not derive CurrentPage from the stale offset.
    /// </summary>
    private int? _pendingContinuousPage;
    private int _pendingContinuousAttempts;
    private bool _continuousRenderPassScheduled;
    // True once the control has left the visual tree — hard-stops all continuous
    // rendering so a closed viewer can't touch a disposed document (#848).
    private bool _continuousDetached;

    // Intra-page position carried across a view-mode switch (#693): the
    // fraction of the current page sitting at the viewport top. Continuous
    // uses it inside the pending-scroll retry; single-page has its own
    // bounded retry because a ScrollViewer clamps Offset to a zero extent
    // before layout.
    private double _pendingContinuousFraction;
    private double _pendingSingleFraction = -1;
    private IDisposable? _pendingSingleFractionSub;

    internal int ContinuousRenderStartCount { get; private set; }
    internal int ContinuousRenderCancellationCount { get; private set; }
    internal int ContinuousRenderCacheHitCount { get; private set; }
    internal int ContinuousRenderCoalescedRequestCount { get; private set; }

    // #855 diagnostics. A continuous-render wait that fails on CI reports only
    // "did not render within Ns", which cannot distinguish "genuinely slow" from
    // "waiting on something that will never arrive" — the two have completely
    // different fixes, and the first read of #855 guessed wrong. These make the
    // NEXT failure legible from the CI log alone (no Windows machine required).
    internal int ContinuousRenderCompletedCount { get; private set; }
    internal long ContinuousRenderWallMs { get; private set; }
    internal int ContinuousRequiredCellCount => _continuousRequiredKeys.Count;
    internal int ContinuousInFlightCount => _continuousInFlight.Count;
    internal int ContinuousEffectiveRenderDpi => ContinuousRenderDpi;

    /// <summary>
    /// One-line snapshot of the continuous render pipeline, for embedding in a
    /// test's timeout message (#855). Names the two things a wall-clock timeout
    /// cannot tell apart: how much work the pass demanded (cells/inflight) and
    /// how much of it actually ran (starts/completed/wall).
    /// </summary>
    internal string ContinuousDiagnostics()
    {
        var vp = _continuousScrollViewer?.Viewport ?? default;
        var off = _continuousScrollViewer?.Offset ?? default;
        int slots = _continuousSlots?.Count ?? -1;
        long perCell = ContinuousRenderCompletedCount > 0
            ? ContinuousRenderWallMs / ContinuousRenderCompletedCount
            : -1;
        return $"cellsRequired={ContinuousRequiredCellCount} inFlight={ContinuousInFlightCount} " +
               $"starts={ContinuousRenderStartCount} completed={ContinuousRenderCompletedCount} " +
               $"cacheHits={ContinuousRenderCacheHitCount} coalesced={ContinuousRenderCoalescedRequestCount} " +
               $"cancelled={ContinuousRenderCancellationCount} renderWallMs={ContinuousRenderWallMs} " +
               $"perCellMs={perCell} gate={_continuousRenderGate.CurrentCount} " +
               $"passScheduled={_continuousRenderPassScheduled} detached={_continuousDetached} " +
               $"viewMode={ViewMode} zoom={ZoomLevel:F2} dpi={ContinuousEffectiveRenderDpi} " +
               $"viewport={vp.Width:F0}x{vp.Height:F0} offsetY={off.Y:F0} slots={slots}";
    }

    private void InitializeContinuous()
    {
        _continuousScrollViewer = this.FindControl<ScrollViewer>("ContinuousScrollViewer");
        _continuousItems = this.FindControl<ItemsControl>("ContinuousItems");

        if (_continuousItems != null)
        {
            _continuousItems.ContainerPrepared += OnContinuousContainerPrepared;
            _continuousItems.ContainerClearing += OnContinuousContainerClearing;
            _continuousItems.LayoutUpdated += OnContinuousItemsLayoutUpdated;
        }
        if (_continuousScrollViewer != null)
        {
            _continuousOffsetSubscription = _continuousScrollViewer
                .GetObservable(ScrollViewer.OffsetProperty)
                .Subscribe(new AnonymousObserver<Vector>(_ => OnContinuousScrolled()));
            _continuousViewportSubscription = _continuousScrollViewer
                .GetObservable(ScrollViewer.ViewportProperty)
                .Subscribe(new AnonymousObserver<Size>(OnContinuousViewportChanged));
            // Permanent: re-apply a pending zoom anchor once layout gives the
            // ScrollViewer its post-re-layout extent. POSTED, not applied
            // synchronously — an Offset write inside the extent-change
            // notification is re-clamped by the ScrollViewer's own layout
            // coercion and silently lost (#700).
            _continuousExtentSubscription = _continuousScrollViewer
                .GetObservable(ScrollViewer.ExtentProperty)
                .Subscribe(new AnonymousObserver<Size>(_ =>
                {
                    if (_pendingZoomAnchorPage > 0)
                        Dispatcher.UIThread.Post(ApplyPendingZoomAnchor, DispatcherPriority.Loaded);
                }));
        }
    }

    private void OnContinuousViewportChanged(Size viewport)
    {
        OnScrollViewerViewportChanged(viewport);
        RenderVisibleContinuousTiles();
    }

    private void OnViewModeChanged()
    {
        bool continuous = ViewMode == PdfViewMode.Continuous;
        Trace($"ViewMode -> {ViewMode} page={CurrentPage} zoom={ZoomLevel:F3} " +
              $"contOffset={_continuousScrollViewer?.Offset.Y:F0}/{_continuousScrollViewer?.Extent.Height:F0} " +
              $"singleOffset={_scrollViewer?.Offset.Y:F0}/{_scrollViewer?.Extent.Height:F0}");

        // Capture the reader's intra-page position BEFORE flipping
        // visibility — a hidden ScrollViewer's offset is not trustworthy.
        // Applied to the destination view once it has laid out (#693).
        double fraction = continuous ? SingleIntraPageFraction() : ContinuousIntraPageFraction();

        if (_continuousScrollViewer != null) _continuousScrollViewer.IsVisible = continuous;
        if (_scrollViewer != null) _scrollViewer.IsVisible = !continuous;

        // #1564: render-ahead belongs to the view that scheduled it.
        if (continuous) CancelSinglePageLookAhead();
        else CancelContinuousLookAhead();

        if (continuous)
        {
            RebuildContinuous();
            ReportActiveViewport();

            // Defer the scroll-to until the items panel has measured the slots —
            // but read CurrentPage when the callback RUNS, not when it is posted.
            //
            // Capturing it here (`int target = CurrentPage;`) captured a STALE page:
            // a navigation issued between the post and the callback would be
            // overwritten by this deferred scroll dragging the user back to
            // wherever they were when the view mode flipped. Switching to
            // continuous and immediately jumping to a page did exactly that.
            Dispatcher.UIThread.Post(() => ScrollToPageContinuous(CurrentPage, fraction), DispatcherPriority.Background);
        }
        else
        {
            ReportActiveViewport();
            // Back to single-page: make sure the current page is rendered.
            // The carried fraction is applied from the render-completion
            // paths, NOT posted here: a post now would burn all its retries
            // through the dispatcher before the async render gives the
            // ScrollViewer a real extent, then give up.
            _pendingSingleFraction = fraction;
            _ = RenderCurrentPageAsync();
        }

        UpdateViewerAutomationProperties();
    }

    /// <summary>Fraction of the current page above the viewport top in continuous view.</summary>
    private double ContinuousIntraPageFraction()
    {
        if (_continuousScrollViewer == null || _continuousSlots == null) return 0;
        int idx = CurrentPage - 1;
        if (idx < 0 || idx >= _continuousSlots.Count) return 0;
        var slot = _continuousSlots[idx];
        if (slot.DisplayHeight <= 0) return 0;
        return Math.Clamp(
            (_continuousScrollViewer.Offset.Y - slot.TopDip) / slot.DisplayHeight, 0, 0.99);
    }

    /// <summary>Fraction of the page above the viewport top in single-page view.</summary>
    private double SingleIntraPageFraction()
    {
        if (_scrollViewer == null) return 0;
        var extent = _scrollViewer.Extent.Height;
        if (extent <= 1) return 0;
        return Math.Clamp(_scrollViewer.Offset.Y / extent, 0, 0.99);
    }

    /// <summary>
    /// The single-page ScrollViewer clamps Offset to a zero extent until the
    /// freshly-rendered page has laid out — and layout may be arbitrarily far
    /// away (headless hosts only lay out on explicit pumps), so
    /// dispatcher-post retries drain uselessly before it. Instead, wait on
    /// the Extent property itself and apply the carried fraction the moment
    /// the content gets a real size.
    /// </summary>
    private bool _applyingSingleFraction;

    private void ApplyPendingSingleFraction()
    {
        // Same re-entrancy guard as ApplyPendingZoomAnchor: GetObservable
        // emits the current value synchronously on subscribe, which would
        // re-enter here before the subscription field is assigned.
        if (_applyingSingleFraction) return;
        _applyingSingleFraction = true;
        try
        {
            ApplyPendingSingleFractionCore();
        }
        finally
        {
            _applyingSingleFraction = false;
        }
    }

    private void ApplyPendingSingleFractionCore()
    {
        if (_pendingSingleFraction < 0 || _scrollViewer == null)
        {
            _pendingSingleFractionSub?.Dispose();
            _pendingSingleFractionSub = null;
            return;
        }
        var extent = _scrollViewer.Extent.Height;
        if (extent <= 1)
        {
            _pendingSingleFractionSub ??= _scrollViewer
                .GetObservable(ScrollViewer.ExtentProperty)
                .Subscribe(new AnonymousObserver<Size>(_ => ApplyPendingSingleFraction()));
            return;
        }
        _pendingSingleFractionSub?.Dispose();
        _pendingSingleFractionSub = null;
        _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, _pendingSingleFraction * extent);
        _pendingSingleFraction = -1;
    }

    /// <summary>(Re)build the per-page slots from the current document.</summary>
    private void RebuildContinuous()
    {
        if (_continuousItems == null) return;
        var doc = Document;
        if (doc == null) { ClearContinuous(); return; }

        var slots = new List<PdfPageSlot>(doc.PageCount);
        for (int i = 1; i <= doc.PageCount; i++)
        {
            var page = doc.GetPage(i);
            slots.Add(new PdfPageSlot(i, page.VisualWidth, page.VisualHeight, ZoomLevel));
        }
        ApplyContinuousSlotLayout(slots);
        // #1466: a structural refresh (RefreshContinuousLayout) or a return to
        // this view replaces the slots without going through
        // InvalidateContinuousCache, so the outgoing slots can still hold their
        // band-sized composites. Release them rather than leave them to the
        // finalizer; each slot defers the dispose until its binding has moved.
        ReleaseSlotComposites(_continuousSlots);
        _continuousSlots = slots;
        RefreshContinuousByteMirrors();
        _continuousItems.ItemsSource = slots;

        // Re-assert CurrentPage now that the slots exist.
        //
        // A navigation can arrive BEFORE the document reaches the viewer — the
        // ViewModel sets the page and the Document binding propagates a frame
        // later. At that moment there are no slots to scroll to, so the request
        // could only be latched... and OnDocumentChanged calls
        // InvalidateContinuousCache(), which clears the latch. The navigation
        // was lost in exactly the window it needed to survive.
        //
        // So don't depend on the latch surviving a document change. The viewer's
        // CurrentPage IS the request; once slots exist, honour it. If it is
        // already page 1 this is a no-op.
        if (_preserveReadingOnNextRebuild)
        {
            // #846: a structural mutation (rotate) asked to keep the reader in
            // place. Restore CurrentPage + the snapshotted fraction through the
            // ROBUST extent-settle anchor loop (the mechanism the zoom path uses),
            // not the one-shot below — the one-shot lands once against a
            // not-yet-settled extent and is then displaced as tiles render, which
            // is the #846 "former top off-screen / bounce".
            _preserveReadingOnNextRebuild = false;
            // Anchor to CurrentPage (VM identity-tracked across rotate/remove/move)
            // at the fraction snapshotted pre-mutation. Because the fraction is
            // page-relative and the VM keeps CurrentPage on the reader's content,
            // it transfers to the page's new number for free.
            _pendingZoomAnchorPage = CurrentPage;
            _pendingZoomAnchorFraction = _preservedReadingFraction;
            ApplyPendingZoomAnchor();
        }
        else
        {
            Dispatcher.UIThread.Post(() => ScrollToPageContinuous(CurrentPage), DispatcherPriority.Loaded);
        }
    }

    private void ClearContinuous()
    {
        ReleaseSlotComposites(_continuousSlots);
        if (_continuousItems != null) _continuousItems.ItemsSource = null;
        _continuousSlots = null;
        RefreshContinuousByteMirrors();
    }

    // Clear every slot's composite; each slot disposes it once its binding has
    // moved (PdfPageSlot.ReleaseAfterBindingMoves, #1466).
    private static void ReleaseSlotComposites(IEnumerable<PdfPageSlot>? slots)
    {
        if (slots == null) return;
        foreach (var slot in slots) slot.ClearComposite();
    }

    // Cancel every in-flight grid-cell render and start a fresh generation. Safe
    // to call repeatedly (detach, document change, cache invalidation) — a queued
    // render observes the cancelled token and bails.
    private void CancelContinuousCellRenders()
    {
        try { _continuousDocCts.Cancel(); } catch (ObjectDisposedException) { }
        try { _continuousDocCts.Dispose(); } catch (ObjectDisposedException) { }
        _continuousDocCts = new CancellationTokenSource();
        _continuousInFlight.Clear();
        _continuousRequiredKeys = new HashSet<ContinuousTileKey>();
        // The look-ahead batch is linked to the old document token, so it is
        // already cancelled; drop its plan with it (#1564).
        CancelContinuousLookAhead();
    }

    private void InvalidateContinuousCache()
    {
        CancelContinuousCellRenders();
        _continuousRenderPassScheduled = false;
        _pendingContinuousPage = null;
        // Clear each slot's composite (released once its binding has moved,
        // #1466) and dispose every cached tile (#1467). The slots themselves are
        // rebuilt by RebuildContinuous.
        ReleaseSlotComposites(_continuousSlots);
        foreach (var entry in _continuousCache) entry.Bitmap.Dispose();
        _continuousCache.Clear();
        _continuousLookAheadTiles.Clear();
        RefreshContinuousByteMirrors();
        _continuousPageLinks.Clear();
        // Same lifetime as the link cache: an annotation cache that outlived
        // the document would hover notes from the previous file (#1074).
        _pageAnnotations.Clear();
        _lastHoveredAnnotation = null;
        // Selection state is per-document/page; drop the letter cache and any
        // in-flight selection so a document or render change can't reuse stale
        // glyphs (#815). Highlight rects live on the slots and are discarded when
        // the slots are rebuilt.
        _continuousPageLetterCache.Clear();
        _continuousSelectionAnchor = null;
        _continuousSelectionFocus = null;
        _continuousSelectionPage = 0;

        // #1492: records of another document are dropped, never released —
        // those streams are not this viewer's to touch any more. On the same
        // document (an annotation toggle, a redaction's render-version bump) the
        // records stay: a page's streams only over-approximate what it now
        // reads, which costs at most a re-decode, and dropping them would pin
        // every visited page's samples until that page rendered again.
        if (!ReferenceEquals(_continuousImageSamplesDocument, Document))
        {
            _continuousImageSamples.Clear();
            _continuousImageSamplesDocument = Document;
        }
    }

    /// <summary>
    /// Merge the image and mask streams one band render of <paramref name="pageNumber"/>
    /// read into its record (#1492). Dropped when the render belonged to a
    /// document the viewer no longer shows.
    /// </summary>
    private void RecordContinuousImageSamples(
        Excise.Core.Document.PdfDocument renderedDocument,
        int pageNumber,
        IReadOnlyCollection<Excise.Core.Primitives.PdfStream> streams)
    {
        if (streams.Count == 0 || !ReferenceEquals(renderedDocument, Document))
            return;
        if (!ReferenceEquals(_continuousImageSamplesDocument, renderedDocument))
        {
            _continuousImageSamples.Clear();
            _continuousImageSamplesDocument = renderedDocument;
        }
        _continuousImageSamples.Record(pageNumber, streams);
    }

    /// <summary>
    /// Release the decoded image samples of pages that are no longer realized
    /// (#1492), keeping every stream a realized page or a page with a render in
    /// flight has read. Recomputed from the current state on every call, so
    /// calling it again is harmless.
    /// </summary>
    private void ReleaseImageSamplesOfUnrealizedPages(IReadOnlySet<int>? realizedPages = null)
    {
        if (_continuousImageSamples.IsEmpty || _continuousDetached)
            return;

        if (realizedPages == null)
        {
            var pages = new HashSet<int>();
            if (_continuousItems != null)
            {
                foreach (var container in _continuousItems.GetRealizedContainers())
                {
                    if (container.DataContext is PdfPageSlot slot)
                        pages.Add(slot.PageNumber);
                }
            }
            realizedPages = pages;
        }

        if (_continuousLookAheadSamplePages.Count > 0)
        {
            // #1564: the neighbours rendered ahead from here keep theirs too.
            var keep = new HashSet<int>(realizedPages);
            keep.UnionWith(_continuousLookAheadSamplePages);
            realizedPages = keep;
        }
        ReleaseContinuousImageSamples(realizedPages, ViewerMetrics.DecodedSampleReleaseUnrealized);
    }

    /// <summary>
    /// Release every recorded stream not read by <paramref name="keepPages"/>
    /// or by a page with a render in flight (#1492). UI thread only; never
    /// waits on a decode in progress.
    /// </summary>
    private (int Streams, long Bytes) ReleaseContinuousImageSamples(IReadOnlySet<int> keepPages, string reason)
    {
        if (_continuousImageSamples.IsEmpty)
            return default;

        var keep = ContinuousImageSampleKeepPages(keepPages, _continuousInFlight);
        var (streams, bytes) = _continuousImageSamples.ReleaseAllExcept(keep);
        ViewerMetrics.RecordDecodedSampleRelease(reason, streams, bytes);
        if (streams > 0 && TraceEnabled)
            Trace($"ImageSamplesReleased reason={reason} streams={streams} bytes={bytes} kept=[{string.Join(",", keep.Order())}]");
        return (streams, bytes);
    }

    /// <summary>
    /// The pages whose image samples stay pinned (#1492): <paramref name="pages"/>
    /// plus every page with a band render in flight. An in-flight render's
    /// streams are not recorded until it lands, and the page they belong to
    /// must not lose its samples mid-render.
    /// </summary>
    internal static HashSet<int> ContinuousImageSampleKeepPages(
        IEnumerable<int> pages, IEnumerable<ContinuousTileKey> inFlight)
    {
        var keep = new HashSet<int>(pages);
        foreach (var key in inFlight)
            keep.Add(key.Page);
        return keep;
    }

    /// <summary>The #1492 per-page stream records (tests only).</summary>
    internal DecodedImageSampleRetention ContinuousImageSamplesForTests => _continuousImageSamples;

    /// <summary>
    /// Called on the render thread with the page number just before a band
    /// render starts (tests only), so a test can hold a render in flight.
    /// </summary>
    internal Action<int>? ContinuousBandRenderStartingForTests { get; set; }

    /// <summary>
    /// Resize every slot to the new zoom (bindings re-layout the borders) and
    /// re-render the currently-realized pages at the new zoom-aware DPI so they
    /// stay sharp. Off-screen pages re-render lazily when realized.
    /// </summary>
    private void ApplyContinuousZoom()
    {
        if (_continuousSlots == null) return;

        // Anchor the viewport across the re-layout (#700). Re-laying the
        // slots at a new zoom while keeping the NUMERIC scroll offset slides
        // the viewport pages away from what the user was reading — and since
        // Offset never changes, no scroll event fires and the scroll→
        // CurrentPage sync silently freezes (live trace: four zoom-outs left
        // the label on page 17 while the screen showed page ~22). Capture
        // page + intra-page fraction at the viewport top against the OLD
        // layout, re-layout, then restore that reading position — the offset
        // assignment also fires the sync. A pending programmatic navigation
        // wins over anchoring.
        int anchorPage = 0;
        double anchorFraction = 0;
        if (_continuousScrollViewer != null && _pendingContinuousPage == null)
        {
            var anchor = ContinuousReadingAnchor.Capture(
                SlotBoxes(_continuousSlots), _continuousScrollViewer.Offset.Y, PageGapDip);
            anchorPage = anchor.Page;
            anchorFraction = anchor.Fraction;
        }

        ApplyContinuousSlotLayout(_continuousSlots);

        if (anchorPage > 0 && _continuousScrollViewer != null)
        {
            // The ScrollViewer clamps Offset against the PRE-layout extent
            // until the next layout pass, so a zoom-IN target (which grows)
            // would silently clamp short — and dispatcher-post retries drain
            // before layout ever runs (the #693 lesson). Apply via the
            // Extent observable instead.
            _pendingZoomAnchorPage = anchorPage;
            _pendingZoomAnchorFraction = anchorFraction;
            ApplyPendingZoomAnchor();
        }

        RenderVisibleContinuousTiles();
    }

    private int _pendingZoomAnchorPage;
    private double _pendingZoomAnchorFraction;

    private bool _preserveReadingOnNextRebuild;
    private double _preservedReadingFraction;

    /// <summary>
    /// #846: snapshot the reader's current intra-page position so the NEXT
    /// <see cref="RebuildContinuous"/> (triggered by a structural mutation
    /// reloading the document) restores it via the robust extent-settle anchor
    /// loop, instead of the default jump to the top of the current page. Called
    /// from the mutation path BEFORE the document swaps, while the current slots
    /// and offset are still valid. No-op outside the continuous view.
    /// </summary>
    /// <summary>
    /// Rebuild the continuous page layout because the document's STRUCTURE
    /// changed (pages added, removed, moved, rotated) while the document
    /// INSTANCE stayed the same (#917).
    ///
    /// Deliberately narrower than a RenderVersion bump. That path also calls
    /// <see cref="InvalidatePageCache"/>, which disposes the bitmap the
    /// single-page Image is still displaying and re-renders asynchronously —
    /// leaving a window where layout touches a disposed bitmap
    /// (ObjectDisposedException in Image.MeasureOverride, caught by the
    /// click-safety sweep). Page CONTENT has not changed here, only the page
    /// order, so the rendered tiles stay valid.
    /// </summary>
    public void RefreshContinuousLayout()
    {
        if (ViewMode != PdfViewMode.Continuous || Document == null)
            return;

        RebuildContinuous();
        RenderVisibleContinuousTiles();
    }

    public void PreserveContinuousReadingPositionOnNextRebuild()
    {
        if (ViewMode != PdfViewMode.Continuous || _continuousScrollViewer == null || _continuousSlots == null)
            return;
        // Snapshot the fraction of the reader's page (CurrentPage) NOW, while slots
        // and CurrentPage still describe the pre-mutation world. RebuildContinuous
        // re-applies it against the post-mutation CurrentPage, so page identity is
        // handled by the VM's CurrentPage tracking rather than inferred here.
        _preservedReadingFraction = ContinuousIntraPageFraction();
        _preserveReadingOnNextRebuild = true;
    }

    /// <summary>Project the live slots onto the pure vertical geometry the reading-anchor math uses.</summary>
    private static IReadOnlyList<SlotBox> SlotBoxes(IReadOnlyList<PdfPageSlot> slots)
    {
        var boxes = new SlotBox[slots.Count];
        for (int i = 0; i < slots.Count; i++)
            boxes[i] = new SlotBox(slots[i].TopDip, slots[i].DisplayHeight);
        return boxes;
    }


    private void ApplyPendingZoomAnchor()
    {
        if (_pendingZoomAnchorPage <= 0 || _continuousScrollViewer == null || _continuousSlots == null ||
            _pendingZoomAnchorPage > _continuousSlots.Count)
        {
            return;
        }

        var boxes = SlotBoxes(_continuousSlots);
        var anchor = new ReadingAnchor(_pendingZoomAnchorPage, _pendingZoomAnchorFraction);
        var target = ContinuousReadingAnchor.ResolveTarget(boxes, anchor);
        // Clamp to the reachable maximum. If the target lies beyond it (deep
        // zoom-out near the end of the document), pinning to the max IS the anchor
        // — the viewport now covers proportionally more document.
        var reachable = ContinuousReadingAnchor.ClampToExtent(
            target, _continuousScrollViewer.Extent.Height, _continuousScrollViewer.Viewport.Height);
        _continuousScrollViewer.Offset = new Vector(_continuousScrollViewer.Offset.X, reachable);

        if (Math.Abs(_continuousScrollViewer.Offset.Y - target) <= 1.0 ||
            (reachable < target && Math.Abs(_continuousScrollViewer.Offset.Y - reachable) <= 1.0 && ExtentReflectsSlots()))
        {
            // Anchored (or correctly pinned at the true max). Done — the
            // permanent extent subscription stops re-posting once this is 0.
            _pendingZoomAnchorPage = 0;
        }
        // else: the extent still reflects the pre-re-layout world; the extent
        // subscription in InitializeContinuous posts us again when it updates.
    }

    /// <summary>The ScrollViewer's extent matches the freshly-laid-out slots.</summary>
    private bool ExtentReflectsSlots()
    {
        if (_continuousScrollViewer == null || _continuousSlots == null || _continuousSlots.Count == 0)
            return true;
        var last = _continuousSlots[^1];
        return Math.Abs(_continuousScrollViewer.Extent.Height - (last.TopDip + last.DisplayHeight + PageGapDip)) < 2.0;
    }

    private void ApplyContinuousSlotLayout(IReadOnlyList<PdfPageSlot> slots)
    {
        double top = 0;
        foreach (var slot in slots)
        {
            slot.ApplyLayout(top, ZoomLevel);
            top += slot.DisplayHeight + PageGapDip;
        }
    }

    // ---- Scroll <-> CurrentPage sync -----------------------------------

    private void ScrollToPageContinuous(int pageNumber) => ScrollToPageContinuous(pageNumber, 0);

    /// <param name="intraPageFraction">Fraction of the page to place above the
    /// viewport top — 0 for plain navigation (outline, page number, search);
    /// the mode switch passes the carried reading position (#693).</param>
    private void ScrollToPageContinuous(int pageNumber, double intraPageFraction)
    {
        if (pageNumber < 1) return;
        _pendingContinuousFraction = intraPageFraction;

        // The slots may not exist yet: the document has loaded but the items panel
        // has not measured. Dropping the navigation here (the old early return) is
        // what silently swallowed "go to page N" issued right after open — the
        // caller's CurrentPage was then overwritten by the first scroll event.
        // Remember it and retry once the slots arrive.
        if (_continuousScrollViewer == null || _continuousSlots == null)
        {
            _pendingContinuousPage = pageNumber;
            _pendingContinuousAttempts = 0;
            Dispatcher.UIThread.Post(RetryPendingContinuousScroll, DispatcherPriority.Loaded);
            return;
        }

        if (pageNumber > _continuousSlots.Count) return;

        var slot = _continuousSlots[pageNumber - 1];
        var targetY = slot.TopDip + intraPageFraction * slot.DisplayHeight;
        var x = _continuousScrollViewer.Offset.X;
        _continuousScrollViewer.Offset = new Vector(x, targetY);

        // A ScrollViewer CLAMPS Offset to its extent. Before layout has run the
        // extent is 0, so the assignment above silently becomes Offset.Y = 0 —
        // and OnContinuousScrolled then computes "topmost visible page = 1" and
        // overwrites CurrentPage, swallowing the navigation entirely.
        //
        // That is not a theoretical race. Open a document and immediately click
        // an outline entry, type a page number, or jump to a search hit, and the
        // jump is lost with no feedback. It only became reachable when continuous
        // scroll became the default view mode.
        //
        // So: remember where we were actually trying to go. Until we get there,
        // OnContinuousScrolled must not overwrite CurrentPage with the stale
        // offset, and we retry once layout gives the viewer a real extent.
        if (!ReachedContinuousTarget(targetY))
        {
            _pendingContinuousPage = pageNumber;
            Dispatcher.UIThread.Post(RetryPendingContinuousScroll, DispatcherPriority.Loaded);
        }
        else
        {
            _pendingContinuousPage = null;
        }
    }

    /// <summary>
    /// Bounded so a document that never lays out cannot leave the pending page set
    /// forever — that would permanently disable the scroll -> CurrentPage sync and
    /// freeze the page number while the user scrolls.
    /// </summary>
    private const int MaxPendingContinuousScrollAttempts = 16;

    private void RetryPendingContinuousScroll()
    {
        if (_pendingContinuousPage is not { } page) return;

        if (++_pendingContinuousAttempts > MaxPendingContinuousScrollAttempts)
        {
            // Give up rather than spin. CurrentPage keeps the value the caller
            // asked for; only the scroll position failed to follow.
            _pendingContinuousPage = null;
            return;
        }

        // Slots still not built — the items panel hasn't measured yet. Wait.
        if (_continuousScrollViewer == null || _continuousSlots == null)
        {
            Dispatcher.UIThread.Post(RetryPendingContinuousScroll, DispatcherPriority.Loaded);
            return;
        }

        if (page < 1 || page > _continuousSlots.Count) { _pendingContinuousPage = null; return; }

        var slot = _continuousSlots[page - 1];
        var targetY = slot.TopDip + _pendingContinuousFraction * slot.DisplayHeight;
        if (ReachedContinuousTarget(targetY))
        {
            _pendingContinuousPage = null;
            return;
        }

        var before = _continuousScrollViewer.Offset.Y;
        _continuousScrollViewer.Offset = new Vector(_continuousScrollViewer.Offset.X, targetY);

        if (ReachedContinuousTarget(targetY))
        {
            _pendingContinuousPage = null;
        }
        else if (!_continuousScrollViewer.Offset.Y.Equals(before))
        {
            // We moved but haven't arrived — layout is still settling. Try again.
            Dispatcher.UIThread.Post(RetryPendingContinuousScroll, DispatcherPriority.Loaded);
        }
        else
        {
            // The offset didn't budge. Either the target is genuinely unreachable
            // (a short document whose last page sits above the max scroll) or the
            // extent is still zero. Give up rather than spin: CurrentPage stays
            // where the caller asked for it, which is the honest outcome.
            _pendingContinuousPage = null;
        }
    }

    private bool ReachedContinuousTarget(double targetY)
    {
        if (_continuousScrollViewer == null) return false;

        // No extent yet => layout has not run => the Offset assignment was clamped
        // to 0 and we have arrived NOWHERE.
        //
        // This check is the whole fix. Without it, "clamped to max" reads as
        // arrival, and with extent 0 the max is 0 — so EVERY target looks reached
        // at offset 0. The pending-navigation latch cleared itself immediately, and
        // the scroll handler was then free to derive CurrentPage from the stale
        // offset and snap the user back to page 1. The guard was disarming itself.
        var extentHeight = _continuousScrollViewer.Extent.Height;
        if (extentHeight <= 0) return false;

        // With a real extent, clamped-to-max DOES count as arrival: the last page's
        // top can legitimately exceed the maximum scroll offset, and demanding exact
        // equality there would spin forever.
        var offsetY = _continuousScrollViewer.Offset.Y;
        var maxY = Math.Max(0, extentHeight - _continuousScrollViewer.Viewport.Height);
        var effectiveTarget = Math.Min(targetY, maxY);

        return Math.Abs(offsetY - effectiveTarget) < 1.0;
    }

    private void OnContinuousScrolled()
    {
        if (ViewMode != PdfViewMode.Continuous || _continuousScrollViewer == null || _continuousSlots == null)
            return;

        // A programmatic jump is in flight and hasn't landed. The offset we would
        // read here is the STALE one, so deriving CurrentPage from it would undo
        // the navigation the user just asked for.
        _recomposeFromCacheOnLayout = true;
        if (_pendingContinuousPage is not null)
        {
            RenderVisibleContinuousTiles();
            return;
        }

        // Topmost visible page = the slot whose cumulative bottom passes the
        // current vertical offset (+ a small bias so a page counts as "current"
        // once its top edge is in view).
        //
        // ⚠️ #1650: this is deliberately NOT the most-visible page. CurrentPage
        // is the SCROLL ANCHOR — mode-switch reading-position carry, the
        // look-ahead window and the zoom re-layout all derive from it, and
        // making it most-visible broke two of them (a zoom at a fixed
        // offset/extent ratio legitimately changes which page dominates, so the
        // anchor stopped being stable). The page a "current page" COMMAND acts
        // on is a different question and is answered by MostVisiblePage.
        double offsetY = _continuousScrollViewer.Offset.Y + 1;
        int top = FindTopVisibleContinuousPage(_continuousSlots, offsetY);

        if (top != CurrentPage)
        {
            // Mark the change as scroll-driven so OnCurrentPageChanged doesn't
            // scroll back (feedback loop).
            _syncingPageFromScroll = true;
            try { CurrentPage = top; }
            finally { _syncingPageFromScroll = false; }
        }

        RenderVisibleContinuousTiles();
    }

    // ---- Container realization -> on-demand render ---------------------

    // #1564: set by a scroll, consumed by the layout pass that follows it.
    private bool _recomposeFromCacheOnLayout;

    /// <summary>
    /// Publish the composites a scroll made possible from cached tiles in the
    /// SAME frame as the scroll (#1564).
    /// </summary>
    /// <remarks>
    /// The render pass is posted at <see cref="DispatcherPriority.Render"/>
    /// from the scroll handler and from container realization. Both happen
    /// before or during the layout pass of the frame that shows the new
    /// offset, and a Render-priority job posted then runs after that frame is
    /// drawn. So even a page whose every tile was rendered ahead appeared one
    /// frame late: measured on the #1544 bench with render-ahead, the turned-to
    /// page drew exactly one 60 Hz frame (17 ms) after the first change on 151
    /// of 162 turns (turn drawn p50 59–61 ms against first change 42–44 ms).
    /// LayoutUpdated is raised at the end of that layout pass, after the
    /// turned-to page's container is realized and before the frame renders;
    /// the composite set here invalidates layout, which the same frame
    /// re-measures. This only composites what is already cached
    /// (RecomposeSlot keeps the previous composite when a cell is missing and
    /// skips a band it already shows), once per layout pass after a scroll —
    /// the work the posted pass would have done one frame later, which then
    /// finds nothing to do. Rendering stays with the posted pass.
    /// </remarks>
    private void OnContinuousItemsLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_recomposeFromCacheOnLayout)
            return;
        _recomposeFromCacheOnLayout = false;
        if (_continuousDetached || _continuousItems == null || ViewMode != PdfViewMode.Continuous
            || _pendingContinuousPage is not null)
            return;
        try
        {
            foreach (var container in _continuousItems.GetRealizedContainers())
            {
                if (container.DataContext is PdfPageSlot slot)
                    RecomposeSlot(slot);
            }
        }
        catch
        {
            // Layout callback: never throw into the layout manager.
        }
    }

    /// <summary>Test hook: run what the end of a layout pass runs after a scroll.</summary>
    internal bool RecomposeFromCacheOnLayoutPending => _recomposeFromCacheOnLayout;

    private void OnContinuousContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container.DataContext is PdfPageSlot)
            RenderVisibleContinuousTiles();
    }

    private void OnContinuousContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        // A page scrolled out of the realized window. The render pass drops the
        // composites of pages that are no longer realized (#1466); its tiles stay
        // in the byte-budgeted cache for a quick, re-render-free return when the
        // page scrolls back (#848 makes that a cache hit).
        //
        // The slot cannot be read from the container here. Avalonia 12 raises
        // ContainerClearing AFTER ClearContainerForItemOverride has cleared the
        // presenter's Content, which clears its DataContext too, so the old
        // `e.Container.DataContext is PdfPageSlot` test never matched and every
        // composite was kept: 37 pages, 940 MB, paging a 126-page document.
        RenderVisibleContinuousTiles();
    }

    private void RenderVisibleContinuousTiles()
    {
        if (_continuousDetached || _continuousItems == null || _continuousRenderPassScheduled)
            return;

        _continuousRenderPassScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _continuousRenderPassScheduled = false;
            RenderVisibleContinuousTilesNow();
        }, DispatcherPriority.Render);
    }

    private void RenderVisibleContinuousTilesNow()
    {
        // Runs from a Dispatcher.Post callback — an exception here (e.g. the
        // document being torn down mid-pass) is unhandled and destabilises the
        // dispatcher, so the whole pass is guarded.
        try { RenderVisibleContinuousTilesNowCore(); } catch { }
    }

    private void RenderVisibleContinuousTilesNowCore()
    {
        if (_continuousDetached || _continuousItems == null || _continuousScrollViewer == null) return;

        var viewport = _continuousScrollViewer.Viewport;
        var offset = _continuousScrollViewer.Offset;
        if (viewport.Width <= 0 || viewport.Height <= 0 || ZoomLevel <= 0) return;
        var doc = Document;
        if (doc == null) return;
        int dpi = ContinuousRenderDpi;

        // Pass 1: compute the required grid cells for every realized page and the
        // union of their keys, so a queued cell render can tell whether it is
        // still needed after it clears the concurrency gate.
        var perSlot = new List<(PdfPageSlot Slot, List<(GridCell Cell, ContinuousTileKey Key)> Cells)>();
        var required = new HashSet<ContinuousTileKey>();
        var realized = new HashSet<PdfPageSlot>(ReferenceEqualityComparer.Instance);

        foreach (var container in _continuousItems.GetRealizedContainers())
        {
            if (container.DataContext is not PdfPageSlot slot) continue;
            realized.Add(slot);
            if (slot.PageNumber < 1 || slot.PageNumber > doc.PageCount) continue;

            var cells = RequiredTileCells(
                slot.DisplayWidth, slot.DisplayHeight, slot.TopDip,
                offset, viewport, ContinuousTileQuantumDip, ContinuousTileOverscanDip);

            var keyed = new List<(GridCell, ContinuousTileKey)>(cells.Count);
            foreach (var cell in cells)
            {
                var key = CellKey(slot.PageNumber, dpi, slot.DisplayWidth, slot.DisplayHeight, cell);
                keyed.Add((cell, key));
                required.Add(key);
            }
            perSlot.Add((slot, keyed));
        }

        _continuousRequiredKeys = required;
        OnContinuousRequiredKeysChanged(required, offset, viewport, dpi);

        // #1466: only a realized page may hold a composite. Container recycling
        // cannot release it (see OnContinuousContainerClearing), so the pass
        // enforces it: jumping pages, which realizes one page at a time, kept
        // every composite it ever published. Runs before pass 2 publishes, so the
        // bound check after each publish counts realized pages only.
        if (_continuousSlots != null)
        {
            foreach (var slot in _continuousSlots)
            {
                if (slot.Bitmap != null && !realized.Contains(slot))
                    slot.ClearComposite();
            }
            RefreshContinuousByteMirrors();
        }

        // #1492: the same bound for decoded image samples. A page that is no
        // longer realized gives back the samples only it read.
        if (!_continuousImageSamples.IsEmpty)
        {
            var realizedPages = new HashSet<int>();
            foreach (var slot in realized)
                realizedPages.Add(slot.PageNumber);
            ReleaseImageSamplesOfUnrealizedPages(realizedPages);
        }

        // Pass 2: schedule renders for cells not yet cached, then (re)composite the
        // page from its cached cells. RecomposeSlot only swaps in a new band bitmap
        // once every covering cell is available, so the previous composite (which
        // covers the old band + overscan) stays on screen during a scroll until the
        // new one is ready — no blank strip (#848), and one bitmap means no seams.
        bool anyPending = false;
        foreach (var (slot, cells) in perSlot)
        {
            // #855: schedule the page's missing cells as ONE batch. A cell render
            // costs a WHOLE-page content-stream execution (RenderOptions.ClipRect
            // only shrinks the output bitmap and sets a canvas clip — every
            // operator still runs), so scheduling them individually made first
            // paint cost cells x full-page-render: 20 renders of the same page for
            // a 1280x900 window. See RenderContinuousCellsAsync.
            var pending = new List<(GridCell Cell, ContinuousTileKey Key)>();
            foreach (var (cell, key) in cells)
            {
                if (TryGetContinuousCached(key, out var c) && c != null) continue;
                if (_continuousInFlight.Contains(key))
                {
                    ContinuousRenderCoalescedRequestCount++;
                    continue;
                }
                pending.Add((cell, key));
            }
            if (pending.Count > 0)
            {
                anyPending = true;
                _ = RenderContinuousCellsAsync(slot, pending);
            }
            RecomposeSlot(slot);
        }

        // #1564: the bands are complete (or waiting only on renders already in
        // flight, whose completion re-asks). Render the next turn's page ahead.
        if (!anyPending)
            MaybeScheduleContinuousLookAhead();
    }

    /// <summary>
    /// Renders every missing grid cell of one page in a SINGLE render pass and
    /// slices the result into the per-cell cache (#855).
    ///
    /// WHY THIS IS NOT A LOOP OVER CELLS
    /// ---------------------------------
    /// <see cref="RenderOptions.ClipRect"/> makes a render's OUTPUT smaller; it
    /// does not make the render cheaper. <c>RenderPage</c> maps the clip to device
    /// bounds, allocates that bitmap and then executes the entire content stream
    /// against it. So a cell costs what the whole page costs. Under the #848 grid
    /// the first paint of a page needs its whole visible band — 20 cells for a
    /// 1280x900 window at 100% — which meant twenty full renders of the same page
    /// to produce one page. Measured on the ACC compensation report (page 1,
    /// 120 DPI, ~1.9s per full render): first paint took 35-50s, which is what
    /// made the #855 CI gate a coin flip against its 60s budget.
    ///
    /// Batching does not weaken the #848 guarantee: cells are still keyed, cached
    /// and composited exactly as before, so a cached cell is still always correct
    /// for its grid position. The slice offsets are the SAME cumulative-floor
    /// arithmetic <see cref="ComputeMosaic"/> uses to lay the cells back out, so
    /// the composite is a contiguous crop of one render rather than a mosaic of
    /// independently-clipped ones — if anything less seam-prone.
    /// </summary>
    /// <param name="lookAhead">
    /// Non-null for a render-ahead batch (#1564): its cells are not in the
    /// current bands by design, so they are not dropped as stale; it is
    /// cancelled through its own token instead, which the render observes
    /// between operators. It keeps its own counters and histogram, and its
    /// tiles are marked so trims and budget cuts drop them first.
    /// </param>
    private async Task RenderContinuousCellsAsync(
        PdfPageSlot slot, List<(GridCell Cell, ContinuousTileKey Key)> batch,
        ContinuousLookAheadBatch? lookAhead = null)
    {
        if (_continuousDetached || batch.Count == 0)
        {
            OnContinuousBandRenderFinished(lookAhead, current: false, batch);
            return;
        }
        // Fire-and-forget (`_ = RenderContinuousCellsAsync(...)`). An unobserved
        // exception here — e.g. the document being disposed mid-render during
        // teardown — must never surface: it would destabilise the whole
        // dispatcher (observed as cross-test dispatcher-pump timeouts / null
        // ItemsSource). Everything below the in-flight bookkeeping is guarded.
        var doc = Document;
        double zoom = ZoomLevel;
        if (doc == null || slot.PageNumber < 1 || slot.PageNumber > doc.PageCount || zoom <= 0)
        {
            OnContinuousBandRenderFinished(lookAhead, current: false, batch);
            return;
        }

        int pageNumber = slot.PageNumber;
        int dpi = batch[0].Key.Dpi;
        double pxPerDip = dpi / (96.0 * zoom);

        var claimed = new List<(GridCell Cell, ContinuousTileKey Key)>(batch.Count);
        Excise.Core.Document.PdfPage page;
        SKRect clip;
        double bandXDip, bandYDip;
        try
        {
            foreach (var entry in batch)
            {
                if (lookAhead != null)
                {
                    // The planner already skipped cached and in-flight cells on
                    // this dispatcher turn; peek so nothing is promoted.
                    if (PeekContinuousCached(entry.Key) != null || !_continuousInFlight.Add(entry.Key))
                        continue;
                    claimed.Add(entry);
                    continue;
                }
                if (TryGetContinuousCached(entry.Key, out var cached) && cached != null)
                {
                    ContinuousRenderCacheHitCount++;
                    continue;
                }
                if (!_continuousInFlight.Add(entry.Key))
                {
                    ContinuousRenderCoalescedRequestCount++;
                    continue;
                }
                claimed.Add(entry);
            }
            if (claimed.Count == 0)
            {
                if (lookAhead == null)
                    RecomposeSlot(slot);
                // Nothing left to claim: a render-ahead step moves on to its
                // next target.
                OnContinuousBandRenderFinished(lookAhead, current: lookAhead != null && !_continuousDetached, claimed);
                return;
            }

            // Bounding box of the claimed cells, in page-local DIPs. The cells of
            // one pass form a rectangular block, so this is normally exactly their
            // union; when earlier cells are already cached it can cover a little
            // more, which costs nothing — it is one render either way.
            bandXDip = double.MaxValue; bandYDip = double.MaxValue;
            double bandRight = 0, bandBottom = 0;
            foreach (var (cell, _) in claimed)
            {
                bandXDip = Math.Min(bandXDip, cell.XDip);
                bandYDip = Math.Min(bandYDip, cell.YDip);
                bandRight = Math.Max(bandRight, cell.XDip + cell.WidthDip);
                bandBottom = Math.Max(bandBottom, cell.YDip + cell.HeightDip);
            }

            page = doc.GetPage(pageNumber);
            int rotation = page.Rotation;
            var contentBox = page.EffectiveCropBox;
            // The band is mapped to a content-space clip by the same
            // (rotation-aware) helper a single cell uses — a batch of one is
            // byte-for-byte the render the per-cell path used to issue.
            var bandCell = new GridCell(
                claimed[0].Cell.Col, claimed[0].Cell.Row,
                bandXDip, bandYDip, bandRight - bandXDip, bandBottom - bandYDip);
            clip = CellToRequest(bandCell, zoom, rotation, contentBox).ClipRect;
        }
        catch
        {
            foreach (var (_, key) in claimed) _continuousInFlight.Remove(key);
            OnContinuousBandRenderFinished(lookAhead, current: false, claimed);
            return;
        }

        var documentToken = _continuousDocCts.Token;
        var token = lookAhead?.Token ?? documentToken;
        // #1492: every image and mask stream this band's render reads. Filled
        // by the renderer when RenderPage returns (or throws), on the render
        // thread, before the awaited task completes; read only after that.
        var imageSamples = new List<Excise.Core.Primitives.PdfStream>();

        // Capture the gate: ContinuousRenderConcurrency may replace the field
        // while this render waits or runs, and the slot must be released on the
        // semaphore it was taken from.
        var renderGate = _continuousRenderGate;
        try
        {
            await renderGate.WaitAsync(token);
            try
            {
                // Scrolled past while this batch waited for the gate — drop the
                // cells that are no longer required. A batch whose cells are ALL
                // stale is dropped without rendering (the cheap skip that keeps
                // rapid scrolling from rendering every intermediate viewport).
                if (token.IsCancellationRequested)
                {
                    if (lookAhead != null) ContinuousLookAheadCancellationCount++;
                    else ContinuousRenderCancellationCount += claimed.Count;
                    return;
                }
                if (lookAhead == null)
                {
                    // A visible batch's cells are stale once no band needs them.
                    // (A render-ahead batch is exempt: its cells are never in
                    // the bands; OnContinuousRequiredKeysChanged cancels it.)
                    int stale = claimed.RemoveAll(e => !_continuousRequiredKeys.Contains(e.Key));
                    ContinuousRenderCancellationCount += stale;
                    if (claimed.Count == 0) return;
                    ContinuousRenderStartCount++;
                }
                else
                {
                    ContinuousLookAheadStartCount++;
                }
                var renderWatch = System.Diagnostics.Stopwatch.StartNew();
                // Read the styled property HERE, on the UI thread. Avalonia
                // properties are not thread-affine-safe to read from the render
                // task, and capturing it also pins the value for this pass.
                var showAnnotations = ShowAnnotations;
                var showComments = ShowCommentAnnotations;
                var showFields = ShowFieldAndLinkAnnotations;
                var revealHidden = RevealHiddenAnnotations;
                var highlightFields = HighlightFormFields;
                var renderStarting = ContinuousBandRenderStartingForTests;
                var skBitmap = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    renderStarting?.Invoke(pageNumber);
                    // A fresh renderer per pass: SkiaRenderer carries per-render
                    // instance state and is not reentrant, and several pages'
                    // bands may render concurrently.
                    var renderer = new SkiaRenderer();
                    // Only render-ahead passes its token: it is the render a
                    // page turn elsewhere should abandon mid-page. The visible
                    // path keeps its historical call unchanged.
                    var renderToken = lookAhead != null ? token : CancellationToken.None;
                    return renderer.RenderPage(page, new RenderOptions
                    {
                        Dpi = dpi,
                        ClipRect = clip,
                        RenderAnnotations = showAnnotations,
                        ShowCommentAnnotations = showComments,
                        ShowFieldAndLinkAnnotations = showFields,
                        RevealHiddenAnnotations = revealHidden,
                        HighlightFormFields = highlightFields,
                        ImageSampleStreamSink = imageSamples,
                    }, renderToken);
                }, token);
                renderWatch.Stop();
                if (lookAhead != null)
                {
                    ViewerMetrics.RecordLookAheadRender(renderWatch.Elapsed, dpi, ViewerMetrics.LookAheadContinuous);
                    ContinuousLookAheadCompletedCount++;
                }
                else
                {
                    ViewerMetrics.RecordBandRender(renderWatch.Elapsed, dpi);
                    ContinuousRenderCompletedCount++;
                    ContinuousRenderWallMs += renderWatch.ElapsedMilliseconds;
                }

                try
                {
                    if (token.IsCancellationRequested) return;
                    int cached = SliceBandIntoCells(skBitmap, claimed, bandXDip, bandYDip, pxPerDip,
                        lookAhead != null);
                    if (cached > 0)
                    {
                        Trace($"BandRendered page={pageNumber} cells={cached} " +
                              $"band={bandXDip:F0},{bandYDip:F0} bmpPx={skBitmap?.Width}x{skBitmap?.Height} " +
                              $"dpi={dpi} zoom={zoom:F3} ms={renderWatch.ElapsedMilliseconds}" +
                              (lookAhead != null ? " lookAhead=1" : ""));
                        // A render-ahead page is normally not realized; only a
                        // realized one (zoomed out, or paged onto mid-render)
                        // has a composite to rebuild.
                        if (lookAhead == null || _continuousItems?.ContainerFromItem(slot) != null)
                            RecomposeSlot(slot);
                    }
                }
                finally
                {
                    skBitmap?.Dispose();
                }
            }
            finally
            {
                renderGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Scrolled away / document changed before the render finished.
            if (lookAhead != null) ContinuousLookAheadCancellationCount++;
        }
        catch
        {
            // A single bad band must not break the reading scroll.
        }
        finally
        {
            // #1492, in this order. Record what the render read (a cancelled
            // render may belong to a document the viewer no longer shows), THEN
            // end the in-flight claim, THEN re-check: a page unrealized while
            // this render was running was kept by the claim, and nothing else
            // would release the samples the render just pinned.
            bool current = !token.IsCancellationRequested && !_continuousDetached;
            if (current)
                RecordContinuousImageSamples(doc, pageNumber, imageSamples);
            foreach (var (_, key) in claimed) _continuousInFlight.Remove(key);
            if (current)
            {
                try { ReleaseImageSamplesOfUnrealizedPages(); } catch { }
            }
            // #1564: continue render-ahead (or start it, once the last visible
            // band has landed). A cancelled look-ahead still reports in, so a
            // visible pass that coalesced onto it gets its cells back.
            bool live = !documentToken.IsCancellationRequested && !_continuousDetached;
            try { OnContinuousBandRenderFinished(lookAhead, live, claimed); } catch { }
        }
    }

    /// <summary>
    /// Cuts the band bitmap into its constituent grid cells and caches each under
    /// its own key. Offsets are cumulative sums of the FLOORED per-cell pixel
    /// sizes — identical to <see cref="ComputeMosaic"/>'s layout — so slicing and
    /// re-compositing round-trips the band exactly. Returns how many cells were
    /// cached; 0 means the band did not match the expected geometry and the caller
    /// should leave the cells to be rendered individually next pass.
    /// </summary>
    private int SliceBandIntoCells(
        SKBitmap? band,
        List<(GridCell Cell, ContinuousTileKey Key)> cells,
        double bandXDip, double bandYDip, double pxPerDip, bool lookAhead = false)
    {
        if (band == null || band.Width <= 0 || band.Height <= 0) return 0;

        // Column/row pixel offsets within the band, from the same floored widths
        // the compositor lays cells out with.
        var colX = new SortedDictionary<int, int>();
        var rowY = new SortedDictionary<int, int>();
        var colW = new SortedDictionary<int, int>();
        var rowH = new SortedDictionary<int, int>();
        foreach (var (cell, _) in cells)
        {
            colW[cell.Col] = ContinuousCellPixelExtent(cell.WidthDip, pxPerDip);
            rowH[cell.Row] = ContinuousCellPixelExtent(cell.HeightDip, pxPerDip);
        }
        int ax = 0;
        foreach (var kv in colW) { colX[kv.Key] = ax; ax += kv.Value; }
        int ay = 0;
        foreach (var kv in rowH) { rowY[kv.Key] = ay; ay += kv.Value; }

        // The band render is ceil(bandDip * pxPerDip) px; the floored cell sums are
        // at most one pixel per row/column short of that. Anything further apart
        // means the geometry assumption does not hold (an unexpected clamp inside
        // the renderer) — refuse to slice rather than cache misaligned tiles.
        if (ax > band.Width || ay > band.Height ||
            band.Width - ax > colW.Count + 1 || band.Height - ay > rowH.Count + 1)
            return 0;

        int cachedCount = 0;
        foreach (var (cell, key) in cells)
        {
            int x = colX[cell.Col], y = rowY[cell.Row];
            int w = colW[cell.Col], h = rowH[cell.Row];
            if (x + w > band.Width) w = band.Width - x;
            if (y + h > band.Height) h = band.Height - y;
            if (w <= 0 || h <= 0) continue;

            using var sub = new SKBitmap();
            if (!band.ExtractSubset(sub, new SKRectI(x, y, x + w, y + h))) continue;
            var bitmap = Imaging.SkiaInterop.ToAvaloniaBitmap(sub);
            if (bitmap == null) continue;
            if (AddToContinuousCache(key, bitmap, lookAhead))
                cachedCount++;
        }

        return cachedCount;
    }

    /// <summary>
    /// The page a command that says "current page" must act on (#1650): in
    /// continuous mode the page with the greatest visible area in the viewport,
    /// otherwise the displayed page.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="CurrentPage"/> on purpose — that one is the
    /// scroll anchor and has to stay the page owning the top edge (see the note
    /// at the scroll handler). This is what the reader would point at and say
    /// "this page", which is what Remove Current Page, Extract, Export, Move
    /// and Rotate must use.
    /// </remarks>
    public int MostVisiblePage
    {
        get
        {
            if (ViewMode != PdfViewMode.Continuous || _continuousScrollViewer == null || _continuousSlots.Count == 0)
                return CurrentPage;

            // ⚠️ A navigation still in flight must NOT be overridden. Setting
            // CurrentPage (Go To Page, a clicked link, a thumbnail) scrolls
            // asynchronously, so for a moment the anchor names the destination
            // while the offset still describes where the reader came from.
            // Answering from that offset would make a command act on the page
            // the user just navigated AWAY from — caught by
            // PageOrganizationCommandTests, which set a page and act at once.
            //
            // _pendingContinuousPage is that signal, and it is sufficient: an
            // externally set CurrentPage reaches ScrollToPageContinuous through
            // OnCurrentPageChanged, which sets it. A second guard ("the anchor
            // page has no pixels on screen") was written and MEASURED INERT —
            // removing it left PageOrganizationCommandTests and
            // CurrentPageCommandTargetTests green — so it is not here.
            if (_pendingContinuousPage is not null)
                return CurrentPage;

            return FindMostVisibleContinuousPage(
                _continuousSlots,
                _continuousScrollViewer.Offset.Y + 1,
                _continuousScrollViewer.Viewport.Height);
        }
    }

    /// <summary>
    /// The page a command that says "current page" must act on (#1650): the one
    /// with the greatest visible height in the viewport.
    /// </summary>
    /// <remarks>
    /// <para>Not <see cref="FindTopVisibleContinuousPage"/>, which answers a
    /// different question — "which page owns the top edge of the viewport" —
    /// and is right for the hit-test and the look-ahead window that use it. It
    /// was wrong here: it returns the first page with ANY pixel on screen, so a
    /// two-pixel sliver of the previous page outvotes the page filling the rest
    /// of the window. Remove Current Page then deletes the page the reader is
    /// not looking at, which is how this was found.</para>
    /// <para>Ties go to the upper page, which is what every reader does — and
    /// what keeps the answer stable while scrolling through equal-height pages
    /// rather than flickering between two.</para>
    /// <para>A zero or negative viewport (a window mid-layout, a measure pass
    /// before the scroll viewer has a size) falls back to the top-visible page:
    /// with no viewport there is no "most visible", and answering the old way
    /// is better than answering 1.</para>
    /// </remarks>
    internal static int FindMostVisibleContinuousPage(
        IReadOnlyList<PdfPageSlot> slots, double offsetY, double viewportHeight)
    {
        if (slots.Count == 0)
            return 1;
        if (viewportHeight <= 0)
            return FindTopVisibleContinuousPage(slots, offsetY);

        var viewTop = offsetY;
        var viewBottom = offsetY + viewportHeight;

        // Start at the first page touching the viewport and walk forward only
        // while pages still intersect it — the slot list can be thousands long
        // and this runs on every scroll event.
        var first = FindTopVisibleContinuousPage(slots, offsetY) - 1;
        var best = first;
        var bestVisible = double.NegativeInfinity;

        for (var i = first; i < slots.Count; i++)
        {
            var top = slots[i].TopDip;
            if (top >= viewBottom)
                break;

            var bottom = top + slots[i].DisplayHeight;
            var visible = Math.Min(bottom, viewBottom) - Math.Max(top, viewTop);
            // Strictly greater: a tie keeps the earlier (upper) page.
            if (visible > bestVisible)
            {
                bestVisible = visible;
                best = i;
            }
        }

        return best + 1;
    }

    internal static int FindTopVisibleContinuousPage(IReadOnlyList<PdfPageSlot> slots, double offsetY)
    {
        if (slots.Count == 0)
            return 1;

        int low = 0;
        int high = slots.Count - 1;
        int result = slots.Count - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            var bottom = slots[mid].TopDip + slots[mid].DisplayHeight + PageGapDip;
            if (offsetY < bottom)
            {
                result = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        return result + 1;
    }

    /// <summary>
    /// Map a point in ContinuousItems coordinates (post-zoom dips, scroll
    /// already accounted for because the ItemsControl scrolls as content) to
    /// the page slot under it and the point's page-local dips (#667). Pure —
    /// unit-tested in Excise.Avalonia.Tests without a window. Uses the same
    /// TopDip/DisplayWidth/DisplayHeight layout math the tile renderer uses:
    /// each slot's Border sits at TopDip and is horizontally centered within
    /// the items width. Points in the inter-page gap or in the letterbox
    /// margins beside a centered page map to nothing.
    /// </summary>
    internal static bool TryMapContinuousPointToPage(
        IReadOnlyList<PdfPageSlot> slots,
        double itemsWidthDip,
        Point itemsPointDip,
        out int pageNumber,
        out Point pagePointDip)
    {
        pageNumber = 0;
        pagePointDip = default;
        if (slots.Count == 0) return false;

        // Candidate = the slot whose bottom edge (incl. trailing gap) is the
        // first to pass the point's Y; containment below rejects gap hits.
        var candidate = FindTopVisibleContinuousPage(slots, itemsPointDip.Y);
        var slot = slots[candidate - 1];

        double yInPage = itemsPointDip.Y - slot.TopDip;
        if (yInPage < 0 || yInPage > slot.DisplayHeight) return false;

        double xOffset = Math.Max(0, (itemsWidthDip - slot.DisplayWidth) / 2);
        double xInPage = itemsPointDip.X - xOffset;
        if (xInPage < 0 || xInPage > slot.DisplayWidth) return false;

        pageNumber = slot.PageNumber;
        pagePointDip = new Point(xInPage, yInPage);
        return true;
    }

    /// <summary>
    /// Rebuild a page's single displayed bitmap by compositing its cached grid
    /// cells into one buffer (#848). The band is the required cells' bounding box.
    /// If any covering cell is not cached yet, the current composite is kept (it
    /// still covers the previous band + overscan) so nothing blanks; the pending
    /// renders trigger another recompose when they land. Emitting ONE bitmap is
    /// what eliminates the inter-tile seams that many separate tile Images had —
    /// the cells are blitted edge-to-edge at integer pixel offsets into one buffer
    /// that is then displayed (and downscaled) as a single Image.
    /// </summary>
    private void RecomposeSlot(PdfPageSlot slot)
    {
        // Called from fire-and-forget cell renders too; must never throw (a
        // disposed document during teardown would otherwise surface unobserved).
        try { RecomposeSlotCore(slot); } catch { }
    }

    private void RecomposeSlotCore(PdfPageSlot slot)
    {
        if (_continuousDetached || _continuousScrollViewer == null || _continuousDocCts.IsCancellationRequested) return;
        var doc = Document;
        if (doc == null || slot.PageNumber < 1 || slot.PageNumber > doc.PageCount) return;

        // #1466: only a realized page may hold a composite. A cell render can
        // finish after its page's container was recycled (a page jump realizes
        // the destination only); publishing then would leave a composite that
        // nothing releases until the next render pass.
        if (_continuousItems?.ContainerFromItem(slot) == null)
        {
            slot.ClearComposite();
            RefreshContinuousByteMirrors();
            // #1492: and its decoded image samples, unless a render of it is
            // still in flight (the render's finally re-checks once it lands).
            ReleaseImageSamplesOfUnrealizedPages();
            return;
        }

        var viewport = _continuousScrollViewer.Viewport;
        var offset = _continuousScrollViewer.Offset;
        if (viewport.Width <= 0 || viewport.Height <= 0 || ZoomLevel <= 0) return;
        int dpi = ContinuousRenderDpi;

        var cells = RequiredTileCells(slot.DisplayWidth, slot.DisplayHeight, slot.TopDip,
            offset, viewport, ContinuousTileQuantumDip, ContinuousTileOverscanDip);
        if (cells.Count == 0) return; // page not visible — keep the last composite

        // Gather cached bitmaps for every required cell; bail (keep current
        // composite) if any is missing. Each cell is laid out and blitted by its
        // CONTENT pixel size (floored), NOT its bitmap's ceil'd size: a cell's
        // bitmap is ceil(content) px, so its last row/column is the empty
        // sub-pixel ceil overhang — tiling by the ceil'd size would leave those
        // empty edges between cells as seams. Flooring to content makes cells abut
        // at true content boundaries (a <1px content shift per cell, invisible in
        // one downscaled bitmap).
        double pxPerDip = dpi / (96.0 * ZoomLevel);
        var parts = new (GridCell Cell, WriteableBitmap Bmp, int PxW, int PxH)[cells.Count];
        int minCol = int.MaxValue, minRow = int.MaxValue;
        double bandX = double.MaxValue, bandY = double.MaxValue, bandRight = 0, bandBottom = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            var key = CellKey(slot.PageNumber, dpi, slot.DisplayWidth, slot.DisplayHeight, cell);
            var bmp = PeekContinuousCached(key);
            if (bmp == null) return; // incomplete — leave the previous composite up
            int pxW = Math.Min(bmp.PixelSize.Width, ContinuousCellPixelExtent(cell.WidthDip, pxPerDip));
            int pxH = Math.Min(bmp.PixelSize.Height, ContinuousCellPixelExtent(cell.HeightDip, pxPerDip));
            parts[i] = (cell, bmp, pxW, pxH);
            minCol = Math.Min(minCol, cell.Col);
            minRow = Math.Min(minRow, cell.Row);
            bandX = Math.Min(bandX, cell.XDip);
            bandY = Math.Min(bandY, cell.YDip);
            bandRight = Math.Max(bandRight, cell.XDip + cell.WidthDip);
            bandBottom = Math.Max(bandBottom, cell.YDip + cell.HeightDip);
        }
        double bandW = bandRight - bandX, bandH = bandBottom - bandY;

        // Skip if this exact band (origin + extent + zoom/dpi) is already composited.
        var compositeKey = new ContinuousTileKey(slot.PageNumber, dpi,
            (int)Math.Round(slot.DisplayWidth), (int)Math.Round(slot.DisplayHeight), minCol, minRow);
        if (slot.Bitmap != null && slot.CompositeKey.Equals(compositeKey)
            && Math.Abs(slot.TileDisplayWidth - bandW) < 0.5
            && Math.Abs(slot.TileDisplayHeight - bandH) < 0.5)
            return;

        var (totalW, totalH, offsets) = ComputeMosaic(
            System.Linq.Enumerable.Select(parts, p => (p.Cell.Col, p.Cell.Row, p.PxW, p.PxH)));
        if (totalW <= 0 || totalH <= 0) return;

        var composite = new WriteableBitmap(new PixelSize(totalW, totalH),
            new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var dst = composite.Lock())
        {
            foreach (var (cell, bmp, pxW, pxH) in parts)
            {
                var (x, y) = offsets[(cell.Col, cell.Row)];
                BlitCell(dst, bmp, x, y, pxW, pxH);
            }
        }

        slot.SetComposite(composite, compositeKey, bandX, bandY, bandW, bandH);
        ViewerMetrics.RecordComposite(ContinuousTileByteSize(totalW, totalH), dpi);
        RefreshContinuousByteMirrors();

        // #1466 bound check. Runs per composite on the UI thread, so the common
        // path is one alloc-free walk of the slots; the band's upper bound
        // (ContinuousCompositeByteSize builds a mosaic) and the strings are only
        // computed when tracing is on or the bound is exceeded.
        long residentBytes = ContinuousCompositeResidentBytes();
        long bound = EffectiveContinuousCompositeByteBound;
        bool overBound = residentBytes > bound;
        if (overBound)
            ContinuousCompositeOverBoundCount++;
        if (!TraceEnabled && !overBound)
            return;

        long bandUpperBoundBytes = ContinuousCompositeByteSize(cells, pxPerDip);
        Trace($"Composite page={slot.PageNumber} band={bandX:F0},{bandY:F0} {bandW:F0}x{bandH:F0} px={totalW}x{totalH} cells={parts.Length} dpi={dpi} zoom={ZoomLevel:F3} bandUpperBoundBytes={bandUpperBoundBytes}");
        if (overBound)
        {
            int composites = 0;
            if (_continuousSlots != null)
                foreach (var s in _continuousSlots)
                    if (s.Bitmap != null) composites++;
            Trace($"WARNING composite bytes over bound (#{ContinuousCompositeOverBoundCount}): residentBytes={residentBytes} " +
                  $"bound={bound} zoom={ZoomLevel:F3} dpi={dpi} pagesWithComposite={composites} " +
                  $"page={slot.PageNumber} bandUpperBoundBytes={bandUpperBoundBytes} (#1466)");
        }
    }

    /// <summary>
    /// Lay out a set of grid cells (given each cell's pixel size) into one mosaic:
    /// columns are placed left-to-right by ascending Col, rows top-to-bottom by
    /// ascending Row, each at the cumulative sum of prior column widths / row
    /// heights. Pure — no rendering — so the "cells tile with no gap and no
    /// overlap" invariant is unit-tested (ContinuousTileGridTests). All cells in a
    /// column share a width and in a row share a height (same cell dip size at the
    /// same dpi), so the result is a clean rectangular tiling.
    /// </summary>
    internal static (int TotalW, int TotalH, Dictionary<(int Col, int Row), (int X, int Y)> Offsets)
        ComputeMosaic(IEnumerable<(int Col, int Row, int PxW, int PxH)> cells)
    {
        var colW = new SortedDictionary<int, int>();
        var rowH = new SortedDictionary<int, int>();
        var list = new List<(int Col, int Row)>();
        foreach (var c in cells)
        {
            colW[c.Col] = c.PxW;
            rowH[c.Row] = c.PxH;
            list.Add((c.Col, c.Row));
        }

        var xOff = new Dictionary<int, int>();
        int ax = 0;
        foreach (var kv in colW) { xOff[kv.Key] = ax; ax += kv.Value; }
        var yOff = new Dictionary<int, int>();
        int ay = 0;
        foreach (var kv in rowH) { yOff[kv.Key] = ay; ay += kv.Value; }

        var offsets = new Dictionary<(int, int), (int, int)>();
        foreach (var (col, row) in list) offsets[(col, row)] = (xOff[col], yOff[row]);
        return (ax, ay, offsets);
    }

    private WriteableBitmap? PeekContinuousCached(ContinuousTileKey key)
    {
        for (var node = _continuousCache.First; node != null; node = node.Next)
            if (node.Value.Key.Equals(key)) return node.Value.Bitmap;
        return null;
    }

    // Copy the top-left copyW x copyH pixels of one cell into the composite buffer
    // at an integer pixel offset (copyW/copyH = the cell's CONTENT size, dropping
    // the empty ceil-overhang edge). Bgra8888, 4 bytes/px, row by row.
    // Bounds-clamped defensively though the mosaic offsets are exact by construction.
    private static unsafe void BlitCell(global::Avalonia.Platform.ILockedFramebuffer dst,
        WriteableBitmap src, int xPx, int yPx, int copyW, int copyH)
    {
        using var s = src.Lock();
        const int bpp = 4;
        int dstW = dst.Size.Width, dstH = dst.Size.Height;
        copyW = Math.Min(copyW, Math.Min(src.PixelSize.Width, Math.Max(0, dstW - xPx)));
        copyH = Math.Min(copyH, src.PixelSize.Height);
        int copyBytes = copyW * bpp;
        if (copyBytes <= 0) return;
        byte* dstBase = (byte*)dst.Address;
        byte* srcBase = (byte*)s.Address;
        for (int row = 0; row < copyH; row++)
        {
            int dy = yPx + row;
            if (dy < 0 || dy >= dstH) continue;
            byte* d = dstBase + (long)dy * dst.RowBytes + (long)xPx * bpp;
            byte* sp = srcBase + (long)row * s.RowBytes;
            System.Buffer.MemoryCopy(sp, d, copyBytes, copyBytes);
        }
    }

    private bool TryGetContinuousCached(ContinuousTileKey key, out WriteableBitmap? bmp)
    {
        for (var node = _continuousCache.First; node != null; node = node.Next)
        {
            if (node.Value.Key.Equals(key))
            {
                _continuousCache.Remove(node);
                _continuousCache.AddFirst(node);
                _continuousLookAheadTiles.Remove(key);
                bmp = node.Value.Bitmap;
                return true;
            }
        }
        bmp = null;
        return false;
    }

    /// <summary>
    /// Transfers ownership of <paramref name="bmp"/> to the tile cache. A tile
    /// replaced under the same key, and every tile evicted to get back under the
    /// byte budget, is disposed (#1467). Internal for tests.
    /// </summary>
    /// <param name="lookAhead">
    /// A render-ahead tile (#1564). It goes in at the MRU end like any other —
    /// the next page is worth more than a page scrolled past long ago — but its
    /// eviction never takes a tile of the current bands: when the budget
    /// cannot hold it without doing so, the look-ahead tile itself is dropped.
    /// </param>
    /// <returns>False when the tile was not kept (a refused look-ahead tile).</returns>
    internal bool AddToContinuousCache(ContinuousTileKey key, WriteableBitmap bmp, bool lookAhead = false)
    {
        if (lookAhead)
            return AddLookAheadTileToContinuousCache(key, bmp);
        _continuousLookAheadTiles.Remove(key);
        for (var node = _continuousCache.First; node != null; node = node.Next)
        {
            if (!node.Value.Key.Equals(key)) continue;
            var replaced = node.Value.Bitmap;
            _continuousCache.Remove(node);
            // Re-adding the very same instance must not dispose the bitmap
            // being inserted.
            if (!ReferenceEquals(replaced, bmp)) replaced.Dispose();
            break;
        }
        _continuousCache.AddFirst((key, bmp));
        // Evict the LRU tail until back under the byte budget — see the
        // ContinuousCacheByteBudget field comment for how that number was
        // measured (#615). Unlink BEFORE disposing: ContinuousCacheResidentBytes
        // reads every remaining entry's PixelSize, which throws on a disposed
        // bitmap. See the _continuousCache field comment for why disposing a
        // tile here is safe.
        long budget = ContinuousCacheBudgetNow();
        while (_continuousCache.Count > ContinuousCacheMinEntries &&
               ContinuousCacheResidentBytes() > budget)
        {
            var (evictedKey, evicted) = _continuousCache.Last!.Value;
            _continuousCache.RemoveLast();
            _continuousLookAheadTiles.Remove(evictedKey);
            evicted.Dispose();
        }
        RefreshContinuousByteMirrors();
        return true;
    }

    private bool AddLookAheadTileToContinuousCache(ContinuousTileKey key, WriteableBitmap bmp)
    {
        // A look-ahead render never replaces a cached tile: the planner and the
        // claim both skipped cached keys, and a key cached since then by a
        // visible render is the one to keep.
        if (PeekContinuousCached(key) != null)
        {
            bmp.Dispose();
            return false;
        }

        _continuousCache.AddFirst((key, bmp));
        _continuousLookAheadTiles.Add(key);
        long budget = ContinuousCacheBudgetNow();
        long resident = ContinuousCacheResidentBytes();
        var node = _continuousCache.Last;
        while (node != null && resident > budget && _continuousCache.Count > ContinuousCacheMinEntries)
        {
            var previous = node.Previous;
            if (node != _continuousCache.First && !_continuousRequiredKeys.Contains(node.Value.Key))
            {
                var bitmap = node.Value.Bitmap;
                resident -= ContinuousTileByteSize(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                _continuousCache.Remove(node);
                _continuousLookAheadTiles.Remove(node.Value.Key);
                bitmap.Dispose();
            }
            node = previous;
        }

        bool kept = true;
        if (resident > budget && _continuousCache.Count > ContinuousCacheMinEntries)
        {
            // Only the current bands (and this tile) are left, and they do not
            // fit: the bands win.
            _continuousCache.RemoveFirst();
            _continuousLookAheadTiles.Remove(key);
            bmp.Dispose();
            kept = false;
        }
        RefreshContinuousByteMirrors();
        return kept;
    }

    private long ContinuousCacheResidentBytes()
    {
        long total = 0;
        foreach (var entry in _continuousCache)
            total += ContinuousTileByteSize(entry.Bitmap.PixelSize.Width, entry.Bitmap.PixelSize.Height);
        return total;
    }

    /// <summary>
    /// Resident byte cost of one cached tile: Bgra8888 is always 4 bytes/pixel —
    /// see <see cref="Imaging.SkiaInterop.ToAvaloniaBitmap"/>, which forces that
    /// format for anything Skia hands back. Pure and internal so it can be
    /// exercised directly from tests (#615) without needing a real
    /// <see cref="WriteableBitmap"/>, which requires a platform render backend.
    /// </summary>
    internal static long ContinuousTileByteSize(int pixelWidth, int pixelHeight) =>
        (long)pixelWidth * pixelHeight * 4;

    /// <summary>
    /// A cell's CONTENT extent in pixels (floored, at least 1) — the size the
    /// band is sliced into and a composite is laid out with. A cell's bitmap is
    /// the ceiling of this; see RecomposeSlotCore for why layout uses the floor.
    /// </summary>
    internal static int ContinuousCellPixelExtent(double dip, double pxPerDip) =>
        Math.Max(1, (int)Math.Floor(dip * pxPerDip));

    /// <summary>
    /// Resident bytes of the composite RecomposeSlotCore builds for
    /// <paramref name="cells"/> (#1466): the <see cref="ComputeMosaic"/> of their
    /// content extents, 4 bytes/pixel. RecomposeSlotCore additionally caps each
    /// extent at its cached bitmap's size, so this is an upper bound on what it
    /// allocates. Pure, for ContinuousCacheMemoryTests.
    /// </summary>
    internal static long ContinuousCompositeByteSize(IEnumerable<GridCell> cells, double pxPerDip)
    {
        var (totalW, totalH, _) = ComputeMosaic(System.Linq.Enumerable.Select(cells, c =>
            (c.Col, c.Row, ContinuousCellPixelExtent(c.WidthDip, pxPerDip), ContinuousCellPixelExtent(c.HeightDip, pxPerDip))));
        return ContinuousTileByteSize(totalW, totalH);
    }

    /// <summary>
    /// Resident bytes of the page composites the slots currently show (#1466) —
    /// the second half of the continuous view's bitmap memory, next to
    /// <see cref="ContinuousCacheResidentBytes"/> for tiles. Composites already
    /// replaced and awaiting release are not counted. Internal for tests.
    /// </summary>
    internal long ContinuousCompositeResidentBytes()
    {
        if (_continuousSlots == null) return 0;
        long total = 0;
        foreach (var slot in _continuousSlots)
        {
            if (slot.Bitmap is { } composite)
                total += ContinuousTileByteSize(composite.PixelSize.Width, composite.PixelSize.Height);
        }
        return total;
    }

    // Stable, content-addressed grid-cell key (#848). Two cells collide iff they
    // show the same content at the same pixel density: same page, same render DPI,
    // same page DIP dimensions (which encode zoom — see CellKey), same grid cell.
    internal readonly record struct ContinuousTileKey(
        int Page, int Dpi, int PageWidthDip, int PageHeightDip, int Col, int Row);

    internal readonly record struct ContinuousTileRequest(
        SKRect ClipRect,
        int XDip,
        int YDip,
        int WidthDip,
        int HeightDip);
}

/// <summary>
/// One rendered grid cell of a continuous-view page (#848), in page-local DIPs
/// (the Border's own coordinate space). Immutable: a tile is created only once
/// its bitmap is ready, and its grid position never changes — the whole point of
/// the content-addressed grid is that a cell painted at its position is always
/// correct for that position. Bound one-per-Image by the DataTemplate.
/// </summary>
/// <summary>
/// One page in the continuous (reading) view. Observable so the data-template's
/// Border size and single displayed <see cref="Bitmap"/> update as zoom changes
/// and the page renders. The grid of tiles is rendered and cached per cell
/// (bounded memory, never-stale coverage — #848), but they are COMPOSITED into
/// this one bitmap for display, so there is exactly one Image per page and hence
/// no inter-tile seams.
/// </summary>
public sealed class PdfPageSlot : INotifyPropertyChanged
{
    private double _displayWidth;
    private double _displayHeight;
    private double _topDip;
    private double _tileDisplayX;
    private double _tileDisplayY;
    private double _tileDisplayWidth;
    private double _tileDisplayHeight;
    private WriteableBitmap? _bitmap;

    internal PdfPageSlot(int pageNumber, double widthPt, double heightPt, double zoom)
    {
        PageNumber = pageNumber;
        WidthPt = widthPt;
        HeightPt = heightPt;
        ApplyZoom(zoom);
    }

    public int PageNumber { get; }
    public double WidthPt { get; }
    public double HeightPt { get; }

    /// <summary>
    /// Text-selection highlight rectangles for this page, in page-local DIPs
    /// (the Border's own coordinate space), bound by the continuous-view
    /// DataTemplate to a Canvas overlay (#815). Populated by the continuous
    /// selection gesture; empty when nothing on this page is selected.
    /// </summary>
    internal System.Collections.ObjectModel.ObservableCollection<PdfSelectionHighlight> SelectionRects { get; } = new();

    internal double TopDip { get => _topDip; private set => Set(ref _topDip, value); }
    public double DisplayWidth { get => _displayWidth; private set => Set(ref _displayWidth, value); }
    public double DisplayHeight { get => _displayHeight; private set => Set(ref _displayHeight, value); }

    /// <summary>The composited band bitmap and where it sits in page-local DIPs.</summary>
    public WriteableBitmap? Bitmap { get => _bitmap; private set => Set(ref _bitmap, value); }
    public double TileDisplayX { get => _tileDisplayX; private set => Set(ref _tileDisplayX, value); }
    public double TileDisplayY { get => _tileDisplayY; private set => Set(ref _tileDisplayY, value); }
    public double TileDisplayWidth { get => _tileDisplayWidth; private set => Set(ref _tileDisplayWidth, value); }
    public double TileDisplayHeight { get => _tileDisplayHeight; private set => Set(ref _tileDisplayHeight, value); }

    /// <summary>
    /// Full tile key of the band the current <see cref="Bitmap"/> was composited
    /// for (the top-left cell's key stands in for the band + zoom). Lets the
    /// recompose skip rebuilding an identical band.
    /// </summary>
    internal PdfViewerControl.ContinuousTileKey CompositeKey { get; private set; }

    internal void ApplyZoom(double zoom)
    {
        DisplayWidth = WidthPt * PdfViewerControl.PointsToDip * zoom;
        DisplayHeight = HeightPt * PdfViewerControl.PointsToDip * zoom;
    }

    internal void ApplyLayout(double topDip, double zoom)
    {
        TopDip = topDip;
        ApplyZoom(zoom);
    }

    /// <summary>
    /// Publish a freshly composited band bitmap and its page-local DIP placement.
    /// The slot takes ownership of <paramref name="bitmap"/>; the composite it
    /// replaces is released once the binding has moved off it (#1466).
    /// </summary>
    internal void SetComposite(WriteableBitmap bitmap, PdfViewerControl.ContinuousTileKey compositeKey,
        double xDip, double yDip, double widthDip, double heightDip)
    {
        var previous = _bitmap;
        CompositeKey = compositeKey;
        TileDisplayX = xDip;
        TileDisplayY = yDip;
        TileDisplayWidth = widthDip;
        TileDisplayHeight = heightDip;
        Bitmap = bitmap;
        ReleaseAfterBindingMoves(previous, bitmap);
    }

    /// <summary>Stop showing a composite and release it once the binding has moved off it (#1466).</summary>
    internal void ClearComposite()
    {
        var previous = _bitmap;
        Bitmap = null;
        CompositeKey = default;
        ReleaseAfterBindingMoves(previous, null);
    }

    /// <summary>
    /// Dispose a composite this slot no longer shows (#1466), but never
    /// synchronously.
    /// </summary>
    /// <remarks>
    /// A composite is bound to an Image (<c>{Binding Bitmap}</c>,
    /// PdfViewerControl.axaml), and Avalonia 12's <c>Bitmap.Dispose</c> releases
    /// its <c>IRef&lt;IBitmapImpl&gt;</c>: an Image still pointing at the
    /// disposed wrapper throws <see cref="ObjectDisposedException"/> on its next
    /// measure or render. By the time this runs, <see cref="Bitmap"/> has already
    /// changed and raised PropertyChanged, which the binding applies to
    /// <c>Image.Source</c> synchronously on the UI thread. The dispose is still
    /// posted at <see cref="DispatcherPriority.Background"/>, below the layout
    /// and render passes, so anything that picked up the old wrapper earlier in
    /// the same dispatcher turn finishes first. Frames the compositor already
    /// recorded are unaffected: render data holds its own cloned, ref-counted
    /// <c>IRef</c> to the pixels.
    /// <para>
    /// Every composite passes through <see cref="_bitmap"/> once —
    /// RecomposeSlotCore allocates a new one for each SetComposite — so each is
    /// posted at most once, and re-publishing the current instance posts
    /// nothing. If the dispatcher never runs the job (application shutdown), the
    /// bitmap falls back to the finalizer, as every composite did before.
    /// </para>
    /// </remarks>
    private static void ReleaseAfterBindingMoves(WriteableBitmap? previous, WriteableBitmap? current)
    {
        if (previous == null || ReferenceEquals(previous, current)) return;
        Dispatcher.UIThread.Post(previous.Dispose, DispatcherPriority.Background);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
