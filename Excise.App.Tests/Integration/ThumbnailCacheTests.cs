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
/// Uses a temp cache dir via XDG_CACHE_HOME so tests don't pollute
/// the user's real cache directory.
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

        // Sandbox the cache dir for this test so we don't fight other
        // runs or pollute ~/.cache/excise.
        var tempCache = Path.Combine(Path.GetTempPath(),
            "excise-thumb-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempCache);
        var prevXdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", tempCache);
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

                var cacheFile = await WaitForCacheFileAsync(svc1.CacheDir, "p00002.webp");
                cacheFile.Should().NotBeNull(
                    $"cache file should be written asynchronously under {svc1.CacheDir}");
            }

            // Second service instance over the same file — the same hash
            // dir is already populated, so this must NOT need to render.
            using (var svc2 = new ThumbnailCacheService(pdfPath, doc, NullLogger.Instance))
            {
                var sw = Stopwatch.StartNew();
                using var bmp = await svc2.GetThumbnailAsync(2);
                sw.Stop();
                _out.WriteLine($"second (cache hit): {sw.ElapsedMilliseconds} ms");

                bmp.Should().NotBeNull();
                bmp!.Width.Should().BeGreaterThan(50);

                // Cache hit should be at least an order of magnitude faster
                // than rendering. Ballpark: render ~50-200ms, decode ~1-10ms.
                // Keep the threshold generous so test isn't flaky.
                sw.ElapsedMilliseconds.Should().BeLessThan(100,
                    "cache hit should be a sub-100ms WebP decode rather than " +
                    "a full re-render of the page");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", prevXdg);
            try { Directory.Delete(tempCache, recursive: true); } catch { }
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

        var tempCache = Path.Combine(Path.GetTempPath(),
            "excise-thumb-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempCache);
        var prevXdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", tempCache);
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
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", prevXdg);
            try { Directory.Delete(tempCache, recursive: true); } catch { }
            TestPdfGenerator.CleanupTestFile(pdfPath);
        }
    }

    [Fact]
    public async Task ConcurrentRequestsForSamePage_CoalesceOnSingleTask()
    {
        var pdfPath = Path.Combine(Path.GetTempPath(), $"excise-thumb-cache-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 2);

        var tempCache = Path.Combine(Path.GetTempPath(),
            "excise-thumb-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempCache);
        var prevXdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", tempCache);
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

            // Only one cache file should have been written (the others
            // coalesced onto the same load).
            var cacheFile = await WaitForCacheFileAsync(svc.CacheDir, "p*.webp");
            cacheFile.Should().NotBeNull();
            Directory.GetFiles(svc.CacheDir, "p*.webp").Length.Should().Be(1);

            foreach (var b in results) b?.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", prevXdg);
            try { Directory.Delete(tempCache, recursive: true); } catch { }
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
