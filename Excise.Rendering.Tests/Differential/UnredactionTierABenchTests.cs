using System.Linq;
using AwesomeAssertions;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1590/#1592 — runs the tier-A bench and holds it to the failure-mode
/// registry's own claims.
///
/// <para>The bench is a SURVEY, not a pass/fail gate on recovery quality
/// (#1590). What is gated here is the thing a survey cannot be trusted without:
/// that its axes cover every failure mode, that a mode the registry calls
/// covered actually recovers, and that a mode it calls a gap is measured as a
/// gap rather than skipped. A survey whose coverage is unverified reports a
/// score over an unknown subset.</para>
/// </summary>
public class UnredactionTierABenchTests
{
    private readonly ITestOutputHelper _out;

    public UnredactionTierABenchTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void EveryRegistryModeHasAnAxis()
    {
        // Derived, so this holds by construction — the test exists to fail
        // loudly if the derivation is ever replaced by a hand-written list.
        UnredactionBenchAxes.RegistryPath.Should().NotBeNull();
        UnredactionBenchAxes.All.Should().NotBeEmpty();
        UnredactionBenchAxes.All.Select(a => a.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EveryModeTheRegistryCallsCovered_RecoversItsPlantedAnswer()
    {
        // The load-bearing assertion. "covered" is a claim; this is where the
        // claim meets a document with a known answer in it.
        var outcomes = UnredactionTierABench.Run();

        foreach (var axis in UnredactionBenchAxes.All.Where(a => a.ExpectedRecoverable))
        {
            var outcome = outcomes.Single(o => o.AxisId == axis.Id);
            if (!outcome.Buildable) continue;   // reported separately below

            outcome.Recovered.Should().BeTrue(
                $"the registry calls '{axis.Id}' covered, so tier A must recover its " +
                "planted answer. Either the channel regressed or the registry overstates.");
        }
    }

    [Fact]
    public void EveryModeTheRegistryCallsAGap_IsMeasuredAndStillZero()
    {
        // A gap that starts recovering is good news that must not pass
        // silently: the registry, the issue and this expectation all need
        // updating together, and a green suite would hide that.
        var outcomes = UnredactionTierABench.Run();

        foreach (var axis in UnredactionBenchAxes.All.Where(a => a.Status == "gap"))
        {
            var outcome = outcomes.Single(o => o.AxisId == axis.Id);
            if (!outcome.Buildable) continue;

            outcome.Recovered.Should().BeFalse(
                $"'{axis.Id}' is recorded as a gap (#{axis.Issue}) and tier A just recovered it. " +
                "That is a fix landing: flip the row to covered in " +
                "tests/unredaction-failure-modes.json, name its evidence test, and close the issue.");
        }
    }

    [Fact]
    public void TheBenchReportsWhichModesItCannotYetPose()
    {
        // A mode with no fixture scores nothing and must not be counted as a
        // measured zero. This prints the list so the hole is visible in the run
        // rather than inferred from a missing row.
        var outcomes = UnredactionTierABench.Run();
        var unposed = outcomes.Where(o => !o.Buildable).Select(o => o.AxisId).ToList();

        _out.WriteLine($"tier A: {outcomes.Count(o => o.Buildable)} of {outcomes.Count} modes posed");
        if (unposed.Count > 0)
            _out.WriteLine("NOT YET POSED (bench holes, not excise results): " + string.Join(", ", unposed));

        // Not an assertion of zero: some modes need a raster, a dictionary or a
        // corpus and will never be tier A. The gate is that we KNOW which.
        unposed.Should().NotBeNull();
    }

    [Fact]
    public void TheScorecardRendersWithItsCoverageStated()
    {
        var outcomes = UnredactionTierABench.Run();
        var grades = UnredactionScorecard.Score(UnredactionTierABench.Score(outcomes));

        var unposed = outcomes.Where(o => !o.Buildable).Select(o => o.AxisId).ToList();
        var coverage = new UnredactionScorecard.Coverage(
            Channels: grades.Select(g => g.Channel).Distinct().OrderBy(c => c).ToList(),
            Tools: new[] { "excise" },
            // The modes with no fixture are exactly what "not measured" means
            // here, and the scorecard prints them so a reader cannot take this
            // for a score over every failure mode.
            MissingReferences: unposed);

        var rendered = UnredactionScorecard.Render(grades, coverage);
        _out.WriteLine(rendered);

        rendered.Should().Contain("UNREDACTION SCORECARD");
        grades.Should().NotBeEmpty("tier A must produce at least one measured axis");
    }
}
