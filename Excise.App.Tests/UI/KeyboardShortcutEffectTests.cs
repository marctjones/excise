using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.Avalonia.Automation;
using Excise.Avalonia.Controls;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;
using PdfCoreDocument = Excise.Core.Document.PdfDocument;

namespace Excise.App.Tests.UI;

/// <summary>
/// Keyboard-shortcut coverage upgrade for #827 (batch B): every test here
/// DISPATCHES THE REAL KEY (raw headless input for window-level shortcuts,
/// routed <see cref="InputElement.KeyDownEvent"/> for the viewer/search/form
/// controls) and asserts the RESULTING EFFECT — a loaded document, a written
/// file, an advanced search index, a rotated page, a toggled sidebar, an opened
/// dialog — never merely that a bound command is non-null.
///
/// The bar the old <c>KeyboardShortcutTests</c> set (<c>Command.Should()
/// .NotBeNull()</c>) passes even when the KEY isn't wired to the command at all.
/// It was: dispatching Ctrl+E / Ctrl+, / Enter did nothing, because those three
/// were advertised only as menu <c>InputGesture</c> text (display-only in
/// Avalonia — every WORKING shortcut is explicitly duplicated in
/// <c>MainWindow_KeyDown</c>) with no key handler behind them. Those three tests
/// were written RED against the unwired code, then the handlers were added.
/// </summary>
[Collection("AvaloniaTests")]
public class KeyboardShortcutEffectTests
{
    private readonly string _tempDir;

    public KeyboardShortcutEffectTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ExciseKbdEffectTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private string Temp(string name) => Path.Combine(_tempDir, name);

    private static MainWindowViewModel NewVmWithDialog(IUserDialogService dialog)
    {
        var loggerFactory = NullLoggerFactory.Instance;
        return new MainWindowViewModel(
            NullLogger<MainWindowViewModel>.Instance,
            loggerFactory,
            new PdfDocumentService(NullLogger<PdfDocumentService>.Instance),
            new PdfRenderService(NullLogger<PdfRenderService>.Instance),
            new RedactionService(NullLogger<RedactionService>.Instance, loggerFactory),
            new PdfTextExtractionService(NullLogger<PdfTextExtractionService>.Instance),
            new PdfSearchService(NullLogger<PdfSearchService>.Instance),
            new SignatureVerificationService(NullLogger<SignatureVerificationService>.Instance),
            new FilenameSuggestionService(),
            new ToastService(),
            dialogService: dialog);
    }

    private sealed class RecordingDialogService : IUserDialogService
    {
        public System.Collections.Generic.List<(string Title, string Message)> Messages { get; } = new();
        public Task ShowMessageAsync(string title, string message)
        {
            Messages.Add((title, message));
            return Task.CompletedTask;
        }
    }

    private static async Task WaitForMatches(MainWindowViewModel vm, int timeoutMs = 20000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (vm.SearchMatches.Count > 0)
                return;
            await Task.Delay(100);
        }
    }

    // ==============================================================
    // File operations — real side effects through the picker seams
    // ==============================================================

    // Ctrl+O / Ctrl+S / Ctrl+Shift+S all end at Avalonia's file dialog
    // (OpenFilePickerAsync / SaveFilePickerAsync) reached through a
    // GetStorageProvider() that returns null in the headless host — and
    // IStorageProvider is sealed against faking, with no path-override seam on
    // THESE commands (the Pick*Override seams cover only the page-organization
    // pickers, not Open/Save/SaveAs). So the honest, robust, and
    // strictly-stronger-than-non-null effect is that the KEY actually EXECUTES
    // the bound command (a ReactiveCommand emits Unit on completion). These
    // three are the shallowest effects in the file; flagged in the #827 report.

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task CtrlO_ExecutesOpenFileCommand()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();

        var executed = false;
        using var sub = vm.OpenFileCommand!.Subscribe(_ => executed = true);

        await window.PressKeyAsync(Key.O, RawInputModifiers.Control);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        for (int i = 0; i < 20 && !executed; i++)
            await Task.Delay(50);

        executed.Should().BeTrue("Ctrl+O must route to and execute OpenFileCommand (the open dialog)");

        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task CtrlS_ExecutesSaveFileCommand()
    {
        var path = Temp("save_effect.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        // Bound the load — this command can reach a save/success-toast path
        // whose dispatcher activity has historically starved headless CI (#363);
        // Task.Delay uses the thread-pool timer, not the dispatcher.
        await Task.WhenAny(vm.LoadDocumentAsync(path), Task.Delay(TimeSpan.FromSeconds(10)));

        var executed = false;
        using var sub = vm.SaveFileCommand!.Subscribe(_ => executed = true);

        await window.PressKeyAsync(Key.S, RawInputModifiers.Control);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        for (int i = 0; i < 20 && !executed; i++)
            await Task.Delay(50);

        executed.Should().BeTrue("Ctrl+S must route to and execute SaveFileCommand");

        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task CtrlShiftS_ExecutesSaveAsCommand()
    {
        var source = Temp("saveas_source.pdf");
        TestPdfGenerator.CreateMultiPagePdf(source, pageCount: 2);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(source);

        var executed = false;
        using var sub = vm.SaveAsCommand!.Subscribe(_ => executed = true);

        await window.PressKeyAsync(Key.S, RawInputModifiers.Control | RawInputModifiers.Shift);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        for (int i = 0; i < 20 && !executed; i++)
            await Task.Delay(50);

        executed.Should().BeTrue("Ctrl+Shift+S must route to and execute SaveAsCommand (distinct from plain Ctrl+S)");

        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task CtrlW_ClosesLoadedDocument()
    {
        var path = Temp("close_effect.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(path);
        vm.IsDocumentLoaded.Should().BeTrue("document loaded before Ctrl+W");

        await window.PressKeyAsync(Key.W, RawInputModifiers.Control);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        for (int i = 0; i < 40 && vm.IsDocumentLoaded; i++)
            await Task.Delay(50);

        vm.IsDocumentLoaded.Should().BeFalse("Ctrl+W must route to CloseDocumentCommand and unload the document");

        window.Close();
    }

    // ==============================================================
    // Search — index navigation and the in-box Enter/Escape handler
    // ==============================================================

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task F3_AdvancesCurrentSearchMatchIndex()
    {
        var path = Temp("f3_effect.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(path);

        vm.SearchText = "Content";                 // "Page N Content" — one per page
        await vm.FindCommand!.Execute();
        await WaitForMatches(vm);
        vm.SearchMatches.Count.Should().BeGreaterThanOrEqualTo(2, "need >1 match to prove F3 advances");
        vm.CurrentSearchMatchIndex.Should().Be(0);

        await window.PressKeyAsync(Key.F3);
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.CurrentSearchMatchIndex.Should().Be(1, "F3 must advance to the next match");

        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task ShiftF3_WrapsCurrentSearchMatchIndexToLast()
    {
        var path = Temp("shiftf3_effect.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(path);

        vm.SearchText = "Content";
        await vm.FindCommand!.Execute();
        await WaitForMatches(vm);
        var total = vm.SearchMatches.Count;
        total.Should().BeGreaterThanOrEqualTo(2);
        vm.CurrentSearchMatchIndex.Should().Be(0);

        await window.PressKeyAsync(Key.F3, RawInputModifiers.Shift);
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.CurrentSearchMatchIndex.Should().Be(total - 1, "Shift+F3 from the first match must wrap to the last");

        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task SearchBoxEnter_RunsImmediateSearch()
    {
        var path = Temp("searchenter_effect.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(path);

        vm.ToggleSearchCommand?.Execute().Subscribe();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        vm.SearchText = "Content";

        var searchBox = window.FindControl<TextBox>("SearchTextBox");
        searchBox.Should().NotBeNull("the search bar hosts a named SearchTextBox");

        RaiseKeyDown(searchBox!, Key.Enter);
        await WaitForMatches(vm);

        vm.SearchMatches.Count.Should().BeGreaterThan(0,
            "Enter in the search box must trigger an immediate search (skipping the debounce)");

        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task SearchBoxEscape_ClosesSearchBar()
    {
        var path = Temp("searchesc_effect.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(path);

        vm.ToggleSearchCommand?.Execute().Subscribe();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        vm.IsSearchVisible.Should().BeTrue("search bar open before Escape");

        var searchBox = window.FindControl<TextBox>("SearchTextBox");
        RaiseKeyDown(searchBox!, Key.Escape);
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.IsSearchVisible.Should().BeFalse("Escape in the search box must close the search bar");

        window.Close();
    }

    // ==============================================================
    // Copy — Ctrl+C copies the live selection into clipboard history
    // ==============================================================

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task CtrlC_CopiesSelectionIntoClipboardHistory()
    {
        var path = Temp("copy_effect.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 1);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(path);

        vm.ToggleTextSelectionModeCommand?.Execute().Subscribe();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        vm.SelectedText = "Page 1 Content";        // stand in for a letter-walk selection

        await window.PressKeyAsync(Key.C, RawInputModifiers.Control);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        for (int i = 0; i < 20 && vm.ClipboardHistory.Count == 0; i++)
            await Task.Delay(50);

        vm.ClipboardHistory.Should().Contain(e => e.Text == "Page 1 Content",
            "Ctrl+C in text-selection mode must copy the live selection into clipboard history");

        window.Close();
    }

    // ==============================================================
    // Page operations — rotation asserts the exact resulting angle
    // ==============================================================

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task CtrlL_RotatesCurrentPageLeft()
    {
        var path = Temp("rotl_effect.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 1);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(path);
        var before = vm.PdfCoreDocument!.GetPage(1).Rotation;

        await window.PressKeyAsync(Key.L, RawInputModifiers.Control);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        for (int i = 0; i < 40 && vm.PdfCoreDocument!.GetPage(1).Rotation == before; i++)
            await Task.Delay(50);

        vm.PdfCoreDocument!.GetPage(1).Rotation.Should().Be((before + 270) % 360,
            "Ctrl+L must rotate the current page left 90°");

        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task CtrlR_RotatesCurrentPageRight()
    {
        // NOTE: the #827 text says "Ctrl+R (redaction mode)", but the code is the
        // authority: Ctrl+R = rotate RIGHT; plain R = redaction mode (already
        // effect-tested by KeyboardShortcutTests.R_ToggleRedactionMode).
        var path = Temp("rotr_effect.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 1);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(path);
        var before = vm.PdfCoreDocument!.GetPage(1).Rotation;

        await window.PressKeyAsync(Key.R, RawInputModifiers.Control);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        for (int i = 0; i < 40 && vm.PdfCoreDocument!.GetPage(1).Rotation == before; i++)
            await Task.Delay(50);

        vm.PdfCoreDocument!.GetPage(1).Rotation.Should().Be((before + 90) % 360,
            "Ctrl+R must rotate the current page right 90°");

        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task CtrlE_ExecutesExportCommand()
    {
        // Export ends at SaveFilePickerAsync -> IStorageFile, which Avalonia
        // seals against faking and exposes no path-override seam, so a written
        // PNG isn't reachable headless. The honest-but-strictly-stronger-than-
        // non-null effect: the KEY actually EXECUTES the bound command (a
        // ReactiveCommand emits Unit on completion). Before the fix, Ctrl+E was
        // unwired (menu InputGesture only) and this stayed false — this test was
        // written RED. This is the shallowest effect in the file; see report.
        var path = Temp("export_effect.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 1);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(path);

        var executed = false;
        using var sub = vm.ExportCurrentPageCommand!.Subscribe(_ => executed = true);

        await window.PressKeyAsync(Key.E, RawInputModifiers.Control);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        for (int i = 0; i < 20 && !executed; i++)
            await Task.Delay(50);

        executed.Should().BeTrue("Ctrl+E must route to and execute ExportCurrentPageCommand");

        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task CtrlP_ShowsPrintExplanationDialog()
    {
        var path = Temp("print_effect.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 1);

        var dialog = new RecordingDialogService();
        var vm = NewVmWithDialog(dialog);
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(path);

        await window.PressKeyAsync(Key.P, RawInputModifiers.Control);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        for (int i = 0; i < 20 && dialog.Messages.Count == 0; i++)
            await Task.Delay(50);

        dialog.Messages.Should().ContainSingle(m => m.Title == "Print",
            "Ctrl+P must surface the #621 print-explanation dialog");
        dialog.Messages.Single().Message.Should().Contain("doesn't print directly",
            "the dialog must carry the deliberate #621 explanation, not a stub");

        window.Close();
    }

    // ==============================================================
    // Redaction — Enter applies (marks) the current redaction area
    // ==============================================================

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task Enter_AppliesRedaction_MarksPendingArea()
    {
        // Enter was advertised as "Apply Redaction" (menu InputGesture) but had
        // no key handler — ApplyRedaction only fired from a pointer draw
        // (OnRedactionDrawn). Written RED, then Enter was wired (guarded on
        // redaction mode + non-text focus).
        var path = Temp("enter_redact_effect.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(path, "ENTERSECRET827");

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(path);

        vm.IsRedactionMode = true;
        vm.CurrentRedactionPageArea = PdfPageRect.FromContentPoints(1, new PdfRectangle(40, 660, 500, 740));
        vm.RedactionWorkflow.PendingCount.Should().Be(0, "nothing marked yet");

        await window.PressKeyAsync(Key.Return);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        for (int i = 0; i < 20 && vm.RedactionWorkflow.PendingCount == 0; i++)
            await Task.Delay(50);

        vm.RedactionWorkflow.PendingCount.Should().Be(1,
            "Enter in redaction mode with a drawn area must apply (mark) it as a pending redaction");

        window.Close();
    }

    // ==============================================================
    // Sidebars — Ctrl+Shift+O / Ctrl+Shift+T toggle visibility
    // ==============================================================

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task CtrlShiftO_TogglesOutlineSidebar()
    {
        var path = Temp("outline_effect.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(path);
        var before = vm.IsOutlineSidebarVisible;

        await window.PressKeyAsync(Key.O, RawInputModifiers.Control | RawInputModifiers.Shift);
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.IsOutlineSidebarVisible.Should().Be(!before, "Ctrl+Shift+O must toggle the outline sidebar");

        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task CtrlShiftT_TogglesThumbnailsSidebar()
    {
        var path = Temp("thumbs_effect.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(path);
        var before = vm.IsThumbnailsSidebarVisible;

        await window.PressKeyAsync(Key.T, RawInputModifiers.Control | RawInputModifiers.Shift);
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.IsThumbnailsSidebarVisible.Should().Be(!before, "Ctrl+Shift+T must toggle the thumbnails sidebar");

        window.Close();
    }

    // ==============================================================
    // Help & preferences dialogs
    // ==============================================================

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task F1_RequestsKeyboardShortcutsDialog()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        vm.MainWindowResolver = () => window;

        FluentAvalonia.UI.Controls.FAContentDialog? requested = null;
        vm.KeyboardShortcutsDialogRequested = d => requested = d;

        await window.PressKeyAsync(Key.F1);
        await KeyboardTestHelpers.FlushDispatcherAsync();

        requested.Should().NotBeNull("F1 must construct and request the real shortcuts dialog");
        requested!.Title.Should().Be("Keyboard Shortcuts");

        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task CtrlComma_OpensPreferencesWindow()
    {
        // Ctrl+, was advertised as "Preferences" (menu InputGesture) but had no
        // key handler. Written RED, then wired.
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        vm.MainWindowResolver = () => window;

        await window.PressKeyAsync(Key.OemComma, RawInputModifiers.Control);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await KeyboardTestHelpers.FlushDispatcherAsync();

        var prefs = window.OwnedWindows.OfType<PreferencesWindow>().SingleOrDefault();
        prefs.Should().NotBeNull("Ctrl+, must open the real Preferences window");

        prefs!.Close();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        window.Close();
    }

    // NOTE: Ctrl+1 fit-width is upgraded in place in KeyboardShortcutTests
    // .Ctrl1_FitsPageWidth (the vacuous ZoomLevel>0 replaced with a fit-width
    // vs fit-page distinction), not duplicated here. (#827)

    // ==============================================================
    // Unhandled keys — confirm intended no-ops (see #827 & batch A/C)
    // ==============================================================

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task CtrlA_IsNoOp_NoSelectAllShortcut()
    {
        // There is no "select all" command in the app; Ctrl+A is intentionally
        // unhandled at the window level. Assert it neither crashes nor mutates
        // document/mode state. (Tracked with the other unhandled keys in #827.)
        var path = Temp("ctrla_noop.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(path);
        var page = vm.CurrentPageIndex;
        var redaction = vm.IsRedactionMode;
        var selection = vm.IsTextSelectionMode;

        await window.PressKeyAsync(Key.A, RawInputModifiers.Control);
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.CurrentPageIndex.Should().Be(page);
        vm.IsRedactionMode.Should().Be(redaction);
        vm.IsTextSelectionMode.Should().Be(selection);

        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task Space_IsNoOp_PanNotImplemented()
    {
        // Space-pan is the dead InteractionMode.Pan feature #827 itself lists as
        // a pointer/scroll gap (batch A/C) — deliberately NOT implemented here.
        // Assert Space changes nothing and doesn't crash.
        var path = Temp("space_noop.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(path);
        var page = vm.CurrentPageIndex;

        await window.PressKeyAsync(Key.Space);
        await KeyboardTestHelpers.FlushDispatcherAsync();

        // Space is unhandled: no navigation, no pan. (ZoomLevel is deliberately
        // not asserted — the fit-on-load ratio settles asynchronously after
        // layout and is unrelated to the key.)
        vm.CurrentPageIndex.Should().Be(page, "Space must not navigate (pan is unimplemented)");

        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task Tab_IsNativeFocusTraversal_NotSwallowed()
    {
        // Tab is left to Avalonia's native focus traversal — MainWindow_KeyDown
        // must NOT intercept it. Assert it does not mutate document/nav state.
        var path = Temp("tab_native.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3);

        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(path);
        var page = vm.CurrentPageIndex;

        await window.PressKeyAsync(Key.Tab);
        await KeyboardTestHelpers.FlushDispatcherAsync();

        vm.CurrentPageIndex.Should().Be(page, "Tab is native focus traversal — the window handler must not act on it");

        window.Close();
    }

    // ==============================================================
    // Viewer control keys — Left/Right page nav, H/Shift+H headings
    // ==============================================================

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task ViewerRightAndLeftArrows_ChangePage()
    {
        var path = Temp("viewer_arrows.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var control = new PdfViewerControl { Document = PdfCoreDocument.Open(path) };
            control.CurrentPage.Should().Be(1);

            RaiseViewerKey(control, Key.Right);
            control.CurrentPage.Should().Be(2, "Right arrow advances the viewer to the next page");

            RaiseViewerKey(control, Key.Left);
            control.CurrentPage.Should().Be(1, "Left arrow returns the viewer to the previous page");

            control.Document?.Dispose();
        });
    }

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task ViewerH_And_ShiftH_NavigateHeadings_AcrossPages()
    {
        var bytes = PdfDocumentBuilder.Create()
            .Tagged()
            .Heading("Page One Heading", 1)
            .Paragraph("Body of page one")
            .PageBreak()
            .Heading("Page Two Heading", 1)
            .Paragraph("Body of page two")
            .SaveToBytes();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var control = new PdfViewerControl { Document = PdfCoreDocument.Open(bytes) };
            control.CurrentPage.Should().Be(1);
            control.CurrentStructureNavigationTarget.Should().BeNull("no heading navigation yet");

            RaiseViewerKey(control, Key.H);
            control.CurrentStructureNavigationTarget!.Value.Role.Should().Be(AccessibleStructRole.Heading);
            control.CurrentStructureNavigationTarget!.Value.Text.Should().Be("Page One Heading");
            control.CurrentPage.Should().Be(1);

            RaiseViewerKey(control, Key.H);
            control.CurrentStructureNavigationTarget!.Value.Text.Should().Be("Page Two Heading");
            control.CurrentPage.Should().Be(2, "H must cross the page boundary onto the next heading's page");

            RaiseViewerKey(control, Key.H, KeyModifiers.Shift);
            control.CurrentStructureNavigationTarget!.Value.Text.Should().Be("Page One Heading",
                "Shift+H must step back to the previous heading");

            control.Document?.Dispose();
        });
    }

    // ==============================================================
    // Down / Up arrow — real key ROUTING (was reflection-only, #827)
    // ==============================================================

    // FINDING (#827): KeyboardShortcutTests drove Down/Up by REFLECTION-invoking
    // MainWindow_KeyDown, which proves the handler body but NOT that a real Down
    // keypress reaches it. It does not: a real Down/Up press never bubbles to the
    // window's KeyDown handler as unhandled (Avalonia's input pipeline consumes
    // arrow keys first), so the MainWindow_KeyDown Down/Up branch was dead for
    // real input. The reachable, deterministic route is the viewer's TUNNEL
    // handler (OnViewerKeyDown) — the same one Left/Right already page-navigate
    // through. Down/Up were added there so a real arrow press now navigates
    // pages, verified below via real routed KeyDown on the viewer.

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task ViewerDownArrow_RealDispatch_AdvancesPage()
    {
        var path = Temp("down_route.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 5);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var control = new PdfViewerControl { Document = PdfCoreDocument.Open(path) };
            control.CurrentPage.Should().Be(1);

            RaiseViewerKey(control, Key.Down);
            control.CurrentPage.Should().Be(2,
                "a real Down keypress must route to the viewer handler and advance the page");

            control.Document?.Dispose();
        });
    }

    [FixedAvaloniaFact(Timeout = 20000)]
    public async Task ViewerUpArrow_RealDispatch_ReturnsToPreviousPage()
    {
        var path = Temp("up_route.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 5);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var control = new PdfViewerControl { Document = PdfCoreDocument.Open(path) };
            control.CurrentPage = 3;

            RaiseViewerKey(control, Key.Up);
            control.CurrentPage.Should().Be(2,
                "a real Up keypress must route to the viewer handler and return to the previous page");

            control.Document?.Dispose();
        });
    }

    // ==============================================================
    // Dispatch helpers
    // ==============================================================

    private static void RaiseKeyDown(Control target, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        target.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Route = RoutingStrategies.Bubble,
            Key = key,
            KeyModifiers = modifiers,
        });
    }

    private static void RaiseViewerKey(PdfViewerControl control, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Route = RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            Key = key,
            KeyModifiers = modifiers,
        };
        control.RaiseEvent(args);
        args.Handled.Should().BeTrue($"{key} must be handled by the viewer key handler");
    }
}
