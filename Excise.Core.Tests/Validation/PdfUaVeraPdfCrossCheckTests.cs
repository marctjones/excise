using System;
using System.Diagnostics;
using System.IO;
using AwesomeAssertions;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Tests.Fixtures;
using Excise.Core.Validation;
using Xunit;

namespace Excise.Core.Tests.Validation;

/// <summary>
/// No-self-oracle cross-check (#772): where the reference PDF/UA validator
/// (veraPDF) is available, excise's <see cref="PdfUaValidator"/> verdict must
/// AGREE with it on controlled fixtures whose expected verdict is known by
/// construction — a conformant builder document, and a document with /Lang
/// removed. Skips when veraPDF is not installed, and that skip turns the
/// core-oracle-tool-skips gate red (#1781): a skipped cross-check is not a pass.
/// </summary>
public class PdfUaVeraPdfCrossCheckTests
{
    private static byte[] WellTaggedBytes()
    {
        var font = PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 11);
        return PdfDocumentBuilder.Create()
            .Tagged().DefaultFont(font).Language("en-US").Title("Accessible Sample")
            .Heading("Overview", 1)
            .Paragraph("Body text with an accent: café.")
            .Table(new[] { new[] { "Item", "Qty" }, new[] { "Widget", "3" } }, headerRow: true)
            .SaveToBytes();
    }

    [Fact]
    public void ExciseVerdict_AgreesWithVeraPdf_OnConformantFixture()
    {
        var verapdf = FindVeraPdf();
        Assert.SkipWhen(verapdf is null, "veraPDF not installed (~/verapdf/verapdf or PATH)");

        var bytes = WellTaggedBytes();
        bool exciseConformant = PdfUaValidator.Validate(PdfDocument.Open(bytes)).CheckedSubsetConformant;
        bool veraConformant = VeraPdfSaysConformant(verapdf!, bytes);

        exciseConformant.Should().BeTrue("the builder fixture is conformant by construction");
        veraConformant.Should().BeTrue("veraPDF must agree the builder fixture is PDF/UA-1 conformant");
        exciseConformant.Should().Be(veraConformant, "excise and veraPDF must agree on the conformant fixture");
    }

    [Fact]
    public void ExciseVerdict_AgreesWithVeraPdf_OnUntaggedFixture()
    {
        var verapdf = FindVeraPdf();
        Assert.SkipWhen(verapdf is null, "veraPDF not installed (~/verapdf/verapdf or PATH)");

        // Remove the structure tree, then re-save so the on-disk file is what both
        // validators see. A document that claims to be tagged with no
        // /StructTreeRoot is unambiguously non-conformant to both validators.
        var doc = PdfDocument.Open(WellTaggedBytes());
        doc.Catalog.Remove("StructTreeRoot");
        var bytes = doc.SaveToBytes();

        bool exciseConformant = PdfUaValidator.Validate(PdfDocument.Open(bytes)).CheckedSubsetConformant;
        bool veraConformant = VeraPdfSaysConformant(verapdf!, bytes);

        exciseConformant.Should().BeFalse("a document without /StructTreeRoot violates a checked Error rule");
        veraConformant.Should().BeFalse("veraPDF must also reject a document without a structure tree");
    }

    private static bool VeraPdfSaysConformant(string verapdf, byte[] pdf)
    {
        var path = Path.Combine(Path.GetTempPath(), $"xcheck_{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
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
            psi.ArgumentList.Add("ua1");
            psi.ArgumentList.Add(path);

            using var proc = Process.Start(psi)!;
            // #925: drain both pipes concurrently — see PdfATests for the
            // failure this prevents.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(120_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* gone */ }
                // #1781: a killed run has no verdict. Returning false here made
                // the NEGATIVE test (…_OnUntaggedFixture) pass on a timeout.
                throw new TimeoutException($"veraPDF ({verapdf}) gave no verdict within 120 s; the cross-check is undecided, not non-conformant.");
            }
            string report = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();
            // #1781: only an explicit verdict counts. A crash, an empty report or
            // an unparsable file must not read as "veraPDF says non-conformant".
            if (report.Contains("isCompliant=\"true\"", StringComparison.Ordinal)) return true;
            if (report.Contains("isCompliant=\"false\"", StringComparison.Ordinal)) return false;
            throw new InvalidOperationException(
                $"veraPDF ({verapdf}) exited {proc.ExitCode} without an isCompliant verdict; the cross-check is undecided. " +
                $"stderr: {stderr[..Math.Min(stderr.Length, 400)]}");
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
