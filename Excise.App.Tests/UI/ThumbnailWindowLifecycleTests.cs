using System;
using System.IO;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Thumbnail viewport-window lifecycle (#687 eviction, #688 prefetch,
/// #689 cache-only pre-warm, #690 disk trim). The window math is pure; the
/// lifecycle tests drive the real VM the way the View does — via
/// <c>NotifyThumbnailViewport</c> / <c>EnsureThumbnailLoadedAsync</c>.
/// </summary>
[Collection("AvaloniaTests")]
public class ThumbnailWindowLifecycleTests
{
    // ── #687/#688: pure window math ─────────────────────────────────────────

    [Theory]
    // visMin, visMax, pages, expected: pf, pt, kf, kt (margins 12 / 48)
    [InlineData(0, 5, 100, 0, 17, 0, 53)]      // top of the doc: clamped at 0
    [InlineData(55, 59, 60, 43, 59, 7, 59)]    // bottom of the doc: clamped at end
    [InlineData(50, 55, 200, 38, 67, 2, 103)]  // middle: symmetric margins
    [InlineData(0, 0, 1, 0, 0, 0, 0)]          // single page
    public void ComputeThumbnailWindow_ClampsToDocument(
        int visMin, int visMax, int pages, int pf, int pt, int kf, int kt)
        => MainWindowViewModel.ComputeThumbnailWindow(visMin, visMax, pages)
            .Should().Be((pf, pt, kf, kt));

    [Fact]
    public void ComputeThumbnailWindow_EmptyInputs_ProduceEmptyWindows()
    {
        MainWindowViewModel.ComputeThumbnailWindow(3, 2, 100).Should().Be((0, -1, 0, -1));
        MainWindowViewModel.ComputeThumbnailWindow(0, 5, 0).Should().Be((0, -1, 0, -1));
    }

    [Fact]
    public void KeepMargin_ExceedsPrefetchMargin_ForHysteresis()
        => MainWindowViewModel.ThumbnailKeepMargin.Should().BeGreaterThan(
            MainWindowViewModel.ThumbnailPrefetchMargin * 2,
            "eviction must lag prefetch by enough that boundary jitter cannot thrash load/evict cycles");

    // ── #687 + #688 + #689: end-to-end against the real VM ──────────────────

    [FixedAvaloniaFact(Timeout = 180000)]
    public async Task ScrollFarAway_EvictsDistantThumbnails_AndPrefetchesNearby()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-thumb-window-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 60);
        var vm = new MainWindowViewModel { ThumbnailPrewarmEnabled = false };
        try
        {
            await vm.LoadDocumentAsync(path);
            vm.PageThumbnails.Count.Should().Be(60);

            // The user looks at the top of the sidebar.
            for (int i = 0; i <= 5; i++)
            {
                vm.NotifyThumbnailViewport(i, isVisible: true);
                await vm.EnsureThumbnailLoadedAsync(i);
            }
            vm.PageThumbnails[0].ThumbnailImage.Should().NotBeNull();

            // …then jumps to the bottom (55–59 visible, top scrolled out).
            for (int i = 0; i <= 5; i++) vm.NotifyThumbnailViewport(i, isVisible: false);
            for (int i = 55; i <= 59; i++) vm.NotifyThumbnailViewport(i, isVisible: true);

            // Let the coalesced Background-priority window pass (and its
            // prefetch chain) run to completion.
            await FlushDispatcherAsync();
            if (vm.ThumbnailPrefetchTask is { } prefetch)
                await prefetch.WaitAsync(TimeSpan.FromSeconds(60));
            await FlushDispatcherAsync();

            // #687: pages 0..5 are outside keep window [7,59] → bitmaps released.
            for (int i = 0; i <= 5; i++)
                vm.PageThumbnails[i].ThumbnailImage.Should().BeNull(
                    $"page {i} is outside the keep window and its bitmap must be released");

            // #688: the prefetch window [43,59] is fully loaded — including
            // pages never reported visible.
            for (int i = 43; i <= 59; i++)
                vm.PageThumbnails[i].ThumbnailImage.Should().NotBeNull(
                    $"page {i} is inside the prefetch window and should be loaded ahead of scrolling");

            // Scrolling back re-populates instantly from the disk cache.
            await vm.EnsureThumbnailLoadedAsync(0);
            vm.PageThumbnails[0].ThumbnailImage.Should().NotBeNull();
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    [FixedAvaloniaFact(Timeout = 180000)]
    public async Task Prewarm_PopulatesDiskCacheOnly_NeverSidebarMemory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-thumb-prewarm-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 12);
        var vm = new MainWindowViewModel(); // prewarm enabled (default)
        try
        {
            await vm.LoadDocumentAsync(path);
            vm.ThumbnailPrewarmTask.Should().NotBeNull("first open should queue the idle pre-warm (#689)");
            await vm.ThumbnailPrewarmTask!.WaitAsync(TimeSpan.FromSeconds(120));

            // Cache-only: the sweep must not have loaded bitmaps into the
            // sidebar (that would defeat the #687 memory bound).
            foreach (var thumb in vm.PageThumbnails)
                thumb.ThumbnailImage.Should().BeNull("pre-warm populates the DISK cache, not sidebar memory");
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    private static async Task FlushDispatcherAsync()
    {
        // Two Background-priority hops: one lets a queued window pass run,
        // the second lets work it posted (evict-dispose, prefetch start) run.
        for (int i = 0; i < 2; i++)
            await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => { }, global::Avalonia.Threading.DispatcherPriority.ApplicationIdle);
    }

    // ── #690: disk LRU trim ─────────────────────────────────────────────────

    [Fact]
    public void TrimCacheRoot_DeletesOldest_KeepsProtected_StopsUnderCap()
    {
        var root = Path.Combine(Path.GetTempPath(), $"excise-trim-{Guid.NewGuid():N}");
        try
        {
            // Three per-file dirs of 1MB each, distinct ages; cap at 2MB.
            var old = MakeCacheDir(root, "aaa-oldest", ageDays: 30);
            var mid = MakeCacheDir(root, "bbb-middle", ageDays: 10);
            var current = MakeCacheDir(root, "ccc-current", ageDays: 0);

            ThumbnailCacheService.TrimCacheRoot(root, capBytes: 2 * 1024 * 1024, protectDirName: "ccc-current");

            Directory.Exists(old).Should().BeFalse("the least-recently-used dir must be trimmed first");
            Directory.Exists(mid).Should().BeTrue("trimming stops once under the cap");
            Directory.Exists(current).Should().BeTrue();

            // Protected dir survives even when it alone exceeds the cap.
            ThumbnailCacheService.TrimCacheRoot(root, capBytes: 1, protectDirName: "ccc-current");
            Directory.Exists(current).Should().BeTrue("the open document's cache is never trimmed");
            Directory.Exists(mid).Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static string MakeCacheDir(string root, string name, int ageDays)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "p00001.webp");
        File.WriteAllBytes(file, new byte[1024 * 1024]);
        var stamp = DateTime.UtcNow.AddDays(-ageDays);
        File.SetLastWriteTimeUtc(file, stamp);
        File.SetLastAccessTimeUtc(file, stamp);
        return dir;
    }
}
