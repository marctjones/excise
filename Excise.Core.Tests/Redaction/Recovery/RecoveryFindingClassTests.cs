using AwesomeAssertions;
using Excise.Core.Redaction.Recovery;
using Xunit;
using static Excise.Core.Redaction.Recovery.RecoveryFindingClassifier;
using Ch = Excise.Core.Redaction.Recovery.RecoveryScanner.Channels;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1669 — the classification, pinned against the carriers that actually fired
/// on 57 clean court filings. Every string below was OBSERVED, not invented.
/// </summary>
public class RecoveryFindingClassTests
{
    [Theory]
    // The 28 false positives, by their real carrier strings.
    [InlineData(Ch.Carrier, "action /URI (annotation /A)", false)]        // 38 of them
    [InlineData(Ch.Carrier, "XMP dc:creator", false)]                     // 23
    [InlineData(Ch.Carrier, "XMP pdfx:SourceModified", false)]
    [InlineData(Ch.Carrier, "XMP pdf:Keywords", false)]
    [InlineData(Ch.Carrier, "/PieceInfo /Acroscan1 /Private", false)]
    [InlineData(Ch.Carrier, "annotation /T (author)", false)]
    [InlineData(Ch.Carrier, "/Info /Author", false)]
    public void OrdinaryDocumentFurniture_IsRankedLow(string channel, string carrier, bool linked)
        => Classify(channel, carrier, linked).Should().Be(RecoveryFindingClass.DocumentFurniture);

    /// <summary>
    /// ⚠️ THE AMBIGUOUS CASE, RESOLVED IN THE SAFE DIRECTION — and this test
    /// records a decision, not a behaviour that fell out.
    ///
    /// <para>A scanned page's OCR layer and a "redaction" done by setting
    /// <c>3 Tr</c> on the sensitive words are STRUCTURALLY IDENTICAL: invisible
    /// extractable text, no mark. Two heuristics were tried to separate them
    /// and both ate the mode they were meant to protect — suppressing
    /// unlinked invisible text took <c>text-render-mode-3</c> from 3/3 to 0/3,
    /// because that mode IS invisible text with no mark.</para>
    ///
    /// <para>So both are RESIDUE. For a security audit a false positive costs
    /// attention and a false negative costs a leak; the costs are not
    /// symmetric. The price is measured and accepted — 7 of 57 clean filings
    /// report their OCR layer — and #1669 tracks ranking it in the REPORT
    /// rather than guessing in the classifier.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvisibleText_IsResidueWhetherOrNotAMarkCoversIt(bool linked)
        => Classify(Ch.HiddenText, "invisible text (render mode 3) — extractable, never painted", linked)
            .Should().Be(RecoveryFindingClass.RedactionResidue,
                "the ambiguity is real and is resolved toward reporting, not toward silence");

    /// <summary>
    /// The line that CAN be drawn without guessing: none of the furniture
    /// carriers is page text. Anything about page text is a finding.
    /// </summary>
    [Fact]
    public void TheFurnitureLine_IsMetadataAndNavigation_NotPageText()
    {
        Classify(Ch.Carrier, "XMP dc:creator", false)
            .Should().Be(RecoveryFindingClass.DocumentFurniture);
        Classify(Ch.Carrier, "structure /ActualText", false)
            .Should().Be(RecoveryFindingClass.ContentCarrier,
                "a structure carrier restates PAGE TEXT, which is the #636 leak");
    }

    [Theory]
    [InlineData(Ch.HiddenText, "black filled rectangle", true)]
    [InlineData(Ch.MarkRegion, "text inside the mark", true)]
    [InlineData(Ch.CoveredImage, "image XObject /Im0", true)]
    [InlineData(Ch.CoveredVector, "vector path (S)", true)]
    public void ContentFoundWhereARedactionWasApplied_IsResidue(string channel, string carrier, bool linked)
        => Classify(channel, carrier, linked).Should().Be(RecoveryFindingClass.RedactionResidue);

    [Theory]
    [InlineData(Ch.MarkedContent, "marked content /ActualText")]
    [InlineData(Ch.FormField, "field /V")]
    [InlineData(Ch.Xfa, "XFA datasets")]
    [InlineData(Ch.PriorRevision, "revision 0 of 2")]
    [InlineData(Ch.Thumbnail, "page /Thumb")]
    [InlineData(Ch.Attachment, "embedded file notes.txt")]
    [InlineData(Ch.ImageLayer, "orphaned original image")]
    public void CarriersThatRestatePageContent_AreContentCarriers(string channel, string carrier)
        => Classify(channel, carrier, false).Should().Be(RecoveryFindingClass.ContentCarrier);

    /// <summary>
    /// ⚠️ Furniture is RANKED, never hidden. #608 is a redacted term leaking
    /// into XMP; if this returned false the tool would trade a flood for a
    /// silent leak, which is the worse of the two.
    /// </summary>
    [Fact]
    public void FurnitureIsStillReported_JustNotCountedInTheVerdict()
    {
        IndicatesAFailedRedaction(RecoveryFindingClass.DocumentFurniture).Should().BeFalse();
        IndicatesAFailedRedaction(RecoveryFindingClass.RedactionResidue).Should().BeTrue();
        IndicatesAFailedRedaction(RecoveryFindingClass.ContentCarrier).Should().BeTrue();
    }
}
