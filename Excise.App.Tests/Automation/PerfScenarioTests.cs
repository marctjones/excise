using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Excise.App.Automation;
using FluentAssertions;
using Xunit;

namespace Excise.App.Tests.Automation;

/// <summary>
/// The scenario-runner's parsing and sequencing (#1497).
/// </summary>
/// <remarks>
/// <para>These are ordinary headless tests on purpose. They cover the parts of
/// the harness that are machine-independent — does the scenario file parse,
/// does a bad one fail loudly, do steps run in order, does a wedged step become
/// a reported failure instead of a hang. They assert <b>nothing</b> about
/// timings or memory: absolute numbers from one machine must never fail a
/// build, and the live measurement is deliberately not a gate row
/// (see <c>tests/gates-tooling.txt</c>).</para>
///
/// <para>What they exist to stop: a scenario file that silently does nothing.
/// A typo'd op, a missing document, a scenario with no steps and a mis-named id
/// would all produce a launch, a clean exit, and a row of numbers that measured
/// nothing — the vacuous-pass shape the suite runner and the
/// reference-performance bench both refuse by construction.</para>
/// </remarks>
public class PerfScenarioTests
{
    // ------------------------------------------------------------- the file

    [Fact]
    public void ShippedScenarioFile_Parses_AndEveryScenarioHasAWhy()
    {
        var path = TryFindRepoFile("tests", "gui-perf-scenarios.json");
        Assert.SkipWhen(path == null, "tests/gui-perf-scenarios.json not found above the test output directory.");

        var scenarios = PerfScenarioFile.Parse(File.ReadAllText(path!));

        scenarios.Should().NotBeEmpty();
        scenarios.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.Why),
            "a scenario nobody can explain is a scenario nobody will maintain");
        scenarios.Should().OnlyContain(s => s.Steps.Count > 0);
    }

    [Fact]
    public void ShippedScenarioFile_HasTheCalibrationBaseline()
    {
        var path = TryFindRepoFile("tests", "gui-perf-scenarios.json");
        Assert.SkipWhen(path == null, "tests/gui-perf-scenarios.json not found above the test output directory.");

        var scenarios = PerfScenarioFile.Parse(File.ReadAllText(path!));

        // Without a document-free scenario there is nothing to price the
        // runner's own cost against, and every other number loses its floor.
        var nullScenario = scenarios.Should().ContainSingle(s => s.Id == "null").Subject;
        nullScenario.Steps.Should().NotContain(s => s.Op == PerfStepOp.Open,
            "the calibration baseline must never open a document");
    }

    [Fact]
    public void ShippedScenarioFile_NeverClosesAMutatedDocument()
    {
        var path = TryFindRepoFile("tests", "gui-perf-scenarios.json");
        Assert.SkipWhen(path == null, "tests/gui-perf-scenarios.json not found above the test output directory.");

        var scenarios = PerfScenarioFile.Parse(File.ReadAllText(path!));

        // The close path calls ConfirmDiscardUnsavedChangesAsync, which shows a
        // modal dialog on a dirty document. An unattended run would sit on it
        // until the step timeout — measuring the timeout, not the close.
        foreach (var scenario in scenarios)
        {
            var mutated = false;
            foreach (var step in scenario.Steps)
            {
                if (step.Op == PerfStepOp.RedactText) mutated = true;
                if (step.Op == PerfStepOp.Close)
                {
                    mutated.Should().BeFalse(
                        $"scenario '{scenario.Id}' closes a document it mutated; the close would " +
                        "block on the unsaved-changes dialog");
                }
            }
        }
    }

    [Fact]
    public void ShippedScenarioFile_ReferencedDocumentsExist()
    {
        var path = TryFindRepoFile("tests", "gui-perf-scenarios.json");
        Assert.SkipWhen(path == null, "tests/gui-perf-scenarios.json not found above the test output directory.");

        var root = Path.GetDirectoryName(Path.GetDirectoryName(path!))!;
        var scenarios = PerfScenarioFile.Parse(File.ReadAllText(path!));
        var documents = scenarios
            .SelectMany(s => s.Steps)
            .Where(s => s.Document != null)
            .Select(s => s.Document!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        documents.Should().NotBeEmpty();

        // The corpora are gitignored, so a missing fixture is an environment
        // fact, not a defect — but a PATH that is misspelt would also look
        // missing, so report which ones so the difference is visible.
        var missing = documents.Where(d => !File.Exists(Path.Combine(root, d))).ToList();
        Assert.SkipWhen(missing.Count == documents.Count,
            $"no scenario fixtures present (corpora not downloaded): {string.Join(", ", missing)}");
        missing.Should().BeEmpty(
            "some fixtures resolve and others do not, which points at a path typo rather than a " +
            "missing corpus");
    }

    // ------------------------------------------------------- parse failures

    [Fact]
    public void Parse_WrongSchemaVersion_Throws() =>
        FluentActions.Invoking(() => PerfScenarioFile.Parse(
                """{"schemaVersion": 99, "scenarios": []}"""))
            .Should().Throw<InvalidOperationException>().WithMessage("*schemaVersion*");

    [Fact]
    public void Parse_NoScenarios_Throws() =>
        FluentActions.Invoking(() => PerfScenarioFile.Parse(
                """{"schemaVersion": 1, "scenarios": []}"""))
            .Should().Throw<InvalidOperationException>();

    [Fact]
    public void Parse_ScenarioWithNoSteps_Throws() =>
        FluentActions.Invoking(() => PerfScenarioFile.Parse(
                """{"schemaVersion":1,"scenarios":[{"id":"a","why":"w","steps":[]}]}"""))
            .Should().Throw<InvalidOperationException>().WithMessage("*no steps*");

    [Fact]
    public void Parse_MissingWhy_Throws() =>
        FluentActions.Invoking(() => PerfScenarioFile.Parse(
                """{"schemaVersion":1,"scenarios":[{"id":"a","steps":[{"op":"mark"}]}]}"""))
            .Should().Throw<InvalidOperationException>();

    [Fact]
    public void Parse_UnknownOp_ThrowsAndListsTheKnownOnes() =>
        FluentActions.Invoking(() => PerfScenarioFile.Parse(
                """{"schemaVersion":1,"scenarios":[{"id":"a","why":"w","steps":[{"op":"teleport"}]}]}"""))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*teleport*").And.Message.Should().Contain("scrollpages");

    [Fact]
    public void Parse_DuplicateScenarioId_Throws() =>
        FluentActions.Invoking(() => PerfScenarioFile.Parse(
                """
                {"schemaVersion":1,"scenarios":[
                  {"id":"a","why":"w","steps":[{"op":"mark"}]},
                  {"id":"a","why":"w","steps":[{"op":"mark"}]}]}
                """))
            .Should().Throw<InvalidOperationException>().WithMessage("*duplicate*");

    [Theory]
    [InlineData("""{"op":"open"}""", "document")]
    [InlineData("""{"op":"idle"}""", "seconds")]
    [InlineData("""{"op":"idle","seconds":0}""", "seconds")]
    [InlineData("""{"op":"scrollPages"}""", "count")]
    [InlineData("""{"op":"pageBy","count":0}""", "count")]
    [InlineData("""{"op":"zoom"}""", "value")]
    [InlineData("""{"op":"zoom","value":-1}""", "value")]
    [InlineData("""{"op":"viewMode"}""", "single|continuous")]
    [InlineData("""{"op":"search"}""", "text")]
    [InlineData("""{"op":"redactText"}""", "text")]
    [InlineData("""{"op":"trim"}""", "background|warn|critical")]
    public void Parse_StepMissingItsRequiredArgument_ThrowsAtParseTime(string step, string expected) =>
        FluentActions.Invoking(() => PerfScenarioFile.Parse(
                $$"""{"schemaVersion":1,"scenarios":[{"id":"a","why":"w","steps":[{{step}}]}]}"""))
            .Should().Throw<InvalidOperationException>().WithMessage($"*{expected}*");

    [Fact]
    public void Select_UnknownId_ThrowsAndNamesWhatExists()
    {
        var scenarios = PerfScenarioFile.Parse(
            """{"schemaVersion":1,"scenarios":[{"id":"real","why":"w","steps":[{"op":"mark"}]}]}""");

        // A name that matches nothing is a TYPO, not "run everything".
        FluentActions.Invoking(() => PerfScenarioFile.Select(scenarios, "typo"))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*typo*").And.Message.Should().Contain("real");
    }

    // ------------------------------------------------------------- inertness

    [Fact]
    public void IsRequested_IsFalse_WhenTheEnvironmentVariableIsUnset()
    {
        var previous = Environment.GetEnvironmentVariable(PerfScenarioOptions.ScenarioFileVariable);
        Environment.SetEnvironmentVariable(PerfScenarioOptions.ScenarioFileVariable, null);
        try
        {
            // The runner ships in the product. Absent the variable it must be
            // inert — it is not an automation listener and must never become one.
            PerfScenarioOptions.IsRequested.Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(PerfScenarioOptions.ScenarioFileVariable, previous);
        }
    }

    // -------------------------------------------------------------- journal

    [Fact]
    public async Task Journal_WritesOneParseableLinePerBoundary_AndMarksItUnacknowledged()
    {
        var directory = NewTempDirectory();
        try
        {
            using var journal = new PerfStepJournal(directory)
            {
                // No harness is attached, so the boundary must time out fast
                // rather than block the run.
                AckTimeout = TimeSpan.FromMilliseconds(120),
                AckPollInterval = TimeSpan.FromMilliseconds(20),
            };

            var acknowledged = await journal.RecordAsync(Record("open"), CancellationToken.None);

            acknowledged.Should().BeFalse();
            journal.UnacknowledgedBoundaries.Should().Be(1);
            journal.AcknowledgedBoundaries.Should().Be(0);

            var lines = File.ReadAllLines(Path.Combine(directory, PerfStepJournal.StepsFileName));
            lines.Should().HaveCount(1);
            var parsed = JsonDocument.Parse(lines[0]).RootElement;
            parsed.GetProperty("seq").GetInt32().Should().Be(1);
            parsed.GetProperty("step").GetString().Should().Be("open");
            parsed.GetProperty("liveHeapBytes").GetInt64().Should().BeGreaterThan(0);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Journal_PublishesTheMarker_AndProceedsWhenTheHarnessAcks()
    {
        var directory = NewTempDirectory();
        try
        {
            using var journal = new PerfStepJournal(directory)
            {
                AckTimeout = TimeSpan.FromSeconds(5),
                AckPollInterval = TimeSpan.FromMilliseconds(20),
            };

            // Stand in for scripts/run-gui-perf-scenarios.sh's ack_loop: read
            // the marker, then write it back as the ack.
            var acker = Task.Run(async () =>
            {
                var markerPath = Path.Combine(directory, PerfStepJournal.MarkerFileName);
                for (var i = 0; i < 200; i++)
                {
                    if (File.Exists(markerPath))
                    {
                        var seq = File.ReadAllText(markerPath).Trim();
                        if (!string.IsNullOrEmpty(seq))
                        {
                            File.WriteAllText(Path.Combine(directory, PerfStepJournal.AckFileName), seq);
                            return;
                        }
                    }

                    await Task.Delay(20);
                }
            });

            var acknowledged = await journal.RecordAsync(Record("scroll"), CancellationToken.None);
            await acker;

            acknowledged.Should().BeTrue();
            journal.AcknowledgedBoundaries.Should().Be(1);
            File.ReadAllText(Path.Combine(directory, PerfStepJournal.MarkerFileName))
                .Trim().Should().Be("1");
        }
        finally
        {
            Cleanup(directory);
        }
    }

    // ------------------------------------------------------------ sequencing

    [Fact]
    public async Task Runner_ExecutesEveryStepInOrder_AndWritesABaselineBoundaryFirst()
    {
        var target = new FakeTarget();
        var scenario = PerfScenarioFile.Parse(
            """
            {"schemaVersion":1,"scenarios":[{"id":"s","why":"w","steps":[
              {"op":"mark","label":"pre-open"},
              {"op":"open","document":"x.pdf"},
              {"op":"waitIdle"},
              {"op":"scrollPages","count":3},
              {"op":"trim","level":"warn"},
              {"op":"close"}]}]}
            """)[0];

        var (result, records) = await RunAsync(target, scenario);

        target.Calls.Should().Equal("open:x.pdf", "waitIdle", "scrollPages:3", "trim:warn", "close");
        result.Steps.Should().Be(6);
        result.Failures.Should().Be(0);

        // The baseline boundary is what "after close" is measured against.
        records.First().GetProperty("step").GetString().Should().Be("baseline");
        records.Select(r => r.GetProperty("step").GetString())
            .Should().Equal("baseline", "pre-open", "open", "waitidle", "scrollpages", "trim", "close");
    }

    [Fact]
    public async Task Runner_RecordsARefusedTrim_RatherThanReportingATrimThatNeverHappened()
    {
        var target = new FakeTarget { TrimAllowed = false };
        var scenario = PerfScenarioFile.Parse(
            """{"schemaVersion":1,"scenarios":[{"id":"s","why":"w","steps":[{"op":"trim","level":"warn"}]}]}""")[0];

        var (result, records) = await RunAsync(target, scenario);

        result.Failures.Should().Be(1);
        var trim = records.Single(r => r.GetProperty("step").GetString() == "trim");
        trim.GetProperty("ok").GetBoolean().Should().BeFalse();
        trim.GetProperty("note").GetString().Should().Contain("refused");
    }

    [Fact]
    public async Task Runner_RecordsAWaitIdleTimeout_AndKeepsGoing()
    {
        var target = new FakeTarget { IdleSettles = false };
        var scenario = PerfScenarioFile.Parse(
            """
            {"schemaVersion":1,"scenarios":[{"id":"s","why":"w","steps":[
              {"op":"waitIdle"},
              {"op":"mark","label":"after"}]}]}
            """)[0];

        var (result, records) = await RunAsync(target, scenario);

        result.Failures.Should().Be(1);
        records.Single(r => r.GetProperty("step").GetString() == "waitidle")
            .GetProperty("note").GetString().Should().Contain("still busy");

        // A failing step must not abort the rest: the later boundaries are the
        // ones that answer "did it come back down".
        records.Should().Contain(r => r.GetProperty("step").GetString() == "after");
    }

    [Fact]
    public async Task Runner_RecordsAThrowingStepAsAFailure_AndKeepsGoing()
    {
        var target = new FakeTarget { OpenThrows = true };
        var scenario = PerfScenarioFile.Parse(
            """
            {"schemaVersion":1,"scenarios":[{"id":"s","why":"w","steps":[
              {"op":"open","document":"x.pdf"},
              {"op":"mark","label":"after"}]}]}
            """)[0];

        var (result, records) = await RunAsync(target, scenario);

        result.Failures.Should().Be(1);
        records.Single(r => r.GetProperty("step").GetString() == "open")
            .GetProperty("note").GetString().Should().Contain("boom");
        records.Should().Contain(r => r.GetProperty("step").GetString() == "after");
    }

    [Fact]
    public async Task Runner_TurnsAWedgedStepIntoAReportedTimeout_NotAHang()
    {
        // The realistic wedge is closing a dirty document: the close path waits
        // on a modal dialog nobody is there to answer. An unattended run must
        // come back with a row that says so.
        var target = new FakeTarget { CloseHangs = true };
        var scenario = PerfScenarioFile.Parse(
            """{"schemaVersion":1,"scenarios":[{"id":"s","why":"w","steps":[{"op":"close"}]}]}""")[0];

        var (result, records) = await RunAsync(
            target, scenario, stepTimeout: TimeSpan.FromMilliseconds(250));

        result.Failures.Should().Be(1);
        records.Single(r => r.GetProperty("step").GetString() == "close")
            .GetProperty("note").GetString().Should().Contain("timeout");
    }

    // ------------------------------------------------------------- helpers

    private static async Task<(PerfScenarioResult Result, List<JsonElement> Records)> RunAsync(
        IPerfScenarioTarget target,
        PerfScenario scenario,
        TimeSpan? stepTimeout = null)
    {
        var directory = NewTempDirectory();
        try
        {
            var options = new PerfScenarioOptions(
                ScenarioFile: "unused",
                ScenarioId: scenario.Id,
                OutputDirectory: directory,
                Repeat: 1,
                StepTimeout: stepTimeout ?? TimeSpan.FromSeconds(5),
                IdleTimeout: TimeSpan.FromMilliseconds(150),
                SampleWindow: TimeSpan.FromMilliseconds(60));

            using var journal = new PerfStepJournal(directory)
            {
                AckTimeout = TimeSpan.FromMilliseconds(60),
                AckPollInterval = TimeSpan.FromMilliseconds(20),
            };

            var result = await PerfScenarioRunner.RunAsync(target, scenario, options, journal);

            var records = File
                .ReadAllLines(Path.Combine(directory, PerfStepJournal.StepsFileName))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonDocument.Parse(l).RootElement.Clone())
                .ToList();

            return (result, records);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    private static PerfStepRecord Record(string step) => new(
        Scenario: "s", Repeat: 1, Step: step, Op: step,
        WallMs: 1, MonotonicMs: 1, Ok: true, Note: null,
        Sample: PerfSample.FromRuntime());

    private static string NewTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "excise-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Cleanup(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
    }

    private static string? TryFindRepoFile(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// Records what the runner asked for. No Avalonia, no display, no fixture —
    /// which is the whole reason <see cref="PerfScenarioRunner"/> drives an
    /// interface rather than a window.
    /// </summary>
    private sealed class FakeTarget : IPerfScenarioTarget
    {
        internal List<string> Calls { get; } = new();

        internal bool TrimAllowed { get; init; } = true;

        internal bool IdleSettles { get; init; } = true;

        internal bool OpenThrows { get; init; }

        internal bool CloseHangs { get; init; }

        public Task OpenAsync(string path, CancellationToken cancellationToken)
        {
            Calls.Add("open:" + path);
            if (OpenThrows) throw new InvalidOperationException("boom");
            return Task.CompletedTask;
        }

        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            Calls.Add("close");
            if (CloseHangs) await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public Task<bool> WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add("waitIdle");
            return Task.FromResult(IdleSettles);
        }

        public Task PageByAsync(int pages, CancellationToken cancellationToken)
        {
            Calls.Add("pageBy:" + pages);
            return Task.CompletedTask;
        }

        public Task ScrollPagesAsync(int pages, CancellationToken cancellationToken)
        {
            Calls.Add("scrollPages:" + pages);
            return Task.CompletedTask;
        }

        public Task SetZoomAsync(double zoom, CancellationToken cancellationToken)
        {
            Calls.Add("zoom:" + zoom.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Task.CompletedTask;
        }

        public Task SetViewModeAsync(string mode, CancellationToken cancellationToken)
        {
            Calls.Add("viewMode:" + mode);
            return Task.CompletedTask;
        }

        public Task SearchAsync(string term, CancellationToken cancellationToken)
        {
            Calls.Add("search:" + term);
            return Task.CompletedTask;
        }

        public Task RedactTextAsync(string term, CancellationToken cancellationToken)
        {
            Calls.Add("redact:" + term);
            return Task.CompletedTask;
        }

        public Task<bool> TrimAsync(string level, CancellationToken cancellationToken)
        {
            Calls.Add("trim:" + level);
            return Task.FromResult(TrimAllowed);
        }

        public PerfSample Sample() => PerfSample.FromRuntime();
    }
}
