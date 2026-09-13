using System.Linq;
using AwesomeAssertions;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1123 — the scorecard/taxonomy logic, pinned on synthetic result rows so it
/// is proven WITHOUT a full corpus run. The taxonomy's whole value is that it
/// names a failure class with a stratum and a percentage; these tests assert it
/// produces exactly those named strings from known inputs, and that it obeys the
/// design rules (never one number, per-stratum, record what was not run).
/// </summary>
public class RedactionScorecardTests
{
    // Two corpora, two tools. excise leaks /ActualText on 1 of 2 tagged docs and
    // is clean on federal; the competitor destroys collateral and produces
    // invalid output. Hand-built so every taxonomy line has a known answer.
    private static readonly string[] Jsonl =
    {
        // excise, tagged corpus: one /ActualText leak, one clean
        """{"tool":"excise","corpus":"tagged","document":"a.pdf","term":"Smith","leakOracleText":false,"leakChannels":["/ActualText"],"collateralFraction":0.0,"qpdfOk":true,"inputQpdfOk":true,"structuralDropped":""}""",
        """{"tool":"excise","corpus":"tagged","document":"b.pdf","term":"Jones","leakOracleText":false,"leakChannels":[],"collateralFraction":0.0,"qpdfOk":true,"inputQpdfOk":true,"structuralDropped":""}""",
        // excise, federal corpus: clean, but drops a bookmark on one
        """{"tool":"excise","corpus":"federal","document":"c.pdf","term":"Doe","leakOracleText":false,"leakChannels":[],"collateralFraction":0.0,"qpdfOk":true,"inputQpdfOk":true,"structuralDropped":"bookmarks 3->0"}""",
        // competitor: leaks text on a.pdf (excise does not → excise BEATS), high collateral, invalid output
        """{"tool":"pymupdf","corpus":"tagged","document":"a.pdf","term":"Smith","leakOracleText":true,"leakChannels":["extractor"],"collateralFraction":0.12,"qpdfOk":false,"inputQpdfOk":true,"structuralDropped":""}""",
        // an errored case — must count as NOT measured, never silently dropped
        """{"tool":"pymupdf","corpus":"federal","document":"c.pdf","term":"Doe","error":"adapter failed"}""",
    };

    [Fact]
    public void Coverage_CountsErroredCasesSeparately_NoSilentHole()
    {
        var rows = RedactionScorecard.Parse(Jsonl);
        var cov = RedactionScorecard.CoverageOf(rows);

        cov.Measured.Should().Be(4, "four rows carry results");
        cov.Errored.Should().Be(1, "the adapter-failed row is recorded, not dropped — " +
            "a scorecard that hides errors reads identically to one with full coverage");
        cov.ToolsSeen.Should().BeEquivalentTo(new[] { "excise", "pymupdf" });
    }

    [Fact]
    public void Taxonomy_NamesTheCarrierLeakWithStratumAndPercentage()
    {
        var tax = RedactionScorecard.FailureTaxonomy(RedactionScorecard.Parse(Jsonl));

        tax.Should().Contain(l => l.Contains("excise leaks via /ActualText") &&
                                  l.Contains("tagged") && l.Contains("50") && l.Contains("%") && l.Contains("1/2"),
            "the taxonomy must name the channel, the stratum, and the rate — not a single score");

        // excise did NOT leak on federal — a clean axis is silent, not "0%".
        tax.Should().NotContain(l => l.Contains("excise leaks") && l.Contains("federal"));
    }

    [Fact]
    public void Taxonomy_NamesCollateral_Fidelity_AndStructuralDrops()
    {
        var tax = RedactionScorecard.FailureTaxonomy(RedactionScorecard.Parse(Jsonl));

        tax.Should().Contain(l => l.Contains("pymupdf destroys >5") && l.Contains("collateral") && l.Contains("tagged"));
        tax.Should().Contain(l => l.Contains("pymupdf produces qpdf-invalid output"));
        tax.Should().Contain(l => l.Contains("excise drops document structure") && l.Contains("1/3"));
    }

    [Fact]
    public void Taxonomy_RecordsTheHeadToHead_WhoBeatsWhomOnTheSameCase()
    {
        var tax = RedactionScorecard.FailureTaxonomy(RedactionScorecard.Parse(Jsonl));

        // On a.pdf|Smith the competitor leaks text and excise does not.
        tax.Should().Contain(l => l.Contains("excise BEATS pymupdf") && l.Contains("a.pdf"),
            "the point is not a single score but 'beats X here, loses to X there' — the trade-off");
    }

    [Fact]
    public void Fidelity_DoesNotChargeInheritedMalformation()
    {
        // A tool whose OUTPUT is invalid only because the INPUT already was must
        // not be counted as a fidelity failure (measured hazard: TAMReview.pdf).
        var rows = RedactionScorecard.Parse(new[]
        {
            """{"tool":"excise","corpus":"x","document":"bad.pdf","term":"t","leakOracleText":false,"leakChannels":[],"collateralFraction":0.0,"qpdfOk":false,"inputQpdfOk":false,"structuralDropped":""}""",
        });
        var tax = RedactionScorecard.FailureTaxonomy(rows);
        tax.Should().NotContain(l => l.Contains("qpdf-invalid"),
            "output invalid because the input was is inherited, not a redaction fidelity defect");
    }

    [Fact]
    public void Parse_SkipsTheMetaHeaderLine()
    {
        // #1400: WriteReport now prepends a _meta line (commit, timestamp,
        // leakEngines) recording the run's actual measurement basis. Without
        // this skip it became a bogus Row -- every field defaulted to ""/
        // false/0 -- counted straight into Coverage and the taxonomy.
        var rows = RedactionScorecard.Parse(new[]
        {
            """{"_meta":true,"commit":"abc123","timestamp":"2026-01-01T00:00:00Z","leakEngines":["mutool"]}""",
            """{"tool":"excise","corpus":"x","document":"a.pdf","term":"t","leakOracleText":false,"leakChannels":[],"collateralFraction":0.0,"qpdfOk":true,"inputQpdfOk":true,"structuralDropped":""}""",
        });

        rows.Should().HaveCount(1, "the _meta line is not a benchmark row");
        RedactionScorecard.CoverageOf(rows).Measured.Should().Be(1);
    }

    // =========================================================================
    // #1163 — the Security x Fidelity GRADE. A naive WEIGHTED AVERAGE across
    // the four categories is exactly what the issue's worked example (excise
    // 88.8 A-, pymupdf 82.6 B, itext 49.5 F, raster 0.0 F) rules out: it would
    // rank a document-destroying tool near the top because its Security is
    // perfect. These pin the two failure modes a weighted average gets wrong.
    // =========================================================================

    private static string Case(
        string tool, string corpus, string doc, string term,
        string verdict, bool probeUsable, bool leakSavedBytes, int visualTermReadable,
        int survivingWordsChecked, int survivingWordsDamaged, double collateralFraction,
        double survivingRenderDelta, bool qpdfOk = true, bool inputQpdfOk = true,
        string structuralDropped = "")
        => $$"""
        {"tool":"{{tool}}","corpus":"{{corpus}}","document":"{{doc}}","term":"{{term}}",
         "leakOracleText":false,"leakChannels":[],
         "verdict":"{{verdict}}","probeUsable":{{(probeUsable ? "true" : "false")}},
         "leakSavedBytes":{{(leakSavedBytes ? "true" : "false")}},
         "visualTermReadable":{{visualTermReadable}},
         "survivingWordsChecked":{{survivingWordsChecked}},"survivingWordsDamaged":{{survivingWordsDamaged}},
         "collateralFraction":{{collateralFraction}},"survivingRenderDelta":{{survivingRenderDelta}},
         "qpdfOk":{{(qpdfOk ? "true" : "false")}},"inputQpdfOk":{{(inputQpdfOk ? "true" : "false")}},
         "structuralDropped":"{{structuralDropped}}"}
        """.ReplaceLineEndings("").Replace(" ", "");

    [Fact]
    public void Overall_LeakingToolWithPerfectOutput_ScoresNearZero_NotHigh()
    {
        // Recoverable leak (the worst Verdict), saved-bytes leak too, AND the
        // secret is still legible on the render — Security should floor at 0.
        // Fidelity is PERFECT: nothing damaged, no collateral, no render delta.
        var row = RedactionScorecard.Parse(new[]
        {
            Case("leaky", "x", "a.pdf", "t", verdict: "Recoverable", probeUsable: true,
                leakSavedBytes: true, visualTermReadable: 1,
                survivingWordsChecked: 100, survivingWordsDamaged: 0,
                collateralFraction: 0.0, survivingRenderDelta: 0.0),
        })[0];

        var grade = RedactionScorecard.GradeCase(row);
        grade.Security.Should().Be(0.0, "every security channel measured says LEAK");
        grade.Fidelity.Should().Be(1.0, "surviving content, collateral and render are all perfect");

        var sc = RedactionScorecard.ComputeScorecards(new[] { row }).Single(s => s.Corpus == "ALL");
        sc.OverallPct.Should().Be(0.0,
            "Security x Fidelity = 0 x 100 = 0 — a naive weighted average (e.g. (0+100)/2=50) " +
            "would score a LEAKING tool with pretty output as a middling C, not a failure");
    }

    [Fact]
    public void Overall_SecureButDestructiveTool_ScoresNearZero_NotHigh()
    {
        // Removed cleanly, no byte leak, not legible on render — Security is
        // PERFECT. But every surviving word is damaged, full collateral, and
        // the render is unrecognisable — Fidelity floors at 0.
        var row = RedactionScorecard.Parse(new[]
        {
            Case("destroyer", "x", "a.pdf", "t", verdict: "Removed", probeUsable: true,
                leakSavedBytes: false, visualTermReadable: 0,
                survivingWordsChecked: 100, survivingWordsDamaged: 100,
                collateralFraction: 1.0, survivingRenderDelta: 1.0),
        })[0];

        var grade = RedactionScorecard.GradeCase(row);
        grade.Security.Should().Be(1.0, "every security channel measured says CLEAN");
        grade.Fidelity.Should().Be(0.0, "surviving content, collateral and render are all destroyed");

        var sc = RedactionScorecard.ComputeScorecards(new[] { row }).Single(s => s.Corpus == "ALL");
        sc.OverallPct.Should().Be(0.0,
            "Security x Fidelity = 100 x 0 = 0 — a naive weighted average (e.g. (100+0)/2=50) " +
            "would rank a DOCUMENT-DESTROYING tool near the top on the strength of its security alone " +
            "(this is precisely the raster-baseline failure the issue's worked example calls out)");
    }

    [Fact]
    public void ProbeUsable_False_ExcludesTheCaseFromSecurity_NeitherPassNorFail()
    {
        // A common-word coincidence (#1182): the term also lives somewhere
        // redaction was never asked to touch, so ProbeUsable is false. Even
        // though every raw signal on this row says "leak", it must not drag
        // Security down — it is not evidence about the tool at all.
        var row = RedactionScorecard.Parse(new[]
        {
            Case("excise", "x", "a.pdf", "your", verdict: "Recoverable", probeUsable: false,
                leakSavedBytes: true, visualTermReadable: 1,
                survivingWordsChecked: 10, survivingWordsDamaged: 0,
                collateralFraction: 0.0, survivingRenderDelta: 0.0),
        })[0];

        var grade = RedactionScorecard.GradeCase(row);
        grade.Security.Should().BeNull("an unusable probe contributes no security channels — not a pass, not a fail");

        var sc = RedactionScorecard.ComputeScorecards(new[] { row }).Single(s => s.Corpus == "ALL");
        sc.SecurityPct.Should().BeNull("with the only case's Security unmeasured, the aggregate has nothing to average");
        sc.OverallPct.Should().BeNull("Overall needs both Security and Fidelity; Security is unmeasured here");
    }

    [Fact]
    public void Robustness_EnforcedOnErroredCases_OtherAxesStayUnmeasured_NotZero()
    {
        var rows = RedactionScorecard.Parse(new[]
        {
            """{"tool":"crashy","corpus":"x","document":"a.pdf","term":"t","error":"adapter threw"}""",
        });

        var grade = RedactionScorecard.GradeCase(rows[0]);
        grade.Robustness.Should().Be(0.0, "the document was not handled at all");
        grade.Security.Should().BeNull("nothing was measured, so this is not evidence of a leak");
        grade.Fidelity.Should().BeNull("nothing was measured, so this is not evidence of damage");
        grade.Integrity.Should().BeNull("nothing was measured, so this is not evidence of a broken PDF");
    }

    [Fact]
    public void Grade_MatchesTheIssuesWorkedExample()
    {
        // The four hard constraints from #1163's hand-validated table.
        RedactionScorecard.LetterGrade(88.8).Should().Be("A-", "excise's measured score");
        RedactionScorecard.LetterGrade(82.6).Should().Be("B", "pymupdf's measured score");
        RedactionScorecard.LetterGrade(49.5).Should().Be("F", "itext leaks");
        RedactionScorecard.LetterGrade(0.0).Should().Be("F", "raster destroys the document");
    }

    [Fact]
    public void ComputeScorecards_ReportsPerCorpus_NotOnlyAggregate()
    {
        // A tool that's perfect on one corpus and destructive on another must
        // not read as "fine on average" — the issue's "concentration" point.
        var rows = RedactionScorecard.Parse(new[]
        {
            Case("excise", "easy", "a.pdf", "t", verdict: "Removed", probeUsable: true,
                leakSavedBytes: false, visualTermReadable: 0,
                survivingWordsChecked: 100, survivingWordsDamaged: 0,
                collateralFraction: 0.0, survivingRenderDelta: 0.0),
            Case("excise", "hard", "b.pdf", "t", verdict: "Recoverable", probeUsable: true,
                leakSavedBytes: true, visualTermReadable: 1,
                survivingWordsChecked: 100, survivingWordsDamaged: 100,
                collateralFraction: 1.0, survivingRenderDelta: 1.0),
        });

        var scorecards = RedactionScorecard.ComputeScorecards(rows);

        scorecards.Should().Contain(s => s.Corpus == "easy" && s.OverallPct == 100.0);
        scorecards.Should().Contain(s => s.Corpus == "hard" && s.OverallPct == 0.0);
        var all = scorecards.Single(s => s.Corpus == "ALL");
        all.Cases.Should().Be(2);
        // Security_ALL = avg(1.0, 0.0) = 50%; Fidelity_ALL = avg(1.0, 0.0) = 50%;
        // Overall = 0.5 x 0.5 x 100 = 25 — the multiplier makes even the AGGREGATE
        // read as a clear failing grade rather than an additive-average "50, so-so".
        all.OverallPct.Should().BeApproximately(25.0, 0.01,
            "the multiplicative aggregate already signals trouble; the per-corpus rows then show exactly where");
    }

    [Fact]
    public void Render_PrintsToolCorpusAndGrade_MatchingTheIssuesTableShape()
    {
        var rows = RedactionScorecard.Parse(new[]
        {
            Case("excise", "smoke", "a.pdf", "t", verdict: "Removed", probeUsable: true,
                leakSavedBytes: false, visualTermReadable: 0,
                survivingWordsChecked: 100, survivingWordsDamaged: 0,
                collateralFraction: 0.0, survivingRenderDelta: 0.0),
        });

        var report = RedactionScorecard.Render(RedactionScorecard.ComputeScorecards(rows));

        report.Should().Contain("excise");
        report.Should().Contain("smoke");
        report.Should().Contain("ALL");
        report.Should().Contain("100.0%", "a perfect case reports 100% on every axis");
        report.Should().Contain(" A", "a perfect score grades A");
    }
}
