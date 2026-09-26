using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Tests.Redaction.Recovery;
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

        // The flatten is off so the name strip's own reach is what runs: with it
        // on, the form is flattened first and its field tree is gone before any
        // name is visited (#1857).
        using (var doc = PdfDocument.Open(trap.Build(true)))
        {
            var report = doc.RedactText(trap.Token,
                RedactionOptions.Maximum with { FlattenInteractiveContent = false });
            AllText(doc.SaveToBytes()).Should().NotContain("(parent)");
            report.Removals.Should().Contain(r => r.Feature.Contains("field name"));
        }
    }

    // ── forms under Maximum (#1857) ─────────────────────────────────────────

    public enum EntryPoint { RedactText, RedactArea, SafetyPass }

    private const string Flattened = "interactive object(s) flattened into the page";

    /// <summary>
    /// One entry point over <paramref name="input"/>, redacting nothing that is
    /// in it: Maximum's flatten is a promise about every form, not about the
    /// fields that happen to hold the term.
    /// </summary>
    private static (byte[] Saved, IReadOnlyList<RedactedFeatureRemoval> Removals, IReadOnlyList<string> Refusals)
        RunProfile(byte[] input, EntryPoint entry, RedactionOptions options)
    {
        using var doc = PdfDocument.Open(input);
        switch (entry)
        {
            case EntryPoint.RedactText:
            {
                var report = doc.RedactText("NOMATCHXYZ", options);
                return (doc.SaveToBytes(), report.Removals,
                    report.Carriers.Where(c => c.RefusedReason != null).Select(c => $"{c.Carrier}: {c.RefusedReason}").ToList());
            }
            case EntryPoint.RedactArea:
            {
                var report = doc.GetPage(1).RedactAreaWithReport(new PdfRectangle(400, 100, 500, 120), options);
                return (doc.SaveToBytes(), report.Removals,
                    report.Carriers.Where(c => c.RefusedReason != null).Select(c => $"{c.Carrier}: {c.RefusedReason}").ToList());
            }
            default:
            {
                var report = RedactedCopySafetyPolicy.Evaluate(doc,
                    RedactedCopySafetyRequest.ForAreas(Array.Empty<RedactedCopySafetyArea>(), options));
                return (doc.SaveToBytes(), report.Removals, report.Warnings);
            }
        }
    }

    public static TheoryData<string, EntryPoint> FormTraps()
    {
        var data = new TheoryData<string, EntryPoint>();
        foreach (var id in new[] { "acroform-all-carriers", "acroform-dv", "acroform-rv", "widget-mk-ca" })
            foreach (var entry in Enum.GetValues<EntryPoint>())
                data.Add(id, entry);
        return data;
    }

    /// <summary>The field-dictionary tokens of a trap, each naming its carrier.</summary>
    private static string[] FieldOnlyTokens(string trapId)
    {
        var t = Token(trapId);
        return trapId == "acroform-all-carriers"
            ? new[] { t + "DEFAULT", t + "RICH", t + "CAPTION", t + "TOOLTIP" }
            : new[] { t };
    }

    [Theory]
    [MemberData(nameof(FormTraps))]
    public void Maximum_FlattensTheForm_SoNoFieldCarrierSurvives_StandardKeepsThem(string trapId, EntryPoint entry)
    {
        var input = CarrierTrapFixtures.Get(trapId).Build(false);

        // Standard, the planted failure: the form is kept, so every carrier is
        // still in the file and the scanner can see it.
        var standard = RunProfile(input, entry, RedactionOptions.Default);
        foreach (var token in FieldOnlyTokens(trapId))
            SavedPdfLeakScanner.FindTerm(standard.Saved, token).Should().NotBeEmpty(
                $"{token}: Standard keeps the form, and the fixture must carry what Maximum is to remove");

        var max = RunProfile(input, entry, RedactionOptions.Maximum);
        foreach (var token in FieldOnlyTokens(trapId))
            SavedPdfLeakScanner.FindTerm(max.Saved, token).Should().BeEmpty(
                $"{token}: Maximum flattens the form, so the field dictionary goes with it. #1857: the name " +
                "strip ran first, the form parser skipped every field without a /T, and nothing was flattened");
        max.Removals.Should().Contain(r => r.Feature == Flattened && r.Count == 1,
            "the flatten is a removal made without a term match, so the report must name it");
        max.Removals.Should().NotContain(r => r.Feature == "form widget(s) removed without being painted",
            "every widget of this form is one the flatten can paint");
        max.Refusals.Should().NotContain(r => r.Contains("could not be flattened"));

        using var reopened = PdfDocument.Open(max.Saved);
        reopened.GetAcroForm().Should().BeNull(
            "/AcroForm /Fields would keep every field dictionary reachable, and the writer ships what is reachable");
        reopened.GetPage(1).GetAnnotations().Should().BeEmpty("no widget survives the flatten");
    }

    [Fact]
    public void Maximum_PaintsTheFieldValueIntoThePage()
    {
        var trap = CarrierTrapFixtures.Get("acroform-all-carriers");
        var max = RunProfile(trap.Build(false), EntryPoint.RedactText, RedactionOptions.Maximum);

        using var reopened = PdfDocument.Open(max.Saved);
        reopened.GetPage(1).Text.Should().Contain(trap.Token + "VALUE",
            "flattening makes the value page content, which is what the reader saw; mutool corroborates this " +
            "in RedactionProfileFormFlattenOracleTests");

        // A scan of the saved bytes does not depend on excise's reader: the painted value is in a content stream.
        SavedPdfLeakScanner.FindTerm(max.Saved, trap.Token + "VALUE").Should().NotBeEmpty(
            "the flattened value is painted into the page content, found by the saved-bytes scanner");
    }

    /// <summary>
    /// A widget the flattener cannot read — a merged field/widget on the page
    /// that <c>/AcroForm /Fields</c> does not list — is not flattened. Once
    /// <c>/AcroForm</c> is gone the carrier scrub cannot reach it either, so
    /// Maximum removes it and says so.
    /// </summary>
    [Theory]
    [InlineData(EntryPoint.RedactText)]
    [InlineData(EntryPoint.RedactArea)]
    [InlineData(EntryPoint.SafetyPass)]
    public void Maximum_RemovesAWidgetTheFlattenCannotRead_AndReportsIt(EntryPoint entry)
    {
        const string secret = "UNLISTEDFIELDTRAP";
        byte[] input;
        using (var doc = PdfDocument.Open(CarrierTrapFixtures.Field(null, $"/FT /Tx /T (name) /V ({secret})")))
        {
            ((Excise.Core.Primitives.PdfDictionary)doc.Catalog.GetOptional("AcroForm")!)["Fields"] =
                new Excise.Core.Primitives.PdfArray();
            input = doc.SaveToBytes();
        }

        SavedPdfLeakScanner.FindTerm(RunProfile(input, entry, RedactionOptions.Default).Saved, secret)
            .Should().NotBeEmpty("planted failure: Standard keeps the field");

        var max = RunProfile(input, entry, RedactionOptions.Maximum);

        SavedPdfLeakScanner.FindTerm(max.Saved, secret).Should().BeEmpty();
        max.Removals.Should().Contain(r => r.Feature == "form widget(s) removed without being painted" && r.Count == 1,
            "CLAUDE.md rule 6: every removal is reported, and this one is not a flatten");
        max.Removals.Should().NotContain(r => r.Feature == Flattened, "nothing was flattened");
    }

    /// <summary>
    /// #1864: a field with no <c>/T</c> (optional in ISO 32000-1) is a field.
    /// The form parser skipped it, so text extraction never emitted it and the
    /// term scrub never rewrote its appearance: under Standard the term that
    /// appearance draws survived the redaction (measured). Maximum removed the
    /// widget unpainted instead of flattening it like any other field.
    /// </summary>
    [Fact]
    public void ANamelessField_IsReadByBothProfiles_AndItsTermLeavesTheFile()
    {
        const string secret = "NAMELESSFIELDTRAP";
        var input = CarrierTrapFixtures.Field(null, $"/FT /Tx /V (Keep {secret} here) /AP << /N 7 0 R >>",
            CarrierTrapFixtures.AppearanceStream($"BT /F1 10 Tf 2 4 Td (Keep {secret} here) Tj ET", compress: true));

        foreach (var options in new[] { RedactionOptions.Default, RedactionOptions.Maximum })
        {
            using var doc = PdfDocument.Open(input);
            var report = doc.RedactText(secret, options);
            var saved = doc.SaveToBytes();

            SavedPdfLeakScanner.FindTerm(saved, secret).Should().BeEmpty(
                $"{options.Profile}: the term was only in a field with no /T, in its /V and its appearance");
            if (options.Profile != RedactionProfile.Maximum) continue;

            report.Removals.Should().Contain(r => r.Feature == Flattened && r.Count == 1,
                "a nameless field is flattened like any other field");
            report.Removals.Should().NotContain(r => r.Feature == "form widget(s) removed without being painted");
            using var reopened = PdfDocument.Open(saved);
            reopened.GetPage(1).Text.Should().Contain("Keep").And.Contain("here",
                "the value around the term is painted into the page, as for a named field");
        }
    }

    /// <summary>
    /// #1864: the same nameless field on the page but missing from
    /// <c>/AcroForm /Fields</c>. Only the orphaned-widget recovery reaches it,
    /// and it too skipped a widget without <c>/T</c>.
    /// </summary>
    [Fact]
    public void ANamelessWidgetMissingFromFields_IsRedactedUnderStandard()
    {
        const string secret = "NAMELESSORPHANTRAP";
        byte[] input;
        using (var doc = PdfDocument.Open(CarrierTrapFixtures.Field(null,
            $"/FT /Tx /V (Keep {secret} here) /AP << /N 7 0 R >>",
            CarrierTrapFixtures.AppearanceStream($"BT /F1 10 Tf 2 4 Td (Keep {secret} here) Tj ET", compress: true))))
        {
            ((Excise.Core.Primitives.PdfDictionary)doc.Catalog.GetOptional("AcroForm")!)["Fields"] =
                new Excise.Core.Primitives.PdfArray();
            input = doc.SaveToBytes();
        }

        using var redacted = PdfDocument.Open(input);
        redacted.RedactText(secret, RedactionOptions.Default);

        SavedPdfLeakScanner.FindTerm(redacted.SaveToBytes(), secret).Should().BeEmpty(
            "the term was only in a widget no /Fields entry reaches, in its /V and its appearance");
    }

    /// <summary>
    /// A form the flattener throws on (here: a page content stream no filter
    /// decodes, so there is nothing to paint into) keeps <c>/AcroForm</c>.
    /// Maximum must refuse loudly, never report a silent zero.
    /// </summary>
    [Fact]
    public void Maximum_RefusesLoudly_WhenTheFormCannotBeFlattened()
    {
        const string secret = "UNFLATTENABLETRAP";
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank();
        doc.AddTextField(1, new PdfRectangle(100, 600, 300, 620), "name").SetValue(secret);
        doc.GetPage(1).Dictionary["Contents"] = new Excise.Core.Primitives.PdfStream(
            new Excise.Core.Primitives.PdfDictionary { ["Filter"] = new Excise.Core.Primitives.PdfName("NoSuchDecode") },
            new byte[] { 1, 2, 3 });

        var report = RedactedCopySafetyPolicy.Evaluate(doc,
            RedactedCopySafetyRequest.ForAreas(Array.Empty<RedactedCopySafetyArea>(), RedactionOptions.Maximum));

        report.Warnings.Should().Contain(w => w.Contains("/AcroForm") && w.Contains("could not be flattened"),
            "CLAUDE.md rule 6: a carrier the engine could not remove is reported, never skipped");
        report.Removals.Should().NotContain(r => r.Feature == Flattened);
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), secret).Should().NotBeEmpty(
            "the refusal is only honest if the value really is still there");
    }

    /// <summary>
    /// The flattener paints values, and a signature has none to paint: it
    /// keeps the widget. Maximum removes it and says so — a redacted copy
    /// cannot carry a valid signature over bytes that changed, and the
    /// signature dictionary names the signer.
    /// </summary>
    [Theory]
    [InlineData(EntryPoint.RedactText)]
    [InlineData(EntryPoint.RedactArea)]
    [InlineData(EntryPoint.SafetyPass)]
    public void Maximum_RemovesASignatureField_AndReportsIt(EntryPoint entry)
    {
        const string signer = "SIGNERNAMETRAP";
        var input = CarrierTrapFixtures.Field(null, "/FT /Sig /T (Signature1) /V 7 0 R",
            $"<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /Name ({signer}) "
            + "/Reason (Approval) /Contents <00> /ByteRange [0 0 0 0] >>");

        SavedPdfLeakScanner.FindTerm(RunProfile(input, entry, RedactionOptions.Default).Saved, signer)
            .Should().NotBeEmpty("planted failure: Standard keeps the signature field");

        var max = RunProfile(input, entry, RedactionOptions.Maximum);

        SavedPdfLeakScanner.FindTerm(max.Saved, signer).Should().BeEmpty();
        max.Removals.Should().Contain(r => r.Feature == "signature field(s) removed" && r.Count == 1);
        max.Refusals.Should().NotContain(r => r.Contains("could not be flattened"));
        using var reopened = PdfDocument.Open(max.Saved);
        reopened.GetPage(1).GetAnnotations().Should().BeEmpty();
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
    /// The same refusal, on the GUI's shape of the call (#1586).
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is a separate test.</b>
    /// <c>Excise.App/Services/RedactionService.RedactArea</c> calls the
    /// <c>void</c> <c>page.RedactArea(rect, options)</c> overload and then
    /// builds the user-facing dialog from
    /// <c>RedactedCopySafetyPolicy.Evaluate</c>. The engine computed the
    /// carrier refusal and the return value went on the floor, so the dialog
    /// said clean over a carrier we KNOW we could not check. A report nobody
    /// receives is not one — the same defect
    /// <c>PdfDocumentRedactionLedger</c> was created for (#1572), and the
    /// reason the count is recorded there rather than only returned.</para>
    /// </remarks>
    [Fact]
    public void TheUncheckableAltRefusal_ReachesTheSafetyReport_OnTheGuiShapedPath()
    {
        var trap = CarrierTrapFixtures.Get("figure-alt-image-no-mcid");
        var area = new PdfRectangle(70, 495, 275, 555);
        using var doc = PdfDocument.Open(trap.Build(false));

        // Exactly what the GUI does: the void overload, report discarded.
        doc.GetPage(1).RedactArea(area, RedactionOptions.Default);
        var report = RedactedCopySafetyPolicy.Evaluate(doc,
            RedactedCopySafetyRequest.ForAreas(
                new[] { new RedactedCopySafetyArea(1, PdfPageRect.FromContentPoints(1, area)) },
                RedactionOptions.Default with { KeepAttachments = true }));

        report.Warnings.Should().Contain(
            w => w.Contains("structure-tree /Alt") && w.Contains("could NOT be checked"),
            "the dialog the user actually reads must carry the refusal, not just the " +
            "RedactionReport the GUI throws away");
        report.HasWarnings.Should().BeTrue();
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
    /// #1866: a form excise cannot decode, under a hidden layer. Drawn only
    /// there, it goes with the layer (#1868): removing it needs no decode.
    /// Drawn visibly too, it stays, and the hidden-layer pass, which cannot
    /// look inside it for hidden spans of its own, says so.
    /// </summary>
    [Theory]
    [InlineData("RedactText", false)]
    [InlineData("RedactArea", false)]
    [InlineData("RedactText", true)]
    [InlineData("RedactArea", true)]
    public void AnUndecodableFormInAHiddenLayer_IsRemovedWithIt_OrKeptAndReported(string entry, bool drawnVisiblyToo)
    {
        const string token = "HIDDENUNDECODABLETRAP";
        using var doc = PdfDocument.Open(
            RecoveryFixtureBuilder.FormInHiddenLayer(token, drawnVisiblyToo, "/Filter /Nonexistent"));

        var report = entry == "RedactText"
            ? doc.RedactText("VISIBLE", RedactionOptions.Default)
            : doc.GetPage(1).RedactAreaWithReport(new PdfRectangle(60, 690, 200, 720), RedactionOptions.Default);

        report.Removals.Should().Contain(r => r.Feature.Contains("hidden optional-content span"));
        var saved = doc.SaveToBytes();
        if (!drawnVisiblyToo)
        {
            report.Carriers.Should().NotContain(c => c.Carrier.StartsWith("form XObject 8 0 R"));
            SavedPdfLeakScanner.FindTerm(saved, token).Should().BeEmpty("nothing draws the form any more");
            return;
        }

        report.Carriers.Should().ContainSingle(c => c.Carrier == "form XObject 8 0 R")
            .Which.Should().Match<CarrierResult>(c => !c.Scrubbed
                && c.RefusedReason!.Contains("/Filter /Nonexistent could not be decoded")
                && c.RefusedReason.Contains("hidden-layer pass"));
        report.IsCleanSuccess.Should().BeFalse("a form the hidden-layer pass could not read was left in place");
        SavedPdfLeakScanner.FindTerm(saved, token).Should().NotBeEmpty(
            "the form is kept and reported, never stripped or skipped in silence");
    }

    /// <summary>
    /// #1868: a decodable form drawn only inside an OFF layer. The hidden-layer
    /// pass drops the span and its <c>Do</c>; the form was left in the page's
    /// <c>/XObject</c>, where no text walk reaches it, and the run reported clean.
    /// </summary>
    [Theory]
    [InlineData("VISIBLE", false)]
    [InlineData(HiddenFormToken, false)]
    [InlineData("area", false)]
    [InlineData("VISIBLE", true)]
    [InlineData(HiddenFormToken, true)]
    [InlineData("area", true)]
    public void AFormDrawnOnlyInAHiddenLayer_LeavesTheFileWithIt(string entry, bool maximum)
    {
        var options = maximum ? RedactionOptions.Maximum : RedactionOptions.Default;
        using var doc = PdfDocument.Open(RecoveryFixtureBuilder.FormInHiddenLayer(HiddenFormToken));

        var report = entry == "area"
            ? doc.GetPage(1).RedactAreaWithReport(new PdfRectangle(60, 690, 200, 720), options)
            : doc.RedactText(entry, options);

        report.Removals.Should().Contain(r => r.Feature.Contains("hidden optional-content span"));
        SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), HiddenFormToken).Should().BeEmpty(
            "the only Do of the form was in the removed span, so the form goes with it");
        report.Carriers.Should().NotContain(c => c.Carrier.StartsWith("form XObject 8 0 R"));
        report.IsCleanSuccess.Should().BeTrue();
        // The area path's flattener inlines and frees the form before this pass runs.
        if (entry != "area")
            report.Removals.Should().ContainSingle(r => r.Feature == "form XObject(s) drawn only in hidden optional content")
                .Which.Count.Should().Be(1, "every removal is reported");
    }

    /// <summary>
    /// #1868, the other side: a form drawn in the OFF layer AND visibly is
    /// still drawn, so it stays, and its text is the normal text walk's.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFormAlsoDrawnVisibly_IsKept_AndItsTextIsTheTextWalks(bool maximum)
    {
        var options = maximum ? RedactionOptions.Maximum : RedactionOptions.Default;
        var input = RecoveryFixtureBuilder.FormInHiddenLayer(HiddenFormToken, drawnVisiblyToo: true);

        using (var doc = PdfDocument.Open(input))
        {
            var report = doc.RedactText("VISIBLE", options);
            SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), HiddenFormToken).Should().NotBeEmpty(
                "the form is still drawn outside the layer: pruning it would delete visible content");
            report.IsCleanSuccess.Should().BeTrue();
        }

        using (var doc = PdfDocument.Open(input))
        {
            var report = doc.RedactText(HiddenFormToken, options);
            report.MatchesLocated.Should().BeGreaterThan(0, "the visible draw is page text");
            SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), HiddenFormToken).Should().BeEmpty();
            report.IsCleanSuccess.Should().BeTrue();
        }
    }

    private const string HiddenFormToken = "HIDDENONLYFORMTRAP";

    /// <summary>
    /// #1868: whether a form is still drawn is decided across every content
    /// stream in the file, and what cannot be decided is reported. Each shape
    /// has the form (object 8, drawing the token) dropped from the page by the
    /// hidden-layer pass.
    /// </summary>
    [Theory]
    [InlineData("an appearance stream draws it", "kept")]
    [InlineData("a form only it draws, also filed on the page", "removed")]
    [InlineData("its own /OC is off", "removed")]
    [InlineData("a form excise cannot decode may draw it", "reported")]
    public void WhetherAFormLeftUndrawnIsRemoved_IsDecidedOverTheWholeFile(string shape, string outcome)
    {
        var form = $"BT /F1 12 Tf 72 500 Td ({HiddenFormToken}) Tj ET";
        var hidden = "/OC /MC0 BDC q /Fx0 Do Q EMC\n";
        var extras = new List<RecoveryFixtureBuilder.Obj> { new("<< /Type /OCG /Name (Draft) >>") };
        RecoveryFixtureBuilder.Obj Form(string content, string extra = "") =>
            new($"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] {extra} /Length {content.Length} >>",
                System.Text.Encoding.ASCII.GetBytes(content));
        string xobjects = "/Fx0 8 0 R", pageExtra = "";
        switch (shape)
        {
            case "an appearance stream draws it":
                extras.Add(Form(form, "/Resources << /Font << /F1 5 0 R >> >>"));
                // No /Subtype /Form: an appearance stream is one whether or not it says so.
                extras.Add(new("<< /BBox [0 0 100 100] /Resources << /XObject << /Fx0 8 0 R >> >> /Length 7 >>",
                    System.Text.Encoding.ASCII.GetBytes("/Fx0 Do")));
                extras.Add(new("<< /Type /Annot /Subtype /Square /Rect [0 0 100 100] /AP << /N 9 0 R >> /P 3 0 R >>"));
                pageExtra = "/Annots [10 0 R]";
                break;
            case "a form only it draws, also filed on the page":
                extras.Add(Form("q /Fy0 Do Q", "/Resources << /XObject << /Fy0 9 0 R >> >>"));
                extras.Add(Form(form, "/Resources << /Font << /F1 5 0 R >> >>"));
                xobjects += " /Fy0 9 0 R";
                break;
            case "its own /OC is off":
                extras.Add(Form(form, "/OC 7 0 R /Resources << /Font << /F1 5 0 R >> >>"));
                hidden = "q /Fx0 Do Q\n";
                break;
            default:
                extras.Add(Form(form, "/Resources << /Font << /F1 5 0 R >> >>"));
                extras.Add(Form("/Fx0 Do", "/Filter /Nonexistent /Resources << /XObject << /Fx0 8 0 R >> >>"));
                hidden += "q /Fz0 Do Q\n";
                xobjects += " /Fz0 9 0 R";
                break;
        }
        using var doc = PdfDocument.Open(RecoveryFixtureBuilder.Build(
            "BT /F1 12 Tf 72 700 Td (VISIBLE) Tj ET\n" + hidden, extras, pageExtra,
            catalogExtra: "/OCProperties << /OCGs [7 0 R] /D << /OFF [7 0 R] >> >>",
            resourcesExtra: $"/Properties << /MC0 7 0 R >> /XObject << {xobjects} >>"));

        var report = doc.RedactText("VISIBLE", RedactionOptions.Default);

        var found = SavedPdfLeakScanner.FindTerm(doc.SaveToBytes(), HiddenFormToken);
        var row = report.Carriers.Where(c => c.Carrier == "form XObject 8 0 R");
        switch (outcome)
        {
            case "removed":
                found.Should().BeEmpty($"{shape}: nothing visible draws the form any more");
                report.IsCleanSuccess.Should().BeTrue();
                break;
            case "kept":
                found.Should().NotBeEmpty($"{shape}: the form is still drawn, so removing it would delete visible content");
                row.Should().BeEmpty("a drawn form is not a refusal");
                break;
            default:
                found.Should().NotBeEmpty($"{shape}: kept, because it cannot be proved unused");
                row.Should().ContainSingle().Which.RefusedReason.Should().Contain("could not be proved unused");
                report.IsCleanSuccess.Should().BeFalse();
                break;
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
