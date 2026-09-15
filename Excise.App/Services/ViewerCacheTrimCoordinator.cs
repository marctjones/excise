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
/// <see cref="PdfViewerCacheTrimLevel.Background"/>.</item>
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
    private readonly CacheTrimPolicy _policy;
    private readonly Func<(long MemoryLoadBytes, long HighMemoryLoadThresholdBytes)> _sampleGc;
    private readonly DispatcherTimer? _idleTimer;
    private readonly ReleasedMemoryReclaimer? _memoryReclaimer;
    private IDisposable? _pressureSource;
    private Action? _detach;
    private long _lastGcSampleTimestamp;
    private bool _disposed;

    internal ViewerCacheTrimCoordinator(
        Action<PdfViewerCacheTrimLevel> trim,
        CacheTrimPolicy policy,
        Func<(long MemoryLoadBytes, long HighMemoryLoadThresholdBytes)>? sampleGc = null,
        Action<PdfViewerCacheTrimLevel>? trimThumbnails = null,
        ReleasedMemoryReclaimer? memoryReclaimer = null)
    {
        _trim = trim ?? throw new ArgumentNullException(nameof(trim));
        _trimThumbnails = trimThumbnails;
        _memoryReclaimer = memoryReclaimer;
        _policy = policy;
        _sampleGc = sampleGc ?? SampleGcMemoryLoad;
        if (policy.SoftTriggers && policy.IdleDelay > TimeSpan.Zero)
        {
            _idleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = policy.IdleDelay };
            _idleTimer.Tick += OnIdleElapsed;
        }
    }

    /// <summary>True while the one-shot idle timer is waiting to fire.</summary>
    internal bool IdleTimerArmed => _idleTimer?.IsEnabled == true;

    /// <summary>True when OS pressure comes from a native source rather than GC sampling.</summary>
    internal bool HasNativePressureSource => _pressureSource != null;

    /// <summary>
    /// Wire the coordinator to a window and its viewer: deactivation and
    /// minimize, viewer activity (scroll, zoom, page, document, view mode,
    /// render), and on macOS the OS pressure source.
    /// <paramref name="trimThumbnails"/> releases the sidebar's thumbnail tier
    /// at Warn and Critical; <paramref name="memoryReclaimer"/> then collects
    /// the managed garbage those trims leave behind (#1481).
    /// </summary>
    internal static ViewerCacheTrimCoordinator Attach(
        Window window, PdfViewerControl viewer, CacheTrimPolicy policy, ILogger? logger = null,
        Action<PdfViewerCacheTrimLevel>? trimThumbnails = null,
        ReleasedMemoryReclaimer? memoryReclaimer = null)
    {
        var coordinator = new ViewerCacheTrimCoordinator(
            viewer.TrimCaches, policy, trimThumbnails: trimThumbnails, memoryReclaimer: memoryReclaimer);

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
        // around them stays until a GC runs, and an idle app runs none. Only
        // the pressure signals ask: they are rare and mean the memory is wanted
        // back. Background trims (deactivate, minimize, idle) do not. They are
        // frequent, a blocking compacting gen2 on every window switch would be
        // a visible hitch, and they release too little managed memory to pay
        // for one. The reclaimer posts, so it runs after both trims above.
        switch (trigger)
        {
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

/// <summary>What asked for a cache trim (#1478); the <c>trigger</c> tag on <c>excise.app.cache_trim.requests</c>.</summary>
internal enum CacheTrimTrigger
{
    Deactivated,
    Minimized,
    Idle,
    OsPressure,
    GcMemoryLoad,
}
