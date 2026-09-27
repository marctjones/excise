using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.App.Workspace;
using Xunit;
using Harness = Excise.App.Tests.UI.MultiDocumentSessionTests.Harness;

namespace Excise.App.Tests.UI;

/// <summary>
/// Pins the invariant a 2026-09-27 cold-launch investigation (#1629/#1819) had
/// to establish by READING code rather than by a test proving it in seconds:
/// the window title is a pure function of whichever tab is currently shown,
/// and switching tabs is the only thing that changes it. #1629 (unreproduced)
/// hypothesized a title naming a document no tab displayed; this test makes
/// that state provably unreachable through the normal tab-switch path, so a
/// future refactor of MainWindow.UpdateTitle or the tab-bind/unbind path can't
/// silently reintroduce the ambiguity that investigation had to resolve by
/// hand.
/// </summary>
[Collection("AvaloniaTests")]
public sealed class WindowTitleTabInvariantTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"excise-titleinvariant-{Guid.NewGuid():N}");

    public WindowTitleTabInvariantTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    private string NewPdf(string name) =>
        TestPdfGenerator.CreateSimpleTextPdf(Path.Combine(_tempDir, name), name);

    [FixedAvaloniaFact(Timeout = 60000)]
    public async Task TheWindowTitle_AlwaysNamesTheCurrentlyShowingTab_NeverTheOneJustLeft()
    {
        using var harness = new Harness();
        var first = harness.OpenWindow(DocumentOpenMode.NewTab);

        foreach (var name in new[] { "alpha.pdf", "beta.pdf" })
        {
            await harness.Workspace.OpenDocumentsAsync([NewPdf(name)], harness.Workspace.ActiveSession);
            foreach (var session in harness.Workspace.Sessions)
                session.ViewModel.DocumentOpenMode = DocumentOpenMode.NewTab;
        }

        var window = (MainWindow)first.Window!;
        await FlushAsync();

        var tabs = window.DocumentTabs!;
        tabs.Tabs.Should().HaveCount(2, "the strip only appears with more than one document");

        var betaTab = tabs.Tabs.Single(t => t.Title == "beta.pdf");
        var alphaTab = tabs.Tabs.Single(t => t.Title == "alpha.pdf");

        // Opening a second document switches focus to it — the same
        // "you asked to open this, so you see it" behaviour Safari/Preview/
        // every tabbed macOS app uses. This is standard, not a bug: verified
        // here rather than assumed.
        tabs.SelectedTab.Should().BeSameAs(betaTab,
            "the just-opened document becomes the active tab");
        window.Title.Should().Be(DocumentWindowTitle.For("beta.pdf", hasUnsavedChanges: false),
            "the title names whichever tab is showing");

        // Switch back to the other tab exactly the way a user does: through
        // the tab's own SelectCommand, not by poking SelectedTab directly.
        alphaTab.SelectCommand.Execute().Subscribe();
        await FlushAsync();

        tabs.SelectedTab.Should().BeSameAs(alphaTab);
        window.Title.Should().Be(DocumentWindowTitle.For("alpha.pdf", hasUnsavedChanges: false),
            "switching tabs is the only thing that ever moves the title — " +
            "it never lags behind or names the tab just left");

        // And back again, so this isn't just "the first switch happened to work".
        betaTab.SelectCommand.Execute().Subscribe();
        await FlushAsync();

        tabs.SelectedTab.Should().BeSameAs(betaTab);
        window.Title.Should().Be(DocumentWindowTitle.For("beta.pdf", hasUnsavedChanges: false));
    }

    private static async Task FlushAsync()
    {
        for (var i = 0; i < 6; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await Task.Delay(20);
        }
    }
}
