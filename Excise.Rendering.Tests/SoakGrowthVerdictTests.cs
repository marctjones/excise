using System;
using System.Linq;
using AwesomeAssertions;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// The verdict function of <see cref="LongSessionSoakTests"/> on recorded heap trajectories. Pure arithmetic on
/// arrays (no document is opened or redacted), kept apart from the soak test that does redact. It pins the shape
/// the verdict must accept (the saw-tooth seen once in a whole-project run of the 2026-10-17 full tier) and the
/// shapes it must reject (the same run with a 64 KB-per-cycle leak, a runaway heap, a flat run that grows).
/// </summary>
public class SoakGrowthVerdictTests
{
    // The trajectory of the one failing run (cycle 0..23, bytes of managed heap), the shape the verdict must accept.
    private static long[] ObservedSawtooth() => new[]
    {
        45.49, 46.96, 47.25, 48.11, 49.02, 49.88, 50.78, 50.74,
        51.64, 45.49, 46.96, 47.28, 48.71, 48.76, 49.30, 50.18, 50.76, 51.64, 51.35, 52.23,
        45.49, 46.68, 46.96, 48.15,
    }.Select(mb => (long)(mb * 1024 * 1024)).ToArray();

    [Fact]
    public void Verdict_AcceptsTheObservedSawtooth_ThatTheOldPeakRuleFailed()
    {
        var run = ObservedSawtooth();
        (run.Skip(LongSessionSoakTests.WarmupCycles).Max() - run[LongSessionSoakTests.WarmupCycles - 1]).Should().BeGreaterThan(LongSessionSoakTests.MaxManagedGrowthBytes, "the old rule's reading");

        LongSessionSoakTests.EvaluateGrowth(run, LongSessionSoakTests.WarmupCycles).Pass.Should().BeTrue("the troughs are flat");
    }

    [Fact]
    public void Verdict_FailsTheSameSawtooth_WithA64KbPerCycleLeak()
    {
        var run = ObservedSawtooth().Select((v, i) => v + i * 64L * 1024).ToArray();

        var verdict = LongSessionSoakTests.EvaluateGrowth(run, LongSessionSoakTests.WarmupCycles);

        verdict.Pass.Should().BeFalse("a leak lifts every trough");
        verdict.GrowthBytes.Should().BeGreaterThan(LongSessionSoakTests.MaxManagedGrowthBytes);
    }

    [Fact]
    public void Verdict_FailsARunawayHeap_EvenWhenItResets()
    {
        var run = ObservedSawtooth().Select((v, i) => i >= LongSessionSoakTests.WarmupCycles ? v + (i - LongSessionSoakTests.WarmupCycles + 1) * 2L * 1024 * 1024 : v).ToArray();

        LongSessionSoakTests.EvaluateGrowth(run, LongSessionSoakTests.WarmupCycles).Pass.Should().BeFalse();
    }

    [Fact]
    public void Verdict_JudgesAFlatRunByThePeak_AsBefore()
    {
        var flat = Enumerable.Range(0, 24).Select(i => 50L * 1024 * 1024 + (i % 3) * 20_000).ToArray();
        LongSessionSoakTests.EvaluateGrowth(flat, LongSessionSoakTests.WarmupCycles).Pass.Should().BeTrue();

        var leaking = Enumerable.Range(0, 24).Select(i => 50L * 1024 * 1024 + Math.Max(0, i - 7) * 160_000).ToArray();
        LongSessionSoakTests.EvaluateGrowth(leaking, LongSessionSoakTests.WarmupCycles).Pass.Should().BeFalse("160 KB per cycle over 16 cycles is 2.5 MB");
    }
}
