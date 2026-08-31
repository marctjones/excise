using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using AwesomeAssertions;
using Excise.App.Tests.UI.InteractionCoverage;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Makes the three dialogs that were invisible to the interaction-coverage gate
/// visible to it (#1066): <see cref="SaveRedactedVersionDialog"/>,
/// <see cref="PreferencesWindow"/>, <see cref="AboutWindow"/>. The gate
/// enumerates a window's elements the first time input reaches it, so a window
/// no test ever touches contributes to neither numerator nor denominator — it
/// does not read as 0% covered, it is simply absent, which is the worst
/// direction to under-report in.
///
/// <para>Each test here raises a REAL pointer event inside the dialog, which
/// trips <c>GuiInteractionRecorder.NoteSurface</c> and enrols the whole dialog
/// into <c>gui-interaction-inventory.tsv</c>. From then on a dialog added with
/// no interactive automation is reported rather than silently missing — that is
/// the property being bought. <see cref="SaveRedactedVersionDialog"/> is the one
/// that matters: it is on the redaction path.</para>
/// </summary>
[Collection("AvaloniaTests")]
public class HiddenDialogCoverageTests
{
    [FixedAvaloniaFact]
    public async Task SaveRedactedVersionDialog_RealInput_EntersTheCoverageInventory()
    {
        var vm = new SaveRedactedVersionDialogViewModel("/tmp/document_REDACTED.pdf", 2);
        var window = new SaveRedactedVersionDialog { DataContext = vm };
        await DriveAndAssertVisible(
            window,
            "SaveRedactedVersionDialog",
            "Cancel Save Redacted Version",
            typeInto: new[] { "SavePathTextBox" });
    }

    [FixedAvaloniaFact]
    public async Task PreferencesWindow_RealInput_EntersTheCoverageInventory()
    {
        var window = new PreferencesWindow { DataContext = new PreferencesViewModel() };
        await DriveAndAssertVisible(
            window,
            "PreferencesWindow",
            "Cancel Preferences",
            arrowKey: new[] { "ReadingOrderStrategyComboBox", "WhitespaceModeComboBox" });
    }

    [FixedAvaloniaFact]
    public async Task AboutWindow_RealInput_EntersTheCoverageInventory()
    {
        var window = new AboutWindow();
        await DriveAndAssertVisible(
            window,
            "AboutWindow",
            "Close About Dialog");
    }

    /// <summary>
    /// Show the dialog, raise a real click on a safe (non-file-picker) button,
    /// and assert the interaction-coverage recorder saw an id under this
    /// surface — which is only possible if the dialog was enumerated into the
    /// inventory. This is the enrolment #1066 is about.
    /// </summary>
    private static async Task DriveAndAssertVisible(
        Window window, string surface, string buttonAutomationName,
        string[]? typeInto = null, string[]? arrowKey = null)
    {
        window.Show();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        try
        {
            // Cover the safe named inputs with real input so they are genuine
            // coverage rather than declared gaps. File-picker buttons and the
            // URL-launching / settings-persisting buttons are deliberately left
            // untouched (and declared as gaps) — driving them has side effects.
            foreach (var name in typeInto ?? Array.Empty<string>())
            {
                var box = window.GetLogicalDescendants().OfType<TextBox>().First(c => c.Name == name);
                box.Focus();
                await KeyboardTestHelpers.FlushDispatcherAsync();
                window.KeyTextInput("x");
                await KeyboardTestHelpers.FlushDispatcherAsync();
            }
            foreach (var name in arrowKey ?? Array.Empty<string>())
            {
                var ctl = window.GetLogicalDescendants().OfType<Control>().First(c => c.Name == name);
                ctl.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Route = global::Avalonia.Interactivity.RoutingStrategies.Bubble,
                    Key = Key.Down,
                });
                await KeyboardTestHelpers.FlushDispatcherAsync();
            }

            var button = window.GetLogicalDescendants()
                .OfType<Button>()
                .FirstOrDefault(b => AutomationProperties.GetName(b) == buttonAutomationName);
            button.Should().NotBeNull($"{surface} must host the '{buttonAutomationName}' button");
            window.UpdateLayout();

            // Raise the routed pointer pair directly on the button. This is real
            // synthetic pointer input the recorder counts, and unlike
            // window.MouseDown it does not depend on the dialog having been laid
            // out for hit-testing (SizeToContent dialogs are not, in headless).
            var pointer = new global::Avalonia.Input.Pointer(
                global::Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
            var pos = new Point(2, 2);
            button!.RaiseEvent(new PointerPressedEventArgs(
                button, pointer, window, pos, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None));
            button.RaiseEvent(new PointerReleasedEventArgs(
                button, pointer, window, pos, 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                KeyModifiers.None, MouseButton.Left));
            await KeyboardTestHelpers.FlushDispatcherAsync();
            await KeyboardTestHelpers.FlushDispatcherAsync();

            GuiInteractionRecorder.ObservedIds.Should().Contain(
                id => id.StartsWith(surface + "/", StringComparison.Ordinal),
                $"a real click inside {surface} must reach the recorder, which is what enrols the whole " +
                "dialog into the coverage inventory — the visibility #1066 buys");
        }
        finally
        {
            try { window.Close(); } catch { /* the click may already have closed it */ }
        }
    }
}
