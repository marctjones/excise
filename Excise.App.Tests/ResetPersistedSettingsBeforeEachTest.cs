using System.IO;
using System.Reflection;
using Excise.App.Services;
using Xunit.v3;

// Applied assembly-wide: runs before every test in Excise.App.Tests.
[assembly: Excise.App.Tests.ResetPersistedSettingsBeforeEachTest]

namespace Excise.App.Tests;

/// <summary>
/// Test isolation for the app's persisted settings.
///
/// <see cref="TestEnvironmentInitializer"/> redirects AppPaths-backed storage
/// (window.json, zoom.txt, preferences.json, …) into ONE temp directory shared
/// by the whole test assembly. That stops the user's real config from being
/// touched, but it also means every test shares those files — so state one test
/// persists leaks into the next.
///
/// The concrete failure this fixes: <c>MainWindow</c> persists the
/// continuous-scroll VIEW-MODE preference to <c>window.json</c> on close
/// (MainWindow.axaml.cs). A test that turns continuous scroll OFF (e.g.
/// GuiToggleStateRegressionTests) therefore leaves <c>window.json</c> saying
/// single-page, and the NEXT test's fresh <c>MainWindow</c> loads it and
/// defaults to single-page — failing every later test that expects the
/// continuous default. Because this only bites once enough tests have run in one
/// process, it reproduces ONLY in the full serial suite (the tests pass in
/// isolation), which is exactly the develop macOS-CI contamination that fails
/// ~9 continuous-view tests.
///
/// Deleting the redirected config files before each test gives every test a
/// clean default state, independent of run order.
/// </summary>
public sealed class ResetPersistedSettingsBeforeEachTest : BeforeAfterTestAttribute
{
    /// <summary>Incremented each time the hook fires — proves the assembly-level attribute is active.</summary>
    internal static int InvocationCount;

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        System.Threading.Interlocked.Increment(ref InvocationCount);
        TryDelete(AppPaths.WindowSettingsPath);   // continuous-scroll view-mode preference
        TryDelete(AppPaths.ZoomSettingsPath);
        TryDelete(AppPaths.PreferencesPath);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort — a locked file simply isn't reset */ }
    }
}
