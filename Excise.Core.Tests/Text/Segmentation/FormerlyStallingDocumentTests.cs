using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// The STALLING document gate — the missing half of #1038, #1044 and #1089.
///
/// <para>Inside <c>RedactText</c>'s loop, a "stall" is when a removal pass
/// leaves the page's extracted text <b>character-for-character identical</b>
/// (<c>stalled = searchTextSnapshot == previousSearchText</c>). excise located
/// the term and removed nothing. On a stall it abandons surgical removal and
/// deletes whole text-showing operators — which is #1038's 5–36% collateral,
/// or #1040's silent survival.</para>
///
/// <para><b>Every fixture in this suite converges on the first pass.</b> A
/// sweep of 5,568 redaction records across ~1,600 documents (#998 step 1) found
/// exactly FOUR that ever needed pass ≥ 1, and all four reached the stalled
/// branch. Without one of them checked in as a gate, three separate things
/// cannot be demonstrated: #1038's damage, whether #1044's blanking beats the
/// fallback, and whether #1089's verification re-read ever fires.</para>
///
/// <para><c>issue15629.pdf</c> is one of the four — 159 KB of real Nitro Pro
/// output from a pdf.js bug report, not a hand-built fixture. It was the one
/// that failed by LEAVING the term rather than over-removing.</para>
///
/// <para><b>⚠️ MEASURED 2026-08-20: this document NO LONGER STALLS.</b> It now
/// reports <c>located=1, verified=1, survived=0, RemovedVerified</c> — a clean
/// success. #1040's indirect-<c>/XObject</c> fix, and #1050's wider accessor
/// sweep, repaired it. Two consequences, both worth knowing:</para>
///
/// <list type="number">
///   <item>#1043's headline example is gone: this document reported <b>3</b>
///     for one occurrence, and now reports <b>1</b>.</item>
///   <item>This file is therefore a REGRESSION PIN, not a stall gate. It locks
///     in that a formerly-leaking real document redacts cleanly and counts
///     honestly. It does NOT exercise the stalled branch, so #1038's damage,
///     #1044's comparison and #1089's verification re-read all remain
///     undemonstrated — those need one of the OTHER THREE documents from
///     #998's sweep, which are not yet identified.</item>
/// </list>
///
/// <para>The assertions below are written to survive either behaviour on
/// purpose: they pin the INVARIANT (the report must never disagree with the
/// file) rather than the current outcome, so if this document ever regresses
/// to stalling, the gate still fails for the right reason.</para>
/// </summary>
public class FormerlyStallingDocumentTests
{
    private const string Fixture = "test-pdfs/pdfjs/issue15629.pdf";
    private const string Term = "Louise";

    private static string? FixturePath()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        if (dir == null) return null;
        var path = Path.Combine(dir.FullName, Fixture);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// The property that matters, and the one #1089 exists to make expressible:
    /// <b>excise must not claim a clean success on a document where the term
    /// survives.</b>
    ///
    /// <para>Before #1089 this was unsayable — the API returned an <c>int</c>,
    /// and this document returned 3 for one occurrence while leaving the name
    /// in the file. Whatever the engine does with this document, the REPORT
    /// must not describe it as clean.</para>
    /// </summary>
    [Fact]
    public void AStallingDocument_IsNeverReportedAsACleanSuccess()
    {
        var path = FixturePath();
        Assert.SkipWhen(path == null, $"{Fixture} not present (gitignored corpus)");

        using var doc = PdfDocument.Open(path!);
        var report = doc.RedactText(Term, drawBlackRect: false);

        // Guard: if the term is not in this document any more, the gate is
        // pinning nothing and must say so rather than passing.
        report.MatchesLocated.Should().BeGreaterThan(0,
            "the fixture must still contain the term, or this gate proves nothing");

        if (report.Survived > 0)
        {
            report.IsCleanSuccess.Should().BeFalse(
                "the term is STILL PRESENT after redaction — reporting that as success " +
                "is exactly the #1040 failure: a real name left in a real document " +
                "behind a black box");
        }
        else
        {
            // Removal succeeded. It may still have been destructive, and if so
            // the report must say so rather than printing a bare count.
            report.Pages.Any(p => p.Outcome == RedactionOutcome.RemovedVerified
                                  || p.Outcome == RedactionOutcome.DestructiveRemoval)
                .Should().BeTrue("every page that did work must classify what kind");
        }
    }

    /// <summary>
    /// The independent check. #1089's verification re-read is excise reading
    /// its own output, which cannot prove removal — so the saved bytes are
    /// scanned with the decompressing scanner, which is not the extractor.
    ///
    /// <para>This is deliberately an ASSERTION ABOUT AGREEMENT, not about
    /// removal: whatever excise reports, the file must match it. A report
    /// saying "clean" over a file that still holds the term is the defect;
    /// a report saying "survived" over a file that still holds it is correct
    /// behaviour on a document excise cannot yet redact.</para>
    /// </summary>
    [Fact]
    public void TheReportAgreesWithWhatIsActuallyInTheSavedFile()
    {
        var path = FixturePath();
        Assert.SkipWhen(path == null, $"{Fixture} not present (gitignored corpus)");

        using var doc = PdfDocument.Open(path!);
        var report = doc.RedactText(Term, drawBlackRect: false);

        using var ms = new MemoryStream();
        doc.Save(ms);
        var stillInFile = SavedPdfLeakScanner.FindTerm(ms.ToArray(), Term).Count > 0;

        if (report.IsCleanSuccess)
        {
            stillInFile.Should().BeFalse(
                "excise reported a clean redaction; the saved bytes must agree. " +
                "Disagreement here is a leak reported as success — #1040 exactly");
        }
    }
}
