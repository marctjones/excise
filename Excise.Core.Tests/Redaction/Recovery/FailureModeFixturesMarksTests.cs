using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Redaction.Recovery;
using Xunit;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1645 batch 2 — the mark families. Every variant is checked; a fixture
/// nobody ran is coverage on paper.
/// </summary>
public class FailureModeFixturesMarksTests
{
    private const string Secret = "KILIMNIK";

    private static RecoveryReport Scan(byte[] pdf) => RecoveryScanner.Scan(pdf);

    private static bool Found(byte[] pdf, string text = Secret) =>
        Scan(pdf).AllFindings.Any(f => (f.Text ?? "").Contains(text));

    private static bool FoundOn(byte[] pdf, string channel, string text = Secret) =>
        Scan(pdf).AllFindings.Any(f => f.Channel == channel && (f.Text ?? "").Contains(text));

    // ── box-drawn-by-annotation ─────────────────────────────────────────────

    [Fact]
    public void Annotation_Square_IsAMarkAndItsTextIsRecovered()
        => Found(FailureModeFixtures.AnnotationSquareOverText(Secret)).Should().BeTrue();

    /// <summary>A channel keyed on `Subtype /Square` alone misses this.</summary>
    [Fact]
    public void Annotation_Highlight_IsAlsoAMark()
        => Found(FailureModeFixtures.AnnotationHighlightOverText(Secret)).Should().BeTrue();

    /// <summary>
    /// ⚠️ EXPECTED MISS, and a DELIBERATE scope boundary rather than a gap.
    /// RedactionMarkDetector accepts /Square, /Circle, /Polygon and /Highlight;
    /// /Stamp is excluded because a stamp with a dark interior is overwhelmingly
    /// a legitimate graphic — FILED, APPROVED, an exhibit sticker — and treating
    /// every one as a redaction mark is the #1624 flood from another direction.
    ///
    /// <para>Pinned so the boundary is VISIBLE. It was undocumented before this
    /// fixture; if someone later decides stamps should count, this test is where
    /// the decision gets recorded.</para>
    /// </summary>
    [Fact]
    public void Annotation_Stamp_IsDeliberatelyNotAMark()
        => Found(FailureModeFixtures.AnnotationStampOverText(Secret)).Should().BeFalse(
            "a dark /Stamp is usually legitimate; the accepted subtypes are " +
            "Square, Circle, Polygon and Highlight");

    // ── box-inside-form-xobject ─────────────────────────────────────────────

    [Fact]
    public void FormXObject_OneDeep_IsAMarkAndItsTextIsRecovered()
        => Found(FailureModeFixtures.BoxInFormDepth1(Secret)).Should().BeTrue();

    [Fact]
    public void FormXObject_TwoDeep_IsStillFound()
        => Found(FailureModeFixtures.BoxInFormDepth2(Secret)).Should().BeTrue(
            "each nesting level contributes a /Matrix that must compose");

    /// <summary>A recursion bound of two passes the shallower fixtures and reports a clean page here.</summary>
    [Fact]
    public void FormXObject_ThreeDeep_IsStillFound()
        => Found(FailureModeFixtures.BoxInFormDepth3(Secret)).Should().BeTrue();

    /// <summary>
    /// The child form is named ONLY in its parent form's /Resources, which is
    /// where §8.10.1 puts it — and the covering box inside it is found (#1666).
    ///
    /// <para>This was an EXPECTED MISS when written, and its own message said
    /// to promote it rather than delete it if the gap ever closed. It closed in
    /// the same pass: the fixture isolated page-only /XObject resolution from
    /// nesting depth, which is what made the one-line fix obvious.</para>
    ///
    /// <para>⚠️ The pair is the point and both must stay. TwoDeep/ThreeDeep also
    /// name every form on the page, so they pin DEPTH; this one names the child
    /// only where the spec puts it, so it pins the LOOKUP. Either alone would
    /// have left the other cause unexplained.</para>
    /// </summary>
    [Fact]
    public void FormXObject_ChildScopedToTheForm_IsFound()
        => Found(FailureModeFixtures.BoxInFormChildScopedToTheForm(Secret)).Should().BeTrue(
            "§8.10.1 scopes a nested Do to its parent form's /Resources");

    // ── redact-annotation-unapplied ─────────────────────────────────────────

    [Fact]
    public void UnappliedRedact_OverAWord_IsRecovered()
        => Found(FailureModeFixtures.UnappliedRedactOverAWord(Secret)).Should().BeTrue();

    /// <summary>The file states both the cover story and the original.</summary>
    [Fact]
    public void UnappliedRedact_WithOverlayText_StillLeaksTheOriginal()
        => Found(FailureModeFixtures.UnappliedRedactWithOverlayText(Secret)).Should().BeTrue();

    /// <summary>
    /// The common real shape: one name inside a sentence is marked and the
    /// surrounding words are meant to stay. The recovery must be the marked run,
    /// not the whole line.
    /// </summary>
    [Fact]
    public void UnappliedRedact_OverPartOfALine_RecoversTheMarkedRun()
    {
        var report = Scan(FailureModeFixtures.UnappliedRedactOverPartOfALine(Secret));
        report.AllFindings.Any(f => (f.Text ?? "").Contains(Secret)).Should().BeTrue();
    }

    // ── marked-content-carrier ──────────────────────────────────────────────

    [Fact]
    public void MarkedContent_ActualText_IsRecovered()
        => FoundOn(FailureModeFixtures.MarkedContentActualText(Secret),
                RecoveryScanner.Channels.MarkedContent).Should().BeTrue();

    [Fact]
    public void MarkedContent_Alt_IsRecovered()
        => FoundOn(FailureModeFixtures.MarkedContentAlt(Secret),
                RecoveryScanner.Channels.MarkedContent).Should().BeTrue();

    /// <summary>
    /// ⚠️ #1599 — the SCRUB side misses a named property list. Recovery reading
    /// it is what keeps that gap measurable instead of theoretical: if this
    /// stops passing, the audit can no longer see a carrier redaction leaves
    /// behind.
    /// </summary>
    [Fact]
    public void MarkedContent_NamedPropertyList_IsRecovered()
        => FoundOn(FailureModeFixtures.MarkedContentNamedPropertyList(Secret),
                RecoveryScanner.Channels.MarkedContent).Should().BeTrue(
                "#1599: the scrubber leaves this carrier behind, so the audit must read it");
}
