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
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1792 — Square/Circle/FreeText/Stamp/ImageStamp annotations had NO working
/// path through the GUI: their Add*FromDrag commands read
/// CurrentRedactionPageArea, staged only by a genuinely-enabled Redaction
/// Mode's own drag, which ALSO marks that area as a pending redaction and
/// clears the rect as a side effect. There was no gesture that left a shape
/// annotation both reachable and safe — every real click on Square/Circle/
/// etc. from the menu, toolbar or palette hit "Drag a box on the page
/// before adding a...", because no drag could ever stage anything for it to
/// find. This file proves the fix end to end: a real menu click arms
/// InteractionMode.ShapeAnnotation, and a real drag on the page — no
/// Redaction Mode involved — directly places the annotation.
/// </summary>
[Collection("AvaloniaTests")]
public class ShapeAnnotationModeWorkflowTests
{
    [FixedAvaloniaFact]
    public async Task RealMenuClick_ArmsSquareMode_AndARealDrag_PlacesASquare_WithNoRedactionInvolved()
    {
        var (source, output, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Draw a square here");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);

        await vm.LoadDocumentAsync(source);
        await Task.Delay(400);

        // "Draw Square" has no x:Name (MainWindow.axaml) — found by its
        // Command binding, same as StickyNotePopupWorkflowTests does for
        // "Place Sticky Note (click page)".
        var menuItem = window.GetLogicalDescendants().OfType<MenuItem>()
            .FirstOrDefault(m => m.Command == vm.ToggleSquareModeCommand);
        menuItem.Should().NotBeNull("MainWindow.axaml must bind a menu item to ToggleSquareModeCommand");

        RaisePointerPressRelease(menuItem!, window);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        menuItem!.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.IsShapeAnnotationMode.Should().BeTrue("a real click on \"Draw Square\" must arm shape-annotation mode");
        vm.ShapeAnnotationKind.Should().Be(ShapeAnnotationKind.Square);
        // The whole point of #1792: arming this mode must NOT touch redaction
        // at all — the old, broken path borrowed IsRedactionMode's own drag.
        vm.IsRedactionMode.Should().BeFalse();

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        await SinglePageViewerWaits.WaitForSinglePageLaidOutAsync(window, viewer!);

        var (start, end) = DragPoints(window, viewer!, vm);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseDown(start, MouseButton.Left));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(end));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseUp(end, MouseButton.Left));
        for (var i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }

        // Mode stays armed (matches Line/Arrow/Polygon precedent: draw
        // several in a row without re-arming) — RedactionWorkflow must stay
        // untouched, and no dialog (the old guard) must have fired: the
        // annotation is simply there.
        vm.RedactionWorkflow.PendingRedactions.Should().BeEmpty(
            "placing a square must never mark a pending redaction — the two are no longer the same gesture");

        vm.PdfCoreDocument!.GetPage(1).GetAnnotations()
            .Should().ContainSingle(a => a.Subtype == PdfAnnotationSubtype.Square,
                "a real drag with Square mode armed must place exactly one Square annotation, " +
                "with no \"Drag a box...\" dialog in the way");

        await vm.SaveFileAsAsync(output);
        using var reopened = PdfDocument.Open(File.ReadAllBytes(output));
        reopened.GetPage(1).GetAnnotations()
            .Should().Contain(a => a.Subtype == PdfAnnotationSubtype.Square,
                "the square must survive save/reopen too, not just live in the in-memory viewer document");

        window.Close();
        Cleanup(dir);
    }

    private static (Point Start, Point End) DragPoints(
        Window window, PdfViewerControl viewer, MainWindowViewModel vm)
    {
        var overlay = viewer.FindControl<Canvas>("OverlayCanvas")!;
        var page = vm.PdfCoreDocument!.GetPage(1);
        var scale = 120.0 / 72.0;
        var startLocal = new Point(page.VisualWidth * scale * 0.25, page.VisualHeight * scale * 0.25);
        var endLocal = new Point(page.VisualWidth * scale * 0.45, page.VisualHeight * scale * 0.45);
        var start = overlay.TranslatePoint(startLocal, window) ?? startLocal;
        var end = overlay.TranslatePoint(endLocal, window) ?? endLocal;
        return (start, end);
    }

    private static void RaisePointerPressRelease(Control target, Visual root)
    {
        var pointer = new global::Avalonia.Input.Pointer(
            global::Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var pos = new Point(4, 4);
        target.RaiseEvent(new PointerPressedEventArgs(
            target, pointer, root, pos, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
        target.RaiseEvent(new PointerReleasedEventArgs(
            target, pointer, root, pos, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
    }

    private static (string source, string output, string dir) MakePaths()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"excise-1792-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return (Path.Combine(dir, "in.pdf"), Path.Combine(dir, "out.pdf"), dir);
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
