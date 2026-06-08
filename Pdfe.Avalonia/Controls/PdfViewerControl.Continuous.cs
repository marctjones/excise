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
    private readonly LinkedList<(int Page, WriteableBitmap Bmp)> _continuousCache = new();
    private readonly Dictionary<int, CancellationTokenSource> _continuousRenderCts = new();
    private const int ContinuousCacheCapacity = 16;
    internal const double PointsToDip = 96.0 / 72.0;
    private const double PageGapDip = 12.0;   // matches the DataTemplate Border bottom margin

    // Guards the scroll -> CurrentPage -> scroll feedback loop.
    private bool _syncingPageFromScroll;

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
            _continuousScrollViewer
                .GetObservable(ScrollViewer.OffsetProperty)
                .Subscribe(new AnonymousObserver<Vector>(_ => OnContinuousScrolled()));
        }
    }

    private void OnViewModeChanged()
    {
        bool continuous = ViewMode == PdfViewMode.Continuous;
        if (_continuousScrollViewer != null) _continuousScrollViewer.IsVisible = continuous;
        if (_scrollViewer != null) _scrollViewer.IsVisible = !continuous;

        if (continuous)
        {
            RebuildContinuous();
            // Defer the scroll-to until the items panel has measured the slots.
            int target = CurrentPage;
            Dispatcher.UIThread.Post(() => ScrollToPageContinuous(target), DispatcherPriority.Background);
        }
        else
        {
            // Back to single-page: make sure the current page is rendered.
            _ = RenderCurrentPageAsync();
        }
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
        _continuousSlots = slots;
        _continuousItems.ItemsSource = slots;
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
        foreach (var entry in _continuousCache) entry.Bmp.Dispose();
        _continuousCache.Clear();
    }

    /// <summary>Resize every slot to the new zoom (bindings re-layout the borders).</summary>
    private void ApplyContinuousZoom()
    {
        if (_continuousSlots == null) return;
        foreach (var slot in _continuousSlots) slot.ApplyZoom(ZoomLevel);
    }

    // ---- Scroll <-> CurrentPage sync -----------------------------------

    private void ScrollToPageContinuous(int pageNumber)
    {
        if (_continuousScrollViewer == null || _continuousSlots == null) return;
        if (pageNumber < 1 || pageNumber > _continuousSlots.Count) return;

        double y = 0;
        for (int i = 0; i < pageNumber - 1; i++)
            y += _continuousSlots[i].DisplayHeight + PageGapDip;

        var x = _continuousScrollViewer.Offset.X;
        _continuousScrollViewer.Offset = new Vector(x, y);
    }

    private void OnContinuousScrolled()
    {
        if (ViewMode != PdfViewMode.Continuous || _continuousScrollViewer == null || _continuousSlots == null)
            return;

        // Topmost visible page = the slot whose cumulative bottom passes the
        // current vertical offset (+ a small bias so a page counts as "current"
        // once its top edge is in view).
        double offsetY = _continuousScrollViewer.Offset.Y + 1;
        double cumulative = 0;
        int top = 1;
        for (int i = 0; i < _continuousSlots.Count; i++)
        {
            cumulative += _continuousSlots[i].DisplayHeight + PageGapDip;
            top = i + 1;
            if (offsetY < cumulative) break;
        }

        if (top != CurrentPage)
        {
            // Mark the change as scroll-driven so OnCurrentPageChanged doesn't
            // scroll back (feedback loop).
            _syncingPageFromScroll = true;
            try { CurrentPage = top; }
            finally { _syncingPageFromScroll = false; }
        }
    }

    // ---- Container realization -> on-demand render ---------------------

    private void OnContinuousContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container.DataContext is PdfPageSlot slot)
            _ = RenderContinuousAsync(slot);
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
            }
        }
    }

    private async Task RenderContinuousAsync(PdfPageSlot slot)
    {
        var doc = Document;
        if (doc == null || slot.PageNumber < 1 || slot.PageNumber > doc.PageCount) return;

        if (TryGetContinuousCached(slot.PageNumber, out var cached))
        {
            slot.Bitmap = cached;
            return;
        }

        // Cancel any prior in-flight render for this same page.
        if (_continuousRenderCts.TryGetValue(slot.PageNumber, out var prior))
            prior.Cancel();
        var cts = new CancellationTokenSource();
        _continuousRenderCts[slot.PageNumber] = cts;
        var token = cts.Token;
        var pageNumber = slot.PageNumber;

        try
        {
            var skBitmap = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var page = doc.GetPage(pageNumber);
                // A fresh renderer per page: SkiaRenderer carries per-render
                // instance state and is not reentrant, and continuous mode may
                // render several pages around the viewport concurrently.
                var renderer = new SkiaRenderer();
                return renderer.RenderPage(page, new RenderOptions { Dpi = DefaultRenderDpi });
            }, token);

            try
            {
                if (token.IsCancellationRequested) return;
                var bitmap = Imaging.SkiaInterop.ToAvaloniaBitmap(skBitmap);
                if (bitmap != null)
                {
                    AddToContinuousCache(pageNumber, bitmap);
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
        }
    }

    private bool TryGetContinuousCached(int page, out WriteableBitmap? bmp)
    {
        for (var node = _continuousCache.First; node != null; node = node.Next)
        {
            if (node.Value.Page == page)
            {
                _continuousCache.Remove(node);
                _continuousCache.AddFirst(node);
                bmp = node.Value.Bmp;
                return true;
            }
        }
        bmp = null;
        return false;
    }

    private void AddToContinuousCache(int page, WriteableBitmap bmp)
    {
        for (var node = _continuousCache.First; node != null; node = node.Next)
        {
            if (node.Value.Page == page) { _continuousCache.Remove(node); break; }
        }
        _continuousCache.AddFirst((page, bmp));
        // Drop (don't dispose) the LRU tail — see field comment on why.
        while (_continuousCache.Count > ContinuousCacheCapacity)
            _continuousCache.RemoveLast();
    }
}

/// <summary>
/// One page in the continuous (reading) view. Observable so the data-template's
/// Border size and Image source update as zoom changes and the page renders.
/// </summary>
public sealed class PdfPageSlot : INotifyPropertyChanged
{
    private double _displayWidth;
    private double _displayHeight;
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

    public double DisplayWidth { get => _displayWidth; private set => Set(ref _displayWidth, value); }
    public double DisplayHeight { get => _displayHeight; private set => Set(ref _displayHeight, value); }
    public WriteableBitmap? Bitmap { get => _bitmap; set => Set(ref _bitmap, value); }

    internal void ApplyZoom(double zoom)
    {
        DisplayWidth = WidthPt * PdfViewerControl.PointsToDip * zoom;
        DisplayHeight = HeightPt * PdfViewerControl.PointsToDip * zoom;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
