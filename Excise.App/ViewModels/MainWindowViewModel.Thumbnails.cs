using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace Excise.App.ViewModels;

/// <summary>
/// Thumbnail sidebar lifecycle: viewport tracking, prefetch, eviction, and
/// idle pre-warm (issues #687/#688/#689; baseline profile on #601).
///
/// The View reports per-item visibility via <see cref="NotifyThumbnailViewport"/>
/// (from Avalonia's EffectiveViewportChanged). From the visible set this partial
/// derives two windows around the viewport:
///
///   prefetch window  (±<see cref="ThumbnailPrefetchMargin"/>)  — pages loaded
///     ahead of scrolling so the user never sees placeholder frames (#688);
///   keep window      (±<see cref="ThumbnailKeepMargin"/>)      — pages outside
///     it have their decoded bitmap RELEASED (#687), bounding sidebar memory by
///     the window rather than the document length (a 1,000-page document would
///     otherwise accumulate ~0.5 GB of never-released thumbnails).
///
/// Eviction is safe because re-entry is effectively free: the on-disk WebP
/// cache (ThumbnailCacheService) decodes in well under a millisecond, so a
/// scrolled-back page repopulates instantly. The keep margin is deliberately
/// several times the prefetch margin (hysteresis) so boundary jitter cannot
/// thrash load/evict cycles.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Pages beyond the visible range to load ahead (#688).</summary>
    internal const int ThumbnailPrefetchMargin = 12;

    /// <summary>Pages beyond the visible range to keep decoded in memory (#687).</summary>
    internal const int ThumbnailKeepMargin = 48;

    private readonly HashSet<int> _visibleThumbnailIndices = new();
    private readonly object _thumbnailViewportLock = new();
    private bool _thumbnailWindowPassScheduled;
    private CancellationTokenSource? _thumbnailPrefetchCts;
    private CancellationTokenSource? _thumbnailPrewarmCts;

    /// <summary>Test hook: the currently-running prefetch chain, if any.</summary>
    internal Task? ThumbnailPrefetchTask { get; private set; }

    /// <summary>Test hook: the currently-running idle pre-warm sweep, if any.</summary>
    internal Task? ThumbnailPrewarmTask { get; private set; }

    /// <summary>Idle pre-warm on/off (#689). Mirrors AdjacentPagePrefetchEnabled.</summary>
    internal bool ThumbnailPrewarmEnabled { get; set; } = true;

    /// <summary>
    /// The prefetch/keep windows for a visible range (pure; unit-tested).
    /// Both are clamped to [0, pageCount).
    /// </summary>
    internal static (int PrefetchFrom, int PrefetchTo, int KeepFrom, int KeepTo) ComputeThumbnailWindow(
        int visibleMin, int visibleMax, int pageCount,
        int prefetchMargin = ThumbnailPrefetchMargin,
        int keepMargin = ThumbnailKeepMargin)
    {
        if (pageCount <= 0 || visibleMin > visibleMax)
            return (0, -1, 0, -1);
        int pf = Math.Max(0, visibleMin - prefetchMargin);
        int pt = Math.Min(pageCount - 1, visibleMax + prefetchMargin);
        int kf = Math.Max(0, visibleMin - keepMargin);
        int kt = Math.Min(pageCount - 1, visibleMax + keepMargin);
        return (pf, pt, kf, kt);
    }

    /// <summary>
    /// Called by the View whenever a thumbnail item's effective viewport
    /// changes. <paramref name="isVisible"/> is false when the item scrolled
    /// out (empty viewport). Coalesces bursts of events into one window pass
    /// per UI-thread Background dispatch.
    /// </summary>
    public void NotifyThumbnailViewport(int pageIndex, bool isVisible)
    {
        lock (_thumbnailViewportLock)
        {
            var changed = isVisible
                ? _visibleThumbnailIndices.Add(pageIndex)
                : _visibleThumbnailIndices.Remove(pageIndex);
            if (!changed || _thumbnailWindowPassScheduled)
                return;
            _thumbnailWindowPassScheduled = true;
        }
        Dispatcher.UIThread.Post(RunThumbnailWindowPass, DispatcherPriority.Background);
    }

    private void RunThumbnailWindowPass()
    {
        int visMin, visMax;
        lock (_thumbnailViewportLock)
        {
            _thumbnailWindowPassScheduled = false;
            if (_visibleThumbnailIndices.Count == 0)
                return; // sidebar hidden or between documents — leave state alone
            visMin = _visibleThumbnailIndices.Min();
            visMax = _visibleThumbnailIndices.Max();
        }

        var pageCount = PageThumbnails.Count;
        var (pf, pt, kf, kt) = ComputeThumbnailWindow(visMin, visMax, pageCount);
        if (pt < pf)
            return;

        // ── Evict (#687): release decoded bitmaps far outside the viewport. ──
        // Runs on the UI thread (we're a Dispatcher.Post). Dispose is deferred
        // to a later Background dispatch so any in-flight render pass that
        // still referenced the bitmap has unbound first.
        List<global::Avalonia.Media.Imaging.Bitmap>? evicted = null;
        for (int i = 0; i < pageCount; i++)
        {
            if (i >= kf && i <= kt) continue;
            var thumb = PageThumbnails[i];
            var bmp = thumb.ThumbnailImage;
            if (bmp == null) continue;
            thumb.ThumbnailImage = null;
            (evicted ??= new()).Add(bmp);
        }
        if (evicted != null)
        {
            _logger.LogDebug("Thumbnail eviction: released {Count} bitmaps outside keep window [{From},{To}]",
                evicted.Count, kf, kt);
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var bmp in evicted)
                    try { bmp.Dispose(); } catch { }
            }, DispatcherPriority.Background);
        }

        // ── Prefetch (#688): load the ±margin window, nearest-first, one at a
        // time so an on-screen demand load is never more than one thumbnail
        // render behind. Replacing the CTS abandons a stale chain when the
        // viewport moves again. ──
        var toLoad = new List<int>();
        for (int i = pf; i <= pt; i++)
        {
            if (PageThumbnails[i].ThumbnailImage == null)
                toLoad.Add(i);
        }
        if (toLoad.Count == 0)
            return;
        int center = (visMin + visMax) / 2;
        toLoad.Sort((a, b) => Math.Abs(a - center).CompareTo(Math.Abs(b - center)));

        _thumbnailPrefetchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _thumbnailPrefetchCts = cts;
        ThumbnailPrefetchTask = PrefetchThumbnailsAsync(toLoad, cts.Token);
    }

    private async Task PrefetchThumbnailsAsync(IReadOnlyList<int> indices, CancellationToken ct)
    {
        try
        {
            foreach (var index in indices)
            {
                if (ct.IsCancellationRequested) return;
                await EnsureThumbnailLoadedAsync(index, ct);
            }
        }
        catch (OperationCanceledException) { /* viewport moved on */ }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Thumbnail prefetch chain stopped");
        }
    }

    private void CancelThumbnailWindowWork()
    {
        _thumbnailPrefetchCts?.Cancel();
        _thumbnailPrewarmCts?.Cancel();
        lock (_thumbnailViewportLock)
        {
            _visibleThumbnailIndices.Clear();
        }
    }

    /// <summary>
    /// Idle pre-warm (#689): after a document opens, walk every page once in
    /// the background so the WebP disk cache is fully populated — the first
    /// full sidebar scroll then shows no placeholders, and every future open
    /// of this file is 100% sub-ms cache hits. Cache-only: the decoded bitmap
    /// is disposed immediately, so this never violates the #687 memory bound.
    /// Yields to demand: it waits while any visible-demand load is in flight
    /// and throttles between pages.
    /// </summary>
    private void QueueThumbnailPrewarm(Services.ThumbnailCacheService thumbnailCache)
    {
        _thumbnailPrewarmCts?.Cancel();
        if (!ThumbnailPrewarmEnabled)
            return;
        var cts = new CancellationTokenSource();
        _thumbnailPrewarmCts = cts;
        var pageCount = PageThumbnails.Count;
        ThumbnailPrewarmTask = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < pageCount; i++)
                {
                    if (cts.Token.IsCancellationRequested) return;

                    // Yield to on-screen demand: don't queue behind the render
                    // gate while a viewport-driven load is pending.
                    while (true)
                    {
                        bool demandBusy;
                        lock (_thumbnailLoadLock) { demandBusy = _thumbnailLoadTasks.Count > 0; }
                        if (!demandBusy) break;
                        await Task.Delay(50, cts.Token);
                    }

                    // Already decoded in memory (visible/prefetched) — its disk
                    // write is queued by the cache service; nothing to do.
                    if (i < PageThumbnails.Count && PageThumbnails[i].ThumbnailImage != null)
                        continue;

                    using var sk = await thumbnailCache.GetThumbnailAsync(i, cts.Token);
                    // Bitmap intentionally discarded: this sweep exists to
                    // populate the DISK cache, not sidebar memory (#687).

                    await Task.Delay(25, cts.Token); // throttle: idle work
                }
                _logger.LogInformation("Thumbnail pre-warm complete: {Pages} pages cached", pageCount);
            }
            catch (OperationCanceledException) { /* document changed/closed */ }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Thumbnail pre-warm stopped");
            }
        }, CancellationToken.None);
    }
}
