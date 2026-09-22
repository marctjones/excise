using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Excise.Avalonia.Tests.HeadlessTestApp))]

namespace Excise.Avalonia.Tests;

/// <summary>
/// Entry point for the headless Avalonia session used by the automation-peer
/// tests (#631) and the viewport/tile-budget tests (#1773). Pure headless
/// drawing — no Skia render platform — because none of these tests read
/// pixels, only the accessibility surface or measured layout.
/// </summary>
/// <remarks>
/// Loads FluentTheme (same as <c>Excise.App.Tests</c>' <c>TestApp</c>) even
/// though nothing here paints: a stock Avalonia control such as
/// <c>ScrollViewer</c> builds its OWN internal template (the
/// <c>ScrollContentPresenter</c> that reports <c>Extent</c>) from a
/// <c>ControlTheme</c> supplied by a loaded theme. Without one, a
/// <c>ScrollViewer</c>'s template never applies and it reports a zero
/// <c>Extent</c> no matter what content it holds or how many layout passes
/// run — measured while porting
/// <see cref="PdfViewerViewportDiagnosticsTests"/> here (#1773): it passed
/// under <c>Excise.App.Tests</c>' Skia-backed app and failed here until this
/// theme was added, so removing it would silently zero out that test again.
/// <c>PdfViewerControl</c> itself needs no theme, since its own template is
/// declared inline in its own .axaml file rather than built from a
/// <c>ControlTheme</c>.
/// </remarks>
public sealed class HeadlessTestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
