using System;
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
///
/// The hook also GUARDS the redirection, before and after every test. One test
/// used to end the override in its <c>finally</c>, which put every later test in
/// the serial run on the user's real ~/Library/Application Support/Excise.App:
/// fixture PDFs landed in the real recent.txt, and this hook deleted the real
/// window.json, zoom.txt and preferences.json before each test. The check runs
/// before the deletes, and the After check names the test that broke isolation
/// rather than whichever test happens to run next.
/// </summary>
public sealed class ResetPersistedSettingsBeforeEachTest : BeforeAfterTestAttribute
{
    /// <summary>Incremented each time the hook fires — proves the assembly-level attribute is active.</summary>
    internal static int InvocationCount;

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        System.Threading.Interlocked.Increment(ref InvocationCount);
        AssertStorageIsRedirected($"before {Describe(methodUnderTest)}");
        TryDelete(AppPaths.WindowSettingsPath);   // continuous-scroll view-mode preference
        TryDelete(AppPaths.ZoomSettingsPath);
        TryDelete(AppPaths.PreferencesPath);
    }

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
        => AssertStorageIsRedirected($"after {Describe(methodUnderTest)}");

    /// <summary>
    /// Throws unless every per-user directory AppPaths hands out resolves under
    /// the assembly's temp override root. Checked against the root rather than
    /// against the real home, so a path that goes wrong in some new way fails too.
    /// </summary>
    internal static void AssertStorageIsRedirected(string context)
    {
        var root = AppPaths.OverrideRootForTests
            ?? throw new InvalidOperationException(
                $"AppPaths is not redirected for tests ({context}). Every later test would read and " +
                "write the user's real settings and caches. TestEnvironmentInitializer sets the " +
                "override once for the whole assembly; no test may replace or end it.");

        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        foreach (var (name, dir) in new[]
                 {
                     (nameof(AppPaths.ConfigDir), AppPaths.ConfigDir),
                     (nameof(AppPaths.DataDir), AppPaths.DataDir),
                     (nameof(AppPaths.CacheDir), AppPaths.CacheDir),
                     (nameof(AppPaths.ThumbnailCacheRoot), AppPaths.ThumbnailCacheRoot),
                 })
        {
            if (!Path.GetFullPath(dir).StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"AppPaths.{name} resolves to '{dir}', outside the test override root '{root}' " +
                    $"({context}). Tests must never reach the user's real directories.");
            }
        }
    }

    private static string Describe(MethodInfo method) => $"{method.DeclaringType?.FullName}.{method.Name}";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort — a locked file simply isn't reset */ }
    }
}
