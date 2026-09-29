using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Avalonia;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Threading;
using Excise.Rendering;

using static Excise.Avalonia.Controls.PdfViewerControl;

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
/// <see cref="PdfViewerControl.ContinuousTileOverscanDip"/> (256 DIP); at fit-width a page is
/// ~1000 DIP tall, so the next page was never rendered before the turn.
/// </para>
/// <para>
/// What it renders. A page turn in the continuous view is
/// <see cref="PdfViewerControl.NextPage"/> → <see cref="ScrollToPageContinuous(int)"/>, which
/// sets <c>Offset.Y</c> to the target slot's top. Look-ahead therefore
/// simulates the render pass at THAT offset — same
/// <see cref="PdfViewerControl.RequiredTileCells"/>, same <see cref="PdfViewerControl.CellKey"/>, same per-page
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
/// existing tile budget (<see cref="PdfViewerControl.ContinuousCacheByteBudget"/>) and may evict
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
internal sealed partial class ContinuousPageView
{
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
            || ContinuousItems == null || ContinuousScrollViewer == null || _continuousSlots == null)
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

        var viewport = ContinuousScrollViewer.Viewport;
        var offset = ContinuousScrollViewer.Offset;
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
                ContinuousScrollViewer.Extent.Height, dpi, doc.PageCount);
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
    internal void CancelContinuousLookAhead()
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
    internal void SuppressContinuousLookAheadAfterTrim()
    {
        if (ContinuousScrollViewer != null && _continuousSlots != null)
        {
            var viewport = ContinuousScrollViewer.Viewport;
            var offset = ContinuousScrollViewer.Offset;
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
}
