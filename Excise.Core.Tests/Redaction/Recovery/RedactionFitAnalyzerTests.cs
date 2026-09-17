using System;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Redaction.Recovery;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1589 — what could fit a redaction mark.
///
/// <para>These pin the properties that make the output a MEASUREMENT rather
/// than a guess: the character range brackets the truth, the bit count follows
/// from the admissible set, and a dictionary never widens what the width
/// admits.</para>
/// </summary>
public class RedactionFitAnalyzerTests
{
    private const string Font = "Helvetica";
    private const double Size = 12;

    private static double WidthOf(string text) =>
        RedactionFitAnalyzer.MeasureWidth(text, Font, Size);

    [Fact]
    public void TheCharacterRangeBracketsTheTruth()
    {
        // The load-bearing property. Whatever was removed, its length must lie
        // between the count of the widest glyph and the count of the narrowest
        // that fill the span — otherwise the range is not a bound at all.
        foreach (var secret in new[] { "Smith", "Harper", "W", "iiiii", "MMMM", "123-45-6789" })
        {
            var report = RedactionFitAnalyzer.Analyse(WidthOf(secret), Font, Size);

            report.MinCharacters.Should().BeLessThanOrEqualTo(secret.Length,
                $"'{secret}' is {secret.Length} chars and the minimum must not exceed it");
            report.MaxCharacters.Should().BeGreaterThanOrEqualTo(secret.Length,
                $"'{secret}' is {secret.Length} chars and the maximum must not fall below it");
        }
    }

    [Fact]
    public void ADictionaryWordThatFits_IsReturnedWithItsWidthError()
    {
        var report = RedactionFitAnalyzer.Analyse(
            WidthOf("Harper"), Font, Size,
            dictionary: new[] { "Harper", "Hansen", "Zzzzzzzzzzzz", "Q" });

        report.Candidates.Should().Contain(c => c.Text == "Harper");
        report.Candidates.Single(c => c.Text == "Harper").ErrorPt
            .Should().BeApproximately(0, 0.01, "the truth measures to the budget exactly");
        report.Candidates.Should().NotContain(c => c.Text == "Zzzzzzzzzzzz",
            "a word far wider than the gap cannot have been there");
    }

    [Fact]
    public void BitsLeaked_IsLog2OfTheAdmissibleSet()
    {
        // Four candidates of identical width -> 2 bits. The number is a
        // property of the SET, which is what makes it reproducible.
        var report = RedactionFitAnalyzer.Analyse(
            WidthOf("iiii"), Font, Size,
            dictionary: new[] { "iiii", "illi", "lili", "llll", "MMMM" });

        report.Candidates.Count.Should().BeGreaterThan(1);
        report.BitsLeaked.Should().BeApproximately(
            Math.Log2(report.Candidates.Count), 0.01);
    }

    [Fact]
    public void ASingleFittingCandidate_IsZeroBitsAndStillCalledACandidate()
    {
        // Even at one candidate the report states a constraint, not an answer.
        // The vocabulary is deliberately "fits exactly", never "the answer is".
        var report = RedactionFitAnalyzer.Analyse(
            WidthOf("Harper"), Font, Size,
            dictionary: new[] { "Harper", "Zzzzzzzzzzzzzzz" });

        report.Candidates.Should().ContainSingle();
        report.BitsLeaked.Should().Be(0);
        report.Confidence.Should().Be("fits exactly");
        RedactionFitAnalyzer.Describe(report).Should().NotContain("the answer");
    }

    [Fact]
    public void AnSsnShapedGapIsNamedAsOne()
    {
        var report = RedactionFitAnalyzer.Analyse(WidthOf("123-45-6789"), Font, Size);
        report.PatternClasses.Should().Contain(p => p.Contains("SSN"));
    }

    [Fact]
    public void AGapTooNarrowForAnSsn_DoesNotClaimOneFits()
    {
        // The negative control for the pattern classes. A classifier that
        // listed every format would be worse than listing none.
        var report = RedactionFitAnalyzer.Analyse(WidthOf("ab"), Font, Size);
        report.PatternClasses.Should().NotContain(p => p.Contains("SSN"));
        report.PatternClasses.Should().NotContain(p => p.Contains("phone"));
    }

    [Fact]
    public void AnUnknownFont_SaysSoRatherThanGuessingARange()
    {
        // Inventing a character range from a default font would be a fabricated
        // measurement, and the width is a real one that should still be shown.
        var report = RedactionFitAnalyzer.Analyse(50, "SomeSubsettedTrueType", Size);

        report.MinCharacters.Should().Be(0);
        report.MetricNote.Should().Contain("no standard metrics");
        report.WidthPt.Should().Be(50, "the measured width is still reported");
        report.Confidence.Should().Be("not analysable");
    }

    [Fact]
    public void NoDictionary_StillProducesLengthAndPatternConstraints()
    {
        var report = RedactionFitAnalyzer.Analyse(WidthOf("Harper"), Font, Size);

        // The character range is the constraint that always exists; pattern
        // classes are legitimately EMPTY when no named format fits, and saying
        // "no named pattern fits" is more useful than padding the list.
        report.MinCharacters.Should().BeGreaterThan(0);
        report.MaxCharacters.Should().BeGreaterThanOrEqualTo(report.MinCharacters);
        report.Confidence.Should().Contain("length and pattern");
    }

    [Fact]
    public void BoxPadding_AdmitsACandidateNarrowerThanTheMark()
    {
        // A redaction box is drawn AROUND the run it covers, so its width is an
        // upper bound on the removed text, not an equality. Treating it as an
        // equality silently rejects the right answer and reports "nothing
        // fits", which understates the leak — the unsafe direction.
        var truth = WidthOf("Smith");
        var markWidth = truth + 4;   // the box, with padding either side

        RedactionFitAnalyzer.Analyse(markWidth, Font, Size, new[] { "Smith" })
            .Candidates.Should().BeEmpty("with no padding allowance the truth is rejected");

        RedactionFitAnalyzer.Analyse(markWidth, Font, Size, new[] { "Smith" }, paddingPt: 6)
            .Candidates.Should().Contain(c => c.Text == "Smith");
    }

    [Fact]
    public void BoxPadding_DoesNotAdmitACandidateWiderThanTheMark()
    {
        // The bound is asymmetric on purpose: text cannot have been wider than
        // the box drawn around it, however generous the padding allowance.
        var markWidth = WidthOf("Smith");

        RedactionFitAnalyzer.Analyse(
                markWidth, Font, Size, new[] { "Smithsonian" }, paddingPt: 20)
            .Candidates.Should().BeEmpty();
    }

    [Fact]
    public void TheCharacterRangeIsNotRepeatedAsAPatternClass()
    {
        // The report carries Min/Max separately; listing the range again as a
        // "pattern" printed it twice in the CLI.
        var report = RedactionFitAnalyzer.Analyse(WidthOf("Harper"), Font, Size);
        report.PatternClasses.Should().NotContain(p => p.Contains("characters"));
    }

    [Fact]
    public void AMarkNothingRecovered_CarriesAFitAnalysis()
    {
        // The case the analysis exists for: a redaction that HELD. The text did
        // not come back, so the width budget is the only thing left to say
        // about what was under it.
        var bytes = RecoveryFixtureBuilder.Build(
            // The box sits CLEAR of the label: overlapping it would make the
            // mark partially recovered, which is a different case.
            "BT /F1 12 Tf 72 700 Td (Name:) Tj ET\n" +
            "q 0 0 0 rg 115 697 34 15 re f Q\n");

        using var doc = Core.Document.PdfDocument.Open(bytes);
        var report = RecoveryScanner.Scan(doc);

        var mark = report.Marks.Single();
        mark.Outcome.Should().Be(MarkRecoveryOutcome.NotRecovered);
        mark.Fit.Should().NotBeNull("a mark that held is exactly where the constraint matters");
        mark.Fit!.MaxCharacters.Should().BeGreaterThan(0);
        mark.Fit.Font.Should().Be("Helvetica",
            "the resource name /F1 must resolve to /BaseFont or every standard-14 " +
            "document lands on the no-metrics path");
    }

    [Fact]
    public void AMarkWhoseTextCameBack_CarriesNoFitAnalysis()
    {
        // Redundant noise: we have the text. Printing a candidate list next to
        // a recovered value invites reading the list as competing answers.
        using var doc = Core.Document.PdfDocument.Open(
            RecoveryFixtureBuilder.TextUnderBox("MANAFORT"));
        var report = RecoveryScanner.Scan(doc);

        report.Marks.Single(m => m.Outcome == MarkRecoveryOutcome.Recovered)
            .Fit.Should().BeNull();
    }

    [Fact]
    public void TheWidthBudget_AgreesWithMuPdfsGlyphPositions()
    {
        // No-self-oracle. excise's character range rests on its own font
        // metrics; if those are wrong the range is confidently wrong. MuPDF
        // lays the same string out independently.
        Assert.SkipUnless(MutoolStextOracle.IsAvailable, "mutool is not on PATH");

        const string secret = "MANAFORT";
        var bytes = RecoveryFixtureBuilder.TextUnderBox(secret, drawBox: false, fontSize: Size);
        var word = MutoolStextOracle.Words(bytes, 1, 792).Single(w => w.Text == secret);
        var mutoolWidth = word.Right - word.Left;

        RedactionFitAnalyzer.MeasureWidth(secret, Font, Size)
            .Should().BeApproximately(mutoolWidth, 0.5,
                "excise's metrics must match an independent layout engine's");

        // And the range derived from those metrics must bracket the truth.
        var report = RedactionFitAnalyzer.Analyse(mutoolWidth, Font, Size);
        report.MinCharacters.Should().BeLessThanOrEqualTo(secret.Length);
        report.MaxCharacters.Should().BeGreaterThanOrEqualTo(secret.Length);
    }
}
