using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Authoring;
using Pdfe.Core.Graphics;
using Xunit;

namespace Pdfe.Core.Tests;

/// <summary>
/// Verifies <see cref="PdfDocumentBuilder.PdfA"/> emits the document-level
/// structures PDF/A requires: an XMP packet with the pdfaid identifier, an sRGB
/// OutputIntent, a trailer /ID, an embedded font (no base-14), and — for
/// subset CID fonts — a /CIDSet (required by PDF/A-1b §6.3.5). Full conformance
/// is validated with veraPDF when present (both PDF/A-1b and -2b PASS).
/// </summary>
public class PdfATests
{
    private const string Dejavu = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";

    private static byte[] BuildPdfA(PdfAConformance conformance)
    {
        var font = PdfFont.FromFile(Dejavu, 11);
        return PdfDocumentBuilder.Create()
            .Language("en-US")
            .Title("Archival Test")
            .DefaultFont(font)
            .PdfA(conformance)
            .Heading("Archival Test")
            .Paragraph("Body text — with an em dash and unicode: café.")
            .SaveToBytes();
    }

    [Fact]
    public void PdfA2B_EmitsXmpPdfaId_OutputIntent_AndTrailerId()
    {
        Assert.SkipUnless(File.Exists(Dejavu), "DejaVuSans (embedding font) not installed");
        var latin1 = Encoding.Latin1.GetString(BuildPdfA(PdfAConformance.PdfA2B));

        Assert.Contains("pdfaid:part>2", latin1);
        Assert.Contains("pdfaid:conformance>B", latin1);
        Assert.Contains("/OutputIntents", latin1);
        Assert.Contains("GTS_PDFA1", latin1);
        Assert.Contains("/ID", latin1);
    }

    [Fact]
    public void PdfA1B_EmitsPart1AndCidSet()
    {
        Assert.SkipUnless(File.Exists(Dejavu), "DejaVuSans (embedding font) not installed");
        var latin1 = Encoding.Latin1.GetString(BuildPdfA(PdfAConformance.PdfA1B));

        Assert.Contains("pdfaid:part>1", latin1);
        // PDF/A-1b requires a /CIDSet for the embedded subset CID font.
        Assert.Contains("/CIDSet", latin1);
    }

    [Fact]
    public void NewDocument_AlwaysGetsATrailerId()
    {
        var bytes = PdfDocumentBuilder.Create().Heading("Hi").SaveToBytes();
        Assert.Contains("/ID", Encoding.Latin1.GetString(bytes));
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA1B, "1b")]
    [InlineData(PdfAConformance.PdfA2B, "2b")]
    public void PdfA_Output_IsConformant_PerVeraPdf(PdfAConformance conformance, string flavour)
    {
        Assert.SkipUnless(File.Exists(Dejavu), "DejaVuSans (embedding font) not installed");
        var verapdf = FindVeraPdf();
        Assert.SkipWhen(verapdf is null, "veraPDF not installed (~/verapdf/verapdf or PATH)");

        var path = Path.Combine(Path.GetTempPath(), $"pdfa_{flavour}_{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, BuildPdfA(conformance));
        try
        {
            var psi = new ProcessStartInfo(verapdf!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("xml");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(flavour);
            psi.ArgumentList.Add(path);

            using var proc = Process.Start(psi)!;
            string report = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(120_000);

            report.Should().Contain("isCompliant=\"true\"",
                $"the builder's PdfA({conformance}) output must be PDF/A-{flavour} conformant. Report:\n" +
                report.Substring(0, Math.Min(report.Length, 4000)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string? FindVeraPdf()
    {
        var home = Environment.GetEnvironmentVariable("HOME") ?? "";
        var local = Path.Combine(home, "verapdf", "verapdf");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var p = Path.Combine(dir, "verapdf");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
