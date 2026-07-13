using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Reactive;
using global::Avalonia.Threading;
using Pdfe.Rendering;
using SkiaSharp;

namespace Pdfe.Avalonia.Controls;

/// <summary>
/// Continuous (reading) view mode for <see cref="PdfViewerControl"/> (#371 part 2).
/// A render-virtualized vertical scroll of every page: only the pages near the
/// viewport are rendered, bitmaps are bounded, and off-screen renders are
/// cancelled. This view is read-only — all editing happens in single-page mode
/// (entering an editing interaction auto-switches back), so none of the
/// security-critical redaction/selection overlays run here.
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
    private readonly Dictionary<int, CancellationTokenSource> _continuousRenderCts = new();
    private readonly Dictionary<int, ContinuousTileKey> _continuousRenderKeys = new();
    private const int ContinuousCacheCapacity = 10;
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
        (int)Math.Clamp(Math.Round(DefaultRenderDpi * ZoomLevel), DefaultRenderDpi, MaxContinuousDpi);

    /// <summary>The render DPI chosen for a given zoom (pure; unit-tested).</summary>
    internal static int EffectiveContinuousDpi(int baseDpi, double zoom, int maxDpi) =>
        (int)Math.Clamp(Math.Round(baseDpi * zoom), baseDpi, maxDpi);

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

    internal int ContinuousRenderStartCount { get; private set; }
    internal int ContinuousRenderCancellationCount { get; private set; }
    internal int ContinuousRenderCacheHitCount { get; private set; }
    internal int ContinuousRenderCoalescedRequestCount { get; private set; }

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
            Dispatcher.UIThread.Post(() => ScrollToPageContinuous(CurrentPage), DispatcherPriority.Background);
        }
        else
        {
            ReportActiveViewport();
            // Back to single-page: make sure the current page is rendered.
            _ = RenderCurrentPageAsync();
        }

        UpdateViewerAutomationProperties();
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
        Dispatcher.UIThread.Post(() => ScrollToPageContinuous(CurrentPage), DispatcherPriority.Loaded);
    }

    private void ClearContinuous()
    {
        if (_continuousItems != null) _continuousItems.ItemsSource = null;
        _continuousSlots = null;
    }

    private void InvalidateContinuousCache()
    {
        foreach (var cts in _continuousRenderCts.Values) cts.Cancel();
        _continuousRenderCts.Clear();
        _continuousRenderKeys.Clear();
        _continuousRenderPassScheduled = false;
        _pendingContinuousPage = null;
        foreach (var entry in _continuousCache) entry.Bitmap.Dispose();
        _continuousCache.Clear();
    }

    /// <summary>
    /// Resize every slot to the new zoom (bindings re-layout the borders) and
    /// re-render the currently-realized pages at the new zoom-aware DPI so they
    /// stay sharp. Off-screen pages re-render lazily when realized.
    /// </summary>
    private void ApplyContinuousZoom()
    {
        if (_continuousSlots == null) return;
        ApplyContinuousSlotLayout(_continuousSlots);

        RenderVisibleContinuousTiles();
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

    private void ScrollToPageContinuous(int pageNumber)
    {
        if (pageNumber < 1) return;

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

        var targetY = _continuousSlots[pageNumber - 1].TopDip;
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

        var targetY = _continuousSlots[page - 1].TopDip;
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
        if (e.Container.DataContext is PdfPageSlot slot)
            _ = RenderContinuousTileAsync(slot);
    }

    private void OnContinuousContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (e.Container.DataContext is PdfPageSlot slot)
        {
            // Cancel an in-flight render for a page scrolled away before it
            // finished; keep the bitmap in the LRU cache for a quick return.
            if (_continuousRenderCts.TryGetValue(slot.PageNumber, out var cts))
            {
                cts.Cancel();
                _continuousRenderCts.Remove(slot.PageNumber);
                _continuousRenderKeys.Remove(slot.PageNumber);
            }
        }
    }

    private void RenderVisibleContinuousTiles()
    {
        if (_continuousItems == null || _continuousRenderPassScheduled)
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
        if (_continuousItems == null) return;

        foreach (var container in _continuousItems.GetRealizedContainers())
        {
            if (container.DataContext is PdfPageSlot slot)
                _ = RenderContinuousTileAsync(slot);
        }
    }

    private async Task RenderContinuousTileAsync(PdfPageSlot slot)
    {
        var doc = Document;
        if (doc == null || slot.PageNumber < 1 || slot.PageNumber > doc.PageCount) return;

        if (!TryCreateVisibleTileRequest(slot, out var request))
            return;

        int dpi = ContinuousRenderDpi;
        var key = new ContinuousTileKey(slot.PageNumber, dpi, request.XDip, request.YDip, request.WidthDip, request.HeightDip);
        if (slot.CurrentTileKey.Equals(key) && slot.Bitmap != null)
            return;

        if (TryGetContinuousCached(key, out var cached))
        {
            ContinuousRenderCacheHitCount++;
            slot.ApplyTile(request, key);
            slot.Bitmap = cached;
            return;
        }

        if (_continuousRenderKeys.TryGetValue(slot.PageNumber, out var inFlightKey) && inFlightKey.Equals(key))
        {
            ContinuousRenderCoalescedRequestCount++;
            return;
        }

        // Cancel any prior in-flight render for this same page.
        if (_continuousRenderCts.TryGetValue(slot.PageNumber, out var prior))
        {
            prior.Cancel();
            ContinuousRenderCancellationCount++;
        }
        var cts = new CancellationTokenSource();
        _continuousRenderCts[slot.PageNumber] = cts;
        _continuousRenderKeys[slot.PageNumber] = key;
        var token = cts.Token;
        var pageNumber = slot.PageNumber;

        try
        {
            ContinuousRenderStartCount++;
            var skBitmap = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var page = doc.GetPage(pageNumber);
                // A fresh renderer per page: SkiaRenderer carries per-render
                // instance state and is not reentrant, and continuous mode may
                // render several pages around the viewport concurrently.
                var renderer = new SkiaRenderer();
                return renderer.RenderPage(page, new RenderOptions
                {
                    Dpi = dpi,
                    ClipRect = request.ClipRect
                });
            }, token);

            try
            {
                if (token.IsCancellationRequested) return;
                var bitmap = Imaging.SkiaInterop.ToAvaloniaBitmap(skBitmap);
                if (bitmap != null)
                {
                    AddToContinuousCache(key, bitmap);
                    slot.ApplyTile(request, key);
                    slot.Bitmap = bitmap;
                }
            }
            finally
            {
                skBitmap?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            // Scrolled away before the render finished — drop it silently.
        }
        catch
        {
            // A single bad page must not break the reading scroll.
        }
        finally
        {
            if (_continuousRenderCts.TryGetValue(pageNumber, out var mine) && mine == cts)
                _continuousRenderCts.Remove(pageNumber);
            if (_continuousRenderKeys.TryGetValue(pageNumber, out var mineKey) && mineKey.Equals(key))
                _continuousRenderKeys.Remove(pageNumber);
        }
    }

    private bool TryCreateVisibleTileRequest(PdfPageSlot slot, out ContinuousTileRequest request)
    {
        request = default;
        if (_continuousScrollViewer == null || _continuousSlots == null)
            return false;

        var viewport = _continuousScrollViewer.Viewport;
        if (viewport.Width <= 0 || viewport.Height <= 0 || ZoomLevel <= 0)
            return false;

        return TryCreateContinuousTileRequest(
            slot,
            _continuousScrollViewer.Offset,
            viewport,
            slot.TopDip,
            ZoomLevel,
            out request);
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

    internal static bool TryCreateContinuousTileRequest(
        PdfPageSlot slot,
        Vector viewportOffset,
        Size viewport,
        double pageTop,
        double zoom,
        out ContinuousTileRequest request)
    {
        request = default;
        if (viewport.Width <= 0 || viewport.Height <= 0 || zoom <= 0)
            return false;

        double visibleLeft = Math.Clamp(viewportOffset.X, 0, slot.DisplayWidth);
        double visibleTop = Math.Clamp(viewportOffset.Y - pageTop, 0, slot.DisplayHeight);
        double visibleRight = Math.Clamp(viewportOffset.X + viewport.Width, 0, slot.DisplayWidth);
        double visibleBottom = Math.Clamp(viewportOffset.Y + viewport.Height - pageTop, 0, slot.DisplayHeight);

        if (visibleRight <= visibleLeft || visibleBottom <= visibleTop)
            return false;

        visibleLeft = AlignTileDown(Math.Max(0, visibleLeft - ContinuousTileOverscanDip));
        visibleTop = AlignTileDown(Math.Max(0, visibleTop - ContinuousTileOverscanDip));
        visibleRight = Math.Min(slot.DisplayWidth, AlignTileUp(visibleRight + ContinuousTileOverscanDip));
        visibleBottom = Math.Min(slot.DisplayHeight, AlignTileUp(visibleBottom + ContinuousTileOverscanDip));

        double dipPerPoint = PointsToDip * zoom;
        double leftPt = visibleLeft / dipPerPoint;
        double rightPt = visibleRight / dipPerPoint;
        double contentTopPt = slot.HeightPt - (visibleTop / dipPerPoint);
        double contentBottomPt = slot.HeightPt - (visibleBottom / dipPerPoint);

        var clip = new SKRect(
            (float)leftPt,
            (float)Math.Max(0, contentBottomPt),
            (float)Math.Min(slot.WidthPt, rightPt),
            (float)Math.Min(slot.HeightPt, contentTopPt));

        request = new ContinuousTileRequest(
            clip,
            (int)Math.Floor(visibleLeft),
            (int)Math.Floor(visibleTop),
            Math.Max(1, (int)Math.Ceiling(visibleRight - visibleLeft)),
            Math.Max(1, (int)Math.Ceiling(visibleBottom - visibleTop)));
        return true;
    }

    private static double AlignTileDown(double value) =>
        Math.Floor(value / ContinuousTileQuantumDip) * ContinuousTileQuantumDip;

    private static double AlignTileUp(double value) =>
        Math.Ceiling(value / ContinuousTileQuantumDip) * ContinuousTileQuantumDip;

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
        // Drop (don't dispose) the LRU tail — see field comment on why.
        while (_continuousCache.Count > ContinuousCacheCapacity)
            _continuousCache.RemoveLast();
    }

    internal readonly record struct ContinuousTileKey(int Page, int Dpi, int XDip, int YDip, int WidthDip, int HeightDip);

    internal readonly record struct ContinuousTileRequest(
        SKRect ClipRect,
        int XDip,
        int YDip,
        int WidthDip,
        int HeightDip);
}

/// <summary>
/// One page in the continuous (reading) view. Observable so the data-template's
/// Border size and Image source update as zoom changes and the page renders.
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

    internal double TopDip { get => _topDip; private set => Set(ref _topDip, value); }
    public double DisplayWidth { get => _displayWidth; private set => Set(ref _displayWidth, value); }
    public double DisplayHeight { get => _displayHeight; private set => Set(ref _displayHeight, value); }
    public double TileDisplayX { get => _tileDisplayX; private set => Set(ref _tileDisplayX, value); }
    public double TileDisplayY { get => _tileDisplayY; private set => Set(ref _tileDisplayY, value); }
    public double TileDisplayWidth { get => _tileDisplayWidth; private set => Set(ref _tileDisplayWidth, value); }
    public double TileDisplayHeight { get => _tileDisplayHeight; private set => Set(ref _tileDisplayHeight, value); }
    public WriteableBitmap? Bitmap { get => _bitmap; set => Set(ref _bitmap, value); }
    internal PdfViewerControl.ContinuousTileKey CurrentTileKey { get; private set; }

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

    internal void ApplyTile(PdfViewerControl.ContinuousTileRequest request, PdfViewerControl.ContinuousTileKey key)
    {
        TileDisplayX = request.XDip;
        TileDisplayY = request.YDip;
        TileDisplayWidth = request.WidthDip;
        TileDisplayHeight = request.HeightDip;
        CurrentTileKey = key;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
