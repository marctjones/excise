using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using AwesomeAssertions;
using PdfEditor.ViewModels;
using PdfEditor.Views;
using Xunit;
namespace PdfEditor.Tests.UI;

/// <summary>
/// User report: "make sure all of the buttons and menu items work as
/// expected". This sweep test walks the entire MainWindow logical tree
/// and verifies every Button and MenuItem with a Command binding
/// resolves to a non-null ICommand on the ViewModel — i.e. catches
/// the class of bugs where a command was renamed in the VM but the
/// XAML binding still points at the old name (silently no-ops).
///
/// CanExecute is also poked: a command that throws inside CanExecute
/// is broken regardless of whether anyone clicks the button. We don't
/// assert any particular CanExecute result because many commands are
/// document-state-gated (false until a doc is loaded) and that's
/// expected behaviour.
/// </summary>
[Collection("AvaloniaTests")]
public class CommandBindingSweepTests
{
    private readonly ITestOutputHelper _out;
    public CommandBindingSweepTests(ITestOutputHelper o) { _out = o; }

    [FixedAvaloniaFact]
    public void EveryButtonAndMenuItemCommand_ResolvesToNonNullCommand()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        window.UpdateLayout();

        var commandHosts = CollectCommandHosts(window).ToList();
        commandHosts.Should().NotBeEmpty(
            "MainWindow contains menu items and buttons — finding zero would mean tree-walk is broken");
        _out.WriteLine($"Found {commandHosts.Count} command hosts to check");

        var nullLeafCommands = new List<string>();
        var brokenExecutions = new List<string>();
        int leafCount = 0;
        int containerCount = 0;

        foreach (var (host, label) in commandHosts)
        {
            ICommand? cmd = host switch
            {
                Button b => b.Command,
                MenuItem m => m.Command,
                _ => null,
            };

            // A "container" is a Menu/MenuItem with submenu items —
            // these are expected to have null Command. Likewise a
            // toggle-style host (CheckBox, RadioButton, MenuItem with
            // ToggleType=CheckBox/Radio) wires its activation through
            // IsChecked rather than Command — null Command is fine
            // there. Everything else is a leaf the user clicks; null
            // Command on a leaf == dead affordance in the UI.
            var isContainer = host is MenuItem mic &&
                              (mic.Items.Count > 0 || mic.ItemsSource != null);
            var isToggle = host is ToggleButton // CheckBox/RadioButton inherit
                          || (host is MenuItem mit && mit.ToggleType != MenuItemToggleType.None);
            if (isContainer || isToggle)
            {
                containerCount++;
                continue;
            }
            leafCount++;

            if (cmd == null)
            {
                nullLeafCommands.Add(label);
                continue;
            }

            // Don't invoke — half the commands open file dialogs, exit
            // the app, or otherwise have side effects we don't want
            // during a sweep. CanExecute is the cheap probe that says
            // "this command at least responds without exploding."
            try
            {
                _ = cmd.CanExecute(null);
            }
            catch (Exception ex)
            {
                brokenExecutions.Add($"{label}: CanExecute threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        _out.WriteLine($"Leaves: {leafCount}, containers (skipped): {containerCount}");
        if (nullLeafCommands.Count > 0)
            _out.WriteLine("LEAVES WITH NULL COMMAND:\n  " + string.Join("\n  ", nullLeafCommands));
        if (brokenExecutions.Count > 0)
            _out.WriteLine("BROKEN EXECUTIONS:\n  " + string.Join("\n  ", brokenExecutions));

        nullLeafCommands.Should().BeEmpty(
            "every clickable leaf Button/MenuItem must have a non-null Command — " +
            "a null Command means the XAML binding doesn't resolve to anything on the VM");
        brokenExecutions.Should().BeEmpty(
            "every command's CanExecute must respond without throwing");
    }

    [FixedAvaloniaFact]
    public void MacNativeMenuCommandItems_ResolveToNonNullCommands()
    {
        var vm = new MainWindowViewModel();
        var menu = MacNativeMenuBuilder.Create(vm);
        var commandLeaves = CollectNativeMenuLeaves(menu).ToList();

        commandLeaves.Should().NotBeEmpty("the macOS native menu should expose command-backed leaf items");

        var nullCommands = new List<string>();
        var brokenCanExecute = new List<string>();
        foreach (var item in commandLeaves)
        {
            if (item.Command == null)
            {
                nullCommands.Add(item.Header?.ToString() ?? "<null>");
                continue;
            }

            try
            {
                _ = item.Command.CanExecute(item.CommandParameter);
            }
            catch (Exception ex)
            {
                brokenCanExecute.Add($"{item.Header}: CanExecute threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        nullCommands.Should().BeEmpty("native menu command leaves should bind to live ViewModel commands");
        brokenCanExecute.Should().BeEmpty("native menu commands should answer CanExecute without throwing");

        var headers = commandLeaves
            .Select(item => item.Header?.ToString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        headers.Should().Contain(new[]
        {
            "Open...",
            "Save As...",
            "Find...",
            "Add Pages...",
            "Insert Pages Before Current...",
            "Insert Pages After Current...",
            "Extract Current Page...",
            "Extract Selected Pages...",
            "Move Page Earlier",
            "Move Page Later",
            "Move Selected Pages Earlier",
            "Move Selected Pages Later",
            "Remove Current Page",
            "Remove Selected Pages",
            "Clear Page Selection",
            "Add Highlight From Selection",
            "Add Sticky Note...",
            "Redaction Mode",
            "Apply Redaction",
            "Verify Digital Signatures..."
        });
    }

    /// <summary>
    /// Walk the logical tree (not visual — MenuItems in a not-yet-opened
    /// top-level Menu aren't realised in the visual tree) and yield each
    /// Button or MenuItem along with a human-readable label.
    /// </summary>
    private static IEnumerable<(Control Host, string Label)> CollectCommandHosts(ILogical root)
    {
        var stack = new Stack<ILogical>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            switch (node)
            {
                case MenuItem mi:
                    yield return (mi, $"MenuItem '{HeaderText(mi)}'");
                    break;
                case Button btn:
                    yield return (btn, $"Button '{ButtonLabel(btn)}'");
                    break;
            }
            foreach (var child in node.LogicalChildren)
                stack.Push(child);
        }
    }

    private static IEnumerable<NativeMenuItem> CollectNativeMenuLeaves(NativeMenu menu)
    {
        foreach (var itemBase in menu.Items)
        {
            if (itemBase is NativeMenuItemSeparator)
                continue;

            if (itemBase is not NativeMenuItem item)
                continue;

            if (item.Menu is { Items.Count: > 0 })
            {
                foreach (var child in CollectNativeMenuLeaves(item.Menu))
                    yield return child;
                continue;
            }

            if (item.Header?.ToString() is "No Recent Files" or "-")
                continue;

            if (item.Command == null && item.ToggleType != MenuItemToggleType.None)
                continue;

            yield return item;
        }
    }

    private static string HeaderText(MenuItem mi) => mi.Header switch
    {
        string s => s,
        TextBlock tb => tb.Text ?? "<TextBlock>",
        _ => mi.Header?.ToString() ?? "<null>",
    };

    private static string ButtonLabel(Button b)
    {
        if (!string.IsNullOrEmpty(b.Name)) return b.Name;
        if (b.Content is string s) return s;
        if (b.Content is TextBlock tb) return tb.Text ?? "<TextBlock>";
        return b.Content?.ToString() ?? "<unnamed>";
    }

}
