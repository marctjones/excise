using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Redaction.Recovery;
using Xunit;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1587 — each recovery channel against a fixture whose ground truth is known
/// by construction, checking the three things the model promises: the right
/// CONFIDENCE class, a LOCATION, and a link to the MARK it came out from under.
/// </summary>
public class RecoveryChannelTests
{
    [Fact]
    public void HiddenText_UnderABlackBox_IsCertainLocatedAndLinked()
    {
        using var doc = PdfDocument.Open(RecoveryFixtureBuilder.TextUnderBox("MANAFORT"));
        var report = RecoveryScanner.Scan(doc);

        var finding = report.AllFindings.Single(f => f.Channel == RecoveryScanner.Channels.HiddenText);
        finding.Confidence.Should().Be(RecoveryConfidence.Certain);
        finding.Text.Should().Be("MANAFORT");
        finding.Location.Should().NotBeNull();
        finding.Location!.PageNumber.Should().Be(1);
        finding.MarkId.Should().NotBeNull("the detector knew which fill covered this run");

        report.MarkCount.Should().BeGreaterThan(0);
        report.Marks.Single(m => m.Mark.Id == finding.MarkId)
            .Outcome.Should().Be(MarkRecoveryOutcome.Recovered);
    }

    [Fact]
    public void TextWithNoBoxOverIt_ProducesNoMarkAndNoHiddenTextFinding()
    {
        // The negative control. Without it a detector that flagged everything
        // would pass every test above.
        using var doc = PdfDocument.Open(
            RecoveryFixtureBuilder.TextUnderBox("PUBLIC", drawBox: false));
        var report = RecoveryScanner.Scan(doc);

        report.AllFindings.Should().NotContain(f => f.Channel == RecoveryScanner.Channels.HiddenText);
        report.MarkCount.Should().Be(0);
    }

    [Fact]
    public void MarkedContentActualText_IsRecoveredWithTheEnclosedGlyphBox()
    {
        // The #1182/#1185 carrier: the glyphs were removed and a box drawn, but
        // the inline /ActualText still spells the name. A structure-tree walk
        // never reaches this.
        using var doc = PdfDocument.Open(
            RecoveryFixtureBuilder.MarkedContentCarrierUnderBox("HARPER"));
        var report = RecoveryScanner.Scan(doc);

        var finding = report.AllFindings
            .Single(f => f.Channel == RecoveryScanner.Channels.MarkedContent);
        finding.Confidence.Should().Be(RecoveryConfidence.Certain);
        finding.Text.Should().Be("HARPER");
        finding.Carrier.Should().Contain("ActualText");
        finding.Location.Should().NotBeNull(
            "a carrier has no geometry of its own; the box comes from the glyphs its span painted");
    }

    [Fact]
    public void MarkedContentActualText_InANamedPropertyList_IsAlsoRecovered()
    {
        // The form the SCRUBBER cannot remove (#1182 follow-up, #1599). Recovery
        // has no such constraint, and a channel blind to it would miss a leak
        // that is physically present.
        using var doc = PdfDocument.Open(
            RecoveryFixtureBuilder.MarkedContentCarrierUnderBox("HANSEN", namedPropertyList: true));
        var report = RecoveryScanner.Scan(doc);

        var finding = report.AllFindings
            .Single(f => f.Channel == RecoveryScanner.Channels.MarkedContent);
        finding.Text.Should().Be("HANSEN");
        finding.Carrier.Should().Contain("named property list",
            "the report must say the scrub side cannot close this one");
    }

    [Fact]
    public void FormFieldValue_SurvivingABlankedAppearance_IsCertainAtTheWidgetRect()
    {
        using var doc = PdfDocument.Open(
            RecoveryFixtureBuilder.FormFieldValueUnderBox("ssn", "123-45-6789"));
        var report = RecoveryScanner.Scan(doc);

        var finding = report.AllFindings
            .Single(f => f.Channel == RecoveryScanner.Channels.FormField);
        finding.Confidence.Should().Be(RecoveryConfidence.Certain);
        finding.Text.Should().Be("123-45-6789");
        finding.Location.Should().NotBeNull();
        finding.Location!.Provenance.Should().Be("widget /Rect");
    }

    [Fact]
    public void UnappliedRedactAnnotation_IsAMarkAndItsTextComesBack()
    {
        // §12.5.6.23: a /Redact annotation marks an INTENTION. Until a tool
        // applies it, the text is untouched -- and the annotation obligingly
        // says where to look.
        using var doc = PdfDocument.Open(
            RecoveryFixtureBuilder.UnappliedRedactAnnotation("CONFIDENTIAL"));
        var report = RecoveryScanner.Scan(doc);

        report.Marks.Should().Contain(m => m.Mark.Kind == RedactionMarkKind.RedactAnnotation);
    }

    [Fact]
    public void ImageUnderABox_IsPresentOnlyNotCertain()
    {
        // The pixels are there and the location is exact, but nothing was READ.
        // Grading this "certain" would assert a value the channel never
        // produced; grading it a candidate would imply a guess that was never
        // made.
        using var doc = PdfDocument.Open(RecoveryFixtureBuilder.ImageUnderBox());
        // #1690: covered-image is the DEFERRED raster half of this channel; the
        // vector half above is Tier 1. The test asks for it rather than losing it.
        var report = RecoveryScanner.Scan(doc, options: RecoveryScanOptions.IncludingDeferred);

        var finding = report.AllFindings
            .Single(f => f.Channel == RecoveryScanner.Channels.CoveredImage);
        finding.Confidence.Should().Be(RecoveryConfidence.PresentOnly);
        finding.Text.Should().BeNull();
        finding.Candidates.Should().BeEmpty();
        finding.Location.Should().NotBeNull();
        finding.MarkId.Should().NotBeNull();
    }

    [Fact]
    public void VectorContentUnderABox_IsPresentOnly()
    {
        // A signature is strokes, not text. A text-only audit reports this page
        // clean while the original ink sits in the file.
        using var doc = PdfDocument.Open(RecoveryFixtureBuilder.VectorUnderBox());
        var report = RecoveryScanner.Scan(doc);

        report.AllFindings.Should().Contain(f =>
            f.Channel == RecoveryScanner.Channels.CoveredVector &&
            f.Confidence == RecoveryConfidence.PresentOnly);
    }

    [Fact]
    public void AnImageNotCoveredByAnything_IsNotReported()
    {
        // Negative control for the covered-content channel: an ordinary
        // illustration must not read as a leak.
        var bytes = RecoveryFixtureBuilder.Build(
            "q 120 0 0 120 72 600 cm /Im0 Do Q\n",
            extraObjects: new[]
            {
                new RecoveryFixtureBuilder.Obj(
                    "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray " +
                    "/BitsPerComponent 8 /Length 4 >>",
                    new byte[] { 0x00, 0x40, 0x80, 0xFF }),
            },
            resourcesExtra: "/XObject << /Im0 7 0 R >>");

        using var doc = PdfDocument.Open(bytes);
        // #1690: opts in, or the negative control passes by not running.
        RecoveryScanner.Scan(doc, options: RecoveryScanOptions.IncludingDeferred).AllFindings
            .Should().NotContain(f => f.Channel == RecoveryScanner.Channels.CoveredImage);
    }

    [Fact]
    public void EveryChannelThatRan_IsNamedInTheReport()
    {
        using var doc = PdfDocument.Open(RecoveryFixtureBuilder.TextUnderBox("X"));
        var report = RecoveryScanner.Scan(doc);

        report.ChannelsRun.Should().Contain(new[]
        {
            RecoveryScanner.Channels.HiddenText,
            RecoveryScanner.Channels.Carrier,
            RecoveryScanner.Channels.MarkedContent,
            RecoveryScanner.Channels.CoveredVector,
            RecoveryScanner.Channels.FormField,
        });

        // #1690: and the deferred one is SKIPPED with a reason, not absent.
        report.ChannelsRun.Should().NotContain(RecoveryScanner.Channels.CoveredImage);
        report.ChannelsSkipped.Should().ContainKey(RecoveryScanner.Channels.CoveredImage);
    }
}
