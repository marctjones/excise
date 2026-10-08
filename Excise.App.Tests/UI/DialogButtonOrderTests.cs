using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using AwesomeAssertions;
using Excise.App.Controls;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1999 — dialog buttons follow the platform: Windows puts the default action
/// first (Primary, Extra, Cancel); macOS and Linux put it last with any extra
/// action apart on the left (Extra … Cancel, Primary). Children are reordered,
/// not only arranged, so Tab order matches what the user sees.
/// </summary>
[Collection("AvaloniaTests")]
public class DialogButtonOrderTests
{
    private static string[] FooterOrder(Window window) =>
        window.GetLogicalDescendants().OfType<DialogButtonPanel>().Single()
            .Children.OfType<Button>()
            .Select(b => global::Avalonia.Automation.AutomationProperties.GetName(b) ?? b.Content?.ToString() ?? "")
            .ToArray();

    private static async Task<string[]> OrderOn(bool windows, Func<Window> create)
    {
        DialogButtonPanel.WindowsOrderOverride = windows;
        try
        {
            var window = create();
            window.Show();
            await KeyboardTestHelpers.FlushDispatcherAsync();
            var order = FooterOrder(window);
            window.Close();
            return order;
        }
        finally
        {
            DialogButtonPanel.WindowsOrderOverride = null;
        }
    }

    private static SecurityDialog Security() => new()
    {
        DataContext = new SecurityDialogViewModel(
            true, _ => true, (_, _, _) => Task.FromResult<string?>(null), () => Task.FromResult<string?>(null)),
    };

    [FixedAvaloniaFact]
    public async Task Security_Windows_PrimaryFirst()
    {
        (await OrderOn(windows: true, Security)).Should().Equal(
            "Apply Security Settings", "Remove Password Protection", "Close Security Dialog");
    }

    [FixedAvaloniaFact]
    public async Task Security_MacAndLinux_ExtraLeft_PrimaryLast()
    {
        (await OrderOn(windows: false, Security)).Should().Equal(
            "Remove Password Protection", "Close Security Dialog", "Apply Security Settings");
    }

    [FixedAvaloniaFact]
    public async Task Preferences_FollowsThePlatform()
    {
        Window Prefs() => new PreferencesWindow { DataContext = new PreferencesViewModel() };

        (await OrderOn(windows: true, Prefs)).Should().Equal(
            "Reset Preferences to Defaults", "Close Preferences");
        (await OrderOn(windows: false, Prefs)).Should().Equal(
            "Reset Preferences to Defaults", "Close Preferences");
    }

    [FixedAvaloniaFact]
    public async Task Bates_TwoButtons_SwapByPlatform()
    {
        Window Bates() => new BatesNumberingDialog { DataContext = new BatesNumberingDialogViewModel() };

        (await OrderOn(windows: true, Bates)).Should().Equal("Apply Bates Numbering", "Cancel Bates Numbering");
        (await OrderOn(windows: false, Bates)).Should().Equal("Cancel Bates Numbering", "Apply Bates Numbering");
    }

    [FixedAvaloniaFact]
    public async Task MacOrder_PutsTheExtraActionAtTheLeftEdge_AndTheRestAtTheRight()
    {
        DialogButtonPanel.WindowsOrderOverride = false;
        try
        {
            var window = Security();
            window.Width = 480;
            window.Show();
            await KeyboardTestHelpers.FlushDispatcherAsync();
            var panel = window.GetLogicalDescendants().OfType<DialogButtonPanel>().Single();
            var buttons = panel.Children.OfType<Button>().ToArray();

            buttons[0].Bounds.X.Should().BeApproximately(0, 0.5, "Remove Protection sits at the panel's left edge");
            buttons[^1].Bounds.Right.Should().BeApproximately(panel.Bounds.Width, 0.5, "Apply sits at the right edge");
            (buttons[1].Bounds.X - buttons[0].Bounds.Right).Should().BeGreaterThan(panel.Spacing,
                "the extra action stands apart from the Cancel/Apply pair");
            window.Close();
        }
        finally
        {
            DialogButtonPanel.WindowsOrderOverride = null;
        }
    }
}
