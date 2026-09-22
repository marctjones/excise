using System;
using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1750 — a multi-word term that wraps across an ORDINARY line break (no
/// hyphen) is the generalization of <see cref="HyphenWrappedRedactionReportingTests"/>
/// to the far more common wrap shape: a name split between two words rather
/// than mid-word by a hyphen. Before this, <c>excise redact in out "Betty Mary"</c>
/// on such a page printed "Redacted 0 occurrence(s)" and exited 0 — a silent
/// false success, not a visible miss.
///
/// <para>Uses a self-contained synthetic fixture (two <c>BT…ET</c> text runs
/// positioned 14pt apart, no hyphen glyph) rather than a corpus PDF, because
/// the exact wrap shape needs to be pinned precisely and independently of
/// whatever real documents happen to be downloaded locally.</para>
///
/// <para><b>What is asserted is the REPORT, not the removal</b> — same
/// calibration as #1372: joining across an ordinary line break still needs a
/// removal box PER LINE (a change to match geometry, #942's lesson), so until
/// that lands excise must SAY the occurrence survives rather than report
/// success over it.</para>
/// </summary>
public class WordWrapRedactionReportingTests
{
    [Fact]
    public void AWordWrappedTerm_SurvivesRedaction_AndIsReportedRatherThanCalledClean()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable,
            "mutool not installed [requires: tool:mutool]");

        var pdfBytes = BuildTwoLinePdf();
        var output = Path.Combine(Path.GetTempPath(), $"excise-1750-{Guid.NewGuid():N}.pdf");
        try
        {
            RedactionReport report;
            using (var doc = PdfDocument.Open(pdfBytes))
            {
                // sanity: FindTextMatches structurally cannot see this —
                // no space is inserted at a line wrap, so "Betty Mary" never
                // matches the concatenated "...BettyMary...".
                doc.GetPage(1).Text.Should().Contain("Betty").And.Contain("Mary");

                report = doc.RedactText("Betty Mary");
                doc.Save(output);
            }

            // 1. excise located nothing (the matcher structurally cannot see
            // the occurrence), so nothing was verified removed and nothing
            // "survived" in the ordinary sense either.
            report.Survived.Should().Be(0,
                "nothing excise matched was left behind — the gap is the occurrence " +
                "it never matched at all");

            // 2. THE ASSERTION THIS TEST EXISTS FOR. excise must not call that
            // outcome a clean success — it must name the occurrence.
            report.IsCleanSuccess.Should().BeFalse(
                "a readable occurrence remains, so this is not a clean redaction");
            report.WordWrapCandidates.Should().NotBeEmpty(
                "the wrapped occurrence must be SURFACED, not silently skipped — reporting " +
                "success over it (\"Redacted 0 occurrence(s)\", exit 0) is what made this " +
                "leak class invisible");
            report.WordWrapCandidates.Should().Contain(
                c => c.BeforeBreak == "Betty" && c.AfterBreak == "Mary",
                "and it must say how the page actually reads, so a reviewer can act");

            // 3. INDEPENDENT oracle: mutool — not excise's own extractor —
            // confirms the name is genuinely still readable in the saved
            // output, so the report above matches reality rather than a
            // detector that flags something that was actually removed.
            var mutoolText = MutoolTextExtractor.ExtractPage(output, 1) ?? "";
            mutoolText.Should().Contain("Betty").And.Contain("Mary",
                "mutool, independent of excise's own content-stream extractor, still reads " +
                "both halves of the wrapped name in the saved output");
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    /// <summary>
    /// Two text-showing operators 14pt apart — "...signed by Betty" then,
    /// on the next line, "Mary on behalf of..." — with NO hyphen glyph
    /// anywhere, so this is squarely the ordinary line-wrap case #1750 is
    /// about, distinct from #1372's hyphen-wrapped word.
    /// </summary>
    private static byte[] BuildTwoLinePdf()
    {
        const string line1 = "This document was signed by Betty";
        const string line2 = "Mary on behalf of the company";
        var content =
            $"BT /F1 12 Tf 72 700 Td ({line1}) Tj ET\n" +
            $"BT /F1 12 Tf 72 686 Td ({line2}) Tj ET\n";

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n");
        sb.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
