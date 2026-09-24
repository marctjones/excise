using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// Right-click on an annotation offers Edit (sticky notes) and Delete, and Delete is undoable (#1815).
/// The saved file is read with qpdf's JSON dump, not excise's own parser.
/// </summary>
[Collection("AvaloniaTests")]
public class AnnotationContextMenuTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"excise-annctx-{Guid.NewGuid():N}");
    public AnnotationContextMenuTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly PdfRectangle HighlightRect = new(100, 688, 200, 704);
    private static readonly PdfRectangle NoteRect = new(300, 700, 320, 720);

    private static string QpdfJson(string pdf)
    {
        var psi = new ProcessStartInfo("qpdf", $"--json=2 \"{pdf}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        // #1068/#1516: drain both pipes concurrently and bound the wait.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("qpdf did not exit within 30s (#1516).");
        }
        _ = stderr.GetAwaiter().GetResult();
        return stdout.GetAwaiter().GetResult();
    }

    private async Task<(MainWindowViewModel Vm, MainWindow Window, PdfViewerControl Viewer)> OpenAsync(bool highlight, bool note)
    {
        var path = Path.Combine(_dir, "doc.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 1);
        using (var doc = PdfDocument.Open(File.ReadAllBytes(path)))
        {
            if (highlight) doc.AddHighlightAnnotation(1, HighlightRect, "HIGHLIGHTNOTE", "tester");
            if (note) doc.AddTextAnnotation(1, NoteRect, "STICKYCONTENT", "tester", open: false, withPopup: true);
            doc.Save(path);
        }

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);
        await vm.LoadDocumentAsync(path);
        vm.ViewMode = PdfViewMode.Continuous;
        vm.ZoomLevel = 0.6;
        await Task.Delay(600);
        window.UpdateLayout();
        return (vm, window, window.FindControl<PdfViewerControl>("PdfViewerControl")!);
    }

    /// <summary>
    /// Window position of the centre of a page-1 rectangle, through the app's own coordinate mapper.
    /// Waits for the page container: an edit rebuilds the slots and it is briefly absent.
    /// </summary>
    private static async Task<Point> WindowPointOfAsync(MainWindow window, PdfViewerControl viewer, MainWindowViewModel vm, PdfRectangle rect)
    {
        var items = viewer.FindControl<ItemsControl>("ContinuousItems")!;
        Border? border = null;
        for (var i = 0; i < 60 && border == null; i++)
        {
            window.UpdateLayout();
            var container = items.ContainerFromIndex(0) as Control;
            border = (container as global::Avalonia.Controls.Presenters.ContentPresenter)?.Child as Border ?? container as Border;
            if (border == null || border.Bounds.Height <= 0) { border = null; await Task.Delay(50); }
        }
        border.Should().NotBeNull("page 1 must be realized to translate a page point to the window");

        var cx = (rect.Left + rect.Right) / 2;
        var cy = (rect.Bottom + rect.Top) / 2;
        var dips = PdfCoordinateMapper.ToContinuousDips(
            vm.PdfCoreDocument!.GetPage(1),
            PdfPageRect.FromContentPoints(1, new PdfRectangle(cx, cy, cx, cy)),
            PdfViewerControl.PointsToDip * viewer.ZoomLevel);
        return border!.TranslatePoint(new Point(dips.X, dips.Y), window)!.Value;
    }

    private static async Task RightClickAsync(MainWindow window, Point point)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
        });
        await Task.Delay(150);
    }

    private static MenuItem Item(PdfViewerControl viewer, string header) =>
        viewer.ContextMenu!.Items.OfType<MenuItem>()
            .Single(i => i.Header is string h && h.Contains(header, StringComparison.Ordinal));

    private static void RequireQpdf() => Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task RightClickOnAHighlight_OffersDelete_ButNotEdit_AndOnBlankPageOffersNeither()
    {
        var (vm, window, viewer) = await OpenAsync(highlight: true, note: false);

        await RightClickAsync(window, await WindowPointOfAsync(window, viewer, vm, HighlightRect));
        viewer.ContextMenuAnnotation.Should().NotBeNull("the right-click landed on the highlight");
        Item(viewer, "_Delete Annotation").IsVisible.Should().BeTrue();
        Item(viewer, "Edit Sticky").IsVisible.Should().BeFalse("only a sticky note has an Edit item");

        // Close and right-click empty page area: nothing to delete.
        viewer.ContextMenu!.Close();
        await Task.Delay(200);
        await RightClickAsync(window, await WindowPointOfAsync(window, viewer, vm, new PdfRectangle(400, 200, 420, 220)));
        viewer.ContextMenuAnnotation.Should().BeNull();
        Item(viewer, "_Delete Annotation").IsVisible.Should().BeFalse();
        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task DeletingAHighlight_RemovesItFromTheSavedFile_AndUndoBringsItBack()
    {
        RequireQpdf();
        var (vm, window, viewer) = await OpenAsync(highlight: true, note: false);
        await RightClickAsync(window, await WindowPointOfAsync(window, viewer, vm, HighlightRect));

        Item(viewer, "_Delete Annotation").Command!.Execute(null);
        await Task.Delay(400);
        vm.UndoMenuHeader.Should().Be("_Undo Delete annotation");

        await vm.UndoCommand.Execute();
        var restored = Path.Combine(_dir, "restored.pdf");
        await vm.SaveFileAsAsync(restored);
        var json = QpdfJson(restored);
        json.Should().Contain("/Highlight", "undo must put the highlight back");
        json.Should().Contain("HIGHLIGHTNOTE");
        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task DeletingAHighlight_AsSaved_LeavesNoHighlight()
    {
        RequireQpdf();
        var (vm, window, viewer) = await OpenAsync(highlight: true, note: false);
        await RightClickAsync(window, await WindowPointOfAsync(window, viewer, vm, HighlightRect));

        Item(viewer, "_Delete Annotation").Command!.Execute(null);
        await Task.Delay(400);

        var saved = Path.Combine(_dir, "deleted.pdf");
        await vm.SaveFileAsAsync(saved);
        var json = QpdfJson(saved);
        json.Should().NotContain("/Highlight");
        json.Should().NotContain("HIGHLIGHTNOTE", "the deleted annotation's contents must not survive");
        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task DeletingAHighlight_UndoThenRedo_RemovesItAgain()
    {
        RequireQpdf();
        var (vm, window, viewer) = await OpenAsync(highlight: true, note: false);
        await RightClickAsync(window, await WindowPointOfAsync(window, viewer, vm, HighlightRect));
        Item(viewer, "_Delete Annotation").Command!.Execute(null);
        await Task.Delay(400);

        await vm.UndoCommand.Execute();
        await vm.RedoCommand.Execute();

        var saved = Path.Combine(_dir, "redeleted.pdf");
        await vm.SaveFileAsAsync(saved);
        QpdfJson(saved).Should().NotContain("/Highlight");
        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task StickyNote_EditReopensTheCard_AndDeleteTakesTheNoteAndItsPopup_UndoRestoresBoth()
    {
        RequireQpdf();
        var (vm, window, viewer) = await OpenAsync(highlight: false, note: true);
        await RightClickAsync(window, await WindowPointOfAsync(window, viewer, vm, NoteRect));

        Item(viewer, "Edit Sticky").IsVisible.Should().BeTrue("a sticky note offers Edit");
        Item(viewer, "Edit Sticky").Command!.Execute(null);
        vm.StickyNotePopup.Should().NotBeNull("Edit reopens the note's card for typing");
        vm.CommitOpenStickyNotePopup();
        for (var i = 0; i < 40 && vm.StickyNotePopup != null; i++) await Task.Delay(50);

        await RightClickAsync(window, await WindowPointOfAsync(window, viewer, vm, NoteRect));
        Item(viewer, "_Delete Annotation").Command!.Execute(null);
        await Task.Delay(400);

        var deleted = Path.Combine(_dir, "note-deleted.pdf");
        // Undo history is cleared by a save, so read the undone state from a second document below.
        var jsonDeleted = await SaveAndReadAsync(vm, deleted);
        jsonDeleted.Should().NotContain("/Popup", "deleting a note takes its linked popup too, or it would dangle");
        jsonDeleted.Should().NotContain("STICKYCONTENT");
        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task StickyNote_UndoOfDelete_RestoresTheNoteWithItsPopupLink()
    {
        RequireQpdf();
        var (vm, window, viewer) = await OpenAsync(highlight: false, note: true);
        await RightClickAsync(window, await WindowPointOfAsync(window, viewer, vm, NoteRect));
        Item(viewer, "_Delete Annotation").Command!.Execute(null);
        await Task.Delay(400);

        await vm.UndoCommand.Execute();

        var json = await SaveAndReadAsync(vm, Path.Combine(_dir, "note-restored.pdf"));
        json.Should().Contain("STICKYCONTENT");
        json.Should().Contain("/Popup", "the note's linked popup comes back with it");
        window.Close();
    }

    private static async Task<string> SaveAndReadAsync(MainWindowViewModel vm, string path)
    {
        await vm.SaveFileAsAsync(path);
        return QpdfJson(path);
    }
}
