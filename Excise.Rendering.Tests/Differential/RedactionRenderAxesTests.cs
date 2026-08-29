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
/// The two RENDER axes the benchmark gained: does the SURVIVING page still render
/// correctly after redaction (not just qpdf-valid and text-present), and is the
/// secret still READABLE IN PIXELS (the visual leak the text oracles miss). Both
/// run per-tool on the tool's output.
/// </summary>
public class RedactionRenderAxesTests
{
    private const string Secret = "SECRETWORD";
    private const string Jbig2Secret = "V1HH";

    // "Keep AAA SECRETWORD keep BBB" — surrounding words on the same line so a
    // render-fidelity regression (mispositioned survivors) would show as pixel
    // change outside the redacted region.
    private static byte[] Fixture(string text)
    {
        var content = Encoding.Latin1.GetBytes($"BT /F1 20 Tf 72 700 Td ({text}) Tj ET\n");
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
          + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        W($"4 0 obj\n<< /Length {content.Length} >>\nstream\n"); ms.Write(content); W("\nendstream\nendobj\n");
        W("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        W("trailer\n<< /Root 1 0 R /Size 6 >>\n%%EOF\n");
        return ms.ToArray();
    }

    // The secret repeated down the page, each line flanked by survivor words. This
    // is the shape that produced the false 4.8% render-delta on a 41×-"COVID" page:
    // masking only the FIRST occurrence let the other redaction boxes read as
    // surviving-content damage.
    private static byte[] MultiOccurrenceFixture(int lines)
    {
        var sb = new StringBuilder("BT /F1 18 Tf ");
        for (var i = 0; i < lines; i++)
        {
            var y = 740 - i * 30;
            sb.Append($"1 0 0 1 72 {y} Tm (keep AAA {Secret} keep BBB) Tj ");
        }
        sb.Append("ET\n");
        var content = Encoding.Latin1.GetBytes(sb.ToString());
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
          + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        W($"4 0 obj\n<< /Length {content.Length} >>\nstream\n"); ms.Write(content); W("\nendstream\nendobj\n");
        W("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        W("trailer\n<< /Root 1 0 R /Size 6 >>\n%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void SurvivingRenderDelta_MasksEveryOccurrence_NotJustTheFirst()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        var input = Path.Combine(Path.GetTempPath(), $"rax-multi-in-{Guid.NewGuid():N}.pdf");
        var output = Path.Combine(Path.GetTempPath(), $"rax-multi-out-{Guid.NewGuid():N}.pdf");
        try
        {
            // 10 occurrences of the secret, each with intact survivor words.
            File.WriteAllBytes(input, MultiOccurrenceFixture(10));
            using (var doc = PdfDocument.Open(File.ReadAllBytes(input)))
            {
                doc.RedactText(Secret);
                using var fs = File.Create(output);
                doc.Save(fs);
            }

            var delta = RedactionBenchmarkRunner.MeasureSurvivingRenderDelta(input, output, Secret);
            delta.Should().BeGreaterThanOrEqualTo(0, "the axis must have measured");
            // Before the fix this spiked because 9 of the 10 covering boxes were
            // counted as surviving-content change. With all occurrences masked the
            // genuinely-surviving words (keep/AAA/keep/BBB) are unchanged.
            delta.Should().BeLessThan(0.02,
                "every occurrence's covering box must be masked — the other redactions " +
                "are not surviving-content damage");
        }
        finally { File.Delete(input); File.Delete(output); }
    }

    [Fact]
    public void CleanGlyphRemoval_LeavesSurvivingRenderIntact_AndTheSecretUnreadable()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        var input = Path.Combine(Path.GetTempPath(), $"rax-in-{Guid.NewGuid():N}.pdf");
        var output = Path.Combine(Path.GetTempPath(), $"rax-out-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(input, Fixture($"KEEP AAA {Secret} keep BBB"));
            using (var doc = PdfDocument.Open(File.ReadAllBytes(input)))
            {
                doc.RedactText(Secret);           // excise glyph-level removal + black box
                using var fs = File.Create(output);
                doc.Save(fs);
            }

            // Surviving content (KEEP/AAA/keep/BBB) must render essentially
            // unchanged — a low delta. #942/#1100 would spike this.
            var delta = RedactionBenchmarkRunner.MeasureSurvivingRenderDelta(input, output, Secret);
            delta.Should().BeGreaterThanOrEqualTo(0, "the axis must have measured");
            delta.Should().BeLessThan(0.02,
                "the rest of the page must render as before — the redaction may not move or " +
                "corrupt surviving content");

            // The secret must not be legible in pixels (glyphs gone + black box).
            var readable = RedactionBenchmarkRunner.MeasureVisualReadable(input, output, Secret);
            if (readable >= 0)   // -1 when tesseract absent
                readable.Should().Be(0, "excise removed the glyphs and covered the region — nothing to OCR");
        }
        finally { File.Delete(input); File.Delete(output); }
    }

    [Fact]
    public void Jbig2Scan_RegionRedaction_PreservesPixelsOutsideTheRequestedTerm()
    {
        // #1195/#1197: a full-page JBIG2 scan is a primary redaction case.
        // Whole-image removal cannot pass this test: every pixel outside the
        // matched term's boxes is compared by Ghostscript, an independent
        // renderer, before versus after the saved output.
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var root = LocateRepoRoot();
        Assert.SkipUnless(root != null, "repository root unavailable");
        var input = Path.Combine(root!, "test-pdfs", "pdfjs", "issue12963.pdf");
        Assert.SkipUnless(File.Exists(input), "JBIG2 corpus fixture absent [requires: corpus:pdfjs]");

        var output = Path.Combine(Path.GetTempPath(), $"rax-jbig2-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var doc = PdfDocument.Open(input))
            {
                var report = doc.RedactText(Jbig2Secret);
                report.ImageRegionsRedacted.Should().BeGreaterThan(0,
                    "the requested scan region must be destroyed in place");
                report.ImagesDroppedWhole.Should().Be(0,
                    "a decodable JBIG2 scan must not be discarded wholesale");
                doc.Save(output);
            }

            var delta = RedactionBenchmarkRunner.MeasureSurvivingRenderDelta(input, output, Jbig2Secret);
            delta.Should().BeGreaterThanOrEqualTo(0,
                "the independent surviving-render axis must be measurable on this fixture");
            delta.Should().BeLessThan(0.01,
                "only requested term regions and their redaction boxes may change; the rest of a full-page scan must survive");

            var extracted = MutoolTextExtractor.ExtractPage(output, 1);
            extracted.Should().NotBeNull("mutool must read the saved redacted output");
            extracted!.Should().NotContain(Jbig2Secret,
                "the invisible OCR layer is independently unextractable after redaction");
        }
        finally { File.Delete(output); }
    }

    [Fact]
    public void VisualReadableAxis_DetectsATermStillLegibleInPixels()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        // A "redaction" that left the secret plainly rendered (output == input) is
        // the visual leak the text oracles would still flag here — but the point is
        // the OCR axis READS it. Text extraction being clean or not is irrelevant
        // to what this axis proves: that a term visible in pixels is caught.
        var pdf = Path.Combine(Path.GetTempPath(), $"rax-vis-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(pdf, Fixture($"HEADER {Secret} FOOTER"));
            var readable = RedactionBenchmarkRunner.MeasureVisualReadable(pdf, pdf, Secret);
            Assert.SkipUnless(readable >= 0, "tesseract not installed");
            readable.Should().Be(1,
                "a term still rendered in the output must be OCR-legible — this is the axis that " +
                "catches a secret surviving as pixels when no text carrier holds it");
        }
        finally { File.Delete(pdf); }
    }

    private static string? LocateRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "excise.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }
}
