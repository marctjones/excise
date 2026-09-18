using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Threading;
using Excise.App.Models;
using Excise.App.Services;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.App.Workspace;
using Xunit;
using Harness = Excise.App.Tests.UI.MultiDocumentSessionTests.Harness;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1554: in-app document tabs. One window, one viewer, several sessions.
/// </summary>
[Collection("AvaloniaTests")]
public sealed class MultiDocumentTabTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"excise-multitab-{Guid.NewGuid():N}");

    public MultiDocumentTabTests() => Directory.CreateDirectory(_tempDir);

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

    private string NewMultiPagePdf(string name, int pages)
    {
        var path = Path.Combine(_tempDir, name);
        TestPdfGenerator.CreateMultiPagePdf(path, pages);
        return path;
    }

    private static async Task FlushAsync()
    {
        for (var i = 0; i < 6; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await Task.Delay(20);
        }
    }

    /// <summary>A window in NewTab mode showing <paramref name="paths"/> as tabs.</summary>
    private static async Task<(DocumentSession First, MainWindow Window, DocumentTabsViewModel Tabs)> OpenTabsAsync(
        Harness harness, params string[] paths)
    {
        var first = harness.OpenWindow(DocumentOpenMode.NewTab);
        foreach (var path in paths)
        {
            await harness.Workspace.OpenDocumentsAsync([path], harness.Workspace.ActiveSession);
            foreach (var session in harness.Workspace.Sessions)
                session.ViewModel.DocumentOpenMode = DocumentOpenMode.NewTab;
        }
        var window = (MainWindow)first.Window!;
        await FlushAsync();
        return (first, window, window.DocumentTabs!);
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task NewTabMode_OpensTheSecondDocumentAsATabOfTheSameWindow_AndShowsIt()
    {
        using var harness = new Harness();
        var (first, window, tabs) = await OpenTabsAsync(harness, NewPdf("t1.pdf", "ONE"), NewPdf("t2.pdf", "TWO"));

        harness.Workspace.Windows.Should().ContainSingle();
        tabs.Tabs.Select(t => t.Title).Should().Equal("t1.pdf", "t2.pdf");
        tabs.IsStripVisible.Should().BeTrue();
        tabs.SelectedTab!.Title.Should().Be("t2.pdf");
        window.DataContext.Should().BeSameAs(tabs.SelectedTab.Session.ViewModel);
        window.DataContext.Should().NotBeSameAs(first.ViewModel);
        harness.Workspace.ActiveSession!.ViewModel.DocumentName.Should().Be("t2.pdf");
        first.Window.Should().BeSameAs(tabs.SelectedTab.Session.Window, "both sessions are hosted by the one window");
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task ASingleDocument_ShowsNoTabStrip()
    {
        using var harness = new Harness();
        var (_, _, tabs) = await OpenTabsAsync(harness, NewPdf("alone.pdf", "ALONE"));

        tabs.Tabs.Should().ContainSingle();
        tabs.IsStripVisible.Should().BeFalse("one document needs no tabs, and the viewer keeps the space");
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task SwitchingTabs_KeepsEachDocumentsPageUndoAndMarks()
    {
        using var harness = new Harness();
        var (_, window, tabs) = await OpenTabsAsync(harness,
            NewMultiPagePdf("pages-a.pdf", 4), NewMultiPagePdf("pages-b.pdf", 3));
        var a = tabs.Tabs[0].Session.ViewModel;
        var b = tabs.Tabs[1].Session.ViewModel;

        tabs.SelectedTab = tabs.Tabs[0];
        await FlushAsync();
        a.ViewMode = Excise.Avalonia.Controls.PdfViewMode.SinglePage;
        await a.GoToPageCommand.Execute(2);
        await a.AddStickyNoteAnnotationCommand.Execute();
        await FlushAsync();

        tabs.SelectedTab = tabs.Tabs[1];
        await FlushAsync();
        window.DataContext.Should().BeSameAs(b);
        b.CanUndo.Should().BeFalse();
        b.HasUnsavedDocumentChanges.Should().BeFalse();

        tabs.SelectedTab = tabs.Tabs[0];
        await FlushAsync();
        window.DataContext.Should().BeSameAs(a);
        a.CurrentPageIndex.Should().Be(2, "a tab switch must not reset the page");
        a.CanUndo.Should().BeTrue();
        a.HasUnsavedDocumentChanges.Should().BeTrue();
        tabs.Tabs[0].DisplayTitle.Should().Be("pages-a.pdf •");
        tabs.Tabs[0].AccessibleName.Should().Be("pages-a.pdf, tab 1 of 2, unsaved changes");
        tabs.Tabs[1].AccessibleName.Should().Be("pages-b.pdf, tab 2 of 2");
    }

    /// <summary>
    /// ⚠️ This passes on macOS and the feature does NOT (#1598).
    /// <c>PressKeyAsync</c> hands Avalonia a synthetic key event, so it exercises
    /// <c>MainWindow.OnTabSwitchKeyDown</c> — the real path on Windows and Linux.
    /// A REAL Control-Tab on macOS is taken by AppKit as a key-view /
    /// key-equivalent keystroke and never reaches that handler, which is why the
    /// gesture also lives on the native Window menu
    /// (<see cref="TheNativeWindowMenu_SwitchesThisWindowsTabs_WithSafarisKeyEquivalents"/>).
    /// Nothing in a headless test can tell the two apart; only a CGEvent against
    /// a real bundle can.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task CtrlTab_AndCtrlPageUp_SwitchTabs()
    {
        using var harness = new Harness();
        var (_, window, tabs) = await OpenTabsAsync(harness,
            NewPdf("k1.pdf", "K1"), NewPdf("k2.pdf", "K2"), NewPdf("k3.pdf", "K3"));
        tabs.SelectedTab!.Title.Should().Be("k3.pdf");

        await window.PressKeyAsync(Key.Tab, RawInputModifiers.Control);
        tabs.SelectedTab!.Title.Should().Be("k1.pdf", "Ctrl+Tab wraps to the first tab");

        await window.PressKeyAsync(Key.PageUp, RawInputModifiers.Control);
        tabs.SelectedTab!.Title.Should().Be("k3.pdf");

        await window.PressKeyAsync(Key.Tab, RawInputModifiers.Control | RawInputModifiers.Shift);
        tabs.SelectedTab!.Title.Should().Be("k2.pdf");
        window.DataContext.Should().BeSameAs(tabs.SelectedTab.Session.ViewModel);
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task ClosingATab_KeepsTheWindow_ShowsTheNeighbour_AndReleasesTheSession()
    {
        using var harness = new Harness();
        var (_, window, tabs) = await OpenTabsAsync(harness,
            NewPdf("c1.pdf", "C1"), NewPdf("c2.pdf", "C2"), NewPdf("c3.pdf", "C3"));
        tabs.SelectedTab = tabs.Tabs[1];
        var closing = tabs.Tabs[1].Session;

        await tabs.Tabs[1].CloseCommand.Execute();
        await FlushAsync();

        closing.IsDisposed.Should().BeTrue();
        harness.Workspace.Windows.Should().ContainSingle();
        tabs.Tabs.Select(t => t.Title).Should().Equal("c1.pdf", "c3.pdf");
        tabs.SelectedTab!.Title.Should().Be("c3.pdf", "the right-hand neighbour is shown");
        window.DataContext.Should().BeSameAs(tabs.SelectedTab.Session.ViewModel);
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task ClosingADirtyTab_Asks_AndCancelKeepsIt()
    {
        using var harness = new Harness();
        var (_, _, tabs) = await OpenTabsAsync(harness, NewPdf("d1.pdf", "D1"), NewPdf("d2.pdf", "D2"));
        var dirty = tabs.Tabs[0];
        await dirty.Session.ViewModel.AddStickyNoteAnnotationCommand.Execute();

        harness.Dialog.Decisions.Enqueue(UnsavedChangesDecision.Cancel);
        await dirty.CloseCommand.Execute();

        harness.Dialog.UnsavedPrompts.Should().ContainSingle();
        tabs.Tabs.Should().HaveCount(2);
        dirty.Session.IsDisposed.Should().BeFalse();
        tabs.SelectedTab.Should().BeSameAs(dirty, "the tab being asked about is brought forward");
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task ClosingTheWindow_AsksAboutEveryDirtyTab()
    {
        using var harness = new Harness();
        var (_, window, tabs) = await OpenTabsAsync(harness, NewPdf("w1.pdf", "W1"), NewPdf("w2.pdf", "W2"));
        foreach (var tab in tabs.Tabs)
            await tab.Session.ViewModel.AddStickyNoteAnnotationCommand.Execute();

        harness.Dialog.Decisions.Enqueue(UnsavedChangesDecision.Discard);
        harness.Dialog.Decisions.Enqueue(UnsavedChangesDecision.Cancel);
        window.Close();
        await FlushAsync();

        harness.Dialog.UnsavedPrompts.Should().HaveCount(2);
        harness.Workspace.Windows.Should().ContainSingle("the second answer kept the window");
        tabs.Tabs.Should().HaveCount(2);
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task MoveTabToNewWindow_GivesTheSessionItsOwnWindow_WithItsStateIntact()
    {
        using var harness = new Harness();
        var (_, window, tabs) = await OpenTabsAsync(harness, NewPdf("m1.pdf", "M1"), NewPdf("m2.pdf", "M2"));
        var moving = tabs.Tabs[1].Session;
        await moving.ViewModel.AddStickyNoteAnnotationCommand.Execute();

        await tabs.Tabs[1].MoveToNewWindowCommand.Execute();
        await FlushAsync();

        harness.Workspace.Windows.Should().HaveCount(2);
        tabs.Tabs.Should().ContainSingle().Which.Title.Should().Be("m1.pdf");
        moving.IsDisposed.Should().BeFalse();
        moving.Window.Should().NotBeSameAs(window);
        moving.Window!.DataContext.Should().BeSameAs(moving.ViewModel);
        moving.WindowHost.MainWindow.Should().BeSameAs(moving.Window, "its dialogs follow it to the new window");
        moving.ViewModel.CanUndo.Should().BeTrue("undo moves with the tab");
        moving.ViewModel.HasUnsavedDocumentChanges.Should().BeTrue();
        window.DataContext.Should().BeSameAs(tabs.Tabs[0].Session.ViewModel);
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task MergeAllWindows_GathersEveryDocumentAsTabs_WithoutClosingAny()
    {
        using var harness = new Harness();
        var first = harness.OpenWindow();
        await harness.Workspace.OpenDocumentsAsync([NewPdf("g1.pdf", "G1")], first);
        await harness.Workspace.OpenDocumentsAsync([NewPdf("g2.pdf", "G2")], first);
        await harness.Workspace.OpenDocumentsAsync([NewPdf("g3.pdf", "G3")], first);
        harness.Workspace.Windows.Should().HaveCount(3);
        foreach (var session in harness.Workspace.Sessions)
            await session.ViewModel.AddStickyNoteAnnotationCommand.Execute();

        await first.ViewModel.MergeAllWindowsCommand.Execute();
        await FlushAsync();

        harness.Dialog.UnsavedPrompts.Should().BeEmpty("merging closes no document");
        harness.Workspace.Windows.Should().ContainSingle();
        harness.Workspace.Sessions.Should().HaveCount(3).And.OnlyContain(s => !s.IsDisposed);
        var tabs = ((MainWindow)first.Window!).DocumentTabs!;
        tabs.Tabs.Select(t => t.Title).Should().Equal("g1.pdf", "g2.pdf", "g3.pdf");
        harness.Workspace.Sessions.Should().OnlyContain(s => ReferenceEquals(s.Window, first.Window));
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task TheOverflowList_OffersEveryTab()
    {
        using var harness = new Harness();
        var (_, _, tabs) = await OpenTabsAsync(harness, NewPdf("o1.pdf", "O1"), NewPdf("o2.pdf", "O2"));

        var items = tabs.OverflowMenuItems;

        items.Select(i => i.Header).Should().Equal("o1.pdf", "o2.pdf");
        items[1].IsChecked.Should().BeTrue();
        items[0].Command!.Execute(null);
        tabs.SelectedTab!.Title.Should().Be("o1.pdf");
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task DragReorder_MovesTheTab_AndPositionsAreReannounced()
    {
        using var harness = new Harness();
        var (_, _, tabs) = await OpenTabsAsync(harness,
            NewPdf("r1.pdf", "R1"), NewPdf("r2.pdf", "R2"), NewPdf("r3.pdf", "R3"));

        tabs.Move(2, 0);

        tabs.Tabs.Select(t => t.Title).Should().Equal("r3.pdf", "r1.pdf", "r2.pdf");
        tabs.Tabs[0].AccessibleName.Should().Be("r3.pdf, tab 1 of 3");
        tabs.Move(0, 5); // out of range: ignored
        tabs.Tabs.Select(t => t.Title).Should().Equal("r3.pdf", "r1.pdf", "r2.pdf");
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task TheTabStrip_AnswersRealPointerInput()
    {
        // Real pointer events, not command calls: a collapsed, covered or
        // unbound tab fails here and nowhere else (gui-interaction-coverage).
        using var harness = new Harness();
        var (_, window, tabs) = await OpenTabsAsync(harness,
            NewPdf("p1.pdf", "P1"), NewPdf("p2.pdf", "P2"), NewPdf("p3.pdf", "P3"));
        tabs.SelectedTab!.Title.Should().Be("p3.pdf");

        Button TabButton(string title) => window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Classes.Contains("document-tab-button")
                         && b.DataContext is DocumentTabViewModel t && t.Title == title);
        Button CloseButton(string title) => window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Classes.Contains("document-tab-close")
                         && b.DataContext is DocumentTabViewModel t && t.Title == title);

        await PointerClickAsync(window, TabButton("p1.pdf"));
        tabs.SelectedTab!.Title.Should().Be("p1.pdf", "clicking a tab shows its document");

        // Drag p1 onto p3: the strip reorders, and the release does not re-select.
        await PointerDragAsync(window, TabButton("p1.pdf"), TabButton("p3.pdf"));
        tabs.Tabs.Select(t => t.Title).Should().Equal(["p2.pdf", "p3.pdf", "p1.pdf"], "dragging a tab onto another reorders");
        tabs.SelectedTab!.Title.Should().Be("p1.pdf", "a drag does not change the shown document");

        await PointerClickAsync(window, TabButton("p2.pdf"), MouseButton.Middle);
        tabs.Tabs.Select(t => t.Title).Should().Equal(["p3.pdf", "p1.pdf"], "middle-click closes a tab");

        await PointerClickAsync(window, CloseButton("p3.pdf"));
        tabs.Tabs.Should().ContainSingle("the close button closes its tab").Which.Title.Should().Be("p1.pdf");

        // One tab left: the strip hides. Re-open a second tab for the list button.
        await harness.Workspace.OpenDocumentsAsync([NewPdf("p4.pdf", "P4")], harness.Workspace.ActiveSession);
        await FlushAsync();
        var overflow = window.FindControl<DocumentTabStrip>("DocumentTabStripHost")!
            .FindControl<Button>("DocumentTabsOverflowButton")!;
        await PointerClickAsync(window, overflow);
        overflow.Flyout!.IsOpen.Should().BeTrue("the list button opens the list of every tab");
        overflow.Flyout.Hide();
        await FlushAsync();
    }

    private static Point CenterIn(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
        ?? throw new InvalidOperationException($"{control} is not in the window");

    private static async Task PointerClickAsync(Window window, Control control, MouseButton button = MouseButton.Left)
    {
        control.IsEffectivelyVisible.Should().BeTrue();
        var point = CenterIn(window, control);
        window.MouseDown(point, button);
        window.MouseUp(point, button);
        await FlushAsync();
    }

    private static async Task PointerDragAsync(Window window, Control from, Control to)
    {
        var start = CenterIn(window, from);
        var end = CenterIn(window, to);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(new Point(start.X + 10, start.Y), RawInputModifiers.LeftMouseButton);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        window.MouseUp(end, MouseButton.Left);
        await FlushAsync();
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task TheTabStrip_IsBoundToTheWindowsTabs()
    {
        using var harness = new Harness();
        var (_, window, tabs) = await OpenTabsAsync(harness, NewPdf("s1.pdf", "S1"), NewPdf("s2.pdf", "S2"));

        var strip = window.FindControl<DocumentTabStrip>("DocumentTabStripHost");
        strip.Should().NotBeNull();
        strip!.DataContext.Should().BeSameAs(tabs, "the strip must not inherit the shown session");
        strip.IsVisible.Should().BeTrue();
    }

    [FixedAvaloniaFact]
    public void AStandAloneWindow_HasAHiddenStripThatInheritsNothing()
    {
        var vm = MainWindowViewModelTestFactory.Create(thumbnailPrewarmEnabled: false);
        var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        window.Show();
        try
        {
            var strip = window.FindControl<DocumentTabStrip>("DocumentTabStripHost")!;
            strip.IsVisible.Should().BeFalse();
            strip.DataContext.Should().BeNull();
        }
        finally
        {
            window.Close();
        }
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task ClosingATab_ReleasesItsSession()
    {
        using var harness = new Harness();
        var (_, _, tabs) = await OpenTabsAsync(harness, NewPdf("keep.pdf", "KEEP"));
        var released = await OpenAndCloseTabAsync(harness, tabs, NewPdf("gone.pdf", "GONE"));

        for (var i = 0; i < 10 && released.IsAlive; i++)
        {
            await FlushAsync();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        released.IsAlive.Should().BeFalse("a closed tab's view model must be collectable");
    }

    /// <summary>
    /// #1584: Avalonia.Native binds a window's native menu to the first
    /// <see cref="NativeMenu"/> it is given and throws "The menu being updated
    /// does not match" when handed another. A tab switch used to attach the
    /// shown session's own menu, which killed the app on the second tab. The
    /// headless platform has no exporter, so this watches the invariant: the
    /// window's menu instance never changes, and its items act on the shown tab.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task SwitchingTabs_KeepsTheWindowsOneNativeMenu_AndItsItemsActOnTheShownTab()
    {
        using var harness = new Harness();
        var (_, window, tabs) = await OpenTabsAsync(harness, NewPdf("m1.pdf", "M1"), NewPdf("m2.pdf", "M2"));
        var a = tabs.Tabs[0].Session.ViewModel;
        var b = tabs.Tabs[1].Session.ViewModel;
        window.AttachesNativeMenuWithoutExporterForTesting = true;

        var attached = new List<NativeMenu?>();
        void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == NativeMenu.MenuProperty)
                attached.Add((NativeMenu?)e.NewValue);
        }
        window.PropertyChanged += OnPropertyChanged;
        try
        {
            tabs.SelectedTab = tabs.Tabs[0];
            await FlushAsync();
            var menu = NativeMenu.GetMenu(window);
            menu.Should().NotBeNull("the attach path must have run for this test to mean anything");
            ActsOnlyOn(menu!, a, other: b);
            var aSubmenus = Submenus(menu!);

            tabs.SelectedTab = tabs.Tabs[1];
            await FlushAsync();
            NativeMenu.GetMenu(window).Should().BeSameAs(menu,
                "Avalonia.Native throws when a window's native menu is replaced by another instance");
            ActsOnlyOn(menu!, b, other: a);

            tabs.SelectedTab = tabs.Tabs[0];
            await FlushAsync();
            NativeMenu.GetMenu(window).Should().BeSameAs(menu);
            ActsOnlyOn(menu!, a, other: b);
            Submenus(menu!).Should().Equal(aSubmenus,
                "a session shown again reuses its items, and an item's submenu is never replaced");

            attached.Should().ContainSingle("the window's menu is set once and never swapped")
                .Which.Should().BeSameAs(menu);
        }
        finally
        {
            window.PropertyChanged -= OnPropertyChanged;
        }
    }

    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task ClosingATab_ReleasesItsSession_WithTheNativeMenuAttached()
    {
        using var harness = new Harness();
        var (_, window, tabs) = await OpenTabsAsync(harness, NewPdf("keep-menu.pdf", "KEEP"));
        window.AttachesNativeMenuWithoutExporterForTesting = true;
        var released = await OpenAndCloseTabAsync(harness, tabs, NewPdf("gone-menu.pdf", "GONE"));

        for (var i = 0; i < 10 && released.IsAlive; i++)
        {
            await FlushAsync();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        NativeMenu.GetMenu(window).Should().NotBeNull("the attach path must have run");
        ActsOnlyOn(NativeMenu.GetMenu(window)!, tabs.SelectedTab!.Session.ViewModel, other: null);
        released.IsAlive.Should().BeFalse(
            "a closed tab's view model must be collectable even after the window's menu showed it");
    }

    private static readonly System.Reflection.PropertyInfo[] CommandProperties =
        typeof(MainWindowViewModel).GetProperties()
            .Where(p => typeof(System.Windows.Input.ICommand).IsAssignableFrom(p.PropertyType)
                        && p.GetIndexParameters().Length == 0)
            .ToArray();

    private static void ActsOnlyOn(NativeMenu menu, MainWindowViewModel shown, MainWindowViewModel? other)
    {
        var leaves = ToolbarOverflowMenuEntriesTests.NativeLeaves(menu).ToList();
        leaves.Should().Contain(n => ReferenceEquals(n.Command, shown.SaveFileCommand),
            "File > Save must save the shown tab");
        if (other == null)
            return;
        var otherCommands = CommandProperties.Select(p => p.GetValue(other)).Where(c => c != null).ToList();
        leaves.Where(n => otherCommands.Any(c => ReferenceEquals(c, n.Command)))
            .Select(n => n.Header)
            .Should().BeEmpty("no menu item may act on a tab that is not shown");
    }

    private static List<NativeMenu> Submenus(NativeMenu menu)
    {
        var result = new List<NativeMenu>();
        foreach (var item in menu.Items.OfType<NativeMenuItem>())
        {
            if (item.Menu is { } submenu)
            {
                result.Add(submenu);
                result.AddRange(Submenus(submenu));
            }
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> OpenAndCloseTabAsync(Harness harness, DocumentTabsViewModel tabs, string path)
    {
        await harness.Workspace.OpenDocumentsAsync([path], harness.Workspace.ActiveSession);
        await FlushAsync();
        var tab = tabs.Tabs.Single(t => t.Title == Path.GetFileName(path));
        var reference = new WeakReference(tab.Session.ViewModel);
        await tab.CloseCommand.Execute();
        await FlushAsync();
        return reference;
    }

    [Fact]
    public void RevealCommand_UsesAnArgumentList_NeverAShell()
    {
        var command = FileManagerReveal.CommandFor("/tmp/a b; rm -rf ~.pdf");
        command.Should().NotBeNull();
        if (OperatingSystem.IsMacOS())
        {
            command!.Value.FileName.Should().Be("open");
            command.Value.Arguments.Should().Equal("-R", "/tmp/a b; rm -rf ~.pdf");
        }
        FileManagerReveal.CommandFor("").Should().BeNull();
    }
    // ─────────────────── #1598: the native menu's tab switching ────────────

    /// <summary>
    /// The macOS Window menu carries Show Previous/Next Tab with Safari's key
    /// equivalents, and they act on the tabs of the window whose menu it is.
    /// This is the only path a real Control-Tab can take on macOS.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task TheNativeWindowMenu_SwitchesThisWindowsTabs_WithSafarisKeyEquivalents()
    {
        using var harness = new Harness();
        var (_, window, tabs) = await OpenTabsAsync(harness,
            NewPdf("m1.pdf", "M1"), NewPdf("m2.pdf", "M2"), NewPdf("m3.pdf", "M3"));
        tabs.SelectedTab!.Title.Should().Be("m3.pdf");

        var (previous, next) = TabSwitchItems((MainWindowViewModel)window.DataContext!);

        previous.Gesture.Should().Be(new KeyGesture(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift));
        next.Gesture.Should().Be(new KeyGesture(Key.Tab, KeyModifiers.Control));
        next.IsEnabled.Should().BeTrue("three tabs are open");
        previous.IsEnabled.Should().BeTrue();

        next.Command!.Execute(null);
        await FlushAsync();
        tabs.SelectedTab!.Title.Should().Be("m1.pdf", "Show Next Tab wraps past the last tab");
        window.DataContext.Should().BeSameAs(tabs.SelectedTab.Session.ViewModel);

        // The menu belongs to the session the window now shows.
        var (previousAfter, _) = TabSwitchItems((MainWindowViewModel)window.DataContext!);
        previousAfter.Command!.Execute(null);
        await FlushAsync();
        tabs.SelectedTab!.Title.Should().Be("m3.pdf", "Show Previous Tab wraps back past the first");
    }

    /// <summary>
    /// With one tab there is nothing to switch to, so the items are DISABLED —
    /// which on macOS also deactivates their key equivalents, leaving Tab to
    /// move focus as it does everywhere else.
    /// </summary>
    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task TheNativeTabSwitchItems_AreDisabled_WithASingleTab()
    {
        using var harness = new Harness();
        var (first, _, tabs) = await OpenTabsAsync(harness, NewPdf("only.pdf", "ONLY"));
        tabs.Tabs.Should().ContainSingle();

        var (previous, next) = TabSwitchItems(first.ViewModel);

        next.IsEnabled.Should().BeFalse("one tab has nothing to switch to");
        previous.IsEnabled.Should().BeFalse();
        next.Command!.CanExecute(null).Should().BeFalse(
            "a native key equivalent is validated against the item's enabled state, which " +
            "Avalonia writes from Command.CanExecute");

        next.Command!.Execute(null);
        await FlushAsync();
        tabs.SelectedTab!.Title.Should().Be("only.pdf", "and executing it anyway changes nothing");
    }

    /// <summary>
    /// Two windows, two menus: each switches its OWN tabs (#1584's per-window
    /// menu machinery, which swaps one menu's items per session).
    /// </summary>
    [FixedAvaloniaFact(Timeout = 90000)]
    public async Task EachWindowsNativeMenu_SwitchesOnlyItsOwnTabs()
    {
        using var harness = new Harness();
        var (_, windowA, tabsA) = await OpenTabsAsync(harness,
            NewPdf("a1.pdf", "A1"), NewPdf("a2.pdf", "A2"));

        var second = harness.OpenWindow(DocumentOpenMode.NewTab);
        await harness.Workspace.OpenDocumentsAsync([NewPdf("b1.pdf", "B1")], second);
        foreach (var session in harness.Workspace.Sessions)
            session.ViewModel.DocumentOpenMode = DocumentOpenMode.NewTab;
        await harness.Workspace.OpenDocumentsAsync([NewPdf("b2.pdf", "B2")], second);
        await FlushAsync();
        var windowB = (MainWindow)second.Window!;
        var tabsB = windowB.DocumentTabs!;
        windowA.Should().NotBeSameAs(windowB);
        tabsB.Tabs.Count.Should().BeGreaterThan(1, "the second window has tabs of its own");

        var selectedA = tabsA.SelectedTab!.Title;
        var selectedB = tabsB.SelectedTab!.Title;

        var (_, nextB) = TabSwitchItems((MainWindowViewModel)windowB.DataContext!);
        nextB.Command!.Execute(null);
        await FlushAsync();

        tabsB.SelectedTab!.Title.Should().NotBe(selectedB,
            "the second window's menu moved the second window's selection");
        tabsA.SelectedTab!.Title.Should().Be(selectedA,
            "and left the first window's selection alone");
    }

    /// <summary>Window ▸ Show Previous Tab and Show Next Tab of one session's menu.</summary>
    private static (NativeMenuItem Previous, NativeMenuItem Next) TabSwitchItems(MainWindowViewModel viewModel)
    {
        var menu = MacNativeMenuBuilder.Create(viewModel);
        var windowMenu = menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Window").Menu!;
        var items = windowMenu.Items.OfType<NativeMenuItem>().ToList();
        var previous = items.Single(i => i.Header == "Show Previous Tab");
        var next = items.Single(i => i.Header == "Show Next Tab");
        previous.Command.Should().NotBeNull();
        next.Command.Should().NotBeNull();
        return (previous, next);
    }
}
