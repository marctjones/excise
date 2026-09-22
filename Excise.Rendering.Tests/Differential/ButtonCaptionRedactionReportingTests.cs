using System;
using System.IO;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1760 — a pushbutton's CUSTOM CAPTION, painted as real glyphs in its
/// <c>/AP/N</c> appearance stream, survived a term redaction that reported
/// success. <c>test-pdfs/pdfjs/issue15053.pdf</c> has a button whose caption
/// reads "This Button can be toggled" — no <c>/V</c> value at all, since a
/// button's <c>/V</c> is an on/off STATE NAME, not text.
///
/// <para><b>Root cause, two layers deep.</b> <c>TextExtractor.EmitFormFieldLetters</c>
/// unconditionally skipped every Button field ("never human-readable text"),
/// so the caption was never even a candidate MATCH — <c>page.Letters</c>
/// never contained it. <c>InteractiveRedactionScrubber.ScrubFormFields</c>
/// then ALSO skipped every Button field outright, so even if a match had
/// existed, the #1098 appearance-stream rewrite that would have removed it
/// never ran. Both blind spots trace to the same stale assumption ("buttons
/// have no readable text") that #669 already disproved for Signature fields
/// — a pushbutton with a custom caption is exactly that same shape.</para>
///
/// <para>This regressed the redaction bench from <c>RemovedWithResidue</c>
/// (2026-09-07) to fully <c>Recoverable</c> (2026-09-21): the redact command
/// reported <c>removed: 2 × JavaScript action(s)</c> and nothing about the
/// button, while "This Button can be toggled" sat fully intact and rendered
/// plainly by mutool and pdftotext.</para>
/// </summary>
public class ButtonCaptionRedactionReportingTests
{
    [Fact]
    public void RedactingTheCaptionWord_RemovesItFromThePushbuttonsAppearance()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable,
            "mutool not installed [requires: tool:mutool]");
        Assert.SkipUnless(PdftotextTextExtractor.IsAvailable,
            "pdftotext not installed [requires: tool:pdftotext]");

        var source = TestRepoLayout.FindFile("test-pdfs", "pdfjs", "issue15053.pdf");
        Assert.SkipWhen(source == null, TestRepoLayout.AbsenceReason(
            "issue15053.pdf", "test-pdfs/pdfjs/issue15053.pdf"));

        var output = Path.Combine(Path.GetTempPath(), $"excise-1760-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var doc = PdfDocument.Open(File.ReadAllBytes(source!)))
            {
                // sanity: BEFORE this fix, "toggled" never matched at all on
                // this page's extracted text for the button's occurrence —
                // FindTextMatches on excise's own extraction is not the
                // oracle here (that would be self-referential); it only
                // establishes what this test is measuring.
                doc.RedactText("toggled");
                doc.Save(output);
            }

            // INDEPENDENT oracles — not excise's own extractor. mutool and
            // pdftotext both render/extract the pushbutton's appearance
            // stream directly, exactly how the leak was originally found.
            var mutoolText = MutoolTextExtractor.ExtractPage(output, 1) ?? "";
            mutoolText.Should().NotContain("toggled",
                "SECURITY: mutool still reads the pushbutton's caption glyphs — " +
                "'This Button can be toggled' must not survive a redaction of 'toggled'");

            var poppler = PdftotextTextExtractor.ExtractPage(output, 1) ?? "";
            poppler.Should().NotContain("toggled",
                "SECURITY: pdftotext (a second, independent extractor) still reads it");

            // The rest of the caption — text NOT asked to be removed — must
            // still be there. A redaction that also erased "This Button can
            // be" would be #942's over-removal in a new shape.
            mutoolText.Should().Contain("This Button can be",
                "the caption's surviving words must not have been destroyed alongside the term");

            SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(output), "toggled").Should().BeEmpty(
                "a saved-bytes scan (decompresses streams) must not find the term either — " +
                "this is what /MK /CA alone being scrubbed while /AP/N kept the glyphs would miss");
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }
}
