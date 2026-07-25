using AwesomeAssertions;

namespace Excise.Avalonia.Tests;

/// <summary>
/// Contract tests for <see cref="HeadlessSessionGuard"/> (#752): a headless
/// session that cannot start must surface as a per-test dynamic skip carrying
/// the real failure reason — never as a propagated exception (which xunit
/// reports as failure of whichever test touched the session first) and never
/// as a test-host crash. Simulated with throwing delegates because the real
/// failure only reproduces on a displayless CI runner.
/// </summary>
public class HeadlessSessionGuardTests
{
    [Fact]
    public void TryStart_CapturesStartFailure_InsteadOfPropagating()
    {
        var result = HeadlessSessionGuard.TryStart<object>(
            () => throw new InvalidOperationException("no display"),
            _ => { });

        result.Session.Should().BeNull();
        result.StartupFailure.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("no display");
    }

    [Fact]
    public void TryStart_CapturesProbeFailure_SoAHungSessionIsNotHandedToTests()
    {
        var result = HeadlessSessionGuard.TryStart<object>(
            () => new object(),
            _ => throw new OperationCanceledException("probe dispatch timed out"));

        result.Session.Should().BeNull();
        result.StartupFailure.Should().BeOfType<OperationCanceledException>();
    }

    [Fact]
    public void TryStart_ReturnsSession_WhenStartAndProbeSucceed()
    {
        var session = new object();
        bool probed = false;

        var result = HeadlessSessionGuard.TryStart(() => session, _ => probed = true);

        result.Session.Should().BeSameAs(session);
        result.StartupFailure.Should().BeNull();
        probed.Should().BeTrue("an unprobed session could hang every dependent test");
    }

    [Fact]
    public void SessionOrSkip_ThrowsDynamicSkip_CarryingTheStartupReason()
    {
        var failed = new HeadlessSessionGuard.StartResult<object>(
            null, new InvalidOperationException("XOpenDisplay failed"));

        // Plain try/catch, not Record.Exception: xunit v3's Record.Exception
        // deliberately rethrows dynamic-skip exceptions, which would turn
        // this test itself into a skip.
        Exception? thrown = null;
        try
        {
            HeadlessSessionGuard.SessionOrSkip(failed, "HeadlessUnitTestSession");
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        thrown.Should().NotBeNull("an unavailable session must not be returned as null");
        // Must be xunit's dynamic-skip exception, NOT the original startup
        // failure re-thrown (that would fail the test) and NOT a swallow.
        thrown!.GetType().FullName.Should().Contain("Skip",
            "session unavailability must skip the test, not fail it");
        thrown.Message.Should().Contain("XOpenDisplay failed",
            "the skip reason must preserve the real startup failure");
        thrown.Message.Should().Contain("#752");
    }

    [Fact]
    public void SessionOrSkip_ReturnsTheSession_WhenStartupSucceeded()
    {
        var session = new object();
        var ok = new HeadlessSessionGuard.StartResult<object>(session, null);

        HeadlessSessionGuard.SessionOrSkip(ok, "HeadlessUnitTestSession")
            .Should().BeSameAs(session);
    }
}
