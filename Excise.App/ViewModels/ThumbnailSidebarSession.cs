using Avalonia.Threading;
using Excise.App.Models;
using Excise.App.Services;
using Excise.Core.Document;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Excise.App.ViewModels;

/// <summary>
/// Owns the thumbnail sidebar's document-derived cache, background work,
/// generation guards, viewport window, and binding-ready items.
/// </summary>
internal sealed class ThumbnailSidebarSession : IDisposable
{
    internal const int PrefetchMargin = 12;
    internal const int KeepMargin = 48;

    /// <summary>
    /// How long the document must be left alone before the background
    /// pre-render of every thumbnail starts (#1565). The running value comes
    /// from the <c>IdleTrimSeconds</c> preference — see
    /// <see cref="PrewarmIdleDelay"/>; this is only the value a session has
    /// before any preferences have been applied to it, and it equals
    /// <see cref="Models.PerformanceSettings.Balanced"/>'s.
    /// </summary>
    /// <remarks>
    /// Pre-warm used to start with the document. On irs-1040-instructions.pdf
    /// (126 pages) it then held a CPU busy for 5.0 s — measured from the
    /// release-baseline run's own log, document open complete at +0.107 s, text
    /// index at +2.0 s, "pre-warm complete" at +5.0 s — which is what #1544 saw
    /// as a window that keeps changing for 5.6 s after launch while Preview
    /// settles in 1.4 s.
    /// </remarks>
    internal static readonly TimeSpan DefaultPrewarmIdleDelay = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger;
    private long _lastActivityTicks = Environment.TickCount64;
    private TimeSpan _prewarmIdleDelay = DefaultPrewarmIdleDelay;
    private readonly Dictionary<int, Task> _loadTasks = new();
    private readonly object _loadLock = new();
    private readonly HashSet<int> _visibleIndices = new();
    private readonly object _viewportLock = new();

    private ThumbnailCacheService? _cache;
    private long _generation;
    private bool _windowPassScheduled;
    private CancellationTokenSource? _prefetchCancellation;
    private CancellationTokenSource? _prewarmCancellation;
    private bool _disposed;

    internal ThumbnailSidebarSession(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The quiet period the pre-warm waits for before it starts (#1565).
    /// </summary>
    /// <remarks>
    /// <para>This is the <c>IdleTrimSeconds</c> preference, pushed in by
    /// <see cref="MainWindowViewModel.ApplyPerformanceSettings"/>. There is
    /// deliberately ONE definition of idle in the app rather than a second
    /// number nobody can find: lowering "Trim caches after (seconds)" also
    /// makes thumbnails warm sooner, and raising it holds them back longer.
    /// It is a duration, so it applies whether or not soft cache trims are on
    /// — a user who turned trimming off did not ask for the pre-warm to run
    /// during their first page.</para>
    /// <para>Changing it re-queues a pending pre-warm, so the new period is in
    /// force at once rather than after the old one expires. Tests that must
    /// observe the pre-warm running set a short one.</para>
    /// </remarks>
    internal TimeSpan PrewarmIdleDelay
    {
        get => _prewarmIdleDelay;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value.Ticks, nameof(value));
            if (_prewarmIdleDelay == value)
                return;
            _prewarmIdleDelay = value;
            if (_cache is { } cache && PrewarmEnabled)
                QueuePrewarm(cache);
        }
    }

    internal ObservableCollection<PageThumbnail> Items { get; } = new();
    internal Task? PrefetchTask { get; private set; }
    internal Task? PrewarmTask { get; private set; }

    private bool _prewarmEnabled = true;
    private int _keepMarginPages = KeepMargin;

    /// <summary>
    /// Background pre-render of every thumbnail. Applies to the open document
    /// at once: turning it off cancels a running pre-render, turning it on
    /// starts one when a document is open.
    /// </summary>
    internal bool PrewarmEnabled
    {
        get => _prewarmEnabled;
        set
        {
            if (_prewarmEnabled == value)
                return;
            _prewarmEnabled = value;
            if (!value)
            {
                CancelAndDispose(ref _prewarmCancellation);
                PrewarmTask = null;
            }
            else if (_cache != null)
            {
                QueuePrewarm(_cache);
            }
        }
    }

    /// <summary>
    /// Thumbnails kept in memory either side of the visible ones (default
    /// <see cref="KeepMargin"/>). Lowering it evicts on the next window pass,
    /// which this setter schedules.
    /// </summary>
    internal int KeepMarginPages
    {
        get => _keepMarginPages;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, PrefetchMargin);
            if (_keepMarginPages == value)
                return;
            _keepMarginPages = value;
            lock (_viewportLock)
            {
                if (_visibleIndices.Count == 0 || _windowPassScheduled)
                    return;
                _windowPassScheduled = true;
            }
            Dispatcher.UIThread.Post(RunWindowPass, DispatcherPriority.Background);
        }
    }
    internal long GenerationForTests => Volatile.Read(ref _generation);

    /// <summary>
    /// Renderer invocations this document's thumbnail cache has made (#1565);
    /// 0 while the pre-warm is still waiting for a quiet period.
    /// </summary>
    internal int ThumbnailRenderCountForTests => _cache?.RenderCount ?? 0;

    /// <summary>Where this document's thumbnail WebPs are written (#1565 tests).</summary>
    internal string? ThumbnailCacheDirForTests => _cache?.CacheDir;

    internal static (int PrefetchFrom, int PrefetchTo, int KeepFrom, int KeepTo) ComputeWindow(
        int visibleMin,
        int visibleMax,
        int pageCount,
        int prefetchMargin = PrefetchMargin,
        int keepMargin = KeepMargin)
    {
        if (pageCount <= 0 || visibleMin > visibleMax)
            return (0, -1, 0, -1);

        var prefetchFrom = Math.Max(0, visibleMin - prefetchMargin);
        var prefetchTo = Math.Min(pageCount - 1, visibleMax + prefetchMargin);
        var keepFrom = Math.Max(0, visibleMin - keepMargin);
        var keepTo = Math.Min(pageCount - 1, visibleMax + keepMargin);
        return (prefetchFrom, prefetchTo, keepFrom, keepTo);
    }

    internal void Start(
        string filePath,
        PdfDocument document,
        int pageCount,
        Action<PageThumbnail>? configureItem = null,
        string? cacheSalt = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);

        Reset();
        NotifyActivity();
        _cache = new ThumbnailCacheService(
            filePath,
            document,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            cacheSalt: cacheSalt);
        AppMetrics.RegisterThumbnailCache(_cache);

        for (var index = 0; index < pageCount; index++)
        {
            var item = new PageThumbnail
            {
                PageNumber = index + 1,
                PageIndex = index,
            };
            configureItem?.Invoke(item);
            Items.Add(item);
        }

        _logger.LogInformation(
            "Created {Count} thumbnail placeholders; loads happen on demand",
            pageCount);
        QueuePrewarm(_cache);
    }

    internal void Reset()
    {
        Interlocked.Increment(ref _generation);
        lock (_loadLock)
        {
            _loadTasks.Clear();
        }

        CancelAndDispose(ref _prefetchCancellation);
        CancelAndDispose(ref _prewarmCancellation);
        PrefetchTask = null;
        PrewarmTask = null;

        lock (_viewportLock)
        {
            _visibleIndices.Clear();
            _windowPassScheduled = false;
        }

        if (_cache != null)
            AppMetrics.RetireThumbnailCache(_cache);
        _cache?.Dispose();
        _cache = null;
        ClearItems();
    }

    internal void NotifyViewport(int pageIndex, bool isVisible)
    {
        NotifyActivity();
        lock (_viewportLock)
        {
            var changed = isVisible
                ? _visibleIndices.Add(pageIndex)
                : _visibleIndices.Remove(pageIndex);
            if (!changed || _windowPassScheduled)
                return;
            _windowPassScheduled = true;
        }

        Dispatcher.UIThread.Post(RunWindowPass, DispatcherPriority.Background);
    }

    /// <summary>
    /// #1478 memory pressure: release the sidebar's thumbnail bitmaps outside
    /// the visible pages plus <paramref name="margin"/>. The disk cache stays,
    /// so a released thumbnail comes back from a WebP decode, not a render.
    /// With nothing visible (the sidebar is collapsed) every bitmap goes.
    /// UI thread: <see cref="EvictOutside"/> moves each binding before it posts
    /// the dispose (#1466).
    /// </summary>
    internal void TrimToVisible(int margin)
    {
        int visibleMin, visibleMax;
        lock (_viewportLock)
        {
            if (_visibleIndices.Count == 0)
            {
                EvictOutside(0, -1);
                return;
            }
            visibleMin = _visibleIndices.Min();
            visibleMax = _visibleIndices.Max();
        }

        var (_, _, keepFrom, keepTo) = ComputeWindow(
            visibleMin, visibleMax, Items.Count, prefetchMargin: margin, keepMargin: margin);
        EvictOutside(keepFrom, keepTo);
    }

    internal async Task EnsureLoadedAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        if (pageIndex < 0 || pageIndex >= Items.Count)
            return;

        var cache = _cache;
        if (cache is null || Items[pageIndex].ThumbnailImage is not null)
            return;

        var generation = Volatile.Read(ref _generation);
        Task loadTask;
        lock (_loadLock)
        {
            if (Items[pageIndex].ThumbnailImage is not null)
                return;

            if (!_loadTasks.TryGetValue(pageIndex, out loadTask!))
            {
                // A demand load is the sidebar filling in for the reader: the
                // pre-warm's quiet period starts again (#1565).
                NotifyActivity();
                loadTask = LoadCoreAsync(pageIndex, generation, cache, cancellationToken);
                _loadTasks[pageIndex] = loadTask;
                _ = loadTask.ContinueWith(
                    _ => RemoveCompletedLoad(pageIndex, loadTask),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        try
        {
            await loadTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected when a viewport item or document session is replaced.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Reset();
        _disposed = true;
    }

    private void RunWindowPass()
    {
        int visibleMin;
        int visibleMax;
        lock (_viewportLock)
        {
            _windowPassScheduled = false;
            if (_visibleIndices.Count == 0)
                return;
            visibleMin = _visibleIndices.Min();
            visibleMax = _visibleIndices.Max();
        }

        var (prefetchFrom, prefetchTo, keepFrom, keepTo) =
            ComputeWindow(visibleMin, visibleMax, Items.Count, keepMargin: _keepMarginPages);
        if (prefetchTo < prefetchFrom)
            return;

        EvictOutside(keepFrom, keepTo);

        var toLoad = Enumerable.Range(prefetchFrom, prefetchTo - prefetchFrom + 1)
            .Where(index => Items[index].ThumbnailImage is null)
            .ToList();
        if (toLoad.Count == 0)
            return;

        var center = (visibleMin + visibleMax) / 2;
        toLoad.Sort((left, right) =>
            Math.Abs(left - center).CompareTo(Math.Abs(right - center)));

        CancelAndDispose(ref _prefetchCancellation);
        var cancellation = new CancellationTokenSource();
        _prefetchCancellation = cancellation;
        PrefetchTask = PrefetchAsync(toLoad, cancellation.Token);
    }

    private void EvictOutside(int keepFrom, int keepTo)
    {
        List<global::Avalonia.Media.Imaging.Bitmap>? evicted = null;
        for (var index = 0; index < Items.Count; index++)
        {
            if (index >= keepFrom && index <= keepTo)
                continue;

            var item = Items[index];
            if (item.ThumbnailImage is not { } bitmap)
                continue;
            item.ThumbnailImage = null;
            (evicted ??= []).Add(bitmap);
        }

        if (evicted is null)
            return;

        _logger.LogDebug(
            "Thumbnail eviction released {Count} bitmaps outside keep window [{From},{To}]",
            evicted.Count,
            keepFrom,
            keepTo);
        Dispatcher.UIThread.Post(
            () => DisposeBitmaps(evicted),
            DispatcherPriority.Background);
    }

    private async Task PrefetchAsync(IReadOnlyList<int> indices, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var index in indices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await EnsureLoadedAsync(index, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The viewport moved or the document session changed.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Thumbnail prefetch chain stopped");
        }
    }

    private void QueuePrewarm(ThumbnailCacheService cache)
    {
        CancelAndDispose(ref _prewarmCancellation);
        if (!PrewarmEnabled)
            return;

        var cancellation = new CancellationTokenSource();
        _prewarmCancellation = cancellation;
        var cancellationToken = cancellation.Token;
        var pageCount = Items.Count;
        var generation = Volatile.Read(ref _generation);
        PrewarmTask = Task.Run(async () =>
        {
            try
            {
                await WaitForQuietAsync(cancellationToken);
                _logger.LogInformation(
                    "Thumbnail pre-warm starting for {Pages} pages", pageCount);

                for (var index = 0; index < pageCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (generation != Volatile.Read(ref _generation))
                        return;

                    while (HasDemandLoads())
                        await Task.Delay(50, cancellationToken);

                    if (index < Items.Count && Items[index].ThumbnailImage is not null)
                        continue;

                    // Warm, not load: the pixels were disposed immediately and
                    // producing them cost three native bitmaps per page (#1565).
                    await cache.WarmAsync(index, cancellationToken);
                    await Task.Delay(25, cancellationToken);
                }

                _logger.LogInformation(
                    "Thumbnail pre-warm complete: {Pages} pages cached",
                    pageCount);
            }
            catch (OperationCanceledException)
            {
                // The document session changed or closed.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Thumbnail pre-warm stopped");
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Wait until nothing has touched this document for
    /// <see cref="DefaultPrewarmIdleDelay"/> (#1565).
    /// </summary>
    /// <remarks>
    /// One wake-up per activity burst, not a poll: each <c>Task.Delay</c> is
    /// exactly the time still owed, and the loop ends as soon as that time has
    /// passed with no further activity. Nothing is armed once it returns, so an
    /// idle app is doing no work here (the #1462 guarantee).
    /// </remarks>
    private async Task WaitForQuietAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owed = _prewarmIdleDelay
                       - TimeSpan.FromMilliseconds(
                           Environment.TickCount64 - Volatile.Read(ref _lastActivityTicks));
            if (owed <= TimeSpan.Zero)
                return;
            await Task.Delay(owed, cancellationToken);
        }
    }

    /// <summary>
    /// Something is using this document, so the pre-warm's quiet period starts
    /// again (#1565). Cheap and thread-safe: the view model calls it from page
    /// changes, zoom changes and search-index progress.
    /// </summary>
    internal void NotifyActivity() =>
        Volatile.Write(ref _lastActivityTicks, Environment.TickCount64);

    private async Task LoadCoreAsync(
        int pageIndex,
        long generation,
        ThumbnailCacheService cache,
        CancellationToken cancellationToken)
    {
        try
        {
            if (pageIndex < 0 || pageIndex >= Items.Count)
                return;
            var item = Items[pageIndex];
            if (item.ThumbnailImage is not null)
                return;

            using var skBitmap = await cache.GetThumbnailAsync(pageIndex, cancellationToken);
            if (skBitmap is null)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested
                    || generation != Volatile.Read(ref _generation)
                    || pageIndex < 0
                    || pageIndex >= Items.Count
                    || !ReferenceEquals(item, Items[pageIndex])
                    || item.ThumbnailImage is not null)
                {
                    return;
                }

                item.ThumbnailImage = Excise.Avalonia.Imaging.SkiaInterop.ToAvaloniaBitmap(skBitmap);
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
            // Expected when the item scrolls away or the document changes.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Thumbnail load failed for page {Page}", pageIndex);
        }
    }

    private void RemoveCompletedLoad(int pageIndex, Task completedTask)
    {
        lock (_loadLock)
        {
            if (_loadTasks.TryGetValue(pageIndex, out var current)
                && ReferenceEquals(current, completedTask))
            {
                _loadTasks.Remove(pageIndex);
            }
        }
    }

    private bool HasDemandLoads()
    {
        lock (_loadLock)
        {
            return _loadTasks.Count > 0;
        }
    }

    private void ClearItems()
    {
        List<global::Avalonia.Media.Imaging.Bitmap>? bitmaps = null;
        foreach (var item in Items)
        {
            if (item.ThumbnailImage is not { } bitmap)
                continue;
            item.ThumbnailImage = null;
            (bitmaps ??= []).Add(bitmap);
        }
        Items.Clear();

        if (bitmaps is not null)
        {
            Dispatcher.UIThread.Post(
                () => DisposeBitmaps(bitmaps),
                DispatcherPriority.Background);
        }
    }

    private static void DisposeBitmaps(IEnumerable<global::Avalonia.Media.Imaging.Bitmap> bitmaps)
    {
        foreach (var bitmap in bitmaps)
        {
            try
            {
                bitmap.Dispose();
            }
            catch
            {
                // Best-effort cleanup after the binding has released the bitmap.
            }
        }
    }

    private static void CancelAndDispose(ref CancellationTokenSource? cancellation)
    {
        var owned = cancellation;
        cancellation = null;
        if (owned is null)
            return;
        owned.Cancel();
        owned.Dispose();
    }
}
