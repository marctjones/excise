using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Redaction.Recovery;
using Excise.TestSupport;
using Xunit;
using static Excise.Rendering.Tests.Differential.UnredactionConfusionMatrix;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1645 — the matrix is pure aggregation over typed rows, so its grading is
/// unit-tested with no PDFs, no engine and no external tool. Same design rule as
/// <see cref="UnredactionScorecard"/>: if the arithmetic needs a corpus to
/// exercise, it cannot be checked on a machine that lacks one.
/// </summary>
public class UnredactionConfusionMatrixTests
{
    private static readonly ToolScope ExciseScope = new("excise", "recoverer", null, AllModes: true);
    private static readonly ToolScope XRayScope =
        new("xray", "detector", new HashSet<string> { "box-over-intact-text" }, AllModes: false);

    private static Case Leak(string mode, string id) => new(mode, id, ContainsLeak: true);
    private static Case Clean(string mode, string id) => new(mode, id, ContainsLeak: false);

    [Fact]
    public void AllFourCellsAreClassifiedFromGroundTruth()
    {
        Classify(Leak("box-over-intact-text", "a"), new ToolResult("excise", "a", true), ExciseScope)
            .Should().Be(Verdict.TruePositive);
        Classify(Leak("box-over-intact-text", "a"), new ToolResult("excise", "a", false), ExciseScope)
            .Should().Be(Verdict.FalseNegative);
        Classify(Clean("box-over-intact-text", "a"), new ToolResult("excise", "a", true), ExciseScope)
            .Should().Be(Verdict.FalsePositive);
        Classify(Clean("box-over-intact-text", "a"), new ToolResult("excise", "a", false), ExciseScope)
            .Should().Be(Verdict.TrueNegative);
    }

    /// <summary>
    /// ⚠️ THE ADVISORY CASE. x-ray finding nothing on a mode it does not claim is
    /// NOT a false negative — but x-ray finding nothing on the ONE mode it does
    /// claim absolutely is, and a matrix that inferred scope from results could
    /// never report that.
    /// </summary>
    [Fact]
    public void SilenceIsAMissInsideScopeAndNotOutsideIt()
    {
        Classify(Leak("leftover-xfa", "x"), new ToolResult("xray", "x", false), XRayScope)
            .Should().Be(Verdict.OutOfScope, "x-ray never claimed XFA");

        Classify(Leak("box-over-intact-text", "b"), new ToolResult("xray", "b", false), XRayScope)
            .Should().Be(Verdict.FalseNegative,
                "this is the one mode x-ray is FOR — silence here is a regression, " +
                "and a scope inferred from results would have hidden it");
    }

    /// <summary>
    /// A hit outside the declared scope is reported, never counted. Either the
    /// tool outgrew its documentation or the registry is stale; averaging it into
    /// precision would bury both.
    /// </summary>
    [Fact]
    public void AHitOutsideDeclaredScopeIsReportedSeparately()
    {
        Classify(Leak("box-inside-form-xobject", "f"), new ToolResult("xray", "f", true), XRayScope)
            .Should().Be(Verdict.BeyondDeclaredScope);

        var cells = Score(
            new[] { Leak("box-inside-form-xobject", "f") },
            new[] { new ToolResult("xray", "f", true) },
            new[] { XRayScope });

        cells.Single().TruePositive.Should().Be(0, "it must not inflate recall");
        cells.Single().BeyondDeclaredScope.Should().Be(1);
    }

    /// <summary>
    /// ⚠️ THE #1624 CELL. A tool that answers yes to everything scores perfect
    /// recall; only the negatives population separates it from a good one.
    /// </summary>
    [Fact]
    public void AToolThatAnswersYesToEverything_ScoresPerfectRecallAndTerriblePrecision()
    {
        var cases = new[]
        {
            Leak("box-over-intact-text", "l1"),
            Clean("box-over-intact-text", "c1"),
            Clean("box-over-intact-text", "c2"),
            Clean("box-over-intact-text", "c3"),
        };
        var eager = cases.Select(c => new ToolResult("excise", c.CaseId, true)).ToList();

        var cell = Score(cases, eager, new[] { ExciseScope }).Single();

        cell.Recall.Should().Be(1.0, "it found the one real leak");
        cell.Precision.Should().Be(0.25, "and three of its four answers were wrong");
        cell.Specificity.Should().Be(0.0, "it never left a clean document alone");
        cell.F1.Should().BeApproximately(0.4, 1e-9);
    }

    /// <summary>
    /// Recall alone cannot distinguish a reading from a guess. The profile can,
    /// and it is computed over TRUE POSITIVES only — a rung on a clean document
    /// or on a miss is not a recovery.
    /// </summary>
    [Fact]
    public void TheRecoveryLadderSeparatesAReadingFromAnInference()
    {
        var cases = new[]
        {
            Leak("m", "read"), Leak("m", "guess"), Leak("m", "part"),
            Clean("m", "clean"),
        };
        var results = new[]
        {
            new ToolResult("excise", "read", true, MarkRecoveryOutcome.Recovered),
            new ToolResult("excise", "guess", true, MarkRecoveryOutcome.CandidatesOnly, ResidualBits: 5.2, CandidateCount: 37),
            new ToolResult("excise", "part", true, MarkRecoveryOutcome.PartiallyRecovered),
            // Detected on a CLEAN case: a false positive, and its rung must not
            // enter the profile at all.
            new ToolResult("excise", "clean", true, MarkRecoveryOutcome.Recovered),
        };

        var p = Profile(cases, results, new[] { ExciseScope }).Single();

        p.Exact.Should().Be(1);
        p.Partial.Should().Be(1);
        p.CandidatesOnly.Should().Be(1);
        p.Total.Should().Be(3, "the false positive's rung is not a recovery");
        p.MedianCandidateSetSize.Should().Be(37);
    }

    /// <summary>
    /// #1707 — a <c>ContentSurvives</c> row gets its own bucket and counts
    /// toward the total.
    ///
    /// <para>⚠️ Without the bucket this row is counted by NOTHING: the profile
    /// sums four named rungs, so a new rung silently shrinks <c>Total</c> and
    /// the bench under-reports how a tool answered. Asserting the bucket alone
    /// would not catch that, so the total is asserted with it.</para>
    /// </summary>
    [Fact]
    public void AContentSurvivesRowIsItsOwnBucketAndCountsInTheTotal()
    {
        var cases = new[] { Leak("m", "read"), Leak("m", "intact"), Leak("m", "guess") };
        var results = new[]
        {
            new ToolResult("excise", "read", true, MarkRecoveryOutcome.Recovered),
            new ToolResult("excise", "intact", true, MarkRecoveryOutcome.ContentSurvives),
            new ToolResult("excise", "guess", true, MarkRecoveryOutcome.CandidatesOnly, ResidualBits: 3.0, CandidateCount: 8),
        };

        var p = Profile(cases, results, new[] { ExciseScope }).Single();

        p.ContentSurvives.Should().Be(1);
        p.CandidatesOnly.Should().Be(1,
            "intact-but-undecoded must not be folded into the inference rung");
        p.Total.Should().Be(3, "every rung the engine can emit has to land in a bucket");
    }

    /// <summary>
    /// A tool with no candidate rung must not be scored as though the missing
    /// rung were a weakness. x-ray is exact-or-nothing by construction.
    /// </summary>
    [Fact]
    public void ADetectorWithNoInferenceRungReportsOnlyExact()
    {
        var cases = new[] { Leak("box-over-intact-text", "a"), Leak("box-over-intact-text", "b") };
        var results = new[]
        {
            new ToolResult("xray", "a", true, MarkRecoveryOutcome.Recovered),
            new ToolResult("xray", "b", false),
        };

        var p = Profile(cases, results, new[] { XRayScope }).Single();

        p.Exact.Should().Be(1);
        p.CandidatesOnly.Should().Be(0);
        p.Total.Should().Be(1, "the miss is a false negative, not a recovery rung");
    }

    /// <summary>The declared scopes parse, and say what the registry says.</summary>
    [Fact]
    public void TheToolRegistryDeclaresExciseEverywhereAndXRayOnOneMode()
    {
        Assert.SkipUnless(RegistryPath != null,
            TestRepoLayout.AbsenceReason(
                "the tool registry", System.IO.Path.Combine("tests", "unredaction-tools.json")));

        var scopes = Scopes();
        var excise = scopes.Single(s => s.Id == "excise");
        var xray = scopes.Single(s => s.Id == "xray");

        excise.AllModes.Should().BeTrue("excise claims every registered mode");
        xray.Covers("box-over-intact-text").Should().BeTrue();
        xray.Covers("leftover-xfa").Should().BeFalse(
            "x-ray's README claims rectangles over text and nothing else");

        scopes.Single(s => s.Id == "edact-ray").Covers("partial-glyph-removal-kerning")
            .Should().BeFalse("the recovery code was deliberately withheld by its author");
    }
}
