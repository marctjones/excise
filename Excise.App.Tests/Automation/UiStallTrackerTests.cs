using System;
using System.Text.Json;
using Excise.App.Automation;
using AwesomeAssertions;
using Xunit;

namespace Excise.App.Tests.Automation;

/// <summary>
/// The UI-stall heartbeat's arithmetic (<see cref="UiStallTracker"/>) and the way a stall reaches the
/// scenario journal. Synthetic times only: nothing here starts a timer or a window, and nothing asserts
/// how long a real UI thread stalls (that is the measurement, not a test).
/// </summary>
public class UiStallTrackerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);

    [Fact]
    public void OnTimeTicks_ReportNoStall()
    {
        var tracker = new UiStallTracker(Interval);
        tracker.Start(0);
        for (var t = 50; t <= 500; t += 50) tracker.Tick(t);

        var reading = tracker.TakeAndReset();

        reading.MaxMs.Should().Be(0);
        reading.Over100Ms.Should().Be(0);
        reading.Ticks.Should().Be(10);
    }

    [Fact]
    public void ALateTick_IsTheStall_NotTheWholeGap()
    {
        var tracker = new UiStallTracker(Interval);
        tracker.Start(0);
        tracker.Tick(50);
        tracker.Tick(300);   // the thread was blocked: due at 100, arrived at 300 => 200 ms late

        var reading = tracker.TakeAndReset();

        reading.MaxMs.Should().BeApproximately(200, 0.001);
        reading.Over100Ms.Should().Be(1);
    }

    [Fact]
    public void OnlyStallsOfAtLeast100Ms_AreCounted_ButTheWorstOfAnySizeIsKept()
    {
        var tracker = new UiStallTracker(Interval);
        tracker.Start(0);
        tracker.Tick(120);   // 70 late: kept as the max, not a hitch
        tracker.Tick(170);
        tracker.Tick(320);   // 100 late: exactly the threshold, counted
        tracker.Tick(370);

        var reading = tracker.TakeAndReset();

        reading.MaxMs.Should().BeApproximately(100, 0.001);
        reading.Over100Ms.Should().Be(1);
    }

    [Fact]
    public void TakeAndReset_StartsANewWindow_ButKeepsTheClock()
    {
        var tracker = new UiStallTracker(Interval);
        tracker.Start(0);
        tracker.Tick(400);   // 350 late
        tracker.TakeAndReset().MaxMs.Should().BeApproximately(350, 0.001);

        tracker.Tick(450);   // on time, measured from the 400 tick, not from the start
        var second = tracker.TakeAndReset();

        second.MaxMs.Should().Be(0);
        second.Over100Ms.Should().Be(0);
        second.Ticks.Should().Be(1);
    }

    [Fact]
    public void AStallStillGoingOn_IsCountedByTheReadThatSeesIt_AndOnlyTheRemainderLater()
    {
        double now = 0;
        var tracker = new UiStallTracker(Interval, () => now);
        tracker.Start(0);
        tracker.Tick(50);

        now = 20_050;   // blocked since the 50 ms tick; no further tick has arrived
        var during = tracker.TakeAndReset();
        during.MaxMs.Should().BeApproximately(19_950, 0.001);
        during.Over100Ms.Should().Be(1);

        tracker.Tick(20_150);   // the thread is back: only 100 - 50 = 50 ms more is late
        now = 20_150;
        var after = tracker.TakeAndReset();
        after.MaxMs.Should().BeApproximately(50, 0.001);
        after.Over100Ms.Should().Be(0);
    }

    [Fact]
    public void AReadBetweenOnTimeTicks_ReportsNoStall()
    {
        double now = 0;
        var tracker = new UiStallTracker(Interval, () => now);
        tracker.Start(0);
        tracker.Tick(50);
        now = 80;   // 30 ms since the last tick, 20 ms before the next is due

        tracker.TakeAndReset().MaxMs.Should().Be(0);
    }

    [Fact]
    public void TheFirstGap_IsMeasuredFromStart()
    {
        var tracker = new UiStallTracker(Interval);
        tracker.Start(1000);
        tracker.Tick(1500);   // the first tick was due at 1050

        tracker.TakeAndReset().MaxMs.Should().BeApproximately(450, 0.001);
    }

    [Fact]
    public void ATickBeforeStart_OrABackwardsClock_IsIgnoredNotNegative()
    {
        var tracker = new UiStallTracker(Interval);
        tracker.Tick(100);                     // no Start yet: becomes the baseline, no gap
        tracker.Tick(50);                      // backwards: ignored, no negative stall, no tick counted
        tracker.Tick(150);

        var reading = tracker.TakeAndReset();

        reading.MaxMs.Should().BeGreaterThanOrEqualTo(0);
        reading.Ticks.Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public void ANonPositiveInterval_IsRejected()
    {
        var zero = () => new UiStallTracker(TimeSpan.Zero);
        zero.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TheStallReachesTheJournalRow_AsNumbers()
    {
        var sample = PerfSample.FromRuntime() with { UiStallMaxMs = 212.5, UiStallsOver100 = 3 };
        var record = new PerfStepRecord("s", 1, "step", "zoom", 10, 20, true, null, sample);

        using var doc = JsonDocument.Parse(record.ToJson(0));

        doc.RootElement.GetProperty("uiStallMaxMs").GetDouble().Should().BeApproximately(212.5, 0.001);
        doc.RootElement.GetProperty("uiStallsOver100").GetInt32().Should().Be(3);
    }

    [Fact]
    public void ASampleWithoutAMonitor_ReportsZeroStall_NotMissingFields()
    {
        var record = new PerfStepRecord("s", 1, "step", "open", 10, 20, true, null, PerfSample.FromRuntime());

        using var doc = JsonDocument.Parse(record.ToJson(0));

        doc.RootElement.GetProperty("uiStallMaxMs").GetDouble().Should().Be(0);
        doc.RootElement.GetProperty("uiStallsOver100").GetInt32().Should().Be(0);
    }
}
