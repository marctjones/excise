using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Core.Redaction.Recovery;
using Xunit;

namespace Excise.Core.Tests.Redaction.Recovery;

/// <summary>
/// #1617 — a redaction bar drawn with NO colour operator in scope.
///
/// <para>§8.6.8 makes the initial fill colour black, so <c>x y w h re f</c> with
/// nothing before it paints a black rectangle. Both detectors used to start from
/// white instead: <see cref="RedactionMarkDetector"/> skipped any fill drawn
/// before a colour operator, and <see cref="HiddenTextDetector"/> scored it as
/// non-obstructive and reported nothing underneath.</para>
///
/// <para><b>This is not a hypothetical shape.</b> Every bar in the Manafort
/// breach-response filing (D.D.C. 1:17-cr-00201 #471, 2019-01-08) — the most
/// cited failed redaction there is — is drawn exactly this way, and
/// <c>unredact</c> reported ZERO marks and ZERO findings on it while pdftotext
/// read the covered text straight out. The corrected filing (#472) yields 24
/// marks and nothing recovered, before and after, so the fix separates the two
/// rather than merely finding more.</para>
///
/// <para>The synthetic corpus could not catch this, because its generator writes
/// <c>0 0 0 rg</c> before every box — what a person writing a fixture does. These
/// tests exist to hold the spec behaviour without a corpus.</para>
/// </summary>
public class DefaultFillColourMarkTests
{
    private const string Secret = "CONFIDENTIAL";

    [Fact]
    public void ABarDrawnWithNoColourOperator_IsAMark()
    {
        var pdf = RecoveryFixtureBuilder.TextUnderBox(Secret, boxColourOp: "");
        using var document = PdfDocument.Open(pdf);

        var marks = RedactionMarkDetector.Detect(document);

        marks.Should().NotBeEmpty(
            "§8.6.8 makes the initial fill colour black, so a bar drawn before " +
            "any colour operator is a black bar");
        marks.Should().Contain(m => m.Kind == RedactionMarkKind.FilledBox);
    }

    [Fact]
    public void TextUnderABarDrawnWithNoColourOperator_IsReportedHidden()
    {
        // The security-relevant half. The mark detector decides what the report
        // is ABOUT; this decides whether the leak is seen at all.
        var pdf = RecoveryFixtureBuilder.TextUnderBox(Secret, boxColourOp: "");
        using var document = PdfDocument.Open(pdf);

        HiddenTextDetector.Scan(document)
            .Should().Contain(h => h.Text.Contains(Secret),
                "an unset fill is black, so the bar obstructs and the text under it is hidden");
    }

    [Fact]
    public void TheWholeScan_RecoversIt()
    {
        var pdf = RecoveryFixtureBuilder.TextUnderBox(Secret, boxColourOp: "");
        using var document = PdfDocument.Open(pdf);

        var report = RecoveryScanner.Scan(document, TestContext.Current.CancellationToken);

        report.MarkCount.Should().BeGreaterThan(0);
        report.AllFindings.Should().Contain(f =>
            f.Confidence == RecoveryConfidence.Certain && f.Text != null && f.Text.Contains(Secret));

        // Attribution, not just detection: the finding must be tied to the mark
        // it came from. Which grade of recovered it earns depends on how much of
        // the mark's width the recovered run covers, and that is the linking
        // rule's business, not this test's — what must not happen is the mark
        // reading as though nothing was found under it.
        report.Marks.Should().Contain(m => m.Outcome != MarkRecoveryOutcome.NotRecovered);
        report.MarksNotRecovered.Should().BeLessThan(report.MarkCount);
    }

    [Fact]
    public void AnExplicitlyWhiteBar_IsStillNotAMark()
    {
        // The other direction, and the reason the old default looked defensible:
        // honouring §8.6.8 must not turn every pale rectangle into a redaction.
        // A producer that SAYS white is saying it covers nothing.
        var pdf = RecoveryFixtureBuilder.TextUnderBox(Secret, boxColourOp: "1 1 1 rg");
        using var document = PdfDocument.Open(pdf);

        RedactionMarkDetector.Detect(document)
            .Should().NotContain(m => m.Kind == RedactionMarkKind.FilledBox,
                "a white fill hides nothing, whoever set it");
    }

    [Fact]
    public void NoBarAtAll_IsStillNoMark()
    {
        // The regression this guards against in the other direction: text alone
        // must not acquire a mark because the default colour is now dark.
        var pdf = RecoveryFixtureBuilder.TextUnderBox(Secret, drawBox: false);
        using var document = PdfDocument.Open(pdf);

        RedactionMarkDetector.Detect(document)
            .Should().BeEmpty("nothing was drawn over anything");
    }
}
