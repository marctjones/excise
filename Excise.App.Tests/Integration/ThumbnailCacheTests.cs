using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.Core.Document;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Xunit;
namespace Excise.App.Tests.Integration;

/// <summary>
/// Pin the contract for the disk-backed thumbnail cache: first call
/// renders + writes to disk, second call (and re-instantiations of
/// the service over the same file) load from disk without rendering.
/// The cache root comes from AppPaths.ThumbnailCacheRoot, which the
/// assembly-wide test override points at a temp directory. (These tests used
/// to set XDG_CACHE_HOME, which only the Linux branch read — on macOS they
/// wrote into the real ~/Library/Caches/excise.) Each test opens a fresh
/// GUID-named PDF, so its cache identity starts empty.
/// </summary>
public class ThumbnailCacheTests
{
    private readonly ITestOutputHelper _out;
    public ThumbnailCacheTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public async Task FirstCallRenders_SecondCallLoadsFromDisk()
    {
        var pdfPath = Path.Combine(Path.GetTempPath(), $"excise-thumb-cache-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 4);

        try
        {
            using var doc = PdfDocument.Open(pdfPath);

            // First service instance — populates the cache from scratch.
            using (var svc1 = new ThumbnailCacheService(pdfPath, doc, NullLogger.Instance))
            {
                var sw = Stopwatch.StartNew();
                using var bmp = await svc1.GetThumbnailAsync(2); // page 3
                sw.Stop();
                _out.WriteLine($"first render: {sw.ElapsedMilliseconds} ms");

                bmp.Should().NotBeNull("first call must produce a thumbnail");
                bmp!.Width.Should().BeGreaterThan(50);
                svc1.RenderCount.Should().Be(1,
                    "the first call over an empty cache must render the page");

                var cacheFile = await WaitForCacheFileAsync(svc1.CacheDir, "p00002.webp");
                cacheFile.Should().NotBeNull(
                    $"cache file should be written asynchronously under {svc1.CacheDir}");
            }

            // Second service instance over the same file — the same hash
            // dir is already populated, so this must NOT need to render.
            // Asserted via the renderer-invocation counter, not wall-clock:
            // an absolute timing threshold raced render time on loaded CI
            // runners and flaked (#733). The counter asserts the intent
            // ("no re-render") deterministically.
            using (var svc2 = new ThumbnailCacheService(pdfPath, doc, NullLogger.Instance))
            {
                var sw = Stopwatch.StartNew();
                using var bmp = await svc2.GetThumbnailAsync(2);
                sw.Stop();
                _out.WriteLine($"second (cache hit): {sw.ElapsedMilliseconds} ms");

                bmp.Should().NotBeNull();
                bmp!.Width.Should().BeGreaterThan(50);
                svc2.RenderCount.Should().Be(0,
                    "the second instance must serve the thumbnail from the " +
                    "disk cache without invoking the renderer (#733)");
            }
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(pdfPath);
        }
    }

    [Fact]
    public async Task ConcurrentRequestsForSamePage_AllCallersDisposeIndependently()
    {
        // Regression for the "app ends unexpectedly while scrolling
        // thumbnails" crash. Pre-fix the cache service shared a single
        // SKBitmap across every awaiter of an in-flight Task; multiple
        // callers each running `using var sk = await GetThumbnailAsync(...)`
        // would race their Dispose calls on the same handle and SkiaSharp
        // would segfault on the second disposal. The fix gives each
        // caller a freshly-copied SKBitmap.
        var pdfPath = Path.Combine(Path.GetTempPath(), $"excise-thumb-cache-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 2);

        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            using var svc = new ThumbnailCacheService(pdfPath, doc, NullLogger.Instance);

            // Eight concurrent same-page requests — pre-fix every awaiter
            // got the same SKBitmap reference, so their Dispose calls
            // raced. Now every awaiter gets its own copy.
            var tasks = new Task<SkiaSharp.SKBitmap?>[8];
            for (int i = 0; i < tasks.Length; i++)
                tasks[i] = svc.GetThumbnailAsync(0);

            var results = await Task.WhenAll(tasks);

            // Every result must be a distinct, disposable instance.
            var seen = new HashSet<SkiaSharp.SKBitmap>();
            foreach (var r in results)
            {
                r.Should().NotBeNull();
                seen.Add(r!).Should().BeTrue(
                    "every caller must receive its own SKBitmap, not a shared reference");
            }

            // Disposing all of them in turn must not throw — pre-fix this
            // would crash on the second Dispose.
            foreach (var b in results) b!.Dispose();
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(pdfPath);
        }
    }

    [Fact]
    public async Task ConcurrentRequestsForSamePage_CoalesceOnSingleTask()
    {
        var pdfPath = Path.Combine(Path.GetTempPath(), $"excise-thumb-cache-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 2);

        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            using var svc = new ThumbnailCacheService(pdfPath, doc, NullLogger.Instance);

            // Fire 8 concurrent requests for the same page. The service's
            // _inFlight dedupe table should make all 8 await one underlying
            // load — this is the contract that protects the renderer (which
            // is single-threaded against shared parser state).
            var tasks = new Task<SkiaSharp.SKBitmap?>[8];
            for (int i = 0; i < tasks.Length; i++)
                tasks[i] = svc.GetThumbnailAsync(0);

            var results = await Task.WhenAll(tasks);
            foreach (var r in results) r.Should().NotBeNull();

            // The real check: only one render ran. "One cache file" alone
            // does not distinguish coalescing from 8 independent renders
            // that all happen to write the same page — a non-coalescing
            // implementation produces that too.
            svc.RenderCount.Should().Be(1,
                "8 concurrent requests for the same page must dedupe onto a single render");

            var cacheFile = await WaitForCacheFileAsync(svc.CacheDir, "p*.webp");
            cacheFile.Should().NotBeNull();
            Directory.GetFiles(svc.CacheDir, "p*.webp").Length.Should().Be(1);

            foreach (var b in results) b?.Dispose();
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(pdfPath);
        }
    }

    /// <summary>
    /// #1565: the sidebar pre-warm wants the WebP on disk and no bitmap at all.
    /// Going through <c>GetThumbnailAsync</c> cost three native bitmaps per
    /// page (master + caller copy + cache-write copy) and, on a re-open,
    /// decoded every cached WebP only to throw the pixels away.
    /// </summary>
    [Fact]
    public async Task WarmAsync_WritesTheCacheSynchronously_AndSkipsPagesAlreadyOnDisk()
    {
        var pdfPath = Path.Combine(Path.GetTempPath(), $"excise-thumb-warm-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 3);

        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            string cacheDir;
            using (var svc = new ThumbnailCacheService(pdfPath, doc, NullLogger.Instance))
            {
                await svc.WarmAsync(1);

                svc.RenderCount.Should().Be(1, "a cold page is rendered once");
                cacheDir = svc.CacheDir;
                // Synchronously, unlike QueueCacheWrite: a warm has no caller
                // waiting on pixels, so it encodes before it returns.
                File.Exists(Path.Combine(cacheDir, "p00001.webp")).Should().BeTrue(
                    "the warm's whole product is the file on disk");

                await svc.WarmAsync(1);
                svc.RenderCount.Should().Be(1, "a page already on disk is not rendered again");
            }

            using (var reopened = new ThumbnailCacheService(pdfPath, doc, NullLogger.Instance))
            {
                await reopened.WarmAsync(1);
                reopened.RenderCount.Should().Be(0,
                    "a second open warms nothing: the WebP is there and is not even decoded");

                using var bmp = await reopened.GetThumbnailAsync(1);
                bmp.Should().NotBeNull("and the warmed file still serves a real thumbnail");
                reopened.RenderCount.Should().Be(0);
            }
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(pdfPath);
        }
    }

    [Fact]
    public async Task WarmAsync_IgnoresPagesOutsideTheDocument()
    {
        var pdfPath = Path.Combine(Path.GetTempPath(), $"excise-thumb-warm-oob-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 2);
        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            using var svc = new ThumbnailCacheService(pdfPath, doc, NullLogger.Instance);

            await svc.WarmAsync(-1);
            await svc.WarmAsync(2);

            svc.RenderCount.Should().Be(0);
            (Directory.Exists(svc.CacheDir)
                ? Directory.GetFiles(svc.CacheDir, "*.webp")
                : Array.Empty<string>()).Should().BeEmpty();
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(pdfPath);
        }
    }

    private static async Task<string?> WaitForCacheFileAsync(string cacheDir, string fileName)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            var files = Directory.Exists(cacheDir)
                ? Directory.GetFiles(cacheDir, fileName, SearchOption.AllDirectories)
                : Array.Empty<string>();
            if (files.Length > 0)
                return files[0];

            await Task.Delay(25);
        }

        return null;
    }
}
