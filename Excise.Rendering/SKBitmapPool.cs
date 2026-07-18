using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace Excise.Rendering;

/// <summary>
/// Thread-safe pool for reusing SKBitmap instances.
/// Reduces GC pressure during thumbnail scrolling and repeated renders.
/// </summary>
public class SKBitmapPool : IDisposable
{
    private readonly object _lockObj = new object();
    private readonly Dictionary<(int width, int height), Queue<PooledBitmap>> _pool = new();
    private readonly LinkedList<PooledBitmap> _allBitmaps = new();

    private const int MaxBitmapsPerSize = 4;
    private const long MaxTotalMemoryBytes = 200 * 1024 * 1024;
    private long _currentMemoryUsage = 0;
    private volatile bool _disposed = false;

    /// <summary>
    /// Rent a bitmap of the specified dimensions.
    /// </summary>
    public SKBitmap Rent(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Width and height must be positive.");
        if (_disposed)
            throw new ObjectDisposedException(nameof(SKBitmapPool));

        lock (_lockObj)
        {
            var key = (width, height);
            if (_pool.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                var pooledBitmap = queue.Dequeue();
                return pooledBitmap.Bitmap;
            }

            var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            var memorySize = width * height * 4;
            _currentMemoryUsage += memorySize;

            var pooledEntry = new PooledBitmap { Bitmap = bitmap, SizeInBytes = memorySize };
            _allBitmaps.AddLast(pooledEntry);
            // _allBitmaps.Last is non-null right after AddLast, even though the
            // property is typed nullable — bang-suppress the false-positive.
            pooledEntry.Node = _allBitmaps.Last!;

            while (_currentMemoryUsage > MaxTotalMemoryBytes && _allBitmaps.Count > 0)
            {
                var eldest = _allBitmaps.First;
                if (eldest != null && eldest.Value.Node == eldest)
                {
                    _allBitmaps.RemoveFirst();
                    _currentMemoryUsage -= eldest.Value.SizeInBytes;
                    eldest.Value.Bitmap.Dispose();
                }
            }

            return bitmap;
        }
    }

    /// <summary>
    /// Return a bitmap to the pool.
    /// </summary>
    public void Return(SKBitmap bitmap)
    {
        if (bitmap == null)
            throw new ArgumentNullException(nameof(bitmap));

        if (_disposed)
        {
            bitmap.Dispose();
            return;
        }

        lock (_lockObj)
        {
            var key = (bitmap.Width, bitmap.Height);

            if (!_pool.TryGetValue(key, out var queue))
            {
                queue = new Queue<PooledBitmap>();
                _pool[key] = queue;
            }

            if (queue.Count >= MaxBitmapsPerSize)
            {
                bitmap.Dispose();
                return;
            }

            var memorySize = bitmap.Width * bitmap.Height * 4;
            var pooledEntry = new PooledBitmap { Bitmap = bitmap, SizeInBytes = memorySize };
            queue.Enqueue(pooledEntry);

            if (pooledEntry.Node != null)
            {
                _allBitmaps.Remove(pooledEntry.Node);
            }
            pooledEntry.Node = _allBitmaps.AddLast(pooledEntry);
        }
    }

    /// <summary>
    /// Dispose all pooled bitmaps and clear the pool.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_lockObj)
        {
            _disposed = true;

            foreach (var bitmap in _allBitmaps)
            {
                bitmap.Bitmap?.Dispose();
            }

            _pool.Clear();
            _allBitmaps.Clear();
            _currentMemoryUsage = 0;
        }
    }

    /// <summary>
    /// Get current memory usage in bytes.
    /// </summary>
    public long CurrentMemoryUsage
    {
        get
        {
            lock (_lockObj)
            {
                return _currentMemoryUsage;
            }
        }
    }

    /// <summary>
    /// Get the number of pooled bitmaps for a specific size.
    /// </summary>
    public int GetPoolCountForSize(int width, int height)
    {
        lock (_lockObj)
        {
            var key = (width, height);
            return _pool.TryGetValue(key, out var queue) ? queue.Count : 0;
        }
    }

    /// <summary>
    /// Get total count of pooled bitmaps across all sizes.
    /// </summary>
    public int GetTotalPoolCount()
    {
        lock (_lockObj)
        {
            return _pool.Values.Sum(q => q.Count);
        }
    }

    /// <summary>
    /// Internal class to track bitmap metadata for pooling.
    /// </summary>
    private class PooledBitmap
    {
        // Bitmap and Node are always populated immediately after construction
        // through the object-initializer / AddLast pair above; null! suppresses
        // the CS8618 "uninitialized non-nullable" warning.
        public SKBitmap Bitmap { get; set; } = null!;
        public long SizeInBytes { get; set; }
        public LinkedListNode<PooledBitmap> Node { get; set; } = null!;
    }
}
