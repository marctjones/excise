using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

// #782: app-wide in-session undo/redo. Each test round-trips the authoritative
// model to its exact prior state, verifies redo re-applies, and confirms the
// stack clears at the save/open lifecycle boundaries. Undo operates only on
// reversible, pre-flatten editing state — the save-then-nothing-to-undo case
// proves flattened content is NOT undoable.
[Collection("AvaloniaTests")]
public class UndoRedoWorkflowTests
{
    // ── Type-over: create + edit each undo/redo to the exact prior state ─────
    [FixedAvaloniaFact]
    public async Task Typewriter_CreateThenEdit_UndoAndRedoRoundTripTheModel()
    {
        var (sourcePath, _, tempDir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Original text");

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        await vm.LoadDocumentAsync(sourcePath);
        vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
        var opId = vm.TypewriterTextOperations.Single().Id;
        vm.OnTypewriterTextEdited(opId, "HELLO782", 1);

        vm.TypewriterTextOperations.Single().Text.Should().Be("HELLO782");
        vm.CanUndo.Should().BeTrue();

        // Undo the edit → box remains, text reverts to the pre-edit (empty) state.
        await vm.UndoCommand.Execute();
        vm.TypewriterTextOperations.Should().HaveCount(1);
        vm.TypewriterTextOperations.Single().HasText.Should().BeFalse();

        // Undo the create → collection is empty again (exact prior state).
        await vm.UndoCommand.Execute();
        vm.TypewriterTextOperations.Should().BeEmpty();
        vm.CanUndo.Should().BeFalse();
        vm.CanRedo.Should().BeTrue();

        // Redo re-applies both, in order.
        await vm.RedoCommand.Execute();
        vm.TypewriterTextOperations.Should().HaveCount(1);
        await vm.RedoCommand.Execute();
        vm.TypewriterTextOperations.Single().Text.Should().Be("HELLO782");
        vm.CanRedo.Should().BeFalse();

        window.Close();
        Cleanup(tempDir);
    }

    // ── Annotation authoring: add undoes to zero, redo restores; final on-disk
    //    reopen is the authoritative check that redo produced a real annotation.
    [FixedAvaloniaFact]
    public async Task Annotation_Add_UndoRemovesIt_RedoRestoresIt()
    {
        var (sourcePath, outputPath, tempDir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Original text");

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        await vm.LoadDocumentAsync(sourcePath);
        await vm.AddStickyNoteAnnotationAsync("NOTE782");

        vm.FileState.AnnotationEditsCount.Should().Be(1);
        AnnotationContents(vm).Should().Contain("NOTE782");
        vm.CanUndo.Should().BeTrue();

        await vm.UndoCommand.Execute();
        AnnotationContents(vm).Should().NotContain("NOTE782");
        vm.FileState.AnnotationEditsCount.Should().Be(0);
        vm.CanRedo.Should().BeTrue();

        await vm.RedoCommand.Execute();
        AnnotationContents(vm).Should().Contain("NOTE782");
        vm.FileState.AnnotationEditsCount.Should().Be(1);

        // Authoritative: the redone annotation must be real on disk.
        await vm.SaveFileAsAsync(outputPath);
        using (var reopened = PdfDocument.Open(outputPath))
        {
            reopened.GetPage(1).GetAnnotations()
                .Should().Contain(a => a.Contents == "NOTE782");
        }
        vm.CanUndo.Should().BeFalse("save clears the in-session history");

        window.Close();
        Cleanup(tempDir);
    }

    // ── Page reorder: move page 1 → position 3, undo restores order, redo re-applies.
    [FixedAvaloniaFact]
    public async Task PageReorder_UndoAndRedoRestorePageOrder()
    {
        var (sourcePath, _, tempDir) = MakePaths();
        TestPdfGenerator.CreateMultiPagePdf(sourcePath, pageCount: 4);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        await vm.LoadDocumentAsync(sourcePath);
        FirstPageText(vm).Should().Contain("Page 1 Content");

        await vm.MovePageAsync(0, 2); // P1 → index 2 ⇒ order becomes [P2,P3,P1,P4]
        FirstPageText(vm).Should().Contain("Page 2 Content");
        vm.CanUndo.Should().BeTrue();

        await vm.UndoCommand.Execute();
        FirstPageText(vm).Should().Contain("Page 1 Content", "undo must restore the original page order");

        await vm.RedoCommand.Execute();
        FirstPageText(vm).Should().Contain("Page 2 Content", "redo must re-apply the reorder");

        window.Close();
        Cleanup(tempDir);
    }

    // ── Page rotate is a clean, exact inverse. ──────────────────────────────
    [FixedAvaloniaFact]
    public async Task PageRotate_UndoRestoresRotation()
    {
        var (sourcePath, _, tempDir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Rotate me");

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        await vm.LoadDocumentAsync(sourcePath);
        var before = vm.PdfCoreDocument!.GetPage(1).Rotation;

        await vm.RotatePageRightCommand.Execute();
        vm.PdfCoreDocument!.GetPage(1).Rotation.Should().Be((before + 90) % 360);

        await vm.UndoCommand.Execute();
        vm.PdfCoreDocument!.GetPage(1).Rotation.Should().Be(before, "undo must restore the exact prior rotation");

        await vm.RedoCommand.Execute();
        vm.PdfCoreDocument!.GetPage(1).Rotation.Should().Be((before + 90) % 360);

        window.Close();
        Cleanup(tempDir);
    }

    // ── Save flattens; nothing before the save remains undoable, and the
    //    flattened content is in the file (irreversible by design). ──────────
    [FixedAvaloniaFact]
    public async Task Save_ClearsHistory_AndFlattenedContentIsNotUndoable()
    {
        var (sourcePath, outputPath, tempDir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Original text");

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        await vm.LoadDocumentAsync(sourcePath);
        vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
        vm.OnTypewriterTextEdited(vm.TypewriterTextOperations.Single().Id, "FLAT782", 1);
        vm.CanUndo.Should().BeTrue();

        await vm.SaveFileAsAsync(outputPath);

        // The edit flattened into the content stream; the pending edit and the
        // whole history are gone. There is nothing to undo.
        vm.TypewriterTextOperations.Should().BeEmpty();
        vm.CanUndo.Should().BeFalse();
        vm.CanRedo.Should().BeFalse();

        using (var saved = PdfDocument.Open(outputPath))
        {
            saved.GetPage(1).Text.Should().Contain("FLAT782", "the flattened text is baked into the file");
        }

        window.Close();
        Cleanup(tempDir);
    }

    // ── Opening a document clears any history from the previous document. ────
    [FixedAvaloniaFact]
    public async Task OpeningAnotherDocument_ClearsHistory()
    {
        var (sourcePath, _, tempDir) = MakePaths();
        var secondPath = Path.Combine(tempDir, "second.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "First doc");
        TestPdfGenerator.CreateSimpleTextPdf(secondPath, "Second doc");

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        await vm.LoadDocumentAsync(sourcePath);
        vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
        vm.CanUndo.Should().BeTrue();

        await vm.LoadDocumentAsync(secondPath);
        vm.CanUndo.Should().BeFalse("opening a document must clear the previous document's history");
        vm.CanRedo.Should().BeFalse();

        window.Close();
        Cleanup(tempDir);
    }

    // ── The Cmd+Z / Cmd+Shift+Z gestures are wired on the macOS native menu. ─
    [FixedAvaloniaFact]
    public void MacNativeMenu_WiresCmdZAndCmdShiftZ_ToUndoRedo()
    {
        var vm = new MainWindowViewModel();
        var menu = MacNativeMenuBuilder.Create(vm);

        var undo = FindItemByCommand(menu, vm.UndoCommand);
        undo.Should().NotBeNull("Undo must be on the native Edit menu");
        undo!.Gesture.Should().Be(new KeyGesture(Key.Z, KeyModifiers.Meta));

        var redo = FindItemByCommand(menu, vm.RedoCommand);
        redo.Should().NotBeNull("Redo must be on the native Edit menu");
        redo!.Gesture.Should().Be(new KeyGesture(Key.Z, KeyModifiers.Meta | KeyModifiers.Shift));
    }

    private static NativeMenuItem? FindItemByCommand(NativeMenu menu, System.Windows.Input.ICommand command)
    {
        foreach (var element in menu.Items)
        {
            if (element is NativeMenuItem item)
            {
                if (ReferenceEquals(item.Command, command))
                    return item;
                if (item.Menu != null)
                {
                    var found = FindItemByCommand(item.Menu, command);
                    if (found != null)
                        return found;
                }
            }
        }
        return null;
    }

    private static string FirstPageText(MainWindowViewModel vm) =>
        vm.PdfCoreDocument!.GetPage(1).Text;

    private static string AnnotationContents(MainWindowViewModel vm) =>
        string.Join("|", vm.PdfCoreDocument!.GetPage(1).GetAnnotations().Select(a => a.Contents ?? string.Empty));

    private static (string source, string output, string dir) MakePaths()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ExciseUndoRedoTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return (Path.Combine(tempDir, "source.pdf"), Path.Combine(tempDir, "output.pdf"), tempDir);
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { }
    }
}
