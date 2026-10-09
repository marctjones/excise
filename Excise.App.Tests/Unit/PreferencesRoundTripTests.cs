using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Services.Printing;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.Core.Operations;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #1640 steps 1 and 3, headless: every value in Preferences survives write → reload through the
/// real window.json (the test config directory the suite redirects), and Reset to Defaults puts
/// every one back. A preference that does not survive is a security choice (Maximum profile,
/// whole-word, keep-attachments, a carrier policy) that silently loosens on the next launch.
/// </summary>
[Collection("AvaloniaTests")]
public class PreferencesRoundTripTests
{
    /// <summary>Make <paramref name="prefs"/> hold <paramref name="change"/>, persist it as the dialog does, and open a fresh session.</summary>
    private static MainWindowViewModel PersistThenReload(Action<PreferencesViewModel> change)
    {
        var first = MainWindowViewModelTestFactory.Create();
        var prefs = new PreferencesViewModel();
        prefs.LoadFromMainViewModel(first);
        change(prefs);
        first.ApplySavedPreferences(prefs);   // the dialog's apply path: copy, apply, write window.json
        return Launch();
    }

    /// <summary>A new launch: the main window reads window.json and applies it to its view model.</summary>
    private static MainWindowViewModel Launch()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new Excise.App.Views.MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        window.Close();
        vm.WindowPreferencesApplied.Should().BeTrue("the window applied window.json on opening");
        return vm;
    }

    [FixedAvaloniaFact]
    public void EveryRedactionPreference_SurvivesALaunch()
    {
        var failures = new List<string>();

        void Check<T>(string name, IEnumerable<T> values, Action<RedactionPreferences, T> set, Func<RedactionPreferences, T> get)
        {
            foreach (var value in values)
            {
                var reloaded = PersistThenReload(p =>
                {
                    var next = p.RedactionPreferences with { };
                    set(next, value);
                    p.RedactionPreferences = next;
                });
                if (!Equals(get(reloaded.RedactionPreferences), value))
                    failures.Add($"{name}: wrote {value}, a new launch has {get(reloaded.RedactionPreferences)}");
            }
        }

        Check("Profile", Enum.GetValues<RedactionProfile>(), (p, v) => p.Profile = v, p => p.Profile);
        Check("WholeWord", new[] { true, false }, (p, v) => p.WholeWord = v, p => p.WholeWord);
        Check("KeepAttachments", new[] { true, false }, (p, v) => p.KeepAttachments = v, p => p.KeepAttachments);
        Check("Width", Enum.GetValues<WidthPolicy>(), (p, v) => p.Width = v, p => p.Width);
        Check("LinkUriPolicy", Enum.GetValues<CarrierScrubMode>(), (p, v) => p.LinkUriPolicy = v, p => p.LinkUriPolicy);
        Check("MetadataPolicy", Enum.GetValues<CarrierScrubMode>(), (p, v) => p.MetadataPolicy = v, p => p.MetadataPolicy);

        failures.Should().BeEmpty();
    }

    [FixedAvaloniaFact]
    public void EveryOtherPreference_SurvivesALaunch()
    {
        var failures = new List<string>();

        foreach (var v in Enum.GetValues<ReadingOrderStrategy>())
            if (PersistThenReload(p => p.SelectedReadingOrderStrategy = v).ReadingOrderStrategy != v)
                failures.Add($"ReadingOrderStrategy {v}");
        foreach (var v in Enum.GetValues<WhitespaceMode>())
            if (PersistThenReload(p => p.SelectedWhitespaceMode = v).WhitespaceMode != v)
                failures.Add($"WhitespaceMode {v}");
        foreach (var v in Enum.GetValues<PrintScalingMode>())
            if (PersistThenReload(p => p.SelectedPrintScaling = v).PrintScaling != v)
                failures.Add($"PrintScaling {v}");
        foreach (var v in Enum.GetValues<DocumentOpenMode>())
            if (PersistThenReload(p => p.SelectedDocumentOpenMode = v).DocumentOpenMode != v)
                failures.Add($"DocumentOpenMode {v}");
        foreach (var v in Enum.GetValues<AppearanceMode>())
            if (PersistThenReload(p => p.SelectedAppearance = v).Appearance != v)
                failures.Add($"Appearance {v}");
        foreach (var v in new[] { true, false })
            if (PersistThenReload(p => p.RunFormCalc = v).RunFormCalc != v)
                failures.Add($"RunFormCalc {v}");

        foreach (var preset in new[] { PerformancePreset.LowMemory, PerformancePreset.Balanced, PerformancePreset.Fast })
        {
            var expected = PerformanceSettings.For(preset);
            var reloaded = PersistThenReload(p => p.SelectedPerformancePreset = preset);
            if (reloaded.PerformanceSettings != expected)
                failures.Add($"Performance preset {preset}: wrote {expected}, a new launch has {reloaded.PerformanceSettings}");
        }

        var custom = new PerformanceSettings(
            TileCacheBudgetMb: 321, SinglePageCachedPages: 9, ThumbnailPrewarm: true, ThumbnailKeepMargin: 17,
            SoftCacheTrims: false, IdleTrimSeconds: 45, RenderThreads: 2).Clamped();
        var customReloaded = PersistThenReload(p =>
        {
            p.TileCacheBudgetMb = custom.TileCacheBudgetMb;
            p.SinglePageCachedPages = custom.SinglePageCachedPages;
            p.ThumbnailPrewarm = custom.ThumbnailPrewarm;
            p.ThumbnailKeepMargin = custom.ThumbnailKeepMargin;
            p.SoftCacheTrims = custom.SoftCacheTrims;
            p.IdleTrimSeconds = custom.IdleTrimSeconds;
            p.RenderThreads = custom.RenderThreads;
        });
        if (customReloaded.PerformanceSettings != custom)
            failures.Add($"Custom performance: wrote {custom}, a new launch has {customReloaded.PerformanceSettings}");

        failures.Should().BeEmpty();
    }

    /// <summary>#1640 step 3: after any performance preset, and the extremes of the advanced values, a document still opens whole.</summary>
    [FixedAvaloniaFact]
    public async Task EveryPerformancePreset_StillOpensADocumentWithAllItsPagesAndThumbnails()
    {
        var path = TestRepoLayout.FindFile("test-pdfs", "smoke", "irs-w4.pdf");
        Assert.SkipWhen(path == null, TestRepoLayout.AbsenceReason("irs-w4.pdf", "test-pdfs/smoke/irs-w4.pdf"));

        var settings = new List<(string Name, PerformanceSettings Value)>
        {
            ("LowMemory", PerformanceSettings.LowMemory),
            ("Balanced", PerformanceSettings.Balanced),
            ("Fast", PerformanceSettings.Fast),
            ("smallest values", new PerformanceSettings(TileCacheBudgetMb: 1, SinglePageCachedPages: 1, ThumbnailPrewarm: false,
                ThumbnailKeepMargin: 0, SoftCacheTrims: true, IdleTrimSeconds: 1, RenderThreads: 1).Clamped()),
            ("largest values", new PerformanceSettings(TileCacheBudgetMb: 100_000, SinglePageCachedPages: 1_000, ThumbnailPrewarm: true,
                ThumbnailKeepMargin: 10_000, SoftCacheTrims: false, IdleTrimSeconds: 100_000, RenderThreads: 1_000).Clamped()),
        };

        foreach (var (name, value) in settings)
        {
            var vm = MainWindowViewModelTestFactory.Create();
            var window = new Excise.App.Views.MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            try
            {
                vm.ApplyPerformanceSettings(value);
                await vm.LoadDocumentAsync(path!);
                vm.TotalPages.Should().Be(5, $"{name}: every page of the W-4 is there");
                vm.PageThumbnails.Should().HaveCount(5, $"{name}: and every thumbnail slot");
                vm.PdfCoreDocument.Should().NotBeNull(name);
            }
            finally
            {
                window.Close();
            }
        }
    }

    [FixedAvaloniaFact]
    public async Task ResetToDefaults_RestoresEveryPreference_AndThatSurvivesALaunch()
    {
        // Move everything off its default first, so a field Reset forgets cannot pass by luck.
        var moved = PersistThenReload(p =>
        {
            p.RedactionPreferences = new RedactionPreferences
            {
                Profile = RedactionProfile.Maximum, WholeWord = true, KeepAttachments = true,
                Width = WidthPolicy.CloseGap, LinkUriPolicy = CarrierScrubMode.RemoveWhole,
                MetadataPolicy = CarrierScrubMode.ReportOnly,
            };
            p.SelectedReadingOrderStrategy = ReadingOrderStrategy.Simple;
            p.SelectedWhitespaceMode = WhitespaceMode.LineFaithful;
            p.SelectedPrintScaling = PrintScalingMode.ActualSize;
            p.SelectedDocumentOpenMode = DocumentOpenMode.NewTab;
            p.SelectedAppearance = AppearanceMode.Dark;
            p.RunFormCalc = false;
            p.SelectedPerformancePreset = PerformancePreset.LowMemory;
        });
        moved.RedactionPreferences.Profile.Should().Be(RedactionProfile.Maximum, "the control: the move took");

        var prefs = new PreferencesViewModel();
        prefs.LoadFromMainViewModel(moved);
        await prefs.ResetToDefaultsCommand.Execute();
        moved.ApplySavedPreferences(prefs);

        var after = Launch();
        var defaults = new RedactionPreferences();
        after.RedactionPreferences.Should().Be(defaults, "Reset restores the redaction defaults (Standard, substring, attachments removed, FixedMarker, Strip)");
        after.ReadingOrderStrategy.Should().Be(ReadingOrderStrategy.ColumnAware);
        after.WhitespaceMode.Should().Be(WhitespaceMode.Smart);
        after.PrintScaling.Should().Be(PrintScalingMode.ShrinkOversized);
        after.DocumentOpenMode.Should().Be(DocumentOpenMode.Automatic);
        after.Appearance.Should().Be(AppearanceMode.System);
        after.RunFormCalc.Should().BeTrue();
        after.PerformanceSettings.Should().Be(PerformanceSettings.Balanced);
    }
}
