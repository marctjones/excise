using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1372 — the leak a SECOND text oracle found and the first could not.
///
/// <para>The redaction bench measured leaked text with mutool alone. mutool is
/// MuPDF, and excise keeps a line-end hyphen exactly as MuPDF does, so neither
/// forms a match across <c>Ander-</c> / <c>son</c>. excise removed everything
/// mutool agreed was present, mutool re-read the output and found nothing, and
/// the run was graded clean — while Poppler's <c>pdftotext</c>, which
/// de-hyphenates on reflow, read the party name straight out of a redacted
/// Supreme Court opinion.</para>
///
/// <para>That is CLAUDE.md's no-self-oracle rule in its sharpest form: it was
/// not enough that the oracle was independent of excise, because it shared
/// excise's blind spot. This test therefore uses the extractor that DISAGREES.
/// </para>
///
/// <para><b>What is asserted is the REPORT, not the removal.</b> Joining across
/// the hyphen closes the leak and was tried and reverted: a match spanning two
/// lines takes a removal box covering everything between them, which regressed
/// 7 collateral fixtures and #942's
/// <c>RedactingATerm_DestroysNothingRemoteFromAnyMatch</c>. The calibration for
/// this issue is that a miss is an improvement to make, while making redaction
/// WORSE is the one thing that must not happen. So until a wrapped match can
/// produce two boxes (one per line), excise must SAY the occurrence is still
/// there rather than report success over it.</para>
/// </summary>
public class HyphenWrappedRedactionReportingTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, ".git"))) d = d.Parent;
        return d!.FullName;
    }

    [Fact]
    public void AHyphenWrappedTerm_SurvivesRedaction_AndIsReportedRatherThanCalledClean()
    {
        Assert.SkipUnless(PdftotextTextExtractor.IsAvailable,
            "pdftotext not installed [requires: tool:pdftotext]");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable,
            "mutool not installed [requires: tool:mutool]");

        var source = Path.Combine(RepoRoot(), "test-pdfs", "smoke", "scotus-trump-v-anderson.pdf");
        Assert.SkipUnless(File.Exists(source), "smoke corpus absent [requires: corpus:smoke]");

        var output = Path.Combine(Path.GetTempPath(), $"excise-1372-{Guid.NewGuid():N}.pdf");
        try
        {
            RedactionReport report;
            int pageCount;
            using (var doc = PdfDocument.Open(File.ReadAllBytes(source)))
            {
                report = doc.RedactText("Anderson");
                pageCount = doc.PageCount;
                doc.Save(output);
            }

            // 1. The engine did real work and believes it finished.
            report.VerifiedRemovals.Should().BeGreaterThan(0,
                "sanity: the occurrences excise CAN match are genuinely removed");
            report.Survived.Should().Be(0,
                "sanity: nothing excise matched was left behind — the gap is the " +
                "occurrence it never matched at all");

            // 2. The oracle that SHARES excise's blind spot agrees it is clean.
            //    This is the reading that hid the leak for eleven days.
            var mutool = string.Concat(MutoolTextExtractor.ExtractAllPages(output, pageCount) ?? []);
            mutool.Should().NotContain("Anderson",
                "MuPDF keeps the line-end hyphen exactly as excise does, so it cannot " +
                "see what excise missed — a single extractor cannot report its own blind spot");

            // 3. The oracle that does NOT share it still reads the term. THIS is
            //    the leak, and it must remain demonstrable: if this line ever
            //    goes green on its own, the removal path changed, and the
            //    collateral ratchets must be re-read before believing it.
            var poppler = string.Concat(PdftotextTextExtractor.ExtractAllPages(output, pageCount) ?? []);
            poppler.Should().Contain("Anderson",
                "poppler de-hyphenates on reflow and reads the wrapped occurrence");

            // 4. THE ASSERTION THIS TEST EXISTS FOR. excise must not call that
            //    outcome a clean success — it must name the occurrence.
            report.IsCleanSuccess.Should().BeFalse(
                "a readable occurrence remains, so this is not a clean redaction");
            report.HyphenatedCandidates.Should().NotBeEmpty(
                "the wrapped occurrence must be SURFACED, not silently skipped — " +
                "reporting success over it is what made this leak invisible");
            report.HyphenatedCandidates.Should().Contain(
                c => c.BeforeBreak.Contains("Ander") && c.AfterBreak.StartsWith("son"),
                "and it must say how the page actually reads, so a reviewer can act");
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }
}
