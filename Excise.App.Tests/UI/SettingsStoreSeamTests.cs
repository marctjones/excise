using System;
using System.IO;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Services;
using Excise.App.Services.Host;
using Excise.App.Tests.Utilities;
using Excise.App.Tests.Utilities.Fakes;
using Excise.App.ViewModels;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1500 step 2: <see cref="ISettingsStore"/> and
/// <see cref="IRecentFilesStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// What is new here is seeding. Persisted state previously reached the view
/// model only by writing the real <c>window.json</c>, <c>zoom.txt</c> or
/// <c>recent.txt</c> into the shared, redirected test config directory — the
/// same mechanism behind the view-mode contamination that only reproduced in a
/// full serial run. A seeded in-memory store lets a test say "a previous
/// session left this" and assert what the view model does with it, without
/// touching disk and without leaking into the next test.
/// </para>
/// <para>
/// The split these pin is mechanism vs. policy: the store hands back whatever
/// was stored, and the view model keeps the rules — the zoom range check, the
/// ten-entry MRU cap, and dropping recent files that no longer exist.
/// </para>
/// </remarks>
[Collection("AvaloniaTests")]
public class SettingsStoreSeamTests
{
    [Fact]
    public void ZoomPreference_IsRestoredFromTheStoreOnConstruction()
    {
        var store = new InMemorySettingsStore().WithZoom(1.75);

        var vm = MainWindowViewModelTestFactory.Create(settingsStore: store);

        vm.ZoomLevel.Should().Be(1.75);
    }

    [Theory]
    // Below DocumentViewportSession.MinimumZoom and above MaximumZoom. The
    // store returns these unchanged; rejecting them is the viewport's rule, and
    // it stays in the view model rather than moving into the persistence layer.
    [InlineData(0.01)]
    [InlineData(99.0)]
    public void ZoomPreference_OutsideTheSupportedRange_IsIgnored(double persisted)
    {
        var store = new InMemorySettingsStore().WithZoom(persisted);

        var vm = MainWindowViewModelTestFactory.Create(settingsStore: store);

        vm.ZoomLevel.Should().Be(1.0, "an out-of-range persisted zoom leaves the default in place");
    }

    [Fact]
    public void ZoomPreference_IsWrittenThroughTheStore_NotToDisk()
    {
        var store = new InMemorySettingsStore();
        var vm = MainWindowViewModelTestFactory.Create(settingsStore: store);

        vm.SetManualZoom(2.0);

        store.SaveZoomCount.Should().BeGreaterThan(0);
        store.LoadZoom().Should().Be(2.0);
    }

    [Fact]
    public void RecentFiles_AreLoadedFromTheStore_DroppingEntriesWhoseFileIsGone()
    {
        // The #25 rule lives in the view model; the store returns what it holds.
        var present = Path.Combine(Path.GetTempPath(), $"excise-recent-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(present, "not really a pdf, only its existence matters here");
        try
        {
            var missing = Path.Combine(Path.GetTempPath(), $"excise-gone-{Guid.NewGuid():N}.pdf");
            var store = new InMemoryRecentFilesStore(present, missing);

            var vm = MainWindowViewModelTestFactory.Create(recentFilesStore: store);

            vm.RecentFiles.Should().ContainSingle().Which.Should().Be(present);
            vm.HasRecentFiles.Should().BeTrue();
        }
        finally
        {
            File.Delete(present);
        }
    }

    [Fact]
    public void RecentFiles_AreCappedAtTen_OnLoad()
    {
        var created = new string[12];
        for (int i = 0; i < created.Length; i++)
        {
            created[i] = Path.Combine(Path.GetTempPath(), $"excise-mru-{i}-{Guid.NewGuid():N}.pdf");
            File.WriteAllText(created[i], "x");
        }

        try
        {
            var vm = MainWindowViewModelTestFactory.Create(
                recentFilesStore: new InMemoryRecentFilesStore(created));

            vm.RecentFiles.Should().HaveCount(10, "the MRU keeps at most ten entries");
            vm.RecentFiles.Should().Equal(created[..10], "in stored order, newest first");
        }
        finally
        {
            foreach (var path in created)
                File.Delete(path);
        }
    }

    [Fact]
    public void AViewModelGivenInMemoryStores_NeverWritesTheRealSettingsFiles()
    {
        // This is the leak class losing its mechanism rather than its symptom:
        // ResetPersistedSettingsBeforeEachTest deletes these files before every
        // test, which is a cleanup. A view model on an in-memory store has
        // nothing to clean up.
        ResetPersistedSettingsBeforeEachTest.AssertStorageIsRedirected(
            "fixture for " + nameof(AViewModelGivenInMemoryStores_NeverWritesTheRealSettingsFiles));

        var vm = MainWindowViewModelTestFactory.Create(
            settingsStore: new InMemorySettingsStore(),
            recentFilesStore: new InMemoryRecentFilesStore());

        vm.SetManualZoom(1.5);

        File.Exists(AppPaths.ZoomSettingsPath).Should().BeFalse(
            "the zoom preference went to the in-memory store");
        File.Exists(AppPaths.WindowSettingsPath).Should().BeFalse(
            "nothing wrote window.json");
    }

    [Fact]
    public void InMemoryStore_Update_IsReadModifyWrite_AndDoesNotHandOutItsLiveInstance()
    {
        // The file store's Load() deserializes a fresh object each call, so a
        // fake that returned its live instance would let a caller mutate
        // persisted state without an Update — a habit that silently does
        // nothing in production.
        var store = new InMemorySettingsStore();

        var loaded = store.Load();
        loaded.ContinuousScrollEnabled = false;

        store.Load().ContinuousScrollEnabled.Should().BeTrue(
            "mutating a loaded copy must not persist anything");

        store.Update(s => s.ContinuousScrollEnabled = false);

        store.Load().ContinuousScrollEnabled.Should().BeFalse("Update persists");
        store.UpdateCount.Should().Be(1);
    }

    [Fact]
    public void InMemoryStore_PreservesEveryFieldTheFileStorePersists()
    {
        // The copy goes through the source-generated JSON context, the same one
        // WindowSettings.Save/Load uses, so the fake cannot quietly forget a
        // field the real file round-trips.
        var seed = new WindowSettings
        {
            Width = 1234,
            Height = 567,
            IsMaximized = true,
            ContinuousScrollEnabled = false,
            ReadingOrderStrategy = "Raw",
            WhitespaceMode = "Preserve",
            RedactionWholeWord = true,
            PerformancePreset = "LowMemory",
            TileCacheBudgetMb = 42,
        };

        var reloaded = new InMemorySettingsStore(seed).Load();

        reloaded.Width.Should().Be(1234);
        reloaded.Height.Should().Be(567);
        reloaded.IsMaximized.Should().BeTrue();
        reloaded.ContinuousScrollEnabled.Should().BeFalse();
        reloaded.ReadingOrderStrategy.Should().Be("Raw");
        reloaded.WhitespaceMode.Should().Be("Preserve");
        reloaded.RedactionWholeWord.Should().BeTrue();
        reloaded.PerformancePreset.Should().Be("LowMemory");
        reloaded.TileCacheBudgetMb.Should().Be(42);
    }

    [Fact]
    public void FileSettingsStore_LoadZoom_IsNull_WhenNothingIsStored()
    {
        // ResetPersistedSettingsBeforeEachTest deletes zoom.txt before each test.
        new FileSettingsStore().LoadZoom().Should().BeNull();
    }

    [Fact]
    public void FileSettingsStore_LoadZoom_IsNull_WhenTheStoredValueIsNotANumber()
    {
        File.WriteAllText(AppPaths.ZoomSettingsPath, "not a number");

        new FileSettingsStore().LoadZoom().Should().BeNull(
            "an unparseable file must read as absent, not throw");
    }

    [Fact]
    public void FileSettingsStore_RoundTripsZoom_InvariantCulture()
    {
        var store = new FileSettingsStore();

        store.SaveZoom(1.25);

        store.LoadZoom().Should().Be(1.25);
        File.ReadAllText(AppPaths.ZoomSettingsPath).Trim().Should().Be("1.25",
            "persisted with the invariant decimal point, so a comma-decimal locale still reads it back");
    }

    [Fact]
    public void FileRecentFilesStore_IsEmpty_WhenNothingIsStored()
    {
        var path = AppPaths.RecentFilesPath;
        if (File.Exists(path))
            File.Delete(path);

        new FileRecentFilesStore().Load().Should().BeEmpty();
    }

    [Fact]
    public void FileRecentFilesStore_RoundTripsOnePathPerLine()
    {
        var store = new FileRecentFilesStore();
        var paths = new[] { "/tmp/one.pdf", "/tmp/two.pdf" };

        store.Save(paths);

        store.Load().Should().Equal(paths);
    }
}
