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
/// #1788 — the interactive sticky-note card: click-to-place, and
/// click-to-reopen an existing note for editing, replacing the modal
/// <c>PromptTextAsync</c> dialog for THIS gesture only.
/// <c>AddStickyNoteAnnotationCommand</c>'s existing modal-prompt flow is
/// untouched and stays covered by <c>AnnotateAndDialogCommandTests</c> and
/// <c>AnnotationAuthoringWorkflowTests</c>.
///
/// <para>#1794 reworked the card itself into a real post-it-sized visual
/// (rendered by <c>SkiaRenderer.RenderStickyNoteDefault</c> at rest, and by
/// <c>StickyNotePopupView</c> — the SAME fill/border/size/position, no
/// separate dialog, no "Done" button — while editing), and added
/// click-vs-drag: a plain click/release on a RESTING note enters edit in
/// place; a press-and-drag past a small threshold moves it instead. Escape
/// now commits too, alongside the existing click-away.</para>
///
/// <para>Every gesture here is a REAL synthetic pointer/keyboard event
/// (<c>MouseDown</c>/<c>MouseMove</c>/<c>MouseUp</c>/<c>KeyTextInput</c>/
/// <c>PressEscapeAsync</c>) through the actual headless input pipeline —
/// never a direct <c>Command.Execute()</c> or VM method call — the same
/// pattern
/// <see cref="TypewriterWorkflowTests.RealClickInTypewriterMode_PlacesABox_NoDragRequired"/>
/// and <c>AnnotationHoverReadingTests</c> already use, so these count toward
/// <c>gui-interaction-coverage</c>.</para>
/// </summary>
[Collection("AvaloniaTests")]
public class StickyNotePopupWorkflowTests
{
    [FixedAvaloniaFact]
    public async Task RealClickInStickyNoteMode_PlacesAPostItCard_SizedToTheDefault_AndOpensItInEditMode()
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

        // #1797: the note's own /Rect is its TRUE ANCHOR — icon-sized, per
        // §12.5.6.4 — and never moves again after this. The real post-it-sized
        // card lives on the linked /Popup's independent /Rect instead
        // (§12.5.6.14), so dragging the card can never look like it silently
        // relocated the note.
        var rect = notes[0].Rect;
        (rect.Right - rect.Left).Should().BeApproximately(PdfAnnotation.TextIconSize, 0.01,
            "the note's own /Rect is its anchor — icon-sized, not the card size");
        (rect.Top - rect.Bottom).Should().BeApproximately(PdfAnnotation.TextIconSize, 0.01);

        var popupRect = notes[0].PopupRect;
        popupRect.Should().NotBeNull("a freshly-placed note must have a linked /Popup with its own /Rect");
        (popupRect!.Value.Right - popupRect.Value.Left).Should().BeApproximately(
            MainWindowViewModel.DefaultStickyNoteCardWidth, 0.01,
            "the CARD (the popup's /Rect) must default to the post-it size, not the icon size");
        (popupRect.Value.Top - popupRect.Value.Bottom).Should().BeApproximately(
            MainWindowViewModel.DefaultStickyNoteCardHeight, 0.01);
        popupRect.Value.Left.Should().BeApproximately(rect.Left, 0.01,
            "the card must start exactly where the note was placed, same top-left as the anchor");
        popupRect.Value.Top.Should().BeApproximately(rect.Top, 0.01);

        vm.StickyNotePopup.Should().NotBeNull(
            "placing a note must open it in edit mode immediately for typing, not just drop a resting card");
        vm.StickyNotePopup!.Text.Should().Be(MainWindowViewModel.DefaultStickyNoteText);
        vm.StickyNotePopup!.CardWidthDips.Should().BeGreaterThan(0,
            "the editing overlay must be sized from the note's actual rect (#1794) — a zero/default " +
            "size would mean RepositionStickyNotePopup never ran");
        vm.StickyNotePopup!.CardHeightDips.Should().BeGreaterThan(0);

        window.Close();
        Cleanup(tempDir);
    }

    [FixedAvaloniaFact]
    public async Task TypingInTheOpenCard_AndClickingAway_CommitsTheText()
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
        vm.StickyNotePopup.Should().NotBeNull("precondition: the card must be open before typing into it");

        // The card's own code-behind focuses and select-alls its TextBox on
        // open (deferred via Dispatcher.Post) — give it a beat, then type a
        // REAL text-input event. KeyTextInput replaces the selection.
        for (var i = 0; i < 5; i++) { await Task.Delay(50); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        window.KeyTextInput("Please verify the totals on page 3");
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.StickyNotePopup!.Text.Should().Be("Please verify the totals on page 3");

        // Click away: a point on the document area far from the card. A REAL
        // press, driving MainWindow's light-dismiss tunnel handler exactly as
        // a user's next click would.
        var away = AwayFromCenter(window, viewer!);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(away, MouseButton.Left);
            window.MouseUp(away, MouseButton.Left);
        });
        for (var i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); }

        vm.StickyNotePopup.Should().BeNull("clicking away must collapse the card back to its resting rendering");

        var note = vm.PdfCoreDocument!.GetPage(1).GetAnnotations()
            .Single(a => a.Subtype == PdfAnnotationSubtype.Text);
        note.Contents.Should().Be("Please verify the totals on page 3",
            "the typed text must be committed to the actual annotation on click-away");
        note.IsOpen.Should().BeFalse("collapsing the card must clear /Open");

        window.Close();
        Cleanup(tempDir);
    }

    [FixedAvaloniaFact]
    public async Task EscapeCommitsTheOpenCard_SameAsClickingAway()
    {
        // #1794: Escape replaces the old "Done" button as the keyboard-only
        // out — same commit path (StickyNotePopupViewModel.CommitCommand),
        // different trigger.
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
        for (var i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        vm.StickyNotePopup.Should().NotBeNull("precondition: the card must be open before typing into it");

        window.KeyTextInput("Committed via Escape");
        await KeyboardTestHelpers.FlushDispatcherAsync();

        await window.PressEscapeAsync();
        for (var i = 0; i < 5; i++) { await Task.Delay(50); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }

        vm.StickyNotePopup.Should().BeNull("Escape must commit and collapse the card, same as clicking away");

        var note = vm.PdfCoreDocument!.GetPage(1).GetAnnotations()
            .Single(a => a.Subtype == PdfAnnotationSubtype.Text);
        note.Contents.Should().Be("Committed via Escape");

        window.Close();
        Cleanup(tempDir);
    }

    [FixedAvaloniaFact]
    public async Task ClickingAnExistingNotesCard_ReopensItInEditMode_PrefilledWithItsCurrentText()
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
        // A point well inside the card's top-left corner — valid regardless
        // of the card's actual size (icon-sized pre-#1794, post-it-sized
        // after), as long as it is at least PdfAnnotation.TextIconSize on a
        // side, which #1794's default card comfortably is.
        var pointInsideCardPdf = new PdfPoint(
            noteAnnotation.Rect.Left + PdfAnnotation.TextIconSize / 2,
            noteAnnotation.Rect.Top - PdfAnnotation.TextIconSize / 2);
        var windowPoint = PdfPointToWindow(window, viewer!, vm, pointInsideCardPdf);
        windowPoint.Should().NotBeNull();

        vm.StickyNotePopup.Should().BeNull("precondition: nothing open yet");

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(windowPoint!.Value, MouseButton.Left);
            window.MouseUp(windowPoint!.Value, MouseButton.Left);
        });
        for (var i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); }

        vm.StickyNotePopup.Should().NotBeNull(
            "a real plain click (no drag) on an EXISTING note's card must reopen it for " +
            "editing in place — ambient, regardless of which tool is currently active");
        vm.StickyNotePopup!.Text.Should().Be(MainWindowViewModel.DefaultStickyNoteText,
            "the reopened card must be pre-filled with the note's CURRENT text");
        vm.StickyNotePopup!.Rect.Should().Be(noteAnnotation.Rect,
            "editing in place means the SAME rect — no visual jump to a different position/size");

        window.Close();
        Cleanup(tempDir);
    }

    [FixedAvaloniaFact]
    public async Task ShortPressRelease_OnARestingNote_EntersEditInPlace_RectUnchanged()
    {
        // #1794's click-vs-drag disambiguation: a press-and-release with no
        // meaningful movement is a CLICK (edit in place), not a drag.
        var (sourcePath, _, tempDir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Short click fixture");

        var (vm, window, viewer, originalRect) = await PlaceCardThenReturnToRestAsync(sourcePath);

        var restingCenter = PdfPointToWindow(
            window, viewer, vm,
            new PdfPoint((originalRect.Left + originalRect.Right) / 2, (originalRect.Top + originalRect.Bottom) / 2))!.Value;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(restingCenter, MouseButton.Left);
            window.MouseUp(restingCenter, MouseButton.Left);
        });
        for (var i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); }

        vm.StickyNotePopup.Should().NotBeNull(
            "a press/release with no movement on a resting note must enter edit mode, not move it");
        // #1797: originalRect (from PlaceCardThenReturnToRestAsync) is the
        // CARD's rect (the linked /Popup's /Rect) — a click reopens the
        // editor exactly where the resting card was, and must never touch
        // the note's own /Rect (its true anchor) either.
        vm.StickyNotePopup!.DisplayRect.Should().Be(originalRect,
            "a click must reopen the card exactly where it was resting, no visual jump");

        var note = vm.PdfCoreDocument!.GetPage(1).GetAnnotations()
            .Single(a => a.Subtype == PdfAnnotationSubtype.Text);
        note.PopupRect.Should().Be(originalRect, "a click must never change the card's /Rect");
        note.Rect.Should().Be(vm.StickyNotePopup!.Rect, "a click must never change the note's own /Rect (its anchor)");

        window.Close();
        Cleanup(tempDir);
    }

    [FixedAvaloniaFact]
    public async Task RealDragPastTheThreshold_OnARestingNote_MovesItsCard_ButNeverTheNotesOwnAnchor_QpdfIndependentlySeesBoth()
    {
        // CLAUDE.md's no-self-oracle rule: prove the moved /Rect landed in
        // the SAVED BYTES via qpdf's own independent parse, not just excise's
        // in-memory PdfCoreDocument — the same shape
        // PlaceAndSave_QpdfIndependentlySeesBothTheNoteAndItsPopup already
        // uses for note+popup existence.
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed [requires: tool:qpdf]");

        var (sourcePath, outputPath, tempDir) = MakePaths();
        TestPdfGenerator.CreateSimpleTextPdf(sourcePath, "Drag fixture");

        // #1797: originalRect is the CARD's rect (linked /Popup's /Rect) —
        // what a drag actually targets and repositions.
        var (vm, window, viewer, originalRect) = await PlaceCardThenReturnToRestAsync(sourcePath);
        var originalIconRect = vm.PdfCoreDocument!.GetPage(1).GetAnnotations()
            .Single(a => a.Subtype == PdfAnnotationSubtype.Text).Rect;

        var dragStart = PdfPointToWindow(
            window, viewer, vm,
            new PdfPoint((originalRect.Left + originalRect.Right) / 2, (originalRect.Top + originalRect.Bottom) / 2))!.Value;
        // Comfortably past PdfViewerControl's 5-DIP click/drag threshold in
        // both axes.
        var dragEnd = new Point(dragStart.X + 60, dragStart.Y + 40);

        await Dispatcher.UIThread.InvokeAsync(() => window.MouseDown(dragStart, MouseButton.Left));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseMove(dragEnd));
        await Task.Delay(50);
        await Dispatcher.UIThread.InvokeAsync(() => window.MouseUp(dragEnd, MouseButton.Left));
        for (var i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); }

        vm.StickyNotePopup.Should().BeNull("a drag must move the card, not enter edit mode");

        var note = vm.PdfCoreDocument!.GetPage(1).GetAnnotations()
            .Single(a => a.Subtype == PdfAnnotationSubtype.Text);
        note.Rect.Should().Be(originalIconRect,
            "dragging the card must NEVER move the note's own /Rect — its true anchor location (§12.5.6.14)");

        var movedRect = note.PopupRect!.Value;
        movedRect.Should().NotBe(originalRect, "a real drag past the threshold must move the CARD's /Rect");
        // Screen right+down maps to PDF right (+X) and down (-Y, bottom-left
        // origin) — CLAUDE.md's pdfY/screenY rule.
        movedRect.Left.Should().BeGreaterThan(originalRect.Left, "dragging right on screen must move the card right in PDF space");
        movedRect.Top.Should().BeLessThan(originalRect.Top, "dragging down on screen must lower the card's pdfY");
        (movedRect.Right - movedRect.Left).Should().BeApproximately(originalRect.Right - originalRect.Left, 0.5,
            "a move translates the rect — it must not resize the card");
        (movedRect.Top - movedRect.Bottom).Should().BeApproximately(originalRect.Top - originalRect.Bottom, 0.5);

        await vm.SaveFileAsAsync(outputPath);
        window.Close();

        var annotations = QpdfReferenceTool.ListAnnotations(outputPath);
        annotations.Should().NotBeNull("qpdf must be able to enumerate the saved file's annotations");
        var savedNote = annotations!.Single(a => a.Subtype == "Text");
        Math.Abs(savedNote.Left - originalIconRect.Left).Should().BeLessThan(0.5,
            "qpdf's own independent parse of the saved bytes must see the note's /Rect UNCHANGED — " +
            "proof a drag never touches the anchor, not just excise's own in-memory model");
        Math.Abs(savedNote.Top - originalIconRect.Top).Should().BeLessThan(0.5);

        var savedPopup = annotations!.Single(a => a.Subtype == "Popup");
        Math.Abs(savedPopup.Left - movedRect.Left).Should().BeLessThan(0.5,
            "qpdf's own independent parse of the saved bytes must see the MOVED /Popup /Rect — " +
            "proof the card's position update reached the file");
        Math.Abs(savedPopup.Top - movedRect.Top).Should().BeLessThan(0.5);

        Cleanup(tempDir);
    }

    [FixedAvaloniaFact]
    public async Task RealMenuClick_EntersStickyNoteMode_AndEscapeCommitsTheNote()
    {
        // The coverage gap #1789's sweep left: the "Place Sticky Note (click
        // page)" menu item is entered via vm.ToggleStickyNoteToolCommand
        // .Execute() everywhere else in this file (fine for the OTHER tests,
        // which are really about the card, not the menu). #1794 removed the
        // old "Done" button this test used to click; Escape is its
        // keyboard-driven replacement, exercised end to end here.
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
        vm.StickyNotePopup.Should().NotBeNull("precondition: the card must be open before typing into it");

        window.KeyTextInput("Committed via Escape from the menu flow");
        await KeyboardTestHelpers.FlushDispatcherAsync();

        await window.PressEscapeAsync();
        for (var i = 0; i < 5; i++) { await Task.Delay(50); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }

        vm.StickyNotePopup.Should().BeNull("Escape must collapse the card, same as click-away");

        await vm.SaveFileAsAsync(outputPath);
        using var reopened = PdfDocument.Open(File.ReadAllBytes(outputPath));
        reopened.GetPage(1).GetAnnotations()
            .Single(a => a.Subtype == PdfAnnotationSubtype.Text).Contents
            .Should().Be("Committed via Escape from the menu flow");

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
        // "a note left open persists as open") carries the still-open card's
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

    // ── shared workflow helper ───────────────────────────────────────────────

    /// <summary>
    /// Click-to-place a note, type nothing, and click away — leaving a
    /// RESTING note (no card open) at its default placed rect. Drag-to-move
    /// (#1794) only applies to a resting note; several tests below start from
    /// exactly this state.
    /// </summary>
    private static async Task<(MainWindowViewModel Vm, MainWindow Window, PdfViewerControl Viewer, PdfRectangle Rect)>
        PlaceCardThenReturnToRestAsync(string sourcePath)
    {
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await Task.Delay(200);

        await vm.LoadDocumentAsync(sourcePath);
        await Task.Delay(400);

        await vm.ToggleStickyNoteToolCommand.Execute();
        var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl")!;
        await SinglePageViewerWaits.WaitForSinglePageLaidOutAsync(window, viewer);

        var center = PageCenterInWindow(window, viewer, vm)!.Value;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
        });
        for (var i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        vm.StickyNotePopup.Should().NotBeNull("precondition: placing opens the card in edit mode");

        var away = AwayFromCenter(window, viewer);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.MouseDown(away, MouseButton.Left);
            window.MouseUp(away, MouseButton.Left);
        });
        for (var i = 0; i < 5; i++) { await Task.Delay(100); window.UpdateLayout(); }
        vm.StickyNotePopup.Should().BeNull("precondition: back to resting before the test's own gesture");

        // #1797: callers use this rect to compute where to click/drag the
        // RESTING CARD on screen — that's the linked /Popup's own /Rect now,
        // not the note's icon-sized anchor (its own /Rect).
        var note = vm.PdfCoreDocument!.GetPage(1).GetAnnotations()
            .Single(a => a.Subtype == PdfAnnotationSubtype.Text);
        var cardRect = note.PopupRect
            ?? throw new InvalidOperationException("precondition: a freshly-placed note must have a linked /Popup");

        return (vm, window, viewer, cardRect);
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
    /// note/card sit somewhere the overlay's own geometry would overlap.
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
