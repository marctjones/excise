using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using AwesomeAssertions;
using Pdfe.Core.Automation;
using PdfEditor.Automation;
using PdfEditor.Tests.Utilities;
using PdfEditor.ViewModels;
using PdfEditor.Views;
using Xunit;

namespace PdfEditor.Tests.UI;

[Collection("AvaloniaTests")]
public class AccessibilityRegressionTests
{
    [FixedAvaloniaFact]
    public async Task CommandBackedControls_UseSharedCommandMetadataForAccessibleText()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        window.UpdateLayout();

        var commandControls = CollectControls(window)
            .Select(control => (Control: control, CommandId: CommandAccessibility.GetCommandId(control)))
            .Where(item => !string.IsNullOrWhiteSpace(item.CommandId))
            .ToList();

        commandControls.Should().NotBeEmpty("core controls should opt into the shared command metadata registry");

        var failures = new List<string>();
        foreach (var (control, commandId) in commandControls)
        {
            if (!PdfCommandRegistry.TryGet(commandId!, out var metadata))
            {
                failures.Add($"{Describe(control)} references unknown command id '{commandId}'");
                continue;
            }

            var name = AutomationProperties.GetName(control);
            if (!string.Equals(name, metadata.Label, StringComparison.Ordinal))
                failures.Add($"{Describe(control)} name '{name}' != registry label '{metadata.Label}'");

            var helpText = AutomationProperties.GetHelpText(control) ?? string.Empty;
            if (!helpText.Contains(metadata.Description, StringComparison.Ordinal))
                failures.Add($"{Describe(control)} help text does not include registry description for {metadata.Id}");

            if (!string.IsNullOrWhiteSpace(metadata.Shortcut) &&
                !helpText.Contains(metadata.Shortcut, StringComparison.Ordinal))
                failures.Add($"{Describe(control)} help text does not include shortcut {metadata.Shortcut}");

            if (!control.IsEnabled)
            {
                var status = AutomationProperties.GetItemStatus(control) ?? string.Empty;
                if (!status.Contains(metadata.DisabledReason, StringComparison.Ordinal))
                    failures.Add($"{Describe(control)} disabled status does not include disabled reason for {metadata.Id}");
            }

            var tooltip = ToolTip.GetTip(control)?.ToString() ?? string.Empty;
            if (!tooltip.Contains(metadata.Label, StringComparison.OrdinalIgnoreCase))
                failures.Add($"{Describe(control)} tooltip does not include registry label '{metadata.Label}'");
        }

        failures.Should().BeEmpty();
    }

    [FixedAvaloniaFact]
    public async Task CoreWorkflows_HaveCommandMetadataBackedControls()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        window.UpdateLayout();

        var present = CollectControls(window)
            .Select(CommandAccessibility.GetCommandId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        present.Should().Contain(new[]
        {
            PdfCommandIds.Open,
            PdfCommandIds.Save,
            PdfCommandIds.SearchOpen,
            PdfCommandIds.SearchFind,
            PdfCommandIds.NextPage,
            PdfCommandIds.PreviousPage,
            PdfCommandIds.ToggleRedactionMode,
            PdfCommandIds.ApplyRedaction,
            PdfCommandIds.ToggleFormAuthoring,
            PdfCommandIds.SaveFlattenedFormCopy,
            PdfCommandIds.VerifySignatures,
        });
    }

    [FixedAvaloniaFact]
    public void SearchControls_StatusBarAndViewer_ExposeKeyboardAccessibleNames()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        vm.IsSearchVisible = true;
        vm.OperationStatus = "Rendering page 1 of 1...";
        window.UpdateLayout();

        var searchTextBox = window.FindControl<TextBox>("SearchTextBox");
        searchTextBox.Should().NotBeNull();
        searchTextBox!.Focusable.Should().BeTrue();
        AutomationProperties.GetName(searchTextBox).Should().Be("Search Text");
        AutomationProperties.GetHelpText(searchTextBox).Should().Contain("Enter text to search");

        var checkboxes = CollectControls(window).OfType<CheckBox>()
            .Select(AutomationProperties.GetName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
        checkboxes.Should().Contain(new[]
        {
            "Case Sensitive Search",
            "Whole Word Search",
            "Regular Expression Search",
        });

        var operationStatus = window.FindControl<TextBlock>("OperationStatusText");
        operationStatus.Should().NotBeNull();
        AutomationProperties.GetName(operationStatus).Should().Be("Operation Status");
        AutomationProperties.GetItemStatus(operationStatus).Should().Be("Rendering page 1 of 1...");

        var modeStatus = window.FindControl<TextBlock>("CurrentModeStatusText");
        modeStatus.Should().NotBeNull();
        AutomationProperties.GetName(modeStatus).Should().Be("Current Interaction Mode");
        AutomationProperties.GetItemStatus(modeStatus).Should().Be(vm.CurrentModeText);

        var viewer = window.FindControl<Pdfe.Avalonia.Controls.PdfViewerControl>("PdfViewerControl");
        viewer.Should().NotBeNull();
        viewer!.Focusable.Should().BeTrue();
    }

    [FixedAvaloniaFact]
    public void Dialogs_ExposeAccessibleNamesAndDefaultCancelSemantics()
    {
        var preferences = new PreferencesWindow { DataContext = new PreferencesViewModel() };
        preferences.Show();
        preferences.UpdateLayout();

        var savePreferences = FindButtonByName(preferences, "Save Preferences");
        savePreferences.Should().NotBeNull();
        savePreferences!.IsDefault.Should().BeTrue();
        AutomationProperties.GetHelpText(savePreferences).Should().Contain("Save preference changes");

        var cancelPreferences = FindButtonByName(preferences, "Cancel Preferences");
        cancelPreferences.Should().NotBeNull();
        cancelPreferences!.IsCancel.Should().BeTrue();

        var redactedDialog = new SaveRedactedVersionDialog
        {
            DataContext = new SaveRedactedVersionDialogViewModel("/tmp/document_REDACTED.pdf", 2)
        };
        redactedDialog.Show();
        redactedDialog.UpdateLayout();

        var savePathTextBox = redactedDialog.FindControl<TextBox>("SavePathTextBox");
        savePathTextBox.Should().NotBeNull();
        AutomationProperties.GetName(savePathTextBox!).Should().Be("Redacted PDF Save Path");

        var saveRedacted = FindButtonByName(redactedDialog, "Save Redacted Version");
        saveRedacted.Should().NotBeNull();
        saveRedacted!.IsDefault.Should().BeTrue();

        var cancelRedacted = FindButtonByName(redactedDialog, "Cancel Save Redacted Version");
        cancelRedacted.Should().NotBeNull();
        cancelRedacted!.IsCancel.Should().BeTrue();

        var about = new AboutWindow();
        about.Show();
        about.UpdateLayout();

        FindButtonByName(about, "Visit GitHub").Should().NotBeNull();
        var closeAbout = FindButtonByName(about, "Close About Dialog");
        closeAbout.Should().NotBeNull();
        closeAbout!.IsDefault.Should().BeTrue();
        closeAbout.IsCancel.Should().BeTrue();
    }

    [FixedAvaloniaFact]
    public async Task KeyboardOnly_OpenSearchNavigateAndToggleModes_AreReachable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdfe-accessibility-keyboard-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3);
        try
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await vm.LoadDocumentAsync(path);

            await window.PressKeyAsync(Avalonia.Input.Key.F, Avalonia.Input.RawInputModifiers.Control);
            await KeyboardTestHelpers.FlushDispatcherAsync();
            vm.IsSearchVisible.Should().BeTrue("Ctrl+F should reach the search workflow");

            var viewer = window.FindControl<Pdfe.Avalonia.Controls.PdfViewerControl>("PdfViewerControl");
            viewer.Should().NotBeNull();
            viewer!.Focus();
            await KeyboardTestHelpers.FlushDispatcherAsync();

            await window.PressKeyAsync(Avalonia.Input.Key.PageDown);
            await KeyboardTestHelpers.FlushDispatcherAsync();
            vm.CurrentPageIndex.Should().BeGreaterThan(0, "keyboard navigation should reach later pages");

            await window.PressKeyAsync(Avalonia.Input.Key.T);
            await KeyboardTestHelpers.FlushDispatcherAsync();
            vm.IsTextSelectionMode.Should().BeTrue("T should toggle text selection without a mouse");

            await window.PressKeyAsync(Avalonia.Input.Key.R);
            await KeyboardTestHelpers.FlushDispatcherAsync();
            vm.IsRedactionMode.Should().BeTrue("R should toggle redaction mode without a mouse");
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    private static IEnumerable<Control> CollectControls(ILogical root)
    {
        var stack = new Stack<ILogical>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is Control control)
                yield return control;

            foreach (var child in node.LogicalChildren)
                stack.Push(child);
        }
    }

    private static string Describe(Control control) =>
        $"{control.GetType().Name} '{control.Name ?? control.ToString()}'";

    private static Button? FindButtonByName(ILogical root, string automationName) =>
        CollectControls(root)
            .OfType<Button>()
            .FirstOrDefault(button => AutomationProperties.GetName(button) == automationName);
}
