using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.Core.Text;
using Excise.App.Tests.Utilities;
using Excise.App.Tests.Utilities.Fakes;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1792 follow-up — Marc's report: "click on the annotation tool and then
/// be able to select the text and have the annotation applied after i let
/// go of the mouse button." Before this, Highlight/Underline/StrikeOut/
/// Squiggly only worked the other way round: select text first (in the
/// general-purpose Select Text mode), THEN click Add*FromSelection. This
/// file proves the new order end to end: a real click on the tool button
/// arms markup mode, and a real text-selection drag — the ordinary
/// TextSelection gesture, unchanged — applies the markup the instant the
/// mouse comes up, no second click.
/// </summary>
[Collection("AvaloniaTests")]
public class MarkupAnnotationModeWorkflowTests
{
    [FixedAvaloniaFact]
    public async Task RealToolbarClick_ArmsHighlightMode_AndARealTextSelection_AppliesItOnMouseUp()
    {
        var (source, output, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Select this highlighted text");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow(new InMemorySettingsStore()) { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);

        await vm.LoadDocumentAsync(source);
        await Task.Delay(400);

        // Unlike Square/Circle's arming path, markup mode deliberately does
        // NOT force single-page — it reuses plain text selection, which
        // already works in both views (#815). Pin single-page here so the
        // rest of this test's geometry (letter -> window point) is
        // predictable; that choice is test setup, not a production default.
        vm.ViewMode = PdfViewMode.SinglePage;

        vm.ToggleAnnotationToolbarCommand.Execute().Subscribe();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        window.UpdateLayout();

        var highlightButton = window.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Name == "AnnotationToolbarHighlightButton");
        highlightButton.Should().NotBeNull("MainWindow.axaml must declare AnnotationToolbarHighlightButton");

        await ClickAsync(highlightButton!, window);

        vm.IsMarkupAnnotationMode.Should().BeTrue("a real click on the Highlight tool must arm markup mode");
        vm.MarkupAnnotationKind.Should().Be(MarkupAnnotationKind.Highlight);

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        await SinglePageViewerWaits.WaitForSinglePageLaidOutAsync(window, viewer!);

        // Arming the tool must NOT have left the viewer in some other mode —
        // it's still the ordinary TextSelection gesture underneath.
        await Dispatcher.UIThread.InvokeAsync(() =>
            viewer!.InteractionMode.Should().Be(InteractionMode.TextSelection));

        var page = vm.PdfCoreDocument!.GetPage(1);
        var letters = page.Letters.Where(l => l.Value.Length == 1 && !char.IsWhiteSpace(l.Value[0]))
            .OrderBy(l => l.GlyphRectangle.Left).ToList();
        letters.Should().NotBeEmpty("the fixture must have real letters to select");

        var first = letters.First();
        var last = letters.Last();
        var overlay = viewer!.FindControl<Canvas>("OverlayCanvas")!;

        Point WindowPointFor(Letter l)
        {
            var g = l.GlyphRectangle;
            var cx = (g.Left + g.Right) / 2.0;
            var cy = (g.Bottom + g.Top) / 2.0;
            var contentRect = PdfPageRect.FromContentPoints(1, new PdfRectangle(cx, cy, cx, cy));
            var dips = PdfCoordinateMapper.ToViewerDips(page, contentRect, MainWindowViewModel.DefaultViewerRenderDpi);
            return overlay.TranslatePoint(new Point(dips.X, dips.Y), window) ?? default;
        }

        var startPoint = WindowPointFor(first);
        var endPoint = WindowPointFor(last);

        var before = vm.FileState.AnnotationEditsCount;

        await Dispatcher.UIThread.InvokeAsync(() => window.MouseDown(startPoint, MouseButton.Left));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(endPoint));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseUp(endPoint, MouseButton.Left));
        for (var i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }

        vm.FileState.AnnotationEditsCount.Should().Be(before + 1,
            "releasing the mouse after a real drag with Highlight mode armed must apply the highlight " +
            "immediately — no separate click on an Add button");

        // Mode stays armed (matches every other arm-then-gesture tool this
        // session added) — select again without re-clicking the tool.
        vm.IsMarkupAnnotationMode.Should().BeTrue("the tool stays armed for marking up more text");

        vm.PdfCoreDocument!.GetPage(1).GetAnnotations()
            .Should().ContainSingle(a => a.Subtype == PdfAnnotationSubtype.Highlight);

        await vm.SaveFileAsAsync(output);
        using var reopened = PdfDocument.Open(File.ReadAllBytes(output));
        reopened.GetPage(1).GetAnnotations()
            .Should().Contain(a => a.Subtype == PdfAnnotationSubtype.Highlight,
                "the highlight must survive save/reopen too");

        window.Close();
        Cleanup(dir);
    }

    [FixedAvaloniaFact]
    public async Task LeavingMarkupModeViaSelectTextMode_StopsAutoApplying()
    {
        // ToggleTextSelectionModeCommand (bound to "Select Text Mode" / the
        // T shortcut) must disarm markup mode when pressed while one is
        // active, or the next plain selection would silently keep applying
        // a markup the user thinks they turned off. A plain toggle of the
        // underlying flag would not do this correctly: markup mode set it
        // true already, so a naive toggle would flip it straight to false
        // and exit text interaction, not "drop back to plain selection".
        var (source, output, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Plain selection only");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);
        await vm.LoadDocumentAsync(source);
        await Task.Delay(400);

        vm.ToggleHighlightModeCommand.Execute().Subscribe();
        vm.IsMarkupAnnotationMode.Should().BeTrue("precondition");

        vm.ToggleTextSelectionModeCommand.Execute().Subscribe();

        vm.IsMarkupAnnotationMode.Should().BeFalse(
            "pressing Select Text Mode while a markup tool is armed must drop the tool");
        vm.IsTextSelectionMode.Should().BeTrue(
            "it must land back in plain text-selection mode, not exit text interaction entirely");

        window.Close();
        Cleanup(dir);
    }

    private static async Task ClickAsync(Control target, Window window)
    {
        window.UpdateLayout();
        target.BringIntoView();
        window.UpdateLayout();

        var centre = new Point(target.Bounds.Width / 2, target.Bounds.Height / 2);
        var inWindow = target.TranslatePoint(centre, window) ?? default;
        window.MouseDown(inWindow, MouseButton.Left);
        window.MouseUp(inWindow, MouseButton.Left);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await KeyboardTestHelpers.FlushDispatcherAsync();
    }

    private static (string source, string output, string dir) MakePaths()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"excise-1792-markup-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return (Path.Combine(dir, "in.pdf"), Path.Combine(dir, "out.pdf"), dir);
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
