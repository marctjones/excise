using System;
using System.Diagnostics;
using System.IO;
using AwesomeAssertions;
using Pdfe.Core.Authoring;
using Pdfe.Core.Graphics;
using Xunit;

namespace Pdfe.Core.Tests.Authoring;

/// <summary>
/// PDF/UA-1 conformance gate (#413): validates a representative
/// <see cref="PdfDocumentBuilder.Tagged"/> document with the veraPDF validator.
/// Skips when veraPDF or the embedding font isn't available (so local/dev runs
/// without them don't fail); CI installs both so it runs there.
/// </summary>
public class PdfUaConformanceTests
{
    private const string Dejavu = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";

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

    [Fact]
    public void TaggedBuilderOutput_IsPdfUa1Compliant()
    {
        Assert.SkipUnless(File.Exists(Dejavu), "DejaVuSans (embedding font) not installed");
        var verapdf = FindVeraPdf();
        Assert.SkipWhen(verapdf is null, "veraPDF not installed (~/verapdf/verapdf or PATH)");

        // A representative accessible document exercising every tagged block.
        var font = PdfFont.FromFile(Dejavu, 11);
        var pdf = PdfDocumentBuilder.Create()
            .Tagged().DefaultFont(font).Language("en-US").Title("Accessible Sample")
            .Heading("Application Form", 1)
            .Paragraph("Please complete all required fields. Café résumé.")
            .Heading("Details", 2)
            .BulletList(new[] { "First point", "Second point" })
            .NumberedList(new[] { "Step one", "Step two" })
            .KeyValue("Name", "Ada Lovelace")
            .Table(new[] { new[] { "Item", "Qty" }, new[] { "Widget", "3" } }, headerRow: true)
            .TextField("Full name", "fullname", required: true)
            .CheckBox("I agree to the terms", "agree")
            .Dropdown("Tier", new[] { "Basic", "Pro" }, "tier")
            .SaveToBytes();

        var path = Path.Combine(Path.GetTempPath(), $"pdfua_{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
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
            psi.ArgumentList.Add("ua1");
            psi.ArgumentList.Add(path);

            using var proc = Process.Start(psi)!;
            string report = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(120_000);

            report.Should().Contain("isCompliant=\"true\"",
                "the builder's Tagged() output must be PDF/UA-1 conformant. Report:\n" +
                report.Substring(0, Math.Min(report.Length, 4000)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
