using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using AwesomeAssertions;
using Excise.App.Automation;
using Excise.App.Tests.UI.InteractionCoverage;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1476: the toolbar may now hide any action with a command, so every such
/// action needs a menu entry. Three had none: Form Authoring Mode, Auto-detect
/// Form Fields and the typewriter colour swatches. (The issue also listed
/// <c>FindCommand</c>; that is the search bar's Find button, not a toolbar item.
/// The toolbar's Find opens the search bar and was already Edit &gt; Find….)
///
/// <para>Each new entry is driven by a real pointer press plus the click it
/// stands for, the <see cref="ViewToggleMenuInteractionTests"/> pattern. That
/// claims the entry's row in the GUI interaction-coverage gate, and it proves the
/// entry does what the button does. On macOS the in-window menu is hidden, so the
/// same actions are checked in <see cref="MacNativeMenuBuilder"/>.</para>
/// </summary>
[Collection("AvaloniaTests")]
public class ToolbarOverflowMenuEntriesTests
{
    [FixedAvaloniaFact]
    public async Task FormAuthoringMenuEntry_IsPointerReachable_AndTogglesTheMode()
    {
        var (vm, window, pdf) = await OpenAsync();
        try
        {
            var item = MenuItemFor(window, "form.toggleAuthoring");
            vm.IsFormAuthoringMode.Should().BeFalse();

            await ClickAsync(item, window);

            vm.IsFormAuthoringMode.Should().BeTrue("Edit > Form Authoring Mode must run ToggleFormAuthoringModeCommand");
            AssertRecorded(nameof(MainWindowViewModel.ToggleFormAuthoringModeCommand));
        }
        finally
        {
            Close(window, pdf);
        }
    }

    [FixedAvaloniaFact]
    public async Task AutoDetectFieldsMenuEntry_IsPointerReachable_AndRunsTheCommand()
    {
        var (vm, window, pdf) = await OpenAsync();
        try
        {
            var item = MenuItemFor(window, "form.autoDetectFields");
            var runs = 0;
            using var subscription = vm.AutoDetectFieldsCommand.Subscribe(_ => runs++);

            await ClickAsync(item, window);

            runs.Should().Be(1, "Tools > Auto-detect Form Fields must run AutoDetectFieldsCommand once");
            AssertRecorded(nameof(MainWindowViewModel.AutoDetectFieldsCommand));
        }
        finally
        {
            Close(window, pdf);
        }
    }

    [FixedAvaloniaFact]
    public async Task TypewriterColorMenuEntry_IsPointerReachable_AndRecoloursTheActiveBox()
    {
        var (vm, window, pdf) = await OpenAsync();
        try
        {
            vm.IsTypewriterMode = true;
            vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
            await KeyboardTestHelpers.FlushDispatcherAsync();

            // Per-preset command id since #1476's follow-up; the shared
            // typewriter.setColor now names the submenu that hosts them.
            var red = MenuItemFor(window, "typewriter.setColor.red", "#D0021B");
            await ClickAsync(red, window);

            vm.TypewriterColor.Should().Be(Color.Parse("#D0021B"));
            var op = vm.TypewriterTextOperations.Single();
            op.Style.Color.R.Should().BeApproximately(0xD0 / 255.0, 0.01,
                "Edit > Typewriter Text Color > Red must restyle the active box, as the toolbar swatch does");
            AssertRecorded(nameof(MainWindowViewModel.SetTypewriterColorCommand));
        }
        finally
        {
            Close(window, pdf);
        }
    }

    /// <summary>
    /// The three preset lists must stay equal. Since #1476's follow-up the
    /// swatches live in the typewriter STYLE flyout (there is no separate colour
    /// button any more) and each carries its own command id, so they are
    /// collected by the command they run rather than by a shared id.
    /// <see cref="TypewriterColorPresetAccessibilityTests"/> owns the per-preset
    /// naming; this test owns "the same eight colours in all three places".
    /// </summary>
    [FixedAvaloniaFact]
    public async Task TypewriterColorPresets_AreTheSameInTheFlyoutTheWindowMenuAndTheMacMenu()
    {
        var (vm, window, pdf) = await OpenAsync();
        try
        {
            var flyout = window.FindControl<Button>("TypewriterStyleFlyoutButton")!.Flyout as Flyout;
            flyout.Should().NotBeNull("the colour presets moved into the typewriter style flyout");
            var flyoutPresets = ((Control)flyout!.Content!).GetLogicalDescendants().OfType<Button>()
                .Where(b => b.CommandParameter is string)
                .Select(b => (string)b.CommandParameter!)
                .ToList();

            var menuPresets = MenuItems(window)
                .Where(m => CommandAccessibility.GetCommandId(m)?.StartsWith("typewriter.setColor.", StringComparison.Ordinal) == true
                            && m.CommandParameter is string)
                .Select(m => (string)m.CommandParameter!)
                .ToList();

            var nativePresets = NativeLeaves(MacNativeMenuBuilder.Create(vm))
                .Where(n => ReferenceEquals(n.Command, vm.SetTypewriterColorCommand))
                .Select(n => (string)n.CommandParameter!)
                .ToList();

            flyoutPresets.Should().HaveCount(8);
            menuPresets.Should().Equal(flyoutPresets, "Edit > Typewriter Text Color must offer the toolbar's swatches");
            nativePresets.Should().Equal(flyoutPresets, "the macOS menu must offer the toolbar's swatches");
        }
        finally
        {
            Close(window, pdf);
        }
    }

    [FixedAvaloniaFact]
    public async Task MacNativeMenu_CarriesTheToolbarActionsThatHadNoMenuEntry()
    {
        var (vm, window, pdf) = await OpenAsync();
        try
        {
            var menu = MacNativeMenuBuilder.Create(vm);
            var leaves = NativeLeaves(menu).ToList();

            foreach (ICommand command in new ICommand[]
                     { vm.ToggleFormAuthoringModeCommand, vm.AutoDetectFieldsCommand, vm.SetTypewriterColorCommand })
            {
                leaves.Should().Contain(n => ReferenceEquals(n.Command, command) && n.IsEnabled,
                    "on macOS the in-window menu bar is hidden, so the native menu is the only menu");
            }

            var formAuthoring = leaves.Single(n => ReferenceEquals(n.Command, vm.ToggleFormAuthoringModeCommand));
            formAuthoring.IsChecked.Should().BeFalse();
            vm.IsFormAuthoringMode = true;
            await KeyboardTestHelpers.FlushDispatcherAsync();
            formAuthoring.IsChecked.Should().BeTrue("the native check mark must follow the mode");
        }
        finally
        {
            Close(window, pdf);
        }
    }

    private static async Task<(MainWindowViewModel Vm, MainWindow Window, string Pdf)> OpenAsync()
    {
        var pdf = Path.Combine(Path.GetTempPath(), $"excise-1476-menu-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(pdf, "Toolbar overflow menu entries");

        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(pdf);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        return (vm, window, pdf);
    }

    private static void Close(Window window, string pdf)
    {
        window.Close();
        TestPdfGenerator.CleanupTestFile(pdf);
    }

    private static IEnumerable<MenuItem> MenuItems(MainWindow window) =>
        window.FindControl<Menu>("MainMenuBar")!.GetLogicalDescendants().OfType<MenuItem>();

    private static MenuItem MenuItemFor(MainWindow window, string commandId, string? parameter = null)
    {
        var item = MenuItems(window).FirstOrDefault(m =>
            CommandAccessibility.GetCommandId(m) == commandId
            && m.Command != null
            && (parameter == null || Equals(m.CommandParameter, parameter)));
        item.Should().NotBeNull($"MainMenuBar must contain an entry for {commandId} {parameter}".TrimEnd());
        return item!;
    }

    private static async Task ClickAsync(MenuItem item, Visual root)
    {
        // Real pointer input: what the interaction-coverage recorder counts.
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var position = new Point(4, 4);
        item.RaiseEvent(new PointerPressedEventArgs(
            item, pointer, root, position, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
        item.RaiseEvent(new PointerReleasedEventArgs(
            item, pointer, root, position, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
        await KeyboardTestHelpers.FlushDispatcherAsync();

        // The click that release stands for on an open menu; it runs the command.
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await KeyboardTestHelpers.FlushDispatcherAsync();
    }

    private static void AssertRecorded(string commandProperty) =>
        GuiInteractionRecorder.ObservedIds.Should().Contain(
            id => id.StartsWith($"MainWindow/MenuItem:{commandProperty}\t", StringComparison.Ordinal),
            "the pointer press must reach the interaction-coverage recorder under the menu item's inventory id");

    internal static IEnumerable<NativeMenuItem> NativeLeaves(NativeMenu menu)
    {
        foreach (var entry in menu.Items)
        {
            if (entry is not NativeMenuItem item || entry is NativeMenuItemSeparator)
                continue;

            if (item.Menu is { } submenu)
            {
                foreach (var child in NativeLeaves(submenu))
                    yield return child;
                continue;
            }

            if (item.Command != null)
                yield return item;
        }
    }
}
