using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Excise.Avalonia.Tests.HeadlessTestApp))]

namespace Excise.Avalonia.Tests;

/// <summary>
/// Entry point for the headless Avalonia session used by the automation-peer
/// tests (#631). Pure headless drawing — no Skia render platform — because
/// peer-tree tests only exercise the accessibility surface, never pixels.
/// </summary>
public sealed class HeadlessTestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
