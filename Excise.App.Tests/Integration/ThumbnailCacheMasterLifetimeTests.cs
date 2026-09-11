using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.Core.Document;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace Excise.App.Tests.Integration;

/// <summary>
/// #1467: the master <see cref="SKBitmap"/> behind a coalesced thumbnail load is
/// owned by <see cref="ThumbnailCacheService"/> and disposed exactly once, after
/// every caller that joined the load has taken its own copy — instead of being
/// left to the finalizer. Sharing that master is what crashed the app while
/// scrolling thumbnails (#363), so the copies must stay independent of it.
/// </summary>
public class ThumbnailCacheMasterLifetimeTests
{
    [Fact]
    public async Task CoalescedAwaiters_EachGetAnIndependentCopy_AndTheMasterIsDisposedExactlyOnce()
    {
        await using var fixture = await ThumbnailFixture.CreateAsync();
        var svc = fixture.Service;

        var tasks = Enumerable.Range(0, 8).Select(_ => svc.GetThumbnailAsync(0)).ToArray();
        var results = await Task.WhenAll(tasks);
        fixture.Owned.AddRange(results.OfType<SKBitmap>());

        svc.RenderCount.Should().Be(1,
            "fixture: the eight requests must coalesce onto one load, or no master is shared");

        await fixture.WaitForReleasesAsync(1);
        fixture.ReleasedCount.Should().Be(1, "the shared master is released exactly once");
        var master = fixture.SingleReleasedMaster();
        master.Handle.Should().Be(IntPtr.Zero, "the released master has been disposed");

        results.Should().OnlyContain(r => r != null);
        results.Distinct().Should().HaveCount(results.Length, "every caller owns its own bitmap");
        results.Should().OnlyContain(r => !ReferenceEquals(r, master), "no caller is handed the master");

        // The copies outlive the master: readable, and identical to each other.
        var expected = results[0]!.Bytes;
        expected.Should().NotBeEmpty();
        foreach (var r in results)
        {
            r!.Handle.Should().NotBe(IntPtr.Zero);
            r.Bytes.AsSpan().SequenceEqual(expected).Should().BeTrue(
                "each copy carries the master's pixels, not a dangling view of them");
        }
    }

    [Fact]
    public async Task AnAwaiterThatGivesUp_NeitherLeaksNorDoubleReleasesTheMaster()
    {
        await using var fixture = await ThumbnailFixture.CreateAsync();
        var svc = fixture.Service;

        var keeper = svc.GetThumbnailAsync(0);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var quitter = svc.GetThumbnailAsync(0, cancelled.Token);

        (await quitter).Should().BeNull("a caller whose token is cancelled gets nothing");
        var kept = await keeper;
        kept.Should().NotBeNull();
        fixture.Owned.Add(kept!);

        await fixture.WaitForReleasesAsync(1);
        fixture.ReleasedCount.Should().Be(1,
            "the caller that gave up dropped its claim; the master is still released once, not twice or never");
        fixture.SingleReleasedMaster().Handle.Should().Be(IntPtr.Zero);

        kept!.Handle.Should().NotBe(IntPtr.Zero);
        kept.Bytes.Should().NotBeEmpty("the surviving caller's copy is independent of the released master");
    }

    private sealed class ThumbnailFixture : IAsyncDisposable
    {
        private readonly string _pdfPath;
        private readonly PdfDocument _doc;
        private readonly List<SKBitmap> _released = new();

        private ThumbnailFixture(string pdfPath, PdfDocument doc, ThumbnailCacheService service)
        {
            _pdfPath = pdfPath;
            _doc = doc;
            Service = service;
            Service.MasterReleasingForTest = m => { lock (_released) _released.Add(m); };
        }

        public ThumbnailCacheService Service { get; }
        public List<SKBitmap> Owned { get; } = new();

        /// <summary>
        /// Masters the service has released, as seen by the hook production
        /// invokes immediately before each disposal (ReleaseMaster).
        /// </summary>
        public int ReleasedCount
        {
            get { lock (_released) return _released.Count; }
        }

        public static Task<ThumbnailFixture> CreateAsync()
        {
            var pdfPath = Path.Combine(Path.GetTempPath(), $"excise-thumb-master-{Guid.NewGuid():N}.pdf");
            TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 2);
            var doc = PdfDocument.Open(pdfPath);
            // A unique salt gives this instance an empty cache directory, so the
            // first request renders (a slow, coalescable load) rather than
            // decoding a WebP left behind by another run.
            var service = new ThumbnailCacheService(pdfPath, doc, NullLogger.Instance,
                cacheSalt: Guid.NewGuid().ToString("N"));
            return Task.FromResult(new ThumbnailFixture(pdfPath, doc, service));
        }

        public async Task WaitForReleasesAsync(int count)
        {
            // Retirement runs as a task continuation, which the runtime may queue
            // rather than inline, so allow it a moment after the awaiters return.
            var sw = Stopwatch.StartNew();
            while (ReleasedCount < count && sw.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(10);
            // Settle, so a (wrong) second release has the chance to show up.
            await Task.Delay(100);
        }

        public SKBitmap SingleReleasedMaster()
        {
            lock (_released)
            {
                _released.Should().ContainSingle();
                return _released[0];
            }
        }

        public ValueTask DisposeAsync()
        {
            foreach (var b in Owned) b.Dispose();
            var cacheDir = Service.CacheDir;
            Service.Dispose();
            _doc.Dispose();
            try { if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true); } catch { }
            TestPdfGenerator.CleanupTestFile(_pdfPath);
            return ValueTask.CompletedTask;
        }
    }
}
