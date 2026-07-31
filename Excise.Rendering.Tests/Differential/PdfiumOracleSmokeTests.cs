using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// PDFium as a real oracle (#857). See PdfiumNativeReferenceRenderer for why
/// this drives the library rather than pdfium_test (that binary is not
/// distributed and needs a Chromium build).
///
/// PDFium is the Chrome/Foxit lineage — independent of MuPDF, Poppler,
/// Ghostscript and PDFBox, and by far the most widely deployed PDF renderer.
///
/// Compared on ink coverage rather than per-pixel, for the reason measured in
/// PdfBoxOracleDifferentialTests: independent rasterisers disagree heavily on
/// text antialiasing while agreeing closely on how much ink lands on the page.
/// </summary>
public class PdfiumOracleSmokeTests
{
    private const double MaxInkCoverageDelta = 0.02;
    private const int Dpi = 150;

    public static TheoryData<string> SmokeCorpusPdfs() => new()
    {
        "irs-w9.pdf", "irs-1040.pdf", "cdc-vis-covid-19.pdf",
    };

    [Theory]
    [MemberData(nameof(SmokeCorpusPdfs))]
    public void ExciseAndPdfium_PutTheSameInkOnPageOne(string fileName)
    {
        Assert.SkipUnless(PdfiumNativeReferenceRenderer.IsAvailable,
            "PDFium not present — run scripts/download-pdfium.sh");
        var path = FindRepoFile("test-pdfs", "smoke", fileName);
        Assert.SkipWhen(path == null, "smoke corpus not present");

        using var reference = PdfiumNativeReferenceRenderer.RenderPage(path!, 1, Dpi);
        reference.Should().NotBeNull("PDFium reported available, so it must render a government form");

        using var mine = RenderWithExcise(path!, 1, Dpi);
        mine.Should().NotBeNull();

        var refInk = InkCoverage(reference!);
        refInk.Should().BeGreaterThan(0.001, "a blank reference would make this vacuous");

        var delta = Math.Abs(InkCoverage(mine!) - refInk);
        delta.Should().BeLessThan(MaxInkCoverageDelta,
            $"excise and PDFium (Chrome's renderer) should put substantially the same ink on " +
            $"{fileName} page 1; delta={delta:F4}");
    }

    /// <summary>
    /// #868's lesson applied pre-emptively: pdfium clamps out-of-range pages
    /// silently in some APIs, so pin that we get the page we asked for.
    /// irs-w9.pdf pages 1 and 6 carry visibly different ink.
    /// </summary>
    [Fact]
    public void PdfiumRenderPage_ReturnsTheRequestedPage()
    {
        Assert.SkipUnless(PdfiumNativeReferenceRenderer.IsAvailable, "PDFium not present");
        var path = FindRepoFile("test-pdfs", "smoke", "irs-w9.pdf");
        Assert.SkipWhen(path == null, "smoke corpus not present");

        using var p1 = PdfiumNativeReferenceRenderer.RenderPage(path!, 1, Dpi);
        using var p6 = PdfiumNativeReferenceRenderer.RenderPage(path!, 6, Dpi);
        p1.Should().NotBeNull(); p6.Should().NotBeNull();

        InkCoverage(p1!).Should().NotBeApproximately(InkCoverage(p6!), 0.005,
            "pages 1 and 6 differ visibly; identical ink means the same page came back twice");

        using var excise1 = RenderWithExcise(path!, 1, Dpi);
        Math.Abs(InkCoverage(p1!) - InkCoverage(excise1!)).Should().BeLessThan(MaxInkCoverageDelta,
            "PDFium's page 1 must be the page excise calls page 1");
    }

    private static SKBitmap? RenderWithExcise(string path, int pageNumber, int dpi)
    {
        using var doc = Excise.Core.Document.PdfDocument.Open(File.ReadAllBytes(path));
        return new SkiaRenderer().RenderPage(doc.GetPage(pageNumber), new RenderOptions { Dpi = dpi });
    }

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
            var c = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(c)) return c;
            dir = dir.Parent;
        }
        return null;
    }
}
