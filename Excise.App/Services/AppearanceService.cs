using Avalonia;
using Avalonia.Styling;
using Excise.App.Models;

namespace Excise.App.Services;

/// <summary>
/// Applies the Appearance preference (#2002) to the application. App-wide, so
/// every window and dialog switches together; <see cref="AppearanceMode.System"/>
/// hands the choice back to the OS (FluentAvaloniaTheme follows it live).
/// </summary>
internal static class AppearanceService
{
    public static ThemeVariant VariantFor(AppearanceMode mode) => mode switch
    {
        AppearanceMode.Light => ThemeVariant.Light,
        AppearanceMode.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    public static void Apply(Application? application, AppearanceMode mode)
    {
        if (application != null)
            application.RequestedThemeVariant = VariantFor(mode);
    }
}
