using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Tests.Content;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1089 — <c>RedactText</c> must report what it VERIFIED, not what it
/// attempted.
///
/// <para>The old <c>int</c> return counted matches LOCATED per pass. It could
/// not distinguish removed from smeared from survived from deliberately
/// skipped, so it reported all four as success — which is how one occurrence
/// printed as "Redacted 3" (#1043), how a whole line could be destroyed
/// silently (#1038), and how a real name stayed in a real document behind a
/// black box while excise reported success (#1040).</para>
///
/// <para>⚠️ MEASURED SCOPE. These pin the REPORTING SHAPE — located vs
/// verified, refusals named, the summary line stating the gap. They do NOT
/// prove the verification re-read catches a failed removal: mutating
/// <c>CountOccurrences</c> to always return 0 leaves every test here green,
/// because on these fixtures removal genuinely succeeds and there is nothing
/// for verification to catch.</para>
///
/// <para>Proving that needs a fixture where removal FAILS — a stalling
/// document, which is #1038's own repro and does not exist in this suite yet —
/// and corroboration by a non-excise oracle corpus-wide, which is #1094.
/// Recorded rather than glossed: a green suite that implies more cover than it
/// has is the exact failure this whole milestone is about.</para>
/// </summary>
public class RedactionReportTests
{
    private const string Secret = "Farrar";

    private static byte[] Pdf() => ContentStreamFixture.Build(
        $"BT /F1 12 Tf 20 700 Td (Louise Anne {Secret} and more) Tj ET\n");

    [Fact]
    public void AVerifiedRemoval_ReportsOneRemovedAndNoneSurviving()
    {
        using var doc = PdfDocument.Open(Pdf());
        var report = doc.RedactText(Secret, drawBlackRect: false);

        report.VerifiedRemovals.Should().Be(1);
        report.Survived.Should().Be(0,
            "the term must not be findable after redaction; anything else is a leak " +
            "regardless of how many removals were attempted");
        report.IsCleanSuccess.Should().BeTrue();
    }

    /// <summary>
    /// The anti-vacuity guard. Without it, a report that returned zeroes for
    /// everything would satisfy the test above.
    /// </summary>
    [Fact]
    public void TheReportDistinguishesLocatedFromVerified()
    {
        using var doc = PdfDocument.Open(Pdf());
        var report = doc.RedactText(Secret, drawBlackRect: false);

        report.MatchesLocated.Should().BeGreaterThan(0,
            "the fixture contains the term — a report claiming it located nothing " +
            "would make VerifiedRemovals==0 look like success");
        report.Pages.Should().ContainSingle();
        report.Pages[0].Outcome.Should().Be(RedactionOutcome.RemovedVerified);
    }

    /// <summary>
    /// #999, as a reported refusal rather than a silent policy. A sub-3-char
    /// term is still redacted from the PAGE, but the document-level carriers
    /// are deliberately skipped — and the old int return could not say so, so a
    /// caller got a success count while /Info and XMP kept the term.
    /// </summary>
    [Fact]
    public void ASubFloorTerm_ReportsEveryCarrierAsRefusedWithAReason()
    {
        using var doc = PdfDocument.Open(Pdf());
        var report = doc.RedactText("Fa", drawBlackRect: false);

        report.Carriers.Should().NotBeEmpty();
        report.Carriers.Should().OnlyContain(c => !c.Scrubbed && c.RefusedReason != null,
            "a 2-character term is below the carrier scrub floor, so /Info, XMP, " +
            "outlines and annotation /Contents were NOT looked at — the caller must " +
            "be told rather than handed a success count");
        report.IsCleanSuccess.Should().BeFalse(
            "a redaction that skipped every document carrier is not a clean success");
    }

    [Fact]
    public void OptingOutOfCarrierScrub_IsAlsoReported()
    {
        using var doc = PdfDocument.Open(Pdf());
        var report = doc.RedactText(Secret, drawBlackRect: false, scrubDocumentCarriers: false);

        report.Carriers.Should().OnlyContain(c => !c.Scrubbed && c.RefusedReason != null,
            "the caller asked for it, and still needs it in the record — a redacted " +
            "file whose carriers were skipped looks identical to one where they were not");
    }

    /// <summary>
    /// The summary line is what a human actually reads. It must state the gap,
    /// not just the good number.
    /// </summary>
    [Fact]
    public void TheSummaryLine_StatesRefusalsRatherThanOnlyTheCount()
    {
        using var doc = PdfDocument.Open(Pdf());
        var report = doc.RedactText("Fa", drawBlackRect: false);

        report.ToString().Should().Contain("NOT scrubbed",
            "a one-line summary that mentions only removals would reproduce exactly the " +
            "failure this type exists to end");
    }

    [Fact]
    public void AnEmptyTerm_ReportsNothingRatherThanThrowing()
    {
        using var doc = PdfDocument.Open(Pdf());
        var report = doc.RedactText("", drawBlackRect: false);

        report.VerifiedRemovals.Should().Be(0);
        report.Pages.Should().BeEmpty();
    }
}
