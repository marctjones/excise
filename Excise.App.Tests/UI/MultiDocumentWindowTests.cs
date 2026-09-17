using System.Reactive.Linq;
using System.Text;
using AwesomeAssertions;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Excise.App.Models;
using Excise.App.ViewModels;
using Excise.App.Workspace;
using Moq;
using Xunit;
using Harness = Excise.App.Tests.UI.MultiDocumentSessionTests.Harness;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1552/#1553: documents in their own windows — the default open mode, the
/// Window menu, the preference, the window title, the macOS tabbing bridge's
/// pure parts, and the second-instance channel.
/// </summary>
[Collection("AvaloniaTests")]
public sealed class MultiDocumentWindowTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"excise-multiwin-{Guid.NewGuid():N}");

    public MultiDocumentWindowTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    private string NewPdf(string name, string text)
    {
        var path = Path.Combine(_tempDir, name);
        TestPdfGenerator.CreateSimpleTextPdf(path, text);
        return path;
    }

    private static IStorageItem MockStorageItem(string path)
    {
        var mock = new Mock<IStorageItem>();
        mock.Setup(f => f.Path).Returns(new Uri(new FileInfo(path).FullName));
        mock.Setup(f => f.Name).Returns(Path.GetFileName(path));
        return mock.Object;
    }

    // ------------------------------------------------------------ open mode

    [FixedAvaloniaFact]
    public void TheDefaultOpenMode_IsAutomatic_WhichOpensANewWindow()
    {
        new WindowSettings().DocumentOpenMode.Should().Be(nameof(DocumentOpenMode.Automatic));
        new PreferencesViewModel().SelectedDocumentOpenMode.Should().Be(DocumentOpenMode.Automatic);
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task WithThePersistedDefault_ASecondDocumentOpensInANewWindow()
    {
        using var harness = new Harness();
        var session = harness.Workspace.CreateSession();
        session.ViewModel.ThumbnailPrewarmEnabled = false;
        harness.Workspace.ShowInNewWindow(session);
        session.ViewModel.DocumentOpenMode.Should().Be(DocumentOpenMode.Automatic,
            "the window applies window.json, whose default is Automatic");

        await harness.Workspace.OpenDocumentsAsync([NewPdf("d1.pdf", "D1")], session);
        await harness.Workspace.OpenDocumentsAsync([NewPdf("d2.pdf", "D2")], session);

        harness.Workspace.Windows.Should().HaveCount(2);
        session.ViewModel.DocumentName.Should().Be("d1.pdf");
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task DroppingSeveralPdfs_OpensEachOne_AndIgnoresWhatIsNotAPdf()
    {
        using var harness = new Harness();
        var origin = harness.OpenWindow();
        var text = Path.Combine(_tempDir, "notes.txt");
        File.WriteAllText(text, "not a pdf");
        IReadOnlyList<IStorageItem> drop =
        [
            MockStorageItem(NewPdf("drop1.pdf", "DROP ONE")),
            MockStorageItem(text),
            MockStorageItem(NewPdf("drop2.pdf", "DROP TWO")),
        ];

        var opened = await origin.ViewModel.OpenDroppedFilesAsync(drop);

        opened.Should().BeTrue();
        harness.Workspace.Sessions.Select(s => s.ViewModel.DocumentName)
            .Should().BeEquivalentTo(new[] { "drop1.pdf", "drop2.pdf" });
        origin.ViewModel.DocumentName.Should().Be("drop1.pdf", "the empty window takes the first file");
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task AFileThatFailsToOpen_DoesNotLeaveAnEmptyWindowBehind()
    {
        using var harness = new Harness();
        var origin = harness.OpenWindow();
        await harness.Workspace.OpenDocumentsAsync([NewPdf("good.pdf", "GOOD")], origin);
        var broken = Path.Combine(_tempDir, "broken.pdf");
        File.WriteAllText(broken, "this is not a PDF at all");

        await harness.Workspace.OpenDocumentsAsync([broken], origin);
        await Task.Delay(50);

        harness.Workspace.Windows.Should().ContainSingle("the failed window was closed again");
        origin.ViewModel.DocumentName.Should().Be("good.pdf");
    }

    // ----------------------------------------------------------- window menu

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task WindowMenu_ListsEveryOpenDocument_MarksTheCurrentOne_AndActivatesOnClick()
    {
        using var harness = new Harness();
        var a = harness.OpenWindow();
        await harness.Workspace.OpenDocumentsAsync([NewPdf("menu-a.pdf", "A")], a);
        await harness.Workspace.OpenDocumentsAsync([NewPdf("menu-b.pdf", "B")], a);
        var b = harness.Workspace.Sessions.Single(s => !ReferenceEquals(s, a));
        await b.ViewModel.AddStickyNoteAnnotationCommand.Execute();

        var all = a.ViewModel.OpenDocumentMenuItems;
        all.Take(3).Select(i => i.Header).Should().Equal("Move Tab to New Window", "Merge All Windows", "-");
        all[0].IsEnabled.Should().BeFalse("a.pdf is its window's only tab");
        all[1].IsEnabled.Should().BeTrue("two windows are open");
        var items = all.Skip(3).ToList();

        items.Select(i => i.Header).Should().Equal("menu-a.pdf", "menu-b.pdf \u2022");
        items[0].IsChecked.Should().BeTrue();
        items[1].IsChecked.Should().BeFalse();
        AutomationProperties.GetName(items[1])
            .Should().Be("menu-b.pdf, unsaved changes");

        items[1].Command!.Execute(items[1].CommandParameter);
        harness.Workspace.ActiveSession.Should().BeSameAs(b);
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task WindowMenu_IsRebuiltWhenAnotherWindowOpensOrCloses()
    {
        using var harness = new Harness();
        var a = harness.OpenWindow();
        var raised = new List<string?>();
        a.ViewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var b = harness.OpenWindow();
        raised.Should().Contain(nameof(MainWindowViewModel.OpenDocumentMenuItems));
        a.ViewModel.OpenDocuments.Should().HaveCount(2);

        raised.Clear();
        b.Window!.Close();
        await Task.Delay(20);
        raised.Should().Contain(nameof(MainWindowViewModel.OpenDocumentMenuItems));
        a.ViewModel.OpenDocuments.Should().ContainSingle();
    }

    [FixedAvaloniaFact]
    public void WindowMenu_WithoutAWorkspace_ShowsOneDisabledPlaceholder()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);

        var items = vm.OpenDocumentMenuItems;

        items.Should().ContainSingle();
        items[0].IsEnabled.Should().BeFalse();
        items[0].Command.Should().BeNull();
    }

    [FixedAvaloniaFact]
    public void TheInWindowMenuBar_HasAWindowMenu()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new Views.MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        try
        {
            var menu = window.FindControl<MenuItem>("WindowMenu");
            menu.Should().NotBeNull();
            menu!.ItemsSource.Should().NotBeNull("the entries come from OpenDocumentMenuItems");
        }
        finally
        {
            window.Close();
        }
    }

    [FixedAvaloniaFact]
    public void TheMacNativeMenu_HasAWindowMenuWithTheTabActions()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var menu = Views.MacNativeMenuBuilder.Create(vm);

        var window = menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Window");
        window.Menu!.Items.OfType<NativeMenuItem>()
            .Where(i => i is not NativeMenuItemSeparator)
            .Select(i => i.Header)
            .Should().Equal(
                "Show Previous Tab",
                "Show Next Tab",
                "Move Tab to New Window",
                "Merge All Windows",
                "Show or Hide Tab Bar");
        window.Menu.Items.OfType<NativeMenuItem>()
            .Where(i => i is not NativeMenuItemSeparator)
            .Should().OnlyContain(i => i.Command != null && !i.IsEnabled,
                "each is a command, disabled while only one document is open");
    }

    // ------------------------------------------------------------ preference

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task TheOpenModePreference_IsSaved_AndReachesEveryWindow()
    {
        using var harness = new Harness();
        var a = harness.OpenWindow();
        var b = harness.OpenWindow();
        var preferences = new PreferencesViewModel();
        preferences.LoadFromMainViewModel(a.ViewModel);
        preferences.DocumentOpenModeOptions.Should().Contain(DocumentOpenMode.ReplaceCurrent);

        preferences.SelectedDocumentOpenMode = DocumentOpenMode.ReplaceCurrent;
        a.ViewModel.ApplySavedPreferences(preferences);

        b.ViewModel.DocumentOpenMode.Should().Be(DocumentOpenMode.ReplaceCurrent);
        harness.Settings.Current.DocumentOpenMode.Should().Be("ReplaceCurrent");

        preferences.ResetToDefaultsCommand.Execute().Subscribe();
        preferences.SelectedDocumentOpenMode.Should().Be(DocumentOpenMode.Automatic);
        await Task.CompletedTask;
    }

    [FixedAvaloniaTheory]
    [InlineData("NewWindow", DocumentOpenMode.NewWindow)]
    [InlineData("ReplaceCurrent", DocumentOpenMode.ReplaceCurrent)]
    [InlineData("nonsense", DocumentOpenMode.Automatic)]
    [InlineData("42", DocumentOpenMode.Automatic)]
    [InlineData(null, DocumentOpenMode.Automatic)]
    public void AnUnknownPersistedOpenMode_KeepsTheDefault(string? persisted, DocumentOpenMode expected)
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);

        vm.ApplyDocumentOpenModePreference(persisted);

        vm.DocumentOpenMode.Should().Be(expected);
    }

    // ---------------------------------------------------------- window title

    [Theory]
    [InlineData(null, false, true, "Excise")]
    [InlineData("", true, false, "Excise")]
    [InlineData("a.pdf", false, true, "a.pdf")]
    [InlineData("a.pdf", true, true, "a.pdf — Edited")]
    [InlineData("a.pdf", false, false, "a.pdf - Excise")]
    [InlineData("a.pdf", true, false, "*a.pdf - Excise")]
    public void TheWindowTitle_NamesTheDocument(string? name, bool dirty, bool mac, string expected) =>
        DocumentWindowTitle.For(name, dirty, mac).Should().Be(expected);

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task TheWindowTitle_FollowsTheDocumentAndItsUnsavedState()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new Views.MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        try
        {
            window.Title.Should().Be("Excise");
            await vm.LoadDocumentAsync(NewPdf("titled.pdf", "TITLE"));
            window.Title.Should().Be(DocumentWindowTitle.For("titled.pdf", false));

            await vm.AddStickyNoteAnnotationCommand.Execute();
            window.Title.Should().Be(DocumentWindowTitle.For("titled.pdf", true));
        }
        finally
        {
            vm.FileState.MarkSaved();
            window.Close();
        }
    }

    // ------------------------------------------------------- macOS tabbing

    [Theory]
    [InlineData(1L, false, true)]
    [InlineData(1L, true, true)]
    [InlineData(0L, false, false)]
    [InlineData(0L, true, false)]
    [InlineData(2L, false, false)]
    [InlineData(2L, true, true)]
    public void OpeningAsATab_FollowsTheSystemPreference(long preference, bool fullScreen, bool expected)
    {
        // Always = 1, Manual = 0, InFullScreen = 2 (NSWindowUserTabbingPreference).
        ((long)MacWindowTabbing.UserPreferenceAlways).Should().Be(1);
        ((long)MacWindowTabbing.UserPreferenceInFullScreen).Should().Be(2);
        MacWindowTabbing.PrefersTabs((nint)preference, fullScreen).Should().Be(expected);
    }

    [Fact]
    public void EveryTabAction_MapsToTheAppKitSelector()
    {
        Enum.GetValues<MacWindowTabbing.TabAction>()
            .Select(MacWindowTabbing.SelectorFor)
            .Should().Equal(
                "mergeAllWindows:",
                "moveTabToNewWindow:",
                "toggleTabBar:",
                "selectNextTab:",
                "selectPreviousTab:");
    }

    [FixedAvaloniaFact]
    public void TheTabbingBridge_DoesNothing_OnAWindowThatIsNotANativeMacWindow()
    {
        var window = new Window { Width = 200, Height = 100 };
        window.Show();
        try
        {
            MacWindowTabbing.Prepare(window, null).Should().BeFalse(
                "the headless platform's handle is not an NSWindow and must never be messaged");
            MacWindowTabbing.JoinTabGroupIfPreferred(window, window, null).Should().BeFalse();
        }
        finally
        {
            window.Close();
        }
    }

    // ----------------------------------------------- second-instance channel

    private static string UniquePipeName() => "ex-t-" + Guid.NewGuid().ToString("N")[..10];

    [Fact(Timeout = 30000)]
    public async Task ASecondLaunch_HandsItsPdfsToTheRunningInstance()
    {
        var pdf = NewPdf("forwarded.pdf", "FORWARDED");
        var name = UniquePipeName();
        var received = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = SingleInstanceChannel.Server.TryStart(name, paths => received.TrySetResult(paths), null);
        server.Should().NotBeNull();

        var text = Path.Combine(_tempDir, "not-a-pdf.txt");
        File.WriteAllText(text, "x");
        var forwarded = await Task.Run(() =>
            SingleInstanceChannel.TryForward(name, [pdf, text], TimeSpan.FromSeconds(5)));

        forwarded.Should().BeTrue("the running instance acknowledged the request");
        var paths = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        paths.Should().Equal(Path.GetFullPath(pdf));
    }

    [Fact(Timeout = 30000)]
    public async Task WithNoRunningInstance_TheLaunchStartsNormally()
    {
        var forwarded = await Task.Run(() =>
            SingleInstanceChannel.TryForward(UniquePipeName(), ["/tmp/x.pdf"], TimeSpan.FromMilliseconds(200)));

        forwarded.Should().BeFalse();
    }

    [Theory]
    [InlineData("excise-open/1\n/a.pdf\n\n", true, 1)]
    [InlineData("excise-open/1\n\n", true, 0)]
    [InlineData("excise-open/2\n/a.pdf\n\n", false, 0)]
    [InlineData("excise-open/1\n/a.pdf\n", false, 0)]
    [InlineData("", false, 0)]
    public async Task TheChannel_RejectsARequestThatBreaksTheProtocol(string request, bool accepted, int count)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request));

        var paths = await SingleInstanceChannel.ReadRequestAsync(stream, CancellationToken.None);

        if (!accepted)
        {
            paths.Should().BeNull();
            return;
        }
        paths.Should().NotBeNull().And.HaveCount(count);
    }

    [Fact]
    public async Task TheChannel_RejectsOverlongLinesAndTooManyPaths()
    {
        var longLine = "excise-open/1\n" + new string('a', SingleInstanceChannel.MaxLineLength + 1) + "\n\n";
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(longLine)))
            (await SingleInstanceChannel.ReadRequestAsync(stream, CancellationToken.None)).Should().BeNull();

        var many = new StringBuilder("excise-open/1\n");
        for (var i = 0; i <= SingleInstanceChannel.MaxPaths; i++)
            many.Append("/p").Append(i).Append(".pdf\n");
        many.Append('\n');
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(many.ToString())))
            (await SingleInstanceChannel.ReadRequestAsync(stream, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public void ThePipeName_IsShortAndStablePerUser()
    {
        var name = SingleInstanceChannel.DefaultPipeName();
        name.Should().Be(SingleInstanceChannel.DefaultPipeName());
        name.Should().StartWith("excise-open-");
        name.Length.Should().BeLessThan(32, "on Linux it becomes a socket path with a ~100 byte limit");
    }

    [Fact]
    public void OnMacOS_TheChannelIsOff_BecauseLaunchServicesAlreadyRoutesDocuments()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Skip("macOS-only property; the channel is on by default elsewhere");
        SingleInstanceChannel.IsEnabled.Should().BeFalse();
        SingleInstanceChannel.TryForwardStartupDocuments(["/tmp/any.pdf"]).Should().BeFalse();
    }
}
