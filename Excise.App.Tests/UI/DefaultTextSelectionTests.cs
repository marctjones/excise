using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Excise.Avalonia.Controls;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #831 — text selection is the RESTING affordance of the reading view: a drag
/// selects text by default, with no mode toggle, exactly like every other PDF
/// reader. These tests drive the FULL window through the VM
/// <see cref="MainWindowViewModel.InteractionMode"/> → XAML binding →
/// <see cref="PdfViewerControl"/> path — the seam that
/// <c>PdfViewerSelectionTests</c> skips by setting <c>control.InteractionMode</c>
/// directly. That seam is exactly where "I dragged and saw no blue" hid: the
/// control's rendering worked in isolation while the app never put the control
/// into selection mode.
///
/// The assertions are end-to-end: default mode, binding propagation, the blue
/// highlight rectangles actually populated, and the selected text landing on the
/// VM (so Copy works) — none of which trust the control to vouch for itself.
/// </summary>
[Collection("AvaloniaTests")]
public class DefaultTextSelectionTests
{
    private readonly ITestOutputHelper _out;
    public DefaultTextSelectionTests(ITestOutputHelper o) { _out = o; }

    [FixedAvaloniaFact]
    public async Task NewDocument_IsInTextSelectionMode_ByDefault_WithoutAnyToggle()
    {
        var vm = MainWindowViewModelTestFactory.Create();

        vm.IsTextSelectionMode.Should().BeTrue(
            "#831: selection is the resting affordance — on by default, no toggle needed");
        vm.InteractionMode.Should().Be(InteractionMode.TextSelection,
            "the resting interaction mode is text selection, not None");
    }

    [FixedAvaloniaFact]
    public async Task ExitingRedactionMode_RestoresTextSelectionAsRestingMode()
    {
        var vm = MainWindowViewModelTestFactory.Create();

        vm.IsRedactionMode = true;
        vm.InteractionMode.Should().Be(InteractionMode.Redaction);
        vm.IsTextSelectionMode.Should().BeFalse("an editing mode suspends selection while active");

        vm.IsRedactionMode = false;
        vm.IsTextSelectionMode.Should().BeTrue(
            "#831: leaving an editing mode returns to selection, not to a dead no-interaction state");
        vm.InteractionMode.Should().Be(InteractionMode.TextSelection);
    }

    [FixedAvaloniaFact(Timeout = 120000)]
    public async Task DefaultDrag_InContinuousReadingView_SelectsTextAndDrawsBlue_ThroughVmBinding()
    {
        var path = WriteTempPdf(
            TestPdfGenerator.CreatePdfWithTextAtPosition("READINGVIEW", x: 180, y: 520, fontSize: 26));
        try
        {
            MainWindowViewModel vm = null!;
            MainWindow window = null!;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                vm = MainWindowViewModelTestFactory.Create();
                window = new MainWindow { DataContext = vm, Width = 900, Height = 1100 };
                window.Show();
            });
            await Task.Delay(150);

            await vm.LoadDocumentAsync(path);

            // The whole point of #831: NO mode is toggled here. A user opens a
            // document and drags. The default resting mode must already be
            // selection, and it must reach the control THROUGH THE BINDING.
            vm.ViewMode.Should().Be(PdfViewMode.Continuous, "continuous is the app default view");
            vm.InteractionMode.Should().Be(InteractionMode.TextSelection,
                "selection is the default resting mode (#831)");

            PdfViewerControl viewer = null!;
            ItemsControl items = null!;
            PdfPageSlot slot = null!;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                bool ready = false;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    window.UpdateLayout();
                    viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
                    items = viewer?.FindControl<ItemsControl>("ContinuousItems")!;
                    slot = items?.ItemsSource?.Cast<PdfPageSlot>().FirstOrDefault()!;
                    ready = viewer != null && slot != null && items!.Bounds.Width > 1
                            && slot.DisplayWidth > 1
                            && vm.PdfCoreDocument!.GetPage(1).Letters.Count > 0;
                });
                if (ready) break;
                await Task.Delay(100);
            }

            viewer.Should().NotBeNull();
            slot.Should().NotBeNull("continuous view builds a slot for page 1");

            // The binding must have carried the VM's resting mode onto the control.
            await Dispatcher.UIThread.InvokeAsync(() =>
                viewer.InteractionMode.Should().Be(InteractionMode.TextSelection,
                    "InteractionMode=\"{Binding InteractionMode}\" must propagate the default onto the viewer — " +
                    "this is the seam where 'dragged but no blue' hid"));

            var page = vm.PdfCoreDocument!.GetPage(1);
            var letters = page.Letters;
            letters.Should().NotBeEmpty();

            double zoom = 0;
            await Dispatcher.UIThread.InvokeAsync(() => zoom = viewer.ZoomLevel);

            var leftmost = letters.OrderBy(l => l.GlyphRectangle.Left).First();
            var rightmost = letters.OrderByDescending(l => l.GlyphRectangle.Right).First();

            // Translate glyph centers to WINDOW coordinates through the realized
            // page border and drive REAL window mouse events — the true user
            // gesture. (Raising a synthetic PointerPressed directly on an inner
            // control does NOT route to the viewer's root handlers in the full
            // window layout, so it would spuriously "find no blue"; only real
            // window input exercises the path the user actually hits.)
            Point WindowPointFor(Letter l, Control border)
            {
                var g = l.GlyphRectangle;
                double cx = (g.Left + g.Right) * 0.5;
                double cy = (g.Bottom + g.Top) * 0.5;
                var dips = PdfCoordinateMapper.ToContinuousDips(
                    page,
                    PdfPageRect.FromContentPoints(1, new Excise.Core.Document.PdfRectangle(cx, cy, cx, cy)),
                    PdfViewerControl.PointsToDip * zoom);
                return border.TranslatePoint(new Point(dips.X, dips.Y), window) ?? default;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                window.UpdateLayout();
                var container = items.ContainerFromIndex(0)!;
                var border = PageBorderOf(container)!;
                var startPt = WindowPointFor(leftmost, border);
                var endPt = WindowPointFor(rightmost, border);

                window.MouseDown(startPt, MouseButton.Left);
                window.MouseMove(endPt);
                window.MouseUp(endPt, MouseButton.Left);
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                slot.SelectionRects.Count.Should().BeGreaterThanOrEqualTo(2,
                    "dragging across the text with the DEFAULT mode must paint the blue highlight");
            });

            var textDeadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < textDeadline && string.IsNullOrWhiteSpace(vm.SelectedText))
                await Task.Delay(50);
            vm.SelectedText.Should().NotBeNullOrWhiteSpace(
                "the drag must also produce selected text on the VM so Copy works");
            _out.WriteLine($"Selected text via default drag: '{vm.SelectedText}'");

            await Dispatcher.UIThread.InvokeAsync(() => window.Close());
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static Border? PageBorderOf(Control container) =>
        container as Border
        ?? (container as global::Avalonia.Controls.Presenters.ContentPresenter)?.Child as Border;

    private static string WriteTempPdf(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-defaultsel-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}
