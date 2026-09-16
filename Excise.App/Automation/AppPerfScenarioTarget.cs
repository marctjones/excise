using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Excise.App.Services;
using Excise.App.ViewModels;
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

    private readonly MainWindowViewModel _viewModel;
    private readonly PdfViewerControl? _viewer;
    private readonly ViewerCacheTrimCoordinator? _cacheTrim;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    internal AppPerfScenarioTarget(
        MainWindowViewModel viewModel,
        PdfViewerControl? viewer,
        ViewerCacheTrimCoordinator? cacheTrim)
    {
        _viewModel = viewModel;
        _viewer = viewer;
        _cacheTrim = cacheTrim;
    }

    public async Task OpenAsync(string path, CancellationToken cancellationToken)
    {
        // Fail loudly on a missing fixture. A scenario that silently fails to
        // open still emits a full row of plausible-looking numbers, which is
        // worse than no row at all; the runner turns this into a reported step
        // failure naming the path it tried.
        if (!File.Exists(path))
            throw new FileNotFoundException($"scenario document not found: {path}", path);

        // LoadDocumentCommand is the scripting load path: awaitable, no dialog,
        // and it carries its own timeout (LoadDocumentTimeoutSeconds).
        await _viewModel.LoadDocumentCommand(path).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        // Fire the command and poll for the side effect rather than awaiting
        // Execute(). The command body awaits Dispatcher.UIThread.InvokeAsync,
        // and awaiting the observable from the UI thread is the shape that
        // deadlocked TextSelectionDragTests (see its comment at the Ctrl+C
        // case). Polling is also what makes a dirty document — where the close
        // path waits on the unsaved-changes dialog nobody is there to answer —
        // come out as the runner's per-step timeout instead of a wedged run.
        _viewModel.CloseDocumentCommand.Execute().Subscribe();

        while (!cancellationToken.IsCancellationRequested)
        {
            if (_viewModel.CurrentDocument == null) return;
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
            if (_viewer != null)
            {
                inFlight = _viewer.GetRenderDiagnostics().ContinuousInFlightCount;
                renderVersion = _viewer.RenderVersion;
                loading = _viewer.IsLoading;
            }

            var settled = inFlight == 0
                && !loading
                && !_viewModel.IsSearching
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
        var total = _viewModel.TotalPages;
        if (total <= 0) return;

        var step = pages > 0 ? 1 : -1;
        for (var i = 0; i < Math.Abs(pages); i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = _viewModel.CurrentPageIndex + step;
            if (next < 0 || next >= total) break;
            _viewModel.CurrentPageIndex = next;

            // Let the page change dispatch and schedule its render, the way a
            // held Page Down key would. Not a settle — that is WaitIdle's job.
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task ScrollPagesAsync(int pages, CancellationToken cancellationToken)
    {
        if (_viewer == null) return;

        var viewport = _viewer.GetViewportDiagnostics();
        if (!viewport.IsAvailable) return;

        // Step in viewport-sized increments so the scroll looks like a reader
        // paging through, and the continuous cache sees the same eviction
        // pressure a user's scroll produces.
        var stepDip = Math.Max(1.0, viewport.Viewport.Height * 0.9);
        var extent = Math.Max(1.0, viewport.Extent.Height);
        var perPage = extent / Math.Max(1, _viewModel.TotalPages);
        var total = perPage * Math.Abs(pages);
        var steps = (int)Math.Ceiling(total / stepDip);

        for (var i = 0; i < steps; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _viewer.TryScrollViewportBy(pages > 0 ? stepDip : -stepDip);
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(true);
        }
    }

    public Task SetZoomAsync(double zoom, CancellationToken cancellationToken)
    {
        _viewModel.ZoomLevel = zoom;
        return Task.CompletedTask;
    }

    public Task SetViewModeAsync(string mode, CancellationToken cancellationToken)
    {
        _viewModel.ViewMode = mode.Equals("continuous", StringComparison.OrdinalIgnoreCase)
            ? PdfViewMode.Continuous
            : PdfViewMode.SinglePage;
        return Task.CompletedTask;
    }

    public async Task SearchAsync(string term, CancellationToken cancellationToken)
    {
        _viewModel.SearchText = term;

        // FindNow is fire-and-forget (it posts to a worker), so the completion
        // signal is IsSearching going false.
        _viewModel.FindNow();

        var deadline = Environment.TickCount64 + 60_000;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(true);
            if (!_viewModel.IsSearching) return;
        }
    }

    public async Task RedactTextAsync(string term, CancellationToken cancellationToken)
    {
        await _viewModel.RedactTextCommand(term).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public Task<bool> TrimAsync(string level, CancellationToken cancellationToken)
    {
        if (_cacheTrim == null) return Task.FromResult(false);

        var requested = level.ToLowerInvariant() switch
        {
            "warn" => _cacheTrim.TryRequestPressure(MemoryPressureLevel.Warn),
            "critical" => _cacheTrim.TryRequestPressure(MemoryPressureLevel.Critical),
            "background" => _cacheTrim.TryRequestBackgroundTrim(),
            _ => false,
        };

        return Task.FromResult(requested);
    }

    public PerfSample Sample()
    {
        var sample = PerfSample.FromRuntime();
        if (_viewer == null) return sample;

        var render = _viewer.GetRenderDiagnostics();
        return sample with
        {
            ContinuousInFlight = render.ContinuousInFlightCount,
            ContinuousEntries = render.ContinuousEntryCount,
            ContinuousResidentBytes = render.ContinuousResidentBytes,
            ContinuousByteBudget = render.ContinuousByteBudget,
            SinglePageEntries = render.SinglePageEntryCount,
            ZoomLevel = _viewModel.ZoomLevel,
            CurrentPageIndex = _viewModel.CurrentPageIndex,
            TotalPages = _viewModel.TotalPages,
        };
    }
}
