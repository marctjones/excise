using System;
using System.Diagnostics;
using Avalonia.Threading;

namespace Excise.App.Automation;

/// <summary>
/// What the UI thread did between two scenario boundaries: the worst stall and how many were
/// longer than <see cref="UiStallTracker.JankMs"/>.
/// </summary>
internal readonly record struct UiStallReading(double MaxMs, int Over100Ms, int Ticks);

/// <summary>
/// The arithmetic of the UI-stall heartbeat, with no Avalonia in it so it can be tested with
/// synthetic times. A timer that asks to fire every <c>interval</c> ms is run on the UI thread;
/// when something blocks that thread the next tick arrives late, and the lateness (the gap between
/// two ticks less the interval) is the stall: how long a key press or a click would have waited.
/// </summary>
/// <remarks>
/// Averages hide the thing users call "slow": a page turn that is 55 ms nine times and 400 ms once
/// feels broken, and its mean looks fine. The runner reads the worst stall since the previous
/// boundary at every boundary, so a step that froze the UI shows in that step's row.
/// </remarks>
internal sealed class UiStallTracker
{
    /// <summary>The stall length that is counted as a visible hitch.</summary>
    internal const double JankMs = 100;

    private readonly double _intervalMs;
    private readonly Func<double>? _clock;
    private readonly object _gate = new();
    private double? _lastTickMs;
    private double _maxMs;
    private int _over100;
    private int _ticks;

    /// <param name="interval">How often the heartbeat asks to fire.</param>
    /// <param name="clock">
    /// Milliseconds now, on the same clock as <see cref="Tick"/>. With it, <see cref="TakeAndReset"/>
    /// counts a stall that is still going on when it is read; without it a stall is only known once
    /// the late tick arrives, and a 20 s freeze would be reported by the row after the one that froze.
    /// </param>
    internal UiStallTracker(TimeSpan interval, Func<double>? clock = null)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _intervalMs = interval.TotalMilliseconds;
        _clock = clock;
    }

    /// <summary>Mark the moment the heartbeat starts; the first gap is measured from here.</summary>
    internal void Start(double nowMs)
    {
        lock (_gate) _lastTickMs = nowMs;
    }

    /// <summary>A tick arrived at <paramref name="nowMs"/>. A clock that runs backwards is ignored.</summary>
    internal void Tick(double nowMs)
    {
        lock (_gate)
        {
            if (_lastTickMs is { } last && nowMs > last)
            {
                var stall = Math.Max(0, nowMs - last - _intervalMs);
                if (stall > _maxMs) _maxMs = stall;
                if (stall >= JankMs) _over100++;
                _ticks++;
            }

            _lastTickMs = nowMs;
        }
    }

    /// <summary>What happened since the last call (or the start), and begin a new window.</summary>
    internal UiStallReading TakeAndReset()
    {
        lock (_gate)
        {
            if (_clock is { } now && _lastTickMs is { } last)
            {
                // The heartbeat is overdue right now: that lateness belongs to this window. Moving the
                // baseline to now means the late tick, when it comes, adds only the remainder.
                var t = now();
                var open = Math.Max(0, t - last - _intervalMs);
                if (open > _maxMs) _maxMs = open;
                if (open >= JankMs) _over100++;
                if (t > last) _lastTickMs = t;
            }

            var reading = new UiStallReading(_maxMs, _over100, _ticks);
            _maxMs = 0;
            _over100 = 0;
            _ticks = 0;
            return reading;
        }
    }
}

/// <summary>
/// Runs a <see cref="UiStallTracker"/> from a UI-thread timer at Input priority (what a key press
/// competes with). Owned by the scenario host for the length of one run; never a static.
/// </summary>
/// <remarks>
/// The interval is 50 ms, not 16: a 60 Hz timer is itself CPU the scenarios measure, and 50 ms
/// resolves a 100 ms hitch with room. Its own cost is read from the <c>null</c> scenario, with and
/// without it.
/// </remarks>
internal sealed class UiStallMonitor : IDisposable
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(50);

    private readonly DispatcherTimer _timer;
    private readonly UiStallTracker _tracker;
    private readonly Stopwatch _clock;

    /// <summary>The tracker's clock: read by the monitor's ticks and by the sampler's open-gap check.</summary>
    internal static (UiStallTracker Tracker, Stopwatch Clock) CreateTracker(TimeSpan interval)
    {
        var clock = Stopwatch.StartNew();
        return (new UiStallTracker(interval, () => clock.Elapsed.TotalMilliseconds), clock);
    }

    internal UiStallMonitor(UiStallTracker tracker, Stopwatch clock, TimeSpan interval)
    {
        _tracker = tracker;
        _clock = clock;
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = interval };
        _timer.Tick += (_, _) => _tracker.Tick(_clock.Elapsed.TotalMilliseconds);
        _tracker.Start(_clock.Elapsed.TotalMilliseconds);
        _timer.Start();
    }

    public void Dispose() => _timer.Stop();
}
