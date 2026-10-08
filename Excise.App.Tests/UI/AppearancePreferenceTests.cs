using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #2002 — the Appearance preference: follows the system by default, persists,
/// round-trips through Preferences, and switches the whole application live.
/// </summary>
[Collection("AvaloniaTests")]
public class AppearancePreferenceTests
{
    [Theory]
    [InlineData("System", "Default")]
    [InlineData("Light", "Light")]
    [InlineData("Dark", "Dark")]
    public void Mode_MapsToThemeVariant(string mode, string variant)
    {
        AppearanceService.VariantFor(Enum.Parse<AppearanceMode>(mode)).Key.Should().Be(variant);
    }

    [Fact]
    public void Default_IsMatchSystem()
    {
        new WindowSettings().Appearance.Should().Be("System");
        new PreferencesViewModel().SelectedAppearance.Should().Be(AppearanceMode.System);
    }

    [FixedAvaloniaFact]
    public void Persists_ThroughWindowSettings_AndIgnoresUnknownValues()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        vm.Appearance = AppearanceMode.Dark;
        var settings = new WindowSettings();
        vm.WritePreferencesTo(settings);
        settings.Appearance.Should().Be("Dark");

        var restored = MainWindowViewModelTestFactory.Create();
        restored.ApplyAppearancePreference(settings.Appearance);
        restored.Appearance.Should().Be(AppearanceMode.Dark);

        restored.ApplyAppearancePreference("Sepia");
        restored.Appearance.Should().Be(AppearanceMode.Dark, "an unknown stored value must not reset the choice");
    }

    [FixedAvaloniaFact]
    public void RoundTrips_ThroughPreferences()
    {
        var main = MainWindowViewModelTestFactory.Create();
        var prefs = new PreferencesViewModel();
        prefs.LoadFromMainViewModel(main);
        prefs.SelectedAppearance.Should().Be(AppearanceMode.System);

        prefs.SelectedAppearance = AppearanceMode.Light;
        prefs.SaveToMainViewModel(main);
        main.Appearance.Should().Be(AppearanceMode.Light);

        prefs.ResetToDefaultsCommand.Execute().Subscribe();
        prefs.SelectedAppearance.Should().Be(AppearanceMode.System);
    }

    [FixedAvaloniaFact]
    public async Task Window_AppliesTheChoice_ToTheWholeApplication()
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            await KeyboardTestHelpers.FlushDispatcherAsync();
            app.RequestedThemeVariant.Should().Be(ThemeVariant.Default, "System is the default and hands the choice to the OS");

            vm.Appearance = AppearanceMode.Dark;
            await KeyboardTestHelpers.FlushDispatcherAsync();
            app.RequestedThemeVariant.Should().Be(ThemeVariant.Dark);
            window.ActualThemeVariant.Should().Be(ThemeVariant.Dark);

            vm.Appearance = AppearanceMode.Light;
            await KeyboardTestHelpers.FlushDispatcherAsync();
            app.RequestedThemeVariant.Should().Be(ThemeVariant.Light);
        }
        finally
        {
            window.Close();
            app.RequestedThemeVariant = original;
        }
    }
}
