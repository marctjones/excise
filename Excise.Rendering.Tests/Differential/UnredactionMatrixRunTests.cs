using System.Linq;
using AwesomeAssertions;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1645 — the matrix, run for real over the fixture corpus and the
/// real-world negatives. This is where the per-tool numbers come from.
/// </summary>
public class UnredactionMatrixRunTests
{
    private readonly Xunit.ITestOutputHelper _out;
    public UnredactionMatrixRunTests(Xunit.ITestOutputHelper o) => _out = o;

    [Fact]
    public void TheMatrix_ScoresBothToolsAcrossEveryBuildableFixture()
    {
        var run = UnredactionMatrixRun.Execute();
        var scopes = UnredactionConfusionMatrix.Scopes();

        var cells = UnredactionConfusionMatrix.Score(run.Cases, run.Results, scopes);
        var profiles = UnredactionConfusionMatrix.Profile(run.Cases, run.Results, scopes);

        var byId = run.Cases.ToDictionary(c => c.CaseId);
        var falsePositives = run.Results
            .Where(r => byId.TryGetValue(r.CaseId, out var c) && !c.ContainsLeak && r.Detected)
            .Select(r => (byId[r.CaseId], r)).ToList();

        _out.WriteLine(UnredactionConfusionMatrix.Render(cells, profiles, run.NotMeasured, falsePositives));

        run.Cases.Should().NotBeEmpty("the fixture corpus is compiled into this assembly");
        cells.Should().NotBeEmpty();

        // ⚠️ A cell for a mode excise claims must actually be SCORED. A matrix
        // that silently drops a mode reads identically to one where the mode
        // passed, which is the #1527 shape.
        var exciseCells = cells.Where(c => c.Tool == "excise").ToList();
        exciseCells.Should().OnlyContain(c => c.Scored > 0,
            "excise declares every mode in scope, so every cell must carry scored cases");
    }

    /// <summary>
    /// ⚠️ THE ONE ASSERTION THAT WOULD HAVE CAUGHT #1624. A tool answering yes
    /// to everything scores perfect recall; only specificity separates it from
    /// a useful one.
    /// </summary>
    [Fact]
    public void Excise_DoesNotReportLeaksOnDocumentsThatHaveNone()
    {
        var run = UnredactionMatrixRun.Execute();
        var scopes = UnredactionConfusionMatrix.Scopes();
        var cells = UnredactionConfusionMatrix.Score(run.Cases, run.Results, scopes);

        var negatives = cells.SingleOrDefault(c => c.Tool == "excise" && c.ModeId == "real-world-negative");
        Assert.SkipWhen(negatives == null,
            "no real-world negatives fetched — run scripts/download-recap-corpus.sh --fetch-negatives");

        _out.WriteLine($"real-world negatives: {negatives!.TrueNegative} clean, " +
                       $"{negatives.FalsePositive} false positive(s) of {negatives.Scored}");

        // ⚠️ A RATCHET AT THE MEASURED VALUE, NOT A TARGET. excise reports a
        // leak on 28 of 57 clean court filings — 21 via `carrier`, 7 via OCR
        // layers (one document produced 13,686 findings). x-ray scores 100% on
        // the same 57. That is #1669, and it is a scope question rather than the
        // graphics-state defect #1624 was.
        //
        // The floor exists so it cannot get WORSE unnoticed. Per CLAUDE.md's own
        // warning about floors: a green run here means "no worse than 50.9%",
        // never "good enough". Raise it when #1669 lands; do not delete it.
        const double measuredFloor = 0.50;
        negatives.Specificity.Should().BeGreaterThanOrEqualTo(measuredFloor,
            "#1669 — the ratchet is the measured 50.9%, and #1624 is why negatives exist at " +
            "all: a bench with none scored 83.7% of a clean filing as hidden text as NOTHING");
    }
}
