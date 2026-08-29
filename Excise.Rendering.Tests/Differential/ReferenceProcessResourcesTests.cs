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
}
