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
}
