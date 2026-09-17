using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using PdfCoreDocument = Excise.Core.Document.PdfDocument;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1565: the background pre-render of every thumbnail waits for a quiet
/// period instead of starting with the document.
/// </summary>
/// <remarks>
/// <para>Why this is a gate and not a preference: on a 126-page document the
/// pre-warm held a CPU busy for 5.0 s from the moment the document opened, and
/// #1544 measured the window still changing 5.6 s after launch (Preview:
/// 1.4 s). It also cost ~110 MB of peak footprint while the reader was waiting
/// for the first page.</para>
/// <para>The timing assertions are one-sided on purpose. A quiet period can
/// only be reached LATE under CPU contention, never early, so "no renders yet"
/// cannot flake — whereas "renders by now" would. Anything that must observe
/// the pre-warm running awaits its task with a long timeout instead of
/// sleeping.</para>
/// </remarks>
[Collection("AvaloniaTests")]
public class ThumbnailPrewarmIdleGateTests
{
    private static readonly TimeSpan Gate = TimeSpan.FromMilliseconds(1500);

    private static string NewPdf(int pages)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-prewarm-gate-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pages);
        return path;
    }

    private static ThumbnailSidebarSession StartedSession(string path, out PdfCoreDocument document)
    {
        var session = new ThumbnailSidebarSession(NullLogger.Instance)
        {
            PrewarmIdleDelay = Gate,
        };
        document = PdfCoreDocument.Open(File.ReadAllBytes(path));
        // A fresh salt per test: every run must start with a cold disk cache,
        // or a previous run's WebPs would make the render count 0 by accident.
        session.Start(path, document, document.PageCount,
            cacheSalt: "prewarm-gate-" + Guid.NewGuid().ToString("N"));
        return session;
    }

    [Fact(Timeout = 180_000)]
    public async Task Prewarm_RendersNothing_UntilTheDocumentHasBeenQuiet()
    {
        var path = NewPdf(8);
        var session = StartedSession(path, out var document);
        try
        {
            session.ThumbnailRenderCountForTests.Should().Be(0,
                "the pre-warm must not render while the document is still opening");

            // Less than the quiet period: the gate cannot have opened yet.
            await Task.Delay(TimeSpan.FromMilliseconds(600));
            session.ThumbnailRenderCountForTests.Should().Be(0,
                "the quiet period had not elapsed");

            session.PrewarmTask.Should().NotBeNull();
            await session.PrewarmTask!.WaitAsync(TimeSpan.FromSeconds(120));

            session.ThumbnailRenderCountForTests.Should().BeGreaterThan(0,
                "once the document is quiet the pre-warm still warms every page");
            Directory.GetFiles(ReadCacheDir(session), "*.webp").Should().HaveCount(document.PageCount,
                "the pre-warm's product is the disk cache");
        }
        finally
        {
            Cleanup(session, document, path);
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task Prewarm_StartsTheQuietPeriodAgain_WhenTheDocumentIsUsed()
    {
        var path = NewPdf(8);
        var session = StartedSession(path, out var document);
        try
        {
            // A wider gate than the other tests use: this one re-arms from a
            // loop, so the margin that matters is how long a stall between one
            // Task.Delay ending and the next NotifyActivity may last on a
            // loaded machine. 3 s against 400 ms spacing tolerates 2.6 s.
            session.PrewarmIdleDelay = TimeSpan.FromSeconds(3);
            session.NotifyActivity();
            // Use the document steadily for longer than the quiet period. Each
            // call re-arms it, so the pre-warm must still not have started.
            for (var i = 0; i < 8; i++)
            {
                session.NotifyActivity();
                await Task.Delay(TimeSpan.FromMilliseconds(400));
            }

            session.ThumbnailRenderCountForTests.Should().Be(0,
                "activity restarts the quiet period, so a reader who keeps working "
                + "never has 126 background page renders started underneath them");

            await session.PrewarmTask!.WaitAsync(TimeSpan.FromSeconds(120));
            session.ThumbnailRenderCountForTests.Should().BeGreaterThan(0,
                "and it does run once they stop");
        }
        finally
        {
            Cleanup(session, document, path);
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task Prewarm_LeavesNoBitmapsInSidebarMemory_AndReRunsNothingAlreadyCached()
    {
        var path = NewPdf(6);
        var salt = "prewarm-gate-" + Guid.NewGuid().ToString("N");
        using var document = PdfCoreDocument.Open(File.ReadAllBytes(path));
        try
        {
            string cacheDir;
            using (var first = new ThumbnailSidebarSession(NullLogger.Instance) { PrewarmIdleDelay = Gate })
            {
                first.Start(path, document, document.PageCount, cacheSalt: salt);
                await first.PrewarmTask!.WaitAsync(TimeSpan.FromSeconds(120));
                first.ThumbnailRenderCountForTests.Should().Be(document.PageCount);
                first.Items.Should().OnlyContain(item => item.ThumbnailImage == null,
                    "a warm produces a WebP on disk and no bitmap anywhere (#689)");
                cacheDir = ReadCacheDir(first);
            }

            using var second = new ThumbnailSidebarSession(NullLogger.Instance) { PrewarmIdleDelay = Gate };
            second.Start(path, document, document.PageCount, cacheSalt: salt);
            await second.PrewarmTask!.WaitAsync(TimeSpan.FromSeconds(120));

            second.ThumbnailRenderCountForTests.Should().Be(0,
                "a second open finds every page on disk and renders none of them");
            Directory.GetFiles(cacheDir, "*.webp").Should().HaveCount(document.PageCount);
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    [FixedAvaloniaFact(Timeout = 180_000)]
    public async Task OpeningADocument_StartsNoThumbnailRenders()
    {
        var path = NewPdf(10);
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: true);
        try
        {
            vm.ThumbnailPrewarmIdleDelay = TimeSpan.FromSeconds(30);
            await vm.LoadDocumentAsync(path);

            vm.ThumbnailPrewarmTask.Should().NotBeNull(
                "the pre-warm is queued with the document, only not started");
            vm.ThumbnailPrewarmTask!.IsCompleted.Should().BeFalse();
            vm.ThumbnailRenderCountForTests.Should().Be(0,
                "opening a document must cost no background thumbnail renders (#1565)");
        }
        finally
        {
            vm.ApplyPerformanceSettings(Excise.App.Models.PerformanceSettings.LowMemory);
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    private static string ReadCacheDir(ThumbnailSidebarSession session) =>
        session.ThumbnailCacheDirForTests
        ?? throw new InvalidOperationException("the session has no thumbnail cache");

    private static void Cleanup(ThumbnailSidebarSession session, PdfCoreDocument document, string path)
    {
        session.PrewarmEnabled = false;
        session.Dispose();
        document.Dispose();
        TestPdfGenerator.CleanupTestFile(path);
    }
}
