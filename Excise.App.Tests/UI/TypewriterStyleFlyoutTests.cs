using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Controls;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Core.Document;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1476 follow-up: the typewriter style inspector — font size, alignment AND
/// the eight colour presets — moved from three inline toolbar controls into a
/// single flyout, because inline it cost the toolbar ~330 px and forced the
/// WHOLE row to icon-only even on a 1600 px window: every unrelated label lost
/// for the duration of a text edit.
///
/// <para>Moving controls into a flyout is exactly the change that silently
/// breaks them: flyout content is not in the window's tree until the flyout is
/// opened, so its bindings are unresolved and <c>window.FindControl</c> cannot
/// see it. These tests therefore OPEN the flyout and then check what a user and
/// a screen reader actually get — the automation names, and that both bindings
/// still carry a value in both directions.</para>
///
/// <para><see cref="ToolbarWidthTests"/> owns the width consequence,
/// <see cref="TypewriterColorPresetAccessibilityTests"/> owns the colour
/// presets inside the same flyout, and this file owns the size and alignment
/// controls that have no menu equivalent at all.</para>
/// </summary>
[Collection("AvaloniaTests")]
public class TypewriterStyleFlyoutTests
{
    [FixedAvaloniaFact]
    public async Task TheFlyout_CarriesTheSizeAndAlignmentControls_WithTheirAutomationNames()
    {
        var (_, window, pdf) = await OpenWithActiveTextBoxAsync();
        try
        {
            var (button, content) = OpenStyleFlyout(window);

            PriorityToolbarPanel.GetPriority(button).Should().Be(97,
                "the inspector has no menu equivalent, so it must be among the last things hidden");
            AutomationProperties.GetName(button).Should().Be("Typewriter Text Style");
            ToolTip.GetTip(button).Should().NotBeNull("the icon-only button must say what it opens");

            var size = Find<NumericUpDown>(content, "TypewriterFontSizeUpDown");
            var alignment = Find<ComboBox>(content, "TypewriterAlignmentComboBox");

            AutomationProperties.GetName(size).Should().Be("Typewriter Font Size");
            AutomationProperties.GetName(alignment).Should().Be("Typewriter Text Alignment");
            AutomationProperties.GetHelpText(size).Should().NotBeNullOrWhiteSpace();
            AutomationProperties.GetHelpText(alignment).Should().NotBeNullOrWhiteSpace();

            alignment.Items.Should().HaveCount(3, "left, center and right, as before the move");

            // The colour presets share this flyout now; their own naming and
            // reachability are pinned by TypewriterColorPresetAccessibilityTests.
            content.GetLogicalDescendants().OfType<Button>()
                .Count(b => b.CommandParameter is string hex && hex.StartsWith('#'))
                .Should().Be(8, "size, alignment and all eight colours must be in ONE flyout");

            // The toolbar's :icon-only style hides TextBlock.toolbar-label, and
            // flyout content is logically parented to the toolbar — so a label
            // class in here would vanish whenever the toolbar is narrow.
            content.GetLogicalDescendants().OfType<TextBlock>()
                .Should().OnlyContain(t => !t.Classes.Contains("toolbar-label"),
                    "nothing inside the flyout may carry toolbar-label: the :icon-only style would hide it");
        }
        finally
        {
            Close(window, pdf);
        }
    }

    [FixedAvaloniaFact]
    public async Task TheFlyoutControls_StillRoundTripTheirTwoWayBindings()
    {
        var (vm, window, pdf) = await OpenWithActiveTextBoxAsync();
        try
        {
            var (_, content) = OpenStyleFlyout(window);
            var size = Find<NumericUpDown>(content, "TypewriterFontSizeUpDown");
            var alignment = Find<ComboBox>(content, "TypewriterAlignmentComboBox");

            // ViewModel -> control.
            vm.TypewriterFontSize = 31;
            vm.TypewriterAlignmentIndex = 2;
            await KeyboardTestHelpers.FlushDispatcherAsync();

            size.Value.Should().Be(31m, "TypewriterFontSize must still reach the control inside the flyout");
            alignment.SelectedIndex.Should().Be(2, "TypewriterAlignmentIndex must still reach the control inside the flyout");

            // Control -> ViewModel.
            size.Value = 17m;
            alignment.SelectedIndex = 1;
            await KeyboardTestHelpers.FlushDispatcherAsync();

            vm.TypewriterFontSize.Should().Be(17, "the flyout's size control must still write back");
            vm.TypewriterAlignmentIndex.Should().Be(1, "the flyout's alignment control must still write back");

            var style = vm.TypewriterTextOperations.Single().Style;
            style.FontSize.Should().Be(17, "the flyout must restyle the active type-over box, as the inline controls did");
        }
        finally
        {
            Close(window, pdf);
        }
    }

    /// <summary>
    /// The button is a new interactive affordance, so it needs a real pointer
    /// event to enter the GUI interaction-coverage NUMERATOR (#1021 follow-up);
    /// without one it would appear in the gap list at
    /// <c>tests/gui-interaction-coverage.tsv</c> instead.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task TheFlyoutButton_IsReachedByARealPointerEvent()
    {
        var (_, window, pdf) = await OpenWithActiveTextBoxAsync();
        try
        {
            var button = window.FindControl<Button>("TypewriterStyleFlyoutButton")!;
            button.Should().NotBeNull();
            button.IsVisible.Should().BeTrue("the inspector is available while a type-over box is active");

            PressAndRelease(button, window);
            await KeyboardTestHelpers.FlushDispatcherAsync();

            InteractionCoverage.GuiInteractionRecorder.ObservedIds.Should().Contain(
                id => id.StartsWith("MainWindow/FlyoutButton:TypewriterStyleFlyoutButton", StringComparison.Ordinal),
                "the pointer press must reach the interaction-coverage recorder under the button's inventory id");
        }
        finally
        {
            Close(window, pdf);
        }
    }

    /// <summary>
    /// Opens the style flyout and returns its root content. A flyout's content
    /// has no DataContext until it is shown, so every binding assertion has to
    /// come after this. <see cref="Flyout.ShowAt(Control)"/> rather than a
    /// synthetic click: a synthetic pointer sequence does not acquire the
    /// pointer capture <see cref="Button"/> needs to raise its own Click.
    /// </summary>
    private static (Button Button, Control Content) OpenStyleFlyout(MainWindow window)
    {
        var button = window.FindControl<Button>("TypewriterStyleFlyoutButton");
        button.Should().NotBeNull("the toolbar must carry the typewriter style flyout button");

        var flyout = button!.Flyout as Flyout;
        flyout.Should().NotBeNull("the size and alignment controls must live in a Flyout, not inline in the toolbar");

        flyout!.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var content = flyout.Content as Control;
        content.Should().NotBeNull();
        return (button, content!);
    }

    private static T Find<T>(Control content, string name) where T : Control
    {
        var found = content.GetLogicalDescendants().OfType<T>()
            .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
        found.Should().NotBeNull(
            $"{name} must keep its x:Name inside the flyout: the GUI-coverage ids are keyed on it");
        return found!;
    }

    private static void PressAndRelease(Control target, Visual root)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var position = new Point(4, 4);
        target.RaiseEvent(new PointerPressedEventArgs(
            target, pointer, root, position, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
        target.RaiseEvent(new PointerReleasedEventArgs(
            target, pointer, root, position, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
    }

    private static async Task<(MainWindowViewModel Vm, MainWindow Window, string Pdf)> OpenWithActiveTextBoxAsync()
    {
        var pdf = Path.Combine(Path.GetTempPath(), $"excise-1476-style-flyout-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(pdf, "Typewriter style flyout");

        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(pdf);
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.IsTypewriterMode = true;
        vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        window.UpdateLayout();

        vm.IsTypewriterStyleInspectorVisible.Should().BeTrue(
            "the inspector must be available before its flyout can be checked");
        return (vm, window, pdf);
    }

    private static void Close(Window window, string pdf)
    {
        window.Close();
        TestPdfGenerator.CleanupTestFile(pdf);
    }
}
