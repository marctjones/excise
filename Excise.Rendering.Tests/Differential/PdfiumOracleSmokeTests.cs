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

    /// <summary>
    /// #1369 — pdfium's API is not thread-safe, and nothing in managed code can
    /// catch what happens when it is called concurrently: the process takes a
    /// SIGSEGV and every result of the run is lost.
    ///
    /// This is a real regression, not a hypothetical. The redaction bench began
    /// measuring its four tools through Parallel.ForEach on 2026-08-29, each
    /// branch rendering through PdfiumNativeReferenceRenderer; on 2026-09-05 the
    /// test host died in CPDF_Color::GetColorRef() under FPDF_LoadPage, thirteen
    /// minutes into the run. pdfium had been in the bench since 2026-08-25 and
    /// was fine while it was called from one thread.
    ///
    /// Without the serialising lock this test kills the test host, which is a
    /// FAILED run and exactly the intended signal — a crashed host cannot be
    /// mistaken for a pass. It is deliberately not a soft assertion.
    /// </summary>
    [Fact]
    public void ConcurrentRenders_DoNotCorruptPdfiumsGlobalState()
    {
        Assert.SkipUnless(PdfiumNativeReferenceRenderer.IsAvailable,
            "PDFium not present — run scripts/download-pdfium.sh");
        var paths = new[] { "irs-w9.pdf", "irs-1040.pdf", "cdc-vis-covid-19.pdf" }
            .Select(f => FindRepoFile("test-pdfs", "smoke", f))
            .Where(p => p != null).Select(p => p!).ToArray();
        Assert.SkipWhen(paths.Length == 0, "smoke corpus not present");

        // Four threads is what the bench uses (REDACTION_BENCH_PARALLELISM
        // default); the failure needs no more than two.
        const int threads = 4, rounds = 6;
        var work = Enumerable.Range(0, threads * rounds)
            .Select(i => paths[i % paths.Length]).ToArray();
        var inks = new double[work.Length];

        System.Threading.Tasks.Parallel.For(0, work.Length,
            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads },
            i =>
            {
                using var bmp = PdfiumNativeReferenceRenderer.RenderPage(work[i], 1, Dpi);
                inks[i] = bmp == null ? -1 : InkCoverage(bmp);
            });

        inks.Should().NotContain(-1,
            "every concurrent render must succeed; a null means pdfium failed under contention");
        inks.Should().OnlyContain(v => v > 0.001,
            "a blank render would make this vacuous");

        // Same file rendered on different threads must give the same answer:
        // corruption that does not crash still shows up as a differing result.
        foreach (var group in work.Select((p, i) => (p, ink: inks[i])).GroupBy(x => x.p))
        {
            var spread = group.Max(x => x.ink) - group.Min(x => x.ink);
            spread.Should().BeLessThan(1e-9,
                $"concurrent renders of {Path.GetFileName(group.Key)} must be identical, not merely non-crashing");
        }
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
