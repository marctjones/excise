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
/// Independent-oracle verification for DeviceCMYK overprint (#634).
///
/// The oracle is Ghostscript with <c>-dOverprint=/simulate</c> — the only
/// reference renderer in the harness that simulates PDF overprint on an RGB
/// output device (mutool and pdftocairo apply overprint only when rendering
/// to CMYK/spot targets, and the repo's default Ghostscript invocation does
/// not simulate it either — verified empirically on the Ghent GWG010/GWG011
/// patches). Excise must move TOWARD the simulate oracle on overprint
/// content while staying put everywhere else.
/// </summary>
public class OverprintDifferentialTests
{
    private const int Dpi = 72;

    [Fact(Timeout = 60000)]
    public void GeneratedOverprintFixture_LandsOnGhostscriptSimulateSideOfTheKnockout()
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

            // Oracle sanity: gs itself must render the overprint overlap
            // differently from the knockout, or it is not actually simulating.
            var gsOverprintOverlap = gsOverprint!.GetPixel(100, 200);
            var gsKnockoutOverlap = gsKnockout!.GetPixel(100, 200);
            ChannelDistance(gsOverprintOverlap, gsKnockoutOverlap).Should().BeGreaterThan(100,
                "the oracle must discriminate overprint from knockout before it can judge excise");

            using var doc = PdfDocument.Open(File.ReadAllBytes(overprintPdf));
            using var excise = new SkiaRenderer().RenderPage(
                doc.GetPage(1),
                new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White });
            var exciseOverlap = excise.GetPixel(100, 200);

            // The defining check: excise's overprinted overlap must be far
            // closer to the oracle's overprint result than to the knockout
            // it used to produce. (Exact colour equality is not expected —
            // gs and excise use different CMYK preview conversions.)
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

    [Fact(Timeout = 60000)]
    public void GhentOverprintModePatch_Opm1XIsHidden_Opm0PatchStaysKnockedOut()
    {
        var path = FindGhentPatch("GWG011_Overprint-Mode_x3.pdf");
        Assert.SkipWhen(path == null,
            "Ghent GWG011 overprint-mode fixture is not present locally (run scripts/download-test-pdfs.sh).");

        using var doc = PdfDocument.Open(path!);
        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White });

        // Right patch (OPM 1): an X painted 0 0 .1 .5 k with /OP /op true over
        // a .9 .1 .9 0 backdrop. Under OPM 1 the zero C/M keep the backdrop's
        // colorants and the merged colour equals the patch background
        // (.9 .1 .1 .5) exactly — the X must disappear into the square.
        // Device coords at 72 DPI (page 255.118 x 141.732, deviceY = 141.73 - pdfY).
        var xArmCenter = bitmap.GetPixel(191, 57);   // crossing point of the X
        var xArmDiagonal = bitmap.GetPixel(191, 63); // on a diagonal stroke
        var patchInterior = bitmap.GetPixel(215, 34); // inside square, off the X
        ChannelDistance(xArmCenter, patchInterior).Should().BeLessThanOrEqualTo(25,
            $"the OPM 1 X ({xArmCenter}) must blend into the patch ({patchInterior}); " +
            "without overprint it renders as a plainly visible gray X (distance > 100)");
        ChannelDistance(xArmDiagonal, patchInterior).Should().BeLessThanOrEqualTo(25,
            "every arm of the OPM 1 X must be hidden");

        // Left patch (OPM 0) is the over-application trap: its background is
        // painted WITH overprint but /OPM 0, so its zero components must
        // still knock out the X underneath — the patch stays uniform. A
        // renderer that applies OPM 1 semantics unconditionally fails here.
        var leftXArm = bitmap.GetPixel(64, 57);
        var leftInterior = bitmap.GetPixel(88, 34);
        ChannelDistance(leftXArm, leftInterior).Should().BeLessThanOrEqualTo(6,
            "OPM 0 zero components must PAINT (knock out), leaving the left patch uniform");
    }

    [Fact(Timeout = 60000)]
    public void GhentOverprintModePatch_MovesTowardGhostscriptSimulate()
    {
        var path = FindGhentPatch("GWG011_Overprint-Mode_x3.pdf");
        Assert.SkipWhen(path == null,
            "Ghent GWG011 overprint-mode fixture is not present locally (run scripts/download-test-pdfs.sh).");
        Assert.SkipWhen(!GhostscriptReferenceRenderer.IsAvailable,
            "Ghostscript is not installed; the overprint-simulate oracle is unavailable.");

        using var reference = RenderWithSimulate(path!);
        Assert.SkipWhen(reference == null,
            "Ghostscript rejected -dOverprint=/simulate (needs gs >= 9.54).");

        using var doc = PdfDocument.Open(path!);
        using var excise = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White });

        // OPM 1 patch interior (right square). Pre-#634 this region measured a
        // mean channel difference of ~22 against the simulate oracle (the X
        // renders as an un-overprinted gray cross); with OPM 1 implemented it
        // measures ~6 (residual = CMYK preview differences between engines).
        var mean = MeanAbsoluteChannelDifference(excise, reference!, 165, 31, 218, 83);
        mean.Should().BeLessThan(12.0,
            "the OPM 1 patch must track the Ghostscript overprint-simulate oracle");
    }

    // ------------------------------------------------------------------

    private const string OverprintContent =
        "1 0 0 0 k 20 20 160 160 re f\n" +
        "/GSop gs 0 0 1 0 k 60 60 80 80 re f\n";

    private const string KnockoutContent =
        "1 0 0 0 k 20 20 160 160 re f\n" +
        "0 0 1 0 k 60 60 80 80 re f\n";

    private const string Resources =
        "/ExtGState << /GSop << /Type /ExtGState /OP true /op true /OPM 1 >> >>";

    private static string WriteTempPdf(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-overprint-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, OverprintRenderingTests.BuildSinglePagePdf(content, Resources, deviceCmykGroup: false));
        return path;
    }

    private static SKBitmap? RenderWithSimulate(string pdfPath)
        => GhostscriptReferenceRenderer.TryRenderPageWithOverprintSimulation(pdfPath, 1, Dpi).Bitmap;

    private static int ChannelDistance(SKColor a, SKColor b)
        => Math.Max(
            Math.Abs(a.Red - b.Red),
            Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue)));

    private static double MeanAbsoluteChannelDifference(
        SKBitmap a, SKBitmap b, int left, int top, int right, int bottom)
    {
        // The two engines may disagree by a pixel on page rounding (256 vs
        // 255 wide at 72 DPI); the probe region stays inside both.
        right = Math.Min(right, Math.Min(a.Width, b.Width));
        bottom = Math.Min(bottom, Math.Min(a.Height, b.Height));
        long total = 0;
        long samples = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var pa = a.GetPixel(x, y);
                var pb = b.GetPixel(x, y);
                total += Math.Abs(pa.Red - pb.Red) +
                         Math.Abs(pa.Green - pb.Green) +
                         Math.Abs(pa.Blue - pb.Blue);
                samples += 3;
            }
        }

        return samples == 0 ? 0 : (double)total / samples;
    }

    private static string? FindGhentPatch(string fileName)
        => FindRepoFile(
            "test-pdfs", "ghent", "extracted", "patches",
            "Ghent_PDF_Output_Suite_V50_Patches", "Categories", "1-CMYK", "Patches",
            fileName);

    private static string? FindRepoFile(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
