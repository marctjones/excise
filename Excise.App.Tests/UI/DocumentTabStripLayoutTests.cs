using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation.Peers;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Tests.Utilities;
using Excise.App.Tests.Utilities.Fakes;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.App.Workspace;
using Xunit;
using Harness = Excise.App.Tests.UI.MultiDocumentSessionTests.Harness;

namespace Excise.App.Tests.UI;

/// <summary>
/// What the tab strip has to LOOK like (#1628), as opposed to what it does
/// (MultiDocumentTabTests).
///
/// <para>The strip shipped painting itself in the toolbar's own background with
/// a single hairline between them, and marking the showing tab by font weight
/// alone. Live, that reads as a second toolbar row colliding with the first,
/// with nothing to say which document is on screen — reported as "it overlaps
/// with the toolbar and it is not clear how to use it or how it works". Every
/// one of those is a property, so every one of them is pinned here.</para>
///
/// <para>These assert geometry and paint, not screenshots: the headless app
/// loads FluentTheme only, which is why the strip declares its own brushes
/// (see DocumentTabStrip.axaml) — a DynamicResource from the app theme would
/// resolve to nothing here and the appearance could not be checked at all.</para>
/// </summary>
[Collection("AvaloniaTests")]
public sealed class DocumentTabStripLayoutTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"excise-tabstrip-{Guid.NewGuid():N}");

    public DocumentTabStripLayoutTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task TheStrip_IsItsOwnBand_AndNeverOverlapsTheToolbar()
    {
        using var harness = new Harness();
        var (window, _) = await OpenTwoTabsAsync(harness);

        var toolbar = window.FindControl<Border>("ToolbarBorder")!;
        var band = Band(window);

        var toolbarTop = toolbar.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        var toolbarBottom = toolbarTop + toolbar.Bounds.Height;
        var bandTop = band.TranslatePoint(new Point(0, 0), window)!.Value.Y;

        bandTop.Should().BeGreaterThanOrEqualTo(toolbarBottom,
            "the tab strip is a row below the toolbar, not a second row inside it");
        band.Bounds.Height.Should().BeGreaterThan(0, "a visible strip has height");

        // Its own band, visibly: a background of its own and a border along the
        // edge it shares with the toolbar. Without these the strip is the same
        // grey as the toolbar and the two read as one control.
        band.Background.Should().BeOfType<SolidColorBrush>()
            .Which.Color.Should().NotBe(Colors.Transparent,
                "the strip paints its own background so it is not mistaken for the toolbar");
        band.BorderThickness.Top.Should().BeGreaterThan(0,
            "the edge the strip shares with the toolbar is drawn");
        band.BorderThickness.Bottom.Should().BeGreaterThan(0,
            "the edge the strip shares with the document is drawn");
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task TheShowingTab_IsDistinguishedByMoreThanFontWeight()
    {
        using var harness = new Harness();
        var (window, tabs) = await OpenTwoTabsAsync(harness);

        var selected = TabBorder(window, tabs.SelectedTab!.Title);
        var other = TabBorder(window, tabs.Tabs.Single(t => !t.IsSelected).Title);

        BackgroundColor(selected).Should().NotBe(BackgroundColor(other),
            "the tab whose document is on screen is filled differently from the others");
        BackgroundColor(selected).Should().NotBe(BackgroundColor(Band(window)),
            "the showing tab is lifted out of the strip's band");

        AccentColor(selected).Should().NotBe(Colors.Transparent,
            "the showing tab carries an accent bar, the cue every tabbed browser uses");
        AccentColor(other).Should().Be(Colors.Transparent,
            "only the showing tab is accented — otherwise the accent says nothing");
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task ThePlusButton_AddsATabToThisWindow_EvenWhenThePreferenceSaysNewWindow()
    {
        // The button is drawn on this window's tab strip, so it has already
        // told the user where the document lands. Honouring a New Window
        // preference here would make the affordance a lie.
        var picker = new RecordingFilePicker();
        using var harness = new Harness(picker);
        var (window, tabs) = await OpenTwoTabsAsync(harness);
        foreach (var session in harness.Workspace.Sessions)
            session.ViewModel.DocumentOpenMode = DocumentOpenMode.NewWindow;

        var third = NewPdf("plus.pdf");
        picker.WillOpen(third);

        var plus = window.FindControl<DocumentTabStrip>("DocumentTabStripHost")!
            .FindControl<Button>("DocumentTabsNewButton")!;
        plus.Command.Should().BeSameAs(tabs.NewTabCommand, "the + button opens a document into these tabs");

        var windowsBefore = harness.Workspace.Sessions.Select(s => s.Window).Distinct().Count();
        await Dispatcher.UIThread.InvokeAsync(() => plus.Command!.Execute(null));
        await FlushAsync();

        tabs.Tabs.Select(t => t.Title).Should().Contain("plus.pdf",
            "+ adds the document as a tab of the window it was clicked in");
        harness.Workspace.Sessions.Select(s => s.Window).Distinct().Count()
            .Should().Be(windowsBefore, "+ opens no new window");
        picker.LastOpenRequest!.Title.Should().Be("Open PDF File",
            "+ uses the same picker as File > Open");
    }

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task EveryTab_AnnouncesItsName_PositionAndUnsavedState()
    {
        // #1554 claimed this; the live macOS accessibility tree showed the tab
        // buttons with no title at all while their close buttons had one, so
        // the claim is checked here rather than trusted.
        using var harness = new Harness();
        var (window, tabs) = await OpenTwoTabsAsync(harness);

        foreach (var tab in tabs.Tabs)
        {
            var button = window.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Classes.Contains("document-tab-button")
                             && b.DataContext is DocumentTabViewModel t && t.Title == tab.Title);
            ControlAutomationPeer.CreatePeerForElement(button).GetName()
                .Should().Be(tab.AccessibleName,
                    "a screen reader has to say which tab this is and where it sits");
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private string NewPdf(string name)
    {
        var path = Path.Combine(_tempDir, name);
        TestPdfGenerator.CreateSimpleTextPdf(path, name);
        return path;
    }

    private async Task<(MainWindow Window, DocumentTabsViewModel Tabs)> OpenTwoTabsAsync(Harness harness)
    {
        var first = harness.OpenWindow(DocumentOpenMode.NewTab);
        foreach (var name in new[] { "one.pdf", "two.pdf" })
        {
            await harness.Workspace.OpenDocumentsAsync([NewPdf(name)], harness.Workspace.ActiveSession);
            foreach (var session in harness.Workspace.Sessions)
                session.ViewModel.DocumentOpenMode = DocumentOpenMode.NewTab;
        }
        var window = (MainWindow)first.Window!;
        await FlushAsync();
        window.DocumentTabs!.Tabs.Should().HaveCount(2, "the strip shows only with more than one document");
        return (window, window.DocumentTabs!);
    }

    private static Border Band(MainWindow window) =>
        window.FindControl<DocumentTabStrip>("DocumentTabStripHost")!.FindControl<Border>("TabStripBand")!;

    private static Border TabBorder(MainWindow window, string title) =>
        window.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("document-tab")
                         && b.DataContext is DocumentTabViewModel t && t.Title == title);

    private static Color BackgroundColor(Border border) =>
        (border.Background as SolidColorBrush)?.Color ?? Colors.Transparent;

    private static Color AccentColor(Border tab) =>
        BackgroundColor(tab.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("document-tab-accent")));

    private static async Task FlushAsync()
    {
        for (var i = 0; i < 6; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await Task.Delay(20);
        }
    }
}
