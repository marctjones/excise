using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Excise.App.Models;
using Excise.App.ViewModels;
using Excise.App.Workspace;
using Xunit;
using Harness = Excise.App.Tests.UI.MultiDocumentSessionTests.Harness;

namespace Excise.App.Tests.UI;

/// <summary>
/// Closing a document window must let go of everything that document cost, the
/// same way closing a tab does.
/// </summary>
/// <remarks>
/// <para>#1543's multi-document run (2026-09-17) opened w9, irs and scan into
/// one instance and closed the third: as tabs it gave back 146 MB of 303 MB
/// opened, as windows 180 MB of 494 MB. Reading that as "a window leaks" needs
/// evidence, because the two configurations do not hold the same things — a
/// second window has its own viewer, its own compositor surfaces and its own
/// page bitmaps, all of which the tab configuration shares.</para>
/// <para>These tests decide the reachability question instead of the footprint
/// question: after the close, is the window, its viewer, its view model and its
/// document still reachable from the workspace? A footprint that does not fall
/// while nothing is reachable is the allocator's business (measured in the
/// #1565 open-cost work: a compacting collect moved a 126-page document's
/// footprint by 57 MB and a malloc pressure-relief moved it by 0); a reachable
/// document is a defect this catches.</para>
/// </remarks>
[Collection("AvaloniaTests")]
public sealed class WindowCloseReleaseTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"excise-close-release-{Guid.NewGuid():N}");

    public WindowCloseReleaseTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    private string NewPdf(string name, int pages)
    {
        var path = Path.Combine(_tempDir, name);
        TestPdfGenerator.CreateMultiPagePdf(path, pages);
        return path;
    }

    private static async Task DrainAsync()
    {
        // Two idle-priority hops: the first lets the Closed handlers and the
        // posted bitmap disposals run, the second lets anything they posted run.
        for (var i = 0; i < 3; i++)
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Collect()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    [FixedAvaloniaFact(Timeout = 120_000)]
    public async Task ClosingOneOfTwoWindows_ReleasesThatWindowsDocument_ViewerAndViewModel()
    {
        using var harness = new Harness();
        var kept = harness.Workspace.CreateSession();
        kept.ViewModel.ThumbnailPrewarmEnabled = false;
        harness.Workspace.ShowInNewWindow(kept);
        await harness.Workspace.OpenDocumentsAsync([NewPdf("kept.pdf", 4)], kept);

        var (windowRef, viewModelRef, documentRef, viewerRef) = await OpenAndCloseSecondWindowAsync(harness);

        Collect();

        harness.Workspace.Windows.Should().HaveCount(1, "only the kept window is left");
        kept.ViewModel.IsDocumentLoaded.Should().BeTrue("the other document is untouched");

        documentRef.IsAlive.Should().BeFalse(
            "a closed window must not keep its document reachable — that is the whole "
            + "point of #1551's 'a closed window lets go of its session'");
        viewModelRef.IsAlive.Should().BeFalse("nor its view model");
        viewerRef.IsAlive.Should().BeFalse("nor its viewer, which owns the page bitmaps");
        windowRef.IsAlive.Should().BeFalse("nor the window itself");
    }

    /// <summary>
    /// The window is created, driven and closed inside this method so no local
    /// of the test frame can keep any of it alive — a JIT-visible local in the
    /// caller is enough to make every assertion above pass or fail for the
    /// wrong reason.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private async Task<(WeakReference Window, WeakReference ViewModel, WeakReference Document, WeakReference Viewer)>
        OpenAndCloseSecondWindowAsync(Harness harness)
    {
        var session = harness.Workspace.CreateSession();
        session.ViewModel.ThumbnailPrewarmEnabled = false;
        var window = harness.Workspace.ShowInNewWindow(session);
        window.Width = 900;
        window.Height = 700;
        await harness.Workspace.OpenDocumentsAsync([NewPdf("closed.pdf", 12)], session);

        session.ViewModel.IsDocumentLoaded.Should().BeTrue("fixture: the second window has a document");
        var viewer = window.GetVisualDescendants()
            .OfType<Excise.Avalonia.Controls.PdfViewerControl>()
            .FirstOrDefault();
        viewer.Should().NotBeNull("fixture: the window hosts a viewer");

        var refs = (
            new WeakReference(window),
            new WeakReference(session.ViewModel),
            new WeakReference(session.ViewModel.PdfCoreDocument!),
            new WeakReference(viewer!));

        session.ViewModel.FileState.MarkSaved();
        window.Close();
        await DrainAsync();
        return refs;
    }
}
