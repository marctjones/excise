using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Redaction.Recovery;
using Xunit;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1587 — the linking and grading rules, over synthetic marks and findings.
/// No PDF and no engine: the arithmetic that turns "8 findings" into "3 of 11
/// marks came back" is the part a reviewer has to be able to check by reading
/// it, and a fixture would only obscure it.
/// </summary>
public class RecoveryReportBuilderTests
{
    private static RedactionMark Mark(string id, double left, double right, int page = 1)
        => new(id, page, new PdfRectangle(left, 100, right, 118),
               RedactionMarkKind.FilledBox, "black filled rectangle");

    private static RecoveredFinding Certain(double left, double right, int page = 1, string text = "SMITH")
        => RecoveredFinding.Certain("hidden-text", "black filled rectangle", text,
            new RecoveryLocation(page, new PdfRectangle(left, 102, right, 114), "glyph boxes"));

    [Fact]
    public void MarkWithNoFinding_IsReportedAsNotRecovered()
    {
        // The whole reason marks are enumerated separately: a report built only
        // from channel output would not contain this row at all, and would read
        // as success over a redaction that held.
        var report = new RecoveryReportBuilder()
            .AddMarks(new[] { Mark("p1m1", 100, 160) })
            .Build();

        report.MarkCount.Should().Be(1);
        report.Marks[0].Outcome.Should().Be(MarkRecoveryOutcome.NotRecovered);
        report.MarksNotRecovered.Should().Be(1);
        report.MarksRecovered.Should().Be(0);
    }

    [Fact]
    public void CertainFindingCoveringTheMark_GradesRecovered()
    {
        var report = new RecoveryReportBuilder()
            .AddMarks(new[] { Mark("p1m1", 100, 160) })
            .AddFinding(Certain(102, 158))
            .Build();

        report.Marks[0].Outcome.Should().Be(MarkRecoveryOutcome.Recovered);
        report.Marks[0].Findings.Should().ContainSingle()
            .Which.MarkId.Should().Be("p1m1", "a linked finding records the mark it belongs to");
        report.Unlinked.Should().BeEmpty();
    }

    [Fact]
    public void CertainFindingCoveringPartOfTheMark_GradesPartial()
    {
        // 100..160 is 60pt of mark; the finding accounts for 20pt of it. The
        // rest of the redaction did not come back, and calling that "recovered"
        // would be the exact overstatement this grading exists to prevent.
        var report = new RecoveryReportBuilder()
            .AddMarks(new[] { Mark("p1m1", 100, 160) })
            .AddFinding(Certain(100, 120))
            .Build();

        report.Marks[0].Outcome.Should().Be(MarkRecoveryOutcome.PartiallyRecovered);
    }

    [Fact]
    public void CandidateOnly_NeverGradesRecovered()
    {
        var report = new RecoveryReportBuilder()
            .AddMarks(new[] { Mark("p1m1", 100, 160) })
            .AddFinding(RecoveredFinding.Candidate(
                "residue", "width residue", new[] { "Harper", "Hansen" }, 1.0,
                new RecoveryLocation(1, new PdfRectangle(100, 102, 160, 114), "residue gap")))
            .Build();

        report.Marks[0].Outcome.Should().Be(MarkRecoveryOutcome.CandidatesOnly,
            "a width-admissible set is a constraint, never a reading");
    }

    /// <summary>
    /// ⚠️ This asserted <c>CandidatesOnly</c> until #1707, with the reason
    /// "pixels under a box are a real leak but not a recovered VALUE". The
    /// second half is right and the grade was wrong: <c>CandidatesOnly</c> says
    /// the value is CONSTRAINED, and here it is not constrained at all — it is
    /// intact in the file and merely undecoded. The tool printed a leaked
    /// photograph as <c>~ candidates-only</c> with zero candidates anywhere in
    /// the report, and exited 0.
    /// </summary>
    [Fact]
    public void PresentOnly_GradesContentSurvives_NotACandidateSet()
    {
        var report = new RecoveryReportBuilder()
            .AddMarks(new[] { Mark("p1m1", 100, 160) })
            .AddFinding(RecoveredFinding.PresentOnly(
                "covered-image", "image XObject /Im0",
                new RecoveryLocation(1, new PdfRectangle(100, 102, 160, 114), "covered content box")))
            .Build();

        report.Marks[0].Outcome.Should().Be(MarkRecoveryOutcome.ContentSurvives,
            "material is intact under the mark; nothing about its VALUE is claimed or constrained");
        report.MarksContentSurvives.Should().Be(1);
        report.MarksCandidatesOnly.Should().Be(0,
            "the old grade absorbed this case, so the count has to stop absorbing it too");
    }

    /// <summary>
    /// The other side of #1707's boundary: a claim about the VALUE outranks
    /// "something is here". Without this, the obvious implementation — any
    /// present-only finding wins — would silently downgrade a mark that DOES
    /// have a candidate set.
    /// </summary>
    [Fact]
    public void PresentOnlyMixedWithACandidate_StaysCandidatesOnly()
    {
        var report = new RecoveryReportBuilder()
            .AddMarks(new[] { Mark("p1m1", 100, 160) })
            .AddFinding(RecoveredFinding.PresentOnly(
                "covered-image", "image XObject /Im0",
                new RecoveryLocation(1, new PdfRectangle(100, 102, 160, 114), "covered content box")))
            .AddFinding(RecoveredFinding.Candidate(
                "residue", "width residue", new[] { "Harper", "Hansen" }, 1.0,
                new RecoveryLocation(1, new PdfRectangle(100, 102, 160, 114), "residue gap")))
            .Build();

        report.Marks[0].Outcome.Should().Be(MarkRecoveryOutcome.CandidatesOnly,
            "a constrained set is a statement about the value; presence is not");
        report.MarksContentSurvives.Should().Be(0);
    }

    [Fact]
    public void FindingOverlappingNoMark_IsKeptAsUnlinked()
    {
        // The commonest real leak: a carrier the redactor never scrubbed, on a
        // page where no box was drawn near it. Dropping it would hide the leak;
        // binding it to the nearest box would misattribute it.
        var report = new RecoveryReportBuilder()
            .AddMarks(new[] { Mark("p1m1", 100, 160) })
            .AddFinding(Certain(400, 460))
            .Build();

        report.Marks[0].Outcome.Should().Be(MarkRecoveryOutcome.NotRecovered);
        report.Unlinked.Should().ContainSingle();
        report.Unlinked[0].MarkId.Should().BeNull();
    }

    [Fact]
    public void FindingOnAnotherPage_DoesNotLinkToThisPagesMark()
    {
        var report = new RecoveryReportBuilder()
            .AddMarks(new[] { Mark("p1m1", 100, 160) })
            .AddFinding(Certain(100, 160, page: 2))
            .Build();

        report.Marks[0].Outcome.Should().Be(MarkRecoveryOutcome.NotRecovered);
        report.Unlinked.Should().ContainSingle();
    }

    [Fact]
    public void FindingWithNoLocation_GoesToTheDocumentLevelList()
    {
        var report = new RecoveryReportBuilder()
            .AddMarks(new[] { Mark("p1m1", 100, 160) })
            .AddFinding(RecoveredFinding.Certain("carrier", "/Info /Title", "SECRET", location: null))
            .Build();

        report.DocumentLevel.Should().ContainSingle();
        report.Unlinked.Should().BeEmpty();
        report.Marks[0].Outcome.Should().Be(MarkRecoveryOutcome.NotRecovered);
    }

    [Fact]
    public void TwoFindingsOverTheSameRun_DoNotAddUpToMoreCoverageThanTheMarkHas()
    {
        // Union, not sum. Two channels reading the SAME 30pt run cover half of
        // a 60pt mark; summing them would reach 60pt and grade it fully
        // recovered, which is the overstatement this rule exists to prevent.
        var report = new RecoveryReportBuilder()
            .AddMarks(new[] { Mark("p1m1", 100, 160) })
            .AddFinding(Certain(100, 130))
            .AddFinding(Certain(100, 130, text: "SMITH"))
            .Build();

        report.Marks[0].Outcome.Should().Be(MarkRecoveryOutcome.PartiallyRecovered);
    }

    [Fact]
    public void AHintedMarkThatWasNotDetected_IsSynthesised()
    {
        // A channel that knows the rectangle it read under always contributes a
        // mark, so the denominator cannot silently exclude redactions the
        // geometry filter was too strict to see.
        var report = new RecoveryReportBuilder()
            .AddFinding(Certain(100, 158), markHint: new PdfRectangle(98, 99, 162, 119))
            .Build();

        report.MarkCount.Should().Be(1);
        report.Marks[0].Outcome.Should().Be(MarkRecoveryOutcome.Recovered);
        report.Marks[0].Mark.Description.Should().Contain("hidden-text");
        report.Unlinked.Should().BeEmpty();
    }

    [Fact]
    public void AResidueHint_SynthesisesAnEmptiedRegionNotABox()
    {
        // The kind follows the evidence: the residue channel saw a hole where a
        // run used to be, with nothing drawn over it.
        var report = new RecoveryReportBuilder()
            .AddFinding(
                RecoveredFinding.Candidate("residue", "width residue", new[] { "Harper" }, 0,
                    new RecoveryLocation(1, new PdfRectangle(100, 102, 160, 114), "residue gap")),
                markHint: new PdfRectangle(100, 100, 160, 118))
            .Build();

        report.MarkCount.Should().Be(1);
        report.Marks[0].Mark.Kind.Should().Be(RedactionMarkKind.EmptiedRegion);
    }

    [Fact]
    public void ChannelsRunAndSkipped_AreBothRecorded()
    {
        // A report over three channels must not read like one over eight.
        var report = new RecoveryReportBuilder()
            .ChannelRan("hidden-text")
            .ChannelSkipped("ocr-differential", "tesseract not on PATH")
            .Build();

        report.ChannelsRun.Should().ContainSingle().Which.Should().Be("hidden-text");
        report.ChannelsSkipped.Should().ContainKey("ocr-differential");
    }

    [Theory]
    // Full span, one box.
    [InlineData(new[] { 100.0, 160.0 }, 1.0)]
    // Half.
    [InlineData(new[] { 100.0, 130.0 }, 0.5)]
    // Two disjoint thirds -> two thirds.
    [InlineData(new[] { 100.0, 120.0, 140.0, 160.0 }, 0.666)]
    // Overlapping boxes must not double-count.
    [InlineData(new[] { 100.0, 130.0, 110.0, 140.0 }, 0.666)]
    // Clipped to the mark: a finding wider than the mark cannot exceed 1.
    [InlineData(new[] { 0.0, 600.0 }, 1.0)]
    public void CoveredWidthFraction_IsAUnionClippedToTheMark(double[] spans, double expected)
    {
        var mark = new PdfRectangle(100, 100, 160, 118);
        var boxes = new List<PdfRectangle>();
        for (var i = 0; i < spans.Length; i += 2)
            boxes.Add(new PdfRectangle(spans[i], 102, spans[i + 1], 114));

        RecoveryReportBuilder.CoveredWidthFraction(mark, boxes)
            .Should().BeApproximately(expected, 0.01);
    }
}
