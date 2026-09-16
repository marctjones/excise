using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.App.Controls;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1476: the main toolbar must never show a scroll bar and never wrap. As its
/// width shrinks it must, in this order: drop the button labels, shrink the
/// icons, then hide items lowest priority first. Everything it can hide must be
/// reachable from the menu.
///
/// <para>History. #589 put the strip in a horizontal ScrollViewer, which overflowed
/// at every width short of full screen (extent 1187 px against a 1102 px viewport at
/// 1280 in the default mode). The first #1476 attempt replaced it with a WrapPanel
/// and a fixed 1480 px icon-only threshold, which wrapped to two rows at 1024 in
/// redaction and form modes. Neither degraded by the space actually available.</para>
///
/// <para>These checks read the panel's own measurements (<see cref="PriorityToolbarPanel.LastPlan"/>)
/// rather than hard-coded pixel breakpoints, so they hold under any font or theme:
/// "labels hidden" must coincide with "the labelled row did not fit", and so on.
/// <see cref="PriorityToolbarLayoutTests"/> pins the decision itself on exact
/// numbers.</para>
/// </summary>
[Collection("AvaloniaTests")]
public class ToolbarWidthTests
{
    private readonly ITestOutputHelper _out;

    public ToolbarWidthTests(ITestOutputHelper output) => _out = output;

    private static readonly double[] Widths = [1600, 1280, 1024, 900, 700, 500];

    /// <summary>
    /// Toolbar children with no command, so no menu item can stand in for them.
    /// They may be hidden only at the high priorities checked below, and only
    /// appear while the user is in the task they serve.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NoMenuEquivalent = new Dictionary<string, string>
    {
        ["TypewriterFontSizeGroup"] = "two-way binding to TypewriterFontSize; there is no command to put in a menu",
        ["TypewriterAlignmentComboBox"] = "two-way binding to TypewriterAlignmentIndex; there is no command",
        ["FormFieldTypeComboBox"] = "SelectionChanged code-behind sets FormAuthoringFieldType; there is no command",
    };

    private const int MinimumPriorityWithoutMenuEquivalent = 90;

    [FixedAvaloniaFact(Timeout = 240000)]
    public async Task Toolbar_DropsLabelsThenShrinksIconsThenHidesLowestPriority_WithoutScrollingOrWrapping()
    {
        var pdfPath = Path.Combine(Path.GetTempPath(), $"excise-1476-toolbar-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 2);

        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(pdfPath);
            await Settle(window);

            var toolbar = window.FindControl<Border>("ToolbarBorder")!;
            var panel = window.FindControl<PriorityToolbarPanel>("ToolbarActionsPanel")!;
            var menu = window.FindControl<Menu>("MainMenuBar")!;
            toolbar.Should().NotBeNull();
            panel.Should().NotBeNull();
            menu.Should().NotBeNull();

            panel.GetVisualAncestors().TakeWhile(ancestor => !ReferenceEquals(ancestor, toolbar))
                .OfType<ScrollViewer>().Should().BeEmpty("#1476: the toolbar actions must not be in a scroll viewer");

            var fitWidth = window.GetVisualDescendants().OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "Fit Width");

            var problems = new List<string>();
            double? fullIconWidth = null;

            foreach (var (mode, enter, leave) in Modes(vm))
            {
                window.Width = Widths[0];
                enter();
                await Settle(window);

                double? modeHeight = null;
                var previousStage = ToolbarStage.Full;
                var previousHidden = 0;

                foreach (var width in Widths)
                {
                    window.Width = width;
                    await Settle(window);
                    var label = $"{mode} @ {width:F0}px";

                    var plan = panel.LastPlan;
                    if (plan is null)
                    {
                        problems.Add($"{label}: the panel has not measured");
                        continue;
                    }

                    var available = panel.LastAvailableWidth;
                    var participants = panel.Children.Where(PriorityToolbarPanel.GetIsAvailable).ToList();
                    var items = participants.Where(c => !PriorityToolbarPanel.GetIsSeparator(c)).ToList();
                    var hidden = items.Where(PriorityToolbarPanel.GetIsOverflowed).ToList();
                    var shown = items.Where(c => c.IsVisible && !PriorityToolbarPanel.GetIsOverflowed(c)).ToList();

                    double Required(ToolbarStage stage) =>
                        plan.StageWidths.TryGetValue(stage, out var w) ? w : double.NaN;
                    bool Fits(ToolbarStage stage) => Required(stage) <= available + PriorityToolbarLayout.Epsilon;

                    _out.WriteLine(
                        $"{label}: stage {panel.Stage}, available {available:F0}, rows full/icon/compact " +
                        $"{Required(ToolbarStage.Full):F0}/{Required(ToolbarStage.IconOnly):F0}/{Required(ToolbarStage.Compact):F0}, " +
                        $"toolbar height {toolbar.Bounds.Height:F0}, hidden [{string.Join(", ", hidden.Select(Describe))}]");

                    // 1. The stage is the widest one that fits, in the required order.
                    switch (panel.Stage)
                    {
                        case ToolbarStage.Full when !Fits(ToolbarStage.Full):
                            problems.Add($"{label}: labels shown although the labelled row ({Required(ToolbarStage.Full):F0}) exceeds {available:F0}");
                            break;
                        case ToolbarStage.IconOnly when Fits(ToolbarStage.Full):
                            problems.Add($"{label}: labels removed although the labelled row fits");
                            break;
                        case ToolbarStage.IconOnly when !Fits(ToolbarStage.IconOnly):
                            problems.Add($"{label}: icon-only stage kept although it does not fit");
                            break;
                        case ToolbarStage.Compact when Fits(ToolbarStage.Full) || Fits(ToolbarStage.IconOnly):
                            problems.Add($"{label}: icons shrunk although a larger stage fits");
                            break;
                        case ToolbarStage.Compact when hidden.Count > 0 && Fits(ToolbarStage.Compact):
                            problems.Add($"{label}: items hidden although the compact row fits");
                            break;
                    }

                    if (hidden.Count > 0 && panel.Stage != ToolbarStage.Compact)
                        problems.Add($"{label}: items hidden before the icons were shrunk");

                    // 2. Labels are visible exactly at the full stage.
                    var labelsVisible = panel.GetVisualDescendants().OfType<TextBlock>()
                        .Any(t => t.Classes.Contains("toolbar-label") && t.IsEffectivelyVisible);
                    if (labelsVisible != (panel.Stage == ToolbarStage.Full))
                        problems.Add($"{label}: labels {(labelsVisible ? "visible" : "hidden")} at stage {panel.Stage}");

                    // 3. Icons shrink only at the compact stage.
                    var icons = panel.GetVisualDescendants().OfType<PathIcon>()
                        .Where(i => i.Classes.Contains("toolbar-icon") && i.IsEffectivelyVisible)
                        .ToList();
                    if (panel.Stage == ToolbarStage.Full && icons.Count > 0)
                        fullIconWidth ??= icons.Max(i => i.Bounds.Width);
                    if (fullIconWidth is double fullIcon)
                    {
                        if (panel.Stage == ToolbarStage.Compact && icons.Any(i => i.Bounds.Width >= fullIcon - 0.5))
                            problems.Add($"{label}: compact stage but an icon is still {fullIcon:F0} px");
                        if (panel.Stage != ToolbarStage.Compact && icons.Any(i => Math.Abs(i.Bounds.Width - fullIcon) > 0.5))
                            problems.Add($"{label}: an icon shrank before the compact stage");
                    }

                    // 4. What is hidden is exactly the lowest priorities.
                    if (hidden.Count > 0 && shown.Count > 0)
                    {
                        var highestHidden = hidden.Max(PriorityToolbarPanel.GetPriority);
                        var lowestShown = shown.Min(PriorityToolbarPanel.GetPriority);
                        if (highestHidden >= lowestShown)
                            problems.Add($"{label}: hid priority {highestHidden} while showing priority {lowestShown}");
                    }

                    // 5. One row, inside the panel, nothing overlapping, nothing scrolling.
                    var previousRight = 0.0;
                    foreach (var child in participants.Where(c => c.IsVisible))
                    {
                        if (child.Bounds.Right > panel.Bounds.Width + 0.5)
                            problems.Add($"{label}: {Describe(child)} ends at {child.Bounds.Right:F0}, past the panel's {panel.Bounds.Width:F0}");
                        if (child.Bounds.Top < -0.5 || child.Bounds.Bottom > panel.Bounds.Height + 0.5)
                            problems.Add($"{label}: {Describe(child)} is outside the single row");
                        if (child.Bounds.Width > 0)
                        {
                            if (child.Bounds.Left < previousRight - 0.5)
                                problems.Add($"{label}: {Describe(child)} overlaps the item before it");
                            previousRight = child.Bounds.Right;
                        }
                    }

                    modeHeight ??= toolbar.Bounds.Height;
                    if (Math.Abs(toolbar.Bounds.Height - modeHeight.Value) > 0.5)
                        problems.Add($"{label}: toolbar height {toolbar.Bounds.Height:F0}, was {modeHeight:F0} at {Widths[0]:F0}px (a second row, or a row that shrinks)");

                    // A text box's own template carries hidden scroll bars; those are
                    // not the toolbar scrolling.
                    var scrollBars = toolbar.GetVisualDescendants().OfType<ScrollBar>()
                        .Where(bar => bar.IsEffectivelyVisible && bar.Bounds.Width > 0 && bar.Bounds.Height > 0
                                      && !bar.GetVisualAncestors().OfType<TextBox>().Any())
                        .ToList();
                    if (scrollBars.Count > 0)
                        problems.Add($"{label}: {scrollBars.Count} visible scroll bar(s) in the toolbar");

                    // 6. Zoom stays on screen (#589), and 1280 px hides nothing in the
                    //    two everyday modes.
                    var fitRight = fitWidth.TranslatePoint(new Point(fitWidth.Bounds.Width, 0), window)!.Value.X;
                    if (fitRight > window.Bounds.Width + 0.5 || !fitWidth.IsEffectivelyVisible)
                        problems.Add($"{label}: zoom Fit is not fully on the {window.Bounds.Width:F0}px window (#589)");
                    if (width >= 1280 && mode is "default" or "redaction" && hidden.Count > 0)
                        problems.Add($"{label}: {hidden.Count} item(s) hidden at 1280 px or wider");

                    // 7. A narrower toolbar never shows more than a wider one.
                    if (panel.Stage < previousStage || hidden.Count < previousHidden)
                        problems.Add($"{label}: shows more than the wider width before it");
                    previousStage = panel.Stage;
                    previousHidden = hidden.Count;
                }

                CheckEveryHideableItemHasAMenuEntry(mode, panel, menu, vm, problems);

                leave();
                await Settle(window);
            }

            if (fullIconWidth is null)
                problems.Add("no mode reached the full stage at any width, so the icon-size ordering was never checked");

            problems.Should().BeEmpty();
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(pdfPath);
        }
    }

    /// <summary>
    /// Every available toolbar child could be hidden at some width, so each one
    /// with a command must have a menu entry for that command, in the in-window
    /// menu AND in the macOS native menu (the in-window bar is hidden on macOS),
    /// enabled whenever the toolbar control is.
    /// </summary>
    private static void CheckEveryHideableItemHasAMenuEntry(
        string mode, PriorityToolbarPanel panel, Menu menu, MainWindowViewModel vm, List<string> problems)
    {
        var windowEntries = menu.GetLogicalDescendants().OfType<MenuItem>().Where(m => m.Command != null).ToList();
        var nativeEntries = ToolbarOverflowMenuEntriesTests.NativeLeaves(MacNativeMenuBuilder.Create(vm)).ToList();

        foreach (var child in panel.Children.Where(c =>
                     PriorityToolbarPanel.GetIsAvailable(c) && !PriorityToolbarPanel.GetIsSeparator(c)))
        {
            var name = $"{mode}: {Describe(child)}";
            var commands = ToolbarCommands(child, vm);

            if (commands.Count == 0)
            {
                if (!NoMenuEquivalent.ContainsKey(child.Name ?? string.Empty))
                    problems.Add($"{name} has no command, so nothing in the menu can stand in for it when it is hidden");
                else if (PriorityToolbarPanel.GetPriority(child) < MinimumPriorityWithoutMenuEquivalent)
                    problems.Add($"{name} has no menu equivalent and must keep priority >= {MinimumPriorityWithoutMenuEquivalent}");
                continue;
            }

            var toolbarEnabled = child.IsEffectivelyEnabled;
            foreach (var command in commands)
            {
                var inWindow = windowEntries.Where(m => ReferenceEquals(m.Command, command)).ToList();
                if (inWindow.Count == 0)
                    problems.Add($"{name}: no MainMenuBar entry runs its command");
                else if (toolbarEnabled && !inWindow.Any(m => m.IsEnabled && command.CanExecute(m.CommandParameter)))
                    problems.Add($"{name}: its MainMenuBar entry is disabled while the toolbar control is enabled");

                var native = nativeEntries.Where(n => ReferenceEquals(n.Command, command)).ToList();
                if (native.Count == 0)
                    problems.Add($"{name}: no macOS native menu entry runs its command");
                else if (toolbarEnabled && !native.Any(n => n.IsEnabled && command.CanExecute(n.CommandParameter)))
                    problems.Add($"{name}: its macOS native menu entry is disabled while the toolbar control is enabled");
            }
        }
    }

    private static IReadOnlyList<ICommand> ToolbarCommands(Control child, MainWindowViewModel vm) => child switch
    {
        Button { Command: { } command } => [command],
        // The colour picker opens a flyout of swatches bound to this command. The
        // flyout's content is not in the tree until it opens, so its bindings are
        // unresolved here; the swatches are compared with the menus in
        // ToolbarOverflowMenuEntriesTests.
        Button { Name: "TypewriterColorFlyoutButton" } => [vm.SetTypewriterColorCommand],
        _ => [],
    };

    private static string Describe(Control child) =>
        !string.IsNullOrEmpty(child.Name) ? child.Name
        : AutomationProperties.GetName(child) is { Length: > 0 } name ? $"'{name}'"
        : child.GetType().Name;

    private static IEnumerable<(string Mode, Action Enter, Action Leave)> Modes(MainWindowViewModel vm)
    {
        yield return ("default", () => { }, () => { });
        yield return ("redaction", () => vm.IsRedactionMode = true, () => vm.IsRedactionMode = false);
        yield return ("form-authoring", () => vm.IsFormAuthoringMode = true, () => vm.IsFormAuthoringMode = false);
        // Last: creating a box leaves a pending type-over edit behind.
        yield return ("typewriter-inspector", () =>
        {
            vm.IsTypewriterMode = true;
            vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
        }, () => vm.IsTypewriterMode = false);
    }

    private static async Task Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await Task.Delay(10);
        }
    }
}
