using AwesomeAssertions;
using Xunit;

using RenderProgram = Excise.RenderTools.Program;

namespace Excise.Cli.Tests;

/// <summary>
/// #1389 — the reference-performance gate's two REFUSE-TO-COMPARE rules, and the fixture
/// filter's refusal to run the wrong thing.
///
/// <para>All three exist because they failed live while the bench was being extended, and
/// each failed <b>silently in the direction of a confident wrong answer</b> — which is the
/// only kind of bench defect that matters, because nobody re-derives a number the tool
/// already printed.</para>
///
/// <para>These run in milliseconds: the gate is a pure function over already-recorded runs,
/// so none of this launches a renderer or touches a PDF.</para>
/// </summary>
public class ReferencePerformanceGateTests
{
    private const double MaxTime = 1.5, MaxRss = 1.25;

    /// <summary>
    /// Native AOT and the JIT generate different code for the same source. Measured on
    /// <c>fifty-transparency-groups</c>: 1568.7 ms under R2R, 2499.2 ms under AOT, with
    /// under 1% spread inside each mode — so a cross-mode ratio reports a 1.59x
    /// "regression" that is entirely codegen. The gate must say so, not score it.
    /// </summary>
    [Fact]
    public void Gate_RefusesToScore_WhenTheBaselineUsedADifferentCodegenMode()
    {
        var current = Runs("f", renderMs: 100, oracleMs: 100, mode: "aot");
        var baseline = Report(Runs("f", renderMs: 100, oracleMs: 100, mode: "jit"));

        var gate = RenderProgram.EvaluateReferencePerformanceGate(current, baseline, MaxTime, MaxRss);

        gate.passed.Should().BeFalse("an incomparable measurement is not a passing one");
        gate.checks.Should().BeEmpty("emitting ratios across codegen modes would give them a meaning they do not have");
        gate.note.Should().Contain("MODE MISMATCH").And.Contain("aot").And.Contain("jit");
    }

    /// <summary>The control: identical numbers in the same mode must score normally.</summary>
    [Fact]
    public void Gate_Scores_WhenTheModeMatches()
    {
        var current = Runs("f", renderMs: 100, oracleMs: 100, mode: "jit");
        var baseline = Report(Runs("f", renderMs: 100, oracleMs: 100, mode: "jit"));

        var gate = RenderProgram.EvaluateReferencePerformanceGate(current, baseline, MaxTime, MaxRss);

        gate.passed.Should().BeTrue();
        gate.checks.Should().NotBeEmpty("the same mode on both sides is exactly the comparable case");
    }

    /// <summary>
    /// A binary predating <c>runtimeMode</c> reports null. Null is "unknown", not "different"
    /// — treating it as a mismatch would make every older baseline unusable overnight.
    /// </summary>
    [Fact]
    public void Gate_Scores_WhenOneSidePredatesTheRuntimeModeField()
    {
        var current = Runs("f", renderMs: 100, oracleMs: 100, mode: "jit");
        var baseline = Report(Runs("f", renderMs: 100, oracleMs: 100, mode: null));

        RenderProgram.EvaluateReferencePerformanceGate(current, baseline, MaxTime, MaxRss)
            .checks.Should().NotBeEmpty();
    }

    /// <summary>
    /// THE 3.489x BUG. The gated metric divides excise by the median oracle. A dev-loop run
    /// with <c>--oracles mutool</c> against a baseline recorded with all five divides by the
    /// FASTEST oracle instead of the median, and the metric silently becomes a different
    /// metric: measured 3.489x on <c>irs-w9-form</c>, whose absolute render time had moved
    /// 0.981x. Restricting to oracles present on both sides fixes it — here excise and mutool
    /// are unchanged, so the ratio must be 1.0 despite the baseline also carrying a slow
    /// second oracle.
    /// </summary>
    [Fact]
    public void Gate_ComparesOnlyOraclesMeasuredOnBothSides()
    {
        // TWO extra slow oracles, not one. With only two values the median convention picks
        // the lower, so a single slow extra leaves the denominator at mutool's 100 and the
        // test passes whether or not the filter exists — verified by deleting the filter and
        // watching it stay green. Three values move the median to 1000, so an unfiltered
        // baseline ratio becomes 0.1 and the check reports 10x.
        var current = Runs("f", renderMs: 100, oracleMs: 100, mode: "jit", oracleName: "mutool");
        var baseline = Report(Runs("f", renderMs: 100, oracleMs: 100, mode: "jit", oracleName: "mutool",
            extraOracles: new[] { ("ghostscript", 1000L), ("pdfbox", 1000L) }));

        var check = RenderProgram.EvaluateReferencePerformanceGate(current, baseline, MaxTime, MaxRss)
            .checks.Single(c => c.name.EndsWith("render-vs-oracles", StringComparison.Ordinal));

        check.actual.Should().BeApproximately(1.0, 0.001,
            "nothing about excise or mutool changed; including an oracle only the baseline measured would " +
            "move the denominator and manufacture a regression");
        check.passed.Should().BeTrue();
    }

    /// <summary>With no shared oracle there is no honest denominator, so refuse.</summary>
    [Fact]
    public void Gate_RefusesToScore_WhenNoOracleIsCommonToBothSides()
    {
        var current = Runs("f", renderMs: 100, oracleMs: 100, mode: "jit", oracleName: "mutool");
        var baseline = Report(Runs("f", renderMs: 100, oracleMs: 100, mode: "jit", oracleName: "ghostscript"));

        var gate = RenderProgram.EvaluateReferencePerformanceGate(current, baseline, MaxTime, MaxRss);

        gate.passed.Should().BeFalse();
        gate.checks.Should().BeEmpty();
        gate.note.Should().Contain("BOTH");
    }

    /// <summary>
    /// A misspelled <c>--fixture</c> must be an error naming the alternatives. Falling back to
    /// the full 5-minute set defeats the flag; running nothing would report a vacuous pass.
    /// </summary>
    [Fact]
    public void FixtureFilter_RejectsAnUnknownName_AndListsTheRealOnes()
    {
        var manifest = Path.Combine(RepositoryRoot(), "tests", "reference-performance", "fixtures.json");
        Assert.SkipUnless(File.Exists(manifest), "fixture manifest not present in this checkout.");

        var act = () => RenderProgram.RunReferencePerformance(
            manifest, Path.Combine(Path.GetTempPath(), "excise-perf-" + Guid.NewGuid().ToString("N")),
            runs: 1, RenderProgram.BenchmarkOracleSelection.None, timeoutMs: 1000, includeHeavy: true,
            fixtureFilter: new[] { "irs-w9-from" }, cohortFilter: Array.Empty<string>(),
            baselinePath: null, maxTimeRatio: MaxTime, maxRssRatio: MaxRss);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*irs-w9-from*")
            .WithMessage("*irs-w9-form*", "the error must name the fixture the user probably meant");
    }

    /// <summary>A selection matching nothing is an error, never an empty passing run.</summary>
    [Fact]
    public void FixtureFilter_RejectsASelectionThatMatchesNothing()
    {
        var manifest = Path.Combine(RepositoryRoot(), "tests", "reference-performance", "fixtures.json");
        Assert.SkipUnless(File.Exists(manifest), "fixture manifest not present in this checkout.");

        // Both names are valid, but no fixture is in both -> zero fixtures selected.
        var act = () => RenderProgram.RunReferencePerformance(
            manifest, Path.Combine(Path.GetTempPath(), "excise-perf-" + Guid.NewGuid().ToString("N")),
            runs: 1, RenderProgram.BenchmarkOracleSelection.None, timeoutMs: 1000, includeHeavy: true,
            fixtureFilter: new[] { "irs-w9-form" }, cohortFilter: new[] { "tail-colour" },
            baselinePath: null, maxTimeRatio: MaxTime, maxRssRatio: MaxRss);

        act.Should().Throw<InvalidDataException>().WithMessage("*vacuous pass*");
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "excise.sln"))) dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    private static IReadOnlyList<RenderProgram.ReferencePerformanceRun> Runs(
        string fixture, double renderMs, long oracleMs, string? mode,
        string oracleName = "mutool", (string Name, long Ms)[]? extraOracles = null)
    {
        var references = new List<RenderProgram.BenchmarkReferenceResult>
        {
            new() { name = oracleName, status = "OK", elapsedMs = oracleMs },
        };
        foreach (var extra in extraOracles ?? Array.Empty<(string Name, long Ms)>())
            references.Add(new RenderProgram.BenchmarkReferenceResult
            {
                name = extra.Name, status = "OK", elapsedMs = extra.Ms,
            });

        return new[]
        {
            new RenderProgram.ReferencePerformanceRun
            {
                fixture = fixture, status = "OK", run = 1,
                exciseCli = new RenderProgram.BenchmarkCliRenderResult
                {
                    name = "excise-cli", status = "OK", renderMs = renderMs,
                    elapsedMs = (long)renderMs + 50, peakWorkingSetBytes = 100_000_000,
                    runtimeMode = mode,
                },
                references = references,
            },
        };
    }

    private static RenderProgram.ReferencePerformanceReport Report(
        IReadOnlyList<RenderProgram.ReferencePerformanceRun> runs)
        => new() { schemaVersion = 1, runs = runs };
}
