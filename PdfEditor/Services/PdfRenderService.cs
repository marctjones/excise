using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Microsoft.Extensions.Logging;
using Pdfe.Core.Document;
using Pdfe.Rendering;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PdfEditor.Services;

/// <summary>
/// Service for rendering PDF pages to images
/// Uses Pdfe.Rendering (SkiaSharp-based renderer)
/// </summary>
public class PdfRenderService
{
    private readonly ILogger<PdfRenderService> _logger;
    private int _maxCacheEntries = 20;
    private long _maxCacheMemoryBytes = 100 * 1024 * 1024; // 100 MB default
    private long _cacheHits;
    private long _cacheMisses;
    private long _currentCacheBytes;

    // Cache SKBitmap data directly as PNG bytes
    private record RenderCacheEntry(byte[] PngData, DateTime LastAccessUtc, long SizeBytes);
    private readonly ConcurrentDictionary<string, RenderCacheEntry> _cache = new();

    public PdfRenderService(ILogger<PdfRenderService> logger)
    {
        _logger = logger;
        _logger.LogDebug("PdfRenderService instance created with max cache: {Entries} entries, {MemoryMB} MB",
            _maxCacheEntries, _maxCacheMemoryBytes / (1024 * 1024));
    }

    /// <summary>
    /// Maximum number of cached render entries. Defaults to 20.
    /// </summary>
    public int MaxCacheEntries
    {
        get => _maxCacheEntries;
        set => _maxCacheEntries = Math.Max(1, value);
    }

    /// <summary>
    /// Maximum cache memory in bytes. Defaults to 100 MB.
    /// </summary>
    public long MaxCacheMemoryBytes
    {
        get => _maxCacheMemoryBytes;
        set => _maxCacheMemoryBytes = Math.Max(1024 * 1024, value); // Minimum 1 MB
    }

    /// <summary>
    /// Clear the render cache (force re-render on next request).
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
        System.Threading.Interlocked.Exchange(ref _currentCacheBytes, 0);
        _logger.LogInformation("Cache cleared");
    }

    /// <summary>
    /// Get cache statistics (current entries, max entries, hits, misses, current bytes, max bytes).
    /// </summary>
    public CacheStatistics GetCacheStats() => new(
        Count: _cache.Count,
        MaxEntries: _maxCacheEntries,
        Hits: _cacheHits,
        Misses: _cacheMisses,
        CurrentBytes: _currentCacheBytes,
        MaxBytes: _maxCacheMemoryBytes,
        HitRate: _cacheHits + _cacheMisses > 0 ? (double)_cacheHits / (_cacheHits + _cacheMisses) : 0
    );

    /// <summary>
    /// Log current cache statistics at Info level.
    /// </summary>
    public void LogCacheStats()
    {
        var stats = GetCacheStats();
        _logger.LogInformation(
            "Cache stats: {Count}/{MaxEntries} entries, {CurrentMB:F1}/{MaxMB:F1} MB, " +
            "hit rate: {HitRate:P1} ({Hits} hits, {Misses} misses)",
            stats.Count, stats.MaxEntries,
            stats.CurrentBytes / (1024.0 * 1024.0), stats.MaxBytes / (1024.0 * 1024.0),
            stats.HitRate, stats.Hits, stats.Misses);
    }

    public record CacheStatistics(
        int Count,
        int MaxEntries,
        long Hits,
        long Misses,
        long CurrentBytes,
        long MaxBytes,
        double HitRate);

    /// <summary>
    /// Render a specific page of a PDF to a bitmap
    /// </summary>
    public virtual async Task<SKBitmap?> RenderPageAsync(
        string pdfPath,
        int pageIndex,
        int dpi = 150,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cacheKey = BuildCacheKey(pdfPath, pageIndex, dpi);
        if (TryGetFromCache(cacheKey, out var cachedPngData))
        {
            _logger.LogDebug("Cache hit for {File} page {Page} @ {Dpi} DPI", Path.GetFileName(pdfPath), pageIndex, dpi);
            if (cachedPngData != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = new MemoryStream(cachedPngData);
                return SKBitmap.Decode(stream); // Decode from cached PNG data
            }
        }

        _logger.LogInformation("Rendering page {PageIndex} from {FileName} at {Dpi} DPI",
            pageIndex, Path.GetFileName(pdfPath), dpi);

        var sw = Stopwatch.StartNew();

        return await Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogDebug("Opening PDF file stream");
                using var fileStream = File.OpenRead(pdfPath);

                var skBitmap = RenderPageFromStream(fileStream, pageIndex, dpi, sw, cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch
                {
                    skBitmap?.Dispose();
                    throw;
                }
                if (skBitmap != null)
                {
                    // Cache the SKBitmap as PNG bytes
                    using var image = SKImage.FromBitmap(skBitmap);
                    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                    AddToCache(cacheKey, data.ToArray());
                }
                return skBitmap;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                sw.Stop();
                _logger.LogDebug("Canceled rendering page {PageIndex} from {FileName} after {ElapsedMs}ms",
                    pageIndex, Path.GetFileName(pdfPath), sw.ElapsedMilliseconds);
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "Error rendering page {PageIndex} from {FileName} after {ElapsedMs}ms",
                    pageIndex, Path.GetFileName(pdfPath), sw.ElapsedMilliseconds);
                return null;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Render a specific page from a PDF stream (for in-memory documents)
    /// </summary>
    public virtual async Task<SKBitmap?> RenderPageFromStreamAsync(
        Stream pdfStream,
        int pageIndex,
        int dpi = 150,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation("Rendering page {PageIndex} from stream at {Dpi} DPI", pageIndex, dpi);
        var sw = Stopwatch.StartNew();

        return await Task.Run(
            () => RenderPageFromStream(pdfStream, pageIndex, dpi, sw, cancellationToken),
            cancellationToken);
    }

    private SKBitmap? RenderPageFromStream(
        Stream pdfStream,
        int pageIndex,
        int dpi,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogDebug("Creating RenderOptions with DPI: {Dpi}", dpi);
            var options = new RenderOptions { Dpi = dpi };

            _logger.LogDebug("Opening PDF document from stream");
            using var pdfDoc = PdfDocument.Open(pdfStream, ownsStream: false);
            cancellationToken.ThrowIfCancellationRequested();

            // pageIndex is 0-based, but GetPage expects 1-based
            var page = pdfDoc.GetPage(pageIndex + 1);

            _logger.LogDebug("Rendering page with Pdfe.Rendering.SkiaRenderer");
            var renderer = new SkiaRenderer();
            // NOTE: Do NOT use 'using' here - the caller is responsible for disposing the returned bitmap
            var skBitmap = renderer.RenderPage(page, options, cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                skBitmap?.Dispose();
                throw;
            }

            if (skBitmap == null)
            {
                _logger.LogWarning("Rendering returned null for page {PageIndex}", pageIndex);
                return null;
            }

            _logger.LogDebug("SKBitmap created: {Width}x{Height}", skBitmap.Width, skBitmap.Height);

            sw.Stop();
            _logger.LogInformation("Page {PageIndex} rendered successfully in {ElapsedMs}ms",
                pageIndex, sw.ElapsedMilliseconds);

            return skBitmap;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            _logger.LogDebug("Canceled rendering page {PageIndex} from stream after {ElapsedMs}ms",
                pageIndex, sw.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Error rendering page {PageIndex} from stream after {ElapsedMs}ms",
                pageIndex, sw.ElapsedMilliseconds);
            return null;
        }
    }

    /// <summary>
    /// Render a page as a thumbnail
    /// </summary>
    public virtual async Task<SKBitmap?> RenderThumbnailAsync(
        string pdfPath,
        int pageIndex,
        int width = 200,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Rendering thumbnail for page {PageIndex}, target width: {Width}", pageIndex, width);

        // Calculate DPI for thumbnail - lower DPI for faster rendering
        int thumbnailDpi = 72; // Standard screen DPI
        return await RenderPageAsync(pdfPath, pageIndex, thumbnailDpi, cancellationToken);
    }
    
    /// <summary>
    /// Get page dimensions without rendering
    /// </summary>
    public (double Width, double Height) GetPageDimensions(string pdfPath, int pageIndex)
    {
        _logger.LogDebug("Getting dimensions for page {PageIndex}", pageIndex);

        try
        {
            using var pdfDoc = PdfDocument.Open(pdfPath);
            // pageIndex is 0-based, GetPage expects 1-based
            var page = pdfDoc.GetPage(pageIndex + 1);

            _logger.LogDebug("Page dimensions: {Width}x{Height}", page.Width, page.Height);
            return (page.Width, page.Height);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get page dimensions for page {PageIndex}, using default", pageIndex);
            return (612, 792); // Default Letter size in points
        }
    }

    private string BuildCacheKey(string pdfPath, int pageIndex, int dpi)
    {
        var lastWrite = File.Exists(pdfPath) ? File.GetLastWriteTimeUtc(pdfPath).Ticks : 0;
        return $"{pdfPath}|{pageIndex}|{dpi}|{lastWrite}";
    }

    private bool TryGetFromCache(string key, out byte[]? pngData)
    {
        pngData = null;

        if (_cache.TryGetValue(key, out var entry))
        {
            pngData = entry.PngData;
            _cache[key] = entry with { LastAccessUtc = DateTime.UtcNow };
            System.Threading.Interlocked.Increment(ref _cacheHits);
            return true;
        }

        System.Threading.Interlocked.Increment(ref _cacheMisses);
        return false;
    }

    private void AddToCache(string key, byte[] pngData)
    {
        long entrySize = pngData.Length;

        // Update entry (or add new one)
        if (_cache.TryGetValue(key, out var existingEntry))
        {
            // Entry exists - update it and adjust size
            System.Threading.Interlocked.Add(ref _currentCacheBytes, entrySize - existingEntry.SizeBytes);
            _cache[key] = new RenderCacheEntry(pngData, DateTime.UtcNow, entrySize);
        }
        else
        {
            // New entry
            _cache[key] = new RenderCacheEntry(pngData, DateTime.UtcNow, entrySize);
            System.Threading.Interlocked.Add(ref _currentCacheBytes, entrySize);
        }

        // Evict entries if over limits (entry count OR memory)
        while (_cache.Count > _maxCacheEntries ||
               System.Threading.Interlocked.Read(ref _currentCacheBytes) > _maxCacheMemoryBytes)
        {
            if (!EvictOldestEntry())
                break; // No more entries to evict
        }
    }

    private bool EvictOldestEntry()
    {
        var oldest = DateTime.MaxValue;
        string? oldestKey = null;

        foreach (var kvp in _cache)
        {
            if (kvp.Value.LastAccessUtc < oldest)
            {
                oldest = kvp.Value.LastAccessUtc;
                oldestKey = kvp.Key;
            }
        }

        if (oldestKey != null && _cache.TryRemove(oldestKey, out var evicted))
        {
            System.Threading.Interlocked.Add(ref _currentCacheBytes, -evicted.SizeBytes);
            _logger.LogDebug("Evicted cache entry: {Key}, freed {SizeKB:F1} KB",
                oldestKey, evicted.SizeBytes / 1024.0);
            return true;
        }

        return false;
    }
}
