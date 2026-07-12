using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AwesomeAssertions;
using Pdfe.Avalonia.Controls;
using PdfEditor.ViewModels;
using PdfEditor.Views;
using Xunit;

namespace PdfEditor.Tests.UI;

[Collection("AvaloniaTests")]
public class GuiToggleStateRegressionTests
{
    [FixedAvaloniaFact]
    public async Task WindowViewToggleMenus_KeepCheckIconsToolbarClassesAndPanelsInSync()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await PumpAsync(window);
        vm.ApplyContinuousScrollPreference(true);
        await PumpAsync(window);

        var continuous = Required<MenuItem>(window, "ViewContinuousMenuItem");
        var outline = Required<MenuItem>(window, "ViewOutlineMenuItem");
        var thumbnails = Required<MenuItem>(window, "ViewThumbnailsMenuItem");
        var viewClipboard = Required<MenuItem>(window, "ViewClipboardMenuItem");
        var redactionClipboard = Required<MenuItem>(window, "RedactionClipboardMenuItem");

        var outlineButton = Required<Button>(window, "ToolbarOutlineButton");
        var thumbnailsButton = Required<Button>(window, "ToolbarThumbnailsButton");
        var continuousButton = Required<Button>(window, "ToolbarContinuousButton");
        var leftSidebar = Required<Border>(window, "LeftSidebarHost");
        var outlinePanel = Required<Grid>(window, "OutlinePanel");
        var thumbnailsPanel = Required<Grid>(window, "ThumbnailsPanel");
        var clipboardSidebar = Required<Border>(window, "ClipboardSidebarHost");

        foreach (var item in new[] { continuous, outline, thumbnails, viewClipboard, redactionClipboard })
        {
            item.ToggleType.Should().Be(MenuItemToggleType.CheckBox,
                "toggle menu items must keep ToggleType so screen readers get the UIA Toggle pattern; " +
                "a bare check glyph in the Icon slot carries no automation semantics");
            item.Command.Should().NotBeNull(
                $"{HeaderText(item)} must mutate state via its VM command — an IsChecked binding with no " +
                "Command is what left these menus dead-on-click and permanently checked");
        }

        outline.IsChecked.Should().BeTrue();
        thumbnails.IsChecked.Should().BeTrue();
        viewClipboard.IsChecked.Should().BeTrue();
        redactionClipboard.IsChecked.Should().BeTrue();
        continuous.IsChecked.Should().BeTrue();
        outlineButton.Classes.Should().Contain("active");
        thumbnailsButton.Classes.Should().Contain("active");
        continuousButton.Classes.Should().Contain("active");
        leftSidebar.IsVisible.Should().BeTrue();
        outlinePanel.IsVisible.Should().BeTrue();
        thumbnailsPanel.IsVisible.Should().BeTrue();
        clipboardSidebar.IsVisible.Should().BeTrue();

        Click(outline);
        await PumpAsync(window);
        vm.IsOutlineSidebarVisible.Should().BeFalse();
        outline.IsChecked.Should().BeFalse();
        outlineButton.Classes.Should().NotContain("active");
        leftSidebar.IsVisible.Should().BeTrue("thumbnails are still visible");
        outlinePanel.IsVisible.Should().BeFalse();
        thumbnailsPanel.IsVisible.Should().BeTrue();

        Click(outline);
        await PumpAsync(window);
        vm.IsOutlineSidebarVisible.Should().BeTrue(
            "a second click must toggle back — if MenuItem also flipped IsChecked itself, the item " +
            "would double-toggle and land back where it started");
        outline.IsChecked.Should().BeTrue();
        Click(outline);
        await PumpAsync(window);

        Click(thumbnails);
        await PumpAsync(window);
        vm.IsThumbnailsSidebarVisible.Should().BeFalse();
        thumbnails.IsChecked.Should().BeFalse();
        thumbnailsButton.Classes.Should().NotContain("active");
        leftSidebar.IsVisible.Should().BeFalse("both left-sidebar sections are hidden");
        thumbnailsPanel.IsVisible.Should().BeFalse();

        Click(viewClipboard);
        await PumpAsync(window);
        vm.IsClipboardSidebarVisible.Should().BeFalse();
        viewClipboard.IsChecked.Should().BeFalse();
        redactionClipboard.IsChecked.Should().BeFalse("duplicate clipboard menu entries must share one VM state");
        clipboardSidebar.IsVisible.Should().BeFalse();

        Click(continuous);
        await PumpAsync(window);
        vm.IsContinuousView.Should().BeFalse();
        vm.ContinuousScrollPreference.Should().BeFalse();
        continuous.IsChecked.Should().BeFalse();
        continuousButton.Classes.Should().NotContain("active");
    }

    [FixedAvaloniaFact]
    public void MacNativeMenuToggleItems_TrackViewModelStateAndExecuteSharedCommands()
    {
        var vm = new MainWindowViewModel();
        vm.ApplyContinuousScrollPreference(true);
        var menu = MacNativeMenuBuilder.Create(vm);

        var outline = RequiredNative(menu, "Show Outline");
        var thumbnails = RequiredNative(menu, "Show Thumbnails");
        var continuous = RequiredNative(menu, "Continuous Scroll");
        var clipboardItems = NativeItems(menu, "Show Clipboard History").ToList();
        var revealHidden = RequiredNative(menu, "Reveal Hidden Text");
        var revealRasterized = RequiredNative(menu, "Reveal Rasterized Hidden Text");

        foreach (var item in new[] { outline, thumbnails, continuous, revealHidden, revealRasterized }.Concat(clipboardItems))
        {
            item.ToggleType.Should().Be(MenuItemToggleType.CheckBox);
            item.Command.Should().NotBeNull($"{item.Header} should execute a VM toggle command");
        }

        outline.IsChecked.Should().BeTrue();
        thumbnails.IsChecked.Should().BeTrue();
        clipboardItems.Should().OnlyContain(item => item.IsChecked);
        continuous.IsChecked.Should().BeTrue();
        revealHidden.IsChecked.Should().BeFalse();
        revealRasterized.IsChecked.Should().BeFalse();

        outline.Command!.Execute(null);
        vm.IsOutlineSidebarVisible.Should().BeFalse();
        outline.IsChecked.Should().BeFalse();

        thumbnails.Command!.Execute(null);
        vm.IsThumbnailsSidebarVisible.Should().BeFalse();
        thumbnails.IsChecked.Should().BeFalse();

        clipboardItems[0].Command!.Execute(null);
        vm.IsClipboardSidebarVisible.Should().BeFalse();
        clipboardItems.Should().OnlyContain(item => !item.IsChecked);

        continuous.Command!.Execute(null);
        vm.IsContinuousView.Should().BeFalse();
        vm.ContinuousScrollPreference.Should().BeFalse();
        continuous.IsChecked.Should().BeFalse();

        revealHidden.Command!.Execute(null);
        vm.RevealHiddenText.Should().BeTrue();
        revealHidden.IsChecked.Should().BeTrue();

        revealRasterized.Command!.Execute(null);
        vm.RevealRasterizedHidden.Should().BeTrue();
        revealRasterized.IsChecked.Should().BeTrue();
    }

    [FixedAvaloniaFact]
    public async Task StatusBarVisualIndicators_TrackModeOperationAndDocumentStatus()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await PumpAsync(window);
        vm.ApplyContinuousScrollPreference(true);
        await PumpAsync(window);

        var modeStatus = Required<TextBlock>(window, "CurrentModeStatusText");
        var operationStatus = Required<TextBlock>(window, "OperationStatusText");
        var documentStatus = Required<TextBlock>(window, "StatusBarTextBlock");
        var continuous = Required<MenuItem>(window, "ViewContinuousMenuItem");
        var continuousButton = Required<Button>(window, "ToolbarContinuousButton");

        modeStatus.Text.Should().Be("Continuous Scroll");
        AutomationProperties.GetItemStatus(modeStatus).Should().Be("Continuous Scroll");
        continuous.IsChecked.Should().BeTrue();
        continuousButton.Classes.Should().Contain("active");
        operationStatus.IsVisible.Should().BeFalse();
        documentStatus.IsVisible.Should().BeTrue();
        documentStatus.Text.Should().Be(vm.StatusBarText);
        AutomationProperties.GetItemStatus(documentStatus).Should().Be(vm.StatusBarText);

        vm.OperationStatus = "Rendering page 1 of 10...";
        await PumpAsync(window);

        operationStatus.IsVisible.Should().BeTrue();
        operationStatus.Text.Should().Be("Rendering page 1 of 10...");
        AutomationProperties.GetItemStatus(operationStatus).Should().Be("Rendering page 1 of 10...");
        documentStatus.IsVisible.Should().BeFalse();

        vm.OperationStatus = string.Empty;
        vm.IsRedactionMode = true;
        await PumpAsync(window);

        operationStatus.IsVisible.Should().BeFalse();
        documentStatus.IsVisible.Should().BeTrue();
        modeStatus.Text.Should().Be("Redaction Mode");
        AutomationProperties.GetItemStatus(modeStatus).Should().Be("Redaction Mode");
        modeStatus.Classes.Should().Contain("redactionActive");
        continuous.IsChecked.Should().BeFalse();
        continuousButton.Classes.Should().NotContain("active");
        vm.ContinuousScrollPreference.Should().BeTrue("editing modes should not overwrite the saved continuous-scroll preference");

        vm.IsRedactionMode = false;
        await PumpAsync(window);

        vm.IsContinuousView.Should().BeTrue("leaving redaction mode must restore the saved preference");
        continuous.IsChecked.Should().BeTrue();
        continuousButton.Classes.Should().Contain("active");
    }

    private static async Task PumpAsync(Window window)
    {
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        window.UpdateLayout();
    }

    private static T Required<T>(Window window, string name) where T : Control
    {
        var control = window.FindControl<T>(name);
        control.Should().NotBeNull($"{name} should be present in the main window");
        return control!;
    }

    /// <summary>
    /// Raises the real Click event rather than poking Command directly. This is the
    /// whole point of the test: Avalonia's MenuItem may flip its own IsChecked on
    /// click, so driving the Command alone would hide a double-toggle. Clicking
    /// exercises both paths together, exactly as the user does.
    /// </summary>
    private static void Click(MenuItem item)
    {
        item.Command.Should().NotBeNull($"{HeaderText(item)} should be command-backed");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static string HeaderText(MenuItem item) =>
        item.Header?.ToString() ?? "<null>";

    private static NativeMenuItem RequiredNative(NativeMenu menu, string header)
    {
        var items = NativeItems(menu, header).ToList();
        items.Should().ContainSingle($"native menu item '{header}' should exist exactly once");
        return items[0];
    }

    private static IEnumerable<NativeMenuItem> NativeItems(NativeMenu menu, string header)
    {
        foreach (var itemBase in menu.Items)
        {
            if (itemBase is not NativeMenuItem item)
                continue;

            if (string.Equals(item.Header?.ToString(), header, StringComparison.Ordinal))
                yield return item;

            if (item.Menu is { } submenu)
            {
                foreach (var child in NativeItems(submenu, header))
                    yield return child;
            }
        }
    }
}
