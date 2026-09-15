using System;
using System.IO;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Preferences → Performance: the preset table, Custom detection, clamping, and
/// persistence. The live effect on a real viewer is in
/// <c>UI/PerformancePreferencesLiveApplyTests</c>.
/// </summary>
public class PerformancePreferencesTests
{
    private const long MiB = 1024L * 1024L;

    [Fact]
    public void PresetTable_IsTheDecidedTable()
    {
        int cpu = Environment.ProcessorCount;

        PerformanceSettings.LowMemory.Should().Be(new PerformanceSettings(
            TileCacheBudgetMb: 64, SinglePageCachedPages: 2, ThumbnailPrewarm: false, ThumbnailKeepMargin: 16,
            SoftCacheTrims: true, IdleTrimSeconds: 15, RenderThreads: 2));
        PerformanceSettings.Balanced.Should().Be(new PerformanceSettings(
            TileCacheBudgetMb: 200, SinglePageCachedPages: 6, ThumbnailPrewarm: true, ThumbnailKeepMargin: 48,
            SoftCacheTrims: true, IdleTrimSeconds: 30, RenderThreads: Math.Clamp(cpu - 1, 2, 6)));
        PerformanceSettings.Fast.Should().Be(new PerformanceSettings(
            TileCacheBudgetMb: 400, SinglePageCachedPages: 12, ThumbnailPrewarm: true, ThumbnailKeepMargin: 48,
            SoftCacheTrims: false, IdleTrimSeconds: 30, RenderThreads: Math.Max(1, Math.Min(cpu - 1, 8))));
    }

    [Fact]
    public void Balanced_IsWhatANewWindowJsonAndTheThumbnailSidebarAlreadyDo()
    {
        var fresh = new WindowSettings();
        PerformanceSettings.FromWindowSettings(fresh).Should().Be(PerformanceSettings.Balanced);
        fresh.CacheTrimSoftTriggers.Should().Be(PerformanceSettings.Balanced.SoftCacheTrims);
        fresh.CacheTrimIdleSeconds.Should().Be(PerformanceSettings.Balanced.IdleTrimSeconds);
        ThumbnailSidebarSession.KeepMargin.Should().Be(PerformanceSettings.Balanced.ThumbnailKeepMargin);
        MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false).PerformanceSettings
            .Should().Be(PerformanceSettings.Balanced, "a view model with no window.json applied runs Balanced");
    }

    [Fact]
    public void WindowJsonWithoutTheFields_LoadsAsBalanced()
    {
        File.WriteAllText(AppPaths.WindowSettingsPath, """{ "Width": 1000, "Height": 700 }""");

        var loaded = WindowSettings.Load();

        loaded.Width.Should().Be(1000, "fixture: the file was read");
        var settings = PerformanceSettings.FromWindowSettings(loaded);
        settings.Should().Be(PerformanceSettings.Balanced);
        settings.DetectPreset().Should().Be(PerformancePreset.Balanced);
    }

    [Fact]
    public void ASavedSoftTriggersFalse_WithNoPresetName_IsKept_AndReadsAsCustom()
    {
        File.WriteAllText(AppPaths.WindowSettingsPath, """{ "CacheTrimSoftTriggers": false }""");

        var settings = PerformanceSettings.FromWindowSettings(WindowSettings.Load());

        settings.SoftCacheTrims.Should().BeFalse("an explicit saved choice from before the Performance card outranks the preset");
        settings.DetectPreset().Should().Be(PerformancePreset.Custom);
    }

    [Fact]
    public void ANamedPreset_IsRederivedForThisMachine_NotReadFromStaleValues()
    {
        var file = new WindowSettings { PerformancePreset = "Fast", RenderThreads = 999, TileCacheBudgetMb = 1 };

        PerformanceSettings.FromWindowSettings(file).Should().Be(PerformanceSettings.Fast);
    }

    [Fact]
    public void CustomValues_AreClampedToTheirRanges()
    {
        var low = new WindowSettings
        {
            PerformancePreset = "Custom",
            TileCacheBudgetMb = 5, SinglePageCachedPages = 0, ThumbnailKeepMargin = 0,
            CacheTrimIdleSeconds = 1, RenderThreads = 0,
        };
        var high = new WindowSettings
        {
            PerformancePreset = "Custom",
            TileCacheBudgetMb = 99_999, SinglePageCachedPages = 100, ThumbnailKeepMargin = 99_999,
            CacheTrimIdleSeconds = 99_999, RenderThreads = 99_999,
        };

        var l = PerformanceSettings.FromWindowSettings(low);
        l.TileCacheBudgetMb.Should().Be(32);
        l.SinglePageCachedPages.Should().Be(1);
        l.ThumbnailKeepMargin.Should().Be(12);
        l.IdleTrimSeconds.Should().Be(10);
        l.RenderThreads.Should().Be(1);

        var h = PerformanceSettings.FromWindowSettings(high);
        h.TileCacheBudgetMb.Should().Be(1024);
        h.SinglePageCachedPages.Should().Be(24);
        h.ThumbnailKeepMargin.Should().Be(500);
        h.IdleTrimSeconds.Should().Be(600);
        h.RenderThreads.Should().Be(Math.Max(2, Environment.ProcessorCount));
    }

    [Fact]
    public void DetectPreset_NamesEachPreset_CustomOtherwise_AndIgnoresTheIdleDelayWhileTrimsAreOff()
    {
        PerformanceSettings.LowMemory.DetectPreset().Should().Be(PerformancePreset.LowMemory);
        PerformanceSettings.Balanced.DetectPreset().Should().Be(PerformancePreset.Balanced);
        PerformanceSettings.Fast.DetectPreset().Should().Be(PerformancePreset.Fast);

        (PerformanceSettings.Balanced with { SinglePageCachedPages = 7 }).DetectPreset().Should().Be(PerformancePreset.Custom);
        (PerformanceSettings.LowMemory with { ThumbnailPrewarm = true }).DetectPreset().Should().Be(PerformancePreset.Custom);
        (PerformanceSettings.Balanced with { IdleTrimSeconds = 31 }).DetectPreset().Should().Be(PerformancePreset.Custom,
            "with soft trims on the idle delay is a real difference");
        (PerformanceSettings.Fast with { IdleTrimSeconds = 99 }).DetectPreset().Should().Be(PerformancePreset.Fast,
            "with soft trims off the idle delay does nothing");
    }

    [Fact]
    public void Dialog_ChoosingAPresetOverwritesTheFields_EditingAFieldSwitchesToCustom()
    {
        var prefs = new PreferencesViewModel();
        prefs.SelectedPerformancePreset.Should().Be(PerformancePreset.Balanced);
        prefs.BuildPerformanceSettings().Should().Be(PerformanceSettings.Balanced);

        prefs.SelectedPerformancePreset = PerformancePreset.LowMemory;
        prefs.BuildPerformanceSettings().Should().Be(PerformanceSettings.LowMemory);

        prefs.TileCacheBudgetMb = 128;
        prefs.SelectedPerformancePreset.Should().Be(PerformancePreset.Custom);
        prefs.SinglePageCachedPages.Should().Be(2, "switching the display to Custom keeps the other values");

        prefs.TileCacheBudgetMb = 64;
        prefs.SelectedPerformancePreset.Should().Be(PerformancePreset.LowMemory,
            "values equal to a preset read as that preset");

        prefs.SelectedPerformancePreset = PerformancePreset.Custom;
        prefs.BuildPerformanceSettings().Should().Be(PerformanceSettings.LowMemory, "choosing Custom changes no value");

        prefs.SoftCacheTrims = false;
        prefs.SelectedPerformancePreset.Should().Be(PerformancePreset.Custom);

        prefs.ResetToDefaultsCommand.Execute().Subscribe();
        prefs.SelectedPerformancePreset.Should().Be(PerformancePreset.Balanced);
        prefs.BuildPerformanceSettings().Should().Be(PerformanceSettings.Balanced);
    }

    [Fact]
    public void Dialog_BuildClampsOutOfRangeEntries()
    {
        var prefs = new PreferencesViewModel { TileCacheBudgetMb = 4, RenderThreads = 0, IdleTrimSeconds = 100_000 };

        var built = prefs.BuildPerformanceSettings();

        built.TileCacheBudgetMb.Should().Be(PerformanceSettings.MinTileCacheBudgetMb);
        built.RenderThreads.Should().Be(PerformanceSettings.MinRenderThreads);
        built.IdleTrimSeconds.Should().Be(PerformanceSettings.MaxIdleTrimSeconds);
    }

    [Fact]
    public void Dialog_RoundTripsThroughTheMainViewModel()
    {
        var main = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        main.ApplyPerformanceSettings(PerformanceSettings.Fast);

        var prefs = new PreferencesViewModel();
        prefs.LoadFromMainViewModel(main);
        prefs.SelectedPerformancePreset.Should().Be(PerformancePreset.Fast);

        prefs.SelectedPerformancePreset = PerformancePreset.LowMemory;
        prefs.SaveToMainViewModel(main);

        main.PerformanceSettings.Should().Be(PerformanceSettings.LowMemory);
        main.ThumbnailPrewarmEnabled.Should().BeFalse();
    }

    [Fact]
    public void Save_WritesWindowJsonImmediately_NotWhenTheMainWindowCloses()
    {
        File.Exists(AppPaths.WindowSettingsPath).Should().BeFalse("fixture: each test starts with no window.json");
        var main = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var prefs = new PreferencesViewModel();
        prefs.LoadFromMainViewModel(main);
        prefs.SelectedPerformancePreset = PerformancePreset.LowMemory;

        main.ApplySavedPreferences(prefs);

        var onDisk = WindowSettings.Load();
        onDisk.PerformancePreset.Should().Be("LowMemory");
        onDisk.TileCacheBudgetMb.Should().Be(64);
        onDisk.SinglePageCachedPages.Should().Be(2);
        onDisk.ThumbnailPrewarm.Should().BeFalse();
        onDisk.ThumbnailKeepMargin.Should().Be(16);
        onDisk.CacheTrimSoftTriggers.Should().BeTrue();
        onDisk.CacheTrimIdleSeconds.Should().Be(15);
        PerformanceSettings.FromWindowSettings(onDisk).Should().Be(PerformanceSettings.LowMemory);
    }

    [Fact]
    public void TheDocumentStateWriter_AndAPreferencesSave_DoNotRevertEachOther()
    {
        var doc = Path.Combine(Path.GetTempPath(), $"excise-perf-docstate-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(doc, "placeholder: document states for missing files are pruned on load");
        try
        {
            // A stale snapshot, taken before either write: saving it wholesale is
            // what the old window-close writer did.
            var staleSnapshot = WindowSettings.Load();

            var main = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
            var prefs = new PreferencesViewModel();
            prefs.LoadFromMainViewModel(main);
            prefs.SelectedPerformancePreset = PerformancePreset.Fast;
            main.ApplySavedPreferences(prefs);

            WindowSettings.Update(s => s.UpdateDocumentState(doc, zoomLevel: 1.5, pageIndex: 3));

            var afterBoth = WindowSettings.Load();
            afterBoth.PerformancePreset.Should().Be("Fast", "the document-state write must keep the Preferences save");
            afterBoth.DocumentStates.Should().ContainSingle(d => d.LastPageIndex == 3);

            main.ApplySavedPreferences(prefs);
            WindowSettings.Load().DocumentStates.Should().ContainSingle(d => d.LastPageIndex == 3,
                "a Preferences save must keep document states written after startup");

            staleSnapshot.PerformancePreset.Should().BeNull("fixture: the snapshot predates both writes");
        }
        finally
        {
            File.Delete(doc);
        }
    }
}
