using Microsoft.Extensions.Logging;
using Pdfe.Core.Document;
using Pdfe.Rendering;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PdfEditor.Services;

/// <summary>
/// Renders PDF page thumbnails on demand and caches them to disk.
/// Re-opening the same PDF reloads thumbnails from a sub-millisecond
/// WebP decode rather than re-running the renderer; first-time opens
/// only render the pages the user actually looks at (the View triggers
/// loads via <see cref="EffectiveViewportChanged"/> on each item).
///
/// Cache layout: <c>{cacheRoot}/thumbnails/v2/{contentHash}/p{NNNNN}.webp</c>
/// Cache root is OS-conventional:
///   Linux:   $XDG_CACHE_HOME/pdfe (default $HOME/.cache/pdfe)
///   macOS:   $HOME/Library/Caches/pdfe
///   Windows: %LOCALAPPDATA%/pdfe/Cache
/// </summary>
public sealed class ThumbnailCacheService : IDisposable
{
    private readonly PdfDocument _doc;
    private readonly SkiaRenderer _renderer = new();
    private readonly ILogger _logger;
    private readonly string _cacheDir;

    // Renders are serialised on a single SemaphoreSlim because the
    // underlying PdfDocument's parser holds shared lexer state — two
    // concurrent GetPage calls would corrupt it. Disk-cache hits skip
    // this gate entirely so the common path stays fast.
    private readonly SemaphoreSlim _renderGate = new(1, 1);

    // De-duplicates concurrent in-flight requests for the same page.
    private readonly Dictionary<int, Task<SKBitmap?>> _inFlight = new();
    private readonly object _lock = new();

    private readonly int _thumbnailDpi;
    private bool _disposed;

    private static readonly string RendererCacheIdentity =
        typeof(SkiaRenderer).Module.ModuleVersionId.ToString("N");

    public ThumbnailCacheService(string pdfPath, PdfDocument doc, ILogger logger,
        int thumbnailDpi = 36)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        _logger = logger;
        _thumbnailDpi = thumbnailDpi;
        var hash = HashFile(pdfPath, thumbnailDpi, RendererCacheIdentity);
        _cacheDir = Path.Combine(GetCacheRoot(), "thumbnails", "v2", hash);
        _logger.LogInformation("Thumbnail cache for {File} → {Dir}",
            Path.GetFileName(pdfPath), _cacheDir);
    }

    /// <summary>Path on disk where this document's thumbnails are stored.</summary>
    public string CacheDir => _cacheDir;

    /// <summary>
    /// Get the thumbnail for <paramref name="pageIndex"/> (zero-based).
    /// Returns from disk cache if present, otherwise renders and caches.
    /// Concurrent calls for the same page coalesce on a single in-flight
    /// Task to protect the renderer (and the disk cache) from duplicated
    /// work; <strong>each caller receives its own owned copy of the
    /// SKBitmap and is responsible for disposing it</strong>. The master
    /// instance behind the Task is allowed to fall out of scope and be
    /// finalised — sharing it would mean every awaiter's `using`/Dispose
    /// would race on the same handle and crash SkiaSharp on the second
    /// disposal (this was the cause of the "app ended unexpectedly while
    /// scrolling thumbnails" crash).
    /// </summary>
    public async Task<SKBitmap?> GetThumbnailAsync(int pageIndex,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) return null;

        Task<SKBitmap?> master;
        lock (_lock)
        {
            if (!_inFlight.TryGetValue(pageIndex, out master!))
            {
                master = Task.Run(() => LoadOrRender(pageIndex, cancellationToken),
                    cancellationToken);
                _inFlight[pageIndex] = master;
                _ = master.ContinueWith(
                    _ =>
                    {
                        lock (_lock)
                        {
                            if (_inFlight.TryGetValue(pageIndex, out var current) &&
                                ReferenceEquals(current, master))
                            {
                                _inFlight.Remove(pageIndex);
                            }
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        // Hand each caller a freshly-copied SKBitmap so disposes don't
        // alias. The master result will be GC'd / finalised once the
        // last reference (this Task chain) is dropped.
        try
        {
            var src = await master.WaitAsync(cancellationToken).ConfigureAwait(false);
            return src?.Copy();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Thumbnail task failed for page {Page}", pageIndex);
            return null;
        }
    }

    private SKBitmap? LoadOrRender(int pageIndex, CancellationToken ct)
    {
        try
        {
            // 1) Disk cache hit. Common path on re-open.
            var cachePath = CachePathFor(pageIndex);
            if (File.Exists(cachePath))
            {
                try
                {
                    using var fs = File.OpenRead(cachePath);
                    var loaded = SKBitmap.Decode(fs);
                    if (loaded != null)
                    {
                        // Touch the file's mtime so a future LRU eviction
                        // sees it as recently used.
                        try { File.SetLastAccessTimeUtc(cachePath, DateTime.UtcNow); } catch { }
                        return loaded;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Cache decode failed for page {Page}; will re-render", pageIndex);
                    try { File.Delete(cachePath); } catch { }
                }
            }

            // 2) Render (serialised — see _renderGate comment).
            ct.ThrowIfCancellationRequested();
            _renderGate.Wait(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (pageIndex < 0 || pageIndex >= _doc.PageCount) return null;
                var page = _doc.GetPage(pageIndex + 1);
                var bmp = _renderer.RenderPage(page,
                    new RenderOptions { Dpi = _thumbnailDpi });
                if (bmp == null) return null;

                // 3) Best-effort write to disk. Failure here shouldn't
                // break thumbnail display.
                TryWriteCache(cachePath, bmp);
                return bmp;
            }
            finally { _renderGate.Release(); }
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Thumbnail render failed for page {Page}", pageIndex);
            return null;
        }
        finally
        {
            lock (_lock) { _inFlight.Remove(pageIndex); }
        }
    }

    private void TryWriteCache(string path, SKBitmap bmp)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            using var img = SKImage.FromBitmap(bmp);
            // WebP @ 90 quality is ~10× smaller than PNG for thumbnails
            // and visually identical at 36 DPI display sizes.
            using var data = img.Encode(SKEncodedImageFormat.Webp, 90);
            using var fs = File.Create(path);
            data.SaveTo(fs);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write thumbnail cache {Path}", path);
        }
    }

    private string CachePathFor(int pageIndex) =>
        Path.Combine(_cacheDir, $"p{pageIndex:D5}.webp");

    public void Dispose()
    {
        _disposed = true;
        _renderGate.Dispose();
    }

    // --- Helpers ---

    /// <summary>
    /// SHA-256 of file content plus render-affecting cache salt, truncated to
    /// 16 hex chars. Moving the file or duplicating it doesn't invalidate the
    /// cache, but renderer/options changes do.
    /// </summary>
    private static string HashFile(string path, int thumbnailDpi, string rendererCacheIdentity)
    {
        using var sha = SHA256.Create();
        var salt = Encoding.UTF8.GetBytes($"thumbnail-dpi={thumbnailDpi}\nrenderer={rendererCacheIdentity}\n");
        sha.TransformBlock(salt, 0, salt.Length, null, 0);
        using var fs = File.OpenRead(path);
        var buffer = new byte[81920];
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            sha.TransformBlock(buffer, 0, read, null, 0);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var bytes = sha.Hash ?? Array.Empty<byte>();
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// OS-appropriate cache root. .NET doesn't have a SpecialFolder for
    /// "user cache directory" so we follow each platform's convention
    /// directly.
    /// </summary>
    private static string GetCacheRoot()
    {
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Caches", "pdfe");
        }
        if (OperatingSystem.IsLinux())
        {
            // XDG Base Directory: $XDG_CACHE_HOME or $HOME/.cache.
            var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (!string.IsNullOrEmpty(xdg))
                return Path.Combine(xdg, "pdfe");
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache", "pdfe");
        }
        // Windows (and the fallback): %LOCALAPPDATA%/pdfe/Cache.
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "pdfe", "Cache");
    }
}
