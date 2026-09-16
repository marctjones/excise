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
/// icons (twice — Dense then Compact), then hide items lowest priority first.
/// Everything it can hide must be reachable from the menu.
///
/// <para>⚠️ <b>Labels do not survive 1280 px in any mode, and that is not a
/// defect.</b> A 1280 px window leaves the panel about 1102 px once the border
/// padding, the reserved zoom column and the margin are taken; the DEFAULT
/// labelled row is about 1187 px. So 1280 is icon-only before any mode adds
/// anything. What is pinned instead is that a mode does not degrade the toolbar
/// EARLIER than the default mode does — see
/// <see cref="CheckTypewriterInspectorCostsOneButton"/>.</para>
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
        // The colours inside its flyout DO have menu entries (Edit > Typewriter
        // Text Color), but the button itself runs no command: it opens a flyout
        // whose size and alignment controls are two-way bindings with no command
        // at all, so no menu item can stand in for the button.
        ["TypewriterStyleFlyoutButton"] =
            "opens a flyout of two-way bindings (TypewriterFontSize, TypewriterAlignmentIndex) plus the colour presets; the button runs no command",
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
            // Icon size per stage, accumulated across every mode and width, so the
            // ladder Full = IconOnly > Dense > Compact can be checked as a whole
            // rather than one observation at a time.
            var iconWidthByStage = new Dictionary<ToolbarStage, double>();
            // Which adjacent stage pairs were ever compared, so "the ordering was
            // never actually observed" fails instead of passing vacuously.
            var stagesOrdered = new HashSet<(ToolbarStage Wider, ToolbarStage Narrower)>();
            // Full-stage row width per mode, for the typewriter-inspector budget below.
            var fullRowByMode = new Dictionary<string, double>(StringComparer.Ordinal);
            var stageByModeAndWidth = new Dictionary<(string Mode, double Width), ToolbarStage>();
            var hiddenByModeAndWidth = new Dictionary<(string Mode, double Width), int>();
            var panelChildrenByMode = new Dictionary<string, int>(StringComparer.Ordinal);

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
                        $"{label}: stage {panel.Stage}, available {available:F0}, rows full/icon/dense/compact " +
                        $"{Required(ToolbarStage.Full):F0}/{Required(ToolbarStage.IconOnly):F0}/" +
                        $"{Required(ToolbarStage.Dense):F0}/{Required(ToolbarStage.Compact):F0}, " +
                        $"toolbar height {toolbar.Bounds.Height:F0}, hidden [{string.Join(", ", hidden.Select(Describe))}]");

                    // Every stage the planner measured must be strictly narrower
                    // than the stage before it. This is checked wherever it is
                    // observable — at the narrow widths the planner probes all
                    // four — rather than only at whichever width happens to
                    // SELECT a given stage.
                    ToolbarStage[] ladder =
                        [ToolbarStage.Full, ToolbarStage.IconOnly, ToolbarStage.Dense, ToolbarStage.Compact];
                    for (var s = 1; s < ladder.Length; s++)
                    {
                        if (!plan.StageWidths.TryGetValue(ladder[s - 1], out var wider) ||
                            !plan.StageWidths.TryGetValue(ladder[s], out var narrower))
                        {
                            continue;
                        }

                        stagesOrdered.Add((ladder[s - 1], ladder[s]));
                        if (narrower >= wider - PriorityToolbarLayout.Epsilon)
                        {
                            problems.Add(
                                $"{label}: the {ladder[s]} row is {narrower:F0} px and the {ladder[s - 1]} row is {wider:F0} px; " +
                                "each stage must be strictly narrower, or a narrower window could show more than a wider one");
                        }
                    }

                    stageByModeAndWidth[(mode, width)] = panel.Stage;
                    hiddenByModeAndWidth[(mode, width)] = hidden.Count;
                    if (!double.IsNaN(Required(ToolbarStage.Full)))
                        fullRowByMode.TryAdd(mode, Required(ToolbarStage.Full));
                    panelChildrenByMode.TryAdd(mode, items.Count);

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
                        case ToolbarStage.Dense when Fits(ToolbarStage.Full) || Fits(ToolbarStage.IconOnly):
                            problems.Add($"{label}: icons shrunk although a larger stage fits");
                            break;
                        case ToolbarStage.Dense when !Fits(ToolbarStage.Dense):
                            problems.Add($"{label}: dense stage kept although it does not fit");
                            break;
                        case ToolbarStage.Compact when Fits(ToolbarStage.Full) || Fits(ToolbarStage.IconOnly) || Fits(ToolbarStage.Dense):
                            problems.Add($"{label}: icons shrunk to their smallest although a larger stage fits");
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

                    // 3. Icons keep their full size until the labels have gone, and
                    //    then shrink once per shrink stage (Dense, then Compact).
                    var icons = panel.GetVisualDescendants().OfType<PathIcon>()
                        .Where(i => i.Classes.Contains("toolbar-icon") && i.IsEffectivelyVisible)
                        .ToList();
                    if (icons.Count > 0)
                    {
                        var iconWidth = icons.Max(i => i.Bounds.Width);
                        if (panel.Stage == ToolbarStage.Full)
                            fullIconWidth ??= iconWidth;
                        if (iconWidthByStage.TryGetValue(panel.Stage, out var seen))
                        {
                            if (Math.Abs(seen - iconWidth) > 0.5)
                                problems.Add($"{label}: icons are {iconWidth:F0} px at stage {panel.Stage}, {seen:F0} px elsewhere at the same stage");
                        }
                        else
                        {
                            iconWidthByStage[panel.Stage] = iconWidth;
                        }

                        if (fullIconWidth is double fullIcon)
                        {
                            var shouldBeFullSize = panel.Stage <= ToolbarStage.IconOnly;
                            if (shouldBeFullSize && Math.Abs(iconWidth - fullIcon) > 0.5)
                                problems.Add($"{label}: an icon is {iconWidth:F0} px before the icons were allowed to shrink ({fullIcon:F0} px)");
                            if (!shouldBeFullSize && iconWidth >= fullIcon - 0.5)
                                problems.Add($"{label}: stage {panel.Stage} but an icon is still {iconWidth:F0} px");
                        }
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

            foreach (var pair in new[]
                     {
                         (ToolbarStage.Full, ToolbarStage.IconOnly),
                         (ToolbarStage.IconOnly, ToolbarStage.Dense),
                         (ToolbarStage.Dense, ToolbarStage.Compact),
                     })
            {
                if (!stagesOrdered.Contains(pair))
                    problems.Add($"{pair.Item1} was never measured against {pair.Item2}, so their ordering is unproven");
            }

            CheckIconSizeLadder(iconWidthByStage, problems);
            CheckTypewriterInspectorCostsOneButton(
                fullRowByMode, panelChildrenByMode, stageByModeAndWidth, hiddenByModeAndWidth, problems);

            problems.Should().BeEmpty();
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(pdfPath);
        }
    }

    /// <summary>
    /// There are now two icon-shrinking stages, so "icons shrink before anything
    /// hides" is no longer a single before/after: it is a ladder. Icons must be
    /// full size at <see cref="ToolbarStage.Full"/> and
    /// <see cref="ToolbarStage.IconOnly"/>, smaller at <see cref="ToolbarStage.Dense"/>,
    /// and smaller again at <see cref="ToolbarStage.Compact"/>. A stage no width
    /// in the sweep SELECTED is not checked here — the row-width ordering above
    /// covers it from the planner's own probes either way.
    /// </summary>
    private static void CheckIconSizeLadder(
        IReadOnlyDictionary<ToolbarStage, double> iconWidthByStage, List<string> problems)
    {
        ToolbarStage[] ladder =
            [ToolbarStage.Full, ToolbarStage.IconOnly, ToolbarStage.Dense, ToolbarStage.Compact];
        for (var i = 1; i < ladder.Length; i++)
        {
            if (!iconWidthByStage.TryGetValue(ladder[i - 1], out var wider) ||
                !iconWidthByStage.TryGetValue(ladder[i], out var narrower))
            {
                continue;
            }

            var mustBeEqual = ladder[i] == ToolbarStage.IconOnly;
            if (mustBeEqual && Math.Abs(wider - narrower) > 0.5)
                problems.Add($"icons are {narrower:F0} px at {ladder[i]} but {wider:F0} px at {ladder[i - 1]}: labels must go before icons shrink");
            if (!mustBeEqual && narrower >= wider - 0.5)
                problems.Add($"icons are {narrower:F0} px at {ladder[i]} and {wider:F0} px at {ladder[i - 1]}: every shrink stage must be strictly smaller");
        }
    }

    /// <summary>
    /// #1476 follow-up: turning on typewriter mode must not degrade the rest of
    /// the toolbar. The style inspector used to be three inline controls costing
    /// ~331 px, which pushed the row past icon-only at 1600 px — the user lost
    /// every unrelated label for the duration.
    ///
    /// <para>Note what is NOT asserted: that labels survive at 1280 px. They
    /// cannot, in ANY mode. At a 1280 px window the panel gets ~1102 px and the
    /// DEFAULT labelled row is ~1187 px, so 1280 is already icon-only before the
    /// inspector exists. The property that matters, and the one pinned here, is
    /// that typewriter mode degrades no earlier than the default mode does.</para>
    /// </summary>
    private static void CheckTypewriterInspectorCostsOneButton(
        IReadOnlyDictionary<string, double> fullRowByMode,
        IReadOnlyDictionary<string, int> panelChildrenByMode,
        IReadOnlyDictionary<(string Mode, double Width), ToolbarStage> stageByModeAndWidth,
        IReadOnlyDictionary<(string Mode, double Width), int> hiddenByModeAndWidth,
        List<string> problems)
    {
        const string inspector = "typewriter-inspector";

        if (!fullRowByMode.TryGetValue("default", out var defaultRow) ||
            !fullRowByMode.TryGetValue(inspector, out var inspectorRow))
        {
            problems.Add("the default and typewriter-inspector rows were not both measured");
            return;
        }

        // One toolbar child's worth. MEASURED 2026-09-15: the inspector adds
        // 109 px (typewriter Full 1240 vs default 1131) — a labelled button
        // with an icon, a colour chip and the word "Style", plus its separator
        // and two spacings. Inline, the three controls cost 331 px.
        //
        // The threshold is 150, not 110: it exists to catch a regression back
        // towards inline, and every realistic one clears it — putting the
        // alignment combo back on the toolbar is +92 (→ ~201) and pulling the
        // colour button back out is +79 (→ ~188). A tighter bound would instead
        // be measuring the font, and would redden on a theme change that cost
        // the toolbar nothing.
        const double InspectorWidthBudget = 150;
        var delta = inspectorRow - defaultRow;
        if (delta > InspectorWidthBudget)
        {
            problems.Add(
                $"the typewriter style inspector adds {delta:F0} px to the labelled row " +
                $"({inspectorRow:F0} vs {defaultRow:F0}); it must cost at most {InspectorWidthBudget:F0} px");
        }

        if (panelChildrenByMode.TryGetValue("default", out var defaultChildren) &&
            panelChildrenByMode.TryGetValue(inspector, out var inspectorChildren) &&
            inspectorChildren - defaultChildren > 1)
        {
            problems.Add(
                $"the inspector adds {inspectorChildren - defaultChildren} toolbar children; " +
                "size, alignment and colour belong in ONE flyout, so it may add exactly one button " +
                "(the separator is a separator and does not count)");
        }

        // 1600 px: the labels survive. This is the regression the flyout exists
        // to fix — before it, typewriter mode was icon-only at 1600.
        if (stageByModeAndWidth.TryGetValue((inspector, 1600), out var at1600) && at1600 != ToolbarStage.Full)
        {
            problems.Add(
                $"typewriter-inspector @ 1600px is at stage {at1600}; the labels must survive there " +
                $"(row {inspectorRow:F0} px)");
        }

        // Stage parity at the two widths where the whole toolbar still fits
        // comfortably. It is deliberately NOT asserted at 1024: there the
        // inspector's ~100 px really does cost a stage, and demanding parity
        // would mean demanding the inspector be free.
        foreach (var width in new[] { 1600.0, 1280 })
        {
            if (!stageByModeAndWidth.TryGetValue((inspector, width), out var inspectorStage) ||
                !stageByModeAndWidth.TryGetValue(("default", width), out var defaultStage))
            {
                continue;
            }

            if (inspectorStage > defaultStage)
            {
                problems.Add(
                    $"typewriter-inspector @ {width:F0}px degrades to {inspectorStage} while the default mode is at " +
                    $"{defaultStage}: styling a text box must not cost the rest of the toolbar a stage");
            }
        }

        foreach (var width in new[] { 1600.0, 1280, 1024 })
        {
            if (stageByModeAndWidth.TryGetValue((inspector, width), out var stage) && stage > ToolbarStage.Dense)
                problems.Add($"typewriter-inspector @ {width:F0}px is already at {stage}; the smallest icons must be a narrower-window measure");
            if (hiddenByModeAndWidth.TryGetValue((inspector, width), out var hidden) && hidden > 0)
                problems.Add($"typewriter-inspector @ {width:F0}px hides {hidden} item(s); nothing may hide at these widths");
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
            var commands = ToolbarCommands(child);

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

    // A flyout host runs no command of its own; what is inside it is checked by
    // TypewriterStyleFlyoutTests and TypewriterColorPresetAccessibilityTests,
    // which open the flyout (its content is not in the tree until then, so its
    // bindings cannot be read from here).
    private static IReadOnlyList<ICommand> ToolbarCommands(Control child) => child switch
    {
        Button { Command: { } command } => [command],
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
