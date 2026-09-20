using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// #899 — reading order on dense MULTI-COLUMN pages. The extraction-parity gate
/// measures character COVERAGE (a multiset vs mutool); it cannot see ORDER, and
/// that blind spot is exactly what let #899 ship: page.Letters was complete
/// (3107 chars vs mutool's 2945) but the letters→string assembly interleaved the
/// columns and dropped a chunk — "made on their behalf directly to Line 10Amount
/// Paid With Request for Extension" jumping mid-sentence from one column into the
/// other column's heading. The §9.4.2 line-stepping fix (#942/#992) resolved it;
/// this pins the READING ORDER so a regression in the column assembler is caught
/// by more than a coverage number.
/// </summary>
public sealed class ReadingOrderRegressionTests
{
    [Fact]
    public void MultiColumnInstructionBooklet_ReadsColumnByColumn_NoInterleaving()
    {
        // The worst #899 page (irs-1040-instructions p117, was 0.774 coverage,
        // interleaved). Corpus-gated: skip when the fixture is absent.
        //
        // #1706 — this used to walk upward to the first .git and join
        // test-pdfs onto it, which stops at a worktree's .git FILE rather than
        // the main checkout where the corpus lives.
        var path = TestRepoLayout.FindFile("test-pdfs", "smoke", "irs-1040-instructions.pdf")
                   ?? TestRepoLayout.FindFile("test-pdfs", "federal", "irs-1040-instructions.pdf");
        Assert.SkipUnless(path != null, TestRepoLayout.AbsenceReason(
            "irs-1040-instructions.pdf",
            "test-pdfs/smoke/irs-1040-instructions.pdf", "test-pdfs/federal/irs-1040-instructions.pdf"));

        using var doc = PdfDocument.Open(path!);
        doc.PageCount.Should().BeGreaterThanOrEqualTo(117);
        // Normalise whitespace runs — reading ORDER is the #899 property, not exact
        // spacing (GetPage.Text emits run-gap spaces the CLI collapses).
        var text = System.Text.RegularExpressions.Regex.Replace(
            doc.GetPage(117).Text, @"\s+", " ");

        // Within-column prose passages must survive INTACT — if the assembler
        // jumps columns mid-sentence (the #899 symptom), these break apart.
        text.Should().Contain("premium tax credit made on their behalf directly",
            "a within-column sentence must not be split by a jump into the other column (#899)");
        text.Should().Contain("enrolled in health insurance through the Market",
            "a within-column sentence must read contiguously (#899)");

        // The #899 interleaving signature: a sentence tail immediately followed by
        // the OTHER column's heading with no boundary. Must NOT occur.
        text.Should().NotContain("behalf directly to Line 10Amount",
            "the mid-sentence column jump #899 described must not reappear");
    }
}
