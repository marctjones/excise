using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Excise.App.Services;
using Excise.App.ViewModels;
using Excise.App.Views;
using Excise.App.Workspace;
using Excise.Avalonia.Controls;

namespace Excise.App.Automation;

/// <summary>
/// Drives a performance scenario against the real view model and viewer (#1497).
/// </summary>
/// <remarks>
/// Every member goes through a public view-model property/command or a public
/// <see cref="PdfViewerControl"/> API. Nothing here synthesises input events or
/// touches accessibility — see <see cref="PerfScenarioRunner"/> for why that
/// choice is load-bearing rather than stylistic.
/// <para>
/// With several documents open (#1551-#1554) every step acts on the
/// workspace's ACTIVE document: its view model, and the viewer and cache-trim
/// coordinator of the window showing it. Before a second document is opened
/// that is the first window, exactly as before.
/// </para>
/// </remarks>
internal sealed class AppPerfScenarioTarget : IPerfScenarioTarget
{
    /// <summary>
    /// How many consecutive quiet polls make the viewer "idle".
    /// </summary>
    /// <remarks>
    /// ⚠️ One quiet poll is not enough, and this is the subtlest correctness
    /// point in the harness. Continuous tiles enter the in-flight set
    /// synchronously at request time, so <c>ContinuousInFlightCount == 0</c>
    /// genuinely means "nothing queued right now" — but a scroll or zoom
    /// schedules its next batch from a later layout pass, so a single zero
    /// reading taken between batches reads as idle in the middle of a burst.
    /// Requiring several consecutive quiet polls, an unchanged
    /// <c>RenderVersion</c>, and a low process CPU share closes that. The CPU
    /// share test is lifted from <c>EditModeSwitchReportTests</c>, which uses
    /// it so background work left by a previous window (text indexing, #1469)
    /// is not billed to the next interaction.
    /// </remarks>
    private const int QuietPollsRequired = 3;

    private const double QuietCpuShare = 0.15;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(120);

    /// <summary>How often a document switch is polled for its restored state.</summary>
    private static readonly TimeSpan SwitchPollInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>How long a switched-to document may take to show its page and scroll position again.</summary>
    private static readonly TimeSpan SwitchRestoreTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Scroll offsets within this many DIPs count as the same position.</summary>
    private const double ScrollToleranceDip = 4.0;

    private readonly MainWindowViewModel _initialViewModel;
    private readonly PdfViewerControl? _initialViewer;
    private readonly ViewerCacheTrimCoordinator? _initialCacheTrim;
    private readonly DocumentWorkspace? _workspace;
    private readonly Func<MainWindow, ViewerCacheTrimCoordinator?>? _cacheTrimFor;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // Where each document was left when the scenario switched away from it.
    // Weak keys: holding a closed document's view model here would keep its
    // memory alive and falsify the close-one measurement.
    private readonly ConditionalWeakTable<MainWindowViewModel, StrongBox<(int Page, double OffsetY, double Range)>> _leftAt = new();

    internal AppPerfScenarioTarget(
        MainWindowViewModel viewModel,
        PdfViewerControl? viewer,
        ViewerCacheTrimCoordinator? cacheTrim,
        DocumentWorkspace? workspace = null,
        Func<MainWindow, ViewerCacheTrimCoordinator?>? cacheTrimFor = null)
    {
        _initialViewModel = viewModel;
        _initialViewer = viewer;
        _initialCacheTrim = cacheTrim;
        _workspace = workspace;
        _cacheTrimFor = cacheTrimFor;
    }

    private DocumentSession? ActiveSession => _workspace?.ActiveSession;

    private MainWindowViewModel ViewModel => ActiveSession?.ViewModel ?? _initialViewModel;

    private PdfViewerControl? Viewer =>
        ActiveSession?.Window is MainWindow window ? window.CacheTrimTarget().Viewer : _initialViewer;

    private ViewerCacheTrimCoordinator? CacheTrim =>
        ActiveSession?.Window is MainWindow window && _cacheTrimFor != null
            ? _cacheTrimFor(window)
            : _initialCacheTrim;

    public async Task OpenAsync(string path, CancellationToken cancellationToken)
    {
        // Fail loudly on a missing fixture. A scenario that silently fails to
        // open still emits a full row of plausible-looking numbers, which is
        // worse than no row at all; the runner turns this into a reported step
        // failure naming the path it tried.
        if (!File.Exists(path))
            throw new FileNotFoundException($"scenario document not found: {path}", path);

        // ⚠️ LoadDocumentAsync, NOT LoadDocumentCommand — see issue #1540 for the
        // full account. The latter is the
        // SCRIPTING load, and its own comment says it does "headless document
        // loading (no thumbnails/rendering)" to avoid dispatcher work. It parses
        // the file and never drives the viewer: measured on altona-close, every
        // viewer counter stayed at 0 through the whole scenario, the scroll had
        // nothing laid out to scroll, and the run still produced a full row of
        // plausible numbers plus a confident (and wrong) verdict on #1461.
        // LoadDocumentAsync is the path a real open takes -- the one
        // App.OpenPathOnUiThread uses -- and the one that emits the
        // excise.app.document_open.phase.duration metrics.
        await ViewModel.LoadDocumentAsync(path).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        // With other documents open, Close Document closes the active
        // document's tab or window and disposes its session (#1551), so the
        // view model may never report CurrentDocument == null.
        var session = ActiveSession;
        var viewModel = ViewModel;
        // Fire the command and poll for the side effect rather than awaiting
        // Execute(). The command body awaits Dispatcher.UIThread.InvokeAsync,
        // and awaiting the observable from the UI thread is the shape that
        // deadlocked TextSelectionDragTests (see its comment at the Ctrl+C
        // case). Polling is also what makes a dirty document — where the close
        // path waits on the unsaved-changes dialog nobody is there to answer —
        // come out as the runner's per-step timeout instead of a wedged run.
        viewModel.CloseDocumentCommand.Execute().Subscribe();

        while (!cancellationToken.IsCancellationRequested)
        {
            if (viewModel.CurrentDocument == null || session is { IsDisposed: true }) return;
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(true);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<bool> WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        using var process = Process.GetCurrentProcess();

        var quiet = 0;
        long lastRenderVersion = -1;
        process.Refresh();
        var lastCpu = process.TotalProcessorTime;
        var lastWall = _clock.Elapsed;

        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(true);

            process.Refresh();
            var cpu = process.TotalProcessorTime;
            var wall = _clock.Elapsed;
            var share = (cpu - lastCpu).TotalMilliseconds
                / Math.Max(1.0, (wall - lastWall).TotalMilliseconds);
            lastCpu = cpu;
            lastWall = wall;

            var inFlight = 0;
            long renderVersion = 0;
            var loading = false;
            if (Viewer is { } viewer)
            {
                inFlight = viewer.GetRenderDiagnostics().ContinuousInFlightCount;
                renderVersion = viewer.RenderVersion;
                loading = viewer.IsLoading;
            }

            var settled = inFlight == 0
                && !loading
                && !ViewModel.IsSearching
                && renderVersion == lastRenderVersion
                && share < QuietCpuShare;

            lastRenderVersion = renderVersion;
            quiet = settled ? quiet + 1 : 0;
            if (quiet >= QuietPollsRequired) return true;
        }

        return false;
    }

    public async Task PageByAsync(int pages, CancellationToken cancellationToken)
    {
        var total = ViewModel.TotalPages;
        if (total <= 0) return;

        var step = pages > 0 ? 1 : -1;
        for (var i = 0; i < Math.Abs(pages); i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = ViewModel.CurrentPageIndex + step;
            if (next < 0 || next >= total) break;
            ViewModel.CurrentPageIndex = next;

            // Let the page change dispatch and schedule its render, the way a
            // held Page Down key would. Not a settle — that is WaitIdle's job.
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task ScrollPagesAsync(int pages, CancellationToken cancellationToken)
    {
        // Every way this step can do nothing is a FAILURE, not a quiet return.
        // Until 2026-09-16 all three returned or were ignored, and the first
        // calibration recorded 10 of 10 Altona scroll runs that never moved:
        // 3 band renders where a real scroll makes 17, 0 step failures, and a
        // tight "noise floor" made of five identical non-measurements.
        var viewer = Viewer
            ?? throw new InvalidOperationException("no viewer to scroll");

        var viewport = viewer.GetViewportDiagnostics();
        if (!viewport.IsAvailable)
            throw new InvalidOperationException($"viewport unavailable in {viewport.ViewMode} mode");

        var before = viewport.Offset.Y;

        // Step in viewport-sized increments so the scroll looks like a reader
        // paging through, and the continuous cache sees the same eviction
        // pressure a user's scroll produces.
        var stepDip = Math.Max(1.0, viewport.Viewport.Height * 0.9);
        var extent = Math.Max(1.0, viewport.Extent.Height);
        var perPage = extent / Math.Max(1, ViewModel.TotalPages);
        var total = perPage * Math.Abs(pages);
        var steps = (int)Math.Ceiling(total / stepDip);

        for (var i = 0; i < steps; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!viewer.TryScrollViewportBy(pages > 0 ? stepDip : -stepDip))
                throw new InvalidOperationException($"TryScrollViewportBy refused at step {i + 1}/{steps}");
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(true);
        }

        // The offset has to have MOVED, by at least one viewport step, in the
        // requested direction. A scroll clamped at an edge, or undone by a
        // later layout pass, measures nothing while looking like a scroll.
        var after = viewer.GetViewportDiagnostics();
        var moved = after.Offset.Y - before;
        if (Math.Sign(moved) != Math.Sign(pages) || Math.Abs(moved) < Math.Min(stepDip, total) * 0.5)
        {
            throw new InvalidOperationException(
                $"viewport did not scroll: offset {before:F0} -> {after.Offset.Y:F0} dip " +
                $"(wanted {(pages > 0 ? "+" : "-")}{total:F0}; extent {after.Extent.Height:F0}, " +
                $"viewport {after.Viewport.Height:F0}, mode {after.ViewMode})");
        }
    }

    public Task SetZoomAsync(double zoom, CancellationToken cancellationToken)
    {
        ViewModel.ZoomLevel = zoom;
        return Task.CompletedTask;
    }

    public Task SetViewModeAsync(string mode, CancellationToken cancellationToken)
    {
        ViewModel.ViewMode = mode.Equals("continuous", StringComparison.OrdinalIgnoreCase)
            ? PdfViewMode.Continuous
            : PdfViewMode.SinglePage;
        return Task.CompletedTask;
    }

    public async Task SearchAsync(string term, CancellationToken cancellationToken)
    {
        ViewModel.SearchText = term;

        // FindNow is fire-and-forget (it posts to a worker), so the completion
        // signal is IsSearching going false.
        ViewModel.FindNow();

        var deadline = Environment.TickCount64 + 60_000;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(true);
            if (!ViewModel.IsSearching) return;
        }
    }

    public async Task RedactTextAsync(string term, CancellationToken cancellationToken)
    {
        await ViewModel.RedactTextCommand(term).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public Task<bool> TrimAsync(string level, CancellationToken cancellationToken)
    {
        var cacheTrim = CacheTrim;
        if (cacheTrim == null) return Task.FromResult(false);

        var requested = level.ToLowerInvariant() switch
        {
            "warn" => cacheTrim.TryRequestPressure(MemoryPressureLevel.Warn),
            "critical" => cacheTrim.TryRequestPressure(MemoryPressureLevel.Critical),
            "background" => cacheTrim.TryRequestBackgroundTrim(),
            _ => false,
        };

        return Task.FromResult(requested);
    }

    public async Task OpenAnotherAsync(string path, string expect, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"scenario document not found: {path}", path);

        var workspace = RequireWorkspace();
        var origin = workspace.ActiveSession
            ?? throw new InvalidOperationException("no active document to open from");
        var host = origin.ViewModel.SessionHost
            ?? throw new InvalidOperationException("the active document has no session host");
        var originWindow = origin.Window;
        var windowsBefore = workspace.Windows.Count;
        var tabsBefore = originWindow == null ? 0 : workspace.SessionsIn(originWindow).Count;

        // The same call File ▸ Open, Open Recent and a Finder open make
        // (App.OpenPathAsync): the workspace decides window, tab or replace
        // from the session's DocumentOpenMode, which came from window.json.
        await host.OpenDocumentsAsync([path], replaceConfirmed: false).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();

        var opened = workspace.FindSessionShowing(path);
        var windowsAfter = workspace.Windows.Count;
        var tabsAfter = originWindow == null ? 0 : workspace.SessionsIn(originWindow).Count;
        var where = $"{windowsBefore}->{windowsAfter} window(s), {tabsBefore}->{tabsAfter} tab(s) in the origin window";

        if (opened == null || !opened.ViewModel.IsDocumentLoaded)
            throw new InvalidOperationException($"{Path.GetFileName(path)} did not open ({where})");
        if (!ReferenceEquals(workspace.ActiveSession, opened))
            throw new InvalidOperationException($"{Path.GetFileName(path)} opened but is not the active document ({where})");

        var landed = expect switch
        {
            "window" => windowsAfter == windowsBefore + 1 && !ReferenceEquals(opened.Window, originWindow),
            "tab" => windowsAfter == windowsBefore && tabsAfter == tabsBefore + 1
                     && ReferenceEquals(opened.Window, originWindow),
            _ => throw new ArgumentOutOfRangeException(nameof(expect), expect, "expected window or tab"),
        };
        if (!landed)
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} did not open in a new {expect} ({where}); " +
                "check the DocumentOpenMode the launch seeded");
    }

    public async Task SwitchDocumentAsync(int offset, CancellationToken cancellationToken)
    {
        if (offset == 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "a switch needs a non-zero offset");

        var workspace = RequireWorkspace();
        var current = workspace.ActiveSession
            ?? throw new InvalidOperationException("no active document to switch from");

        DocumentSession target;
        Action performSwitch;
        if (current.Window is MainWindow { DocumentTabs: { Tabs.Count: > 1 } tabs } && tabs.TabFor(current) is { } tab)
        {
            // Several tabs: the command Ctrl+Tab / Ctrl+Shift+Tab executes.
            var index = tabs.Tabs.IndexOf(tab);
            target = tabs.Tabs[Modulo(index + offset, tabs.Tabs.Count)].Session;
            var command = offset > 0 ? tabs.SelectNextTabCommand : tabs.SelectPreviousTabCommand;
            performSwitch = () =>
            {
                for (var i = 0; i < Math.Abs(offset); i++)
                    command.Execute().Subscribe();
            };
        }
        else
        {
            // One document per window: the Window menu's path.
            var entries = workspace.DescribeOpenDocuments(current)
                .Where(e => e.Key is DocumentSession { IsDisposed: false })
                .ToList();
            if (entries.Count < 2)
                throw new InvalidOperationException("only one document is open; there is nothing to switch to");
            var index = entries.FindIndex(e => ReferenceEquals(e.Key, current));
            var entry = entries[Modulo(index + offset, entries.Count)];
            target = (DocumentSession)entry.Key;
            var host = current.ViewModel.SessionHost
                ?? throw new InvalidOperationException("the active document has no session host");
            performSwitch = () => host.ActivateDocument(entry);
        }

        if (ReferenceEquals(target, current))
            throw new InvalidOperationException("the switch would land on the active document");

        Remember(current);
        _leftAt.TryGetValue(target.ViewModel, out var saved);
        performSwitch();

        var deadline = Environment.TickCount64 + (long)SwitchRestoreTimeout.TotalMilliseconds;
        var state = "not yet active";
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadShown(workspace, target, out var page, out var offsetY, out var range, out state)
                && (saved == null || Matches(saved.Value, page, offsetY, range, out state)))
                return;
            await Task.Delay(SwitchPollInterval, cancellationToken).ConfigureAwait(true);
        }

        var name = target.ViewModel.DocumentName;
        throw new InvalidOperationException(saved == null
            ? $"switching to {name} did not complete within {SwitchRestoreTimeout.TotalSeconds:F0}s: {state}"
            : $"{name} did not come back where it was left within {SwitchRestoreTimeout.TotalSeconds:F0}s: " +
              $"left at page {saved.Value.Page + 1}, offset {saved.Value.OffsetY:F0} dip; {state}");
    }

    public string? DescribeDocumentMismatch(int? documents, int? windows)
    {
        if (_workspace == null)
            return "no document workspace";

        var open = _workspace.DescribeOpenDocuments(null).Count(e => e.FilePath != null);
        var shown = _workspace.Windows.Count;
        if ((documents == null || documents == open) && (windows == null || windows == shown))
            return null;

        return $"expected {documents?.ToString() ?? "any number of"} document(s) in " +
               $"{windows?.ToString() ?? "any number of"} window(s); found {open} in {shown}";
    }

    public async Task<int> ReviewUnsavedChangesForQuitAsync(CancellationToken cancellationToken)
    {
        var workspace = RequireWorkspace();
        var sessions = workspace.DescribeOpenDocuments(null)
            .Select(e => e.Key)
            .OfType<DocumentSession>()
            .Where(s => !s.IsDisposed)
            .ToList();

        var answered = 0;
        UnsavedChangesDecision Discard()
        {
            answered++;
            return UnsavedChangesDecision.Discard;
        }

        foreach (var session in sessions)
            session.ViewModel.UnsavedChangesAnswer = Discard;
        try
        {
            // The review File ▸ Exit and Cmd+Q run (RequestQuitAsync) before
            // they ask the platform to quit. RequestQuitAsync itself is not
            // called: it shuts down synchronously, before this step's boundary
            // could be written. The host's own Shutdown quits afterwards, and
            // Discard has marked every reviewed document saved.
            if (!await workspace.ReviewUnsavedChangesAsync("quit excise").ConfigureAwait(true))
                throw new InvalidOperationException("the quit review kept a document open");
        }
        finally
        {
            foreach (var session in sessions)
                session.ViewModel.UnsavedChangesAnswer = null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return answered;
    }

    private DocumentWorkspace RequireWorkspace() =>
        _workspace ?? throw new InvalidOperationException("no document workspace (multi-document steps need the real app)");

    private static int Modulo(int value, int count) => ((value % count) + count) % count;

    private void Remember(DocumentSession session)
    {
        var viewer = session.Window is MainWindow window ? window.CacheTrimTarget().Viewer : null;
        if (viewer == null)
            return;
        var viewport = viewer.GetViewportDiagnostics();
        if (!viewport.IsAvailable)
            return;
        _leftAt.AddOrUpdate(session.ViewModel, new StrongBox<(int Page, double OffsetY, double Range)>(
            (session.ViewModel.CurrentPageIndex, viewport.Offset.Y, ScrollRange(viewport))));
    }

    /// <summary>
    /// Is <paramref name="target"/> the active document AND the one its
    /// window's viewer shows? A tab switch swaps the window's DataContext and
    /// restores the scroll position later, at ContextIdle.
    /// </summary>
    private static bool TryReadShown(
        DocumentWorkspace workspace,
        DocumentSession target,
        out int page,
        out double offsetY,
        out double range,
        out string state)
    {
        page = -1;
        offsetY = range = 0;
        if (!ReferenceEquals(workspace.ActiveSession, target))
        {
            state = "the target is not the active document";
            return false;
        }

        if (target.Window is not MainWindow window || !ReferenceEquals(window.DataContext, target.ViewModel))
        {
            state = "the target's window does not show it";
            return false;
        }

        var viewer = window.CacheTrimTarget().Viewer;
        if (viewer == null || viewer.IsLoading)
        {
            state = "the viewer has not laid the document out";
            return false;
        }

        var viewport = viewer.GetViewportDiagnostics();
        if (!viewport.IsAvailable)
        {
            state = "the viewer has not laid the document out";
            return false;
        }

        page = target.ViewModel.CurrentPageIndex;
        offsetY = viewport.Offset.Y;
        range = ScrollRange(viewport);
        state = $"showing page {page + 1}, offset {offsetY:F0} dip";
        return true;
    }

    private static bool Matches((int Page, double OffsetY, double Range) saved, int page, double offsetY, double range, out string state)
    {
        // MainWindow restores the scroll FRACTION, so compare in fraction
        // terms scaled back to DIPs: an extent that differs by a pixel after
        // relayout must not read as a lost position.
        var expected = saved.Range > 0 && range > 0 ? saved.OffsetY / saved.Range * range : saved.OffsetY;
        var ok = page == saved.Page && Math.Abs(offsetY - expected) <= ScrollToleranceDip;
        state = $"showing page {page + 1}, offset {offsetY:F0} dip (expected page {saved.Page + 1}, offset {expected:F0})";
        return ok;
    }

    private static double ScrollRange(PdfViewerViewportDiagnostics viewport) =>
        Math.Max(0, viewport.Extent.Height - viewport.Viewport.Height);

    public PerfSample Sample()
    {
        var sample = PerfSample.FromRuntime();
        if (_workspace != null)
        {
            sample = sample with
            {
                OpenDocuments = _workspace.DescribeOpenDocuments(null).Count(e => e.FilePath != null),
                DocumentWindows = _workspace.Windows.Count,
            };
        }

        var viewer = Viewer;
        if (viewer == null) return sample;

        var render = viewer.GetRenderDiagnostics();
        return sample with
        {
            ContinuousInFlight = render.ContinuousInFlightCount,
            ContinuousEntries = render.ContinuousEntryCount,
            ContinuousResidentBytes = render.ContinuousResidentBytes,
            ContinuousByteBudget = render.ContinuousByteBudget,
            SinglePageEntries = render.SinglePageEntryCount,
            ZoomLevel = ViewModel.ZoomLevel,
            CurrentPageIndex = ViewModel.CurrentPageIndex,
            TotalPages = ViewModel.TotalPages,
        };
    }
}
