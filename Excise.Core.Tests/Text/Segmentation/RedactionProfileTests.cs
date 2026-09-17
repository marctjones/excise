using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1586 — the Standard and Maximum output profiles.
/// </summary>
/// <remarks>
/// <para><b>Every removal is tested TWO-SIDED.</b> Each fact asserts the
/// carrier is gone with the knob ON <i>and</i> present with the knob OFF. The
/// second half is the planted failure, kept in the suite rather than done once
/// by hand: without it, a fixture that never carried the payload — or a
/// scanner that cannot see the carrier — would make the first half pass over a
/// document that was never dangerous. That is the #1527 failure mode ("checks
/// that cannot fail"), and it has happened here before.</para>
///
/// <para><b>The reader is not excise's extractor.</b>
/// <see cref="SavedPdfLeakScanner"/> searches the SAVED BYTES including inside
/// Flate streams, so it sees a carrier packed into an object stream that a
/// content-stream assertion cannot. The qpdf/mutool corroboration of the same
/// traps lives in <c>Excise.Rendering.Tests</c>, which is where the reference
/// tools are.</para>
/// </remarks>
public class RedactionProfileTests
{
    private static byte[] Redact(string trapId, RedactionOptions options)
    {
        var trap = CarrierTrapFixtures.Get(trapId);
        using var doc = PdfDocument.Open(trap.Build(true));
        doc.RedactText(trap.Token, options);
        return doc.SaveToBytes();
    }

    private static string Token(string trapId) => CarrierTrapFixtures.Get(trapId).Token;

    /// <summary>
    /// The whole file as text, raw plus every decompressed stream — what a
    /// structural assertion has to read, because excise compresses on save and
    /// packs most dictionaries into object streams.
    /// </summary>
    private static string AllText(byte[] saved) => SavedPdfLeakScanner.AllCarriersText(saved);

    // ── defaults ────────────────────────────────────────────────────────────

    [Fact]
    public void Default_IsTheStandardProfile_OnEveryFlag()
    {
        var o = RedactionOptions.Default;

        o.Profile.Should().Be(RedactionProfile.Standard);
        o.RemoveScripts.Should().BeTrue();
        o.RemoveExternalActions.Should().BeTrue();
        o.RemovePieceInfo.Should().BeTrue();
        o.RemoveThumbnails.Should().BeTrue();
        o.RemoveHiddenLayerContent.Should().BeTrue();
        o.RemoveHiddenAnnotationAppearances.Should().BeTrue();
        o.StripDocumentMetadata.Should().BeTrue();

        // Maximum-only, and off by default: each one destroys accessibility or
        // interactivity, which #1586 makes an explicit choice.
        o.RemoveBookmarks.Should().BeFalse();
        o.RemoveLinkAnnotations.Should().BeFalse();
        o.RemoveMarkupAnnotations.Should().BeFalse();
        o.RemoveFieldNames.Should().BeFalse();
        o.FlattenInteractiveContent.Should().BeFalse();

        RedactionOptions.ForProfile(RedactionProfile.Standard).Should().Be(o);
    }

    [Fact]
    public void Maximum_SetsEveryStandardRemoval_PlusItsOwn()
    {
        var max = RedactionOptions.Maximum;

        max.Profile.Should().Be(RedactionProfile.Maximum);
        // Standard's removals are a SUBSET — Maximum is "everything above, plus".
        max.RemoveScripts.Should().BeTrue();
        max.RemoveExternalActions.Should().BeTrue();
        max.RemovePieceInfo.Should().BeTrue();
        max.RemoveThumbnails.Should().BeTrue();
        max.RemoveHiddenLayerContent.Should().BeTrue();
        max.StripDocumentMetadata.Should().BeTrue();

        max.RemoveBookmarks.Should().BeTrue();
        max.RemoveLinkAnnotations.Should().BeTrue();
        max.RemoveMarkupAnnotations.Should().BeTrue();
        max.RemoveFieldNames.Should().BeTrue();
        max.FlattenInteractiveContent.Should().BeTrue();

        RedactionOptions.ForProfile(RedactionProfile.Maximum).Should().Be(max);
    }

    [Fact]
    public void Maximum_RemovesTheKeptCarriersWhole_NotByTerm()
    {
        var policy = RedactionOptions.Maximum.CarrierPolicy;

        foreach (var kept in new[]
                 {
                     Excise.Core.Operations.RedactionCarriers.Outlines,
                     Excise.Core.Operations.RedactionCarriers.Annotations,
                     Excise.Core.Operations.RedactionCarriers.FormFields,
                     Excise.Core.Operations.RedactionCarriers.StructTree,
                     Excise.Core.Operations.RedactionCarriers.ActionUris,
                 })
        {
            policy.ModeFor(kept).Should().Be(
                Excise.Core.Operations.CarrierScrubMode.RemoveWhole,
                "#1169: cutting a term out of a KNOWN string can reveal it, and " +
                "Maximum is the profile that takes that seriously");
        }

        // NOT RemoveWhole on the carriers the flags delete outright: asking for
        // it there would only produce refusal rows (XFA refuses RemoveWhole by
        // design) about carriers that are already gone.
        policy.ModeFor(Excise.Core.Operations.RedactionCarriers.Xfa)
            .Should().Be(Excise.Core.Operations.CarrierScrubMode.Strip);
    }

    // ── scripts and external actions (#1581) ────────────────────────────────

    [Theory]
    [InlineData("document-javascript")]
    [InlineData("openaction-javascript")]
    [InlineData("field-javascript")]
    [InlineData("field-javascript-stream")]
    [InlineData("nonterminal-field-javascript")]
    public void RemoveScripts_TakesEveryJavaScriptAction_WhereverItLives(string trapId)
    {
        // ON: no JavaScript action survives anywhere in the file.
        AllText(Redact(trapId, RedactionOptions.Default))
            .Should().NotContain("/JavaScript",
                "the reachable-graph walk reaches field and annotation actions, and /JS " +
                "held as a stream, which is what enumerating known locations missed (#1581)");

        // OFF (the planted failure): the action is still there, proving the
        // fixture carries one and the scanner can see it.
        AllText(Redact(trapId, RedactionOptions.Default with { RemoveScripts = false }))
            .Should().Contain("/JavaScript");
    }

    [Fact]
    public void RemoveExternalActions_TakesALaunchTarget_AndKeepsInternalNavigation()
    {
        var token = Token("launch-action");

        var stripped = Redact("launch-action", RedactionOptions.Default);
        SavedPdfLeakScanner.FindTerm(stripped, token).Should().BeEmpty(
            "the /Launch file target names something outside the document, and the name " +
            "IS the leak — a stripped filename still reveals its length and shape (#1581)");
        AllText(stripped).Should().NotContain("/Launch");

        var kept = Redact("launch-action", RedactionOptions.Default with { RemoveExternalActions = false });
        AllText(kept).Should().Contain("/Launch",
            "planted failure: with the knob off the action survives, so the fixture and " +
            "the scanner both work");

        // Internal navigation is deliberately untouched — a redaction that
        // broke every page link would be unusable, and a /GoTo names nothing
        // outside the file.
        var outlineTrap = CarrierTrapFixtures.Get("outline-title");
        using var doc = PdfDocument.Open(outlineTrap.Build(true));
        doc.RedactText(outlineTrap.Token, RedactionOptions.Default);
        AllText(doc.SaveToBytes()).Should().Contain("/Dest",
            "an internal destination is navigation, not an external effect");
    }

    // ── /PieceInfo, /Thumb (#1583, #1586) ───────────────────────────────────

    [Fact]
    public void RemovePieceInfo_TakesTheProducersPrivateDictionary()
    {
        var token = Token("pieceinfo");

        SavedPdfLeakScanner.FindTerm(Redact("pieceinfo", RedactionOptions.Default), token)
            .Should().BeEmpty("#1583: /PieceInfo /Private has no schema, so there is nothing " +
                              "to scrub selectively — it goes whole");

        var kept = Redact("pieceinfo", RedactionOptions.Default with { RemovePieceInfo = false });
        SavedPdfLeakScanner.FindTerm(kept, token).Should().NotBeEmpty(
            "planted failure: /PieceInfo is the only carrier in this trap, and nothing else " +
            "in the pipeline reads it");
        AllText(kept).Should().Contain("/PieceInfo");
    }

    [Fact]
    public void RemoveThumbnails_TakesThePreRedactionPictureOfThePage()
    {
        var trap = CarrierTrapFixtures.Get("page-thumbnail");

        using (var doc = PdfDocument.Open(trap.Build(true)))
        {
            doc.RedactText(trap.Token, RedactionOptions.Default);
            AllText(doc.SaveToBytes()).Should().NotContain("/Thumb",
                "a thumbnail is a rendered picture of the page BEFORE the redaction and " +
                "nothing regenerates it");
        }

        using (var doc = PdfDocument.Open(trap.Build(true)))
        {
            doc.RedactText(trap.Token, RedactionOptions.Default with { RemoveThumbnails = false });
            AllText(doc.SaveToBytes()).Should().Contain("/Thumb", "planted failure");
        }
    }

    // ── hidden optional content ─────────────────────────────────────────────

    [Fact]
    public void RemoveHiddenLayerContent_TakesContentInAnOffLayer_AndTheGroupItself()
    {
        var token = Token("ocg-hidden");

        var stripped = Redact("ocg-hidden", RedactionOptions.Default);
        SavedPdfLeakScanner.FindTerm(stripped, token).Should().BeEmpty();
        AllText(stripped).Should().NotContain("/OCProperties",
            "the group's /Name is itself a carrier — a layer called \"Confidential draft — " +
            "Quillfeather\" names what was on it — and a group whose content is gone " +
            "describes nothing");

        // OFF: the span survives. Note the term scrub cannot reach content-stream
        // text, so this is the knob's own effect and nothing else's.
        var kept = Redact("ocg-hidden", RedactionOptions.Default with { RemoveHiddenLayerContent = false });
        AllText(kept).Should().Contain("/OCProperties", "planted failure");
    }

    [Fact]
    public void RemoveHiddenLayerContent_IsGatedOnIncludeHiddenLayers()
    {
        // A caller who said "do not reach into hidden layers" must not get them
        // DELETED instead — that is the opposite of the request, and worse than
        // either answer on its own.
        var kept = Redact("ocg-hidden", RedactionOptions.Default with { IncludeHiddenLayers = false });

        AllText(kept).Should().Contain("/OCProperties",
            "IncludeHiddenLayers: false puts hidden layers out of scope entirely");
    }

    // ── hidden annotation appearances (#1581) ───────────────────────────────

    [Fact]
    public void RemoveHiddenAnnotationAppearances_TakesTheAppearanceNothingPaints()
    {
        var token = Token("widget-appearance");

        SavedPdfLeakScanner.FindTerm(Redact("widget-appearance", RedactionOptions.Default), token)
            .Should().BeEmpty("#1581: the widget is flagged /F 2 (Hidden), so its appearance " +
                              "is never painted — the text has no reader and every reviewer " +
                              "who checked the page has not seen it");

        var kept = Redact("widget-appearance",
            RedactionOptions.Default with { RemoveHiddenAnnotationAppearances = false });
        SavedPdfLeakScanner.FindTerm(kept, token).Should().NotBeEmpty(
            "planted failure: this is the measured #1581 leak, and with the knob off it " +
            "reproduces exactly");
    }

    // ── document metadata (#1583) ───────────────────────────────────────────

    [Fact]
    public void StripDocumentMetadata_TakesACustomInfoKey_WhichNoKeyListCanKnow()
    {
        var token = Token("info-custom-key");

        SavedPdfLeakScanner.FindTerm(Redact("info-custom-key", RedactionOptions.Default), token)
            .Should().BeEmpty("#1583: §14.3.3 allows an Info dictionary \"any other key\", so " +
                              "a targeted scrub has to know names a producer is free to invent");

        SavedPdfLeakScanner.FindTerm(
                Redact("info-custom-key", RedactionOptions.Default with { StripDocumentMetadata = false }),
                token)
            .Should().NotBeEmpty("planted failure: /CaseName is below no floor and matches no " +
                                 "standard key, so only the wholesale strip removes it");
    }

    [Fact]
    public void StripDocumentMetadata_IsReported_NotSilent()
    {
        var trap = CarrierTrapFixtures.Get("info-custom-key");
        using var doc = PdfDocument.Open(trap.Build(true));

        var report = doc.RedactText(trap.Token, RedactionOptions.Default);

        report.Removals.Should().Contain(r => r.Feature.Contains("/Info"),
            "a removal made on the user's behalf WITHOUT a term match is destruction they " +
            "are entitled to know about — the only thing between it and \"the tool mangled " +
            "my document\" is the report");
        report.Profile.Should().Be(RedactionProfile.Standard);
        report.AccessibilityAndInteractivityRemoved.Should().BeFalse(
            "Standard keeps the accessibility and navigation carriers");
        report.ToString().Should().Contain("/Info");
    }

    // ── the kept carriers survive Standard ──────────────────────────────────

    [Fact]
    public void Standard_KeepsTheAccessibilityAndNavigationCarriers()
    {
        // The whole point of having two profiles: Standard must not be a
        // scorched-earth pass wearing a default's clothes. A bookmark whose
        // title does NOT hold the term keeps its title.
        var trap = CarrierTrapFixtures.Get("outline-title");
        using var doc = PdfDocument.Open(trap.Build(true));
        doc.RedactText("SOMETHINGELSEENTIRELY", RedactionOptions.Default);
        var text = AllText(doc.SaveToBytes());

        text.Should().Contain("/Outlines", "bookmarks are navigation and Standard keeps them");
        text.Should().Contain("Chapter on", "an unrelated title is not touched");
    }

    [Fact]
    public void Maximum_SaysTheOutputIsNoLongerAccessibleOrInteractive()
    {
        var trap = CarrierTrapFixtures.Get("outline-title");
        using var doc = PdfDocument.Open(trap.Build(true));

        var report = doc.RedactText(trap.Token, RedactionOptions.Maximum);

        report.Profile.Should().Be(RedactionProfile.Maximum);
        report.AccessibilityAndInteractivityRemoved.Should().BeTrue();
        report.ToString().Should().Contain("NO LONGER accessible or interactive");
        AllText(doc.SaveToBytes()).Should().NotContain("/Outlines");
    }

    [Fact]
    public void Maximum_RemovesLinkAndMarkupAnnotations_StandardDoesNot()
    {
        foreach (var (trapId, marker) in new[]
                 {
                     ("link-uri", "/Link"),
                     ("annotation-author", "/Text"),
                     ("annotation-subj", "/Highlight"),
                 })
        {
            var trap = CarrierTrapFixtures.Get(trapId);

            using (var doc = PdfDocument.Open(trap.Build(true)))
            {
                doc.RedactText(trap.Token, RedactionOptions.Default);
                AllText(doc.SaveToBytes()).Should().Contain(marker,
                    $"Standard keeps the {marker} annotation and scrubs its text");
            }

            using (var doc = PdfDocument.Open(trap.Build(true)))
            {
                doc.RedactText(trap.Token, RedactionOptions.Maximum);
                AllText(doc.SaveToBytes()).Should().NotContain(marker,
                    $"Maximum removes the {marker} annotation outright");
            }
        }
    }

    [Fact]
    public void Maximum_RemovesFieldNames_IncludingOnANonTerminalNode()
    {
        // §12.7.3.2: a non-terminal field node has /T and /Kids, no /FT and no
        // /Subtype /Widget. There are 6 in irs-w4 and 30 in irs-1040, and a
        // type-marker walk misses every one — so this walks the FIELD TREE.
        var trap = CarrierTrapFixtures.Get("nonterminal-field-javascript");

        using (var doc = PdfDocument.Open(trap.Build(true)))
        {
            doc.RedactText(trap.Token, RedactionOptions.Default);
            AllText(doc.SaveToBytes()).Should().Contain("(parent)",
                "Standard keeps field names: they are a screen reader's labels");
        }

        using (var doc = PdfDocument.Open(trap.Build(true)))
        {
            var report = doc.RedactText(trap.Token, RedactionOptions.Maximum);
            AllText(doc.SaveToBytes()).Should().NotContain("(parent)");
            report.Removals.Should().Contain(r => r.Feature.Contains("field name"));
        }
    }

    // ── the report is the contract ──────────────────────────────────────────

    [Fact]
    public void EveryRemovalRow_NamesSomethingTheDocumentActuallyHad()
    {
        // A row for a removal that removed nothing trains people to ignore the
        // rows. The clean control has no scripts, no /PieceInfo, no thumbnail
        // and no hidden layer — it must produce no rows for those.
        using var doc = PdfDocument.Open(CarrierTrapFixtures.Clean());

        var report = doc.RedactText("NOTHINGHERE", RedactionOptions.Default);

        report.Removals.Should().OnlyContain(r => r.Count > 0);
        report.Removals.Should().NotContain(r => r.Feature.Contains("JavaScript"));
        report.Removals.Should().NotContain(r => r.Feature.Contains("thumbnail"));
        report.Removals.Should().NotContain(r => r.Feature.Contains("optional-content"));
        // The clean control DOES carry tool-written /Info and XMP, so that one
        // row is expected — and is the proof this test is not vacuous.
        report.Removals.Should().Contain(r => r.Feature.Contains("/Info"));
    }

    [Fact]
    public void AreaRedaction_ReportsTheSameRemovals_AsTheTermPath()
    {
        // #896's lesson: a guarantee re-established by every front end holds
        // until someone writes a new one. The profile is applied by ONE
        // stripper called from both entry points, and the area path — which had
        // no return channel at all before #1586 — now reports it.
        var trap = CarrierTrapFixtures.Get("pieceinfo");
        using var doc = PdfDocument.Open(trap.Build(true));

        var report = doc.GetPage(1).RedactAreaWithReport(
            new PdfRectangle(60, 670, 400, 700), RedactionOptions.Default);

        report.Removals.Should().Contain(r => r.Feature.Contains("/PieceInfo"));
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), trap.Token).Should().BeEmpty();
    }

    // ── the two new traps (#1586) ───────────────────────────────────────────

    /// <summary>
    /// A Figure whose <c>/Alt</c> describes an image the AREA path blacked out,
    /// with no <c>/MCID</c> link between element and content.
    /// </summary>
    /// <remarks>
    /// <para><b>What was suspected, and what is true.</b>
    /// <c>StructureTreeRedactionScrubber</c> has two passes and both are blind
    /// here by construction: pass 1 needs a structural (<c>/MCID</c> or
    /// <c>/OBJR</c>) link to the redaction area, and pass 2 content-matches the
    /// carrier against text the glyph pass REMOVED — an image redaction removes
    /// no text, so there is nothing to compare against.</para>
    /// <para><b>The fix is a REPORT, not a strip.</b> Silently deleting the
    /// <c>/Alt</c> of every Figure near a redaction would guess: an image can be
    /// blacked out in one corner and correctly described by its alternate text
    /// everywhere else, and an <c>/Alt</c> is the only thing a blind reader
    /// gets. So Standard surfaces it and Maximum removes it — the decided
    /// carrier policy, applied to a matcher gap (compare
    /// <see cref="HyphenatedTermCandidate"/>, which is the same shape).</para>
    /// </remarks>
    [Fact]
    public void FigureAltOverARedactedImage_WithNoMcidLink_IsRemovedByMaximum()
    {
        var trap = CarrierTrapFixtures.Get("figure-alt-image-no-mcid");

        // The image sits at 72,500 200x50, so this box covers it. The AREA
        // blocks below build the trap WITHOUT the visible page text: an area
        // redaction is asked to remove one region, and page text elsewhere
        // surviving is correct, not a leak. That would mask the carrier.
        var box = new PdfRectangle(70, 495, 275, 555);

        // Standard, area path: the /Alt survives — AND THE REPORT SAYS SO.
        // The survival alone is the boundary of what the structure-tree
        // scrubber can know; the refusal row is what makes that boundary
        // visible instead of a silent clean verdict.
        using (var doc = PdfDocument.Open(trap.Build(false)))
        {
            var report = doc.GetPage(1).RedactAreaWithReport(box, RedactionOptions.Default);

            report.Carriers.Should().Contain(
                c => c.Carrier == "structure-tree /Alt" && !c.Scrubbed
                     && c.RefusedReason!.Contains("no content link"),
                "an /Alt describing a blacked-out image that we could NOT check must be " +
                "raised, not passed over");
            report.IsCleanSuccess.Should().BeFalse(
                "this is the whole point: a confirmed carrier leak may never read as a " +
                "clean redaction (#1527 — a check that cannot fail is worse than none)");
            SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), trap.Token).Should().NotBeEmpty(
                "no /MCID link and no removed text to content-match against — see this " +
                "test's remarks for why the answer is a report rather than a guess");
        }

        // Maximum, area path: RemoveWhole takes the value and reports the
        // removal, so the same document comes out with nothing to raise.
        using (var doc = PdfDocument.Open(trap.Build(false)))
        {
            var report = doc.GetPage(1).RedactAreaWithReport(box, RedactionOptions.Maximum);

            report.Removals.Should().Contain(
                r => r.Feature.Contains("unlinked alternate-text"),
                "Maximum drops the value it cannot check, and says it did");
            report.Carriers.Should().NotContain(c => c.Carrier == "structure-tree /Alt",
                "nothing is left to refuse once the value is gone");
            SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), trap.Token).Should().BeEmpty();
        }

        // Maximum, term path: the same value, reached by the RemoveWhole
        // carrier scrub rather than by the image-triggered sweep.
        using (var doc = PdfDocument.Open(trap.Build(true)))
        {
            doc.RedactText(trap.Token, RedactionOptions.Maximum);
            SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), trap.Token).Should().BeEmpty(
                "Maximum removes the whole /Alt value rather than cutting the term out");
        }
    }

    /// <summary>
    /// A hidden <c>/OC</c> span inside a VISIBLE form XObject (#1586).
    /// </summary>
    /// <remarks>
    /// <para><b>What this settles.</b> The hidden-layer pass walked the PAGE
    /// content stream and, for a <c>Do</c>, looked only at whether the XObJECT
    /// itself carried a hidden <c>/OC</c>. A visible form holding
    /// <c>/OC /MC0 BDC … EMC</c> was untouched — and the report still said
    /// "hidden optional-content span(s) removed", which is the failure mode
    /// this project treats as worse than removing nothing: a stated guarantee
    /// that holds one level deep.</para>
    /// <para>Two-sided: the knob off leaves it, which is also the #1170 rule
    /// (a caller who asked NOT to reach into hidden layers must not have them
    /// DELETED instead).</para>
    /// </remarks>
    [Fact]
    public void AHiddenLayerInsideAVisibleForm_IsRemovedToo()
    {
        var trap = CarrierTrapFixtures.Get("ocg-hidden-in-form");

        using (var doc = PdfDocument.Open(trap.Build(false)))
        {
            var report = doc.RedactText("UNRELATED", RedactionOptions.Default);
            report.Removals.Should().Contain(
                r => r.Feature.Contains("hidden optional-content span"),
                "one level down is still inside the document");
            SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), trap.Token).Should().BeEmpty(
                "the span's text is gone from the form's stream, not just from the page's");
        }

        // Knob off: the layer stays. Nothing is deleted behind the back of a
        // caller who said not to reach into hidden content.
        using (var doc = PdfDocument.Open(trap.Build(false)))
        {
            doc.RedactText("UNRELATED",
                RedactionOptions.Default with { RemoveHiddenLayerContent = false });
            SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), trap.Token).Should().NotBeEmpty();
        }
    }

    /// <summary>
    /// A one- or two-character redaction in a structure element with no
    /// structural link: below <c>StructureTreeRedactionScrubber.MinMatchLength</c>
    /// and below <c>PdfDocumentSanitizer</c>'s term floor, both 3.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The floor is not lowered, deliberately.</b> One- and two-character
    /// fragments ("a", "of") match almost any alternate description, so
    /// content-matching on them turns the structure-tree pass into "delete the
    /// structure tree" — the exact destruction #631 and #942 are about. The
    /// term is REPORTED instead: <c>RedactionReport.Carriers</c> names the
    /// floor and <c>IsCleanSuccess</c> is false, so a reviewer is told rather
    /// than reassured.
    /// </remarks>
    [Fact]
    public void AShortTermInAnUnlinkedStructureElement_IsReported_NotSilentlyLeft()
    {
        var trap = CarrierTrapFixtures.Get("structure-alt-short-term");
        trap.Token.Length.Should().BeLessThan(3, "the trap only means anything below the floor");

        using var doc = PdfDocument.Open(trap.Build(true));
        var report = doc.RedactText(trap.Token, RedactionOptions.Default);

        report.IsCleanSuccess.Should().BeFalse(
            "a run that left the term in a carrier it could not act on has not finished " +
            "until a human reads the report");
        report.Carriers.Should().Contain(
            c => c.RefusedReason != null && c.RefusedReason.Contains("floor"),
            "the reason names the floor, so the reviewer knows what to do about it");
    }

    [Fact]
    public void AShortTerm_IsRemovedByMaximum_WhichHasNoTermToFloor()
    {
        // Maximum's RemoveWhole on the structure tree does not need to match a
        // 2-character fragment: it drops the value. This is the escape hatch the
        // floor leaves open, and it is why the floor is not a dead end.
        var trap = CarrierTrapFixtures.Get("structure-alt-short-term");
        using var doc = PdfDocument.Open(trap.Build(true));

        doc.RedactText(trap.Token, RedactionOptions.Maximum);

        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), $"Exhibit {trap.Token} summary")
            .Should().BeEmpty();
    }
}
