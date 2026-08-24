using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1121 — the two open-source competitor adapters the benchmark gained (Adobe,
/// proprietary, is deliberately out). Each pins the adapter's DEFINING property,
/// gated on the tool being present.
/// </summary>
public class CompetitorAdapterTests
{
    private const string Secret = "Farrar";

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, ".git"))) d = d.Parent;
        return d!.FullName;
    }

    private static byte[] Fixture()
    {
        var content = Encoding.Latin1.GetBytes($"BT /F1 24 Tf 72 700 Td (Name: Louise {Secret}) Tj ET\n");
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

    private static (int exit, string outp) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit(120_000);
        return (p.ExitCode, o.GetAwaiter().GetResult() + e.GetAwaiter().GetResult());
    }

    [Fact]
    public void RasterBaseline_LeavesNoExtractableTextAnywhere_TheAnchor()
    {
        var venv = Path.Combine(RepoRoot(), "tools", "vendor", "xray-venv", "bin", "python");
        Assert.SkipUnless(File.Exists(venv) && MutoolReferenceRenderer.IsAvailable, "venv/mutool absent");

        var input = Path.Combine(Path.GetTempPath(), $"ras-in-{Guid.NewGuid():N}.pdf");
        var output = Path.Combine(Path.GetTempPath(), $"ras-out-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(input, Fixture());
            var script = Path.Combine(RepoRoot(), "scripts", "benchmark-adapters", "redact-raster.py");
            var (exit, _) = Run(venv, script, input, output, Secret);
            exit.Should().Be(0);

            // The anchor's whole point: rasterising everything means NO text
            // survives — perfect Leak bought with total Collateral.
            var text = MutoolTextExtractor.ExtractPage(output, 1) ?? "";
            text.Trim().Should().BeEmpty(
                "the raster baseline turns the page into an image — no extractable text, " +
                "the perfect-Leak / total-Collateral end of the trade-off curve");
        }
        finally { File.Delete(input); File.Delete(output); }
    }

    [Fact]
    public void ItextPdfSweep_RemovesTheTerm_ButKeepsItsNeighbours()
    {
        var jarDir = Path.Combine(RepoRoot(), "tools", "vendor", "itext");
        var driver = Path.Combine(RepoRoot(), "scripts", "ItextRedactor.java");
        Assert.SkipUnless(Directory.Exists(jarDir) && Directory.GetFiles(jarDir, "*.jar").Length > 0
            && File.Exists(driver) && PdfBoxReferenceRedactor.IsAvailable && MutoolReferenceRenderer.IsAvailable,
            "itext jars / java / mutool absent");

        var java = Environment.GetEnvironmentVariable("EXCISE_JAVA_COMMAND")
                   ?? (File.Exists("/opt/homebrew/opt/openjdk/bin/java") ? "/opt/homebrew/opt/openjdk/bin/java" : "java");
        var cp = string.Join(Path.PathSeparator, Directory.GetFiles(jarDir, "*.jar"));
        var input = Path.Combine(Path.GetTempPath(), $"it-in-{Guid.NewGuid():N}.pdf");
        var output = Path.Combine(Path.GetTempPath(), $"it-out-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(input, Fixture());
            var (exit, _) = Run(java, "--class-path", cp, driver, input, output, Secret);
            exit.Should().Be(0);

            var text = MutoolTextExtractor.ExtractPage(output, 1) ?? "";
            text.Should().NotContain(Secret, "pdfSweep must remove the term");
            text.Should().Contain("Louise",
                "iText's content-level cleanup keeps neighbouring text — unlike the whole-operator " +
                "PDFBox reference and unlike the raster baseline");
        }
        finally { File.Delete(input); File.Delete(output); }
    }
}
