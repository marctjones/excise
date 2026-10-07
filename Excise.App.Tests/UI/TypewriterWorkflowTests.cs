using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

[Collection("AvaloniaTests")]
public class TypewriterWorkflowTests
{
    [FixedAvaloniaFact]
    public async Task TypewriterUndo_RedrawDoesNotRecordAnotherEditOrEraseRedo()
    {
        var (sourcePath, _, _) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Original text");
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(sourcePath);
            await vm.ToggleTypewriterModeCommand.Execute();
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            await SinglePageViewerWaits.WaitForSinglePageLaidOutAsync(window, viewer);
            vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
            await KeyboardTestHelpers.FlushDispatcherAsync();
            var editor = viewer.SinglePagePart.TypewriterLayer.GetVisualDescendants().OfType<TextBox>().Single();
            editor.Focus();
            await window.TypeTextAsync("BEFORE");
            await window.TypeTextAsync("AFTER");

            // Native menu actions invoke the document command directly, bypassing window-key guards (#1977).
            await vm.UndoCommand.Execute();
            await KeyboardTestHelpers.FlushDispatcherAsync();
            vm.TypewriterTextOperations.Single().Text.Should().Be("BEFORE");
            vm.CanRedo.Should().BeTrue("rehydrating the editor must not push a new edit");
            for (var replay = 0; replay < 3; replay++)
            {
                await vm.RedoCommand.Execute();
                await KeyboardTestHelpers.FlushDispatcherAsync();
                vm.TypewriterTextOperations.Single().Text.Should().Be("BEFOREAFTER");
                viewer.SinglePagePart.TypewriterLayer.GetVisualDescendants().OfType<TextBox>().Single()
                    .Text.Should().Be("BEFOREAFTER");
                await vm.UndoCommand.Execute();
                await KeyboardTestHelpers.FlushDispatcherAsync();
                vm.TypewriterTextOperations.Single().Text.Should().Be("BEFORE");
                vm.CanRedo.Should().BeTrue();
            }
        }
        finally { window.Close(); }
    }

    [FixedAvaloniaFact]
    public async Task SaveFileAsAsync_OverflowKeepsPendingEditsAndDoesNotWriteOutput()
    {
        var (sourcePath, outputPath, _) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Original text");
        var toast = new Excise.App.Services.ToastService();
        string? details = null;
        toast.ToastRequested += (_, e) => details = e.Details;
        var vm = MainWindowViewModelTestFactory.Create(toastService: toast);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(sourcePath);
            vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 635), 1);
            var id = vm.TypewriterTextOperations.Single().Id;
            const string marker = "FIRSTLINE\nLASTLINE";
            vm.OnTypewriterTextEdited(id, marker, 1);

            await vm.SaveFileAsAsync(outputPath);

            File.Exists(outputPath).Should().BeFalse("an overflowing save must fail before writing");
            vm.TypewriterTextOperations.Single().Text.Should().Be(marker);
            vm.HasPendingTypewriterEdits.Should().BeTrue();
            vm.CanUndo.Should().BeTrue("failed saves must retain edit history");
            details.Should().Contain("page 1").And.Contain("resize");
        }
        finally { window.Close(); }
    }

    [FixedAvaloniaFact]
    public async Task SaveFileAsAsync_OverflowPreservesExistingOutput_ThenResizeSavesAllText()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftotextTextExtractor.IsAvailable, "pdftotext not installed");
        var (sourcePath, outputPath, _) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "PREEXISTING1978");
        TestPdfGenerator.CreateSimpleTextPdf(outputPath, "DONOTOVERWRITE1978");
        var before = File.ReadAllBytes(outputPath);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(sourcePath);
            vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 212, 635), 1);
            var id = vm.TypewriterTextOperations.Single().Id;
            const string marker = "EXCISE QA typewriter 3.16.0 r t R T";
            vm.OnTypewriterTextEdited(id, marker, 1);
            await vm.SaveFileAsAsync(outputPath);
            File.ReadAllBytes(outputPath).Should().Equal(before);
            vm.TypewriterTextOperations.Single().Text.Should().Be(marker);

            vm.OnTypewriterTextBoundsChanged(id, new PdfRectangle(72, 600, 400, 660), 1);
            await vm.SaveFileAsAsync(outputPath);
            vm.TypewriterTextOperations.Should().BeEmpty();
            foreach (var text in new[] { MutoolTextExtractor.ExtractPage(outputPath, 1), PdftotextTextExtractor.ExtractPage(outputPath, 1) })
                text.Should().NotBeNull().And.Contain(marker).And.Contain("PREEXISTING1978");
        }
        finally { window.Close(); }
    }

    [FixedAvaloniaFact]
    public async Task TypingR_KeepsTypewriterEditorFocusedAndPreservesCompleteText()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Excise.AppTypewriterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var sourcePath = Path.Combine(tempDir, "source.pdf");
        TestPdfGenerator.CreateMultiPagePdf(sourcePath, pageCount: 3);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        try
        {
            await vm.LoadDocumentAsync(sourcePath);
            await vm.ToggleTypewriterModeCommand.Execute();
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
            await SinglePageViewerWaits.WaitForSinglePageLaidOutAsync(window, viewer);
            vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
            await KeyboardTestHelpers.FlushDispatcherAsync();
            var editor = viewer.SinglePagePart.TypewriterLayer.GetVisualDescendants().OfType<TextBox>().Single();
            editor.Focus();
            await window.TypeTextAsync("EXCISE QA typew");
            if (OperatingSystem.IsMacOS())
            {
                await window.PressKeyAsync(Key.F, RawInputModifiers.Control);
                vm.IsSearchVisible.Should().BeFalse("macOS Control-F belongs to the editor, not global Find (#1977)");
            }
            // TextInput alone bypasses shortcut routing: exercise the real key-down first (#1976).
            await window.PressKeyAsync(Key.R);
            await window.PressKeyAsync(Key.T);
            foreach (var key in new[] { Key.Down, Key.Up, Key.Home, Key.End, Key.PageDown, Key.PageUp })
                await window.PressKeyAsync(key);
            vm.CurrentPageIndex.Should().Be(0, "cursor navigation inside an editor must not navigate the PDF (#1977)");
            vm.IsTypewriterMode.Should().BeTrue();
            vm.IsRedactionMode.Should().BeFalse();
            window.FocusManager!.GetFocusedElement().Should().BeSameAs(editor);
            editor.CaretIndex = editor.Text!.Length;
            await window.TypeTextAsync("riter 3.16.0");
            editor.Text.Should().Be("EXCISE QA typewriter 3.16.0");
            vm.TypewriterTextOperations.Single().Text.Should().Be(editor.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [FixedAvaloniaFact]
    public async Task SaveFileAsAsync_FlattensPendingTypewriterTextIntoSavedPdf()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Excise.AppTypewriterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var sourcePath = Path.Combine(tempDir, "source.pdf");
        var outputPath = Path.Combine(tempDir, "output.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Original text");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        await vm.LoadDocumentAsync(sourcePath);
        vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
        var operationId = vm.TypewriterTextOperations.Single().Id;
        vm.OnTypewriterTextEdited(operationId, "Saved typewriter note", 1);

        await vm.SaveFileAsAsync(outputPath);

        using var saved = PdfDocument.Open(outputPath);
        saved.GetPage(1).Text.Should().Contain("Saved typewriter note");
        vm.TypewriterTextOperations.Should().BeEmpty();
        vm.FileState.TypewriterEditsCount.Should().Be(0);

        window.Close();
    }

    // #780: no-self-oracle. SaveFileAsAsync_Flattens... above verifies the saved
    // file with excise's OWN extractor (saved.GetPage(1).Text) — excise vouching
    // for excise. The engine-path fidelity suite (Excise.Rendering.Tests'
    // TypewriterOutputFidelityTests) does ask an INDEPENDENT tool, but it goes
    // through PdfTypewriterTextApplier/ApplyAndSave, NOT the GUI save command.
    // So nothing covered: REAL GUI save command → disk → independent extractor.
    // This closes that gap: an independent tool (mutool) must read the typed
    // text back out of the bytes the GUI save command wrote, and the
    // pre-existing page text must survive (typing is an overlay, not a redaction).
    [FixedAvaloniaFact]
    public async Task SaveFileAsAsync_TypedText_IsReadBackByAnIndependentExtractor()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var tempDir = Path.Combine(Path.GetTempPath(), "Excise.AppTypewriterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var sourcePath = Path.Combine(tempDir, "source.pdf");
        var outputPath = Path.Combine(tempDir, "output.pdf");

        // Single-token strings so a mutool space/word-break cannot fail Contain.
        const string preExisting = "PREEXISTING780";
        const string typed = "TYPEDNOTE780";
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, preExisting);

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        await vm.LoadDocumentAsync(sourcePath);
        vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
        var operationId = vm.TypewriterTextOperations.Single().Id;
        vm.OnTypewriterTextEdited(operationId, typed, 1);

        // The REAL GUI save command writes the file to disk (and reloads it).
        await vm.SaveFileAsAsync(outputPath);
        vm.TypewriterTextOperations.Should().BeEmpty("the pending edit must have flattened on save");

        // Free the file: nothing holds an exclusive handle here (the save path's
        // reload is internal), so an external process can read the saved bytes.
        window.Close();

        // INDEPENDENT ORACLE — mutool, not excise's own .Text. If the typed text
        // is only in a carrier excise can read but an independent tool cannot,
        // this fails where saved.GetPage(1).Text would pass.
        var extracted = MutoolTextExtractor.ExtractPage(outputPath, 1);
        extracted.Should().NotBeNull("mutool must be able to read the GUI-saved file");
        extracted!.Should().Contain(typed,
            "the typed note must be real text an independent tool can read out of the bytes the " +
            "GUI save command wrote — not just text excise's own extractor vouches for");
        extracted.Should().Contain(preExisting,
            "the GUI save must not destroy pre-existing page text — typewriter is an overlay, " +
            "not a redaction");
    }

    // #780: exiting typewriter mode must NOT discard pending edits, but the user
    // must be able to SEE they are pending — otherwise they flatten unseen on
    // the next save. The edit survives mode-exit, the indicator reflects it, and
    // a save still flattens it (reopen proves the text is really in the PDF).
    [FixedAvaloniaFact]
    public async Task ExitingTypewriterMode_KeepsPendingEdit_AndIndicatorReflectsIt_AndSaveStillFlattens()
    {
        var (sourcePath, outputPath, tempDir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Original text");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        await vm.LoadDocumentAsync(sourcePath);
        await vm.ToggleTypewriterModeCommand.Execute();
        vm.IsTypewriterMode.Should().BeTrue();

        vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
        var opId = vm.TypewriterTextOperations.Single().Id;
        vm.OnTypewriterTextEdited(opId, "Survives mode exit", 1);

        // Leave the mode as a user would after "backing out".
        await vm.ToggleTypewriterModeCommand.Execute();
        vm.IsTypewriterMode.Should().BeFalse();

        // Non-destructive: the edit is still pending and visible.
        vm.TypewriterTextOperations.Should().HaveCount(1, "mode-exit must not silently drop edits");
        vm.FileState.TypewriterEditsCount.Should().Be(1);
        vm.HasPendingTypewriterEdits.Should().BeTrue();
        vm.StatusBarText.Should().Contain("typewriter edit(s) pending",
            "the indicator must persist even after leaving the mode");

        await vm.SaveFileAsAsync(outputPath);

        using var saved = PdfDocument.Open(outputPath);
        saved.GetPage(1).Text.Should().Contain("Survives mode exit",
            "flatten-on-save is correct behaviour and must be preserved");

        window.Close();
        Cleanup(tempDir);
    }

    // #780: the explicit discard command is the ONLY non-saving way to clear
    // pending edits. After discard, a save must NOT contain the text — proving
    // the edit was genuinely dropped, not merely hidden from the in-memory list.
    [FixedAvaloniaFact]
    public async Task DiscardPendingTypewriterEdits_ClearsState_AndTextIsAbsentFromSavedPdf()
    {
        var (sourcePath, outputPath, tempDir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Original text");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        await vm.LoadDocumentAsync(sourcePath);
        vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
        var opId = vm.TypewriterTextOperations.Single().Id;
        vm.OnTypewriterTextEdited(opId, "Should be discarded", 1);
        vm.HasPendingTypewriterEdits.Should().BeTrue();

        await vm.DiscardPendingTypewriterEditsCommand.Execute();

        vm.TypewriterTextOperations.Should().BeEmpty();
        vm.FileState.TypewriterEditsCount.Should().Be(0);
        vm.HasPendingTypewriterEdits.Should().BeFalse();

        await vm.SaveFileAsAsync(outputPath);

        using var saved = PdfDocument.Open(outputPath);
        saved.GetPage(1).Text.Should().NotContain("Should be discarded",
            "a discarded edit must never reach the saved PDF");

        window.Close();
        Cleanup(tempDir);
    }

    // #780: pending edits on a non-current page are invisible (the layer only
    // renders the current page) yet still flatten. GoToNextPendingTypewriterEdit
    // navigates to them so nothing bakes in unseen.
    [FixedAvaloniaFact]
    public async Task GoToNextPendingTypewriterEdit_NavigatesToOffPageEdit()
    {
        var (sourcePath, _, tempDir) = MakePaths();
        TestPdfGenerator.CreateMultiPagePdf(sourcePath, pageCount: 4);

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        await vm.LoadDocumentAsync(sourcePath);
        vm.CurrentPageIndex = 0; // viewing page 1
        vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 3);
        vm.OnTypewriterTextEdited(vm.TypewriterTextOperations.Single().Id, "On page three", 3);

        await vm.GoToNextPendingTypewriterEditCommand.Execute();

        vm.CurrentPage.Should().Be(3, "navigation must reach the off-page pending edit");

        window.Close();
        Cleanup(tempDir);
    }

    // #780: the pointer-driven creation path (PointerPressed → PointerReleased →
    // CreateTypewriterTextFromPointer → TypewriterTextCreated → VM) was entirely
    // untested — every other test calls OnTypewriterTextCreated directly. This
    // drives a REAL click (MouseDown+MouseUp at the same point, no drag) through
    // the headless pointer pipeline and asserts a box is placed. If this regresses
    // to "a click places nothing" (the headline bug), this test fails.
    [FixedAvaloniaFact]
    public async Task RealClickInTypewriterMode_PlacesABox_NoDragRequired()
    {
        var (sourcePath, _, tempDir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Click to place");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);

        await vm.LoadDocumentAsync(sourcePath);
        await Task.Delay(400);

        await vm.ToggleTypewriterModeCommand.Execute();
        vm.IsTypewriterMode.Should().BeTrue();

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        viewer.Should().NotBeNull();

        // Wait for the single-page PAGE, not just its ScrollViewer: the overlay
        // canvas shares the zoom transform, so a page-space point translates to
        // window coords only once the page Image has sized the ZoomHost. The
        // typewriter toggle switches the default continuous view to single-page,
        // which renders the page then (#1473); a user cannot aim at it earlier.
        await SinglePageViewerWaits.WaitForSinglePageLaidOutAsync(window, viewer!);

        var overlay = viewer!.SinglePagePart.OverlayCanvas;
        overlay.Should().NotBeNull();

        // Centre of the page in viewer-DIP space (render DPI 120), translated to
        // window coords. A plain click here — no drag.
        var page = vm.PdfCoreDocument!.GetPage(1);
        var localCenter = new Point(
            page.VisualWidth * 120.0 / 72.0 / 2.0,
            page.VisualHeight * 120.0 / 72.0 / 2.0);
        var center = overlay!.TranslatePoint(localCenter, window);
        center.Should().NotBeNull("the overlay must be attached so a page point maps to a window point");

        vm.TypewriterTextOperations.Should().BeEmpty("precondition: nothing placed yet");

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(center!.Value, MouseButton.Left);
            window.MouseUp(center!.Value, MouseButton.Left);
        });
        for (var i = 0; i < 3; i++) { await Task.Delay(100); window.UpdateLayout(); }

        vm.TypewriterTextOperations.Should().HaveCount(1,
            "a plain click (no drag) must place a default-sized type-over box");

        window.Close();
        Cleanup(tempDir);
    }

    // #1842 step 8: the drag preview (a dashed rectangle on the interaction layer) is
    // shown while a typewriter box is dragged out and hidden when the drag ends. The
    // capture code that owns it moved within the viewer; no other test looked at it.
    [FixedAvaloniaFact]
    public async Task RealDragInTypewriterMode_ShowsThePreview_AndHidesItOnRelease()
    {
        var (sourcePath, _, tempDir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Drag to size");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);

        await vm.LoadDocumentAsync(sourcePath);
        await Task.Delay(400);
        await vm.ToggleTypewriterModeCommand.Execute();

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
        await SinglePageViewerWaits.WaitForSinglePageLaidOutAsync(window, viewer);

        var page = vm.PdfCoreDocument!.GetPage(1);
        var localCenter = new Point(
            page.VisualWidth * 120.0 / 72.0 / 2.0,
            page.VisualHeight * 120.0 / 72.0 / 2.0);
        var start = viewer.SinglePagePart.OverlayCanvas.TranslatePoint(localCenter, window)!.Value;
        var end = start + new Point(80, 40);

        bool PreviewVisible() => viewer.SinglePagePart.InteractionLayer.Children
            .OfType<global::Avalonia.Controls.Shapes.Rectangle>().Any(r => r.IsVisible);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end);
        });
        window.UpdateLayout();
        PreviewVisible().Should().BeTrue("dragging out a box shows its outline");

        await Dispatcher.UIThread.InvokeAsync(() => window.MouseUp(end, MouseButton.Left));
        for (var i = 0; i < 3; i++) { await Task.Delay(100); window.UpdateLayout(); }

        vm.TypewriterTextOperations.Should().HaveCount(1, "fixture: the drag placed a box");
        PreviewVisible().Should().BeFalse("the outline goes when the box is placed");

        window.Close();
        Cleanup(tempDir);
    }

    // #780/#642: defence in depth — the permission re-check on CREATE, not just
    // on the mode toggle. Uses the GUI load path (LoadDocumentAsync) so the
    // in-memory document is set and OnTypewriterTextCreated's permission gate is
    // the real blocker (contrast: SaveFileAsAsync_Flattens... adds an op on an
    // unrestricted doc via the same call). A restricted fixture denies /P bit 4.
    [FixedAvaloniaFact]
    public async Task OnTypewriterTextCreated_ModifyForbidden_AddsNothing()
    {
        var fixturePath = RestrictedFixturePathOrNull();
        Assert.SkipWhen(fixturePath == null, "Restricted /P bit-4 fixture not available");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        await vm.LoadDocumentAsync(fixturePath!);

        vm.OnTypewriterTextCreated(new PdfRectangle(72, 700, 300, 720), pageNumber: 1);

        vm.TypewriterTextOperations.Should().BeEmpty("the fixture denies /P bit 4 (modify)");
        vm.FileState.TypewriterEditsCount.Should().Be(0);

        window.Close();
    }

    // #781: the style inspector is visible only in typewriter mode AND only
    // when a box is active, so it never floats with nothing to style.
    [FixedAvaloniaFact]
    public async Task StyleInspector_IsVisibleOnlyWhenTypewriterBoxIsActive()
    {
        var (source, _, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Original text");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(source);

        vm.IsTypewriterStyleInspectorVisible.Should().BeFalse("no mode, no box");

        vm.IsTypewriterMode = true;
        vm.IsTypewriterStyleInspectorVisible.Should().BeFalse("in mode but no active box yet");

        vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
        vm.IsTypewriterStyleInspectorVisible.Should().BeTrue("a box is now active");

        vm.IsTypewriterMode = false;
        vm.IsTypewriterStyleInspectorVisible.Should().BeFalse("leaving the mode hides the inspector");

        window.Close();
        Cleanup(dir);
    }

    // #781: changing size/color/alignment on the active box must route through
    // WithStyle onto that box's immutable Style.
    [FixedAvaloniaFact]
    public async Task StyleChanges_UpdateTheActiveBoxStyle()
    {
        var (source, _, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Original text");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(source);

        vm.IsTypewriterMode = true;
        vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
        var id = vm.TypewriterTextOperations.Single().Id;
        vm.OnTypewriterTextEdited(id, "Styled note", 1);

        vm.TypewriterFontSize = 28;
        vm.SetTypewriterColor("#FF0000");
        vm.TypewriterAlignmentIndex = 1; // Center

        var op = vm.TypewriterTextOperations.Single();
        op.Style.FontSize.Should().Be(28);
        op.Style.Color.R.Should().BeApproximately(1.0, 0.001);
        op.Style.Color.G.Should().BeApproximately(0.0, 0.001);
        op.Style.Color.B.Should().BeApproximately(0.0, 0.001);
        op.Style.Alignment.Should().Be(Excise.Core.Graphics.TextAlignment.Center);

        window.Close();
        Cleanup(dir);
    }

    // #781: the chosen style must survive the real GUI save → the flattened
    // page content stream carries the size (Tf) and colour (rg) operators.
    // Byte-level assertion (raw operators), not excise's text interpretation,
    // so it isn't a self-oracle for the style property.
    [FixedAvaloniaFact]
    public async Task StyledTypewriterText_RoundTripsIntoSavedPdf()
    {
        var (source, output, dir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(source, "Original text");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(source);

        vm.IsTypewriterMode = true;
        vm.OnTypewriterTextCreated(new PdfRectangle(72, 620, 300, 660), 1);
        var id = vm.TypewriterTextOperations.Single().Id;
        vm.OnTypewriterTextEdited(id, "STYLED781", 1);
        vm.TypewriterFontSize = 29;      // distinctive, not used by the base PDF
        vm.SetTypewriterColor("#FF0000"); // red -> "1 0 0 rg" (base text is black)
        vm.TypewriterAlignmentIndex = 2;  // Right

        await vm.SaveFileAsAsync(output);

        using var saved = PdfDocument.Open(output);
        var page = saved.GetPage(1);
        var content = System.Text.Encoding.Latin1.GetString(page.GetContentStreamBytes());

        content.Should().Contain("29 Tf", "the flattened text must carry the chosen font size");
        content.Should().Contain("1 0 0 rg", "the flattened text must carry the chosen red colour");
        page.Text.Should().Contain("STYLED781", "the typed text must be present");

        window.Close();
        Cleanup(dir);
    }

    // #1706 — the shared locator, not a hand-rolled walk to excise.sln.
    private static string? RestrictedFixturePathOrNull() =>
        TestRepoLayout.FindFile("test-pdfs", "poppler", "unittestcases", "Gday garçon - owner.pdf");

    private static (string source, string output, string dir) MakePaths()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Excise.AppTypewriterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return (Path.Combine(tempDir, "source.pdf"), Path.Combine(tempDir, "output.pdf"), tempDir);
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { }
    }
}
