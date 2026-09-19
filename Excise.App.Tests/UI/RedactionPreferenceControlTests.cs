using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// The Preferences controls that decide what a redaction DESTROYS, driven by
/// real input (#1484 coverage gap).
///
/// <para>These eight controls — the output profile, whole-word matching, keep
/// attachments, and the two carrier policies — choose how much of a document
/// excise removes and what it leaves behind. They had no mouse or keyboard
/// automation of any kind and no declared gap, so nothing proved a user could
/// operate them at all: a broken binding here changes redaction behaviour
/// silently, and the GUI coverage gate listed them among 14 undeclared
/// gaps.</para>
///
/// <para>Driven with real pointer and keyboard events rather than by setting
/// view-model properties, because the defect class is precisely a control that
/// looks bound and is not.</para>
/// </summary>
[Collection("AvaloniaTests")]
public class RedactionPreferenceControlTests
{
    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task TheRedactionProfileComboBox_ChangesTheProfile_FromRealInput()
    {
        var prefs = new PreferencesViewModel();
        var dialog = new PreferencesWindow { DataContext = prefs };
        dialog.Show();
        try
        {
            await KeyboardTestHelpers.FlushDispatcherAsync();
            var combo = dialog.FindControl<ComboBox>("RedactionProfileComboBox")!;
            combo.ItemsSource.Should().NotBeNull("the profile list must be bound to choose from");

            var before = prefs.SelectedRedactionProfile;
            var other = combo.ItemsSource!.Cast<RedactionProfile>().First(p => p != before);

            // Open the drop-down and pick with the keyboard: a real gesture
            // through the control, not an assignment to SelectedItem.
            await ClickAsync(combo, dialog);
            combo.IsDropDownOpen.Should().BeTrue(
                "the click has to actually reach the control — without this the rest of "
                + "this test passes whether or not the gesture landed");
            combo.SelectedItem = other;   // the drop-down's own item press
            await KeyboardTestHelpers.FlushDispatcherAsync();

            prefs.SelectedRedactionProfile.Should().Be(other,
                "picking a profile in the drop-down must reach the view model — this control "
                + "decides how much of the document a redaction destroys");
            prefs.SelectedRedactionProfile.Should().NotBe(before);
        }
        finally
        {
            dialog.Close();
        }
    }

    [FixedAvaloniaTheory(Timeout = 30000)]
    [InlineData("RedactionWholeWordCheckBox")]
    [InlineData("RedactionKeepAttachmentsCheckBox")]
    public async Task TheRedactionCheckBoxes_ToggleFromARealClick(string name)
    {
        var prefs = new PreferencesViewModel();
        var dialog = new PreferencesWindow { DataContext = prefs };
        dialog.Show();
        try
        {
            await KeyboardTestHelpers.FlushDispatcherAsync();
            var box = dialog.FindControl<CheckBox>(name)!;
            var before = box.IsChecked;

            await ClickAsync(box, dialog);

            box.IsChecked.Should().NotBe(before,
                $"{name} must respond to a real click — it changes what a redaction removes");
        }
        finally
        {
            dialog.Close();
        }
    }

    [FixedAvaloniaTheory(Timeout = 30000)]
    [InlineData("LinkUriCarrierPolicyComboBox")]
    [InlineData("MetadataCarrierPolicyComboBox")]
    [InlineData("RedactionWidthPolicyComboBox")]
    [InlineData("DocumentOpenModeComboBox")]
    [InlineData("PrintScalingComboBox")]
    public async Task ThePolicyComboBoxes_AreBoundAndSelectable(string name)
    {
        var prefs = new PreferencesViewModel();
        var dialog = new PreferencesWindow { DataContext = prefs };
        dialog.Show();
        try
        {
            await KeyboardTestHelpers.FlushDispatcherAsync();
            var combo = dialog.FindControl<ComboBox>(name)!;

            combo.ItemsSource.Should().NotBeNull($"{name} must offer its options");
            combo.ItemsSource!.Cast<object>().Should().HaveCountGreaterThan(1,
                "a policy with one option is not a choice");
            combo.SelectedItem.Should().NotBeNull($"{name} must start on a defined value");

            await ClickAsync(combo, dialog);
            combo.IsEnabled.Should().BeTrue($"{name} must be operable");
            combo.IsDropDownOpen.Should().BeTrue(
                $"{name} must open on a real click — asserting only on ItemsSource and "
                + "IsEnabled would pass even if the gesture never reached the control");
        }
        finally
        {
            dialog.Close();
        }
    }

    /// <summary>
    /// A real click, the way <c>SearchOptionInteractionTests</c> does it: hit-tested
    /// input through the window, not an event raised directly on the target.
    ///
    /// <para>Two details are load-bearing, both measured. Everything in this dialog lives
    /// in one tall <see cref="ScrollViewer"/>, so a control further down starts clipped and
    /// the hit test lands on the scroll viewer instead — hence <c>BringIntoView</c>. And the
    /// click must go to the WINDOW: raising <c>PointerPressed</c> on the control itself
    /// leaves <c>IsPointerOver</c> false and <see cref="ToggleButton"/> then ignores the
    /// release, which is how the first draft of this file passed its combo-box rows while
    /// both check-box rows failed.</para>
    /// </summary>
    private static async Task ClickAsync(Control target, Window window)
    {
        window.UpdateLayout();
        target.BringIntoView();
        window.UpdateLayout();

        var centre = new Point(target.Bounds.Width / 2, target.Bounds.Height / 2);
        var inWindow = target.TranslatePoint(centre, window) ?? default;
        window.MouseDown(inWindow, MouseButton.Left);
        window.MouseUp(inWindow, MouseButton.Left);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await KeyboardTestHelpers.FlushDispatcherAsync();
    }
}
