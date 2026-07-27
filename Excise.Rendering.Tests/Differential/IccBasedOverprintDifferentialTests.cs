using System;
using System.IO;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering;
using Excise.Rendering.Differential;
using Excise.Rendering.Tests.Visual;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Independent-oracle verification for ICCBased-CMYK (N=4) overprint (#803,
/// follow-up to #634). The oracle is Ghostscript with
/// <c>-dOverprint=/simulate</c> — the only reference renderer in the harness
/// that simulates PDF overprint on an RGB output device. Before #803 an
/// ICCBased-CMYK fill knocked out even under /OP /op /OPM 1; excise must now
/// move TOWARD the simulate oracle's overprint result on such content.
/// Mirrors OverprintDifferentialTests' generated-fixture shape, swapping only
/// the overprinting fill's colour space to ICCBased CMYK.
/// </summary>
public class IccBasedOverprintDifferentialTests
{
    private const int Dpi = 72;

    [Fact(Timeout = 60000)]
    public void IccBasedCmykOverprint_LandsOnGhostscriptSimulateSideOfTheKnockout()
    {
        Assert.SkipWhen(!GhostscriptReferenceRenderer.IsAvailable,
            "Ghostscript is not installed; the overprint-simulate oracle is unavailable.");

        var overprintPdf = WriteTempPdf(OverprintContent);
        var knockoutPdf = WriteTempPdf(KnockoutContent);
        try
        {
            using var gsOverprint = RenderWithSimulate(overprintPdf);
            using var gsKnockout = RenderWithSimulate(knockoutPdf);
            Assert.SkipWhen(gsOverprint == null || gsKnockout == null,
                "Ghostscript rejected -dOverprint=/simulate (needs gs >= 9.54).");

            // Oracle sanity: gs itself must render overprint differently from
            // knockout, or it is not actually simulating.
            var gsOverprintOverlap = gsOverprint!.GetPixel(100, 200);
            var gsKnockoutOverlap = gsKnockout!.GetPixel(100, 200);
            ChannelDistance(gsOverprintOverlap, gsKnockoutOverlap).Should().BeGreaterThan(100,
                "the oracle must discriminate overprint from knockout before it can judge excise");

            using var doc = PdfDocument.Open(File.ReadAllBytes(overprintPdf));
            using var excise = new SkiaRenderer().RenderPage(
                doc.GetPage(1),
                new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White });
            var exciseOverlap = excise.GetPixel(100, 200);

            var toOverprint = ChannelDistance(exciseOverlap, gsOverprintOverlap);
            var toKnockout = ChannelDistance(exciseOverlap, gsKnockoutOverlap);
            toOverprint.Should().BeLessThan(toKnockout / 2,
                $"excise overlap {exciseOverlap} must sit on the overprint side " +
                $"(gs overprint {gsOverprintOverlap}, gs knockout {gsKnockoutOverlap})");
        }
        finally
        {
            TryDelete(overprintPdf);
            TryDelete(knockoutPdf);
        }
    }

    // ------------------------------------------------------------------

    // Cyan DeviceCMYK backdrop, then a yellow square painted through the
    // ICCBased CMYK space with overprint (/OP /op /OPM 1).
    private const string OverprintContent =
        "1 0 0 0 k 20 20 160 160 re f\n" +
        "/ICCCS cs /GSop gs 0 0 1 0 scn 60 60 80 80 re f\n";

    // Same fill, no overprint gs → knockout.
    private const string KnockoutContent =
        "1 0 0 0 k 20 20 160 160 re f\n" +
        "/ICCCS cs 0 0 1 0 scn 60 60 80 80 re f\n";

    private static string WriteTempPdf(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-icc-overprint-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, IccBasedCmykOverprintTests.BuildIccBasedOverprintPdf(content, deviceCmykGroup: false));
        return path;
    }

    private static SKBitmap? RenderWithSimulate(string pdfPath)
        => GhostscriptReferenceRenderer.TryRenderPageWithOverprintSimulation(pdfPath, 1, Dpi).Bitmap;

    private static int ChannelDistance(SKColor a, SKColor b)
        => Math.Max(
            Math.Abs(a.Red - b.Red),
            Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue)));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
