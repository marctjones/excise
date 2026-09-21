using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Redaction.Recovery;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1592 — one test per failure mode in
/// <c>tests/unredaction-failure-modes.json</c>, including the modes excise does
/// NOT cover.
///
/// <para><b>The gap tests are the point.</b> A registry row saying "gap" is a
/// claim about excise's behaviour, and an unverified claim in a registry is
/// exactly the drift this repository keeps getting caught by. Each gap here is
/// demonstrated: the fixture leaks, an independent tool reads the leak, and
/// excise reports nothing. When the gap is closed the test fails, which is the
/// correct way for it to come to someone's attention.</para>
/// </summary>
public class FailureModeChannelTests
{
    // ── modes with a channel ────────────────────────────────────────────────

    [Fact]
    public void PriorRevision_RecoversTextTheCurrentRevisionRemoved()
    {
        // The unredacted revision is still at the front of the file.
        var original = RecoveryFixtureBuilder.TextUnderBox("MANAFORT", drawBox: false);
        var updated = RecoveryFixtureBuilder.IncrementalUpdate(
            original, "BT /F1 14 Tf 72 700 Td (REDACTED) Tj ET\n");

        var (findings, summary) = PriorRevisionRecovery.Scan(updated);

        summary.RevisionCount.Should().BeGreaterThan(1, "the file is an incremental update");
        summary.RevisionsParsed.Should().BeGreaterThan(0);
        findings.Should().Contain(f => f.Text == "MANAFORT" && f.PageNumber == 1);
        findings.Single(f => f.Text == "MANAFORT").Rect.Width.Should().BeGreaterThan(0,
            "a prior-revision finding carries the box it occupied THEN");
    }

    [Fact]
    public void PriorRevision_OnASingleRevisionFile_FindsNothing()
    {
        // Negative control. Without it a scanner that reported every word would
        // pass the test above.
        var single = RecoveryFixtureBuilder.TextUnderBox("MANAFORT", drawBox: false);
        var (findings, summary) = PriorRevisionRecovery.Scan(single);

        summary.RevisionCount.Should().Be(1);
        findings.Should().BeEmpty();
    }

    [Fact]
    public void PriorRevision_TextPresentInBothRevisions_IsNotReported()
    {
        // Only the DIFFERENCE is a recovery. Text the current revision still
        // shows is not something anyone needs recovering.
        var original = RecoveryFixtureBuilder.TextUnderBox("KEEPME", drawBox: false);
        var updated = RecoveryFixtureBuilder.IncrementalUpdate(
            original, "BT /F1 14 Tf 72 700 Td (KEEPME) Tj ET\n");

        var (findings, _) = PriorRevisionRecovery.Scan(updated);
        findings.Should().NotContain(f => f.Text == "KEEPME");
    }

    [Fact]
    public void Thumbnail_IsReportedAsAPresentOnlyLeak()
    {
        using var doc = PdfDocument.Open(RecoveryFixtureBuilder.PageWithThumbnail());
        var finding = RecoveryScanner.Scan(doc).AllFindings
            .Single(f => f.Channel == RecoveryScanner.Channels.Thumbnail);

        finding.Confidence.Should().Be(RecoveryConfidence.PresentOnly);
        finding.Carrier.Should().Contain("/Thumb");
        finding.Text.Should().BeNull("a thumbnail is a picture, not a recovered value");
    }

    [Fact]
    public void Attachment_IsReportedAsAPresentOnlyLeak()
    {
        using var doc = PdfDocument.Open(RecoveryFixtureBuilder.PageWithAttachment("secret-notes.txt"));
        var finding = RecoveryScanner.Scan(doc).AllFindings
            .Single(f => f.Channel == RecoveryScanner.Channels.Attachment);

        finding.Confidence.Should().Be(RecoveryConfidence.PresentOnly);
        finding.Carrier.Should().Contain("secret-notes.txt");
        finding.Location.Should().BeNull("an attachment belongs to the document, not a page");
    }

    [Fact]
    public void ADocumentWithNoThumbnailOrAttachment_ReportsNeither()
    {
        using var doc = PdfDocument.Open(RecoveryFixtureBuilder.TextUnderBox("X"));
        var findings = RecoveryScanner.Scan(doc).AllFindings.ToList();

        findings.Should().NotContain(f => f.Channel == RecoveryScanner.Channels.Thumbnail);
        findings.Should().NotContain(f => f.Channel == RecoveryScanner.Channels.Attachment);
    }

    [Fact]
    public void BoxInsideAFormXObject_IsAMarkAndItsTextIsRecovered()
    {
        // #1606, the last registry gap. The page content stream holds the text
        // and a Do; the covering box is one level down, so without recursing
        // there is no mark and the text reads as a redaction that held.
        Assert.SkipUnless(MutoolTextOracle.IsAvailable, "mutool is not on PATH");

        var bytes = RecoveryFixtureBuilder.TextUnderBoxInFormXObject("FORMCOVERED");
        MutoolTextOracle.ExtractAllPages(bytes).Should().Contain("FORMCOVERED",
            "the text is in the page content stream, untouched");

        using var doc = PdfDocument.Open(bytes);
        var report = RecoveryScanner.Scan(doc);

        report.Marks.Should().Contain(m => m.Mark.Description.Contains("Form XObject"),
            "the box inside the form is recognised as a redaction mark");
        report.AllFindings.Should().Contain(
            f => f.Text != null && f.Text.Contains("FORMCOVERED"));
    }

    [Fact]
    public void AFormXObjectWithNoDarkFill_ProducesNoMark()
    {
        // Negative control: forms are ubiquitous (headers, logos, stamps), and
        // a recursion that treated every form as a mark would bury the report.
        var bytes = RecoveryFixtureBuilder.Build(
            "BT /F1 14 Tf 72 700 Td (PUBLIC) Tj ET\nq /Fx0 Do Q\n",
            extraObjects: new[]
            {
                new RecoveryFixtureBuilder.Obj(
                    "<< /Type /XObject /Subtype /Form /BBox [0 0 100 20] /Length 26 >>",
                    System.Text.Encoding.ASCII.GetBytes("1 1 1 rg 0 0 100 20 re f\n ")),
            },
            resourcesExtra: "/XObject << /Fx0 7 0 R >>");

        using var doc = PdfDocument.Open(bytes);
        RecoveryScanner.Scan(doc).MarkCount.Should().Be(0,
            "a white fill inside a form is page furniture, not a redaction");
    }

    [Fact]
    public void OrphanedOriginalImage_IsReportedAsAPresentOnlyLeak()
    {
        // #1608. The page draws a blacked-out replacement; the original is
        // still in the file, referenced by nothing, and mutool extract or
        // qpdf --qdf recovers it whole.
        using var doc = PdfDocument.Open(RecoveryFixtureBuilder.OrphanedOriginalImage());

        var finding = RecoveryScanner.Scan(doc, options: RecoveryScanOptions.IncludingDeferred).AllFindings
            .Single(f => f.Channel == RecoveryScanner.Channels.ImageLayer);

        finding.Confidence.Should().Be(RecoveryConfidence.PresentOnly);
        finding.Carrier.Should().Contain("referenced by");
        finding.Text.Should().BeNull("the channel names the leak, it does not decode pixels");
    }

    [Fact]
    public void FullyMaskedImage_IsReportedAsAPresentOnlyLeak()
    {
        using var doc = PdfDocument.Open(RecoveryFixtureBuilder.FullyMaskedImage());

        var finding = RecoveryScanner.Scan(doc, options: RecoveryScanOptions.IncludingDeferred).AllFindings
            .Single(f => f.Channel == RecoveryScanner.Channels.ImageLayer);

        finding.Confidence.Should().Be(RecoveryConfidence.PresentOnly);
        finding.Carrier.Should().Contain("transparent");
    }

    [Fact]
    public void AnOrdinaryImage_IsNotReportedAsAnImageLayerLeak()
    {
        // The negative control that matters most here: both shapes occur
        // innocently, and a channel that fired on every document with an image
        // would be worse than no channel.
        using var doc = PdfDocument.Open(RecoveryFixtureBuilder.ImageUnderBox());

        RecoveryScanner.Scan(doc, options: RecoveryScanOptions.IncludingDeferred).AllFindings
            .Should().NotContain(f => f.Channel == RecoveryScanner.Channels.ImageLayer,
                "an ordinary image under a box is the covered-image channel's subject");
    }

    [Fact]
    public void AnUnreferencedImageWithNoMatchingReplacement_IsNotReported()
    {
        // Unreferenced objects accumulate benignly in incrementally-updated
        // files. Without the same-dimensions replacement that signals a SWAP,
        // an orphan belongs to the prior-revision channel, not this one.
        var bytes = RecoveryFixtureBuilder.IncrementalUpdate(
            RecoveryFixtureBuilder.PageWithThumbnail("BEFORE"),
            "BT /F1 14 Tf 72 700 Td (AFTER) Tj ET\n");

        using var doc = PdfDocument.Open(bytes);
        // #1690: the negative control OPTS IN deliberately. With the deferred
        // channel off it would pass without running anything -- a check that
        // cannot fail.
        RecoveryScanner.Scan(doc, options: RecoveryScanOptions.IncludingDeferred).AllFindings
            .Should().NotContain(f => f.Channel == RecoveryScanner.Channels.ImageLayer);
    }

    [Fact]
    public void XfaFieldValue_IsRecoveredFromTheDatasetsPacket()
    {
        // #1609. The page shows a black box; the XFA datasets packet still
        // holds the value, and a reader that renders the XFA form paints it
        // straight back.
        var bytes = RecoveryFixtureBuilder.XfaFormWithValue("ssn", "123-45-6789");
        using var doc = PdfDocument.Open(bytes);

        var finding = RecoveryScanner.Scan(doc).AllFindings
            .Single(f => f.Channel == RecoveryScanner.Channels.Xfa);

        finding.Confidence.Should().Be(RecoveryConfidence.Certain);
        finding.Text.Should().Be("123-45-6789");
        finding.Carrier.Should().Contain("ssn");
        finding.Location.Should().BeNull(
            "an XFA value has no laid-out box yet (#1547); inventing one would be worse");

        // Corroboration that this is a real leak and not an excise artefact:
        // the value is in the saved bytes.
        SavedPdfLeakScanner.FindTerm(bytes, "123-45-6789").Should().NotBeEmpty();
    }

    [Fact]
    public void ADocumentWithNoXfa_SaysSoRatherThanReportingNothing()
    {
        // "No XFA findings" and "this document has no XFA" are different
        // claims, and the report must not let the first be read as the second.
        using var doc = PdfDocument.Open(RecoveryFixtureBuilder.TextUnderBox("X"));
        var report = RecoveryScanner.Scan(doc);

        report.ChannelsRun.Should().NotContain(RecoveryScanner.Channels.Xfa);
        report.ChannelsSkipped.Should().ContainKey(RecoveryScanner.Channels.Xfa)
            .WhoseValue.Should().Contain("no /AcroForm /XFA");
    }

    [Fact]
    public void XfaStructuralElements_AreNotReportedAsFieldValues()
    {
        // Negative control: a collector that reported every element's text
        // would fill the report with form design and bury a real value.
        var bytes = RecoveryFixtureBuilder.XfaFormWithValue("ssn", "123-45-6789");
        using var doc = PdfDocument.Open(bytes);

        var xfa = RecoveryScanner.Scan(doc).AllFindings
            .Where(f => f.Channel == RecoveryScanner.Channels.Xfa).ToList();

        xfa.Should().ContainSingle("only the leaf value is a finding");
        xfa[0].Carrier.Should().NotContain("datasets");
        xfa[0].Carrier.Should().NotContain("xdp");
    }

    // ── newly closed, and the controls that keep them honest ────────────────

    [Fact]
    public void InvisibleTextRenderMode3_IsRecovered()
    {
        // #1607, now closed. Tr 3 text is fully extractable and never painted —
        // how every OCR layer is written. The walker used to parse Tr and throw
        // it away, so no sink could tell this glyph from a visible one.
        Assert.SkipUnless(MutoolTextOracle.IsAvailable, "mutool is not on PATH");

        var bytes = RecoveryFixtureBuilder.InvisibleText("INVISIBLESECRET");
        MutoolTextOracle.ExtractAllPages(bytes).Should().Contain("INVISIBLESECRET",
            "the leak is real: an independent engine reads the invisible text");

        using var doc = PdfDocument.Open(bytes);
        var finding = RecoveryScanner.Scan(doc).AllFindings.Single(
            f => f.Text != null && f.Text.Contains("INVISIBLESECRET"));

        finding.Confidence.Should().Be(RecoveryConfidence.Certain);
        finding.Carrier.Should().Contain("render mode 3");
    }

    [Fact]
    public void VisibleText_IsNotReportedAsInvisible()
    {
        // The negative control for pairing D. Ordinary text is render mode 0,
        // and a detector that flagged everything would pass the test above.
        using var doc = PdfDocument.Open(
            RecoveryFixtureBuilder.TextUnderBox("PUBLIC", drawBox: false));

        RecoveryScanner.Scan(doc).AllFindings
            .Should().NotContain(f => f.Carrier.Contains("invisible text"));
    }

    [Fact]
    public void AnnotationDrawnBox_TextInsideTheMarkIsRecovered()
    {
        // #1606, and the dangerous direction. The mark IS detected (a dark
        // /Square annotation), so the report shows a redaction — and grades it
        // "not recovered" while the text underneath is trivially extractable.
        // A reader takes that row as "this redaction held".
        Assert.SkipUnless(MutoolTextOracle.IsAvailable, "mutool is not on PATH");

        var bytes = RecoveryFixtureBuilder.TextUnderSquareAnnotation("ANNOTCOVERED");
        MutoolTextOracle.ExtractAllPages(bytes).Should().Contain("ANNOTCOVERED",
            "the text is in the page content stream, untouched");

        using var doc = PdfDocument.Open(bytes);
        var report = RecoveryScanner.Scan(doc);

        report.Marks.Should().Contain(m => m.Mark.Kind == RedactionMarkKind.ShapeAnnotation,
            "the annotation IS recognised as a redaction mark");

        // #1606, now closed. The hidden-text detector walks the page content
        // stream and an annotation is not in it; the mark-region channel reads
        // the page inside the mark instead.
        var finding = report.AllFindings.Single(
            f => f.Text != null && f.Text.Contains("ANNOTCOVERED"));
        finding.Channel.Should().Be(RecoveryScanner.Channels.MarkRegion);
        finding.Confidence.Should().Be(RecoveryConfidence.Certain);

        // And the mark no longer reads as a redaction that held.
        report.Marks.Single(m => m.Mark.Kind == RedactionMarkKind.ShapeAnnotation)
            .Outcome.Should().NotBe(MarkRecoveryOutcome.NotRecovered);
    }

    [Fact]
    public void UnappliedRedactAnnotation_TextInsideTheMarkIsRecovered()
    {
        // The tier-A bench caught the registry claiming this mode covered on
        // the strength of a test that only checked the MARK was detected. The
        // text is what matters: §12.5.6.23 says the annotation marks a region
        // intended for redaction, so text still inside one is material somebody
        // meant to remove.
        using var doc = PdfDocument.Open(
            RecoveryFixtureBuilder.UnappliedRedactAnnotation("CONFIDENTIAL"));
        var report = RecoveryScanner.Scan(doc);

        var finding = report.AllFindings.Single(
            f => f.Text != null && f.Text.Contains("CONFIDENTIAL"));
        finding.Channel.Should().Be(RecoveryScanner.Channels.MarkRegion);
        finding.Carrier.Should().Contain("never removed");
    }

    [Fact]
    public void AContentStreamBox_IsNotDoubleReportedByTheMarkRegionChannel()
    {
        // The mark-region channel is restricted to ANNOTATION marks precisely
        // so it does not duplicate every ordinary hidden-text finding.
        using var doc = PdfDocument.Open(RecoveryFixtureBuilder.TextUnderBox("MANAFORT"));

        RecoveryScanner.Scan(doc).AllFindings
            .Where(f => f.Text != null && f.Text.Contains("MANAFORT"))
            .Should().ContainSingle("a box in page content is the hidden-text channel's job alone");
    }
}
