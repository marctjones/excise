using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Redaction;
using Excise.Core.Tests.TestSupport;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests;

/// <summary>
/// #1799 — a /Hidden or /NoView-flagged annotation's /Contents survives
/// Standard-profile redaction. <c>RemoveHiddenAppearances</c>
/// (RedactionFeatureStripper.cs) only strips /AP — nothing checks these two
/// flag bits when deciding whether to drop the WHOLE annotation dictionary,
/// unlike the OC-hidden-layer case a few hundred lines below it, which does
/// (same "invisible to the reviewer, readable by every tool" reasoning
/// REDACTION_AI_GUIDELINES.md's carrier table already gives for that case).
/// </summary>
public class HiddenAnnotationLeakProbeTests
{
    [Fact(Skip = "#1799: Standard profile does not remove a Hidden/NoView-flagged " +
                 "annotation's /Contents — only its /AP. Unskip once fixed.")]
    public void StandardProfile_HiddenFlaggedTextAnnotation_WithNoAppearanceStream_LeaksContents()
        => AssertContentsScrubbed(RedactionProfile.Standard);

    /// <summary>
    /// Passes today, but NOT because Maximum targets hidden annotations
    /// specifically — <c>RemoveMarkupAnnotations</c> wipes every markup
    /// annotation, visible or hidden alike, as a blanket sweep, and happens
    /// to catch this one as a side effect. Kept active so a future change
    /// that narrows that sweep (e.g. "only remove VISIBLE markup, #1799
    /// already handles hidden ones") cannot reopen this specific case
    /// silently.
    /// </summary>
    [Fact]
    public void MaximumProfile_HiddenFlaggedTextAnnotation_WithNoAppearanceStream_ContentsScrubbed()
        => AssertContentsScrubbed(RedactionProfile.Maximum);

    private static void AssertContentsScrubbed(RedactionProfile profile)
    {
        const string secret = "SECRETHIDDENANNOTATIONPROBE";
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var annot = doc.AddTextAnnotation(1, new PdfRectangle(100, 700, 117, 717), secret);
        annot.RawDictionary.SetInt("F", (int)PdfAnnotationFlags.Hidden);

        RedactionFeatureStripper.Apply(doc, RedactionOptions.ForProfile(profile));
        var saved = doc.SaveToBytes();

        // Independent scanner (decompresses streams, not excise's own reader)
        // — CLAUDE.md's no-self-oracle rule.
        SavedPdfLeakScanner.FindTerm(saved, secret).Should().BeEmpty(
            $"a Hidden-flagged annotation's /Contents must not survive {profile}-profile redaction");
    }
}
