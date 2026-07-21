using Avalonia;
using Avalonia.Headless;
using ReactiveUI;

// Registers the headless app builder used by [FixedAvaloniaFact] tests across the
// assembly. Required for UseHeadlessDrawing=false to take effect so that
// Bitmap(stream) decodes real pixels (visual-regression tests rely on this).
[assembly: global::Avalonia.Headless.AvaloniaTestApplication(typeof(Excise.App.Tests.UI.TestAppBuilder))]

namespace Excise.App.Tests.UI;

/// <summary>
/// Application builder for headless UI tests
/// </summary>
public class TestAppBuilder
{
    private static bool _isInitialized;
    private static readonly object _lock = new object();

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia()
        // Wire ReactiveUI to the Avalonia (headless) dispatcher exactly as the
        // real app does in Program.cs (vendored scheduler, #593). Without
        // this, RxApp.MainThreadScheduler is not the UI dispatcher, so a
        // CreateFromTask command's IsExecuting/CanExecute notifications fire
        // on the thread-pool continuation and Button/MenuItem.get_Command()
        // throws a cross-thread InvalidOperation exception during/after the
        // test. (Issue #358)
        .AfterSetup(_ =>
            RxSchedulers.MainThreadScheduler = Excise.App.Threading.AvaloniaDispatcherScheduler.Instance)
        .UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            // Route drawing through the real Skia backend so that Bitmap(stream)
            // decodes to the correct dimensions (default null renderer returns 1x1),
            // enabling visual-regression tests to capture actual rendered pixels.
            UseHeadlessDrawing = false
        });

    /// <summary>
    /// Ensures Avalonia is initialized only once. Thread-safe.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_isInitialized) return;

        lock (_lock)
        {
            if (_isInitialized) return;

            try
            {
                BuildAvaloniaApp().SetupWithoutStarting();
                _isInitialized = true;
            }
            catch (InvalidOperationException)
            {
                // Already initialized by another test - this is fine
                _isInitialized = true;
            }
        }
    }
}

/// <summary>
/// Minimal Avalonia application for testing.
/// Loads FluentTheme so controls (TreeView, TextBox, Button) actually
/// build their visual templates — without it they're invisible nodes
/// with no descendants, breaking any click-simulation test.
/// </summary>
public class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new global::Avalonia.Themes.Fluent.FluentTheme());
    }
}
