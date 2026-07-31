using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Makes Apache PDFBox an actual oracle (#857), and pins the page-selection bug
/// it uncovered (#868).
///
/// The Differential/ directory ships six reference renderers but the suite only
/// ever exercised four: PdfBoxReferenceRenderer was referenced by ZERO test
/// files, so it silently drifted — asking it for page 1 of a 6-page PDF
/// returned page 6. Nothing noticed, because nothing ran it.
///
/// PDFBox is worth wiring up specifically because it is the only oracle here
/// that is NOT a C/C++ rasteriser: mutool, pdftocairo, pdftoppm and Ghostscript
/// all descend from that lineage, while PDFBox is independent Java. A bug the C
/// family shares is exactly what a fourth cousin cannot catch.
///
/// pdfium is still not covered: pdfium_test has no Homebrew formula and needs a
/// Chromium-side build, so it cannot be provisioned the way
/// scripts/download-pdfbox.sh provisions this.
///
/// METRIC CHOICE — measured, not assumed. At 150 DPI on these forms, PDFBox and
/// mutool (no shared code) differ on 14-19% of PIXELS purely from text
/// antialiasing, while agreeing on ink coverage to within 0.004-0.009 absolute.
/// A per-pixel gate loose enough to tolerate Java2D-vs-Skia AA would stop
/// catching anything. Ink coverage still catches what matters: blank page,
/// missing text, wrong scale, wrong page. The 0.02 threshold is 2x the worst
/// observed cross-renderer delta, not a number tuned until this went green.
/// </summary>
public class PdfBoxOracleDifferentialTests
{
    private const double MaxInkCoverageDelta = 0.02;
    private const int Dpi = 150;

    public static TheoryData<string> SmokeCorpusPdfs() => new()
    {
        "irs-w9.pdf",
        "irs-1040.pdf",
        "cdc-vis-covid-19.pdf",
    };

    [Theory]
    [MemberData(nameof(SmokeCorpusPdfs))]
    public void ExciseAndPdfBox_PutTheSameInkOnPageOne(string fileName)
    {
        Assert.SkipUnless(PdfBoxReferenceRenderer.IsAvailable,
            "PDFBox not configured — run scripts/download-pdfbox.sh and set EXCISE_PDFBOX_JAR");

        var path = FindRepoFile("test-pdfs", "smoke", fileName);
        Assert.SkipWhen(path == null, "smoke corpus not present (scripts/download-smoke-corpus.sh)");

        using var reference = PdfBoxReferenceRenderer.RenderPage(path!, 1, Dpi);
        reference.Should().NotBeNull(
            "PDFBox reported IsAvailable, so it must render a well-formed government form");

        using var mine = RenderWithExcise(path!, 1, Dpi);
        mine.Should().NotBeNull("excise must render a page PDFBox can render");

        var referenceInk = InkCoverage(reference!);
        referenceInk.Should().BeGreaterThan(0.001,
            "a blank PDFBox reference would make the comparison below vacuous");

        var mineInk = InkCoverage(mine!);
        var delta = Math.Abs(mineInk - referenceInk);
        delta.Should().BeLessThan(MaxInkCoverageDelta,
            $"excise and PDFBox — an independent Java implementation, not another " +
            $"descendant of the C rasteriser lineage the other oracles share — should put " +
            $"substantially the same ink on {fileName} page 1. " +
            $"excise={mineInk:F4} pdfbox={referenceInk:F4} delta={delta:F4}");
    }

    /// <summary>
    /// #868 regression guard: PdfBoxReferenceRenderer used to select its output
    /// by newest-file, which on a multi-page PDF is the LAST page. irs-w9.pdf
    /// has 6 pages with clearly different ink (p1 0.0780, p6 0.0360), so asking
    /// for page 1 and receiving page 6 is detectable without a golden image.
    /// </summary>
    [Fact]
    public void PdfBoxRenderPage_ReturnsTheRequestedPage_NotTheLastOne()
    {
        Assert.SkipUnless(PdfBoxReferenceRenderer.IsAvailable,
            "PDFBox not configured — run scripts/download-pdfbox.sh and set EXCISE_PDFBOX_JAR");

        var path = FindRepoFile("test-pdfs", "smoke", "irs-w9.pdf");
        Assert.SkipWhen(path == null, "smoke corpus not present");

        using var page1 = PdfBoxReferenceRenderer.RenderPage(path!, 1, Dpi);
        using var page6 = PdfBoxReferenceRenderer.RenderPage(path!, 6, Dpi);
        page1.Should().NotBeNull();
        page6.Should().NotBeNull();

        var ink1 = InkCoverage(page1!);
        var ink6 = InkCoverage(page6!);

        ink1.Should().NotBeApproximately(ink6, 0.005,
            "pages 1 and 6 of this form carry visibly different amounts of ink; " +
            "identical values mean the renderer is handing back the same page for both");

        using var excisePage1 = RenderWithExcise(path!, 1, Dpi);
        Math.Abs(ink1 - InkCoverage(excisePage1!)).Should().BeLessThan(MaxInkCoverageDelta,
            "PDFBox's 'page 1' must be the same page excise calls page 1");
    }

    // ---------------------------------------------------------------- helpers --

    private static SKBitmap? RenderWithExcise(string path, int pageNumber, int dpi)
    {
        using var doc = Excise.Core.Document.PdfDocument.Open(File.ReadAllBytes(path));
        var renderer = new SkiaRenderer();
        return renderer.RenderPage(doc.GetPage(pageNumber), new RenderOptions { Dpi = dpi });
    }

    /// Fraction of "ink" (dark) pixels — insensitive to where antialiasing
    /// lands, sensitive to whether the content is there at all.
    private static double InkCoverage(SKBitmap bmp)
    {
        long dark = 0;
        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Alpha > 8 && (c.Red + c.Green + c.Blue) / 3 < 128) dark++;
        }
        return (double)dark / (bmp.Width * (long)bmp.Height);
    }

    private static string? FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
