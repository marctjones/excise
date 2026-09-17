using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Avalonia;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Threading;
using Excise.Rendering;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Render-ahead (#1564): once the visible page has finished drawing and the
/// viewer is idle, render the page a page turn would show next (then the one
/// before it) at the current zoom, so the turn is a cache hit and draws in one
/// step.
/// </summary>
/// <remarks>
/// <para>
/// Why: the #1544 speed benchmark (2026-09-17) measured excise page turns
/// finishing in 73–120 ms (Altona p95 394 ms) against 30–40 ms for Preview and
/// Acrobat, and most excise turns drew in TWO steps (IRS 52 of 60, Altona 31
/// of 45): the page moved, then its pixels arrived 36–70 ms later. The
/// continuous view renders only the viewport plus
/// <see cref="ContinuousTileOverscanDip"/> (256 DIP); at fit-width a page is
/// ~1000 DIP tall, so the next page was never rendered before the turn.
/// </para>
/// <para>
/// What it renders. A page turn in the continuous view is
/// <see cref="NextPage"/> → <see cref="ScrollToPageContinuous(int)"/>, which
/// sets <c>Offset.Y</c> to the target slot's top. Look-ahead therefore
/// simulates the render pass at THAT offset — same
/// <see cref="RequiredTileCells"/>, same <see cref="CellKey"/>, same per-page
/// batch — rather than "rendering page N+1". The keys it caches are the keys
/// the real pass will ask for, and a page's missing cells form the same batch
/// the real pass would issue, so the band render and its slicing are the same
/// calls and the composite is pixel-identical (pinned by
/// <c>RenderAheadTests</c>). Look-ahead changes timing only.
/// </para>
/// <para>
/// Cost and bounds. One look-ahead batch at a time, behind the same render
/// gate as visible bands (width ≥ 2, so a visible band always gets a slot),
/// and only when no visible band is in flight. Its tiles are charged to the
/// existing tile budget (<see cref="ContinuousCacheByteBudget"/>) and may evict
/// scroll-back tiles, never a tile of the current bands. At fit-width on a 2×
/// display a letter page is ~1464×1894 px, ~11 MiB, so N±1 costs ~22 MiB of a
/// 200 MiB budget. Every trim level drops them (they are outside the current
/// bands), and a trim never re-arms look-ahead: the #1478 contract is that a
/// trim re-renders nothing.
/// </para>
/// <para>
/// No timer, no polling (#1462). Look-ahead is scheduled from exactly two
/// places — the end of a render pass with nothing left to render, and the end
/// of a band render — and each step either starts a batch for cells it has not
/// tried at this scroll position or marks the position done. Keys are tried at
/// most once per position (a tile the budget refuses is not retried), so the
/// chain ends and an idle viewer does no work.
/// </para>
/// </remarks>
public partial class PdfViewerControl
{
    /// <summary>
    /// Whether the viewer renders the next and previous page ahead (#1564).
    /// Default on. Tests turn it off to compare a cold page turn with a
    /// pre-rendered one.
    /// </summary>
    internal bool RenderAheadEnabled
    {
        get => _renderAheadEnabled;
        set
        {
            _renderAheadEnabled = value;
            if (!value)
            {
                CancelContinuousLookAhead();
                CancelSinglePageLookAhead();
            }
        }
    }

    private bool _renderAheadEnabled = true;

    // ---- Continuous view --------------------------------------------------

    /// <summary>One in-flight look-ahead band: its keys and its own cancellation.</summary>
    private sealed class ContinuousLookAheadBatch : IDisposable
    {
        internal ContinuousLookAheadBatch(int page, HashSet<ContinuousTileKey> keys, CancellationToken documentToken)
        {
            Page = page;
            Keys = keys;
            Source = CancellationTokenSource.CreateLinkedTokenSource(documentToken);
        }

        internal int Page { get; }
        internal HashSet<ContinuousTileKey> Keys { get; }
        internal CancellationTokenSource Source { get; }
        internal CancellationToken Token => Source.Token;

        internal void Cancel()
        {
            try { Source.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void Dispose() => Source.Dispose();
    }

    /// <summary>
    /// The scroll position, viewport, render DPI, page and slot list a
    /// look-ahead plan was made for. Any change starts a new plan.
    /// </summary>
    private readonly record struct ContinuousLookAheadAnchor(
        double OffsetX, double OffsetY, double ViewportWidth, double ViewportHeight,
        int Dpi, int CurrentPage, List<PdfPageSlot>? Slots, Excise.Core.Document.PdfDocument? Document);

    private ContinuousLookAheadBatch? _continuousLookAheadBatch;
    private bool _continuousLookAheadScheduled;
    private ContinuousLookAheadAnchor _continuousLookAheadAnchor;
    private bool _continuousLookAheadDone;
    private readonly HashSet<ContinuousTileKey> _continuousLookAheadAttempted = new();
    // Tiles a look-ahead render cached that no visible pass has used yet.
    // Trims and budget cuts drop these first (#1564).
    private readonly HashSet<ContinuousTileKey> _continuousLookAheadTiles = new();
    // Pages rendered ahead from the CURRENT position keep their decoded image
    // samples although they are not realized (#1492 releases an unrealized
    // page's samples). Without this, the page a turn lands on is shown from
    // pre-rendered tiles with its samples already gone, and the first scroll
    // past the rendered band decodes its images again — the cost #1492 kept
    // off the realized page. At most the two neighbours of one position; the
    // set is emptied when the reader moves and by every trim.
    private readonly HashSet<int> _continuousLookAheadSamplePages = new();

    /// <summary>The pages whose decoded samples render-ahead keeps (tests).</summary>
    internal IReadOnlyCollection<int> ContinuousLookAheadSamplePagesForTests => _continuousLookAheadSamplePages;

    internal int ContinuousLookAheadStartCount { get; private set; }
    internal int ContinuousLookAheadCompletedCount { get; private set; }
    internal int ContinuousLookAheadCancellationCount { get; private set; }

    /// <summary>True while a look-ahead band render is queued or running (tests).</summary>
    internal bool ContinuousLookAheadInFlight => _continuousLookAheadBatch != null;

    /// <summary>True when the in-flight look-ahead render has been told to stop (tests).</summary>
    internal bool ContinuousLookAheadCancellationRequested =>
        _continuousLookAheadBatch is { } batch && batch.Token.IsCancellationRequested;

    /// <summary>The cached tiles no visible pass has used yet (tests).</summary>
    internal IReadOnlyCollection<ContinuousTileKey> ContinuousLookAheadTilesForTests => _continuousLookAheadTiles;

    /// <summary>
    /// Post one look-ahead step, once. Called when the visible band may have
    /// just completed; the step itself checks that it has.
    /// </summary>
    private void MaybeScheduleContinuousLookAhead()
    {
        if (!_renderAheadEnabled || _continuousLookAheadScheduled || _continuousDetached)
            return;
        _continuousLookAheadScheduled = true;
        // Background: below input, layout and render, so a key press that
        // arrives in the same turn is handled before any look-ahead starts.
        Dispatcher.UIThread.Post(() =>
        {
            _continuousLookAheadScheduled = false;
            try { RunContinuousLookAheadStep(); } catch { }
        }, DispatcherPriority.Background);
    }

    private void RunContinuousLookAheadStep()
    {
        if (!_renderAheadEnabled || _continuousDetached || ViewMode != PdfViewMode.Continuous
            || _continuousItems == null || _continuousScrollViewer == null || _continuousSlots == null)
            return;
        var doc = Document;
        if (doc == null || _continuousDocCts.IsCancellationRequested)
            return;
        // A programmatic jump or zoom anchor has not landed: the offset is not
        // the one the reader will see. The pass that lands reschedules.
        if (_pendingContinuousPage != null || _pendingZoomAnchorPage > 0)
            return;
        if (_continuousLookAheadBatch != null || !ContinuousVisibleBandSettled())
            return;

        var viewport = _continuousScrollViewer.Viewport;
        var offset = _continuousScrollViewer.Offset;
        if (viewport.Width <= 0 || viewport.Height <= 0 || ZoomLevel <= 0)
            return;
        int dpi = ContinuousRenderDpi;

        var anchor = new ContinuousLookAheadAnchor(offset.X, offset.Y, viewport.Width, viewport.Height,
            dpi, CurrentPage, _continuousSlots, doc);
        if (!anchor.Equals(_continuousLookAheadAnchor))
        {
            _continuousLookAheadAnchor = anchor;
            _continuousLookAheadAttempted.Clear();
            _continuousLookAheadDone = false;
            if (_continuousLookAheadSamplePages.Count > 0)
            {
                // The old neighbours' samples go unless they are realized now.
                _continuousLookAheadSamplePages.Clear();
                ReleaseImageSamplesOfUnrealizedPages();
            }
        }
        if (_continuousLookAheadDone)
            return;

        // Next page first (reading forward is the common case), then the
        // previous one. Reading forward, the previous page is usually still
        // cached as scroll-back, so its step finds nothing to do.
        foreach (int target in new[] { CurrentPage + 1, CurrentPage - 1 })
        {
            if (target < 1 || target > _continuousSlots.Count || target > doc.PageCount)
                continue;
            var plan = PlanContinuousLookAhead(_continuousSlots, target, offset, viewport,
                _continuousScrollViewer.Extent.Height, dpi, doc.PageCount);
            if (plan is not { } batch)
                continue;

            foreach (var (_, key) in batch.Cells)
                _continuousLookAheadAttempted.Add(key);
            var keys = new HashSet<ContinuousTileKey>();
            foreach (var (_, key) in batch.Cells)
                keys.Add(key);
            var lookAhead = new ContinuousLookAheadBatch(batch.Slot.PageNumber, keys, _continuousDocCts.Token);
            _continuousLookAheadSamplePages.Add(batch.Slot.PageNumber);
            _continuousLookAheadBatch = lookAhead;
            Trace($"LookAhead start target={target} page={batch.Slot.PageNumber} cells={keys.Count} dpi={dpi}");
            _ = RenderContinuousCellsAsync(batch.Slot, batch.Cells, lookAhead);
            return;
        }

        _continuousLookAheadDone = true;
        Trace($"LookAhead idle page={CurrentPage} offsetY={offset.Y:F0}");
    }

    /// <summary>
    /// The first page's missing cells at the offset a turn to
    /// <paramref name="targetPage"/> would scroll to, or null when every cell
    /// there is cached, in flight or already tried at this position.
    /// </summary>
    private (PdfPageSlot Slot, List<(GridCell Cell, ContinuousTileKey Key)> Cells)? PlanContinuousLookAhead(
        IReadOnlyList<PdfPageSlot> slots, int targetPage, Vector offset, Size viewport,
        double extentHeight, int dpi, int pageCount)
    {
        var predicted = PredictedContinuousOffset(slots, targetPage, offset, viewport, extentHeight);
        foreach (var slot in SlotsIntersecting(slots, predicted.Y, viewport.Height))
        {
            if (slot.PageNumber < 1 || slot.PageNumber > pageCount) continue;
            var cells = RequiredTileCells(slot.DisplayWidth, slot.DisplayHeight, slot.TopDip,
                predicted, viewport, ContinuousTileQuantumDip, ContinuousTileOverscanDip);
            List<(GridCell, ContinuousTileKey)>? missing = null;
            foreach (var cell in cells)
            {
                var key = CellKey(slot.PageNumber, dpi, slot.DisplayWidth, slot.DisplayHeight, cell);
                // Peek, not TryGet: planning must not reorder the LRU.
                if (PeekContinuousCached(key) != null || _continuousInFlight.Contains(key)
                    || _continuousLookAheadAttempted.Contains(key))
                    continue;
                (missing ??= new()).Add((cell, key));
            }
            if (missing != null)
                return (slot, missing);
        }
        return null;
    }

    /// <summary>
    /// The offset <see cref="ScrollToPageContinuous(int)"/> lands on for
    /// <paramref name="targetPage"/>: the slot's top, clamped the way the
    /// ScrollViewer clamps it. Pure; unit-tested.
    /// </summary>
    internal static Vector PredictedContinuousOffset(
        IReadOnlyList<PdfPageSlot> slots, int targetPage, Vector offset, Size viewport, double extentHeight)
    {
        var top = slots[targetPage - 1].TopDip;
        double max = Math.Max(0, extentHeight - viewport.Height);
        return new Vector(offset.X, Math.Clamp(top, 0, max));
    }

    /// <summary>The slots whose box intersects [<paramref name="offsetY"/>, +<paramref name="height"/>).</summary>
    private static IEnumerable<PdfPageSlot> SlotsIntersecting(IReadOnlyList<PdfPageSlot> slots, double offsetY, double height)
    {
        if (slots.Count == 0) yield break;
        int first = Math.Max(0, FindTopVisibleContinuousPage(slots, offsetY) - 2);
        for (int i = first; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot.TopDip >= offsetY + height) yield break;
            yield return slot;
        }
    }

    /// <summary>
    /// Every tile the last render pass required is cached and no band render
    /// is in flight: the page on screen has finished drawing.
    /// </summary>
    private bool ContinuousVisibleBandSettled()
    {
        if (_continuousInFlight.Count > 0 || _continuousRequiredKeys.Count == 0)
            return false;
        foreach (var key in _continuousRequiredKeys)
        {
            if (PeekContinuousCached(key) == null)
                return false;
        }
        return true;
    }

    /// <summary>
    /// A render pass computed its bands. If the reader has moved since the
    /// look-ahead plan was made (scroll, page turn, zoom, resize), cancel the
    /// look-ahead render unless the new bands need some of its cells — paging
    /// onto the page being pre-rendered must not throw that render away (the
    /// pass coalesces on it through <see cref="_continuousInFlight"/>). A pass
    /// at the same position (layout, container churn) leaves it alone.
    /// </summary>
    private void OnContinuousRequiredKeysChanged(IReadOnlySet<ContinuousTileKey> required,
        Vector offset, Size viewport, int dpi)
    {
        if (_continuousLookAheadBatch is not { } batch)
            return;
        var anchor = new ContinuousLookAheadAnchor(offset.X, offset.Y, viewport.Width, viewport.Height,
            dpi, CurrentPage, _continuousSlots, Document);
        if (anchor.Equals(_continuousLookAheadAnchor) || batch.Keys.Overlaps(required)
            || PositionNeedsAny(batch.Keys, offset, viewport, dpi))
            return;
        batch.Cancel();
        Trace($"LookAhead cancel page={batch.Page} (reader moved)");
    }

    /// <summary>
    /// Whether the bands at <paramref name="offset"/> include any of
    /// <paramref name="keys"/>, computed from the slot geometry rather than
    /// from the realized containers. The render pass right after a page turn
    /// can run before the items panel has realized the turned-to page; its
    /// <c>required</c> set is then empty, and judging by it cancelled exactly
    /// the render the turn was waiting for (caught by
    /// <c>RenderAheadTests.Continuous_TurningOntoThePageBeingRenderedAhead_KeepsThatRender</c>).
    /// </summary>
    private bool PositionNeedsAny(HashSet<ContinuousTileKey> keys, Vector offset, Size viewport, int dpi)
    {
        if (_continuousSlots == null || viewport.Width <= 0 || viewport.Height <= 0)
            return false;
        foreach (var slot in SlotsIntersecting(_continuousSlots, offset.Y, viewport.Height))
        {
            foreach (var cell in RequiredTileCells(slot.DisplayWidth, slot.DisplayHeight, slot.TopDip,
                offset, viewport, ContinuousTileQuantumDip, ContinuousTileOverscanDip))
            {
                if (keys.Contains(CellKey(slot.PageNumber, dpi, slot.DisplayWidth, slot.DisplayHeight, cell)))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Cancel the look-ahead render whatever it holds (document change,
    /// detach, disable). Its in-flight claims end in its own finally.
    /// </summary>
    private void CancelContinuousLookAhead()
    {
        _continuousLookAheadBatch?.Cancel();
        _continuousLookAheadDone = false;
        _continuousLookAheadAnchor = default;
        _continuousLookAheadAttempted.Clear();
        _continuousLookAheadSamplePages.Clear();
    }

    /// <summary>
    /// A trim released the look-ahead tiles: cancel a look-ahead render that
    /// the current bands do not need, and do not plan again at this position.
    /// </summary>
    private void SuppressContinuousLookAheadAfterTrim()
    {
        if (_continuousScrollViewer != null && _continuousSlots != null)
        {
            var viewport = _continuousScrollViewer.Viewport;
            var offset = _continuousScrollViewer.Offset;
            if (_continuousLookAheadBatch is { } batch && !batch.Keys.Overlaps(_continuousRequiredKeys)
                && !PositionNeedsAny(batch.Keys, offset, viewport, ContinuousRenderDpi))
                batch.Cancel();
            _continuousLookAheadAnchor = new ContinuousLookAheadAnchor(offset.X, offset.Y,
                viewport.Width, viewport.Height, ContinuousRenderDpi, CurrentPage, _continuousSlots, Document);
            _continuousLookAheadDone = true;
        }
        // Trims release the samples of every page outside the bands.
        _continuousLookAheadSamplePages.Clear();
    }

    /// <summary>
    /// Called in a band render's finally, after its in-flight claims end.
    /// </summary>
    private void OnContinuousBandRenderFinished(ContinuousLookAheadBatch? lookAhead, bool current,
        IReadOnlyList<(GridCell Cell, ContinuousTileKey Key)> claimed)
    {
        if (lookAhead != null)
        {
            if (ReferenceEquals(_continuousLookAheadBatch, lookAhead))
                _continuousLookAheadBatch = null;
            lookAhead.Dispose();
            if (!current)
                return;
            // A visible pass that coalesced onto this render while it was being
            // cancelled is still waiting for these cells: hand them back to it.
            foreach (var (_, key) in claimed)
            {
                if (_continuousRequiredKeys.Contains(key) && PeekContinuousCached(key) == null)
                {
                    RenderVisibleContinuousTiles();
                    break;
                }
            }
        }
        if (current)
            MaybeScheduleContinuousLookAhead();
    }

    // ---- Single-page view -------------------------------------------------

    /// <summary>
    /// Everything that decides a single-page raster: shared by the visible
    /// render and look-ahead so both produce the same bitmap under the same
    /// cache key (<c>(page, DeviceDpi)</c>).
    /// </summary>
    internal readonly record struct SinglePageRenderSpec(
        int LogicalDpi, int DeviceDpi, double BitmapDpi, double MaxScale,
        double WidthPt, double HeightPt, Size LayoutSize);

    private SinglePageRenderSpec ComputeSinglePageRenderSpec(Excise.Core.Document.PdfPage page)
    {
        var logicalDpi = EffectiveSinglePageRenderDpi(page);
        var box = page.EffectiveCropBox;
        var widthPt = page.Rotation is 90 or 270 ? box.Height : box.Width;
        var heightPt = page.Rotation is 90 or 270 ? box.Width : box.Height;
        double scale = ZoomLevel * EffectiveRenderScaling;
        double maxScale = MaxSinglePageRenderScale(widthPt, heightPt, logicalDpi);
        var (renderDpi, bitmapDpi) = SinglePageRenderPlan(logicalDpi, scale, maxScale);
        return new SinglePageRenderSpec(logicalDpi, renderDpi, bitmapDpi, maxScale, widthPt, heightPt,
            SinglePageLayoutSize(widthPt, heightPt, logicalDpi));
    }

    /// <summary>
    /// The render options for a single page. One place, so a look-ahead
    /// render cannot drift from the visible one. Reads styled properties, so
    /// UI thread only. MaxSinglePagePreviewPixels is a DEVICE-pixel (memory)
    /// ceiling, so it is NOT scaled by the device-pixel-ratio: a normal page at
    /// device resolution stays far under it (crisp), while a very large page is
    /// still capped at the same memory bound (it simply doesn't gain the HiDPI
    /// sharpening).
    /// </summary>
    private Excise.Rendering.RenderOptions SinglePageRenderOptions(int deviceDpi) => new()
    {
        Dpi = deviceDpi,
        MaxPixelCount = MaxSinglePagePreviewPixels,
        RenderAnnotations = ShowAnnotations,
        ShowCommentAnnotations = ShowCommentAnnotations,
        ShowFieldAndLinkAnnotations = ShowFieldAndLinkAnnotations,
        RevealHiddenAnnotations = RevealHiddenAnnotations,
        HighlightFormFields = HighlightFormFields,
    };

    private sealed class SinglePageLookAhead
    {
        internal SinglePageLookAhead(Excise.Core.Document.PdfDocument document, int page, int dpi)
        {
            Document = document;
            Page = page;
            Dpi = dpi;
        }

        internal Excise.Core.Document.PdfDocument Document { get; }
        internal int Page { get; }
        internal int Dpi { get; }
        internal CancellationTokenSource Source { get; } = new();
        internal Task Task { get; set; } = Task.CompletedTask;
    }

    private readonly record struct SinglePageLookAheadAnchor(
        Excise.Core.Document.PdfDocument? Document, int Page, double Zoom, double RenderScaling, long Generation);

    private SinglePageLookAhead? _singlePageLookAhead;
    private bool _singlePageLookAheadScheduled;
    private SinglePageLookAheadAnchor _singlePageLookAheadAnchor;
    private bool _singlePageLookAheadDone;
    private readonly HashSet<(int Page, int Dpi)> _singlePageLookAheadAttempted = new();
    // Bumped by anything that makes an in-flight look-ahead result stale
    // (document, content or annotation-setting change).
    private long _singlePageLookAheadGeneration;
    // Bumped by every RenderCurrentPageAsync call, so a call that waited on a
    // look-ahead render can tell whether a newer request superseded it.
    private long _singlePageRequestSequence;

    internal int SinglePageLookAheadStartCount { get; private set; }
    internal int SinglePageLookAheadCompletedCount { get; private set; }
    internal int SinglePageLookAheadCancellationCount { get; private set; }
    internal int SinglePageLookAheadJoinCount { get; private set; }

    /// <summary>
    /// Called on the render thread with the page number just before a
    /// single-page look-ahead render starts (tests only), so a test can hold
    /// it in flight.
    /// </summary>
    internal Action<int>? SinglePageLookAheadStartingForTests { get; set; }

    /// <summary>True while a single-page look-ahead render is running (tests).</summary>
    internal bool SinglePageLookAheadInFlight => _singlePageLookAhead != null;

    /// <summary>Whether (page, device DPI) is in the single-page cache, without touching LRU order (tests).</summary>
    internal bool SinglePageCacheContainsForTests(int page) =>
        Document is { } doc && page >= 1 && page <= doc.PageCount
        && _singlePageRenderLifetime.Contains(page, ComputeSinglePageRenderSpec(doc.GetPage(page)).DeviceDpi);

    private void ScheduleSinglePageLookAhead()
    {
        if (!_renderAheadEnabled || _singlePageLookAheadScheduled)
            return;
        _singlePageLookAheadScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _singlePageLookAheadScheduled = false;
            try { RunSinglePageLookAheadStep(); } catch { }
        }, DispatcherPriority.Background);
    }

    private void RunSinglePageLookAheadStep()
    {
        if (!_renderAheadEnabled || ViewMode != PdfViewMode.SinglePage || !IsAttachedToVisualTree())
            return;
        var doc = Document;
        if (doc == null || IsLoading || _singlePageLookAhead != null)
            return;
        if (CurrentPage < 1 || CurrentPage > doc.PageCount)
            return;

        var anchor = new SinglePageLookAheadAnchor(doc, CurrentPage, ZoomLevel, EffectiveRenderScaling,
            _singlePageLookAheadGeneration);
        if (!anchor.Equals(_singlePageLookAheadAnchor))
        {
            _singlePageLookAheadAnchor = anchor;
            _singlePageLookAheadAttempted.Clear();
            _singlePageLookAheadDone = false;
        }
        if (_singlePageLookAheadDone)
            return;

        foreach (int target in new[] { CurrentPage + 1, CurrentPage - 1 })
        {
            if (target < 1 || target > doc.PageCount)
                continue;
            var page = doc.GetPage(target);
            var spec = ComputeSinglePageRenderSpec(page);
            if (!_singlePageLookAheadAttempted.Add((target, spec.DeviceDpi)))
                continue;
            if (_singlePageRenderLifetime.Contains(target, spec.DeviceDpi))
                continue;

            StartSinglePageLookAhead(doc, page, target, spec);
            return;
        }

        _singlePageLookAheadDone = true;
    }

    private void StartSinglePageLookAhead(
        Excise.Core.Document.PdfDocument doc, Excise.Core.Document.PdfPage page, int pageNumber, SinglePageRenderSpec spec)
    {
        var lookAhead = new SinglePageLookAhead(doc, pageNumber, spec.DeviceDpi);
        _singlePageLookAhead = lookAhead;
        long generation = _singlePageLookAheadGeneration;
        var options = SinglePageRenderOptions(spec.DeviceDpi);
        var token = lookAhead.Source.Token;
        var starting = SinglePageLookAheadStartingForTests;
        SinglePageLookAheadStartCount++;
        Trace($"SinglePageLookAhead start page={pageNumber} dpi={spec.DeviceDpi}");
        lookAhead.Task = RunAsync();

        async Task RunAsync()
        {
            bool landed = false;
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                // Its own renderer: SkiaRenderer carries per-render state and
                // the visible render may start on _renderer while this runs.
                var skBitmap = await Task.Run(() =>
                {
                    starting?.Invoke(pageNumber);
                    return new SkiaRenderer().RenderPage(page, options, token);
                }, token);
                try
                {
                    if (token.IsCancellationRequested || generation != _singlePageLookAheadGeneration
                        || !ReferenceEquals(Document, doc))
                        return;
                    var bitmap = Imaging.SkiaInterop.ToAvaloniaBitmap(skBitmap);
                    if (bitmap == null)
                        return;
                    var shown = _pdfImage?.Source as WriteableBitmap;
                    _singlePageRenderLifetime.Add(pageNumber, spec.DeviceDpi, bitmap, spec.LayoutSize,
                        keep: b => ReferenceEquals(b, shown));
                    ViewerMetrics.RecordLookAheadRender(watch.Elapsed, spec.DeviceDpi, ViewerMetrics.LookAheadSinglePage);
                    SinglePageLookAheadCompletedCount++;
                    landed = true;
                    Trace($"SinglePageLookAhead cached page={pageNumber} dpi={spec.DeviceDpi} ms={watch.ElapsedMilliseconds}");
                }
                finally
                {
                    skBitmap?.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                SinglePageLookAheadCancellationCount++;
            }
            catch
            {
                // A page that fails to render here fails visibly when navigated to.
            }
            finally
            {
                if (ReferenceEquals(_singlePageLookAhead, lookAhead))
                    _singlePageLookAhead = null;
                lookAhead.Source.Dispose();
                if (landed || !token.IsCancellationRequested)
                    ScheduleSinglePageLookAhead();
            }
        }
    }

    /// <summary>
    /// A visible single-page request for (<paramref name="pageNumber"/>,
    /// <paramref name="deviceDpi"/>) is about to start. If look-ahead is
    /// rendering exactly that page, wait for it instead of rendering it twice;
    /// otherwise cancel it so the visible render gets the CPU. Returns false
    /// when a newer request superseded this one while it waited.
    /// </summary>
    private async Task<bool> JoinOrCancelSinglePageLookAheadAsync(
        Excise.Core.Document.PdfDocument doc, int pageNumber, int deviceDpi, long requestSequence)
    {
        if (_singlePageLookAhead is not { } lookAhead)
            return true;
        if (lookAhead.Page != pageNumber || lookAhead.Dpi != deviceDpi || !ReferenceEquals(lookAhead.Document, doc))
        {
            CancelSinglePageLookAhead();
            return true;
        }

        SinglePageLookAheadJoinCount++;
        IsLoading = true;
        try { await lookAhead.Task; } catch { }
        return requestSequence == _singlePageRequestSequence && ReferenceEquals(Document, doc);
    }

    private void CancelSinglePageLookAhead()
    {
        if (_singlePageLookAhead is { } lookAhead)
        {
            try { lookAhead.Source.Cancel(); } catch (ObjectDisposedException) { }
            Trace($"SinglePageLookAhead cancel page={lookAhead.Page}");
        }
    }

    /// <summary>Cached single-page results may be stale: cancel and start over.</summary>
    private void InvalidateSinglePageLookAhead()
    {
        _singlePageLookAheadGeneration++;
        CancelSinglePageLookAhead();
    }

    private void SuppressSinglePageLookAheadAfterTrim()
    {
        CancelSinglePageLookAhead();
        if (Document is { } doc)
        {
            _singlePageLookAheadAnchor = new SinglePageLookAheadAnchor(doc, CurrentPage, ZoomLevel,
                EffectiveRenderScaling, _singlePageLookAheadGeneration);
            _singlePageLookAheadDone = true;
        }
    }

    private bool IsAttachedToVisualTree() => global::Avalonia.Controls.TopLevel.GetTopLevel(this) != null;
}
