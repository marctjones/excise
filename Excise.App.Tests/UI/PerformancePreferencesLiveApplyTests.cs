using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Services;
using Excise.App.Tests.Controls;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Xunit;
using PdfCoreDocument = Excise.Core.Document.PdfDocument;

namespace Excise.App.Tests.UI;

/// <summary>
/// Preferences → Performance applies to the running app, not the next launch:
/// each knob is driven against a real headless viewer, sidebar, trim
/// coordinator or dialog, and checked by what it releases or bounds.
/// </summary>
[Collection("AvaloniaTests")]
public class PerformancePreferencesLiveApplyTests
{
    private const long MiB = 1024L * 1024L;
    private readonly ITestOutputHelper _out;

    public PerformancePreferencesLiveApplyTests(ITestOutputHelper output) => _out = output;

    [FixedAvaloniaFact]
    public void Balanced_IsExactlyWhatAFreshViewerDoes()
    {
        var viewer = new PdfViewerControl();
        var balanced = PerformanceSettings.Balanced;

        viewer.ContinuousTileCacheByteBudget.Should().Be(balanced.TileCacheBudgetMb * MiB);
        viewer.SinglePageCacheCapacity.Should().Be(balanced.SinglePageCachedPages);
        viewer.ContinuousRenderConcurrency.Should().Be(balanced.RenderThreads);
        viewer.GetRenderDiagnostics().ContinuousByteBudget.Should().Be(balanced.TileCacheBudgetMb * MiB);
    }

    [FixedAvaloniaFact(Timeout = 120_000)]
    public async Task LoweringTheTileBudget_EvictsToTheBudgetAtOnce_KeepsTheVisibleBand_AndDisposesWhatItDrops()
    {
        var (window, viewer, items) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(pageCount: 3);
        try
        {
            await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
            var band = viewer.ContinuousRequiredKeysForTests.ToHashSet();
            band.Should().NotBeEmpty("fixture: page 1's band is on screen");
            viewer.ContinuousCacheEntriesForTests().Select(e => e.Key).Should().Contain(band,
                "fixture: a settled band is fully cached");

            // Newer junk puts the band at the LRU TAIL, which a plain tail-first
            // eviction would take first.
            const int junkSide = 256;
            for (int i = 0; i < 64; i++)
                viewer.AddToContinuousCache(JunkKey(i), JunkTile(junkSide));

            var before = viewer.ContinuousCacheEntriesForTests();
            long bandBytes = before.Where(e => band.Contains(e.Key)).Sum(e => Bytes(e.Bitmap));
            long budget = bandBytes + 4 * PdfViewerControl.ContinuousTileByteSize(junkSide, junkSide);
            viewer.ContinuousTileCacheResidentBytes.Should().BeGreaterThan(budget, "fixture: the cache is over the new budget");

            viewer.ContinuousTileCacheByteBudget = budget;

            // Read at once: no further insert may do the eviction for us.
            viewer.ContinuousTileCacheResidentBytes.Should().BeLessThanOrEqualTo(budget,
                "lowering the budget evicts down to it immediately");
            viewer.GetRenderDiagnostics().ContinuousByteBudget.Should().Be(budget);

            var after = viewer.ContinuousCacheEntriesForTests();
            after.Select(e => e.Key).Should().Contain(band, "the visible band is never evicted");
            var kept = after.Select(e => (object)e.Bitmap).ToHashSet(ReferenceEqualityComparer.Instance);
            var dropped = before.Where(e => !kept.Contains(e.Bitmap)).ToList();
            dropped.Should().NotBeEmpty();
            dropped.Should().OnlyContain(e => !band.Contains(e.Key));
            dropped.Should().OnlyContain(e => IsDisposed(e.Bitmap), "an evicted tile's native pixels are released at once (#1467)");
            after.Should().OnlyContain(e => !IsDisposed(e.Bitmap));
        }
        finally
        {
            window.Close();
            viewer.Document?.Dispose();
        }
    }

    [FixedAvaloniaFact(Timeout = 120_000)]
    public async Task LoweringThePageCache_DisposesTheExtras_ButNotTheBitmapOnScreen()
    {
        var viewer = new PdfViewerControl();
        var window = new Window { Content = viewer, Width = 900, Height = 700 };
        window.Show();
        viewer.Document = PdfCoreDocument.Open(MultiPagePdfBytes(8));
        var image = viewer.FindControl<Image>("PdfImage")!;
        try
        {
            var published = new List<WriteableBitmap>();
            for (int page = 1; page <= 7; page++)
            {
                long publishes = viewer.SinglePagePublishCount;
                if (page > 1)
                    viewer.CurrentPage = page;
                published.Add(await WaitForSinglePagePublishAsync(window, viewer, image, publishes));
            }
            viewer.GetRenderDiagnostics().SinglePageEntryCount.Should().BeGreaterThan(1, "fixture: several pages are cached");
            var shown = (WriteableBitmap)image.Source!;
            shown.Should().BeSameAs(published[^1]);

            viewer.SinglePageCacheCapacity = 1;

            var diagnostics = viewer.GetRenderDiagnostics();
            diagnostics.SinglePageCapacity.Should().Be(1);
            diagnostics.SinglePageEntryCount.Should().Be(1);
            IsDisposed(shown).Should().BeFalse("the bitmap on screen is never disposed");
            image.Source.Should().BeSameAs(shown);
            for (int i = 0; i < published.Count - 1; i++)
                IsDisposed(published[i]).Should().BeTrue($"page {i + 1} is no longer cached, so its pixels are released");

            // Still navigates and publishes under the new capacity.
            long before = viewer.SinglePagePublishCount;
            viewer.CurrentPage = 8;
            var page8 = await WaitForSinglePagePublishAsync(window, viewer, image, before);
            IsDisposed(page8).Should().BeFalse();
        }
        finally
        {
            window.Close();
            viewer.Document?.Dispose();
        }
    }

    [FixedAvaloniaFact(Timeout = 150_000)]
    public async Task ChangingRenderThreads_BoundsTheRendersThatStartAfterwards()
    {
        var viewer = new PdfViewerControl { ZoomLevel = 0.3 };
        var window = new Window { Content = viewer, Width = 900, Height = 1400 };
        window.Show();
        int running = 0, peak = 0;
        viewer.ContinuousBandRenderStartingForTests = _ =>
        {
            int now = Interlocked.Increment(ref running);
            int seen;
            while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
            Thread.Sleep(150);   // hold the slot so concurrent renders overlap
            Interlocked.Decrement(ref running);
        };
        try
        {
            viewer.ContinuousRenderConcurrency = 1;
            viewer.Document = PdfCoreDocument.Open(MultiPagePdfBytes(12));
            viewer.ViewMode = PdfViewMode.Continuous;
            await WaitForRendersToSettleAsync(window, viewer, minStarts: 3);
            peak.Should().Be(1, "with one render thread no two band renders overlap");

            int starts = viewer.ContinuousRenderStartCount;
            Volatile.Write(ref peak, 0);
            viewer.ContinuousRenderConcurrency = 4;
            viewer.ZoomLevel = 0.25;   // new keys, and more pages on screen: every visible band renders again
            await WaitForRendersToSettleAsync(window, viewer, minStarts: starts + 2);
            _out.WriteLine($"peak concurrent band renders after raising to 4: {peak}");
            peak.Should().BeGreaterThan(1, "renders started after the change use the wider gate");
        }
        finally
        {
            viewer.ContinuousBandRenderStartingForTests = null;
            window.Close();
            viewer.Document?.Dispose();
        }
    }

    [FixedAvaloniaFact(Timeout = 180_000)]
    public async Task TurningThumbnailPrewarmOff_StopsTheRunningBackgroundPrerender()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-perf-prewarm-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, 120);
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: true);
        try
        {
            await vm.LoadDocumentAsync(path);
            var prewarm = vm.ThumbnailPrewarmTask;
            prewarm.Should().NotBeNull("fixture: pre-render starts with the document");
            prewarm!.IsCompleted.Should().BeFalse("fixture: 120 paced pages cannot be done yet");

            vm.ApplyPerformanceSettings(PerformanceSettings.LowMemory);

            vm.ThumbnailPrewarmEnabled.Should().BeFalse();
            vm.ThumbnailPrewarmTask.Should().BeNull();
            await prewarm.WaitAsync(TimeSpan.FromSeconds(5));   // cancelled, not left to run 120 pages

            // #1565: Balanced no longer turns it on, so use the preset that does.
            vm.ApplyPerformanceSettings(PerformanceSettings.Fast);
            vm.ThumbnailPrewarmTask.Should().NotBeNull("turning it back on restarts it for the open document");
            vm.ApplyPerformanceSettings(PerformanceSettings.LowMemory);
        }
        finally
        {
            vm.ApplyPerformanceSettings(PerformanceSettings.LowMemory);
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    [FixedAvaloniaFact(Timeout = 180_000)]
    public async Task LoweringTheThumbnailKeepMargin_ReleasesThumbnailsOutsideItAtOnce()
    {
        const int pageCount = 40;
        const int lastVisible = 2;
        var path = Path.Combine(Path.GetTempPath(), $"excise-perf-keep-{Guid.NewGuid():N}.pdf");
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
            vm.PageThumbnails.Should().OnlyContain(t => t.ThumbnailImage != null, "fixture: the default keep margin (48) covers all 40");

            vm.ApplyPerformanceSettings(PerformanceSettings.LowMemory with { ThumbnailPrewarm = false });
            await PumpForAsync(TimeSpan.FromMilliseconds(200));

            int keepTo = lastVisible + PerformanceSettings.LowMemory.ThumbnailKeepMargin;
            for (int i = 0; i < pageCount; i++)
            {
                if (i <= keepTo)
                    vm.PageThumbnails[i].ThumbnailImage.Should().NotBeNull($"page {i} is inside the new keep margin");
                else
                    vm.PageThumbnails[i].ThumbnailImage.Should().BeNull($"page {i} is outside the new keep margin");
            }
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    [FixedAvaloniaFact]
    public async Task SoftTriggerPolicy_UpdatesALiveCoordinator_BothWays_AndOffArmsNothing()
    {
        var trims = new List<PdfViewerCacheTrimLevel>();
        var on = new CacheTrimPolicy(OnMemoryPressure: true, SoftTriggers: true, IdleDelay: TimeSpan.FromMinutes(10));
        using var coordinator = new ViewerCacheTrimCoordinator(trims.Add, on, () => (0, 0));
        coordinator.OnActivity();
        coordinator.IdleTimerArmed.Should().BeTrue("fixture: activity arms the idle timer");

        coordinator.UpdatePolicy(on with { SoftTriggers = false });
        coordinator.IdleTimerArmed.Should().BeFalse("turning soft triggers off disarms the idle timer at once (#1462)");
        coordinator.OnActivity();
        coordinator.IdleTimerArmed.Should().BeFalse("with soft triggers off, activity arms nothing");
        coordinator.OnDeactivated();
        coordinator.OnMinimized();
        await PumpForAsync(TimeSpan.FromMilliseconds(300));
        trims.Should().BeEmpty();

        coordinator.UpdatePolicy(on with { IdleDelay = TimeSpan.FromMilliseconds(100) });
        coordinator.IdleTimerArmed.Should().BeFalse("turning them on does not arm the timer by itself");
        coordinator.OnDeactivated();
        trims.Should().Equal(PdfViewerCacheTrimLevel.Background);
        coordinator.OnActivity();
        coordinator.IdleTimerArmed.Should().BeTrue();
        await PumpUntilAsync(() => trims.Count == 2, TimeSpan.FromSeconds(10));
        trims[1].Should().Be(PdfViewerCacheTrimLevel.Background);
        coordinator.IdleTimerArmed.Should().BeFalse("still one-shot");

        coordinator.OnActivity();
        coordinator.UpdatePolicy(on with { IdleDelay = TimeSpan.FromMinutes(5) });
        coordinator.IdleTimerArmed.Should().BeTrue("changing only the delay keeps an armed timer armed");
    }

    [FixedAvaloniaFact(Timeout = 60_000)]
    public void MainWindow_PushesAppliedSettingsIntoItsViewer_AndIntoTheTrimCoordinator()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        try
        {
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            viewer.ContinuousTileCacheByteBudget.Should().Be(200 * MiB, "no window.json restores Balanced");
            vm.ThumbnailPrewarmEnabled.Should().BeFalse("a restored Balanced never re-enables pre-render the host turned off");

            var (_, startPolicy) = window.CacheTrimTarget();
            using var coordinator = new ViewerCacheTrimCoordinator(_ => { }, startPolicy, () => (0, 0));
            window.CacheTrimPolicyChanged += coordinator.UpdatePolicy;   // App.axaml.cs does exactly this
            coordinator.OnActivity();
            coordinator.IdleTimerArmed.Should().BeTrue("fixture: Balanced has soft triggers on");

            vm.ApplyPerformanceSettings(PerformanceSettings.Fast);
            viewer.ContinuousTileCacheByteBudget.Should().Be(400 * MiB);
            viewer.SinglePageCacheCapacity.Should().Be(12);
            viewer.ContinuousRenderConcurrency.Should().Be(PerformanceSettings.Fast.RenderThreads);
            coordinator.IdleTimerArmed.Should().BeFalse("Fast turns soft triggers off in the live coordinator");
            window.CacheTrimTarget().Policy.SoftTriggers.Should().BeFalse();

            vm.ApplyPerformanceSettings(PerformanceSettings.LowMemory);
            viewer.ContinuousTileCacheByteBudget.Should().Be(64 * MiB);
            viewer.SinglePageCacheCapacity.Should().Be(2);
            viewer.ContinuousRenderConcurrency.Should().Be(2);
            window.CacheTrimTarget().Policy.IdleDelay.Should().Be(TimeSpan.FromSeconds(15));
            coordinator.OnActivity();
            coordinator.IdleTimerArmed.Should().BeTrue();
        }
        finally
        {
            window.Close();
        }
    }

    [FixedAvaloniaFact(Timeout = 60_000)]
    public async Task PreferencesSave_ThroughTheRealDialog_AppliesOnTheUiThread_AndPersistsBeforeTheMainWindowCloses()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        vm.MainWindowResolver = () => window;
        int uiThread = Environment.CurrentManagedThreadId;
        var applyThreads = new List<int>();
        vm.PerformanceSettingsApplied += (_, _) => applyThreads.Add(Environment.CurrentManagedThreadId);
        var dispatcherErrors = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler onError = (_, e) => dispatcherErrors.Add(e.Exception);
        Dispatcher.UIThread.UnhandledException += onError;
        try
        {
            await vm.ShowPreferencesCommand.Execute();
            await KeyboardTestHelpers.FlushDispatcherAsync();
            await KeyboardTestHelpers.FlushDispatcherAsync();

            var dialog = window.OwnedWindows.OfType<PreferencesWindow>().Single();
            dialog.HasMemoryReadoutTimer.Should().BeTrue();
            var prefs = (PreferencesViewModel)dialog.DataContext!;
            prefs.TileCacheText.Should().EndWith("MB", "the readout reads the main window's viewer");

            prefs.SelectedPerformancePreset = PerformancePreset.LowMemory;
            await prefs.SaveCommand.Execute();
            await KeyboardTestHelpers.FlushDispatcherAsync();
            await KeyboardTestHelpers.FlushDispatcherAsync();

            applyThreads.Should().Equal(new[] { uiThread }, "Save applies once, on the UI thread, where the viewer's setters may run");
            dispatcherErrors.Should().BeEmpty();
            window.OwnedWindows.OfType<PreferencesWindow>().Should().BeEmpty("Save closes the dialog");
            dialog.HasMemoryReadoutTimer.Should().BeFalse();

            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            viewer.ContinuousTileCacheByteBudget.Should().Be(64 * MiB);
            viewer.SinglePageCacheCapacity.Should().Be(2);

            WindowSettings.Load().PerformancePreset.Should().Be("LowMemory", "Save persists immediately");

            window.Close();
            await KeyboardTestHelpers.FlushDispatcherAsync();
            var afterClose = WindowSettings.Load();
            afterClose.PerformancePreset.Should().Be("LowMemory", "the window-close writer must not revert a Preferences save");
            afterClose.TileCacheBudgetMb.Should().Be(64);
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= onError;
            window.Close();
        }
    }

    [FixedAvaloniaFact]
    public async Task MemoryReadoutTimer_ExistsOnlyWhileTheDialogIsOpen()
    {
        long tileBytes = 42 * MiB;
        var prefs = new PreferencesViewModel { TileCacheBytesSource = () => tileBytes };
        var dialog = new PreferencesWindow { DataContext = prefs };
        dialog.HasMemoryReadoutTimer.Should().BeFalse("nothing runs before the dialog opens");

        dialog.Show();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        dialog.HasMemoryReadoutTimer.Should().BeTrue();
        prefs.TileCacheText.Should().Be(FormatMb(42));
        prefs.WorkingSetText.Should().EndWith("MB");
        prefs.ManagedHeapText.Should().EndWith("MB");

        tileBytes = 7 * MiB;
        await PumpUntilAsync(() => prefs.TileCacheText == FormatMb(7), TimeSpan.FromSeconds(10));

        dialog.Close();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        dialog.HasMemoryReadoutTimer.Should().BeFalse("the timer is stopped and dropped on close (#1462)");

        tileBytes = 9 * MiB;
        await PumpForAsync(PreferencesWindow.MemoryReadoutInterval * 2.5);
        prefs.TileCacheText.Should().Be(FormatMb(7), "no readout tick runs after the dialog closed");
    }

    [FixedAvaloniaFact]
    public void PerformanceControls_HaveAccessibleNamesAndHelp_AndTheNumericFieldsBindBothWays()
    {
        var prefs = new PreferencesViewModel();
        var dialog = new PreferencesWindow { DataContext = prefs };
        dialog.Show();
        try
        {
            var expander = Find<Expander>(dialog, "PerformanceAdvancedExpander");
            expander.IsExpanded = true;
            dialog.UpdateLayout();

            foreach (var name in new[]
                     {
                         "PerformancePresetComboBox", "MemoryReadoutPanel", "PerformanceAdvancedExpander",
                         "TileCacheBudgetNumericUpDown", "SinglePageCachedPagesNumericUpDown", "ThumbnailPrewarmCheckBox",
                         "ThumbnailKeepMarginNumericUpDown", "SoftCacheTrimsCheckBox", "IdleTrimSecondsNumericUpDown",
                         "RenderThreadsNumericUpDown",
                     })
            {
                var control = Find<Control>(dialog, name);
                AutomationProperties.GetName(control).Should().NotBeNullOrWhiteSpace($"{name} needs an accessible name");
                AutomationProperties.GetHelpText(control).Should().NotBeNullOrWhiteSpace($"{name} needs help text");
            }

            var tiles = Find<NumericUpDown>(dialog, "TileCacheBudgetNumericUpDown");
            tiles.Value.Should().Be(200m);
            // SetCurrentValue is how the control's own spinner and text entry
            // change Value; a plain local set would replace the binding.
            tiles.SetCurrentValue(NumericUpDown.ValueProperty, 96m);
            prefs.TileCacheBudgetMb.Should().Be(96);
            prefs.SelectedPerformancePreset.Should().Be(PerformancePreset.Custom);

            prefs.SelectedPerformancePreset = PerformancePreset.Fast;
            Dispatcher.UIThread.RunJobs();
            Find<NumericUpDown>(dialog, "SinglePageCachedPagesNumericUpDown").Value.Should().Be(12m,
                "a field the user did not touch follows the preset");
            tiles.Value.Should().Be(400m, "choosing a preset rewrites the Advanced fields");
            Find<NumericUpDown>(dialog, "RenderThreadsNumericUpDown").Maximum.Should().Be(PerformanceSettings.MaxRenderThreads);
        }
        finally
        {
            dialog.Close();
        }
    }

    /// <summary>
    /// Report-only: tile-cache bytes and managed heap after paging a document
    /// under Low memory and Fast. Asserts only that Low stays within its budget.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 170_000)]
    public async Task Measure_LowMemoryVsFast_AfterPagingADocument()
    {
        const int pages = 24;
        const int steps = 20;
        var bytes = MultiPagePdfBytes(pages);
        var results = new Dictionary<PerformancePreset, (long Tiles, int Count, long Heap)>();
        foreach (var preset in new[] { PerformancePreset.LowMemory, PerformancePreset.Fast })
        {
            var settings = PerformanceSettings.For(preset);
            var (window, viewer, items) = ContinuousTileEvictionCompositeTests.ShowContinuousViewer(bytes);
            try
            {
                viewer.ContinuousTileCacheByteBudget = settings.TileCacheBudgetMb * MiB;
                viewer.SinglePageCacheCapacity = settings.SinglePageCachedPages;
                viewer.ContinuousRenderConcurrency = settings.RenderThreads;
                await ContinuousTileEvictionCompositeTests.WaitForSettledCompositeAsync(window, viewer, items, pageNumber: 1);
                var sw = Stopwatch.StartNew();
                for (int step = 1; step <= steps; step++)
                {
                    viewer.TrySetViewportVerticalFraction(step / (double)steps).Should().BeTrue();
                    await WaitForRendersToSettleAsync(window, viewer, minStarts: 0);
                }
                results[preset] = (viewer.ContinuousTileCacheResidentBytes,
                    viewer.GetRenderDiagnostics().ContinuousEntryCount,
                    GC.GetTotalMemory(forceFullCollection: false));
                _out.WriteLine($"{preset}: paged {pages} pages in {steps} steps ({sw.Elapsed.TotalSeconds:F1} s); " +
                               $"tile cache {results[preset].Tiles / (double)MiB:F1} MiB in {results[preset].Count} tiles " +
                               $"(budget {settings.TileCacheBudgetMb} MiB), managed heap {results[preset].Heap / (double)MiB:F1} MiB");
            }
            finally
            {
                window.Close();
                viewer.Document?.Dispose();
            }
        }

        results[PerformancePreset.LowMemory].Tiles.Should().BeLessThanOrEqualTo(64 * MiB);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static string FormatMb(long mb) =>
        string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{(double)mb:N0} MB");

    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetLogicalDescendants().OfType<T>().SingleOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"{name} not found in {window.GetType().Name}");

    private static byte[] MultiPagePdfBytes(int pageCount)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-perf-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount);
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return bytes;
    }

    private static PdfViewerControl.ContinuousTileKey JunkKey(int col) =>
        new(Page: int.MaxValue, Dpi: 1, PageWidthDip: 1, PageHeightDip: 1, Col: col, Row: 0);

    private static WriteableBitmap JunkTile(int side) =>
        new(new PixelSize(side, side), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    private static long Bytes(WriteableBitmap bitmap) =>
        PdfViewerControl.ContinuousTileByteSize(bitmap.PixelSize.Width, bitmap.PixelSize.Height);

    private static bool IsDisposed(Bitmap bitmap)
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

    private static async Task<WriteableBitmap> WaitForSinglePagePublishAsync(
        Window window, PdfViewerControl viewer, Image image, long publishesBefore)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Dispatcher.UIThread.RunJobs();
            if (viewer.SinglePagePublishCount > publishesBefore && !viewer.IsLoading && image.Source is WriteableBitmap shown)
                return shown;
            if (sw.Elapsed > TimeSpan.FromSeconds(60))
                throw new TimeoutException($"page {viewer.CurrentPage} did not publish");
            await Task.Delay(20);
        }
    }

    private static async Task WaitForRendersToSettleAsync(Window window, PdfViewerControl viewer, int minStarts)
    {
        var sw = Stopwatch.StartNew();
        int quiet = 0;
        while (quiet < 5)
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Dispatcher.UIThread.RunJobs();
            quiet = viewer.ContinuousRenderStartCount >= minStarts && viewer.ContinuousInFlightCount == 0 ? quiet + 1 : 0;
            if (sw.Elapsed > TimeSpan.FromSeconds(60))
                throw new TimeoutException($"renders did not settle (starts={viewer.ContinuousRenderStartCount}, in flight={viewer.ContinuousInFlightCount})");
            await Task.Delay(30);
        }
    }

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
