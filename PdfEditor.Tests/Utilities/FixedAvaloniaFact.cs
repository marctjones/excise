// Workaround for upstream Avalonia.Headless.XUnit 12.0.2 bug — the stock
// [AvaloniaFact] dispatches the xunit.v3 test runner onto the headless
// dispatcher thread via HeadlessUnitTestSession.Dispatch(...), which calls
// the internal DispatchCore(captureExecutionContext: false). That suppresses
// AsyncLocal flow, so xunit.v3's TestContext.Current returns an "idle"
// context on the dispatcher thread. xunit's own cleanup then reads
// TestContext.Current.KeyValueStorage, throws, and the test is reported
// failed with [Test Case Cleanup Failure] even though the body passed.
//
// This file replicates the [AvaloniaFact] -> Discoverer -> TestCase ->
// TestCaseRunner -> TestRunner chain (which is mostly internal-sealed in
// Avalonia.Headless.XUnit) but routes the dispatch through DispatchCore
// with captureExecutionContext: true via reflection — propagating
// TestContext.Current correctly onto the dispatcher thread.
//
// Tracking: see issue #337. When upstream Avalonia.Headless.XUnit ships a
// fix, replace [FixedAvaloniaFact] with [AvaloniaFact] and delete this file.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Reactive;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

namespace PdfEditor.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[XunitTestCaseDiscoverer(typeof(FixedAvaloniaFactDiscoverer))]
public sealed class FixedAvaloniaFactAttribute : FactAttribute
{
    public FixedAvaloniaFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber) { }
}

public class FixedAvaloniaFactDiscoverer : FactDiscoverer
{
    protected override IXunitTestCase CreateTestCase(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        IXunitTestMethod testMethod,
        IFactAttribute factAttribute)
    {
        var details = TestIntrospectionHelper.GetTestCaseDetails(
            discoveryOptions, testMethod, factAttribute);

        // Traits enter as IReadOnlyDictionary<string, IReadOnlyCollection<string>>;
        // XunitTestCase wants Dictionary<string, HashSet<string>>. Project across.
        var traits = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in testMethod.Traits)
            traits[key] = new HashSet<string>(values);

        return new FixedAvaloniaTestCase(
            details.ResolvedTestMethod,
            details.TestCaseDisplayName,
            details.UniqueID,
            details.Explicit,
            details.SkipExceptions,
            details.SkipReason,
            details.SkipType,
            details.SkipUnless,
            details.SkipWhen,
            traits,
            testMethodArguments: null,
            details.SourceFilePath,
            details.SourceLineNumber,
            details.Timeout);
    }
}

internal sealed class FixedAvaloniaTestCase : XunitTestCase, ISelfExecutingXunitTestCase
{
    public FixedAvaloniaTestCase(
        IXunitTestMethod testMethod,
        string testCaseDisplayName,
        string uniqueID,
        bool @explicit,
        Type[]? skipExceptions = null,
        string? skipReason = null,
        Type? skipType = null,
        string? skipUnless = null,
        string? skipWhen = null,
        Dictionary<string, HashSet<string>>? traits = null,
        object?[]? testMethodArguments = null,
        string? sourceFilePath = null,
        int? sourceLineNumber = null,
        int? timeout = null)
        : base(testMethod, testCaseDisplayName, uniqueID, @explicit,
            skipExceptions, skipReason, skipType, skipUnless, skipWhen,
            traits, testMethodArguments, sourceFilePath, sourceLineNumber,
            timeout)
    { }

    [Obsolete("Called by the de-serializer", error: true)]
    public FixedAvaloniaTestCase() { }

    public async ValueTask<RunSummary> Run(
        ExplicitOption explicitOption,
        IMessageBus messageBus,
        object?[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource)
    {
        var tests = await aggregator.RunAsync(
            CreateTests, (IReadOnlyCollection<IXunitTest>)Array.Empty<IXunitTest>());
        return await FixedAvaloniaTestCaseRunner.Instance.Run(
            this, tests, messageBus, aggregator, cancellationTokenSource,
            TestCaseDisplayName, SkipReason, explicitOption, constructorArguments);
    }
}

internal sealed class FixedAvaloniaTestCaseRunner
    : XunitTestCaseRunnerBase<FixedAvaloniaTestCaseRunnerContext, IXunitTestCase, IXunitTest>
{
    public static FixedAvaloniaTestCaseRunner Instance { get; } = new();
    private FixedAvaloniaTestCaseRunner() { }

    public async ValueTask<RunSummary> Run(
        IXunitTestCase testCase,
        IReadOnlyCollection<IXunitTest> tests,
        IMessageBus messageBus,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource,
        string displayName,
        string? skipReason,
        ExplicitOption explicitOption,
        object?[] constructorArguments)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(
            testCase.TestClass.Class.Assembly);
        var ctxt = new FixedAvaloniaTestCaseRunnerContext(
            testCase, tests, messageBus, aggregator, cancellationTokenSource,
            displayName, skipReason, explicitOption, constructorArguments, session);
        await using (ctxt)
        {
            await ctxt.InitializeAsync();
            return await Run(ctxt);
        }
    }

    protected override ValueTask<RunSummary> RunTest(
        FixedAvaloniaTestCaseRunnerContext ctxt, IXunitTest test)
    {
        return FixedAvaloniaTestRunner.Instance.Run(
            test,
            ctxt.MessageBus,
            ctxt.ConstructorArguments,
            ctxt.ExplicitOption,
            ctxt.Aggregator.Clone(),
            ctxt.CancellationTokenSource,
            ctxt.BeforeAfterTestAttributes,
            ctxt.Session);
    }
}

internal sealed class FixedAvaloniaTestCaseRunnerContext(
    IXunitTestCase testCase,
    IReadOnlyCollection<IXunitTest> tests,
    IMessageBus messageBus,
    ExceptionAggregator aggregator,
    CancellationTokenSource cancellationTokenSource,
    string displayName,
    string? skipReason,
    ExplicitOption explicitOption,
    object?[] constructorArguments,
    HeadlessUnitTestSession session)
    : XunitTestCaseRunnerContext(
        testCase, tests, messageBus, aggregator, cancellationTokenSource,
        displayName, skipReason, explicitOption, constructorArguments)
{
    public HeadlessUnitTestSession Session { get; } = session;
}

internal sealed class FixedAvaloniaTestRunner
    : XunitTestRunnerBase<FixedAvaloniaTestRunnerContext, IXunitTest>
{
    public static FixedAvaloniaTestRunner Instance { get; } = new();
    private FixedAvaloniaTestRunner() { }

    // The whole point of this file: invoke HeadlessUnitTestSession.DispatchCore
    // with captureExecutionContext: true. That parameter is internal, only
    // exposed via reflection.
    private static readonly MethodInfo DispatchCoreMethod =
        typeof(HeadlessUnitTestSession).GetMethod(
            "DispatchCore",
            BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException(
            "HeadlessUnitTestSession.DispatchCore not found — Avalonia.Headless internals " +
            "may have changed. The FixedAvaloniaFact workaround needs updating.");

    public async ValueTask<RunSummary> Run(
        IXunitTest test,
        IMessageBus messageBus,
        object?[] constructorArguments,
        ExplicitOption explicitOption,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource,
        IReadOnlyCollection<IBeforeAfterTestAttribute> beforeAfterAttributes,
        HeadlessUnitTestSession session)
    {
        var ctxt = new FixedAvaloniaTestRunnerContext(
            test, messageBus, explicitOption, aggregator,
            cancellationTokenSource, beforeAfterAttributes,
            constructorArguments, session);
        HeadlessWindowTracker.Ensure();
        await using (ctxt)
        {
            await ctxt.InitializeAsync();
            return await DispatchWithExecutionContext(session, async () =>
            {
                var summary = await Run(ctxt);
                // Close any windows the test opened. The headless test app has no
                // desktop lifetime, so windows shown with Show() and never closed
                // pile up on the shared dispatcher (90+ Show() / 2 Close() across
                // the suite) until a frame stalls and the blame-hang collector
                // kills the whole host — the residual #363 flakiness left after
                // the *_MatchesBaseline exclusion. Closing per-test keeps the
                // dispatcher's live-window set bounded.
                HeadlessWindowTracker.CloseAll();
                Dispatcher.UIThread.RunJobs(null);
                return summary;
            }, ctxt.CancellationTokenSource.Token);
        }
    }

    private static Task<TResult> DispatchWithExecutionContext<TResult>(
        HeadlessUnitTestSession session,
        Func<Task<TResult>> action,
        CancellationToken cancellationToken)
    {
        var generic = DispatchCoreMethod.MakeGenericMethod(typeof(TResult));
        return (Task<TResult>)generic.Invoke(
            session,
            new object[] { action, /* captureExecutionContext: */ true, cancellationToken })!;
    }
}

internal sealed class FixedAvaloniaTestRunnerContext(
    IXunitTest test,
    IMessageBus messageBus,
    ExplicitOption explicitOption,
    ExceptionAggregator aggregator,
    CancellationTokenSource cancellationTokenSource,
    IReadOnlyCollection<IBeforeAfterTestAttribute> beforeAfterTestAttributes,
    object?[] constructorArguments,
    HeadlessUnitTestSession session)
    : XunitTestRunnerContext(
        test, messageBus, explicitOption, aggregator,
        cancellationTokenSource, beforeAfterTestAttributes, constructorArguments)
{
    public HeadlessUnitTestSession Session { get; } = session;
}

/// <summary>
/// Tracks every <see cref="Window"/> opened during the headless test run and lets
/// the test runner close them after each test. The headless test application uses
/// <c>SetupWithoutStarting</c> with no desktop lifetime, so Avalonia keeps no
/// global window list and windows shown with <c>Show()</c> are never disposed —
/// they accumulate on the single shared dispatcher (the suite does 90+ Show() and
/// only 2 Close()) until a dispatcher frame stalls and the blame-hang collector
/// kills the entire test host. That is the residual #363 host-crash flakiness that
/// survived excluding the heavy *_MatchesBaseline render tests. We subscribe once
/// to the global <see cref="RoutedEvent"/> <c>Raised</c> streams for window
/// open/close (no per-test-file changes needed) and close the live set between
/// tests so the dispatcher never carries more than one test's windows.
/// </summary>
internal static class HeadlessWindowTracker
{
    private static readonly HashSet<Window> _open = new();
    private static readonly object _lock = new();
    private static int _initialized;

    public static void Ensure()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

        Window.WindowOpenedEvent.Raised.Subscribe(new AnonymousObserver<(object, Avalonia.Interactivity.RoutedEventArgs)>(t =>
        {
            if (t.Item1 is Window w) { lock (_lock) _open.Add(w); }
        }));
        Window.WindowClosedEvent.Raised.Subscribe(new AnonymousObserver<(object, Avalonia.Interactivity.RoutedEventArgs)>(t =>
        {
            if (t.Item1 is Window w) { lock (_lock) _open.Remove(w); }
        }));
    }

    /// <summary>Close all currently-open tracked windows (call on the UI thread).</summary>
    public static void CloseAll()
    {
        Window[] snapshot;
        lock (_lock) { snapshot = _open.ToArray(); _open.Clear(); }
        foreach (var w in snapshot)
        {
            try
            {
                DetachWindowContent(w);
                w.Close();
            }
            catch { /* a test may have left the window in an odd state; ignore */ }
        }
    }

    private static void DetachWindowContent(Window window)
    {
        try
        {
            foreach (var image in window.GetVisualDescendants().OfType<Image>())
                image.Source = null;

            window.DataContext = null;
        }
        catch
        {
            // Cleanup is best-effort; failing here would mask the test result.
        }
    }
}
