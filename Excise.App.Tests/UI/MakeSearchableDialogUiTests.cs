using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using AwesomeAssertions;
using Excise.Ocr;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Drives the real <see cref="MakeSearchableDialog"/> window (#658) through
/// a headless click sequence: Start → progress → completion, plus the
/// Cancel/Close-button semantics and the tesseract-missing banner. This is
/// the "actually run and click the feature" verification CLAUDE.md asks
/// for, exercised through Avalonia's headless test infrastructure since
/// this environment can't drive a real windowed app. The OCR engine itself
/// is faked here (a controllable delegate) — it's already covered by
/// <c>Excise.Ocr.Tests/PdfSearchableConverterTests.cs</c> and, with real
/// tesseract, by <c>Excise.App.Tests/Integration/MakeSearchableWiringTests.cs</c>.
/// </summary>
[Collection("AvaloniaTests")]
public class MakeSearchableDialogUiTests
{
    private static SearchableDocumentResult MakeResult(int processed = 1, int skipped = 0, int written = 3) =>
        new(processed, skipped, written, 0, Array.Empty<SearchablePageResult>());

    [FixedAvaloniaFact]
    public async Task StartButtonClick_DrivesRealOcrRun_AndSurfacesResultSummaryInTheWindow()
    {
        var vm = new MakeSearchableDialogViewModel(
            tesseractAvailable: true,
            runOcr: (_, _, progress, _) =>
            {
                progress.Report((1, 1));
                return Task.FromResult(MakeResult());
            });

        var window = new MakeSearchableDialog { DataContext = vm };
        window.Show();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        var startButton = FindButtonByName(window, "Start Make Searchable");
        startButton.Should().NotBeNull();
        startButton!.IsEnabled.Should().BeTrue("tesseract is available and nothing is running yet");

        // A real mouse click, not a direct command invocation — routes
        // through the full headless pointer pipeline (OnPointerReleased ->
        // Button.OnClick -> Command.Execute) exactly as a user click would.
        await ClickAsync(window, startButton);

        vm.IsDone.Should().BeTrue("the fake OCR delegate completes synchronously");
        vm.ResultSummary.Should().Contain("1 page(s) processed").And.Contain("3 word(s) written");

        window.UpdateLayout();
        var summaryText = FindTextByAutomationOrContent(window, vm.ResultSummary!);
        summaryText.Should().NotBeNull("the completed run's summary must actually render in the window, not just live on the view model");

        // Once done, Start is disabled and the shared button reads "Close".
        startButton.IsEnabled.Should().BeFalse();
        var cancelOrClose = FindButtonByName(window, "Cancel or Close Make Searchable");
        cancelOrClose.Should().NotBeNull();
        cancelOrClose!.Content.Should().Be("Close");
    }

    [FixedAvaloniaFact]
    public async Task CloseButtonClick_WhenNotRunning_ClosesTheWindow()
    {
        var vm = new MakeSearchableDialogViewModel(
            tesseractAvailable: true,
            runOcr: (_, _, _, _) => Task.FromResult(MakeResult()));

        var window = new MakeSearchableDialog { DataContext = vm };
        window.Show();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        var closed = false;
        window.Closed += (_, _) => closed = true;

        var cancelOrClose = FindButtonByName(window, "Cancel or Close Make Searchable");
        cancelOrClose.Should().NotBeNull();
        cancelOrClose!.Content.Should().Be("Close", "no run has started yet");

        await ClickAsync(window, cancelOrClose);

        closed.Should().BeTrue("the code-behind must wire CloseRequested to actually closing the window");
    }

    [FixedAvaloniaFact]
    public async Task TesseractUnavailable_ShowsInstallHintBanner_AndDisablesStart()
    {
        var vm = new MakeSearchableDialogViewModel(
            tesseractAvailable: false,
            runOcr: (_, _, _, _) => Task.FromResult(MakeResult()));

        var window = new MakeSearchableDialog { DataContext = vm };
        window.Show();
        window.UpdateLayout();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        var startButton = FindButtonByName(window, "Start Make Searchable");
        startButton.Should().NotBeNull();
        startButton!.IsEnabled.Should().BeFalse("Start must stay disabled until tesseract is installed");

        var banner = FindTextByAutomationOrContent(window, MakeSearchableDialogViewModel.TesseractMissingMessage);
        banner.Should().NotBeNull("the exact CLI install-hint text must be visible to the user");
    }

    /// <summary>
    /// Clicks a control via the real headless pointer pipeline (MouseDown +
    /// MouseUp at its on-screen center), rather than raising Button.ClickEvent
    /// directly — raising the routed event alone does not run ButtonBase's
    /// OnClick/Command-execution logic, only a genuine pointer press/release
    /// does.
    /// </summary>
    private static async Task ClickAsync(Window window, Control control)
    {
        var center = new Point(control.Bounds.Width / 2, control.Bounds.Height / 2);
        var pointInWindow = control.TranslatePoint(center, window) ?? default;
        window.MouseDown(pointInWindow, MouseButton.Left);
        window.MouseUp(pointInWindow, MouseButton.Left);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await KeyboardTestHelpers.FlushDispatcherAsync();
    }

    private static Button? FindButtonByName(global::Avalonia.LogicalTree.ILogical root, string automationName) =>
        CollectControls(root)
            .OfType<Button>()
            .FirstOrDefault(button => AutomationProperties.GetName(button) == automationName);

    private static TextBlock? FindTextByAutomationOrContent(global::Avalonia.LogicalTree.ILogical root, string text) =>
        CollectControls(root)
            .OfType<TextBlock>()
            .FirstOrDefault(tb => string.Equals(tb.Text, text, StringComparison.Ordinal));

    private static System.Collections.Generic.IEnumerable<Control> CollectControls(global::Avalonia.LogicalTree.ILogical root)
    {
        var stack = new System.Collections.Generic.Stack<global::Avalonia.LogicalTree.ILogical>();
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
}
