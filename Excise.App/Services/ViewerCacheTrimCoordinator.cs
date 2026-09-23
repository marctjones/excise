using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Excise.Avalonia.Controls;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;

namespace Excise.App.Services;

/// <summary>When the viewer's caches may be trimmed (#1478); see <see cref="Models.WindowSettings"/>.</summary>
internal readonly record struct CacheTrimPolicy(bool OnMemoryPressure, bool SoftTriggers, TimeSpan IdleDelay);

/// <summary>The OS's memory-pressure state, as the libdispatch source reports it.</summary>
internal enum MemoryPressureLevel
{
    Normal = 0,
    Warn = 1,
    Critical = 2,
}

/// <summary>
/// Decides WHEN the viewer releases its caches (#1478); the viewer's
/// <see cref="PdfViewerControl.TrimCaches"/> decides WHAT. The control stays
/// platform-free, so the signals are gathered here.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>OS memory pressure: the libdispatch memory-pressure source on macOS
/// (<see cref="MacMemoryPressureSource"/>), Warn and Critical mapped to the
/// same trim levels. Where no native source is installed,
/// <see cref="GC.GetGCMemoryInfo()"/> is compared against its high-load
/// threshold instead. That info only refreshes after a GC, so it is sampled on
/// viewer activity, throttled, and never on a timer.</item>
/// <item>Soft triggers, behind <see cref="CacheTrimPolicy.SoftTriggers"/>:
/// window deactivation or minimize, and an idle delay after the last viewer
/// activity. Both only ever ask for
/// <see cref="PdfViewerCacheTrimLevel.Background"/>. The idle trim alone also
/// asks for a heap reclaim, once per idle period (#1496, #1713).</item>
/// </list>
/// The idle timer is ONE-SHOT: restarted by activity, stopped when it fires,
/// and never armed while soft triggers are off. A periodic wake-up would undo
/// the #1462 guarantee that an idle app does no work. The trim itself cannot
/// re-arm it: Background and Warn keep the visible band, so nothing re-renders,
/// and no trim moves the scroll offset, the zoom or the page.
/// </remarks>
internal sealed class ViewerCacheTrimCoordinator : IDisposable
{
    /// <summary>Minimum time between two GC memory-load samples.</summary>
    internal static readonly TimeSpan GcSampleInterval = TimeSpan.FromSeconds(10);

    private readonly Action<PdfViewerCacheTrimLevel> _trim;
    private readonly Action<PdfViewerCacheTrimLevel>? _trimThumbnails;
    private CacheTrimPolicy _policy;
    private readonly Func<(long MemoryLoadBytes, long HighMemoryLoadThresholdBytes)> _sampleGc;
    private DispatcherTimer? _idleTimer;
    private readonly ReleasedMemoryReclaimer? _memoryReclaimer;
    private IDisposable? _pressureSource;
    private Action? _detach;
    private long _lastGcSampleTimestamp;
    // Whether this idle period has asked for a reclaim; activity clears it.
    // Shared by every window's coordinator in the app (#1551 follow-up).
    private readonly IdleReclaimGate _idleReclaimGate;
    private bool _disposed;

    internal ViewerCacheTrimCoordinator(
        Action<PdfViewerCacheTrimLevel> trim,
        CacheTrimPolicy policy,
        Func<(long MemoryLoadBytes, long HighMemoryLoadThresholdBytes)>? sampleGc = null,
        Action<PdfViewerCacheTrimLevel>? trimThumbnails = null,
        ReleasedMemoryReclaimer? memoryReclaimer = null,
        IdleReclaimGate? idleReclaimGate = null)
    {
        _trim = trim ?? throw new ArgumentNullException(nameof(trim));
        _trimThumbnails = trimThumbnails;
        _memoryReclaimer = memoryReclaimer;
        _policy = policy;
        _sampleGc = sampleGc ?? SampleGcMemoryLoad;
        _idleReclaimGate = idleReclaimGate ?? new IdleReclaimGate();
        ConfigureIdleTimer(policy);
    }

    /// <summary>True while the one-shot idle timer is waiting to fire.</summary>
    internal bool IdleTimerArmed => _idleTimer?.IsEnabled == true;

    /// <summary>
    /// Apply a new soft-trigger policy to a live coordinator (Preferences →
    /// Performance). UI thread. Soft triggers off stops and drops the idle timer,
    /// so nothing is armed (#1462); on creates it, or changes its delay, and
    /// leaves it disarmed until the next viewer activity. The OS pressure switch
    /// is fixed at <see cref="Attach"/>, where its native source is installed,
    /// so <see cref="CacheTrimPolicy.OnMemoryPressure"/> is not changed here.
    /// </summary>
    internal void UpdatePolicy(CacheTrimPolicy policy)
    {
        if (_disposed)
            return;
        _policy = policy with { OnMemoryPressure = _policy.OnMemoryPressure };
        ConfigureIdleTimer(_policy);
    }

    private void ConfigureIdleTimer(CacheTrimPolicy policy)
    {
        if (policy.SoftTriggers && policy.IdleDelay > TimeSpan.Zero)
        {
            if (_idleTimer == null)
            {
                _idleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = policy.IdleDelay };
                _idleTimer.Tick += OnIdleElapsed;
            }
            else if (_idleTimer.Interval != policy.IdleDelay)
            {
                // Changing Interval on a running DispatcherTimer restarts it;
                // stop first so a change never arms a timer that was idle.
                bool wasArmed = _idleTimer.IsEnabled;
                _idleTimer.Stop();
                _idleTimer.Interval = policy.IdleDelay;
                if (wasArmed)
                    _idleTimer.Start();
            }
        }
        else if (_idleTimer != null)
        {
            _idleTimer.Stop();
            _idleTimer.Tick -= OnIdleElapsed;
            _idleTimer = null;
        }
    }

    /// <summary>True when OS pressure comes from a native source rather than GC sampling.</summary>
    internal bool HasNativePressureSource => _pressureSource != null;

    /// <summary>
    /// Wire the coordinator to a window and its viewer: deactivation and
    /// minimize, viewer activity (scroll, zoom, page, document, view mode,
    /// render), and on macOS the OS pressure source.
    /// <paramref name="trimThumbnails"/> releases the sidebar's thumbnail tier
    /// at Warn and Critical; <paramref name="memoryReclaimer"/> then collects
    /// the managed garbage those trims leave behind (#1481).
    /// <paramref name="idleReclaimGate"/>, when the app has several windows,
    /// is shared by all their coordinators so an idle app pays one reclaim,
    /// not one per window.
    /// </summary>
    internal static ViewerCacheTrimCoordinator Attach(
        Window window, PdfViewerControl viewer, CacheTrimPolicy policy, ILogger? logger = null,
        Action<PdfViewerCacheTrimLevel>? trimThumbnails = null,
        ReleasedMemoryReclaimer? memoryReclaimer = null,
        IdleReclaimGate? idleReclaimGate = null)
    {
        var coordinator = new ViewerCacheTrimCoordinator(
            viewer.TrimCaches, policy, trimThumbnails: trimThumbnails, memoryReclaimer: memoryReclaimer,
            idleReclaimGate: idleReclaimGate);

        EventHandler onDeactivated = (_, _) => coordinator.OnDeactivated();
        EventHandler<AvaloniaPropertyChangedEventArgs> onWindowProperty = (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty && e.NewValue is WindowState.Minimized)
                coordinator.OnMinimized();
        };
        EventHandler<ScrollChangedEventArgs> onScroll = (_, _) => coordinator.OnActivity();
        EventHandler<AvaloniaPropertyChangedEventArgs> onViewerProperty = (_, e) =>
        {
            if (IsActivityProperty(e.Property))
                coordinator.OnActivity();
        };

        window.Deactivated += onDeactivated;
        window.PropertyChanged += onWindowProperty;
        viewer.AddHandler(ScrollViewer.ScrollChangedEvent, onScroll);
        viewer.PropertyChanged += onViewerProperty;
        coordinator._detach = () =>
        {
            window.Deactivated -= onDeactivated;
            window.PropertyChanged -= onWindowProperty;
            viewer.RemoveHandler(ScrollViewer.ScrollChangedEvent, onScroll);
            viewer.PropertyChanged -= onViewerProperty;
        };

        if (policy.OnMemoryPressure && OperatingSystem.IsMacOS())
            coordinator._pressureSource = MacMemoryPressureSource.TryStart(coordinator.PostPressure, logger);

        return coordinator;
    }

    // A render pass follows each of these: the page or band changes, or (for
    // IsLoading) a single-page render starts or lands. Document is also the
    // document-open sample point for the GC fallback.
    private static bool IsActivityProperty(AvaloniaProperty property) =>
        property == PdfViewerControl.ZoomLevelProperty
        || property == PdfViewerControl.CurrentPageProperty
        || property == PdfViewerControl.DocumentProperty
        || property == PdfViewerControl.ViewModeProperty
        || property == PdfViewerControl.RenderVersionProperty
        || property == PdfViewerControl.IsLoadingProperty;

    /// <summary>The viewer did something: restart the idle delay and sample the GC fallback if due.</summary>
    internal void OnActivity()
    {
        if (_disposed)
            return;
        _idleReclaimGate.Requested = false;
        if (_idleTimer != null)
        {
            _idleTimer.Stop();
            _idleTimer.Start();
        }
        SampleGcMemoryLoadIfDue();
    }

    internal void OnDeactivated() => SoftTrim(CacheTrimTrigger.Deactivated);

    internal void OnMinimized() => SoftTrim(CacheTrimTrigger.Minimized);

    /// <summary>UI thread. Maps OS pressure to a trim level; Normal releases nothing.</summary>
    internal void OnPressure(MemoryPressureLevel level)
    {
        if (_disposed || !_policy.OnMemoryPressure)
            return;
        switch (level)
        {
            case MemoryPressureLevel.Warn:
                Request(CacheTrimTrigger.OsPressure, PdfViewerCacheTrimLevel.Warn);
                break;
            case MemoryPressureLevel.Critical:
                Request(CacheTrimTrigger.OsPressure, PdfViewerCacheTrimLevel.Critical);
                break;
        }
    }

    /// <summary>
    /// Any thread (the libdispatch queue). TrimCaches must run on the UI thread
    /// (#1467), so the pressure is posted there.
    /// </summary>
    internal void PostPressure(MemoryPressureLevel level) =>
        Dispatcher.UIThread.Post(() => OnPressure(level));

    /// <summary>
    /// UI thread. Like <see cref="OnPressure"/>, but says whether the request
    /// was actually made (#1497).
    /// </summary>
    /// <remarks>
    /// The in-app performance scenario runner uses this instead of
    /// <c>sudo memory_pressure</c>. <see cref="OnPressure"/> drops a request
    /// silently when the user's Preferences → Performance policy has pressure
    /// trims switched off, and a measurement harness that cannot tell "trimmed"
    /// from "silently did nothing" would report a trim step that never
    /// happened. Behaviour is otherwise identical — no policy is bypassed;
    /// the refusal is reported instead of swallowed.
    /// </remarks>
    internal bool TryRequestPressure(MemoryPressureLevel level)
    {
        if (_disposed || !_policy.OnMemoryPressure || level == MemoryPressureLevel.Normal)
            return false;

        OnPressure(level);
        return true;
    }

    /// <summary>
    /// UI thread. Request a background-level soft trim and say whether the
    /// policy allowed it (#1497) — the #1478/#1496 soft-trim path.
    /// </summary>
    /// <remarks>
    /// This is exactly the idle trim, reclaim gate included, so the harness's
    /// <c>soft-trim-cycle</c> scenario measures what an idle app does. A second
    /// call without viewer activity in between trims but does not reclaim.
    /// </remarks>
    internal bool TryRequestBackgroundTrim()
    {
        if (_disposed || !_policy.SoftTriggers)
            return false;

        Request(CacheTrimTrigger.Idle, PdfViewerCacheTrimLevel.Background);
        return true;
    }

    private void SoftTrim(CacheTrimTrigger trigger)
    {
        if (_disposed)
            return;
        // In the background there is nothing to wait for; activity re-arms it.
        _idleTimer?.Stop();
        if (_policy.SoftTriggers)
            Request(trigger, PdfViewerCacheTrimLevel.Background);
    }

    private void OnIdleElapsed(object? sender, EventArgs e)
    {
        // One-shot: stop first, whatever happens next.
        _idleTimer?.Stop();
        if (_disposed || !_policy.SoftTriggers)
            return;
        // Never above Background: Critical evicts the visible band's tiles, so
        // an idle Critical trim would make the next frame re-render.
        Request(CacheTrimTrigger.Idle, PdfViewerCacheTrimLevel.Background);
    }

    private void SampleGcMemoryLoadIfDue()
    {
        if (!_policy.OnMemoryPressure || _pressureSource != null)
            return;
        long now = Stopwatch.GetTimestamp();
        if (_lastGcSampleTimestamp != 0 &&
            Stopwatch.GetElapsedTime(_lastGcSampleTimestamp, now) < GcSampleInterval)
            return;
        _lastGcSampleTimestamp = now;

        var (load, threshold) = _sampleGc();
        if (threshold > 0 && load >= threshold)
            Request(CacheTrimTrigger.GcMemoryLoad, PdfViewerCacheTrimLevel.Warn);
    }

    private static (long, long) SampleGcMemoryLoad()
    {
        var info = GC.GetGCMemoryInfo();
        return (info.MemoryLoadBytes, info.HighMemoryLoadThresholdBytes);
    }

    private void Request(CacheTrimTrigger trigger, PdfViewerCacheTrimLevel level)
    {
        AppMetrics.RecordCacheTrimRequest(trigger, level);
        _trim(level);
        // The design's Warn tier: thumbnails are not the viewer's, and Background
        // leaves them alone (they are small and reload from the disk cache).
        if (level != PdfViewerCacheTrimLevel.Background)
            _trimThumbnails?.Invoke(level);

        // #1481: the trims above release bitmaps, but the managed garbage
        // around them stays until a GC runs, and an idle app runs none. The
        // pressure signals always ask: they are rare and mean the memory is
        // wanted back. Deactivate and minimize never do: they are frequent, and
        // a blocking compacting gen2 on every window switch would be a visible
        // hitch. Idle is the exception among the soft triggers (#1496). After
        // paging Altona the idle app's footprint grew from ~1320 to ~1566 MB
        // with 239–442 MB of the heap fragmented, and only a reclaim returns
        // it (a Warn trim, which reclaims, took fragmentation 239 → 4 MB and
        // the footprint down 485 MB). After the idle delay (30 s by default)
        // the user is unlikely to be mid-gesture.
        //
        // #1713: the reclaim used to run only when GC.GetGCMemoryInfo().
        // FragmentedBytes read past a 64 MB threshold. A heap-dump diff of a
        // reclaim that DID fire (336.8 -> 145.0 MB, 191.8 MB freed) found only
        // 78.5 MB of that was FragmentedBytes; the other 99.1 MB was
        // unreachable System.Byte[] the GC does not count as fragmentation
        // until a collection actually runs. That under-counts by more than
        // half, and it is noisy: measured fragmentation just before the gate,
        // across identical sessions of the same scenario, was 41.6-64.4 MB on
        // runs that did not fire and 77-114.1 MB on runs that did, a spread
        // that straddles any fixed threshold. So identical sessions ended
        // 145-146 MB or 248-272 MB live heap purely on which side of the gate
        // that run's noise landed. The reclaim uses GCCollectionMode.Aggressive,
        // which .NET documents for exactly this "app is going idle" use, and
        // the compacting pause it measured was 33-105 ms, not user-visible
        // after 30 s of inactivity. So the reclaim now runs unconditionally,
        // still gated to once per idle period by _idleReclaimGate (activity
        // resets it) — that gate is the only cooldown; there is no separate
        // time-based one. The reclaimer posts, so it runs after both trims
        // above.
        switch (trigger)
        {
            case CacheTrimTrigger.Idle when _memoryReclaimer != null && !_idleReclaimGate.Requested:
                // The GC is process-wide, so with a shared gate the period is
                // the app's: activity in any window starts a new one.
                _idleReclaimGate.Requested = true;
                _memoryReclaimer.Request(HeapReclaimTrigger.Idle);
                break;
            case CacheTrimTrigger.OsPressure when level != PdfViewerCacheTrimLevel.Background:
                _memoryReclaimer?.Request(HeapReclaimTrigger.OsPressure);
                break;
            case CacheTrimTrigger.GcMemoryLoad when level != PdfViewerCacheTrimLevel.Background:
                _memoryReclaimer?.Request(HeapReclaimTrigger.GcMemoryLoad);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _idleTimer?.Stop();
        _detach?.Invoke();
        _detach = null;
        _pressureSource?.Dispose();
        _pressureSource = null;
    }
}

/// <summary>
/// Whether the current idle period has already asked for a heap reclaim
/// (#1496). One instance per coordinator by default; the app shares one across
/// every window's coordinator, because the collection is process-wide: N idle
/// windows would otherwise run N blocking gen2 collections for one heap.
/// UI thread only.
/// </summary>
internal sealed class IdleReclaimGate
{
    internal bool Requested { get; set; }
}

/// <summary>What asked for a cache trim (#1478); the <c>trigger</c> tag on <c>excise.app.cache_trim.requests</c>.</summary>
internal enum CacheTrimTrigger
{
    Deactivated,
    Minimized,
    Idle,
    OsPressure,
    GcMemoryLoad,
}
