using System.Reactive.Linq;
using AwesomeAssertions;
using Avalonia.Input;
using Avalonia.Headless;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1170 — Ctrl+Z, Ctrl+Y and Ctrl+Shift+C were advertised by the menus and
/// wired to nothing.
/// </summary>
/// <remarks>
/// <para>
/// In Avalonia a <c>MenuItem.InputGesture</c> is display text only; the working
/// shortcuts are each duplicated by hand in <c>MainWindow_KeyDown</c>, and
/// these three had no branch there. The Edit menu said Ctrl+Z and Ctrl+Y, the
/// View menu and the toolbar tooltip said Ctrl+Shift+C, and pressing any of
/// them did nothing on Windows/Linux. (macOS was unaffected — its native menu
/// carries real <c>NativeMenuItem.Gesture</c>s.)
/// </para>
/// <para>
/// Every test here presses the REAL key through the headless raw-input path and
/// asserts the model changed. That distinction is the whole point of the issue:
/// the interaction recorder logs a declared gesture on keydown whether or not a
/// command runs, so a test that merely pressed the key would have claimed
/// coverage for a shortcut that did nothing.
/// </para>
/// </remarks>
[Collection("AvaloniaTests")]
public class UndoRedoContinuousKeyboardTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"excise-undo-keys-{Guid.NewGuid():N}");

    public UndoRedoContinuousKeyboardTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    private string NewPdf(string name, int pages = 2)
    {
        var path = Path.Combine(_tempDir, name);
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: pages);
        return path;
    }

    private static async Task<(MainWindowViewModel vm, MainWindow window)> OpenAsync(string path)
    {
        var vm = MainWindowViewModelTestFactory.Create();
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
        window.Show();
        await vm.LoadDocumentAsync(path);
        return (vm, window);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 60; i++)
        {
            await KeyboardTestHelpers.FlushDispatcherAsync();
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task CtrlZ_UndoesThePreviousEdit()
    {
        var (vm, window) = await OpenAsync(NewPdf("undo.pdf"));

        var before = vm.PdfCoreDocument!.GetPage(1).Rotation;
        await vm.RotatePageRightCommand.Execute();
        await WaitForAsync(() => vm.PdfCoreDocument!.GetPage(1).Rotation != before);
        var rotated = vm.PdfCoreDocument!.GetPage(1).Rotation;
        rotated.Should().NotBe(before, "the fixture edit must actually have happened");
        vm.CanUndo.Should().BeTrue();

        await window.PressKeyAsync(Key.Z, RawInputModifiers.Control);
        await WaitForAsync(() => vm.PdfCoreDocument!.GetPage(1).Rotation == before);

        vm.PdfCoreDocument!.GetPage(1).Rotation.Should().Be(before,
            "Ctrl+Z must actually revert the edit, not merely be a menu label");

        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task CtrlY_RedoesTheUndoneEdit()
    {
        var (vm, window) = await OpenAsync(NewPdf("redo.pdf"));

        var before = vm.PdfCoreDocument!.GetPage(1).Rotation;
        await vm.RotatePageRightCommand.Execute();
        await WaitForAsync(() => vm.PdfCoreDocument!.GetPage(1).Rotation != before);
        var rotated = vm.PdfCoreDocument!.GetPage(1).Rotation;

        await vm.UndoCommand.Execute();
        await WaitForAsync(() => vm.PdfCoreDocument!.GetPage(1).Rotation == before);
        vm.CanRedo.Should().BeTrue();

        await window.PressKeyAsync(Key.Y, RawInputModifiers.Control);
        await WaitForAsync(() => vm.PdfCoreDocument!.GetPage(1).Rotation == rotated);

        vm.PdfCoreDocument!.GetPage(1).Rotation.Should().Be(rotated,
            "Ctrl+Y must actually reapply the undone edit");

        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task CtrlShiftC_TogglesContinuousScroll()
    {
        var (vm, window) = await OpenAsync(NewPdf("continuous.pdf"));
        var before = vm.IsContinuousView;

        await window.PressKeyAsync(Key.C, RawInputModifiers.Control | RawInputModifiers.Shift);
        await WaitForAsync(() => vm.IsContinuousView != before);

        vm.IsContinuousView.Should().Be(!before, "Ctrl+Shift+C must toggle continuous scroll");

        window.Close();
    }

    /// <summary>
    /// The regression the branch ORDER exists to prevent. The plain Ctrl+C copy
    /// branch does not (and historically did not) exclude Shift, so a
    /// Ctrl+Shift+C handler placed after it would never run in text-selection
    /// mode — the view toggle would silently keep doing nothing, in exactly the
    /// mode a user is most likely to be in.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task CtrlShiftC_StillTogglesWhileInTextSelectionMode()
    {
        var (vm, window) = await OpenAsync(NewPdf("continuous-textmode.pdf"));

        if (!vm.IsTextSelectionMode)
            await vm.ToggleTextSelectionModeCommand.Execute();
        vm.IsTextSelectionMode.Should().BeTrue("this test is about the copy-branch collision");

        var before = vm.IsContinuousView;
        await window.PressKeyAsync(Key.C, RawInputModifiers.Control | RawInputModifiers.Shift);
        await WaitForAsync(() => vm.IsContinuousView != before);

        vm.IsContinuousView.Should().Be(!before,
            "Ctrl+Shift+C must not be swallowed by the Ctrl+C copy branch");

        window.Close();
    }

    /// <summary>
    /// Ctrl+Z must not steal a text field's own undo. The handler returns
    /// WITHOUT setting Handled when a TextBox has focus, so the keystroke
    /// reaches the editor instead of rotating pages back.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task CtrlZ_WithSearchBoxFocused_DoesNotUndoTheDocument()
    {
        var (vm, window) = await OpenAsync(NewPdf("undo-guard.pdf"));

        var before = vm.PdfCoreDocument!.GetPage(1).Rotation;
        await vm.RotatePageRightCommand.Execute();
        await WaitForAsync(() => vm.PdfCoreDocument!.GetPage(1).Rotation != before);
        var rotated = vm.PdfCoreDocument!.GetPage(1).Rotation;

        // Ctrl+F opens the search bar and focuses its TextBox.
        await window.PressKeyAsync(Key.F, RawInputModifiers.Control);
        await WaitForAsync(() => vm.IsSearchVisible);
        await WaitForAsync(() =>
            window.FocusManager?.GetFocusedElement() is global::Avalonia.Controls.TextBox);

        await window.PressKeyAsync(Key.Z, RawInputModifiers.Control);
        await KeyboardTestHelpers.FlushDispatcherAsync();
        await Task.Delay(100);

        vm.PdfCoreDocument!.GetPage(1).Rotation.Should().Be(rotated,
            "a window-level Ctrl+Z must not swallow the focused text field's own undo");

        window.Close();
    }
}
