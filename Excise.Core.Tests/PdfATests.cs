using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Tests.Fixtures;
using Xunit;

namespace Excise.Core.Tests;

/// <summary>
/// Verifies <see cref="PdfDocumentBuilder.PdfA"/> emits the document-level
/// structures PDF/A requires: an XMP packet with the pdfaid identifier, an sRGB
/// OutputIntent, a trailer /ID, an embedded font (no base-14), and — for
/// subset CID fonts — a /CIDSet (required by PDF/A-1b §6.3.5). Full conformance
/// is validated with veraPDF when present (both PDF/A-1b and -2b PASS). Uses
/// the DejaVu Sans fixture embedded in this assembly (#603).
/// </summary>
public class PdfATests
{
    private static byte[] BuildPdfA(PdfAConformance conformance)
    {
        var font = PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 11);
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
            // #925: stderr is redirected, so it MUST be drained concurrently —
            // veraPDF is chatty there, and a full 64KB pipe wedges the child,
            // which wedges ReadToEnd, which trips CI's 2-minute blame timer as
            // a "test host crash" on an innocent commit.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(120_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* gone */ }
            }
            string report = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();

            report.Should().Contain("isCompliant=\"true\"",
                $"the builder's PdfA({conformance}) output must be PDF/A-{flavour} conformant. Report:\n" +
                report.Substring(0, Math.Min(report.Length, 4000)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PdfA2B_RoundTripThroughCompressedWriter_IsConformant_PerVeraPdf()
    {
        var verapdf = FindVeraPdf();
        Assert.SkipWhen(verapdf is null, "veraPDF not installed (~/verapdf/verapdf or PATH)");

        using var doc = PdfDocument.Open(BuildPdfA(PdfAConformance.PdfA2B));
        var saved = doc.SaveToBytes();
        Encoding.Latin1.GetString(saved).Should().Contain("/Type /ObjStm",
            "PDF/A-2 permits object streams, so this validates the compressed writer path");

        var path = Path.Combine(Path.GetTempPath(), $"pdfa_2b_compressed_{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, saved);
        try
        {
            var report = RunVeraPdf(verapdf!, path, "2b");
            report.Should().Contain("isCompliant=\"true\"",
                "compressed writer output must remain PDF/A-2b conformant. Report:\n" +
                report.Substring(0, Math.Min(report.Length, 4000)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string RunVeraPdf(string verapdf, string path, string flavour)
    {
        var psi = new ProcessStartInfo(verapdf)
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
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(120_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* gone */ }
        }
        var report = stdoutTask.GetAwaiter().GetResult();
        _ = stderrTask.GetAwaiter().GetResult();
        return report;
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
