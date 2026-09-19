using System.Linq;
using AwesomeAssertions;
using Excise.Core.Redaction.Recovery;
using Xunit;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>#1645 batch 3 — contrast, invisibility, covered non-text, artefacts, forms, structure.</summary>
public class FailureModeFixturesRestTests
{
    private const string Secret = "KILIMNIK";

    private static RecoveryReport Scan(byte[] pdf) => RecoveryScanner.Scan(pdf);
    private static bool Found(byte[] pdf, string text = Secret) =>
        Scan(pdf).AllFindings.Any(f => (f.Text ?? "").Contains(text));
    private static bool OnChannel(byte[] pdf, string channel) =>
        Scan(pdf).AllFindings.Any(f => f.Channel == channel);

    // ── box-light-or-low-contrast ───────────────────────────────────────────

    [Fact] public void Contrast_BlackOnBlack_IsFound()
        => Found(FailureModeFixtures.LowContrastExact(Secret)).Should().BeTrue();

    [Fact] public void Contrast_NearMatchUnderTheThreshold_IsFound()
        => Found(FailureModeFixtures.LowContrastNearMatch(Secret)).Should().BeTrue(
            "RGB distance ~0.087 is under the 0.20 threshold and unreadable on screen");

    /// <summary>
    /// Red on black is READABLE — RGB distance 1.0, well over the 0.20
    /// threshold — so pairing B (low contrast) correctly does not fire.
    ///
    /// <para>⚠️ It IS reported, by pairing C, and my first expectation here was
    /// wrong. #1180: readable text on a dark, text-sized fill is a VISIBLE
    /// FAILED redaction — somebody drew a box and the text is on top of it —
    /// and an audit must surface that. The fixture is kept with the corrected
    /// expectation because the distinction between "not low-contrast" and "not
    /// reported" is exactly the thing to get wrong.</para>
    /// </summary>
    [Fact] public void Contrast_RedOnBlack_IsReportedAsAVisibleFailedRedaction()
    {
        var carriers = Scan(FailureModeFixtures.RedOnBlackIsReadable(Secret)).AllFindings
            .Where(f => (f.Text ?? "").Contains(Secret)).Select(f => f.Carrier).ToList();

        carriers.Should().NotBeEmpty("#1180: a box with legible text on it is a redaction that did not take");
        carriers.Should().NotContain(c => c.Contains("low-contrast"),
            "RGB distance 1.0 is not low contrast — that is pairing B's job and it correctly declined");
    }

    // ── text-render-mode-3 ──────────────────────────────────────────────────

    [Fact] public void Invisible_Mode3_IsFound()
        => Found(FailureModeFixtures.InvisibleTextPlain(Secret)).Should().BeTrue();

    /// <summary>§9.3.6 Table 106: mode 7 is add-to-clip, also invisible.</summary>
    [Fact] public void Invisible_Mode7Clip_IsAlsoFound()
        => Found(FailureModeFixtures.InvisibleTextClipMode(Secret)).Should().BeTrue(
            "a detector keyed on the literal 3 misses add-to-clip");

    /// <summary>
    /// ⚠️ NEGATIVE control for the Table 52 restore: text after the Q is
    /// VISIBLE and must not be reported. A detector leaking Tr past the restore
    /// reports both.
    /// </summary>
    [Fact] public void Invisible_RestoredByQ_DoesNotReportTheVisibleRun()
        => Found(FailureModeFixtures.InvisibleTextRestoredByQ(Secret), "PUBLIC")
            .Should().BeFalse("3 Tr is Table 52 text state and Q restores it");

    // ── image-covered-only / vector-covered-only ────────────────────────────

    [Fact] public void Image_FullyCovered_IsPresentOnly()
        => OnChannel(FailureModeFixtures.ImageFullyCovered(),
            RecoveryScanner.Channels.CoveredImage).Should().BeTrue();

    [Fact] public void Image_PartlyCovered_IsStillReported()
        => OnChannel(FailureModeFixtures.ImagePartlyCovered(),
            RecoveryScanner.Channels.CoveredImage).Should().BeTrue(
            "a partial cover still hides part of the image");

    [Fact] public void Image_OnlyTheCoveredOneOfTwo_IsReported()
        => Scan(FailureModeFixtures.OneOfTwoImagesCovered()).AllFindings
            .Count(f => f.Channel == RecoveryScanner.Channels.CoveredImage)
            .Should().Be(1, "the uncovered image is not a leak");

    [Fact] public void Vector_StrokesCovered_IsPresentOnly()
        => OnChannel(FailureModeFixtures.VectorFullyCovered(),
            RecoveryScanner.Channels.CoveredVector).Should().BeTrue();

    [Fact] public void Vector_FilledShapeCovered_IsPresentOnly()
        => OnChannel(FailureModeFixtures.FilledVectorCovered(),
            RecoveryScanner.Channels.CoveredVector).Should().BeTrue();

    /// <summary>Bounds of a curve come from its control points, not its endpoints.</summary>
    [Fact] public void Vector_CurveCovered_IsPresentOnly()
        => OnChannel(FailureModeFixtures.CurveUnderBox(),
            RecoveryScanner.Channels.CoveredVector).Should().BeTrue();

    // ── leftover-page-thumbnail ─────────────────────────────────────────────

    [Fact] public void Thumbnail_Small_IsReported()
        => OnChannel(FailureModeFixtures.ThumbnailOnTheOnlyPage(),
            RecoveryScanner.Channels.Thumbnail).Should().BeTrue();

    [Fact] public void Thumbnail_LargeEnoughToRead_IsReported()
        => OnChannel(FailureModeFixtures.LargeThumbnail(),
            RecoveryScanner.Channels.Thumbnail).Should().BeTrue();

    [Fact] public void Thumbnail_Rgb_IsReported()
        => OnChannel(FailureModeFixtures.RgbThumbnail(),
            RecoveryScanner.Channels.Thumbnail).Should().BeTrue("colour space must not gate the report");

    // ── leftover-embedded-file ──────────────────────────────────────────────

    [Fact] public void Attachment_InTheNameTree_IsReported()
        => OnChannel(FailureModeFixtures.AttachmentInNameTree(),
            RecoveryScanner.Channels.Attachment).Should().BeTrue();

    /// <summary>
    /// ⚠️ EXPECTED MISS (#1667) — and the asymmetry is the finding. The
    /// SCRUBBER walks page and annotation /AF (#1572); the AUDIT calls
    /// GetEmbeddedFiles(), which walks only /Catalog/Names/EmbeddedFiles,
    /// /Catalog/Names/AF and /Catalog/AF. So excise REMOVES an attachment it
    /// cannot SEE: `redact` strips this file and `unredact` calls the document
    /// clean.
    /// </summary>
    [Fact] public void Attachment_OnPageAssociatedFiles_IsNotYetSeenByTheAudit()
        => OnChannel(FailureModeFixtures.AttachmentOnPageAssociatedFiles(),
            RecoveryScanner.Channels.Attachment).Should().BeFalse(
            "#1667: the audit's embedded-file walk is catalog-only");

    [Fact] public void Attachment_AsAFileAttachmentAnnotation_IsReported()
        => OnChannel(FailureModeFixtures.AttachmentAsAnnotation(),
            RecoveryScanner.Channels.Attachment).Should().BeTrue();

    // ── leftover-form-value ─────────────────────────────────────────────────

    [Fact] public void FormValue_TextField_IsRecovered()
        => Found(FailureModeFixtures.FormTextFieldValue("123-45-6789"), "123-45-6789")
            .Should().BeTrue();

    [Fact] public void FormValue_ChoiceField_IsRecovered()
        => Found(FailureModeFixtures.FormChoiceFieldValue("123-45-6789"), "123-45-6789")
            .Should().BeTrue("/V on a /Ch field is the selected option");

    /// <summary>A viewer resets to /DV, so it leaks exactly as /V does.</summary>
    [Fact] public void FormValue_DefaultValueOnly_IsRecovered()
        => Found(FailureModeFixtures.FormDefaultValueOnly("123-45-6789"), "123-45-6789")
            .Should().BeTrue("/DV is what a reset restores");

    // ── structure-tree-carrier ──────────────────────────────────────────────

    [Fact] public void Structure_ActualText_IsRecovered()
        => Found(FailureModeFixtures.StructureActualText(Secret)).Should().BeTrue();

    [Fact] public void Structure_Alt_IsRecovered()
        => Found(FailureModeFixtures.StructureAlt(Secret)).Should().BeTrue();

    /// <summary>§14.9.4 /E, the abbreviation expansion — the least-known of the three.</summary>
    [Fact] public void Structure_Expansion_IsRecovered()
        => Found(FailureModeFixtures.StructureExpansion(Secret)).Should().BeTrue();
}
