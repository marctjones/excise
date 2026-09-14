using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.App.Tests.Controls;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.Avalonia.Controls;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1478: the signals that decide when the viewer trims its caches. Each
/// trigger is driven through <see cref="ViewerCacheTrimCoordinator"/>'s hooks
/// with a recording sink; the last test wires a real window and viewer. No test
/// raises real OS memory pressure: that is system-wide on a shared machine.
/// </summary>
[Collection("AvaloniaTests")]
public class ViewerCacheTrimTriggerTests
{
    private static readonly CacheTrimPolicy SoftOn = new(OnMemoryPressure: true, SoftTriggers: true, IdleDelay: TimeSpan.FromMinutes(10));
    private static readonly CacheTrimPolicy SoftOff = SoftOn with { SoftTriggers = false };

    [FixedAvaloniaFact]
    public void DeactivateAndMinimize_TrimToBackground_OnlyWhenSoftTriggersAreOn()
    {
        var trims = new List<PdfViewerCacheTrimLevel>();
        var thumbnails = new List<PdfViewerCacheTrimLevel>();
        using (var on = new ViewerCacheTrimCoordinator(trims.Add, SoftOn, NoGcLoad, thumbnails.Add))
        {
            on.OnDeactivated();
            on.OnMinimized();
        }
        trims.Should().Equal(PdfViewerCacheTrimLevel.Background, PdfViewerCacheTrimLevel.Background);
        thumbnails.Should().BeEmpty("Background leaves the thumbnail tier alone; it is released from Warn up");

        trims.Clear();
        using (var off = new ViewerCacheTrimCoordinator(trims.Add, SoftOff, NoGcLoad))
        {
            off.OnDeactivated();
            off.OnMinimized();
            off.OnActivity();
            off.IdleTimerArmed.Should().BeFalse("with soft triggers off the idle timer is never armed, so nothing wakes an idle app");
        }
        trims.Should().BeEmpty("soft triggers are off by default");
    }

    [FixedAvaloniaFact]
    public async Task IdleTimer_IsOneShot_ArmedByActivity_StoppedWhenItFires_AndByDeactivation()
    {
        var trims = new List<PdfViewerCacheTrimLevel>();
        using var coordinator = new ViewerCacheTrimCoordinator(trims.Add,
            SoftOn with { IdleDelay = TimeSpan.FromMilliseconds(100) }, NoGcLoad);
        coordinator.IdleTimerArmed.Should().BeFalse("nothing is armed before the viewer does anything");

        coordinator.OnActivity();
        coordinator.IdleTimerArmed.Should().BeTrue();
        await PumpUntilAsync(() => trims.Count > 0, TimeSpan.FromSeconds(10));
        trims.Should().Equal(new[] { PdfViewerCacheTrimLevel.Background }, "idle only ever asks for Background");
        coordinator.IdleTimerArmed.Should().BeFalse("the idle timer stops when it fires; it is never periodic (#1462)");

        await PumpForAsync(TimeSpan.FromMilliseconds(400));
        trims.Should().HaveCount(1, "a fired idle timer does not fire again without new activity");

        coordinator.OnActivity();
        coordinator.IdleTimerArmed.Should().BeTrue("activity re-arms it");
        coordinator.OnDeactivated();
        coordinator.IdleTimerArmed.Should().BeFalse("a deactivated window has nothing to wait for");
        await PumpForAsync(TimeSpan.FromMilliseconds(400));
        trims.Should().Equal(PdfViewerCacheTrimLevel.Background, PdfViewerCacheTrimLevel.Background);
    }

    [FixedAvaloniaFact]
    public async Task OsPressure_MapsWarnAndCritical_IgnoresNormal_IsPostedToTheUiThread_AndHonoursThePolicy()
    {
        MacMemoryPressureSource.MapPressureData(MacMemoryPressureSource.DispatchMemoryPressureNormal).Should().Be(MemoryPressureLevel.Normal);
        MacMemoryPressureSource.MapPressureData(MacMemoryPressureSource.DispatchMemoryPressureWarn).Should().Be(MemoryPressureLevel.Warn);
        MacMemoryPressureSource.MapPressureData(MacMemoryPressureSource.DispatchMemoryPressureCritical).Should().Be(MemoryPressureLevel.Critical);
        MacMemoryPressureSource.MapPressureData(
            MacMemoryPressureSource.DispatchMemoryPressureWarn | MacMemoryPressureSource.DispatchMemoryPressureCritical)
            .Should().Be(MemoryPressureLevel.Critical, "the most severe bit wins");

        // The headless dispatcher answers CheckAccess() true on any thread, so
        // "posted to the UI thread" is checked by thread identity instead: this
        // test body runs on the dispatcher thread.
        int uiThread = Environment.CurrentManagedThreadId;
        int postingThread = 0;
        var trims = new List<(PdfViewerCacheTrimLevel Level, int Thread)>();
        var thumbnails = new List<PdfViewerCacheTrimLevel>();
        using (var coordinator = new ViewerCacheTrimCoordinator(
                   level => trims.Add((level, Environment.CurrentManagedThreadId)), SoftOff, NoGcLoad, thumbnails.Add))
        {
            coordinator.OnPressure(MemoryPressureLevel.Normal);
            coordinator.OnPressure(MemoryPressureLevel.Warn);
            // The libdispatch handler runs on a pool thread; it must post.
            await Task.Run(() =>
            {
                postingThread = Environment.CurrentManagedThreadId;
                coordinator.PostPressure(MemoryPressureLevel.Critical);
            });
            await PumpUntilAsync(() => trims.Count == 2, TimeSpan.FromSeconds(10));
        }
        postingThread.Should().NotBe(uiThread, "fixture: the pressure must arrive from another thread");
        trims.Should().Equal((PdfViewerCacheTrimLevel.Warn, uiThread), (PdfViewerCacheTrimLevel.Critical, uiThread));
        thumbnails.Should().Equal(new[] { PdfViewerCacheTrimLevel.Warn, PdfViewerCacheTrimLevel.Critical },
            "OS pressure also releases the thumbnail in-memory tier");

        var ignored = new List<PdfViewerCacheTrimLevel>();
        using (var off = new ViewerCacheTrimCoordinator(ignored.Add, SoftOn with { OnMemoryPressure = false }, NoGcLoad))
            off.OnPressure(MemoryPressureLevel.Critical);
        ignored.Should().BeEmpty("the pressure switch turns OS trimming off");
    }

    [FixedAvaloniaFact]
    public async Task GcFallback_SamplesOnlyOnActivity_Throttled_AndTrimsWarnAtHighLoad()
    {
        int samples = 0;
        var trims = new List<PdfViewerCacheTrimLevel>();
        using var coordinator = new ViewerCacheTrimCoordinator(trims.Add, SoftOff, () =>
        {
            samples++;
            return (MemoryLoadBytes: 900, HighMemoryLoadThresholdBytes: 800);
        });

        await PumpForAsync(TimeSpan.FromMilliseconds(200));
        samples.Should().Be(0, "the fallback is sampled on activity, never on a timer");

        coordinator.OnActivity();
        coordinator.OnActivity();
        samples.Should().Be(1, $"sampling is throttled to one per {ViewerCacheTrimCoordinator.GcSampleInterval}");
        trims.Should().Equal(PdfViewerCacheTrimLevel.Warn);

        int lowSamples = 0;
        var none = new List<PdfViewerCacheTrimLevel>();
        using (var low = new ViewerCacheTrimCoordinator(none.Add, SoftOff, () => { lowSamples++; return (100, 800); }))
            low.OnActivity();
        lowSamples.Should().Be(1);
        none.Should().BeEmpty("below the high-load threshold nothing is trimmed");
    }

    [FixedAvaloniaFact]
    public void MacPressureSource_InstallsAndCancels_WithoutFiring()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "the libdispatch memory-pressure source exists only on macOS");
        var source = MacMemoryPressureSource.TryStart(_ => { });
        source.Should().NotBeNull("libSystem exports the dispatch memory-pressure source on every supported macOS");
        source!.Dispose();
        source.Dispose();
    }

    [FixedAvaloniaFact]
    public async Task Attach_WiresMinimizeScrollAndPressure_ToTheRealViewerTrim_AndDisposeUnwiresThem()
    {
        var (window, viewer, items) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(pageCount: 2);
        var dispatcherErrors = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler onError = (_, e) => dispatcherErrors.Add(e.Exception);
        Dispatcher.UIThread.UnhandledException += onError;
        var coordinator = ViewerCacheTrimCoordinator.Attach(window, viewer, SoftOn);
        try
        {
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            coordinator.HasNativePressureSource.Should().Be(OperatingSystem.IsMacOS());

            viewer.TryScrollViewportBy(40).Should().BeTrue("fixture: the document scrolls");
            await PumpUntilAsync(() => coordinator.IdleTimerArmed, TimeSpan.FromSeconds(10));

            int trimsBefore = viewer.CacheTrimCount;
            window.WindowState = WindowState.Minimized;
            viewer.CacheTrimCount.Should().Be(trimsBefore + 1, "minimizing the window trims");
            viewer.LastCacheTrim.Level.Should().Be(PdfViewerCacheTrimLevel.Background);
            window.WindowState = WindowState.Normal;

            await Task.Run(() => coordinator.PostPressure(MemoryPressureLevel.Critical));
            await PumpUntilAsync(() => viewer.CacheTrimCount == trimsBefore + 2, TimeSpan.FromSeconds(10));
            viewer.LastCacheTrim.Level.Should().Be(PdfViewerCacheTrimLevel.Critical);

            coordinator.Dispose();
            window.WindowState = WindowState.Minimized;
            viewer.TryScrollViewportBy(40);
            await PumpForAsync(TimeSpan.FromMilliseconds(100));
            viewer.CacheTrimCount.Should().Be(trimsBefore + 2, "a disposed coordinator no longer listens");
            coordinator.IdleTimerArmed.Should().BeFalse();
            dispatcherErrors.Should().BeEmpty();
        }
        finally
        {
            coordinator.Dispose();
            Dispatcher.UIThread.UnhandledException -= onError;
            window.Close();
            viewer.Document?.Dispose();
        }
    }

    [FixedAvaloniaFact(Timeout = 180000)]
    public async Task ThumbnailTier_WarnKeepsThePrefetchWindow_CriticalOnlyTheVisiblePages_BackgroundEverything()
    {
        const int pageCount = 30;
        const int lastVisible = 2;
        var path = Path.Combine(Path.GetTempPath(), $"excise-trim-thumbs-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount);
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        try
        {
            await vm.LoadDocumentAsync(path);
            for (int i = 0; i <= lastVisible; i++)
                vm.NotifyThumbnailViewport(i, isVisible: true);
            for (int i = 0; i < pageCount; i++)
                await vm.EnsureThumbnailLoadedAsync(i);
            await PumpForAsync(TimeSpan.FromMilliseconds(100));
            if (vm.ThumbnailPrefetchTask is { } prefetch)
                await prefetch.WaitAsync(TimeSpan.FromSeconds(60));
            await PumpForAsync(TimeSpan.FromMilliseconds(50));
            var loaded = vm.PageThumbnails.Select(t => t.ThumbnailImage).ToArray();
            loaded.Should().OnlyContain(b => b != null, "fixture: every thumbnail is loaded (keep window covers all 30)");

            vm.TrimThumbnailCaches(PdfViewerCacheTrimLevel.Background);
            vm.PageThumbnails.Should().OnlyContain(t => t.ThumbnailImage != null, "Background releases no thumbnails");

            int prefetchTo = lastVisible + MainWindowViewModel.ThumbnailPrefetchMargin;
            vm.TrimThumbnailCaches(PdfViewerCacheTrimLevel.Warn);
            for (int i = 0; i < pageCount; i++)
            {
                if (i <= prefetchTo)
                    vm.PageThumbnails[i].ThumbnailImage.Should().BeSameAs(loaded[i], $"page {i} is inside the prefetch window");
                else
                    vm.PageThumbnails[i].ThumbnailImage.Should().BeNull($"Warn releases page {i}, outside the prefetch window");
            }
            await PumpForAsync(TimeSpan.FromMilliseconds(100));
            for (int i = 0; i < pageCount; i++)
                IsDisposed(loaded[i]!).Should().Be(i > prefetchTo, $"page {i}: a released thumbnail is disposed once its binding moved, a kept one never");

            vm.TrimThumbnailCaches(PdfViewerCacheTrimLevel.Critical);
            for (int i = 0; i <= prefetchTo; i++)
            {
                if (i <= lastVisible)
                    vm.PageThumbnails[i].ThumbnailImage.Should().BeSameAs(loaded[i], $"Critical keeps visible page {i}");
                else
                    vm.PageThumbnails[i].ThumbnailImage.Should().BeNull($"Critical releases non-visible page {i}");
            }
            await PumpForAsync(TimeSpan.FromMilliseconds(100));
            for (int i = 0; i <= prefetchTo; i++)
                IsDisposed(loaded[i]!).Should().Be(i > lastVisible);
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    private static bool IsDisposed(global::Avalonia.Media.Imaging.Bitmap bitmap)
    {
        try
        {
            _ = bitmap.PixelSize;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static (long, long) NoGcLoad() => (0, 0);

    private static async Task PumpUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
                throw new TimeoutException("condition not met");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static async Task PumpForAsync(TimeSpan duration)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }
}
