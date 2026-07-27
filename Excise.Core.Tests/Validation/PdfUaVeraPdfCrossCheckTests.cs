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
/// removed. Skips cleanly when veraPDF is not installed (as on a dev box);
/// CI installs it, so the cross-check runs there.
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
            string report = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(120_000);
            return report.Contains("isCompliant=\"true\"", StringComparison.Ordinal);
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
