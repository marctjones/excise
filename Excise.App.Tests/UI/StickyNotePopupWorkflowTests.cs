using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
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
using Excise.Rendering.Differential;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1788 — the interactive yellow sticky-note popup: click-to-place, and
/// click-to-reopen an existing note for editing, replacing the modal
/// <c>PromptTextAsync</c> dialog for THIS gesture only.
/// <c>AddStickyNoteAnnotationCommand</c>'s existing modal-prompt flow is
/// untouched and stays covered by <c>AnnotateAndDialogCommandTests</c> and
/// <c>AnnotationAuthoringWorkflowTests</c>.
///
/// <para>Every gesture here is a REAL synthetic pointer/keyboard event
/// (<c>MouseDown</c>/<c>MouseUp</c>/<c>KeyTextInput</c>) through the actual
/// headless input pipeline — never a direct <c>Command.Execute()</c> or VM
/// method call — the same pattern
/// <see cref="TypewriterWorkflowTests.RealClickInTypewriterMode_PlacesABox_NoDragRequired"/>
/// and <c>AnnotationHoverReadingTests</c> already use, so these count toward
/// <c>gui-interaction-coverage</c>.</para>
/// </summary>
[Collection("AvaloniaTests")]
public class StickyNotePopupWorkflowTests
{
    [FixedAvaloniaFact]
    public async Task RealClickInStickyNoteMode_PlacesANote_AndOpensThePopup()
    {
        var (sourcePath, _, tempDir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Click to place a note");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);

        await vm.LoadDocumentAsync(sourcePath);
        await Task.Delay(400);

        await vm.ToggleStickyNoteToolCommand.Execute();
        vm.IsStickyNoteToolActive.Should().BeTrue();

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        viewer.Should().NotBeNull();
        await SinglePageViewerWaits.WaitForSinglePageLaidOutAsync(window, viewer!);

        var center = PageCenterInWindow(window, viewer!, vm);
        center.Should().NotBeNull("the overlay must be attached so a page point maps to a window point");

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(center!.Value, MouseButton.Left);
            window.MouseUp(center!.Value, MouseButton.Left);
        });
        for (var i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); }

        var notes = vm.PdfCoreDocument!.GetPage(1).GetAnnotations()
            .Where(a => a.Subtype == PdfAnnotationSubtype.Text).ToList();
        notes.Should().HaveCount(1,
            "a plain click (no drag) in sticky-note mode must place exactly one note, " +
            "like the real toolbar/menu gesture would");

        vm.StickyNotePopup.Should().NotBeNull(
            "placing a note must open its popup immediately for typing, not just drop the icon");
        vm.StickyNotePopup!.Text.Should().Be(MainWindowViewModel.DefaultStickyNoteText);

        window.Close();
        Cleanup(tempDir);
    }

    [FixedAvaloniaFact]
    public async Task TypingInTheOpenPopup_AndClickingAway_CommitsTheText()
    {
        var (sourcePath, _, tempDir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Click to place a note");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);

        await vm.LoadDocumentAsync(sourcePath);
        await Task.Delay(400);

        await vm.ToggleStickyNoteToolCommand.Execute();
        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        await SinglePageViewerWaits.WaitForSinglePageLaidOutAsync(window, viewer!);

        var center = PageCenterInWindow(window, viewer!, vm)!.Value;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
        });
        for (var i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); }
        vm.StickyNotePopup.Should().NotBeNull("precondition: the popup must be open before typing into it");

        // The popup's own code-behind focuses and select-alls its TextBox on
        // open (deferred via Dispatcher.Post) — give it a beat, then type a
        // REAL text-input event. KeyTextInput replaces the selection.
        for (var i = 0; i < 5; i++) { await Task.Delay(50); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        window.KeyTextInput("Please verify the totals on page 3");
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.StickyNotePopup!.Text.Should().Be("Please verify the totals on page 3");

        // Click away: a point on the document area far from the popup and the
        // note's icon. A REAL press, driving MainWindow's light-dismiss tunnel
        // handler exactly as a user's next click would.
        var away = AwayFromCenter(window, viewer!);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(away, MouseButton.Left);
            window.MouseUp(away, MouseButton.Left);
        });
        for (var i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); }

        vm.StickyNotePopup.Should().BeNull("clicking away must collapse the popup back to just the icon");

        var note = vm.PdfCoreDocument!.GetPage(1).GetAnnotations()
            .Single(a => a.Subtype == PdfAnnotationSubtype.Text);
        note.Contents.Should().Be("Please verify the totals on page 3",
            "the typed text must be committed to the actual annotation on click-away");
        note.IsOpen.Should().BeFalse("collapsing the popup must clear /Open");

        window.Close();
        Cleanup(tempDir);
    }

    [FixedAvaloniaFact]
    public async Task ClickingAnExistingNotesIcon_ReopensThePopup_PrefilledWithItsCurrentText()
    {
        var (sourcePath, _, tempDir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Existing note fixture");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);

        await vm.LoadDocumentAsync(sourcePath);
        await Task.Delay(400);

        // Create the note through the EXISTING, frozen, modal-prompt entry
        // point — this test's own subject is reopening a note that already
        // exists, not how it got there.
        await vm.AddStickyNoteAnnotationCommand.Execute();
        vm.PdfCoreDocument!.GetPage(1).GetAnnotations()
            .Count(a => a.Subtype == PdfAnnotationSubtype.Text).Should().Be(1, "precondition");

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        vm.ViewMode = PdfViewMode.SinglePage;
        await SinglePageViewerWaits.WaitForSinglePageLaidOutAsync(window, viewer!);

        var noteAnnotation = vm.PdfCoreDocument!.GetPage(1).GetAnnotations()
            .Single(a => a.Subtype == PdfAnnotationSubtype.Text);
        var iconCenterPdf = new PdfPoint(
            noteAnnotation.Rect.Left + PdfAnnotation.TextIconSize / 2,
            noteAnnotation.Rect.Top - PdfAnnotation.TextIconSize / 2);
        var iconWindowPoint = PdfPointToWindow(window, viewer!, vm, iconCenterPdf);
        iconWindowPoint.Should().NotBeNull();

        vm.StickyNotePopup.Should().BeNull("precondition: nothing open yet");

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(iconWindowPoint!.Value, MouseButton.Left);
            window.MouseUp(iconWindowPoint!.Value, MouseButton.Left);
        });
        for (var i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); }

        vm.StickyNotePopup.Should().NotBeNull(
            "a real click on an EXISTING note's icon must reopen its popup — ambient, " +
            "regardless of which tool is currently active");
        vm.StickyNotePopup!.Text.Should().Be(MainWindowViewModel.DefaultStickyNoteText,
            "the reopened popup must be pre-filled with the note's CURRENT text");

        window.Close();
        Cleanup(tempDir);
    }

    [FixedAvaloniaFact]
    public async Task PlaceEditAndSave_SurvivesReload_WithPopupAndOpenState()
    {
        // #1788's end-to-end promise: a note placed and edited through the
        // REAL click/typing gestures, then saved, must round-trip through an
        // independent PdfDocument.Open — /Popup, /Open and the final /Contents
        // all present, not just what excise's own live objects report.
        var (sourcePath, outputPath, tempDir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Round trip fixture");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);

        await vm.LoadDocumentAsync(sourcePath);
        await Task.Delay(400);

        await vm.ToggleStickyNoteToolCommand.Execute();
        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        await SinglePageViewerWaits.WaitForSinglePageLaidOutAsync(window, viewer!);

        var center = PageCenterInWindow(window, viewer!, vm)!.Value;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
        });
        for (var i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }

        window.KeyTextInput("Final wording for the note");
        await KeyboardTestHelpers.FlushDispatcherAsync();

        // Save WITHOUT clicking away first — proving the pre-save flush (#1788:
        // "a note left open persists as open") carries the still-open popup's
        // latest text into the saved file.
        vm.StickyNotePopup.Should().NotBeNull("precondition: still open when Save runs");
        await vm.SaveFileAsAsync(outputPath);

        using var reopened = PdfDocument.Open(File.ReadAllBytes(outputPath));
        var annotations = reopened.GetPage(1).GetAnnotations();
        var note = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Text);
        var popup = annotations.Single(a => a.Subtype == PdfAnnotationSubtype.Popup);

        note.Contents.Should().Be("Final wording for the note");
        note.IsOpen.Should().BeTrue("the note was left open at save time and must persist as open");
        popup.IsOpen.Should().BeTrue();

        var parentRef = popup.RawDictionary.GetOptional("Parent");
        (reopened.Resolve(parentRef!) as Excise.Core.Primitives.PdfDictionary)
            .Should().BeSameAs(note.RawDictionary,
                "/Parent must resolve back to the SAME reloaded /Text dictionary");

        window.Close();
        Cleanup(tempDir);
    }

    [FixedAvaloniaFact]
    public async Task PlaceAndSave_QpdfIndependentlySeesBothTheNoteAndItsPopup()
    {
        // PlaceEditAndSave_SurvivesReload_WithPopupAndOpenState above asks
        // excise's OWN PdfDocument.Open whether the /Parent<->/Popup link
        // round-tripped — proof the writer and reader agree, not proof about
        // the file (CLAUDE.md's no-self-oracle rule, same one #933 applies to
        // pixels). qpdf parses the saved bytes independently and has never
        // heard of PdfAnnotationAuthoring.
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed [requires: tool:qpdf]");

        var (sourcePath, outputPath, tempDir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Qpdf independent check fixture");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);

        await vm.LoadDocumentAsync(sourcePath);
        await Task.Delay(400);

        await vm.ToggleStickyNoteToolCommand.Execute();
        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        await SinglePageViewerWaits.WaitForSinglePageLaidOutAsync(window, viewer!);
        var center = PageCenterInWindow(window, viewer!, vm)!.Value;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
        });
        for (var i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }

        window.KeyTextInput("Seen independently by qpdf");
        await KeyboardTestHelpers.FlushDispatcherAsync();
        vm.CommitOpenStickyNotePopup();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        await vm.SaveFileAsAsync(outputPath);
        window.Close();

        var check = QpdfReferenceTool.Check(outputPath);
        check.Should().NotBeNull();
        check!.Value.Success.Should().BeTrue($"qpdf must consider the saved file structurally sound:\n{check.Value.Output}");

        var annotations = QpdfReferenceTool.ListAnnotations(outputPath);
        annotations.Should().NotBeNull("qpdf must be able to enumerate the saved file's annotations");
        annotations!.Should().Contain(a => a.Subtype == "Text" && a.Contents == "Seen independently by qpdf",
            "qpdf's own parser, not excise's, must find the note text in a /Text annotation");
        annotations.Should().Contain(a => a.Subtype == "Popup",
            "qpdf's own parser must find the linked /Popup annotation #1788 authors alongside /Text");

        Cleanup(tempDir);
    }

    [FixedAvaloniaFact]
    public async Task RealMenuClick_EntersStickyNoteMode_AndARealClickOnDone_CommitsTheNote()
    {
        // The two coverage gaps left after #1789's sweep: the "Place Sticky
        // Note (click page)" menu item is entered via vm.ToggleStickyNoteToolCommand
        // .Execute() everywhere else in this file (that's fine for the OTHER
        // tests, which are really about the popup, not the menu), and the
        // popup's "Done" button (Excise.App/Views/StickyNotePopupView.axaml)
        // had no test clicking it directly — click-away dismissal exercises
        // MainWindowViewModel.CommitOpenStickyNotePopup instead. Both real
        // gestures, end to end, in one workflow.
        var (sourcePath, outputPath, tempDir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Click to place a note");

        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);

        await vm.LoadDocumentAsync(sourcePath);
        await Task.Delay(400);

        // "Place Sticky Note (click page)" has no x:Name (MainWindow.axaml:309)
        // — found by its Command binding, same as the coverage inventory does
        // when a static XAML scan cannot otherwise name it.
        var menuItem = window.GetLogicalDescendants().OfType<MenuItem>()
            .FirstOrDefault(m => m.Command == vm.ToggleStickyNoteToolCommand);
        menuItem.Should().NotBeNull("MainWindow.axaml must still bind a menu item to ToggleStickyNoteToolCommand");

        RaisePointerPressRelease(menuItem!, window);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        // As ViewToggleMenuInteractionTests notes: a pointer press/release on a
        // CLOSED menu's item does not itself run the command — the explicit
        // Click event is what a real open-menu selection ultimately raises.
        menuItem!.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.IsStickyNoteToolActive.Should().BeTrue(
            "a real click on \"Place Sticky Note (click page)\" must enter sticky-note mode");

        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
        await SinglePageViewerWaits.WaitForSinglePageLaidOutAsync(window, viewer!);
        var center = PageCenterInWindow(window, viewer!, vm)!.Value;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
        });
        for (var i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        vm.StickyNotePopup.Should().NotBeNull("precondition: the popup must be open before typing into it");

        window.KeyTextInput("Committed via the Done button");
        await KeyboardTestHelpers.FlushDispatcherAsync();

        window.UpdateLayout();
        var doneButton = window.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Content as string == "Done");
        doneButton.Should().NotBeNull("StickyNotePopupView.axaml must declare the Done button");

        var doneCenter = doneButton!.TranslatePoint(
            new Point(doneButton.Bounds.Width / 2, doneButton.Bounds.Height / 2), window) ?? default;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(doneCenter, MouseButton.Left);
            window.MouseUp(doneCenter, MouseButton.Left);
        });
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.StickyNotePopup.Should().BeNull("a real click on Done must collapse the popup, same as click-away");

        await vm.SaveFileAsAsync(outputPath);
        using var reopened = PdfDocument.Open(File.ReadAllBytes(outputPath));
        reopened.GetPage(1).GetAnnotations()
            .Single(a => a.Subtype == PdfAnnotationSubtype.Text).Contents
            .Should().Be("Committed via the Done button");

        window.Close();
        Cleanup(tempDir);
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

    // ── shared geometry helpers ──────────────────────────────────────────────

    private static Point? PageCenterInWindow(
        Window window, PdfViewerControl viewer, MainWindowViewModel vm)
    {
        var overlay = viewer.FindControl<Canvas>("OverlayCanvas");
        if (overlay == null) return null;
        var page = vm.PdfCoreDocument!.GetPage(1);
        var localCenter = new Point(
            page.VisualWidth * 120.0 / 72.0 / 2.0,
            page.VisualHeight * 120.0 / 72.0 / 2.0);
        return overlay.TranslatePoint(localCenter, window);
    }

    /// <summary>
    /// A point guaranteed to land inside <c>DocumentArea</c> itself — not
    /// derived from the overlay canvas, so it stays valid even when the
    /// note/popup sit somewhere the overlay's own geometry would overlap.
    /// Near DocumentArea's bottom-left corner, away from a page-centre note.
    /// </summary>
    private static Point AwayFromCenter(Window window, PdfViewerControl viewer)
    {
        var documentArea = (Control)window.FindControl<Control>("DocumentArea")!;
        var b = documentArea.Bounds;
        var local = new Point(Math.Min(10, b.Width - 1), Math.Max(0, b.Height - 10));
        return documentArea.TranslatePoint(local, window) ?? local;
    }

    private static Point? PdfPointToWindow(
        Window window, PdfViewerControl viewer, MainWindowViewModel vm, PdfPoint pdfPoint)
    {
        var overlay = viewer.FindControl<Canvas>("OverlayCanvas");
        if (overlay == null) return null;
        var page = vm.PdfCoreDocument!.GetPage(1);
        const double dpi = 120.0;
        var localX = pdfPoint.X * dpi / 72.0;
        // PDF is bottom-left, the overlay canvas is top-left (CLAUDE.md's
        // pdfY/screenY rule) — flip against the page's own height in DIPs.
        var localY = (page.MediaBox.Normalize().Top - pdfPoint.Y) * dpi / 72.0;
        return overlay.TranslatePoint(new Point(localX, localY), window);
    }

    private static (string source, string output, string dir) MakePaths()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Excise.AppStickyNoteTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return (Path.Combine(tempDir, "source.pdf"), Path.Combine(tempDir, "output.pdf"), tempDir);
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { }
    }
}
