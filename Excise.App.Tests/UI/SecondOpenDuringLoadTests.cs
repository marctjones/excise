using AwesomeAssertions;
using Avalonia.Threading;
using Excise.App.Models;
using Excise.App.Views;
using Excise.App.Workspace;
using Xunit;

namespace Excise.App.Tests.UI;

/// <summary>
/// #1629: a second open request arriving while the first document is still
/// loading (a second Finder double-click, a second `open -a`) must not lose
/// the in-flight document, and the window title must name what the selected
/// tab actually shows.
/// </summary>
[Collection("AvaloniaTests")]
public sealed class SecondOpenDuringLoadTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"excise-1629-{Guid.NewGuid():N}");

    public SecondOpenDuringLoadTests() => Directory.CreateDirectory(_tempDir);

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

    private static async Task FlushAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(20);
        }
    }

    [FixedAvaloniaTheory(Timeout = 60000)]
    [InlineData(DocumentOpenMode.NewTab)]
    [InlineData(DocumentOpenMode.Automatic)]
    public async Task SecondOpen_WhileTheFirstIsLoading_KeepsTheFirstInItsOwnTab(DocumentOpenMode mode)
    {
        using var harness = new MultiDocumentSessionTests.Harness();
        var origin = harness.OpenWindow(mode);
        var first = NewPdf("first-w4.pdf", "FIRST");
        var second = NewPdf("second-1040.pdf", "SECOND");

        // Both requests come from the OS with no origin: the workspace routes
        // them from the active session, exactly as App.OpenPathsAsync does.
        var firstOpen = harness.Workspace.OpenDocumentsAsync([first], origin: null);
        firstOpen.IsCompleted.Should().BeFalse("the first load must still be in flight for this race");
        var secondOpen = harness.Workspace.OpenDocumentsAsync([second], origin: null);
        await Task.WhenAll(firstOpen, secondOpen);
        await FlushAsync();

        var live = harness.Workspace.Sessions.Where(s => !s.IsDisposed).ToList();
        live.Select(s => s.FilePath is { } p ? Path.GetFileName(p) : "Untitled")
            .Should().BeEquivalentTo(new[] { "first-w4.pdf", "second-1040.pdf" },
                "the in-flight document survives to its own session and no empty session is left");
        origin.FilePath.Should().NotBeNull();
        Path.GetFileName(origin.FilePath!).Should().Be("first-w4.pdf");

        foreach (var window in harness.Workspace.Windows)
        {
            var tabs = window.DocumentTabs!;
            tabs.Tabs.Select(t => t.Title).Should().NotContain("Untitled");

            // The title bar follows the selected tab, never the last request.
            var shown = tabs.SelectedTab!.Session.ViewModel;
            window.DataContext.Should().BeSameAs(shown);
            window.Title.Should().Be(DocumentWindowTitle.For(shown.DocumentName, shown.HasUnsavedDocumentChanges));

            foreach (var tab in tabs.Tabs)
            {
                tabs.SelectedTab = tab;
                await FlushAsync();
                var vm = tab.Session.ViewModel;
                window.DataContext.Should().BeSameAs(vm);
                vm.IsDocumentLoaded.Should().BeTrue();
                window.Title.Should().Be(DocumentWindowTitle.For(vm.DocumentName, vm.HasUnsavedDocumentChanges));
            }
        }
    }
}
