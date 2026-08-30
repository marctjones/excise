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

    private readonly ILogger _logger;
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

    internal ObservableCollection<PageThumbnail> Items { get; } = new();
    internal Task? PrefetchTask { get; private set; }
    internal Task? PrewarmTask { get; private set; }
    internal bool PrewarmEnabled { get; set; } = true;
    internal long GenerationForTests => Volatile.Read(ref _generation);

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
        _cache = new ThumbnailCacheService(
            filePath,
            document,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            cacheSalt: cacheSalt);

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

        _cache?.Dispose();
        _cache = null;
        ClearItems();
    }

    internal void NotifyViewport(int pageIndex, bool isVisible)
    {
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
            ComputeWindow(visibleMin, visibleMax, Items.Count);
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
                for (var index = 0; index < pageCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (generation != Volatile.Read(ref _generation))
                        return;

                    while (HasDemandLoads())
                        await Task.Delay(50, cancellationToken);

                    if (index < Items.Count && Items[index].ThumbnailImage is not null)
                        continue;

                    using var bitmap = await cache.GetThumbnailAsync(index, cancellationToken);
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
