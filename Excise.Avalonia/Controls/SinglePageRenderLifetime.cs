using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;

namespace Excise.Avalonia.Controls;

/// <summary>
/// Owns the cancellable render generation and bounded LRU for single-page
/// viewer bitmaps. Navigation and display binding deliberately remain in
/// <see cref="PdfViewerControl"/>; this type owns only resource lifetime.
/// </summary>
internal sealed class SinglePageRenderLifetime<TBitmap> : IDisposable
    where TBitmap : class, IDisposable
{
    private readonly object _gate = new();
    private readonly int _cacheCapacity;
    private readonly LinkedList<CacheEntry> _cache = new();
    private CancellationTokenSource? _activeRenderSource;
    private long _activeGeneration;
    private bool _disposed;

    internal SinglePageRenderLifetime(int cacheCapacity)
    {
        if (cacheCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(cacheCapacity));

        _cacheCapacity = cacheCapacity;
    }

    /// <summary>
    /// Starts a new render generation, cancelling and disposing the source for
    /// any previous generation. The returned lease lets a completion prove it
    /// still belongs to the current request before it publishes UI state.
    /// </summary>
    internal RenderLease BeginRender()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            CancelActiveRenderNoLock();

            _activeRenderSource = new CancellationTokenSource();
            _activeGeneration++;
            return new RenderLease(this, _activeGeneration, _activeRenderSource.Token);
        }
    }

    /// <summary>
    /// Supersedes the current render without starting another one. This is used
    /// by document invalidation, detach, and cache hits: all three make an older
    /// in-flight completion stale even though no replacement render is needed.
    /// </summary>
    internal void CancelRender()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            CancelActiveRenderNoLock();
            _activeGeneration++;
        }
    }

    internal bool TryGet(int pageNumber, int dpi, out TBitmap? bitmap, out Size dipSize)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            for (var node = _cache.First; node != null; node = node.Next)
            {
                if (node.Value.PageNumber != pageNumber || node.Value.Dpi != dpi)
                    continue;

                _cache.Remove(node);
                _cache.AddFirst(node);
                bitmap = node.Value.Bitmap;
                dipSize = node.Value.DipSize;
                return true;
            }

            bitmap = null;
            dipSize = default;
            return false;
        }
    }

    /// <summary>
    /// Transfers ownership of <paramref name="bitmap"/> to the cache. Replaced
    /// and evicted entries are disposed immediately.
    /// </summary>
    internal void Add(int pageNumber, int dpi, TBitmap bitmap, Size dipSize)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        lock (_gate)
        {
            ThrowIfDisposed();
            for (var node = _cache.First; node != null; node = node.Next)
            {
                if (node.Value.PageNumber != pageNumber || node.Value.Dpi != dpi)
                    continue;

                node.Value.Bitmap.Dispose();
                _cache.Remove(node);
                break;
            }

            _cache.AddFirst(new CacheEntry(pageNumber, dpi, bitmap, dipSize));
            while (_cache.Count > _cacheCapacity)
            {
                var last = _cache.Last!;
                _cache.RemoveLast();
                last.Value.Bitmap.Dispose();
            }
        }
    }

    internal void InvalidateCache()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            DisposeCacheNoLock();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            CancelActiveRenderNoLock();
            _activeGeneration++;
            DisposeCacheNoLock();
        }
    }

    private bool IsCurrent(long generation)
    {
        lock (_gate)
        {
            return !_disposed
                && _activeRenderSource != null
                && _activeGeneration == generation;
        }
    }

    private void Complete(long generation)
    {
        lock (_gate)
        {
            if (_activeRenderSource == null || _activeGeneration != generation)
                return;

            _activeRenderSource.Dispose();
            _activeRenderSource = null;
        }
    }

    private void CancelActiveRenderNoLock()
    {
        if (_activeRenderSource == null)
            return;

        _activeRenderSource.Cancel();
        _activeRenderSource.Dispose();
        _activeRenderSource = null;
    }

    private void DisposeCacheNoLock()
    {
        foreach (var entry in _cache)
            entry.Bitmap.Dispose();
        _cache.Clear();
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record CacheEntry(int PageNumber, int Dpi, TBitmap Bitmap, Size DipSize);

    internal sealed class RenderLease : IDisposable
    {
        private readonly SinglePageRenderLifetime<TBitmap> _owner;
        private readonly long _generation;
        private bool _disposed;

        internal RenderLease(
            SinglePageRenderLifetime<TBitmap> owner,
            long generation,
            CancellationToken token)
        {
            _owner = owner;
            _generation = generation;
            Token = token;
        }

        internal CancellationToken Token { get; }

        internal bool IsCurrent => !_disposed && _owner.IsCurrent(_generation);

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _owner.Complete(_generation);
        }
    }
}
