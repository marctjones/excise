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
    internal const long ContinuousCacheByteBudget = 200L * 1024 * 1024;
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
    private double _pendingSingleFraction = -1;
    private IDisposable? _pendingSingleFractionSub;
    private void OnViewModeChanged()
    {
        bool continuous = ViewMode == PdfViewMode.Continuous;
        Trace($"ViewMode -> {ViewMode} page={CurrentPage} zoom={ZoomLevel:F3} " +
              $"contOffset={ContinuousScrollViewer?.Offset.Y:F0}/{ContinuousScrollViewer?.Extent.Height:F0} " +
              $"singleOffset={PdfScrollViewer?.Offset.Y:F0}/{PdfScrollViewer?.Extent.Height:F0}");

        // Capture the reader's intra-page position BEFORE flipping
        // visibility — a hidden ScrollViewer's offset is not trustworthy.
        // Applied to the destination view once it has laid out (#693).
        double fraction = continuous ? SingleIntraPageFraction() : ContinuousPart.ContinuousIntraPageFraction();

        if (ContinuousScrollViewer != null) ContinuousScrollViewer.IsVisible = continuous;
        if (PdfScrollViewer != null) PdfScrollViewer.IsVisible = !continuous;

        // #1564: render-ahead belongs to the view that scheduled it.
        if (continuous) CancelSinglePageLookAhead();
        else ContinuousPart.CancelContinuousLookAhead();

        if (continuous)
        {
            ContinuousPart.RebuildContinuous();
            ReportActiveViewport();

            // Defer the scroll-to until the items panel has measured the slots —
            // but read CurrentPage when the callback RUNS, not when it is posted.
            //
            // Capturing it here (`int target = CurrentPage;`) captured a STALE page:
            // a navigation issued between the post and the callback would be
            // overwritten by this deferred scroll dragging the user back to
            // wherever they were when the view mode flipped. Switching to
            // continuous and immediately jumping to a page did exactly that.
            Dispatcher.UIThread.Post(() => ContinuousPart.ScrollToPageContinuous(CurrentPage, fraction), DispatcherPriority.Background);
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
    /// <summary>Fraction of the page above the viewport top in single-page view.</summary>
    private double SingleIntraPageFraction()
    {
        if (PdfScrollViewer == null) return 0;
        var extent = PdfScrollViewer.Extent.Height;
        if (extent <= 1) return 0;
        return Math.Clamp(PdfScrollViewer.Offset.Y / extent, 0, 0.99);
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
        if (_pendingSingleFraction < 0 || PdfScrollViewer == null)
        {
            _pendingSingleFractionSub?.Dispose();
            _pendingSingleFractionSub = null;
            return;
        }
        var extent = PdfScrollViewer.Extent.Height;
        if (extent <= 1)
        {
            _pendingSingleFractionSub ??= PdfScrollViewer
                .GetObservable(ScrollViewer.ExtentProperty)
                .Subscribe(new AnonymousObserver<Size>(_ => ApplyPendingSingleFraction()));
            return;
        }
        _pendingSingleFractionSub?.Dispose();
        _pendingSingleFractionSub = null;
        PdfScrollViewer.Offset = new Vector(PdfScrollViewer.Offset.X, _pendingSingleFraction * extent);
        _pendingSingleFraction = -1;
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
    /// <summary>
    /// #846: snapshot the reader's current intra-page position so the NEXT
    /// <c>RebuildContinuous</c> (triggered by a structural mutation
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
    /// <summary>
    /// Re-lay-out the continuous view after a STRUCTURAL mutation — a page
    /// added, inserted, moved, removed or rotated (#917).
    /// </summary>
    /// <remarks>
    /// ⚠️ #1651: this must drop the tile cache, and for a whole release it did
    /// not. Tiles are keyed by <see cref="ContinuousTileKey"/>, whose first
    /// field is the page NUMBER, so a mutation that changes which page has a
    /// given number makes every tile for those numbers stale. The note left
    /// here said "page CONTENT did not change — only the page order", which is
    /// true of the pages and false of the keys: scrolling back to a number
    /// re-composed the pre-mutation pixels and the reader's edit looked lost.
    /// Measured before the fix — 5.5 MB of tiles survived moving page 4 to the
    /// front of a four-page document.
    ///
    /// Invalidating is what the document-change and render-version paths
    /// already do, and it carries the deferred-dispose handling (#1466/#1467)
    /// that keeps the bitmap currently on screen alive until its binding has
    /// moved.
    /// </remarks>
    public void RefreshContinuousLayout() => ContinuousPart.RefreshContinuousLayout();

    /// <summary>
    /// #1876: <paramref name="reloaded"/> is about to become the Document and is
    /// the document on screen, reopened from the bytes a save just wrote. Keep the
    /// continuous view's pages on screen until it has rendered them.
    /// </summary>
    public void KeepPagesOnScreenUntilRendered(Excise.Core.Document.PdfDocument reloaded) =>
        ContinuousPart.KeepPagesOnScreenUntilRendered(reloaded);

    /// <summary>
    /// #846: snapshot the reader's current intra-page position so the next
    /// continuous rebuild (triggered by a structural mutation reloading the
    /// document) restores it. No-op outside the continuous view.
    /// </summary>
    public void PreserveContinuousReadingPositionOnNextRebuild() =>
        ContinuousPart.PreserveContinuousReadingPositionOnNextRebuild();

    /// <summary>
    /// The page a command that says "current page" must act on (#1650): in
    /// continuous mode the page with the greatest visible area in the viewport,
    /// otherwise the displayed page. Separate from <see cref="CurrentPage"/>, the
    /// scroll anchor.
    /// </summary>
    public int MostVisiblePage => ContinuousPart.MostVisiblePage;

    /// <summary>Clear every page's continuous selection highlight.</summary>
    public void ClearContinuousSelectionHighlight() => ContinuousPart.ClearContinuousSelectionHighlight();

    // ── the continuous view (#1842 step 6) ──────────────────────────────────

    /// <summary>The continuous view's scroller (template part of the child).</summary>
    internal ScrollViewer ContinuousScrollViewer => ContinuousPart.ContinuousScrollViewer;

    /// <summary>The continuous view's page list (template part of the child).</summary>
    internal ItemsControl ContinuousItems => ContinuousPart.ContinuousItems;

    /// <summary>
    /// Wire the continuous view to this viewer: it reads the viewer's state and
    /// reports the scroll-derived anchor page, its viewport, finished selections and
    /// cache invalidations back here. Called once, from <see cref="WireTemplateParts"/>.
    /// </summary>
    private void WireContinuousView()
    {
        ContinuousPart.AnchorPageChanged += OnContinuousAnchorPageChanged;
        ContinuousPart.ViewportChanged += OnScrollViewerViewportChanged;
        ContinuousPart.TextSelected += (_, e) => TextSelected?.Invoke(this, e);
        ContinuousPart.CachesInvalidated += () =>
        {
            // Same lifetime as the tile cache: an annotation cache that outlived
            // the document would hover notes from the previous file (#1074).
            _pageCaches.Clear();
            _lastHoveredAnnotation = null;
        };
        ContinuousPart.Attach(this);
    }

    // ── Test seams of the continuous view, forwarded under their old names so the
    //    tests keep addressing the viewer (#1842, design §3.1 principle 8). ──────
    internal bool AddToContinuousCache(ContinuousTileKey key, WriteableBitmap bmp, bool lookAhead = false) =>
        ContinuousPart.AddToContinuousCache(key, bmp, lookAhead);
    internal IReadOnlyList<(ContinuousTileKey Key, WriteableBitmap Bitmap)> ContinuousCacheEntriesForTests() =>
        ContinuousPart.ContinuousCacheEntriesForTests();
    internal IReadOnlySet<ContinuousTileKey> ContinuousRequiredKeysForTests => ContinuousPart.ContinuousRequiredKeysForTests;
    internal long ContinuousCompositeResidentBytes() => ContinuousPart.ContinuousCompositeResidentBytes();
    internal ContinuousBitmapOverlap MeasureContinuousBitmapOverlap() => ContinuousPart.MeasureContinuousBitmapOverlap();
    internal string ContinuousDiagnostics() => ContinuousPart.ContinuousDiagnostics();
    internal DecodedImageSampleRetention ContinuousImageSamplesForTests => ContinuousPart.ContinuousImageSamplesForTests;
    internal Action<int>? ContinuousBandRenderStartingForTests
    {
        get => ContinuousPart.ContinuousBandRenderStartingForTests;
        set => ContinuousPart.ContinuousBandRenderStartingForTests = value;
    }
    internal long? ContinuousCacheByteBudgetOverride
    {
        get => ContinuousPart.ContinuousCacheByteBudgetOverride;
        set => ContinuousPart.ContinuousCacheByteBudgetOverride = value;
    }
    internal long? ContinuousCompositeByteBoundOverride
    {
        get => ContinuousPart.ContinuousCompositeByteBoundOverride;
        set => ContinuousPart.ContinuousCompositeByteBoundOverride = value;
    }
    internal int ContinuousCompositeOverBoundCount => ContinuousPart.ContinuousCompositeOverBoundCount;
    internal int ContinuousRenderStartCount => ContinuousPart.ContinuousRenderStartCount;
    internal int ContinuousRenderCancellationCount => ContinuousPart.ContinuousRenderCancellationCount;
    internal int ContinuousRenderCoalescedRequestCount => ContinuousPart.ContinuousRenderCoalescedRequestCount;
    internal int ContinuousRenderCompletedCount => ContinuousPart.ContinuousRenderCompletedCount;
    internal long ContinuousRenderWallMs => ContinuousPart.ContinuousRenderWallMs;
    internal int ContinuousRequiredCellCount => ContinuousPart.ContinuousRequiredCellCount;
    internal int ContinuousInFlightCount => ContinuousPart.ContinuousInFlightCount;
    internal int ContinuousEffectiveRenderDpi => ContinuousPart.ContinuousEffectiveRenderDpi;
    internal bool RecomposeFromCacheOnLayoutPending => ContinuousPart.RecomposeFromCacheOnLayoutPending;
    internal int ContinuousLookAheadStartCount => ContinuousPart.ContinuousLookAheadStartCount;
    internal int ContinuousLookAheadCompletedCount => ContinuousPart.ContinuousLookAheadCompletedCount;
    internal int ContinuousLookAheadCancellationCount => ContinuousPart.ContinuousLookAheadCancellationCount;
    internal bool ContinuousLookAheadInFlight => ContinuousPart.ContinuousLookAheadInFlight;
    internal bool ContinuousLookAheadCancellationRequested => ContinuousPart.ContinuousLookAheadCancellationRequested;
    internal IReadOnlyCollection<ContinuousTileKey> ContinuousLookAheadTilesForTests => ContinuousPart.ContinuousLookAheadTilesForTests;
    internal IReadOnlyCollection<int> ContinuousLookAheadSamplePagesForTests => ContinuousPart.ContinuousLookAheadSamplePagesForTests;

    /// <summary>
    /// The reader scrolled a different page to the viewport top. Mark the change as
    /// scroll-driven so <see cref="OnCurrentPageChanged"/> does not scroll back
    /// (feedback loop).
    /// </summary>
    private void OnContinuousAnchorPageChanged(int page)
    {
        _syncingPageFromScroll = true;
        try { CurrentPage = page; }
        finally { _syncingPageFromScroll = false; }
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
    // Copy the top-left copyW x copyH pixels of one cell into the composite buffer
    // at an integer pixel offset (copyW/copyH = the cell's CONTENT size, dropping
    // the empty ceil-overhang edge). Bgra8888, 4 bytes/px, row by row.
    // Bounds-clamped defensively though the mosaic offsets are exact by construction.
    internal static unsafe void BlitCell(global::Avalonia.Platform.ILockedFramebuffer dst,
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
