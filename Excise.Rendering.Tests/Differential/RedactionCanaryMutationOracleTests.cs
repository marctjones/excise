using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1199: prove the independent detectors themselves reject an output mutation
/// that leaves a unique, visible canary in place. A green structural-redaction
/// test alone cannot demonstrate this property.
/// </summary>
public sealed class RedactionCanaryMutationOracleTests
{
    private const string Canary = "VISIBLECANARYX";

    [Fact]
    public void IndependentOracles_RejectVisibleCanaryMutation_AndAcceptStructuralRemoval()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        var input = TempPdf();
        var output = TempPdf();
        try
        {
            File.WriteAllBytes(input, BuildPdf());

            // The intentionally unredacted input is the mutation: every
            // independent detector must call it a leak.
            SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(input), Canary).Should().NotBeEmpty();
            (MutoolTextExtractor.ExtractPage(input, 1) ?? "").Should().Contain(Canary);
            var visibleMutation = RedactionBenchmarkRunner.MeasureVisualReadable(input, input, Canary);
            Assert.SkipWhen(visibleMutation < 0, "tesseract unavailable or did not produce a readable canary");
            visibleMutation.Should().Be(1, "independent render plus OCR must reject a visible canary (#1199)");

            using (var doc = PdfDocument.Open(File.ReadAllBytes(input)))
            {
                doc.RedactText(Canary).VerifiedRemovals.Should().Be(1);
                doc.Save(output);
            }

            SavedPdfLeakScanner.FindTerm(File.ReadAllBytes(output), Canary).Should().BeEmpty();
            (MutoolTextExtractor.ExtractPage(output, 1) ?? "").Should().NotContain(Canary);
            RedactionBenchmarkRunner.MeasureVisualReadable(input, output, Canary).Should().Be(0,
                "independent render plus OCR must accept only the structurally redacted output");
        }
        finally { TryDelete(input); TryDelete(output); }
    }

    private static string TempPdf() => Path.Combine(Path.GetTempPath(), $"excise-canary-{Guid.NewGuid():N}.pdf");
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    private static byte[] BuildPdf()
    {
        const string content = "BT /F1 48 Tf 72 400 Td (VISIBLECANARYX) Tj ET\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(text.ToString()));
            text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(text.ToString());
        text.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }
}
