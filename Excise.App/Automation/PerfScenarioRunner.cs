using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Excise.App.Services;

namespace Excise.App.Automation;

/// <summary>Knobs the outer harness passes in through the environment.</summary>
internal sealed record PerfScenarioOptions(
    string ScenarioFile,
    string ScenarioId,
    string OutputDirectory,
    int Repeat,
    TimeSpan StepTimeout,
    TimeSpan IdleTimeout,
    TimeSpan SampleWindow)
{
    internal const string ScenarioFileVariable = "EXCISE_PERF_SCENARIO";
    internal const string ScenarioIdVariable = "EXCISE_PERF_SCENARIO_ID";
    internal const string OutputVariable = "EXCISE_PERF_SCENARIO_OUT";
    internal const string RepeatVariable = "EXCISE_PERF_SCENARIO_REPEAT";
    internal const string SampleWindowVariable = "EXCISE_PERF_SCENARIO_SAMPLE_MS";

    /// <summary>
    /// True only when a scenario file is named. Absent the variable this whole
    /// subsystem is inert — the same posture as <see cref="VisualTraceRunner"/>,
    /// and the reason the runner can ship in the product without being an
    /// automation surface: it is not a listener, it takes no input, and it
    /// reads one local file the user named.
    /// </summary>
    internal static bool IsRequested =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ScenarioFileVariable));

    internal static PerfScenarioOptions FromEnvironment()
    {
        var file = Environment.GetEnvironmentVariable(ScenarioFileVariable)
            ?? throw new InvalidOperationException($"{ScenarioFileVariable} is not set.");
        var id = Environment.GetEnvironmentVariable(ScenarioIdVariable);
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException($"{ScenarioIdVariable} is not set.");

        var output = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(output))
            output = Path.Combine(Path.GetDirectoryName(file) ?? ".", "gui-perf-out");

        return new PerfScenarioOptions(
            ScenarioFile: file,
            ScenarioId: id,
            OutputDirectory: output,
            Repeat: ReadInt(RepeatVariable, 1),
            StepTimeout: TimeSpan.FromSeconds(120),
            IdleTimeout: TimeSpan.FromSeconds(60),
            SampleWindow: TimeSpan.FromMilliseconds(ReadInt(SampleWindowVariable, 20_000)));
    }

    private static int ReadInt(string name, int fallback) =>
        int.TryParse(
            Environment.GetEnvironmentVariable(name),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;

    /// <summary>
    /// Resolve a scenario's document path against the repository, not the
    /// process working directory.
    /// </summary>
    /// <remarks>
    /// Scenario documents are written repo-relative (<c>test-pdfs/…</c>). They
    /// must not depend on the launcher having <c>cd</c>'d to the repo root
    /// first: a scenario that silently fails to open still produces a full row
    /// of plausible-looking numbers. The scenario file lives at
    /// <c>&lt;root&gt;/tests/gui-perf-scenarios.json</c>, so its grandparent is
    /// the root — the same walk the headless tests use.
    /// </remarks>
    internal string ResolveDocument(string document)
    {
        if (Path.IsPathRooted(document)) return document;

        var scenarioDirectory = Path.GetDirectoryName(Path.GetFullPath(ScenarioFile));
        var root = Path.GetDirectoryName(scenarioDirectory);
        if (!string.IsNullOrEmpty(root))
        {
            var candidate = Path.Combine(root, document);
            if (File.Exists(candidate)) return candidate;
        }

        return Path.GetFullPath(document);
    }
}

/// <summary>
/// Runs one named scenario inside the real application and quits (#1497).
/// </summary>
/// <remarks>
/// <para>This exists because every live GUI performance verdict so far — #1477,
/// #1478, #1481, #1492, #1483's L1–L5 — needed the owner to page, scroll and
/// open files by hand, and to run <c>sudo memory_pressure</c> himself. That
/// costs his time, the results are not repeatable, and they vary with how the
/// app was driven. Worse, the obvious way to automate it made the numbers
/// wrong: background accessibility automation pegs the app at ~96% CPU and
/// muddied the first #1477 reading.</para>
///
/// <para>So the driving happens <b>in process, through the view model and the
/// viewer's own public APIs</b> — no synthetic input, no accessibility, no
/// second process poking at the UI. The price of that choice is that this is
/// not exactly a user's input path: it skips input dispatch and hit testing.
/// That residual is not waved away, it is <i>measured</i> — the harness's
/// driving-fidelity calibration compares one step driven this way against the
/// same step driven by real keyboard input, and reports the difference.</para>
///
/// <para>⚠️ <b>This runner changes no application behaviour.</b> It sets no GC
/// flag, installs no policy, and forces no collection. It is a driver and a
/// sampler. The one thing it deliberately does that a user does not is call
/// the cache-trim coordinator directly, which is what replaces
/// <c>sudo memory_pressure</c> in the runbook — recorded per step, and the
/// macOS pressure-signal delivery itself was separately verified live on
/// 2026-09-14.</para>
/// </remarks>
internal static class PerfScenarioRunner
{
    /// <summary>
    /// Execute every step in order, journalling a boundary after each.
    /// </summary>
    /// <remarks>
    /// Returns the number of steps that did not complete cleanly. The caller
    /// shuts the app down; this method never calls <c>Shutdown</c> itself so
    /// the sequencing stays testable.
    /// </remarks>
    internal static async Task<PerfScenarioResult> RunAsync(
        IPerfScenarioTarget target,
        PerfScenario scenario,
        PerfScenarioOptions options,
        PerfStepJournal journal,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var failures = 0;
        var steps = 0;

        // A boundary before any step runs: the floor every later number is read
        // against. Without it "after close" has nothing to come back down TO.
        await BoundaryAsync(
            target, journal, options, scenario, "baseline", "baseline",
            0, true, null, clock, cancellationToken).ConfigureAwait(true);

        foreach (var step in scenario.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            steps++;

            var stepClock = Stopwatch.StartNew();
            var ok = true;
            string? note = null;

            try
            {
                using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                stepCts.CancelAfter(options.StepTimeout);
                note = await ExecuteAsync(target, step, options, stepCts.Token).ConfigureAwait(true);
                if (note != null) ok = false;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A step that wedges — the unsaved-changes dialog on close is
                // the realistic one — must become a reported timeout, not a
                // hung unattended run.
                ok = false;
                note = $"timeout after {options.StepTimeout.TotalSeconds:F0}s";
            }
            catch (Exception ex)
            {
                ok = false;
                note = $"{ex.GetType().Name}: {ex.Message}";
            }

            stepClock.Stop();
            if (!ok) failures++;

            await BoundaryAsync(
                target, journal, options, scenario, step.Name, step.Op.ToString().ToLowerInvariant(),
                stepClock.Elapsed.TotalMilliseconds, ok, note, clock, cancellationToken).ConfigureAwait(true);
        }

        return new PerfScenarioResult(
            scenario.Id,
            options.Repeat,
            steps,
            failures,
            journal.AcknowledgedBoundaries,
            journal.UnacknowledgedBoundaries,
            clock.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Perform one step. Returns null on success, or a note describing why not.
    /// </summary>
    private static async Task<string?> ExecuteAsync(
        IPerfScenarioTarget target,
        PerfScenarioStep step,
        PerfScenarioOptions options,
        CancellationToken cancellationToken)
    {
        switch (step.Op)
        {
            case PerfStepOp.Mark:
                return null;

            case PerfStepOp.Open:
                var document = options.ResolveDocument(step.Document!);
                if (!File.Exists(document))
                    return $"document not found: {document}";
                await target.OpenAsync(document, cancellationToken).ConfigureAwait(true);
                return null;

            case PerfStepOp.Close:
                await target.CloseAsync(cancellationToken).ConfigureAwait(true);
                return null;

            case PerfStepOp.WaitIdle:
                return await target.WaitForIdleAsync(options.IdleTimeout, cancellationToken).ConfigureAwait(true)
                    ? null
                    : $"still busy after {options.IdleTimeout.TotalSeconds:F0}s";

            case PerfStepOp.Idle:
                await Task.Delay(TimeSpan.FromSeconds(step.Seconds!.Value), cancellationToken).ConfigureAwait(true);
                return null;

            case PerfStepOp.PageBy:
                await target.PageByAsync(step.Count!.Value, cancellationToken).ConfigureAwait(true);
                return null;

            case PerfStepOp.ScrollPages:
                await target.ScrollPagesAsync(step.Count!.Value, cancellationToken).ConfigureAwait(true);
                return null;

            case PerfStepOp.Zoom:
                await target.SetZoomAsync(step.Value!.Value, cancellationToken).ConfigureAwait(true);
                return null;

            case PerfStepOp.ViewMode:
                await target.SetViewModeAsync(step.Level!, cancellationToken).ConfigureAwait(true);
                return null;

            case PerfStepOp.Search:
                await target.SearchAsync(step.Text!, cancellationToken).ConfigureAwait(true);
                return null;

            case PerfStepOp.RedactText:
                await target.RedactTextAsync(step.Text!, cancellationToken).ConfigureAwait(true);
                return null;

            case PerfStepOp.Trim:
                return await target.TrimAsync(step.Level!, cancellationToken).ConfigureAwait(true)
                    ? null
                    : "trim request refused (pressure trims disabled in Preferences → Performance)";

            default:
                return $"unhandled op {step.Op}";
        }
    }

    private static async Task BoundaryAsync(
        IPerfScenarioTarget target,
        PerfStepJournal journal,
        PerfScenarioOptions options,
        PerfScenario scenario,
        string stepName,
        string op,
        double wallMs,
        bool ok,
        string? note,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        // Marker first, so the metrics JSONL and the journal agree on the
        // boundary even if the ack wait times out.
        AppMetrics.RecordScenarioStep(scenario.Id, stepName, op, wallMs);

        var sample = target.Sample();
        var boundaryClock = Stopwatch.StartNew();
        var record = new PerfStepRecord(
            Scenario: scenario.Id,
            Repeat: options.Repeat,
            Step: stepName,
            Op: op,
            WallMs: wallMs,
            MonotonicMs: (long)clock.Elapsed.TotalMilliseconds,
            Ok: ok,
            Note: note,
            Sample: sample);

        await journal.RecordAsync(record, cancellationToken).ConfigureAwait(true);
        boundaryClock.Stop();

        // The time spent holding still for the harness is recorded separately
        // and is never charged to the step it follows.
        AppMetrics.RecordScenarioSampleWindow(
            scenario.Id, stepName, boundaryClock.Elapsed.TotalMilliseconds);
    }
}

/// <summary>What one scenario run produced, for the run manifest.</summary>
internal sealed record PerfScenarioResult(
    string ScenarioId,
    int Repeat,
    int Steps,
    int Failures,
    int AcknowledgedBoundaries,
    int UnacknowledgedBoundaries,
    double TotalMs);
