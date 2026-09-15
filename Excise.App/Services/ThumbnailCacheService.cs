using Microsoft.Extensions.Logging;
using Excise.Core.Document;
using Excise.Rendering;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Excise.App.Services;

/// <summary>
/// Renders PDF page thumbnails on demand and caches them to disk.
/// Re-opening the same PDF reloads thumbnails from a sub-millisecond
/// WebP decode rather than re-running the renderer; first-time opens
/// only render the pages the user actually looks at (the View triggers
/// loads via <see cref="EffectiveViewportChanged"/> on each item).
///
/// Cache layout: <c>{cacheRoot}/thumbnails/v3/{fileIdentity}/p{NNNNN}.webp</c>
/// Cache root is OS-conventional:
///   Linux:   $XDG_CACHE_HOME/excise (default $HOME/.cache/excise)
///   macOS:   $HOME/Library/Caches/excise
///   Windows: %LOCALAPPDATA%/excise/Cache
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

    // De-duplicates concurrent in-flight requests for the same page. Every
    // field of an InFlightThumbnail, and membership in this map, is guarded by
    // _lock.
    private readonly Dictionary<int, InFlightThumbnail> _inFlight = new();
    private readonly object _lock = new();

    // Test seam (#1467): a hook that sees each coalesced master bitmap just
    // before it is disposed.
    internal Action<SKBitmap>? MasterReleasingForTest { get; set; }

    /// <summary>
    /// One coalesced load, shared by every caller that asked for the same page
    /// while it was in flight. The master bitmap it produces is disposed exactly
    /// once, by whichever of <see cref="RetireInFlight"/> and
    /// <see cref="ReleaseAwaiter"/> runs last, and only when BOTH hold:
    /// <list type="bullet">
    /// <item><see cref="Retired"/> — the entry has left <see cref="_inFlight"/>,
    /// so no new caller can join; set only once <see cref="Master"/> has
    /// completed.</item>
    /// <item><see cref="Awaiters"/> is 0 — every caller that joined has finished
    /// copying the master (or given up) and dropped its reference.</item>
    /// </list>
    /// Both are read and written only under <c>_lock</c>, and
    /// <see cref="Released"/> makes the release one-shot.
    /// </summary>
    private sealed class InFlightThumbnail(Task<SKBitmap?> master)
    {
        public Task<SKBitmap?> Master { get; } = master;
        public int Awaiters;
        public bool Retired;
        public bool Released;
    }

    private readonly int _thumbnailDpi;
    private bool _disposed;

    // Test seam (#733): counts renderer invocations so tests can assert
    // "cache hit did not re-render" directly instead of via a flaky
    // wall-clock threshold. Interlocked because renders run on the pool.
    private int _renderCount;

    /// <summary>
    /// Number of times this instance invoked the renderer (as opposed to
    /// serving a disk-cache hit). Test observability (#733) and the
    /// <c>excise.app.thumbnail.renders</c> counter (#1491).
    /// </summary>
    internal int RenderCount => Volatile.Read(ref _renderCount);

    private static readonly string RendererCacheIdentity =
        typeof(SkiaRenderer).Module.ModuleVersionId.ToString("N");

    public ThumbnailCacheService(string pdfPath, PdfDocument doc, ILogger logger,
        int thumbnailDpi = 36,
        string? cacheSalt = null)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        _logger = logger;
        _thumbnailDpi = thumbnailDpi;
        var identity = BuildCacheIdentity(pdfPath, thumbnailDpi, RendererCacheIdentity, cacheSalt);
        var versionRoot = Path.Combine(AppPaths.ThumbnailCacheRoot, "thumbnails", "v3");
        _cacheDir = Path.Combine(versionRoot, identity);
        _logger.LogInformation("Thumbnail cache for {File} → {Dir}",
            Path.GetFileName(pdfPath), _cacheDir);

        // LRU trim (#690): the cache grows across every file ever opened, and
        // until now nothing ever deleted anything (reads touch mtimes for
        // exactly this). Best-effort, off the open path, never touching the
        // document we just opened.
        var protectDir = identity;
        // A LOCAL logger, not _logger: reading the field would capture `this`,
        // and with it _doc, for as long as the directory walk runs. That kept a
        // replaced document alive past the #1481 collection that was queued to
        // free it (measured: still reachable 300 ms after open, gone by 600 ms).
        var trimLogger = _logger;
        _ = Task.Run(() => TrimCacheRoot(versionRoot, DefaultCacheCapBytes, protectDir, trimLogger));
    }

    /// <summary>Disk budget for the whole thumbnail cache across all files (#690).</summary>
    internal const long DefaultCacheCapBytes = 500L * 1024 * 1024;

    /// <summary>
    /// Delete least-recently-used per-file cache directories until the version
    /// root is under <paramref name="capBytes"/> (#690). Recency is the newest
    /// last-access/mtime of any file in the directory — reads touch mtimes for
    /// exactly this purpose. <paramref name="protectDirName"/> (the currently
    /// open document) is never deleted. Best-effort: IO races with another
    /// instance are swallowed; a trimmed file simply re-renders on next open.
    /// </summary>
    internal static void TrimCacheRoot(string versionRoot, long capBytes, string? protectDirName, ILogger? logger = null)
    {
        try
        {
            if (!Directory.Exists(versionRoot)) return;

            var entries = new List<(string Dir, long Bytes, DateTime LastUsed)>();
            foreach (var dir in Directory.EnumerateDirectories(versionRoot))
            {
                long bytes = 0;
                var lastUsed = DateTime.MinValue;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        var info = new FileInfo(file);
                        bytes += info.Length;
                        var used = info.LastAccessTimeUtc > info.LastWriteTimeUtc
                            ? info.LastAccessTimeUtc : info.LastWriteTimeUtc;
                        if (used > lastUsed) lastUsed = used;
                    }
                }
                catch { continue; }
                entries.Add((dir, bytes, lastUsed));
            }

            var total = entries.Sum(e => e.Bytes);
            if (total <= capBytes) return;

            foreach (var entry in entries.OrderBy(e => e.LastUsed))
            {
                if (total <= capBytes) break;
                if (protectDirName != null &&
                    string.Equals(Path.GetFileName(entry.Dir), protectDirName, StringComparison.Ordinal))
                    continue;
                try
                {
                    Directory.Delete(entry.Dir, recursive: true);
                    total -= entry.Bytes;
                    logger?.LogInformation("Thumbnail cache trim: removed {Dir} ({Bytes} bytes)",
                        Path.GetFileName(entry.Dir), entry.Bytes);
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Thumbnail cache trim: could not remove {Dir}", entry.Dir);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Thumbnail cache trim failed (best-effort)");
        }
    }

    /// <summary>Path on disk where this document's thumbnails are stored.</summary>
    public string CacheDir => _cacheDir;

    /// <summary>
    /// Get the thumbnail for <paramref name="pageIndex"/> (zero-based).
    /// Returns from disk cache if present, otherwise renders and caches.
    /// Concurrent calls for the same page coalesce on a single in-flight
    /// Task to protect the renderer (and the disk cache) from duplicated
    /// work; <strong>each caller receives its own owned copy of the
    /// SKBitmap and is responsible for disposing it</strong>. Callers never
    /// see the shared master: handing it out would make every awaiter's
    /// `using`/Dispose race on the same handle, and SkiaSharp crashed on the
    /// second disposal (the "app ended unexpectedly while scrolling thumbnails"
    /// crash). The service owns the master and disposes it once the last
    /// joined caller has taken its copy (#1467) — see
    /// <see cref="InFlightThumbnail"/> for the rule.
    /// </summary>
    public async Task<SKBitmap?> GetThumbnailAsync(int pageIndex,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) return null;

        InFlightThumbnail entry;
        bool created = false;
        lock (_lock)
        {
            if (!_inFlight.TryGetValue(pageIndex, out entry!))
            {
                entry = new InFlightThumbnail(
                    Task.Run(() => LoadOrRender(pageIndex, cancellationToken), cancellationToken));
                _inFlight[pageIndex] = entry;
                created = true;
            }
            // Joining is only possible while the entry is in _inFlight, i.e.
            // before it is retired, so a retired entry's count never grows.
            entry.Awaiters++;
        }

        if (created)
        {
            // Registered after this caller has joined: if the load already
            // finished, the continuation runs inline here and sees Awaiters >= 1.
            _ = entry.Master.ContinueWith(
                _ => RetireInFlight(pageIndex, entry),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        try
        {
            var src = await entry.Master.WaitAsync(cancellationToken).ConfigureAwait(false);
            // The copy completes before the finally below drops this caller's
            // claim, so the master cannot be released while it is being read.
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
        finally
        {
            ReleaseAwaiter(entry);
        }
    }

    /// <summary>
    /// The load has completed: take the entry out of <see cref="_inFlight"/> so
    /// no further caller can join, then release the master if nobody still holds
    /// a claim on it. The single point of retirement — nothing else removes an
    /// entry.
    /// </summary>
    private void RetireInFlight(int pageIndex, InFlightThumbnail entry)
    {
        SKBitmap? toRelease;
        lock (_lock)
        {
            if (_inFlight.TryGetValue(pageIndex, out var current) && ReferenceEquals(current, entry))
                _inFlight.Remove(pageIndex);
            entry.Retired = true;
            toRelease = TakeMasterIfUnclaimed(entry);
        }
        ReleaseMaster(toRelease);
    }

    private void ReleaseAwaiter(InFlightThumbnail entry)
    {
        SKBitmap? toRelease;
        lock (_lock)
        {
            entry.Awaiters--;
            toRelease = TakeMasterIfUnclaimed(entry);
        }
        ReleaseMaster(toRelease);
    }

    // Caller holds _lock. Returns the master to dispose at most once over the
    // entry's life; null while it is still joinable or claimed, or when the load
    // produced no bitmap (cancelled, failed, or null).
    private static SKBitmap? TakeMasterIfUnclaimed(InFlightThumbnail entry)
    {
        if (!entry.Retired || entry.Awaiters > 0 || entry.Released)
            return null;
        entry.Released = true;
        return entry.Master.IsCompletedSuccessfully ? entry.Master.Result : null;
    }

    // Outside _lock: the entry is retired and unclaimed, so nothing else can
    // reach this bitmap any more.
    private void ReleaseMaster(SKBitmap? master)
    {
        if (master == null) return;
        MasterReleasingForTest?.Invoke(master);
        master.Dispose();
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
                Interlocked.Increment(ref _renderCount);
                // Pre-warm renders every page of the document exactly once, and
                // the result is cached as pixels here and on disk, so the image
                // samples it decodes are not needed again. Without the release
                // they stayed inflated in the document's object cache until
                // close — the main GUI retention driver after #1468's first
                // increment. The viewer's own renders keep them.
                var bmp = _renderer.RenderPage(page,
                    new RenderOptions { Dpi = _thumbnailDpi, ReleaseDecodedImageSamples = true });
                if (bmp == null) return null;

                // 3) Best-effort write to disk after returning the pixels to
                // the UI. WebP encoding can dominate first-visible-thumbnail
                // latency, while cache persistence is only an optimization.
                QueueCacheWrite(cachePath, bmp);
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
        // No _inFlight removal here (#1467): this ran BEFORE the task completed
        // and removed the entry by page number, a second retirement point next
        // to RetireInFlight. The master-release rule needs exactly one.
    }

    // The write task gets its own copy, taken synchronously before the master is
    // published to any caller, and disposes it itself — so it never aliases the
    // master or a caller's copy.
    private void QueueCacheWrite(string path, SKBitmap bmp)
    {
        var cacheBitmap = bmp.Copy();
        if (cacheBitmap == null)
            return;

        _ = Task.Run(() =>
        {
            using (cacheBitmap)
            {
                TryWriteCache(path, cacheBitmap);
            }
        });
    }

    private void TryWriteCache(string path, SKBitmap bmp)
    {
        string? tempPath = null;
        try
        {
            Directory.CreateDirectory(_cacheDir);
            using var img = SKImage.FromBitmap(bmp);
            // WebP @ 90 quality is ~10× smaller than PNG for thumbnails
            // and visually identical at 36 DPI display sizes.
            using var data = img.Encode(SKEncodedImageFormat.Webp, 90);
            // Write a sibling temp file and rename it into place. File.Create on the
            // final path makes the name visible before the bytes are written, so a
            // concurrent reader (another service instance, a second window, or a
            // test waiting for the file) could decode a truncated WebP, delete it and
            // re-render. A same-directory rename is atomic. See issue #1474.
            tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            using (var fs = File.Create(tempPath))
                data.SaveTo(fs);
            File.Move(tempPath, path, overwrite: true);
            tempPath = null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write thumbnail cache {Path}", path);
        }
        finally
        {
            if (tempPath != null)
            {
                try { File.Delete(tempPath); } catch { }
            }
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
    /// SHA-256 of cheap file identity plus render-affecting cache salt,
    /// truncated to 16 hex chars. This intentionally avoids hashing the full
    /// PDF during document open; large books should not block startup just to
    /// choose a thumbnail cache directory.
    /// </summary>
    private static string BuildCacheIdentity(
        string path,
        int thumbnailDpi,
        string rendererCacheIdentity,
        string? cacheSalt)
    {
        var info = new FileInfo(path);
        var identity = string.Join('\n',
            $"thumbnail-dpi={thumbnailDpi}",
            $"renderer={rendererCacheIdentity}",
            $"cache-salt={cacheSalt ?? string.Empty}",
            $"path={Path.GetFullPath(path)}",
            $"length={(info.Exists ? info.Length : 0)}",
            $"last-write-utc={(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }
}
