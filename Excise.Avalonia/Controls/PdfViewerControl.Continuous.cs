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

    // Bitmaps are bounded by an LRU list. We do NOT dispose on eviction: a bitmap
    // may still be bound to a realized (visible) Image, and disposing it would
    // crash the render. Dropping the reference lets the GC reclaim it once no
    // slot/Image holds it. Full disposal happens only on document change.
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
    // Cap concurrent cell renders: a grid multiplies the old per-page fan-out by
    // the visible cell count, and SkiaRenderer serializes typeface acquisition
    // process-wide (_typefaceLoadLock) — unbounded Task.Run just thrashes.
    private readonly SemaphoreSlim _continuousRenderGate =
        new(Math.Clamp(Environment.ProcessorCount - 1, 2, 6));
    // #615/#848: the cache bounds total resident BYTES, not a flat entry count.
    // Under the content-addressed grid (#848), tiles are now UNIFORM — every
    // interior cell is a full ContinuousTileQuantumDip square, edge cells smaller
    // — so the old 10x per-tile spread is gone. A worst-case tile is a full
    // quantum cell rendered at the (dpr-scaled) MaxContinuousDpi cap: at
    // ContinuousTileQuantumDip=256 that is ~1.6MB (dpr 1) up to ~6.5MB (dpr 2),
    // Bgra8888 4 bytes/px (see SkiaInterop.ToAvaloniaBitmap). Measurement lives in
    // Excise.Avalonia.Tests/ContinuousCacheMemoryTests.cs, which drives the real
    // CellToRequest + EffectiveContinuousDpi + ContinuousTileByteSize code paths.
    //
    // Budget: ~200MB peak resident bytes. That comfortably holds many uniform
    // tiles (dozens to ~120), i.e. the visible grid of the current page plus a
    // generous scroll-back buffer, so scrolling away and back is a cache hit
    // rather than a re-render — the reuse the grid was designed to make free. If
    // tile geometry changes (quantum, overscan, or the DPI cap) re-run
    // ContinuousCacheMemoryTests and reconsider this number -- don't just restate it.
    private const long ContinuousCacheByteBudget = 200L * 1024 * 1024;

    // Always keep at least this many entries, even if a single tile alone
    // exceeds the byte budget -- a single huge page must not defeat the LRU
    // entirely and force a full re-render on every scroll frame.
    private const int ContinuousCacheMinEntries = 2;

    internal const double PointsToDip = 96.0 / 72.0;
    private const double PageGapDip = 12.0;   // matches the DataTemplate Border bottom margin
    internal const int ContinuousTileQuantumDip = 256;
    internal const int ContinuousTileOverscanDip = 256;

    // Sharp high-zoom (#371): render each continuous page at a DPI that scales
    // with zoom so it stays crisp instead of upscaling a fixed-DPI bitmap, capped
    // so deep zoom stays bounded. Realized pages render only the visible region
    // through RenderOptions.ClipRect rather than allocating a full-page bitmap.
    internal const int MaxContinuousDpi = 240;
    private int ContinuousRenderDpi =>
        EffectiveContinuousDpi(DefaultRenderDpi, ZoomLevel, MaxContinuousDpi, EffectiveRenderScaling);

    /// <summary>
    /// The render DPI chosen for a given zoom and display device-pixel-ratio
    /// (pure; unit-tested). Multiplying by <paramref name="renderScaling"/> makes
    /// text crisp on HiDPI/Retina displays (#682): at 100% zoom on a 2× display,
    /// a page point occupies ~2.67 device pixels, so a 120-DPI raster upscales and
    /// softens — rendering at baseDpi×dpr gives the pixels the display actually has.
    /// The tile is laid out by its DIP dimensions, so more render pixels change
    /// only sharpness, never geometry. The cap scales with dpr too, so zoom stays
    /// crisp to the same *visual* zoom on every display (#683); the byte-budgeted
    /// tile cache (#615) absorbs the larger tiles.
    /// </summary>
    internal static int EffectiveContinuousDpi(int baseDpi, double zoom, int maxDpi, double renderScaling)
    {
        double dpr = Math.Clamp(renderScaling <= 0 ? 1.0 : renderScaling, 1.0, 4.0);
        return (int)Math.Clamp(
            Math.Round(baseDpi * zoom * dpr),
            Math.Round(baseDpi * dpr),
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
        _continuousSlots = slots;
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
        if (_continuousItems != null) _continuousItems.ItemsSource = null;
        _continuousSlots = null;
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
    }

    private void InvalidateContinuousCache()
    {
        CancelContinuousCellRenders();
        _continuousRenderPassScheduled = false;
        _pendingContinuousPage = null;
        // Drop each slot's live tile references so their bitmaps aren't retained
        // past the cache. The slots themselves are rebuilt by RebuildContinuous.
        if (_continuousSlots != null)
            foreach (var slot in _continuousSlots) slot.ClearComposite();
        foreach (var entry in _continuousCache) entry.Bitmap.Dispose();
        _continuousCache.Clear();
        _continuousPageLinks.Clear();
        // Selection state is per-document/page; drop the letter cache and any
        // in-flight selection so a document or render change can't reuse stale
        // glyphs (#815). Highlight rects live on the slots and are discarded when
        // the slots are rebuilt.
        _continuousPageLetterCache.Clear();
        _continuousSelectionAnchor = null;
        _continuousSelectionFocus = null;
        _continuousSelectionPage = 0;
    }

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
        if (_pendingContinuousPage is not null)
        {
            RenderVisibleContinuousTiles();
            return;
        }

        // Topmost visible page = the slot whose cumulative bottom passes the
        // current vertical offset (+ a small bias so a page counts as "current"
        // once its top edge is in view).
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

    private void OnContinuousContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container.DataContext is PdfPageSlot)
            RenderVisibleContinuousTiles();
    }

    private void OnContinuousContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        // A page scrolled out of the realized window: release its live tile
        // references so their bitmaps aren't retained beyond the LRU cache. The
        // bitmaps stay in the byte-budgeted cache for a quick, re-render-free
        // return when the page scrolls back (#848 makes that a cache hit).
        if (e.Container.DataContext is PdfPageSlot slot)
            slot.ClearComposite();
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

        foreach (var container in _continuousItems.GetRealizedContainers())
        {
            if (container.DataContext is not PdfPageSlot slot) continue;
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

        // Pass 2: schedule renders for cells not yet cached, then (re)composite the
        // page from its cached cells. RecomposeSlot only swaps in a new band bitmap
        // once every covering cell is available, so the previous composite (which
        // covers the old band + overscan) stays on screen during a scroll until the
        // new one is ready — no blank strip (#848), and one bitmap means no seams.
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
                _ = RenderContinuousCellsAsync(slot, pending);
            RecomposeSlot(slot);
        }
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
    private async Task RenderContinuousCellsAsync(
        PdfPageSlot slot, List<(GridCell Cell, ContinuousTileKey Key)> batch)
    {
        if (_continuousDetached || batch.Count == 0) return;
        // Fire-and-forget (`_ = RenderContinuousCellsAsync(...)`). An unobserved
        // exception here — e.g. the document being disposed mid-render during
        // teardown — must never surface: it would destabilise the whole
        // dispatcher (observed as cross-test dispatcher-pump timeouts / null
        // ItemsSource). Everything below the in-flight bookkeeping is guarded.
        var doc = Document;
        if (doc == null || slot.PageNumber < 1 || slot.PageNumber > doc.PageCount) return;

        int pageNumber = slot.PageNumber;
        int dpi = batch[0].Key.Dpi;
        double zoom = ZoomLevel;
        if (zoom <= 0) return;
        double pxPerDip = dpi / (96.0 * zoom);

        var claimed = new List<(GridCell Cell, ContinuousTileKey Key)>(batch.Count);
        Excise.Core.Document.PdfPage page;
        SKRect clip;
        double bandXDip, bandYDip;
        try
        {
            foreach (var entry in batch)
            {
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
                RecomposeSlot(slot);
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
            var contentBox = SkiaRenderer.ResolveEffectiveRenderBox(page).Normalize();
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
            return;
        }

        var token = _continuousDocCts.Token;

        try
        {
            await _continuousRenderGate.WaitAsync(token);
            try
            {
                // Scrolled past while this batch waited for the gate — drop the
                // cells that are no longer required. A batch whose cells are ALL
                // stale is dropped without rendering (the cheap skip that keeps
                // rapid scrolling from rendering every intermediate viewport).
                if (token.IsCancellationRequested)
                {
                    ContinuousRenderCancellationCount += claimed.Count;
                    return;
                }
                int stale = claimed.RemoveAll(e => !_continuousRequiredKeys.Contains(e.Key));
                ContinuousRenderCancellationCount += stale;
                if (claimed.Count == 0) return;

                ContinuousRenderStartCount++;
                var renderWatch = System.Diagnostics.Stopwatch.StartNew();
                var skBitmap = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    // A fresh renderer per pass: SkiaRenderer carries per-render
                    // instance state and is not reentrant, and several pages'
                    // bands may render concurrently.
                    var renderer = new SkiaRenderer();
                    return renderer.RenderPage(page, new RenderOptions
                    {
                        Dpi = dpi,
                        ClipRect = clip
                    });
                }, token);
                renderWatch.Stop();
                ContinuousRenderCompletedCount++;
                ContinuousRenderWallMs += renderWatch.ElapsedMilliseconds;

                try
                {
                    if (token.IsCancellationRequested) return;
                    int cached = SliceBandIntoCells(skBitmap, claimed, bandXDip, bandYDip, pxPerDip);
                    if (cached > 0)
                    {
                        Trace($"BandRendered page={pageNumber} cells={cached} " +
                              $"band={bandXDip:F0},{bandYDip:F0} bmpPx={skBitmap?.Width}x{skBitmap?.Height} " +
                              $"dpi={dpi} zoom={zoom:F3} ms={renderWatch.ElapsedMilliseconds}");
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
                _continuousRenderGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Scrolled away / document changed before the render finished.
        }
        catch
        {
            // A single bad band must not break the reading scroll.
        }
        finally
        {
            foreach (var (_, key) in claimed) _continuousInFlight.Remove(key);
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
        double bandXDip, double bandYDip, double pxPerDip)
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
            colW[cell.Col] = Math.Max(1, (int)Math.Floor(cell.WidthDip * pxPerDip));
            rowH[cell.Row] = Math.Max(1, (int)Math.Floor(cell.HeightDip * pxPerDip));
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
            AddToContinuousCache(key, bitmap);
            cachedCount++;
        }

        return cachedCount;
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
            int pxW = Math.Min(bmp.PixelSize.Width, Math.Max(1, (int)Math.Floor(cell.WidthDip * pxPerDip)));
            int pxH = Math.Min(bmp.PixelSize.Height, Math.Max(1, (int)Math.Floor(cell.HeightDip * pxPerDip)));
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
        Trace($"Composite page={slot.PageNumber} band={bandX:F0},{bandY:F0} {bandW:F0}x{bandH:F0} px={totalW}x{totalH} cells={parts.Length} dpi={dpi} zoom={ZoomLevel:F3}");
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
                bmp = node.Value.Bitmap;
                return true;
            }
        }
        bmp = null;
        return false;
    }

    private void AddToContinuousCache(ContinuousTileKey key, WriteableBitmap bmp)
    {
        for (var node = _continuousCache.First; node != null; node = node.Next)
        {
            if (node.Value.Key.Equals(key)) { _continuousCache.Remove(node); break; }
        }
        _continuousCache.AddFirst((key, bmp));
        // Drop (don't dispose) the LRU tail until back under the byte budget —
        // see the ContinuousCacheByteBudget field comment for how that number
        // was measured (#615). See the field comment on _continuousCache for why
        // eviction drops the reference rather than disposing it.
        while (_continuousCache.Count > ContinuousCacheMinEntries &&
               ContinuousCacheResidentBytes() > ContinuousCacheByteBudget)
            _continuousCache.RemoveLast();
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

    /// <summary>Publish a freshly composited band bitmap and its page-local DIP placement.</summary>
    internal void SetComposite(WriteableBitmap bitmap, PdfViewerControl.ContinuousTileKey compositeKey,
        double xDip, double yDip, double widthDip, double heightDip)
    {
        CompositeKey = compositeKey;
        TileDisplayX = xDip;
        TileDisplayY = yDip;
        TileDisplayWidth = widthDip;
        TileDisplayHeight = heightDip;
        Bitmap = bitmap;
    }

    internal void ClearComposite()
    {
        Bitmap = null;
        CompositeKey = default;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
