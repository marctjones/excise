using System;
using System.IO;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Services;
using Xunit;

namespace Excise.App.Tests;

/// <summary>
/// Verifies the assembly-wide per-test settings reset (the fix for the develop
/// full-suite continuous-view contamination) actually fires and clears state.
/// </summary>
public class ResetPersistedSettingsBeforeEachTestTests
{
    [Fact]
    public void ResetHook_FiresBeforeEachTest()
    {
        // If the assembly-level [assembly: ResetPersistedSettingsBeforeEachTest]
        // is active, Before() has run at least once (for THIS test).
        ResetPersistedSettingsBeforeEachTest.InvocationCount.Should().BeGreaterThan(0,
            "the assembly-level BeforeAfterTest hook must run before every test");
    }

    [Fact]
    public void PersistedSinglePagePreference_IsClearedBeforeTheTest()
    {
        // Simulate a prior test having saved single-page, then confirm a NEW load
        // reads the default (continuous) — i.e. the reset removed the stale file.
        // The reset ran before THIS test, so window.json is already gone; writing
        // it here and reloading proves the load path defaults correctly, and the
        // absence assertion proves the reset cleared any prior contamination.
        File.Exists(AppPaths.WindowSettingsPath).Should().BeFalse(
            "the per-test reset must delete window.json before each test so no test inherits a saved view-mode preference");

        // And the persisted-preference contract the contamination depended on:
        // a freshly loaded WindowSettings (no file) reports the continuous default.
        WindowSettings.Load().ContinuousScrollEnabled.Should().BeTrue(
            "with no persisted file, continuous scroll is the default");
    }

    [Fact]
    public void AppPaths_NeverResolvesToTheRealUserDirectories()
    {
        // A test run once wrote fixture PDFs into the real
        // ~/Library/Application Support/Excise.App/recent.txt: one test ended
        // the assembly-wide override and every later test ran un-redirected.
        // The hook's Before/After guard catches that for EVERY test; this pins
        // the property itself, compared against the real resolution and not
        // just against the override root.
        var realDirs = new[]
        {
            AppPaths.ResolveConfigDirFresh(),
            AppPaths.ResolveDataDirFresh(),
            AppPaths.ResolveCacheDirFresh(),
        };
        var livePaths = new[]
        {
            AppPaths.ConfigDir, AppPaths.DataDir, AppPaths.CacheDir, AppPaths.ThumbnailCacheRoot,
            AppPaths.WindowSettingsPath, AppPaths.RecentFilesPath, AppPaths.ZoomSettingsPath,
            AppPaths.PreferencesPath, AppPaths.ResponsivenessReportRequestPath,
        };

        AppPaths.OverrideRootForTests.Should().NotBeNull(
            "TestEnvironmentInitializer must redirect AppPaths before any test runs");
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        foreach (var path in livePaths)
        {
            Path.GetFullPath(path).Should().StartWith(tempRoot,
                $"{path} must live under the per-run temp root, never the user's home");
            foreach (var real in realDirs)
                path.Should().NotStartWith(real, "tests must never touch the user's real app directories");
        }

        // The thumbnail cache has its own production root (not CacheDir).
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AppPaths.ThumbnailCacheRoot.Should().NotStartWith(Path.Combine(home, "Library"))
            .And.NotStartWith(Path.Combine(home, ".cache"));
    }

    [Fact]
    public void Guard_AcceptsTheAssemblyOverride()
    {
        // The guard runs before and after every test; if it misfired on a
        // correctly redirected run, every test in the assembly would fail.
        var guard = () => ResetPersistedSettingsBeforeEachTest.AssertStorageIsRedirected("direct call");
        guard.Should().NotThrow();
    }
}
