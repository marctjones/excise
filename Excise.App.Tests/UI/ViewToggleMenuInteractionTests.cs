using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using AwesomeAssertions;
using Excise.App.Tests.UI.InteractionCoverage;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Drives the eight view/tools toggle menu items (#1085) with REAL synthetic
/// input — a routed pointer press/release on the menu item (so
/// <see cref="GuiInteractionRecorder"/> counts it exactly as it counts a user
/// click) plus the real <c>MenuItem.Click</c> the pointer release stands in for
/// on an open menu — and asserts the bound ViewModel state actually flipped.
///
/// <para>Before this, these eight had ViewModel-level command tests but nothing
/// that reached the menu item through input, so a broken binding (a null
/// Command, a collapsed item, a mistyped property path) would not be noticed.
/// The pointer press is what the interaction-coverage gate measures; the
/// <c>Click</c> raise is what produces the effect the assertion checks, mirroring
/// <c>GuiToggleStateRegressionTests.Click</c>. The two together are one user
/// click.</para>
/// </summary>
[Collection("AvaloniaTests")]
public class ViewToggleMenuInteractionTests
{
    // menu item x:Name, ViewModel command property, ViewModel bound-state property.
    [InlineData("ViewAnnotationsMenuItem", "ToggleAnnotationsCommand", "AreAnnotationsVisible")]
    [InlineData("ViewCommentAnnotationsMenuItem", "ToggleCommentAnnotationsCommand", "AreCommentAnnotationsVisible")]
    [InlineData("ViewFieldAndLinkAnnotationsMenuItem", "ToggleFieldAndLinkAnnotationsCommand", "AreFieldAndLinkAnnotationsVisible")]
    [InlineData("ViewHighlightFormFieldsMenuItem", "ToggleFormFieldHighlightingCommand", "AreFormFieldsHighlighted")]
    [InlineData("ViewAnnotationAuditMenuItem", "ToggleAnnotationAuditModeCommand", "IsAnnotationAuditModeEnabled")]
    [InlineData("RevealHiddenTextMenuItem", "ToggleRevealHiddenTextCommand", "RevealHiddenText")]
    [InlineData("RevealRasterizedHiddenMenuItem", "ToggleRevealRasterizedHiddenCommand", "RevealRasterizedHidden")]
    [InlineData("ViewClipboardMenuItem", "ToggleClipboardSidebarCommand", "IsClipboardSidebarVisible")]
    [FixedAvaloniaTheory]
    public async Task PointerAndClick_OnToggleMenuItem_FlipsBoundViewModelState(
        string menuItemName, string commandProperty, string stateProperty)
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1200, Height = 900 };
        window.Show();

        try
        {
            await KeyboardTestHelpers.FlushDispatcherAsync();

            var item = window.GetLogicalDescendants()
                .OfType<MenuItem>()
                .FirstOrDefault(m => m.Name == menuItemName);
            item.Should().NotBeNull($"MainWindow.axaml must still declare the {menuItemName} toggle");
            item!.Command.Should().NotBeNull($"{menuItemName} must be bound to {commandProperty}");

            var before = ReadBool(vm, stateProperty);

            // Real pointer input at the menu item — this is what the coverage
            // gate records. It does not, on a closed menu, execute the command.
            RaisePointerPressRelease(item, window);
            await KeyboardTestHelpers.FlushDispatcherAsync();

            // The click the pointer release stands in for on an open menu, which
            // is what actually runs the command (the item keeps ToggleType only
            // for UIA; the VM state is flipped by the command, not by the item).
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await KeyboardTestHelpers.FlushDispatcherAsync();

            ReadBool(vm, stateProperty).Should().Be(!before,
                $"a real interaction on {menuItemName} must toggle {stateProperty} through {commandProperty}");

            GuiInteractionRecorder.ObservedIds.Should().Contain(
                id => id.StartsWith($"MainWindow/ToggleMenuItem:{commandProperty}\t", StringComparison.Ordinal),
                "the real pointer press must reach the interaction-coverage recorder under the " +
                "same id the inventory gives the menu item — this is what claims the coverage row back");
        }
        finally
        {
            window.Close();
        }
    }

    private static bool ReadBool(object vm, string property)
    {
        var pi = vm.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
        pi.Should().NotBeNull($"the ViewModel must expose {property}");
        return (bool)pi!.GetValue(vm)!;
    }

    private static void RaisePointerPressRelease(Control target, Visual root)
    {
        var pointer = new global::Avalonia.Input.Pointer(
            global::Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var pos = new Point(4, 4);
        target.RaiseEvent(new PointerPressedEventArgs(
            target, pointer, root, pos, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
        target.RaiseEvent(new PointerReleasedEventArgs(
            target, pointer, root, pos, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
    }
}
