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
