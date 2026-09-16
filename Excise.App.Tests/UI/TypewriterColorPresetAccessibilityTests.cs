using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using AwesomeAssertions;
using Excise.App.Automation;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Core.Automation;
using Excise.Core.Document;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1476 follow-up: each of the eight typewriter colour presets must announce
/// WHICH colour it is.
///
/// <para><b>The defect this pins.</b> All eight swatches and all eight menu
/// items used to carry the single command id <c>typewriter.setColor</c>, and
/// <see cref="CommandAccessibility"/> derives
/// <see cref="AutomationProperties.NameProperty"/> from the id's registry
/// label — so a screen reader read eight identical "Set Typewriter Color"
/// buttons. The only other signal is the swatch's colour, which a screen
/// reader cannot read at all, so the control was genuinely unusable without
/// sight. Eight per-preset ids fix it.</para>
///
/// <para><b>Why the names are asserted and not the tooltips.</b> Both come from
/// the same registry entry via <see cref="CommandAccessibility"/>, so the
/// registry label is the single thing that has to be right, and the label is
/// what assistive technology reads.</para>
///
/// <para><b>macOS.</b> A <c>NativeMenuItem</c> has nowhere to attach
/// <c>CommandAccessibility</c>; VoiceOver reads the item's TITLE, and those
/// were already distinct ("Black", "Gray", ...). What was missing there was a
/// guarantee that the native list cannot drift from the other two, so the
/// preset table now carries the command id and this file cross-checks all
/// three surfaces against it.</para>
/// </summary>
[Collection("AvaloniaTests")]
public class TypewriterColorPresetAccessibilityTests
{
    /// <summary>The eight presets as the registry and all three surfaces must agree on them.</summary>
    public static readonly IReadOnlyList<(string Name, string Hex, string CommandId)> Presets =
    [
        ("Black", "#000000", PdfCommandIds.TypewriterSetColorBlack),
        ("Gray", "#555555", PdfCommandIds.TypewriterSetColorGray),
        ("Red", "#D0021B", PdfCommandIds.TypewriterSetColorRed),
        ("Orange", "#F5A623", PdfCommandIds.TypewriterSetColorOrange),
        ("Green", "#2E7D32", PdfCommandIds.TypewriterSetColorGreen),
        ("Blue", "#1565C0", PdfCommandIds.TypewriterSetColorBlue),
        ("Purple", "#6A1B9A", PdfCommandIds.TypewriterSetColorPurple),
        ("White", "#FFFFFF", PdfCommandIds.TypewriterSetColorWhite),
    ];

    [Fact]
    public void EveryPreset_HasItsOwnRegistryEntry_WithADistinctLabelNamingTheColour()
    {
        var labels = new List<string>();

        foreach (var (name, hex, commandId) in Presets)
        {
            PdfCommandRegistry.TryGet(commandId, out var metadata).Should().BeTrue(
                $"{commandId} must be registered, or CommandAccessibility cannot name the control");

            metadata.Label.Should().Contain(name,
                $"the accessible name for {commandId} is the only signal a screen reader gets — " +
                "the swatch's colour is not readable");
            metadata.Description.Should().Contain(hex,
                "the description should say exactly which colour is applied");
            labels.Add(metadata.Label);
        }

        labels.Should().OnlyHaveUniqueItems(
            "eight controls sharing one accessible name is the defect this test exists for");

        // The generic id stays: it is the scripting entry point for an arbitrary
        // hex, and dropping it would break that surface.
        PdfCommandRegistry.TryGet(PdfCommandIds.TypewriterSetColor, out _).Should().BeTrue();
    }

    [FixedAvaloniaFact]
    public async Task TheFlyoutSwatches_EachCarryTheirOwnIdNameAndTooltip()
    {
        var (_, window, pdf) = await OpenAsync();
        try
        {
            var swatches = FlyoutSwatches(window);

            swatches.Should().HaveCount(8, "the style flyout must offer all eight presets");
            swatches.Select(b => (string)b.CommandParameter!)
                .Should().Equal(Presets.Select(p => p.Hex), "in the documented preset order");
            swatches.Select(b => CommandAccessibility.GetCommandId(b)!)
                .Should().Equal(Presets.Select(p => p.CommandId), "one id per swatch, not one id for all eight");

            var names = new List<string>();
            foreach (var (preset, swatch) in Presets.Zip(swatches))
            {
                var metadata = PdfCommandRegistry.Get(preset.CommandId);
                var name = AutomationProperties.GetName(swatch)!;
                name.Should().Be(metadata.Label,
                    $"CommandAccessibility must have named the {preset.Name} swatch from its own registry entry");
                ToolTip.GetTip(swatch)?.ToString().Should().Contain(preset.Name,
                    "a sighted user hovering a coloured square also needs to be told which colour it is");
                names.Add(name);
            }

            names.Should().OnlyHaveUniqueItems();
        }
        finally
        {
            Close(window, pdf);
        }
    }

    [FixedAvaloniaFact]
    public async Task TheWindowMenuEntries_KeepTheirShortHeaders_AndGainDistinctAccessibleNames()
    {
        var (_, window, pdf) = await OpenAsync();
        try
        {
            var names = new List<string>();
            foreach (var (name, hex, commandId) in Presets)
            {
                var item = MenuItems(window).SingleOrDefault(
                    m => CommandAccessibility.GetCommandId(m) == commandId);
                item.Should().NotBeNull($"Edit > Typewriter Text Color must carry an entry for {commandId}");

                item!.Header.Should().Be(name,
                    "the VISIBLE menu text stays the short colour name; only the accessible name is verbose");
                item.CommandParameter.Should().Be(hex);
                var accessibleName = AutomationProperties.GetName(item)!;
                accessibleName.Should().Be(PdfCommandRegistry.Get(commandId).Label);
                names.Add(accessibleName);
            }

            names.Should().OnlyHaveUniqueItems(
                "before #1476's follow-up all eight read as 'Set Typewriter Color'");

            // The generic id must stay reachable from the GUI: the registry gate
            // (scripts/check-gui-interaction-registry.sh) reports any declared
            // command id that no control carries as unreachable by a human.
            MenuItems(window).Should().Contain(
                m => CommandAccessibility.GetCommandId(m) == PdfCommandIds.TypewriterSetColor,
                "typewriter.setColor names the submenu that hosts the presets");
        }
        finally
        {
            Close(window, pdf);
        }
    }

    [FixedAvaloniaFact]
    public async Task TheMacNativeMenu_OffersTheSameEightPresets_WithDistinctTitles()
    {
        var (vm, window, pdf) = await OpenAsync();
        try
        {
            MacNativeMenuBuilder.TypewriterColorPresets.Should().Equal(Presets,
                "the native preset table is the list the other two surfaces are checked against");

            var native = ToolbarOverflowMenuEntriesTests.NativeLeaves(MacNativeMenuBuilder.Create(vm))
                .Where(n => ReferenceEquals(n.Command, vm.SetTypewriterColorCommand))
                .ToList();

            native.Should().HaveCount(8);
            native.Select(n => n.Header!).Should().Equal(Presets.Select(p => p.Name),
                "on macOS the item title IS the accessible name, so the titles must stay distinct");
            native.Select(n => (string)n.CommandParameter!).Should().Equal(Presets.Select(p => p.Hex));
            native.Should().OnlyContain(n => n.IsEnabled,
                "the in-window menu bar is hidden on macOS, so these are the only menu entries there");
        }
        finally
        {
            Close(window, pdf);
        }
    }

    /// <summary>
    /// The swatches still work where they now live. A real pointer event, so
    /// this also claims the swatch's row in the GUI interaction-coverage gate
    /// rather than leaving eight new rows in its gap list.
    /// </summary>
    [FixedAvaloniaFact]
    public async Task ClickingASwatchInTheFlyout_RecoloursTheActiveBox()
    {
        var (vm, window, pdf) = await OpenAsync();
        try
        {
            var flyout = (Flyout)window.FindControl<Button>("TypewriterStyleFlyoutButton")!.Flyout!;
            flyout.ShowAt(window.FindControl<Button>("TypewriterStyleFlyoutButton")!);
            await KeyboardTestHelpers.FlushDispatcherAsync();

            var red = FlyoutSwatches(window)
                .Single(b => CommandAccessibility.GetCommandId(b) == PdfCommandIds.TypewriterSetColorRed);

            PressAndRelease(red, window);
            await KeyboardTestHelpers.FlushDispatcherAsync();
            red.Command.Should().NotBeNull("the flyout must be open for the swatch's binding to have resolved");
            red.Command!.Execute(red.CommandParameter);
            await KeyboardTestHelpers.FlushDispatcherAsync();

            vm.TypewriterColor.Should().Be(Color.Parse("#D0021B"));
            vm.TypewriterTextOperations.Single().Style.Color.R
                .Should().BeApproximately(0xD0 / 255.0, 0.01, "the swatch must restyle the active box");

            InteractionCoverage.GuiInteractionRecorder.ObservedIds.Should().Contain(
                id => id.StartsWith("MainWindow/Button:SetTypewriterColorCommand", StringComparison.Ordinal),
                "the pointer press must reach the interaction-coverage recorder");
        }
        finally
        {
            Close(window, pdf);
        }
    }

    private static IReadOnlyList<Button> FlyoutSwatches(MainWindow window)
    {
        var button = window.FindControl<Button>("TypewriterStyleFlyoutButton");
        button.Should().NotBeNull("the colour presets live in the typewriter style flyout");
        var flyout = button!.Flyout as Flyout;
        flyout.Should().NotBeNull();
        var content = flyout!.Content as Control;
        content.Should().NotBeNull();

        return content!.GetLogicalDescendants().OfType<Button>()
            .Where(b => CommandAccessibility.GetCommandId(b)?.StartsWith(
                "typewriter.setColor.", StringComparison.Ordinal) == true)
            .ToList();
    }

    private static IEnumerable<MenuItem> MenuItems(MainWindow window) =>
        window.FindControl<Menu>("MainMenuBar")!.GetLogicalDescendants().OfType<MenuItem>();

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

    private static async Task<(MainWindowViewModel Vm, MainWindow Window, string Pdf)> OpenAsync()
    {
        var pdf = Path.Combine(Path.GetTempPath(), $"excise-1476-presets-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(pdf, "Typewriter colour presets");

        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(pdf);
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.IsTypewriterMode = true;
        vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        window.UpdateLayout();
        return (vm, window, pdf);
    }

    private static void Close(Window window, string pdf)
    {
        window.Close();
        TestPdfGenerator.CleanupTestFile(pdf);
    }
}
