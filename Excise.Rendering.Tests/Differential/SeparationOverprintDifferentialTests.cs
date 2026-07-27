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
/// Independent-oracle verification for Separation / DeviceN overprint (#634).
///
/// The oracle is Ghostscript with <c>-dOverprint=/simulate</c> — the only
/// reference renderer in the harness that simulates PDF overprint on an RGB
/// output device. A spot colour whose tint transform maps to yellow
/// (0 0 1 0) painted over a cyan backdrop (1 0 0 0 k) must, with overprint on,
/// keep the cyan colorant (green overlap) rather than knock it out (yellow).
/// Excise must move TOWARD the simulate oracle on that overlap.
///
/// Crucially, the oracle renders /OPM 0 and /OPM 1 IDENTICALLY for a
/// Separation fill — the "unnamed colorants stay put" rule is independent of
/// overprint mode — so excise must overprint under /OPM 0 too.
/// </summary>
public class SeparationOverprintDifferentialTests
{
    private const int Dpi = 72;

    [Fact(Timeout = 60000)]
    public void SeparationOverprintFixture_LandsOnGhostscriptSimulateSideOfTheKnockout()
    {
        Assert.SkipWhen(!GhostscriptReferenceRenderer.IsAvailable,
            "Ghostscript is not installed; the overprint-simulate oracle is unavailable.");

        var overprintPdf = WriteTempPdf(SepOverprintContent);
        var knockoutPdf = WriteTempPdf(SepKnockoutContent);
        try
        {
            using var gsOverprint = RenderWithSimulate(overprintPdf);
            using var gsKnockout = RenderWithSimulate(knockoutPdf);
            Assert.SkipWhen(gsOverprint == null || gsKnockout == null,
                "Ghostscript rejected -dOverprint=/simulate (needs gs >= 9.54).");

            // Oracle sanity: gs must itself distinguish the overprinted overlap
            // from the knockout, or it is not actually simulating overprint.
            var gsOverprintOverlap = gsOverprint!.GetPixel(100, 200);
            var gsKnockoutOverlap = gsKnockout!.GetPixel(100, 200);
            ChannelDistance(gsOverprintOverlap, gsKnockoutOverlap).Should().BeGreaterThan(100,
                "the oracle must discriminate Separation overprint from knockout before it can judge excise");

            var exciseOverlap = RenderExciseOverlap(overprintPdf);

            var toOverprint = ChannelDistance(exciseOverlap, gsOverprintOverlap);
            var toKnockout = ChannelDistance(exciseOverlap, gsKnockoutOverlap);
            toOverprint.Should().BeLessThan(toKnockout / 2,
                $"excise Separation overlap {exciseOverlap} must sit on the overprint side " +
                $"(gs overprint {gsOverprintOverlap}, gs knockout {gsKnockoutOverlap})");
        }
        finally
        {
            TryDelete(overprintPdf);
            TryDelete(knockoutPdf);
        }
    }

    [Fact(Timeout = 60000)]
    public void SeparationOverprint_UnderOpm0_StillOverprints_LikeGhostscriptSimulate()
    {
        Assert.SkipWhen(!GhostscriptReferenceRenderer.IsAvailable,
            "Ghostscript is not installed; the overprint-simulate oracle is unavailable.");

        var opm0Pdf = WriteTempPdf(SepOverprintOpm0Content);
        var knockoutPdf = WriteTempPdf(SepKnockoutContent);
        try
        {
            using var gsOpm0 = RenderWithSimulate(opm0Pdf);
            using var gsKnockout = RenderWithSimulate(knockoutPdf);
            Assert.SkipWhen(gsOpm0 == null || gsKnockout == null,
                "Ghostscript rejected -dOverprint=/simulate (needs gs >= 9.54).");

            var gsOpm0Overlap = gsOpm0!.GetPixel(100, 200);
            var gsKnockoutOverlap = gsKnockout!.GetPixel(100, 200);
            // The whole point: for a Separation, gs overprints under OPM 0 too,
            // so its OPM 0 overlap is NOT the knockout.
            ChannelDistance(gsOpm0Overlap, gsKnockoutOverlap).Should().BeGreaterThan(100,
                "oracle: a Separation overprints under /OPM 0 as well (unnamed colorants stay put regardless of OPM)");

            var exciseOverlap = RenderExciseOverlap(opm0Pdf);
            var toOverprint = ChannelDistance(exciseOverlap, gsOpm0Overlap);
            var toKnockout = ChannelDistance(exciseOverlap, gsKnockoutOverlap);
            toOverprint.Should().BeLessThan(toKnockout / 2,
                $"excise must overprint a Separation under /OPM 0 (overlap {exciseOverlap}, " +
                $"gs overprint {gsOpm0Overlap}, gs knockout {gsKnockoutOverlap})");
        }
        finally
        {
            TryDelete(opm0Pdf);
            TryDelete(knockoutPdf);
        }
    }

    [Fact(Timeout = 60000)]
    public void DeviceNOverprintFixture_LandsOnGhostscriptSimulateSideOfTheKnockout()
    {
        Assert.SkipWhen(!GhostscriptReferenceRenderer.IsAvailable,
            "Ghostscript is not installed; the overprint-simulate oracle is unavailable.");

        var overprintPdf = WriteTempPdf(DevNOverprintContent);
        var knockoutPdf = WriteTempPdf(DevNKnockoutContent);
        try
        {
            using var gsOverprint = RenderWithSimulate(overprintPdf);
            using var gsKnockout = RenderWithSimulate(knockoutPdf);
            Assert.SkipWhen(gsOverprint == null || gsKnockout == null,
                "Ghostscript rejected -dOverprint=/simulate (needs gs >= 9.54).");

            var gsOverprintOverlap = gsOverprint!.GetPixel(100, 200);
            var gsKnockoutOverlap = gsKnockout!.GetPixel(100, 200);
            ChannelDistance(gsOverprintOverlap, gsKnockoutOverlap).Should().BeGreaterThan(100,
                "the oracle must discriminate DeviceN overprint from knockout before it can judge excise");

            var exciseOverlap = RenderExciseOverlap(overprintPdf);
            var toOverprint = ChannelDistance(exciseOverlap, gsOverprintOverlap);
            var toKnockout = ChannelDistance(exciseOverlap, gsKnockoutOverlap);
            toOverprint.Should().BeLessThan(toKnockout / 2,
                $"excise DeviceN overlap {exciseOverlap} must sit on the overprint side " +
                $"(gs overprint {gsOverprintOverlap}, gs knockout {gsKnockoutOverlap})");
        }
        finally
        {
            TryDelete(overprintPdf);
            TryDelete(knockoutPdf);
        }
    }

    // ------------------------------------------------------------------

    private const string Background = "1 0 0 0 k 20 20 160 160 re f\n";
    private const string SepOverprintContent = Background + "/GSop gs /CSsep cs 1 scn 60 60 80 80 re f\n";
    private const string SepOverprintOpm0Content = Background + "/GSop0 gs /CSsep cs 1 scn 60 60 80 80 re f\n";
    private const string SepKnockoutContent = Background + "/CSsep cs 1 scn 60 60 80 80 re f\n";
    private const string DevNOverprintContent = Background + "/GSop gs /CSdevn cs 1 scn 60 60 80 80 re f\n";
    private const string DevNKnockoutContent = Background + "/CSdevn cs 1 scn 60 60 80 80 re f\n";

    private const string Resources =
        "/ExtGState << " +
        "/GSop << /Type /ExtGState /OP true /op true /OPM 1 >> " +
        "/GSop0 << /Type /ExtGState /OP true /op true /OPM 0 >> " +
        ">> " +
        "/ColorSpace << " +
        "/CSsep [ /Separation /MyYellow /DeviceCMYK " +
        "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0 1 0] /N 1 >> ] " +
        "/CSdevn [ /DeviceN [ /MySpot ] /DeviceCMYK " +
        "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0 1 0] /N 1 >> ] " +
        ">>";

    private static SKColor RenderExciseOverlap(string pdfPath)
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(pdfPath));
        using var excise = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White });
        return excise.GetPixel(100, 200);
    }

    private static string WriteTempPdf(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-sep-overprint-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, OverprintRenderingTests.BuildSinglePagePdf(content, Resources, deviceCmykGroup: false));
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
