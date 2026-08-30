using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #834 — the macOS application (app-name) menu. Regression guard for the bug
/// where the bold app menu showed Avalonia's built-in "About Avalonia" (opening
/// AboutAvaloniaDialog) instead of the app's own About. These tests assert the
/// menu MODEL that <see cref="App"/> installs on the Application during
/// Initialize, and that clicking "About Excise" opens the real About window.
///
/// The native macOS global menu bar itself cannot be exercised in the headless
/// test host, so this verifies the menu that feeds it (built by
/// <see cref="MacApplicationMenu"/>) plus the click→dialog dispatch — the two
/// things that were actually wrong.
/// </summary>
[Collection("AvaloniaTests")]
public class MacApplicationMenuTests
{
    [FixedAvaloniaFact]
    public void AppMenu_FirstItemIsAboutExcise_NotAboutAvalonia()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        var menu = MacApplicationMenu.Build(() => vm);

        var items = menu.Items.OfType<NativeMenuItem>().ToList();

        items.Should().NotBeEmpty();
        items[0].Header.Should().Be("About Excise",
            "the app menu's first item must be the app's own About — not Avalonia's default \"About Avalonia\"");
        items.Select(i => i.Header).Should().NotContain("About Avalonia");
        items.Select(i => i.Header).Should().Contain("Preferences…",
            "the app menu also carries Preferences (Quit/Hide/Services are appended by Avalonia)");
    }

    [FixedAvaloniaFact]
    public async Task ClickingAboutExcise_OpensTheAboutWindow()
    {
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        vm.MainWindowResolver = () => window;

        var menu = MacApplicationMenu.Build(() => vm);
        var about = menu.Items.OfType<NativeMenuItem>().First(i => i.Header == "About Excise");

        // Simulate a real menu click the way the native exporter would.
        ((INativeMenuItemExporterEventsImplBridge)about).RaiseClicked();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        var aboutWindow = window.OwnedWindows.OfType<AboutWindow>().SingleOrDefault();
        aboutWindow.Should().NotBeNull(
            "clicking \"About Excise\" must open the app's own About window — the #834 bug opened Avalonia's dialog");

        aboutWindow!.Title.Should().Be("About Excise");

        aboutWindow.Close();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        window.Close();
    }
}
