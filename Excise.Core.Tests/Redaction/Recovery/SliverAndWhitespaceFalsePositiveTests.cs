using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Redaction;
using Excise.Core.Redaction.Recovery;
using Xunit;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1625 — two independent reasons a clean court filing reported a failed
/// redaction, both found on <c>recap_ddff65c821d6b7d1.pdf</c>: a 468 × 0.48 pt
/// table rule became a <c>FilledBox</c> mark, and the seven "recovered"
/// findings under it were each a single SPACE.
///
/// <para>Measured before and after on that file: <b>1 mark / 7 certain
/// findings → 0 marks / 0 certain findings</b>, with the two remaining
/// findings correctly classed as document furniture (<c>/Info /Author</c> and
/// XMP <c>dc:creator</c>). The Manafort pair is unchanged either way — #471
/// still 25 marks / 24 recovered, #472 still 24 / 0 — which is what says this
/// is a false-positive fix and not a suppression.</para>
///
/// <para>⚠️ Each half is pinned on BOTH sides. A gate that only asserts the
/// absence of something would pass on a fixture that never carried it; #1586
/// learned that, so every test here has a sibling proving the same fixture
/// still reports when it should.</para>
/// </summary>
public class SliverAndWhitespaceFalsePositiveTests
{
    // ── The synthesis door (RecoveryReportBuilder) ──────────────────────────

    /// <summary>
    /// A finding whose obstruction hint is rule-shaped must not become a mark.
    /// <c>RedactionMarkDetector</c> rejects these at both of its call sites;
    /// before #1625 the builder synthesised one anyway, because it held no
    /// opinion about what a mark is.
    /// </summary>
    [Fact]
    public void ARuleShapedHint_IsNotSynthesisedIntoAMark()
    {
        var report = ReportFromHint(new PdfRectangle(72, 199.2, 540, 199.68));   // 468 × 0.48

        report.Marks.Should().BeEmpty(
            "a 0.48pt-tall rectangle is a table rule, and the detector's own " +
            "MinSidePt gate says so — synthesis must not be a third door around it");
    }

    /// <summary>
    /// The other side: a bar-shaped hint STILL synthesises a mark. Without
    /// this, the test above would pass on a builder that synthesised nothing.
    /// </summary>
    [Fact]
    public void ABarShapedHint_IsStillSynthesisedIntoAMark()
    {
        var report = ReportFromHint(new PdfRectangle(72, 499.19, 540, 513));     // 468 × 13.8

        report.Marks.Should().HaveCount(1,
            "13.8pt is the height of a real redaction bar — the Manafort filing's");
    }

    /// <summary>
    /// The threshold is one constant, not a copy per call site. A second copy
    /// is how #1625 happened; this fails if someone reintroduces one.
    /// </summary>
    [Theory]
    [InlineData(1.99, false)]
    [InlineData(2.0, true)]
    public void TheSizeGateIsTheSharedThreshold(double side, bool expected)
        => RecoveryGeometry.IsMarkSized(468, side).Should().Be(expected);

    // ── The whitespace half (HiddenTextDetector.CoveredRun) ─────────────────

    /// <summary>
    /// A covered run of pure whitespace is not a leak: there is nothing in a
    /// space to recover. This is the shape that produced seven findings of
    /// <c>" "</c> on a filing that leaks nothing.
    /// </summary>
    [Fact]
    public void ABoxCoveringOnlySpaces_ReportsNoHiddenText()
    {
        using var document = PdfDocument.Open(
            RecoveryFixtureBuilder.TextUnderBox("     ", fontSize: 14));

        global::Excise.Core.Text.Segmentation.HiddenTextDetector.Scan(document, includeVisibleFailedRedactions: true)
            .Should().BeEmpty("a run of spaces carries no recoverable text");
    }

    /// <summary>
    /// The other side, and the one that would catch an over-eager filter: the
    /// same detector must still report a covered WORD.
    /// </summary>
    [Fact]
    public void ABoxCoveringAWord_StillReportsIt()
    {
        using var document = PdfDocument.Open(
            RecoveryFixtureBuilder.TextUnderBox("KILIMNIK", fontSize: 14));

        global::Excise.Core.Text.Segmentation.HiddenTextDetector.Scan(document, includeVisibleFailedRedactions: true)
            .Should().NotBeEmpty("the whitespace rule must not suppress real covered text");
    }

    // ⚠️ THERE IS NO SYNTHETIC TEST HERE FOR THE REAL FILE'S SHAPE, and the
    // absence is deliberate. One was written — a 0.48pt rule laid across a
    // line of ordinary text, on the theory that a space's glyph cell is
    // degenerate and so gets "majority-covered" by a rule that cannot cover a
    // 14pt letter. It passed. It also passed with BOTH fixes reverted, which
    // means it never exercised them: excise gives a space a real cell (#1618 —
    // the nominal em cell, not an ink box), so the rule covers nothing at all
    // and the assertion was vacuous.
    //
    // Per #1527 an inert gate is worse than no gate, because it reads as
    // coverage. Deleted rather than kept. What the real mechanism is on
    // recap_ddff65c821d6b7d1.pdf is not yet explained; the measured
    // before/after on that file is recorded in this class's summary, and the
    // rule that actually fires — a covered run with no ink is not a finding —
    // is gated by ABoxCoveringOnlySpaces_ReportsNoHiddenText, which does go red
    // when the fix is reverted.

    private static RecoveryReport ReportFromHint(PdfRectangle hint)
    {
        var finding = RecoveredFinding.Certain(
            "hidden-text", "black filled rectangle", "SECRET",
            new RecoveryLocation(4, hint, "test"));

        return new RecoveryReportBuilder().AddFinding(finding, hint).Build();
    }
}
