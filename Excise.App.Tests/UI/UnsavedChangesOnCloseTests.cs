using System.Reactive.Linq;
using AwesomeAssertions;
using Avalonia.Headless;
using Avalonia.Threading;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1233 — closing, quitting, or opening another file with unsaved document
/// changes used to discard them SILENTLY.
/// </summary>
/// <remarks>
/// <para>
/// The hole was not subtle: <c>MainWindow.Closing</c> persisted window geometry
/// and returned. It never read <c>FileState.HasUnsavedChanges</c>, never set
/// <c>e.Cancel</c>, and no <c>e.Cancel</c> existed anywhere in
/// <c>Excise.App</c>. The user got no prompt, no toast, and no log line — the
/// redactions/annotations/page edits were simply gone.
/// </para>
/// <para>
/// These tests were written RED against that code and assert the EFFECT
/// (window still open, file on disk, counters still dirty), never merely that
/// a prompt method exists. The two that matter most are the failure paths:
/// a cancelled picker and a throwing save must both leave the document open
/// AND still dirty, because "we tried to save and couldn't" is the case where
/// silently proceeding destroys the most work.
/// </para>
/// </remarks>
[Collection("AvaloniaTests")]
public class UnsavedChangesOnCloseTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"excise-unsaved-close-{Guid.NewGuid():N}");

    public UnsavedChangesOnCloseTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    /// <summary>
    /// Records the three-way prompt and answers with a scripted decision.
    /// </summary>
    private sealed class FakeUserDialogService : IUserDialogService
    {
        public int UnsavedPromptCount { get; private set; }
        public UnsavedChangesDecision Decision { get; set; } = UnsavedChangesDecision.Cancel;
        public string? LastMessage { get; private set; }
        public string? LastSaveActionText { get; private set; }

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<UnsavedChangesDecision> ShowUnsavedChangesAsync(
            string title, string message, string saveActionText)
        {
            UnsavedPromptCount++;
            LastMessage = message;
            LastSaveActionText = saveActionText;
            return Task.FromResult(Decision);
        }
    }

    private string NewPdf(string name, string text = "Unsaved changes close test")
    {
        var path = Path.Combine(_tempDir, name);
        TestPdfGenerator.CreateSimpleTextPdf(path, text);
        return path;
    }

    private static (MainWindowViewModel vm, FakeUserDialogService dialog) CreateViewModel()
    {
        var dialog = new FakeUserDialogService();
        var vm = MainWindowViewModelTestFactory.Create(dialogService: dialog);
        return (vm, dialog);
    }

    /// <summary>
    /// Make the document genuinely dirty through a real user command, not by
    /// poking the counter — so the test breaks if the command stops marking
    /// the document modified.
    /// </summary>
    private static async Task MakeDirtyAsync(MainWindowViewModel vm)
    {
        await vm.AddStickyNoteAnnotationCommand.Execute();
        vm.HasUnsavedDocumentChanges.Should().BeTrue(
            "the fixture must actually be dirty or the test proves nothing");
    }

    private static async Task FlushAsync()
    {
        for (var i = 0; i < 10; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(20);
        }
    }

    // ---------------------------------------------------------------- window close

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task ClosingWithUnsavedChanges_CancelsTheClose_AndKeepsTheDocumentDirty()
    {
        var source = NewPdf("cancel-close.pdf");
        var (vm, dialog) = CreateViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        await vm.LoadDocumentAsync(source);
        await MakeDirtyAsync(vm);

        dialog.Decision = UnsavedChangesDecision.Cancel;
        window.Close();
        await FlushAsync();

        dialog.UnsavedPromptCount.Should().Be(1, "closing a dirty document must ask exactly once");
        window.IsVisible.Should().BeTrue("cancelling the prompt must abandon the close");
        vm.HasUnsavedDocumentChanges.Should().BeTrue("cancelling must not touch the pending edits");

        vm.FileState.MarkSaved();   // let the window close during teardown
        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task ClosingWithNoUnsavedChanges_ClosesImmediatelyWithoutPrompting()
    {
        var source = NewPdf("clean-close.pdf");
        var (vm, dialog) = CreateViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        await vm.LoadDocumentAsync(source);

        window.Close();
        await FlushAsync();

        dialog.UnsavedPromptCount.Should().Be(0,
            "a clean document must close with no dialog at all — the guard must not become a nag");
        window.IsVisible.Should().BeFalse();
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task ClosingWithUnsavedChanges_Discard_ClosesTheWindow()
    {
        var source = NewPdf("discard-close.pdf");
        var (vm, dialog) = CreateViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        await vm.LoadDocumentAsync(source);
        await MakeDirtyAsync(vm);

        dialog.Decision = UnsavedChangesDecision.Discard;
        window.Close();
        await FlushAsync();

        dialog.UnsavedPromptCount.Should().Be(1);
        window.IsVisible.Should().BeFalse("an explicit Discard must let the close proceed");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task ClosingWithUnsavedChanges_Save_WritesACopy_LeavesOriginalUntouched_AndCloses()
    {
        var source = NewPdf("save-close.pdf");
        var originalBytes = await File.ReadAllBytesAsync(source);
        var copyPath = Path.Combine(_tempDir, "save-close-copy.pdf");

        var (vm, dialog) = CreateViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        await vm.LoadDocumentAsync(source);
        await MakeDirtyAsync(vm);

        vm.PickSavePdfPathOverride = () => Task.FromResult<string?>(copyPath);
        dialog.Decision = UnsavedChangesDecision.Save;

        window.Close();
        await FlushAsync();

        dialog.UnsavedPromptCount.Should().Be(1);
        File.Exists(copyPath).Should().BeTrue("Save must actually write the copy");
        (await File.ReadAllBytesAsync(source)).Should().Equal(originalBytes,
            "the ORIGINAL source must never be overwritten by the close-prompt save (#1233)");
        vm.HasUnsavedDocumentChanges.Should().BeFalse("a completed save clears the dirty state");
        window.IsVisible.Should().BeFalse("a successful save must let the close proceed");
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task ClosingWithUnsavedChanges_SavePickerCancelled_KeepsWindowOpenAndDirty()
    {
        var source = NewPdf("picker-cancelled.pdf");
        var (vm, dialog) = CreateViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        await vm.LoadDocumentAsync(source);
        await MakeDirtyAsync(vm);

        // The user picks Save, then backs out of the file picker.
        vm.PickSavePdfPathOverride = () => Task.FromResult<string?>(null);
        dialog.Decision = UnsavedChangesDecision.Save;

        window.Close();
        await FlushAsync();

        window.IsVisible.Should().BeTrue(
            "a cancelled save picker must NOT be read as permission to close and discard");
        vm.HasUnsavedDocumentChanges.Should().BeTrue("the edits must survive a cancelled save");

        vm.FileState.MarkSaved();   // let the window close during teardown
        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task ClosingWithUnsavedChanges_SaveFails_KeepsWindowOpenAndDirty()
    {
        var source = NewPdf("save-fails.pdf");
        var (vm, dialog) = CreateViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        await vm.LoadDocumentAsync(source);
        await MakeDirtyAsync(vm);

        // A directory that does not exist: SaveFileAsAsync catches and logs the
        // IO failure, so the ONLY signal that it failed is the dirty state.
        var unwritable = Path.Combine(_tempDir, "no-such-dir", "out.pdf");
        vm.PickSavePdfPathOverride = () => Task.FromResult<string?>(unwritable);
        dialog.Decision = UnsavedChangesDecision.Save;

        window.Close();
        await FlushAsync();

        File.Exists(unwritable).Should().BeFalse("the save genuinely could not happen");
        window.IsVisible.Should().BeTrue("a FAILED save must never be treated as a successful one");
        vm.HasUnsavedDocumentChanges.Should().BeTrue();

        vm.FileState.MarkSaved();   // let the window close during teardown
        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task ClosePrompt_OnAnOriginalSource_OffersACopyWording_AndSaysTheOriginalIsSafe()
    {
        var source = NewPdf("wording.pdf");
        var (vm, dialog) = CreateViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        await vm.LoadDocumentAsync(source);
        await MakeDirtyAsync(vm);

        dialog.Decision = UnsavedChangesDecision.Cancel;
        window.Close();
        await FlushAsync();

        dialog.LastSaveActionText.Should().Contain("Copy",
            "on an unmodified original the affirmative button must not read as an in-place overwrite");
        dialog.LastMessage.Should().Contain("never overwritten",
            "a user who fears losing the original will pick Discard — so the prompt must say the original is safe");
        dialog.LastMessage.Should().Contain("annotation edit",
            "the prompt must name what is at risk rather than saying 'unsaved changes'");

        vm.FileState.MarkSaved();   // let the window close during teardown
        window.Close();
    }

    /// <summary>
    /// Discard must CLEAR the dirty state, not merely return "go ahead".
    /// </summary>
    /// <remarks>
    /// Quit is where this bites. ExitAsync asks, the user picks Discard,
    /// TryShutdown then closes the window — and if the counters were still
    /// non-zero, OnWindowClosing would ask the identical question a second
    /// time. The double prompt cannot be reproduced headlessly (there is no
    /// desktop lifetime to shut down), so the property it depends on is
    /// pinned directly.
    /// </remarks>
    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task Discard_ClearsDirtyState_SoASecondGuardDoesNotAskAgain()
    {
        var source = NewPdf("discard-clears.pdf");
        var (vm, dialog) = CreateViewModel();
        await vm.LoadDocumentAsync(source);
        await MakeDirtyAsync(vm);

        dialog.Decision = UnsavedChangesDecision.Discard;
        var proceed = await vm.ConfirmDiscardUnsavedChangesAsync("quit excise");

        proceed.Should().BeTrue();
        vm.HasUnsavedDocumentChanges.Should().BeFalse(
            "after Discard the edits are gone, so a later guard must not re-ask about them");

        dialog.Decision = UnsavedChangesDecision.Cancel;
        (await vm.ConfirmDiscardUnsavedChangesAsync("close this window")).Should().BeTrue(
            "a second guard must pass straight through rather than prompting again");
        dialog.UnsavedPromptCount.Should().Be(1, "exactly one prompt for one user decision");
    }

    /// <summary>
    /// macOS Launch Services delivers a file activation whenever the user
    /// double-clicks a PDF in Finder while excise is already running — a
    /// document replacement, and before #1233 an unguarded one.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task FileActivation_WithUnsavedChanges_Cancelled_KeepsTheCurrentDocument()
    {
        var first = NewPdf("activation-first.pdf", "First");
        var second = NewPdf("activation-second.pdf", "Second");

        var (vm, dialog) = CreateViewModel();
        await vm.LoadDocumentAsync(first);
        await MakeDirtyAsync(vm);

        dialog.Decision = UnsavedChangesDecision.Cancel;
        await Excise.App.App.OpenPathAsync(
            vm, second, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        dialog.UnsavedPromptCount.Should().Be(1,
            "double-clicking a PDF in Finder replaces the open document, so it must ask");
        vm.FileState.CurrentFilePath.Should().Be(first,
            "cancelling must leave the current document open");

        vm.FileState.MarkSaved();
    }

    // ------------------------------------------------- Ctrl+W / document close

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task CloseDocumentCommand_WithUnsavedChanges_Cancelled_KeepsDocumentLoaded()
    {
        var source = NewPdf("ctrlw-cancel.pdf");
        var (vm, dialog) = CreateViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        await vm.LoadDocumentAsync(source);
        await MakeDirtyAsync(vm);

        dialog.Decision = UnsavedChangesDecision.Cancel;
        await vm.CloseDocumentCommand.Execute();

        dialog.UnsavedPromptCount.Should().Be(1, "Ctrl+W discards edits too, so it must ask");
        vm.PdfCoreDocument.Should().NotBeNull("cancelling must leave the document open");
        vm.HasUnsavedDocumentChanges.Should().BeTrue();

        vm.FileState.MarkSaved();   // let the window close during teardown
        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task CloseDocumentCommand_WithUnsavedChanges_Discard_ClosesTheDocument()
    {
        var source = NewPdf("ctrlw-discard.pdf");
        var (vm, dialog) = CreateViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        await vm.LoadDocumentAsync(source);
        await MakeDirtyAsync(vm);

        dialog.Decision = UnsavedChangesDecision.Discard;
        await vm.CloseDocumentCommand.Execute();

        vm.PdfCoreDocument.Should().BeNull("Discard must actually close the document");
        window.Close();
    }

    // ------------------------------------------------------ opening another file

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task LoadRecentFile_WithUnsavedChanges_Cancelled_KeepsTheCurrentDocument()
    {
        var first = NewPdf("first.pdf", "First document");
        var second = NewPdf("second.pdf", "Second document");

        var (vm, dialog) = CreateViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        await vm.LoadDocumentAsync(first);
        await MakeDirtyAsync(vm);

        dialog.Decision = UnsavedChangesDecision.Cancel;
        await vm.LoadRecentFileCommand.Execute(second);

        dialog.UnsavedPromptCount.Should().Be(1,
            "replacing the open document discards its edits just as a close does");
        vm.FileState.CurrentFilePath.Should().Be(first,
            "cancelling must leave the ORIGINAL document open, not swap it out anyway");
        vm.HasUnsavedDocumentChanges.Should().BeTrue();

        vm.FileState.MarkSaved();   // let the window close during teardown
        window.Close();
    }

    [FixedAvaloniaFact(Timeout = 30000)]
    public async Task LoadRecentFile_WithUnsavedChanges_Discard_SwitchesDocuments()
    {
        var first = NewPdf("first-d.pdf", "First document");
        var second = NewPdf("second-d.pdf", "Second document");

        var (vm, dialog) = CreateViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        await vm.LoadDocumentAsync(first);
        await MakeDirtyAsync(vm);

        dialog.Decision = UnsavedChangesDecision.Discard;
        await vm.LoadRecentFileCommand.Execute(second);

        vm.FileState.CurrentFilePath.Should().Be(second, "Discard must let the switch happen");
        window.Close();
    }
}
