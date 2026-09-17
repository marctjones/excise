using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Avalonia.Threading;
using Excise.App.Composition;
using Excise.App.Models;
using Excise.App.Services;
using Excise.App.Services.Host;
using Excise.App.Services.Printing;
using Excise.App.Tests.Utilities.Fakes;
using Excise.App.ViewModels;
using Excise.App.Workspace;
using Excise.Core.Document;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1551: two document sessions in one process, built by the PRODUCTION
/// composition root (one DI scope per session), shown in real headless
/// windows. Design: docs/architecture/main-window-architecture.md §7.
/// </summary>
[Collection("AvaloniaTests")]
public sealed class MultiDocumentSessionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"excise-multidoc-{Guid.NewGuid():N}");

    public MultiDocumentSessionTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    internal sealed class ScriptedDialogService : IUserDialogService
    {
        public List<string> UnsavedPrompts { get; } = new();
        public Queue<UnsavedChangesDecision> Decisions { get; } = new();

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<UnsavedChangesDecision> ShowUnsavedChangesAsync(
            string title, string message, string saveActionText)
        {
            UnsavedPrompts.Add(message);
            return Task.FromResult(Decisions.Count > 0 ? Decisions.Dequeue() : UnsavedChangesDecision.Cancel);
        }
    }

    private sealed class InMemoryRecentFilesStore : IRecentFilesStore
    {
        private List<string> _paths = new();
        public IReadOnlyList<string> Load() => _paths.ToArray();
        public void Save(IEnumerable<string> paths) => _paths = paths.ToList();
    }

    internal sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _provider;

        internal Harness(IFilePicker? picker = null)
        {
            Dialog = new ScriptedDialogService();
            Settings = new InMemorySettingsStore();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddExciseApplicationServices();
            services.AddSingleton<IUserDialogService>(Dialog);
            services.AddSingleton<ISettingsStore>(Settings);
            services.AddSingleton<IRecentFilesStore>(new InMemoryRecentFilesStore());
            services.AddSingleton(new ReleasedMemoryReclaimer(collect: static _ => { }));
            services.AddSingleton<IDocumentPrinter>(new RecordingDocumentPrinter());
            if (picker != null)
                services.AddScoped<IFilePicker>(_ => picker);
            _provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

            Workspace = _provider.GetRequiredService<DocumentWorkspace>();
            Workspace.RequestShutdown = () => ShutdownRequests++;
        }

        internal ScriptedDialogService Dialog { get; }
        internal InMemorySettingsStore Settings { get; }
        internal DocumentWorkspace Workspace { get; }
        internal int ShutdownRequests { get; private set; }

        internal DocumentSession OpenWindow(DocumentOpenMode mode = DocumentOpenMode.NewWindow)
        {
            var session = Workspace.CreateSession();
            session.ViewModel.ThumbnailPrewarmEnabled = false;
            var window = Workspace.ShowInNewWindow(session);
            window.Width = 1000;
            window.Height = 700;
            // The window applies window.json (the in-memory store here) on
            // bind; the mode under test is set after that.
            session.ViewModel.DocumentOpenMode = mode;
            return session;
        }

        public void Dispose()
        {
            foreach (var window in Workspace.Windows.ToArray())
            {
                foreach (var session in Workspace.SessionsIn(window))
                    session.ViewModel.FileState.MarkSaved();
                window.Close();
            }

            _provider.Dispose();
        }
    }

    private string NewPdf(string name, string text)
    {
        var path = Path.Combine(_tempDir, name);
        TestPdfGenerator.CreateSimpleTextPdf(path, text);
        return path;
    }

    private static async Task FlushAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(20);
        }
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task TwoSessions_KeepIndependentDocumentUndoRedactionAndUnsavedState()
    {
        using var harness = new Harness();
        var a = harness.OpenWindow();
        var b = harness.OpenWindow();
        await a.ViewModel.LoadDocumentAsync(NewPdf("a.pdf", "ALPHA DOCUMENT"));
        await b.ViewModel.LoadDocumentAsync(NewPdf("b.pdf", "BRAVO DOCUMENT"));

        a.ViewModel.PdfCoreDocument.Should().NotBeNull();
        a.ViewModel.PdfCoreDocument.Should().NotBeSameAs(b.ViewModel.PdfCoreDocument);
        a.ViewModel.DocumentName.Should().Be("a.pdf");
        b.ViewModel.DocumentName.Should().Be("b.pdf");

        // Undo and unsaved state: an edit in A is A's alone.
        await a.ViewModel.AddStickyNoteAnnotationCommand.Execute();
        a.ViewModel.HasUnsavedDocumentChanges.Should().BeTrue();
        a.ViewModel.CanUndo.Should().BeTrue();
        b.ViewModel.HasUnsavedDocumentChanges.Should().BeFalse("B was not edited");
        b.ViewModel.CanUndo.Should().BeFalse("undo history is per document");

        // Redaction workflow: a mark in B is B's alone.
        b.ViewModel.IsRedactionMode = true;
        b.ViewModel.CurrentRedactionPageArea =
            PdfPageRect.FromContentPoints(1, new PdfRectangle(40, 660, 500, 740));
        await b.ViewModel.ApplyRedactionCommand!.Execute();
        b.ViewModel.RedactionWorkflow.PendingCount.Should().Be(1);
        a.ViewModel.RedactionWorkflow.PendingCount.Should().Be(0);
        a.ViewModel.IsRedactionMode.Should().BeFalse("interaction modes are per document");

        // Closing A's document leaves B's untouched.
        a.ViewModel.FileState.MarkSaved();
        a.Window!.Close();
        await FlushAsync();
        a.IsDisposed.Should().BeTrue();
        b.ViewModel.IsDocumentLoaded.Should().BeTrue();
        b.ViewModel.PdfCoreDocument!.GetPage(1).Text.Should().Contain("BRAVO");
        harness.Workspace.Sessions.Should().ContainSingle().Which.Should().BeSameAs(b);
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task EachSession_OwnsItsDialogsThroughItsOwnWindow()
    {
        using var harness = new Harness();
        var a = harness.OpenWindow();
        var b = harness.OpenWindow();

        a.WindowHost.MainWindow.Should().BeSameAs(a.Window);
        b.WindowHost.MainWindow.Should().BeSameAs(b.Window);
        a.ViewModel.MainWindowResolver().Should().BeSameAs(a.Window,
            "the view model's owner lookup forwards to the session's host");
        await Task.CompletedTask;
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task ReplaceMode_ReplacesTheDocument_LikeTheSingleDocumentApp()
    {
        using var harness = new Harness();
        var session = harness.OpenWindow(DocumentOpenMode.ReplaceCurrent);
        var first = NewPdf("first.pdf", "FIRST");
        var second = NewPdf("second.pdf", "SECOND");
        await harness.Workspace.OpenDocumentsAsync([first], session);
        session.ViewModel.DocumentName.Should().Be("first.pdf");

        await harness.Workspace.OpenDocumentsAsync([second], session);

        harness.Workspace.Sessions.Should().ContainSingle();
        harness.Workspace.Windows.Should().ContainSingle();
        session.ViewModel.DocumentName.Should().Be("second.pdf");
        harness.Dialog.UnsavedPrompts.Should().BeEmpty("nothing was dirty");
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task ReplaceMode_AsksBeforeDiscarding_AndCancelKeepsTheDocument()
    {
        using var harness = new Harness();
        var session = harness.OpenWindow(DocumentOpenMode.ReplaceCurrent);
        await harness.Workspace.OpenDocumentsAsync([NewPdf("keep.pdf", "KEEP")], session);
        await session.ViewModel.AddStickyNoteAnnotationCommand.Execute();

        harness.Dialog.Decisions.Enqueue(UnsavedChangesDecision.Cancel);
        await harness.Workspace.OpenDocumentsAsync([NewPdf("other.pdf", "OTHER")], session);

        harness.Dialog.UnsavedPrompts.Should().ContainSingle();
        session.ViewModel.DocumentName.Should().Be("keep.pdf");
        session.ViewModel.HasUnsavedDocumentChanges.Should().BeTrue();
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task NewWindowMode_OpensEachFileInItsOwnWindow_WithoutAskingAboutUnsavedChanges()
    {
        using var harness = new Harness();
        var origin = harness.OpenWindow();
        var first = NewPdf("one.pdf", "ONE");
        var second = NewPdf("two.pdf", "TWO");
        var third = NewPdf("three.pdf", "THREE");

        await harness.Workspace.OpenDocumentsAsync([first], origin);
        origin.ViewModel.DocumentName.Should().Be("one.pdf", "an empty window takes the file itself");
        await origin.ViewModel.AddStickyNoteAnnotationCommand.Execute();

        await harness.Workspace.OpenDocumentsAsync([second, third], origin);

        harness.Dialog.UnsavedPrompts.Should().BeEmpty("nothing is replaced, so nothing is discarded");
        harness.Workspace.Windows.Should().HaveCount(3);
        harness.Workspace.Sessions.Select(s => s.ViewModel.DocumentName)
            .Should().BeEquivalentTo(new[] { "one.pdf", "two.pdf", "three.pdf" });
        origin.ViewModel.HasUnsavedDocumentChanges.Should().BeTrue("the origin's edit survives");
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task OpeningAFileThatIsAlreadyOpen_BringsItsWindowForward_InsteadOfOpeningItTwice()
    {
        using var harness = new Harness();
        var origin = harness.OpenWindow();
        var path = NewPdf("once.pdf", "ONCE");
        var other = NewPdf("other.pdf", "OTHER");
        await harness.Workspace.OpenDocumentsAsync([path], origin);
        await harness.Workspace.OpenDocumentsAsync([other], origin);
        var otherSession = harness.Workspace.FindSessionShowing(other);
        otherSession.Should().NotBeNull();

        await harness.Workspace.OpenDocumentsAsync([path], otherSession);

        harness.Workspace.Sessions.Should().HaveCount(2);
        harness.Workspace.ActiveSession.Should().BeSameAs(origin);
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task OpenFileCommand_InAWindowWithADocument_OpensTheChoiceInANewWindow()
    {
        var picked = NewPdf("picked.pdf", "PICKED");
        var picker = new RecordingFilePicker().WillOpen(picked);
        using var harness = new Harness(picker);
        var origin = harness.OpenWindow();
        await harness.Workspace.OpenDocumentsAsync([NewPdf("base.pdf", "BASE")], origin);
        await origin.ViewModel.AddStickyNoteAnnotationCommand.Execute();

        await origin.ViewModel.OpenFileCommand.Execute();

        picker.OpenRequests.Should().ContainSingle()
            .Which.AllowMultiple.Should().BeTrue("a workspace can open several files at once");
        harness.Dialog.UnsavedPrompts.Should().BeEmpty();
        harness.Workspace.FindSessionShowing(picked).Should().NotBeNull().And.NotBeSameAs(origin);
        origin.ViewModel.DocumentName.Should().Be("base.pdf");
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task CloseDocument_WithAnotherSessionOpen_ClosesTheWindow_AndTheLastKeepsItsEmptyWindow()
    {
        using var harness = new Harness();
        var a = harness.OpenWindow();
        await harness.Workspace.OpenDocumentsAsync([NewPdf("close-a.pdf", "A")], a);
        await harness.Workspace.OpenDocumentsAsync([NewPdf("close-b.pdf", "B")], a);
        var b = harness.Workspace.Sessions.Single(s => !ReferenceEquals(s, a));

        await b.ViewModel.CloseDocumentCommand.Execute();
        await FlushAsync();

        b.IsDisposed.Should().BeTrue();
        harness.Workspace.Windows.Should().ContainSingle();

        await a.ViewModel.CloseDocumentCommand.Execute();
        await FlushAsync();

        a.IsDisposed.Should().BeFalse("the last window stays open, empty, as before #1463");
        a.ViewModel.IsDocumentLoaded.Should().BeFalse();
        harness.Workspace.Windows.Should().ContainSingle();
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task Quit_ReviewsEveryDirtySession_AndACancelKeepsEverythingOpen()
    {
        using var harness = new Harness();
        var a = harness.OpenWindow();
        await harness.Workspace.OpenDocumentsAsync([NewPdf("quit-a.pdf", "A")], a);
        await harness.Workspace.OpenDocumentsAsync([NewPdf("quit-b.pdf", "B")], a);
        var b = harness.Workspace.Sessions.Single(s => !ReferenceEquals(s, a));
        await a.ViewModel.AddStickyNoteAnnotationCommand.Execute();
        await b.ViewModel.AddStickyNoteAnnotationCommand.Execute();

        // Discard A, cancel at B: the quit is abandoned and B keeps its edit.
        harness.Dialog.Decisions.Enqueue(UnsavedChangesDecision.Discard);
        harness.Dialog.Decisions.Enqueue(UnsavedChangesDecision.Cancel);
        await a.ViewModel.ExitCommand.Execute();

        harness.Dialog.UnsavedPrompts.Should().HaveCount(2, "both dirty documents were asked about");
        harness.ShutdownRequests.Should().Be(0);
        b.ViewModel.HasUnsavedDocumentChanges.Should().BeTrue();

        // Discard B too: now the quit goes through, without asking about A again.
        harness.Dialog.Decisions.Enqueue(UnsavedChangesDecision.Discard);
        await b.ViewModel.ExitCommand.Execute();

        harness.Dialog.UnsavedPrompts.Should().HaveCount(3);
        harness.ShutdownRequests.Should().Be(1);
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task SavedPreferences_ReachEveryOpenSession()
    {
        using var harness = new Harness();
        var a = harness.OpenWindow();
        var b = harness.OpenWindow();
        b.ViewModel.LinkUriCarrierPolicy.Should().Be(Excise.Core.Operations.CarrierScrubMode.Strip);

        var preferences = new PreferencesViewModel();
        preferences.LoadFromMainViewModel(a.ViewModel);
        preferences.SelectedLinkUriCarrierPolicy = Excise.Core.Operations.CarrierScrubMode.RemoveWhole;
        preferences.RedactionWholeWord = true;
        a.ViewModel.ApplySavedPreferences(preferences);

        b.ViewModel.LinkUriCarrierPolicy.Should().Be(Excise.Core.Operations.CarrierScrubMode.RemoveWhole,
            "a redaction policy must never differ between two open windows");
        b.ViewModel.RedactionWholeWord.Should().BeTrue();
        harness.Settings.Current.LinkUriCarrierPolicy.Should().Be("RemoveWhole");
        await Task.CompletedTask;
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task RecentFiles_AreOneListForTheWholeApplication()
    {
        using var harness = new Harness();
        var a = harness.OpenWindow();
        var b = harness.OpenWindow();
        var path = NewPdf("recent.pdf", "RECENT");

        await a.ViewModel.LoadDocumentAsync(path);

        b.ViewModel.RecentFiles.Should().Contain(path);
        b.ViewModel.HasRecentFiles.Should().BeTrue();
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task ClosingAWindow_ReleasesItsSession()
    {
        using var harness = new Harness();
        var keep = harness.OpenWindow();
        var released = await OpenAndCloseSecondSessionAsync(harness, NewPdf("released.pdf", "RELEASED"));

        for (var i = 0; i < 10 && released.IsAlive; i++)
        {
            await FlushAsync();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        released.IsAlive.Should().BeFalse(
            "a closed document's view model (and the document it holds) must be collectable");
        keep.IsDisposed.Should().BeFalse();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> OpenAndCloseSecondSessionAsync(Harness harness, string path)
    {
        var session = harness.OpenWindow();
        await session.ViewModel.LoadDocumentAsync(path);
        await session.ViewModel.GoToPageCommand.Execute(0);
        var reference = new WeakReference(session.ViewModel);
        session.Window!.Close();
        await FlushAsync();
        session.IsDisposed.Should().BeTrue();
        return reference;
    }
}
