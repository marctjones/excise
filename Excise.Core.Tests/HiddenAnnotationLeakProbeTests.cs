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
    [Fact]
    public void StandardProfile_HiddenFlaggedTextAnnotation_WithNoAppearanceStream_ContentsScrubbed()
        => AssertContentsScrubbed(RedactionProfile.Standard);

    /// <summary>
    /// The planted failure: with the knob off the same document leaks, so the
    /// Standard assertion above is not passing by accident.
    /// </summary>
    [Fact]
    public void StandardProfile_WithHiddenAnnotationKnobOff_StillLeaks()
    {
        const string secret = "SECRETKNOBOFFPROBE";
        using var doc = HiddenTextAnnotationDoc(secret);

        RedactionFeatureStripper.Apply(doc,
            RedactionOptions.Default with { RemoveHiddenAnnotationAppearances = false });

        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), secret).Should().NotBeEmpty();
    }

    /// <summary>
    /// A /Text annotation carries a linked /Popup whose /Parent points back at
    /// it. Dropping only the parent from /Annots would leave the Popup holding
    /// it reachable — /Contents and all — in the saved bytes.
    /// </summary>
    [Fact]
    public void StandardProfile_DroppedAnnotation_LeavesNoPopupHoldingItsContents()
    {
        const string secret = "SECRETPOPUPPARENTPROBE";
        using var doc = HiddenTextAnnotationDoc(secret);

        var report = RedactionFeatureStripper.Apply(doc, RedactionOptions.Default);

        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), secret).Should().BeEmpty();
        report.Should().Contain(r => r.Feature == "hidden annotation(s) removed" && r.Count >= 1,
            "every removal made on the user's behalf is reported (CLAUDE.md rule 6)");
    }

    /// <summary>
    /// A conditionally hidden form field is routinely /F Hidden. Its dictionary
    /// is the field itself, so it must survive, with only its text carriers
    /// scrubbed.
    /// </summary>
    [Fact]
    public void StandardProfile_HiddenWidget_KeepsTheField_ButScrubsItsContents()
    {
        const string secret = "SECRETHIDDENWIDGETPROBE";
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var field = doc.AddTextField(1, new PdfRectangle(100, 600, 300, 620), "conditional");
        field.RawDictionary.SetInt("F", (int)PdfAnnotationFlags.Hidden);
        field.RawDictionary.SetString("Contents", secret);

        RedactionFeatureStripper.Apply(doc, RedactionOptions.Default);
        var saved = doc.SaveToBytes();

        SavedPdfLeakScanner.FindTerm(saved, secret).Should().BeEmpty();
        using var reopened = PdfDocument.Open(saved);
        reopened.GetAcroForm()!.Fields.Should().ContainSingle(f => f.FullName == "conditional",
            "removing the widget dictionary would orphan the field in /AcroForm /Fields");
    }

    [Fact]
    public void StandardProfile_VisibleTextAnnotation_IsKept()
    {
        const string note = "VISIBLENOTEKEPT";
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        doc.AddTextAnnotation(1, new PdfRectangle(100, 700, 117, 717), note);

        RedactionFeatureStripper.Apply(doc, RedactionOptions.Default);

        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), note).Should().NotBeEmpty(
            "Standard must not sweep annotations a reviewer can see");
    }

    private static PdfDocument HiddenTextAnnotationDoc(string secret)
    {
        var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        var annot = doc.AddTextAnnotation(1, new PdfRectangle(100, 700, 117, 717), secret);
        annot.RawDictionary.SetInt("F", (int)PdfAnnotationFlags.Hidden);
        return doc;
    }

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
