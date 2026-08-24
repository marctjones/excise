using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1141 — the benchmark's VISUAL axis: ink an independent renderer draws in the
/// redaction region after a BOX-SUPPRESSED removal. The text axes cannot see
/// ink; a redaction can be clean on every text carrier and still show a vector
/// path or raster pixel. This gate pins the axis's wiring — before/after sanity
/// and the "not measured" sentinels — with no gitignored corpus.
/// </summary>
public class RedactionBenchmarkInkAxisTests
{
    private static string WriteTemp(byte[] pdf)
    {
        var p = Path.Combine(Path.GetTempPath(), $"ink-axis-{System.Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, pdf);
        return p;
    }

    /// <summary>One page, "CONFIDENTIAL" in black at a known baseline.</summary>
    private static byte[] PageWithSecret()
    {
        var content = "BT /F1 24 Tf 100 700 Td (CONFIDENTIAL) Tj ET\n";
        var body = Encoding.Latin1.GetBytes(content);
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
          + "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        W($"4 0 obj\n<< /Length {body.Length} >>\nstream\n"); ms.Write(body); W("\nendstream\nendobj\n");
        W("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        W("trailer\n<< /Root 1 0 R /Size 6 >>\n%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void InkAxis_SeesInkBefore_AndBlankAfterACleanTextRemoval()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        var path = WriteTemp(PageWithSecret());
        try
        {
            var (before, after) = RedactionBenchmarkRunner.MeasureInkAxis(path, "CONFIDENTIAL", "excise");

            before.Should().BeGreaterThan(0.02,
                "the secret is inked in the region before redaction — if this is ~0 the "
              + "region box is wrong and the 'after' number would be meaningless");
            after.Should().BeLessThan(0.01,
                "box-suppressed text removal must leave the region visually blank; "
              + "residual ink here would be a leak no text axis can see (#1141)");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void InkAxis_IsNotMeasured_ForACompetitorOrAnAbsentTerm()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        var path = WriteTemp(PageWithSecret());
        try
        {
            // We cannot suppress a competitor's coverage box, so its region reads
            // as ITS ink, not the residual — recorded as not-measured, never guessed.
            RedactionBenchmarkRunner.MeasureInkAxis(path, "CONFIDENTIAL", "pymupdf")
                .Should().Be((-1d, -1d));

            // A term not on page 1 has no region to measure.
            RedactionBenchmarkRunner.MeasureInkAxis(path, "NOTPRESENT", "excise")
                .Should().Be((-1d, -1d));
        }
        finally { File.Delete(path); }
    }
}
