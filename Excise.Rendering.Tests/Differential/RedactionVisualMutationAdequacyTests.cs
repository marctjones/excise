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
/// #1201 adequacy check: the visual evidence must catch both a target left
/// readable and an untargeted survivor damaged outside the redaction region.
/// The visible-target mutation itself is pinned in
/// <see cref="RedactionCanaryMutationOracleTests"/>; this adds the distinct
/// collateral/geometry failure mode.
/// </summary>
public sealed class RedactionVisualMutationAdequacyTests
{
    private const string Secret = "VISUALGOLDSECRET";
    private const string Keep = "SURVIVORWORD";

    [Fact]
    public void SurvivingRenderAxis_RejectsUntargetedGlyphRemovalMutation()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");
        var input = TempPdf();
        var output = TempPdf();
        try
        {
            File.WriteAllBytes(input, BuildPdf($"{Keep} {Secret} {Keep}"));
            using (var document = PdfDocument.Open(File.ReadAllBytes(input)))
            {
                document.RedactText(Secret).VerifiedRemovals.Should().Be(1);
                // Mutation: the target is properly removed, but unrelated
                // visible words are removed too. A target-only leak detector
                // would accept this; the surviving-render axis must not.
                document.RedactText(Keep).VerifiedRemovals.Should().Be(1);
                document.Save(output);
            }

            var delta = RedactionBenchmarkRunner.MeasureSurvivingRenderDelta(input, output, Secret);
            delta.Should().BeGreaterThan(0.005,
                "removing untargeted visible words outside the secret mask is a rendering/fidelity failure");
        }
        finally { TryDelete(input); TryDelete(output); }
    }

    private static string TempPdf() => Path.Combine(Path.GetTempPath(), $"excise-visual-mutation-{Guid.NewGuid():N}.pdf");
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    private static byte[] BuildPdf(string text)
    {
        var content = $"BT /F1 28 Tf 72 400 Td ({text}) Tj ET\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = Encoding.Latin1.GetByteCount(builder.ToString());
            builder.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(builder.ToString());
        builder.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets) builder.Append($"{offset:D10} 00000 n \n");
        builder.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
    }
}
