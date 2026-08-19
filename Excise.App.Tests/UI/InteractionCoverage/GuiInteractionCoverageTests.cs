using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI.InteractionCoverage;

/// <summary>
/// Publishes the DENOMINATOR for GUI interaction coverage and proves the
/// measurement instrument works.
///
/// <para>The ratio itself is NOT asserted here, on purpose. Any in-assembly
/// assertion of "N% of elements were driven" false-fails on every
/// <c>--filter</c> run and every chunk of a chunked run, because the numerator
/// is only complete after the whole project has executed. That is the lesson
/// <c>check-skip-budget.sh</c> already carries — "needs whole-project runs" —
/// and a gate that always fails locally is a gate people stop reading. So the
/// ratio is judged by <c>scripts/check-gui-interaction-coverage.sh</c> over the
/// artifacts a full run leaves behind, and this class only guarantees those
/// artifacts are real.</para>
/// </summary>
[Collection("AvaloniaTests")]
public class GuiInteractionCoverageTests
{
    /// <summary>
    /// A floor on the ENUMERATED COUNT. Without it, an enumerator bug that
    /// returns twelve elements reads as excellent coverage instead of as a
    /// broken instrument — the vacuous-green shape this repo keeps finding.
    /// Raise it when the window genuinely grows; never lower it to make a run
    /// pass.
    /// </summary>
    private const int MinimumEnumeratedElements = 120;

    [FixedAvaloniaFact]
    public async Task TheInventory_IsPublishedForTheCoverageScript_AndIsNotVacuous()
    {
        var vm = new MainWindowViewModel { ThumbnailPrewarmEnabled = false };
        var window = new MainWindow { DataContext = vm, Width = 1200, Height = 900 };
        window.Show();

        try
        {
            await KeyboardTestHelpers.FlushDispatcherAsync();

            var elements = GuiInteractiveElementInventory.Enumerate(window);

            elements.Count.Should().BeGreaterThanOrEqualTo(MinimumEnumeratedElements,
                "an enumerator that finds almost nothing would report almost-total coverage; " +
                "the floor makes a broken instrument read as red rather than as green");

            // A denominator of only buttons would quietly exclude every toggle,
            // combo and text field — the surfaces least likely to be clicked by
            // a test and most likely to break.
            var kinds = elements.Select(e => e.Kind).Distinct().ToList();
            kinds.Should().Contain("MenuItem");
            kinds.Should().Contain("Button");
            kinds.Should().Contain("Gesture",
                "the 28 keyboard shortcuts are declared as MenuItem InputGesture and are " +
                "affordances in their own right — MainWindow_KeyDown dispatches them, not the menu");

            // APPEND, never overwrite: the recorder contributes every dialog a
            // test opens to this same file, and whichever ran first must not be
            // erased by whichever ran second. The reader deduplicates.
            var path = Path.Combine(
                GuiInteractionRecorder.RepoArtifactsDirectory(), "gui-interaction-inventory.tsv");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllLines(path, elements.Select(e => e.Id).OrderBy(x => x, StringComparer.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The acceptance check for the whole apparatus: a REAL CLICK is recorded
    /// and a COMMAND EXECUTION is not.
    ///
    /// <para>If executing a command leaked into the numerator, this gate would
    /// be measuring command invocation — which <c>GuiClickSafetySweepTests</c>
    /// already does for 61 commands — and would report near-total mouse coverage
    /// for a suite that barely clicks anything. The two halves of this test are
    /// what make the reported number mean what it says.</para>
    /// </summary>
    [FixedAvaloniaFact]
    public async Task ARealClickIsRecorded_AndExecutingACommandIsNot()
    {
        var vm = new MainWindowViewModel { ThumbnailPrewarmEnabled = false };
        var window = new MainWindow { DataContext = vm, Width = 1200, Height = 900 };
        window.Show();

        try
        {
            await KeyboardTestHelpers.FlushDispatcherAsync();

            var target = window.GetLogicalDescendants()
                .OfType<Button>()
                .FirstOrDefault(b => b.IsVisible && b.Bounds.Width > 0 && b.Bounds.Height > 0
                                     && b.Command != null && b.IsEffectivelyEnabled);
            target.Should().NotBeNull(
                "the canary needs one laid-out, enabled, command-bound button; if none exists " +
                "the recorder is untested and every number it produces is unverified");

            var commandNames = GuiInteractionNaming.CommandNameMap(vm);
            var expected = GuiInteractionNaming.Describe(target!, nameof(MainWindow), commandNames);
            expected.Should().NotBeNull();

            // Half one — executing the Command must NOT register as interaction.
            var beforeExecute = GuiInteractionRecorder.ObservedIds.ToHashSet(StringComparer.Ordinal);
            if (target!.Command!.CanExecute(target.CommandParameter))
                target.Command.Execute(target.CommandParameter);
            await KeyboardTestHelpers.FlushDispatcherAsync();

            GuiInteractionRecorder.ObservedIds
                .Where(id => !beforeExecute.Contains(id))
                .Should().BeEmpty(
                    "Command.Execute is not a click: it cannot fail on a collapsed, disabled, " +
                    "covered or unbound control, so counting it would inflate the numerator with " +
                    "the very cases real input exists to catch");

            // Half two — a real pointer press must register.
            var p = target.TranslatePoint(
                new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? default;
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            await KeyboardTestHelpers.FlushDispatcherAsync();
            await KeyboardTestHelpers.FlushDispatcherAsync();

            GuiInteractionRecorder.ObservedIds.Should().Contain(
                id => id.StartsWith(expected!.Id + "\t", StringComparison.Ordinal),
                "a real pointer press on this button must reach the recorder under the SAME id " +
                "the inventory gives it — the event source is the template's inner TextBlock, so " +
                "this also pins the walk up to the nearest interactive ancestor");
        }
        finally
        {
            window.Close();
        }
    }
}

/// <summary>
/// The first defect the interaction-coverage inventory caught, pinned (#1021
/// follow-up).
///
/// <para>Enumerating the declared keyboard gestures turned three of them into
/// nonsense — <c>Ctrl+None</c>, <c>Ctrl+Cancel</c>, <c>Ctrl+Back</c> — because
/// <c>InputGesture="Ctrl+0"</c> is parsed with <c>Enum.Parse&lt;Key&gt;</c>,
/// and <c>"0"</c>, <c>"1"</c> and <c>"2"</c> are valid NUMERIC values of the
/// <see cref="Key"/> enum: <c>None</c>, <c>Cancel</c> and <c>Back</c>. The
/// accelerator shown next to Actual Size / Fit Width / Fit Page therefore did
/// not name the key that works.</para>
///
/// <para>The shortcuts themselves were never broken — <c>MainWindow_KeyDown</c>
/// tests <c>Key.D0</c> directly — which is exactly why this survived: the
/// behaviour was right, only the label lied, and nothing looked at the label.</para>
/// </summary>
[Collection("AvaloniaTests")]
public class MenuAcceleratorGestureTests
{
    [FixedAvaloniaTheory]
    [InlineData("view.zoomActualSize", Key.D0)]
    [InlineData("view.zoomFitWidth", Key.D1)]
    [InlineData("view.zoomFitPage", Key.D2)]
    public void ZoomAcceleratorsNameTheKeyThatActuallyWorks(string commandId, Key expected)
    {
        var vm = new MainWindowViewModel { ThumbnailPrewarmEnabled = false };
        var window = new MainWindow { DataContext = vm, Width = 1200, Height = 900 };
        window.Show();

        try
        {
            var item = window.GetLogicalDescendants()
                .OfType<MenuItem>()
                .FirstOrDefault(m =>
                    Excise.App.Automation.CommandAccessibility.GetCommandId(m) == commandId);
            item.Should().NotBeNull($"the menu must still contain {commandId}");

            item!.InputGesture.Should().NotBeNull();
            item.InputGesture!.Key.Should().Be(expected,
                "the declared accelerator must resolve to the key MainWindow_KeyDown handles; " +
                "\"Ctrl+0\" resolves to Key.None because \"0\" is a valid numeric Key value");
            item.InputGesture.KeyModifiers.Should().Be(KeyModifiers.Control);
        }
        finally
        {
            window.Close();
        }
    }
}
