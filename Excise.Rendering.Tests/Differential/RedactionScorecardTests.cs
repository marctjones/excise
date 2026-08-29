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
}
