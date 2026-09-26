using AwesomeAssertions;
using Excise.Rendering.Differential;
using Xunit;

// The launch/drain/timeout path every reference-oracle wrapper shares (#1757). Kept with
// ReferenceProcessResourcesTests in the deterministic suite: it needs only a POSIX shell.
namespace Excise.Rendering.Tests.Performance;

public sealed class ReferenceProcessTests
{
    private const int Megabyte = 1_000_000;

    // A child that fills a pipe nobody is reading blocks forever, so a wrapper that waits before
    // it drains reports TIMEOUT for a tool that finished. 1 MB on each pipe is 15x the 64 KB buffer.
    [Fact]
    public void Run_ChildFillingBothPipes_CompletesWithEveryByteCaptured()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs a POSIX shell");

        var run = ReferenceProcess.Run("/bin/sh",
            new[] { "-c", $"head -c {Megabyte} /dev/zero | tr '\\0' o; head -c {Megabyte} /dev/zero | tr '\\0' e >&2" },
            timeoutMs: 30_000);

        run.Started.Should().BeTrue();
        run.TimedOut.Should().BeFalse("the pipes are drained while the child runs");
        run.ExitCode.Should().Be(0);
        run.Stdout.Length.Should().Be(Megabyte);
        run.Stderr.Length.Should().Be(Megabyte);
    }

    [Fact]
    public void Run_ChildOutlivingTheTimeout_IsKilledAndReportedTimedOut()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs a POSIX shell");

        var run = ReferenceProcess.Run("/bin/sh", new[] { "-c", "sleep 60" }, timeoutMs: 300);

        run.Started.Should().BeTrue();
        run.TimedOut.Should().BeTrue();
        run.ExitCode.Should().Be(-1, "an unfinished run has no exit code to trust");
    }

    [Fact]
    public void Probes_DistinguishLaunchingFromSucceeding()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs a POSIX shell");

        ReferenceProcess.IsLaunchable("/bin/sh", 5_000, "-c", "exit 3").Should().BeTrue("any exit code means installed");
        ReferenceProcess.ExitsZero("/bin/sh", 5_000, "-c", "exit 3").Should().BeFalse();
        ReferenceProcess.ExitsZero("/bin/sh", 5_000, "-c", "exit 0").Should().BeTrue();
        ReferenceProcess.IsLaunchable("/no/such/excise-oracle", 5_000).Should().BeFalse();
        ReferenceProcess.ExitsZero("/no/such/excise-oracle", 5_000).Should().BeFalse();
    }
}
