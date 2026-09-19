using System.Diagnostics;
using AwesomeAssertions;
using Excise.Rendering.Differential;
using Xunit;

// Keep this accounting contract in the deterministic suite: release coverage
// deliberately excludes the expensive external-oracle Differential namespace.
namespace Excise.Rendering.Tests.Performance;

public sealed class ReferenceProcessResourcesTests
{
    [Fact]
    public void Capture_CurrentProcess_ReportsNonNegativeResourceValues()
    {
        using var process = Process.GetCurrentProcess();

        var resources = ReferenceProcessResources.Capture(process);

        resources.PeakWorkingSetBytes.Should().BeGreaterThan(0);
        resources.CpuMs.Should().NotBeNull();
        resources.CpuMs!.Value.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void WaitForExitAndCapture_CompletedDotnetProcess_ReturnsMergedResources()
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "--version" },
        });
        process.Should().NotBeNull();

        var exited = ReferenceProcessResources.WaitForExitAndCapture(process!, 30_000, out var resources);

        exited.Should().BeTrue();
        process.ExitCode.Should().Be(0);
        resources.CpuMs.Should().NotBeNull(
            "a completed child must report its CPU — see ShortLivedProcess_StillReportsBothCounters");
        resources.CpuMs!.Value.Should().BeGreaterThanOrEqualTo(0);
    }

    // #1674 — the counters used to be lost entirely for any process that exited
    // inside the sampler's first wait, because the loop waited before it sampled
    // and a process that has exited has no readable accounting left. That deleted
    // the measurement for whichever tool was FASTEST: over a 2026-09-19 sweep,
    // excise NativeAOT kept 15 of 27 samples and mutool 45 of 81, while the three
    // slowest tools lost none — so the fast binary reported MORE cpu than the slow
    // one. This pins the short case specifically; the pre-existing tests all use
    // either the long-lived current process or `dotnet --version`, and none of them
    // could see it.
    [Fact]
    public void ShortLivedProcess_StillReportsBothCounters()
    {
        using var process = Process.Start(new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 50 ms: under the old 100 ms wait, so a sampler that waits before it
            // samples reads nothing, but long enough to be sampled at all. This is
            // the real timescale — the fastest reference-performance run measured on
            // 2026-09-19 was mutool at 51 ms, and the excise rows it silently dropped
            // were 74-121 ms. ⚠️ A process that exits before the FIRST sample is still
            // unmeasurable by polling; only kernel accounting (wait4/getrusage, or
            // /usr/bin/time) closes that, and #1674 tracks it.
            ArgumentList = { "-c", "sleep 0.05" },
        });
        process.Should().NotBeNull();

        var exited = ReferenceProcessResources.WaitForExitAndCapture(process!, 30_000, out var resources);

        exited.Should().BeTrue();
        resources.PeakWorkingSetBytes.Should().NotBeNull(
            "a run too short to sample is still a run — dropping it biases every "
            + "aggregate toward the slower tool");
        resources.PeakWorkingSetBytes!.Value.Should().BeGreaterThan(0);
        resources.CpuMs.Should().NotBeNull();
    }

    [Fact]
    public void WaitForExitAndCapture_ShortTimeout_ReturnsFalseWithSampledResources()
    {
        using var process = Process.GetCurrentProcess();

        // WaitForExit's polling interval is 100ms, so this deliberately
        // samples the still-running test host once and then expires.
        var exited = ReferenceProcessResources.WaitForExitAndCapture(process, 1, out var resources);

        exited.Should().BeFalse();
        resources.PeakWorkingSetBytes.Should().BeGreaterThan(0);
    }

    // #1406 — Process caches WorkingSet64/PeakWorkingSet64 internally and only
    // re-queries the OS after Refresh() is called. Without that call, repeated
    // Capture() calls on the SAME Process handle returned the SAME near-startup
    // snapshot no matter how much the process's real RSS grew afterward — the
    // reference-performance bench reported 84 MiB peak RSS for a fixture that
    // /usr/bin/time -l measured at 2.53 GiB. This test grows the CURRENT
    // process's real RSS by >100 MiB between two Capture() calls on the SAME
    // Process handle and asserts the second call sees the growth; before the
    // Refresh() fix this failed (second capture stayed pinned to the first).
    [Fact]
    public void Capture_SameProcessHandle_ReflectsGrowthSinceThePreviousCall()
    {
        // On macOS the counter tracks the CURRENT resident set, so the baseline
        // must not include garbage the allocation below could get collected and
        // returned: in a full-suite run (2026-09-15) the "after" reading came in
        // 26 MiB LOWER than "before" (610 → 584 MiB) while it passed alone.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        using var process = Process.GetCurrentProcess();
        var before = ReferenceProcessResources.Capture(process);
        before.PeakWorkingSetBytes.Should().NotBeNull();

        // Allocate and fill >150 MiB so the OS-reported resident set genuinely
        // grows rather than relying on GC/allocator slack. Random bytes, not one
        // touched byte per page: mostly-zero pages are exactly what the macOS
        // memory compressor squeezes out of the resident set under pressure.
        const int chunkCount = 4;
        const int chunkSize = 50_000_000;
        var chunks = new byte[chunkCount][];
        for (var i = 0; i < chunkCount; i++)
        {
            chunks[i] = new byte[chunkSize];
            Random.Shared.NextBytes(chunks[i]);
        }

        var after = ReferenceProcessResources.Capture(process);
        after.PeakWorkingSetBytes.Should().NotBeNull();
        after.PeakWorkingSetBytes!.Value.Should().BeGreaterThan(
            before.PeakWorkingSetBytes!.Value + 100L * 1024 * 1024,
            "Capture() must Refresh() the process before reading OS counters, or growth after the first call is invisible");

        GC.KeepAlive(chunks);
    }
}
