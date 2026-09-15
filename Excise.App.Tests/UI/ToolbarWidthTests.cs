using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1476: the main toolbar showed a horizontal scroll bar whenever the window
/// was narrower than its action strip. Measured on the scroll viewer before the
/// fix, it overflowed at EVERY width below full screen: extent 1187 px against a
/// 1102 px viewport at 1280 in the default mode (1279 in redaction mode, 1299 in
/// form authoring), and 341-453 px short at 1024.
///
/// <para>The strip is now a wrapping panel whose labelled buttons collapse to
/// icons below 1480 px. What must hold at every width and mode: nothing scrolls,
/// every action lies inside the toolbar, the zoom controls stay on screen (#589),
/// and every action keeps its accessible name.</para>
/// </summary>
[Collection("AvaloniaTests")]
public class ToolbarWidthTests
{
    private readonly ITestOutputHelper _out;
    private readonly string _tempDir;

    public ToolbarWidthTests(ITestOutputHelper output)
    {
        _out = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "excise-1476", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private static readonly double[] Widths = [1600, 1440, 1280, 1024, 900];

    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task Toolbar_NeverScrolls_KeepsEveryActionAndZoomVisible_AtCommonWidths()
    {
        var pdfPath = Path.Combine(_tempDir, "toolbar.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 2);

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(pdfPath);
            var toolbar = window.FindControl<Border>("ToolbarBorder");
            var actions = window.FindControl<WrapPanel>("ToolbarActionsPanel");
            toolbar.Should().NotBeNull();
            actions.Should().NotBeNull();

            actions!.GetVisualAncestors().TakeWhile(ancestor => !ReferenceEquals(ancestor, toolbar))
                .OfType<ScrollViewer>().Should().BeEmpty("#1476: the toolbar actions must not be in a scroll viewer");

            var fitWidth = window.GetVisualDescendants().OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "Fit Width");

            var problems = new List<string>();
            foreach (var (mode, enter, leave) in Modes(vm))
            {
                enter();
                foreach (var width in Widths)
                {
                    window.Width = width;
                    await Settle(window);

                    var visible = actions.Children.Where(child => child.IsVisible).ToList();
                    var rows = CountRows(visible);
                    var fitRight = fitWidth.TranslatePoint(new Point(fitWidth.Bounds.Width, 0), window)!.Value.X;
                    var labelsShown = LabelsShown(actions);
                    _out.WriteLine(
                        $"{mode} @ {width:F0}px: {rows} row(s), labels {(labelsShown ? "shown" : "hidden")}, " +
                        $"panel {actions.Bounds.Width:F0}px, zoom Fit right edge {fitRight:F0}px");

                    foreach (var child in visible.Where(child => child.Bounds.Right > actions.Bounds.Width + 0.5))
                        problems.Add($"{mode} @ {width:F0}px: {Describe(child)} ends at {child.Bounds.Right:F0}, past the panel's {actions.Bounds.Width:F0}");
                    if (fitRight > window.Bounds.Width + 0.5)
                        problems.Add($"{mode} @ {width:F0}px: zoom Fit ends at {fitRight:F0}, off the {window.Bounds.Width:F0}px window (#589)");
                    if (width >= 1600 && !labelsShown)
                        problems.Add($"{mode} @ {width:F0}px: a wide window must keep the button labels");
                    if (width <= 1280 && labelsShown)
                        problems.Add($"{mode} @ {width:F0}px: the labels must collapse to icons below 1480px");
                    if (width >= 1280 && rows != 1)
                        problems.Add($"{mode} @ {width:F0}px: the toolbar must stay one row at 1280px and wider, found {rows}");
                }
                leave();
                await Settle(window);
            }

            foreach (var button in actions.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible))
            {
                var name = AutomationProperties.GetName(button);
                if (string.IsNullOrWhiteSpace(name) && ToolTip.GetTip(button) is null)
                    problems.Add($"a toolbar button has neither an accessible name nor a tooltip: {Describe(button)}");
            }

            problems.Should().BeEmpty();
        }
        finally
        {
            window.Close();
            TestPdfGenerator.CleanupTestFile(pdfPath);
        }
    }

    /// <summary>
    /// Rows of a wrap panel. Items in one row have different tops when they differ
    /// in height (a 24 px separator is centred against 32 px buttons), so a new
    /// row starts only where an item begins below the current row's items.
    /// </summary>
    private static int CountRows(IReadOnlyList<Control> visible)
    {
        var rows = 0;
        var rowBottom = double.NegativeInfinity;
        foreach (var child in visible.OrderBy(child => child.Bounds.Top))
        {
            if (child.Bounds.Top >= rowBottom - 0.5)
            {
                rows++;
                rowBottom = child.Bounds.Bottom;
            }
            else
            {
                rowBottom = Math.Max(rowBottom, child.Bounds.Bottom);
            }
        }
        return rows;
    }

    private static bool LabelsShown(Control actions) =>
        actions.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.Classes.Contains("toolbar-label"))
            .Any(block => block.IsEffectivelyVisible);

    private static string Describe(Visual child) =>
        child is Control control && AutomationProperties.GetName(control) is { Length: > 0 } name
            ? $"'{name}'"
            : child.GetType().Name;

    private static IEnumerable<(string Mode, Action Enter, Action Leave)> Modes(MainWindowViewModel vm)
    {
        yield return ("default", () => { }, () => { });
        yield return ("redaction", () => vm.IsRedactionMode = true, () => vm.IsRedactionMode = false);
        yield return ("form-authoring", () => vm.IsFormAuthoringMode = true, () => vm.IsFormAuthoringMode = false);
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
