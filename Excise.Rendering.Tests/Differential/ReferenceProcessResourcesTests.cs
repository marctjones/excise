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
        // macOS and Windows may discard accounting as soon as a short-lived
        // process exits; null is the documented cross-platform result.
        (resources.CpuMs is null || resources.CpuMs.Value >= 0).Should().BeTrue();
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
        using var process = Process.GetCurrentProcess();
        var before = ReferenceProcessResources.Capture(process);
        before.PeakWorkingSetBytes.Should().NotBeNull();

        // Allocate and touch >150 MiB so the OS-reported resident set
        // genuinely grows rather than relying on GC/allocator slack.
        const int chunkCount = 4;
        const int chunkSize = 50_000_000;
        var chunks = new byte[chunkCount][];
        for (var i = 0; i < chunkCount; i++)
        {
            chunks[i] = new byte[chunkSize];
            for (var offset = 0; offset < chunkSize; offset += 4096)
                chunks[i][offset] = 1; // touch each page so it is actually resident
        }

        var after = ReferenceProcessResources.Capture(process);
        after.PeakWorkingSetBytes.Should().NotBeNull();
        after.PeakWorkingSetBytes!.Value.Should().BeGreaterThan(
            before.PeakWorkingSetBytes!.Value + 100L * 1024 * 1024,
            "Capture() must Refresh() the process before reading OS counters, or growth after the first call is invisible");

        GC.KeepAlive(chunks);
    }
}
