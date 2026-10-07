using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AwesomeAssertions;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;
namespace Excise.App.Tests.UI;

/// <summary>
/// Comprehensive keyboard shortcut tests for Excise.App.
/// Verifies all keyboard bindings advertised in MainWindow.axaml and code-behind.
/// Tests follow the pattern: load test PDF → send keystroke → assert effect on ViewModel.
///
/// Categories covered:
/// - File operations: Ctrl+O, Ctrl+S, Ctrl+Shift+S, Ctrl+W, Alt+F4
/// - Edit/Search: Ctrl+F, F3, Shift+F3, Escape (close search), Ctrl+C
/// - Navigation: PageUp, PageDown, Home, End, Up/Down arrows
/// - Page ops: Ctrl+L (rotate left), Ctrl+R (rotate right), Ctrl+E (export), Ctrl+P (print)
/// - Zoom: Ctrl+=, Ctrl+-, Ctrl+0, Ctrl+1, Ctrl+2
/// - Mode toggles: T (text selection); bare R must not change mode (#1976)
/// - Other: F1 (help), Ctrl+, (preferences), Enter (apply redaction)
/// </summary>
[Collection("AvaloniaTests")]
public class KeyboardShortcutTests : IDisposable
{
    /// <summary>Every window this class shows is closed after the test, including
    /// when it fails (#706, #1771) — this class showed 23 windows and closed
    /// none, accumulating for the whole run and perturbing pointer routing and
    /// focus for every later test.</summary>
    private readonly ShownWindowTracker _windows = new();

    public void Dispose() => _windows.Dispose();

    private readonly ITestOutputHelper _out;
    private readonly string _tempDir;

    public KeyboardShortcutTests(ITestOutputHelper output)
    {
        _out = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "Excise.AppKeyboardTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    private string CreateTestPdf(string nameHint = "test.pdf")
        => Path.Combine(_tempDir, nameHint);

    #region File Operations

    // Ctrl+O and Ctrl+S real-effect coverage lives in
    // KeyboardShortcutEffectTests.CtrlO_ExecutesOpenFileCommand /
    // CtrlS_ExecutesSaveFileCommand (#1777): the versions formerly here asserted
    // only Command.Should().NotBeNull(), which passes even when the key isn't
    // wired to the command at all.

    #endregion

    #region Search & Edit

    /// <summary>
    /// Ctrl+F: Toggle search bar visibility.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 15000)]
    public async Task CtrlF_ToggleSearchBar()
    {
        // Arrange
        var pdfPath = CreateTestPdf("search_toggle.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 2);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        _windows.Show(window);

        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(100);
        var initialState = vm.IsSearchVisible;

        // Act
        await window.PressKeyAsync(Key.F, RawInputModifiers.Control);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await Task.Delay(100);

        // Assert
        vm.IsSearchVisible.Should().NotBe(initialState, "Ctrl+F should toggle search bar");
    }

    // F3 real-effect coverage lives in
    // KeyboardShortcutEffectTests.F3_AdvancesCurrentSearchMatchIndex (#1777): the
    // version formerly here asserted only FindNextCommand.Should().NotBeNull().

    /// <summary>
    /// Escape: Close search bar (when visible).
    /// </summary>
    [FixedAvaloniaFact(Timeout = 15000)]
    public async Task Escape_ClosesSearchBar()
    {
        // Arrange
        var pdfPath = CreateTestPdf("search_escape.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 2);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        _windows.Show(window);

        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(100);

        // Open search bar first
        vm.ToggleSearchCommand?.Execute().Subscribe();
        await Task.Delay(100);
        vm.IsSearchVisible.Should().BeTrue("search bar should be open");

        // Act
        await window.PressKeyAsync(Key.Escape);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await Task.Delay(100);

        // Assert
        vm.IsSearchVisible.Should().BeFalse("Escape should close search bar");
    }


    #endregion

    #region Page Navigation

    /// <summary>
    /// Page Down: Advance to next page.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 15000)]
    public async Task PageDown_AdvancesToNextPage()
    {
        // Arrange
        var pdfPath = CreateTestPdf("pagedown.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 5);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        _windows.Show(window);

        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(100);
        await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => window.Focus());
        var initialPage = vm.CurrentPageIndex;

        // Act
        await window.PressKeyAsync(Key.PageDown);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await Task.Delay(100);

        // Assert
        vm.CurrentPageIndex.Should().BeGreaterThan(initialPage, "PageDown should advance page");
    }

    /// <summary>
    /// Page Up: Go to previous page.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 15000)]
    public async Task PageUp_ReturnsToPreviousPage()
    {
        // Arrange
        var pdfPath = CreateTestPdf("pageup.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 5);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        _windows.Show(window);

        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(100);

        // Start at page 2
        vm.CurrentPageIndex = 1;
        await Task.Delay(50);
        await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => window.Focus());
        var pageBeforeUp = vm.CurrentPageIndex;

        // Act
        await window.PressKeyAsync(Key.PageUp);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await Task.Delay(100);

        // Assert
        vm.CurrentPageIndex.Should().BeLessThan(pageBeforeUp, "PageUp should go to previous page");
    }

    /// <summary>
    /// Home: Jump to first page.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 15000)]
    public async Task Home_JumpsToFirstPage()
    {
        // Arrange
        var pdfPath = CreateTestPdf("home.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 5);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        _windows.Show(window);

        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(100);

        // Start at middle
        vm.CurrentPageIndex = 2;
        await Task.Delay(50);

        // Act
        await window.PressKeyAsync(Key.Home);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await Task.Delay(100);

        // Assert
        vm.CurrentPageIndex.Should().Be(0, "Home should jump to first page");
    }

    /// <summary>
    /// End: Jump to last page.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 15000)]
    public async Task End_JumpsToLastPage()
    {
        // Arrange
        var pdfPath = CreateTestPdf("end.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 5);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        _windows.Show(window);

        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(100);

        // Start at page 1
        vm.CurrentPageIndex = 0;
        await Task.Delay(50);

        // Act
        await window.PressKeyAsync(Key.End);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await Task.Delay(100);

        // Assert
        vm.CurrentPageIndex.Should().Be(vm.TotalPages - 1, "End should jump to last page");
    }

    /// <summary>
    /// Down arrow: Advance to next page (when not in a text control).
    /// </summary>
    [FixedAvaloniaFact(Timeout = 15000)]
    public async Task DownArrow_AdvancesToNextPage()
    {
        // Arrange
        var pdfPath = CreateTestPdf("downarrow.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 5);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        _windows.Show(window);

        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(100);
        var initialPage = vm.CurrentPageIndex;

        // Act
        await InvokeMainWindowKeyDownAsync(window, Key.Down);
        await Task.Delay(100);

        // Assert
        vm.CurrentPageIndex.Should().BeGreaterThan(initialPage, "Down arrow should advance page");
    }

    /// <summary>
    /// Up arrow: Return to previous page.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 15000)]
    public async Task UpArrow_ReturnsToPreviousPage()
    {
        // Arrange
        var pdfPath = CreateTestPdf("uparrow.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 5);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        _windows.Show(window);

        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(100);

        // Start at page 2
        vm.CurrentPageIndex = 1;
        await Task.Delay(50);
        var pageBeforeUp = vm.CurrentPageIndex;

        // Act
        await InvokeMainWindowKeyDownAsync(window, Key.Up);
        await Task.Delay(100);

        // Assert
        vm.CurrentPageIndex.Should().BeLessThan(pageBeforeUp, "Up arrow should go to previous page");
    }

    #endregion

    private static async Task InvokeMainWindowKeyDownAsync(
        MainWindow window,
        Key key,
        KeyModifiers modifiers = KeyModifiers.None)
    {
        var method = typeof(MainWindow).GetMethod(
            "MainWindow_KeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull("arrow shortcut replacements should exercise the real MainWindow key handler");

        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Route = global::Avalonia.Interactivity.RoutingStrategies.Bubble,
            Key = key,
            KeyModifiers = modifiers,
        };
        method!.Invoke(window, new object?[] { window, args });
        args.Handled.Should().BeTrue($"{key} should be handled by MainWindow_KeyDown");
        await KeyboardTestHelpers.FlushDispatcherAsync();
    }

    #region Page Operations

    // Ctrl+L real-effect coverage lives in
    // KeyboardShortcutEffectTests.CtrlL_RotatesCurrentPageLeft (#1777): the
    // version formerly here asserted only RotatePageLeftCommand.Should().NotBeNull().

    #endregion

    #region Zoom

    /// <summary>
    /// Ctrl+Plus: Zoom in.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 15000)]
    public async Task CtrlPlus_ZoomsIn()
    {
        // Arrange
        var pdfPath = CreateTestPdf("zoom_in.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 1);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        _windows.Show(window);

        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(100);
        var initialZoom = vm.ZoomLevel;

        // Act
        await window.PressKeyAsync(Key.OemPlus, RawInputModifiers.Control);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await Task.Delay(100);

        // Assert
        vm.ZoomLevel.Should().BeGreaterThan(initialZoom, "Ctrl++ should increase zoom");
    }

    /// <summary>
    /// Ctrl+Minus: Zoom out.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 15000)]
    public async Task CtrlMinus_ZoomsOut()
    {
        // Arrange
        var pdfPath = CreateTestPdf("zoom_out.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 1);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        _windows.Show(window);

        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(100);

        // First zoom in so we can zoom out
        vm.ZoomLevel = 2.0;
        await Task.Delay(50);
        var initialZoom = vm.ZoomLevel;

        // Act
        await window.PressKeyAsync(Key.OemMinus, RawInputModifiers.Control);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await Task.Delay(100);

        // Assert
        vm.ZoomLevel.Should().BeLessThan(initialZoom, "Ctrl+- should decrease zoom");
    }

    /// <summary>
    /// Ctrl+0: Actual size (100%).
    /// </summary>
    [FixedAvaloniaFact(Timeout = 15000)]
    public async Task Ctrl0_ResetsZoomToActualSize()
    {
        // Arrange
        var pdfPath = CreateTestPdf("zoom_actual.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 1);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        _windows.Show(window);

        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(100);

        // Change zoom first
        vm.ZoomLevel = 1.5;
        await Task.Delay(50);

        // Act
        await window.PressKeyAsync(Key.D0, RawInputModifiers.Control);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await Task.Delay(100);

        // Assert: Should be close to 1.0 (actual size)
        vm.ZoomLevel.Should().BeApproximately(1.0, 0.1, "Ctrl+0 should reset zoom to actual size (100%)");
    }

    /// <summary>
    /// Ctrl+1: Fit width.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 15000)]
    public async Task Ctrl1_FitsPageWidth()
    {
        // Arrange
        var pdfPath = CreateTestPdf("zoom_fit_width.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 1);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        _windows.Show(window);

        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(100);

        // Act
        await window.PressKeyAsync(Key.D1, RawInputModifiers.Control);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await Task.Delay(100);
        var fitWidthZoom = vm.ZoomLevel;

        // Assert: `BeGreaterThan(0)` alone was vacuous — ZoomLevel is >0 by
        // construction in every zoom path, so it couldn't tell a real fit-WIDTH
        // ratio apart from a no-op or a fit-page ratio landing here by mistake
        // (#827). Ground it against the real fit-width command, then prove it is
        // genuinely fit-WIDTH by contrasting with fit-page on this non-square
        // (portrait page / landscape viewport) combination. The exact ratio math
        // is covered by AnnotateAndDialogCommandTests.ZoomFitPageCommand_*.
        fitWidthZoom.Should().BeGreaterThan(0, "Ctrl+1 should fit page to width");

        vm.ZoomFitWidthCommand.Execute().Subscribe();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await Task.Delay(100);
        vm.ZoomLevel.Should().BeApproximately(fitWidthZoom, 0.01,
            "Ctrl+1 must produce the same zoom as the fit-width command");

        vm.ZoomFitPageCommand.Execute().Subscribe();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await Task.Delay(100);
        Math.Abs(fitWidthZoom - vm.ZoomLevel).Should().BeGreaterThan(0.01,
            "Ctrl+1 must route to fit-WIDTH, distinct from fit-page on this non-square page — not merely leave ZoomLevel > 0");
    }

    /// <summary>
    /// Ctrl+2: Fit page.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 15000)]
    public async Task Ctrl2_FitsEntirePage()
    {
        // Arrange
        var pdfPath = CreateTestPdf("zoom_fit_page.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 1);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        _windows.Show(window);

        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(100);

        // Act
        await window.PressKeyAsync(Key.D2, RawInputModifiers.Control);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await Task.Delay(100);
        var fitPageZoom = vm.ZoomLevel;

        // Assert: `BeGreaterThan(0)` alone is vacuous — ZoomLevel is >0 by
        // construction/clamping in every zoom path, so it can't tell a
        // correctly-computed fit-page ratio apart from a no-op or a
        // fit-WIDTH ratio landing here by mistake (#816). The default
        // multi-page fixture's page is portrait (non-square), so on a
        // non-square viewport fit-page (constrained by height too) must
        // differ from fit-width (constrained by width alone) — assert that
        // distinction directly using the real ZoomFitWidthCommand as the
        // comparison, not a hard-coded geometry recomputation (the exact
        // ratio math is covered independently by
        // AnnotateAndDialogCommandTests.ZoomFitPageCommand_ComputesTheFitPageRatio_DistinctFromFitWidth).
        fitPageZoom.Should().BeGreaterThan(0, "Ctrl+2 should fit entire page");

        vm.ZoomFitWidthCommand.Execute().Subscribe();
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await Task.Delay(100);
        var fitWidthZoom = vm.ZoomLevel;

        Math.Abs(fitPageZoom - fitWidthZoom).Should().BeGreaterThan(0.01,
            "Ctrl+2 must route to the fit-PAGE ratio, which is distinct from fit-width on this non-square page — " +
            "not merely leave ZoomLevel at some positive value");
    }

    #endregion

    #region Mode Toggles

    /// <summary>
    /// Bare R must not toggle redaction mode (#1976).
    /// </summary>
    [FixedAvaloniaFact(Timeout = 15000)]
    public async Task R_DoesNotToggleRedactionMode()
    {
        // Arrange
        var pdfPath = CreateTestPdf("redaction_mode.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 1);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        _windows.Show(window);

        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(100);
        var initialState = vm.IsRedactionMode;

        // Act
        await window.PressKeyAsync(Key.R);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await Task.Delay(100);

        // Assert
        vm.IsRedactionMode.Should().Be(initialState, "bare R is ordinary input, not a mode shortcut (#1976)");
    }

    /// <summary>
    /// T: Toggle text selection mode.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 15000)]
    public async Task T_ToggleTextSelectionMode()
    {
        // Arrange
        var pdfPath = CreateTestPdf("text_select_mode.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 1);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        _windows.Show(window);

        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(100);
        var initialState = vm.IsTextSelectionMode;

        // Act
        await window.PressKeyAsync(Key.T);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await Task.Delay(100);

        // Assert
        vm.IsTextSelectionMode.Should().NotBe(initialState, "T should toggle text selection mode");
    }

    // Enter (apply redaction) real-effect coverage lives in
    // KeyboardShortcutEffectTests.Enter_AppliesRedaction_MarksPendingArea
    // (#1777): the version formerly here asserted only
    // ApplyRedactionCommand.Should().NotBeNull().

    #endregion

    #region Help & Preferences



    #endregion

    #region Compound Flow Tests

    /// <summary>
    /// Compound: Ctrl+F → type text → Enter → verify search progresses.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 15000)]
    public async Task CompoundFlow_SearchWorkflow()
    {
        // Arrange
        var pdfPath = CreateTestPdf("search_workflow.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 3);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        _windows.Show(window);

        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(100);

        // Act: Open search
        await window.PressKeyAsync(Key.F, RawInputModifiers.Control);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await Task.Delay(100);

        // Act: Type search term
        await window.TypeTextAsync("Page");
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await Task.Delay(200); // Give search time to find matches

        // Assert: Search should be visible and have populated SearchText
        vm.IsSearchVisible.Should().BeTrue("Search bar should be open after Ctrl+F");
        vm.SearchText.Should().Contain("Page", "SearchText should contain typed text");
    }

    /// <summary>
    /// Compound: PageDown multiple times → verify navigation.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 15000)]
    public async Task CompoundFlow_MultiplePageDowns()
    {
        // Arrange
        var pdfPath = CreateTestPdf("multi_pagedown.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 5);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        _windows.Show(window);

        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(100);
        var startPage = vm.CurrentPageIndex;

        // Act: Press PageDown three times
        for (int i = 0; i < 3; i++)
        {
            await window.PressKeyAsync(Key.PageDown);
            await KeyboardTestHelpers.FlushDispatcherAsync();
            await Task.Delay(50);
        }

        // Assert: Should have advanced by 3 (unless we hit the end)
        var expectedPage = Math.Min(startPage + 3, vm.TotalPages - 1);
        vm.CurrentPageIndex.Should().Be(expectedPage, "Three PageDown presses should advance 3 pages");
    }

    /// <summary>
    /// Compound: Ctrl+= twice → verify zoom stacks correctly.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 15000)]
    public async Task CompoundFlow_MultipleZoomIns()
    {
        // Arrange
        var pdfPath = CreateTestPdf("multi_zoom_in.pdf");
        TestPdfGenerator.CreateMultiPagePdf(pdfPath, pageCount: 1);
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        _windows.Show(window);

        await vm.LoadDocumentAsync(pdfPath);
        await Task.Delay(100);
        var initialZoom = vm.ZoomLevel;

        // Act: Press Ctrl++ twice
        for (int i = 0; i < 2; i++)
        {
            await window.PressKeyAsync(Key.OemPlus, RawInputModifiers.Control);
            await KeyboardTestHelpers.FlushDispatcherAsync();
            await Task.Delay(50);
        }

        // Assert: Zoom should be higher than initial (accounting for possible min/max bounds)
        vm.ZoomLevel.Should().BeGreaterThan(initialZoom, "Two Ctrl++ presses should increase zoom");
    }

    #endregion
}
