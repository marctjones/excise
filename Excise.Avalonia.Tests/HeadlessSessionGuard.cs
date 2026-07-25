using Avalonia.Headless;

namespace Excise.Avalonia.Tests;

/// <summary>
/// Startup guard for the shared <see cref="HeadlessUnitTestSession"/> used by
/// the automation-peer tests (#631). On CI environments where the headless
/// Avalonia app lifecycle cannot start (observed on the displayless Linux
/// runner, intermittently on macOS — #752), a session-start failure must fail
/// or skip only the tests that need the session, never take down the whole
/// test host: a crashing host kills the assembly's other, unrelated tests
/// with no captured output, which is the worst possible outcome.
///
/// The guard starts the session once (lazily), verifies it can actually
/// dispatch work with a bounded no-op probe, and converts any managed startup
/// failure into a dynamic skip with the underlying reason. Test-body
/// exceptions are never touched — assertions still fail loudly.
/// </summary>
internal static class HeadlessSessionGuard
{
    /// <summary>Outcome of a guarded session start: exactly one of
    /// <paramref name="Session"/> / <paramref name="StartupFailure"/> is set.</summary>
    internal sealed record StartResult<T>(T? Session, Exception? StartupFailure) where T : class;

    private static readonly Lazy<StartResult<HeadlessUnitTestSession>> Holder = new(
        () => TryStart(
            () => HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessTestApp).Assembly),
            ProbeDispatch),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The shared assembly session, or a dynamic xunit skip (never a crash)
    /// if it could not be started on this platform.
    /// </summary>
    public static HeadlessUnitTestSession Session() =>
        SessionOrSkip(Holder.Value, nameof(HeadlessUnitTestSession));

    /// <summary>
    /// Runs <paramref name="start"/> then <paramref name="probe"/>, capturing
    /// any exception as a recorded startup failure instead of propagating it.
    /// Factored out (and internal) so the skip-not-crash contract is unit
    /// testable without needing a platform where startup actually fails.
    /// </summary>
    internal static StartResult<T> TryStart<T>(Func<T> start, Action<T> probe) where T : class
    {
        try
        {
            var session = start();
            probe(session);
            return new StartResult<T>(session, null);
        }
        catch (Exception ex)
        {
            return new StartResult<T>(null, ex);
        }
    }

    /// <summary>
    /// Returns the started session, or throws xunit's dynamic-skip exception
    /// (not the original startup failure) so only session-dependent tests are
    /// skipped, with the real reason preserved in the skip message.
    /// </summary>
    internal static T SessionOrSkip<T>(StartResult<T> result, string what) where T : class
    {
        if (result.Session is null)
        {
            var failure = result.StartupFailure!;
            Assert.Skip(
                $"{what} could not start on this platform " +
                $"({failure.GetType().Name}: {failure.Message}); " +
                "skipping the accessibility-peer tests instead of crashing the test host (#752).");
        }

        return result.Session;
    }

    /// <summary>
    /// Bounded no-op dispatch proving the session's UI thread is actually
    /// serving work — a session that starts but never dispatches would
    /// otherwise hang every test (#93-style missing-timeout trap).
    /// </summary>
    private static void ProbeDispatch(HeadlessUnitTestSession session)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        session.Dispatch(() => true, cts.Token).GetAwaiter().GetResult();
    }
}
