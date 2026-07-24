using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering;
using Excise.Rendering.Differential;
using Excise.Rendering.Tests.Visual;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Independent-oracle verification for Type 3 (CharProc) font rendering
/// (#514, epic #512). Type 3 rendering was previously exercised only by
/// synthetic in-memory fixtures that excise both builds and checks — i.e.
/// excise was its own oracle. These tests render checked-in real Type 3
/// corpus PDFs and compare against a reference that is NOT excise: the
/// poppler/cairo reference PNG shipped alongside the fixture, and (when
/// installed) a live pdftocairo render. The pdf.js Type 3 fixtures, which
/// had no assertions at all, are wired in as non-blank render smoke.
/// </summary>
public class Type3RenderingDifferentialTests
{
    private const int Dpi = 150;

    // Cross-engine (cairo vs excise) antialiasing and hinting differ, so this
    // uses the same tolerance the corpus oracle uses for a passing page.
    private const double MaxDifferingPixelFraction = 0.10;
    private const double MaxMeanAbsoluteError = 32.0;

    [Fact]
    public void Type3_PopplerFixture_MatchesCheckedInCairoReference()
    {
        var pdf = FindRepoFile("test-pdfs", "poppler", "tests", "type3.pdf");
        var refPng = FindRepoFile("test-pdfs", "poppler", "tests", "type3.pdf-0-cairo-ref.png");
        Assert.SkipWhen(pdf == null || refPng == null,
            "poppler type3.pdf fixture or its cairo reference PNG is not present locally.");

        using var doc = PdfDocument.Open(pdf!);
        using var excise = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White });
        using var reference = VisualAssertions.LoadPng(refPng!);

        // The cairo reference DPI is not carried through the pipeline; match
        // excise's raster to the reference's pixel dimensions before comparing.
        using var aligned = DifferentialMetrics.ResizeMatch(excise, reference.Width, reference.Height);
        var report = DifferentialMetrics.Compare(aligned, reference);

        report.DifferingPixelFraction.Should().BeLessThan(MaxDifferingPixelFraction,
            $"excise's Type 3 rendering of {Path.GetFileName(pdf)} must match the poppler/cairo " +
            $"reference within the corpus gate (differing={report.DifferingPixelFraction:P2}, " +
            $"MAE={report.MeanAbsoluteError:F1})");
        report.MeanAbsoluteError.Should().BeLessThan(MaxMeanAbsoluteError);
    }

    [Fact]
    public void Type3_PopplerFixture_MatchesLivePdftocairo()
    {
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed.");
        var pdf = FindRepoFile("test-pdfs", "poppler", "tests", "type3.pdf");
        Assert.SkipWhen(pdf == null, "poppler type3.pdf fixture is not present locally.");

        using var doc = PdfDocument.Open(pdf!);
        using var excise = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White });
        using var reference = PdftocairoReferenceRenderer.RenderPage(pdf!, 1, Dpi);
        Assert.SkipWhen(reference == null, "pdftocairo declined to render the fixture.");

        using var aligned = DifferentialMetrics.ResizeMatch(excise, reference!.Width, reference.Height);
        var report = DifferentialMetrics.Compare(aligned, reference);

        report.DifferingPixelFraction.Should().BeLessThan(MaxDifferingPixelFraction,
            $"excise's Type 3 rendering must match a live pdftocairo render " +
            $"(differing={report.DifferingPixelFraction:P2}, MAE={report.MeanAbsoluteError:F1})");
        report.MeanAbsoluteError.Should().BeLessThan(MaxMeanAbsoluteError);
    }

    [Theory]
    [InlineData("Type3WordSpacing.pdf")]
    [InlineData("ContentStreamNoCycleType3insideType3.pdf")]
    [InlineData("ContentStreamCycleType3insideType3.pdf")]
    public void Type3_PdfjsFixture_RendersNonBlank(string fixture)
    {
        var pdf = FindRepoFile("test-pdfs", "pdfjs", fixture);
        Assert.SkipWhen(pdf == null, $"pdf.js fixture {fixture} is not present locally.");

        using var doc = PdfDocument.Open(pdf!);
        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White });

        // The Type 3 glyphs must actually paint — a blank page would mean the
        // CharProcs silently failed (the cycle fixtures must terminate, not hang
        // or blank). This is not a self-oracle: it asserts non-emptiness, not a
        // excise-defined pixel baseline.
        InkFraction(bitmap).Should().BeGreaterThan(0.001,
            $"{fixture} must render visible Type 3 glyphs, not a blank page");
    }

    // ---- #514 d0/d1 metrics + d1 bbox clipping: generated spec fixtures ----
    // Spec fixtures are PRIMARY evidence (unit tests in SkiaRendererTests);
    // the tests below CORROBORATE the same generated fixtures against
    // reference renderers that are not excise. Reference agreement, probed
    // before implementation:
    //   - d1 bbox clip: pdftocairo AND Ghostscript both clip to the declared
    //     box (all-zero box: poppler+mutool don't clip; gs blanks the glyph —
    //     so the zero-box case is asserted only in the unit tests).
    //   - missing /Widths → d0 wx advance: Ghostscript advances by wx;
    //     poppler and mutool substitute other fallbacks (bbox-derived / 1em),
    //     so only gs corroborates that case.

    [Fact]
    public void Type3_d1BBoxClip_MatchesPdftocairo()
    {
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed.");
        AssertD1BBoxClipAgainstReference(
            path => PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi), "pdftocairo");
    }

    [Fact]
    public void Type3_d1BBoxClip_MatchesGhostscript()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "Ghostscript not installed.");
        AssertD1BBoxClipAgainstReference(
            path => GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi), "Ghostscript");
    }

    private static void AssertD1BBoxClipAgainstReference(
        Func<string, SKBitmap?> renderReference, string referenceName)
    {
        // 24pt Type 3 glyph at (100,120): d1 declares a 300x300-unit bbox
        // (7.2pt) but the CharProc paints a 500x700-unit rect (12 x 16.8pt).
        // Everything outside the declared box must be clipped away.
        var pdfData = SkiaRendererTests.CreateType3FixturePdf(
            "0 0 0 rg BT /F1 24 Tf 100 120 Td <41> Tj ET",
            new[] { ("A", "500 0 0 0 300 300 d1 0 0 500 700 re f") },
            encodingDifferences: "65 /A",
            widthsClause: "/FirstChar 65 /LastChar 65 /Widths [500] ");

        WithTempPdf(pdfData, path =>
        {
            using var reference = renderReference(path);
            Assert.SkipWhen(reference == null, $"{referenceName} declined to render the fixture.");

            using var doc = PdfDocument.Open(pdfData);
            using var excise = new SkiaRenderer().RenderPage(
                doc.GetPage(1),
                new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White });

            foreach (var (bitmap, who) in new[] { (reference!, referenceName), (excise, "excise") })
            {
                DarkPixelsInPtRect(bitmap, 100.5f, 120.5f, 106.5f, 126.5f).Should().BeGreaterThan(20,
                    $"{who} must paint the glyph inside its declared d1 bbox");
                DarkPixelsInPtRect(bitmap, 100.5f, 129f, 111f, 135.5f).Should().BeLessThan(5,
                    $"{who} must clip marks above the declared d1 bbox");
                DarkPixelsInPtRect(bitmap, 108.5f, 120.5f, 111.5f, 126.5f).Should().BeLessThan(5,
                    $"{who} must clip marks right of the declared d1 bbox");
            }
        });
    }

    [Fact]
    public void Type3_MissingWidthsWxAdvance_MatchesGhostscript()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "Ghostscript not installed.");

        // No /Widths: the advance comes from the CharProc's d0 wx (500 units
        // = 12pt at 24pt). Each glyph paints a 400-unit (9.6pt) rect, so two
        // 'A's span x [100..109.6] and [112..121.6] with a gap between.
        var pdfData = SkiaRendererTests.CreateType3FixturePdf(
            "0 0 0 rg BT /F1 24 Tf 100 120 Td <4141> Tj ET",
            new[] { ("A", "500 0 d0 0 0 400 700 re f") },
            encodingDifferences: "65 /A",
            widthsClause: "");

        WithTempPdf(pdfData, path =>
        {
            using var reference = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
            Assert.SkipWhen(reference == null, "Ghostscript declined to render the fixture.");

            using var doc = PdfDocument.Open(pdfData);
            using var excise = new SkiaRenderer().RenderPage(
                doc.GetPage(1),
                new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White });

            foreach (var (bitmap, who) in new[] { (reference!, "Ghostscript"), (excise, "excise") })
            {
                DarkPixelsInPtRect(bitmap, 113f, 121f, 120.5f, 135f).Should().BeGreaterThan(20,
                    $"{who} must advance the second glyph by the d0 wx metric when /Widths is absent");
                DarkPixelsInPtRect(bitmap, 110.2f, 121f, 111.6f, 135f).Should().BeLessThan(5,
                    $"{who} must leave the gap between wx-advanced glyphs unpainted");
                DarkPixelsInPtRect(bitmap, 122.5f, 121f, 126f, 135f).Should().BeLessThan(5,
                    $"{who} must not use a larger bbox-derived advance fallback");
            }
        });
    }

    private static void WithTempPdf(byte[] pdfData, Action<string> body)
    {
        // Unique path per call — Excise.Rendering.Tests runs 4-way parallel.
        var path = Path.Combine(
            Path.GetTempPath(), $"excise-514-type3-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdfData);
        try
        {
            body(path);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    // Counts dark pixels inside a PDF-point rectangle (bottom-left origin)
    // on a raster rendered at Dpi over a 612x792pt page.
    private static int DarkPixelsInPtRect(
        SKBitmap bitmap, float x0Pt, float y0Pt, float x1Pt, float y1Pt)
    {
        var scale = Dpi / 72f;
        var left = Math.Max(0, (int)(x0Pt * scale));
        var right = Math.Min(bitmap.Width, (int)Math.Ceiling(x1Pt * scale));
        var top = Math.Max(0, bitmap.Height - (int)Math.Ceiling(y1Pt * scale));
        var bottom = Math.Min(bitmap.Height, bitmap.Height - (int)(y0Pt * scale));

        var count = 0;
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                var p = bitmap.GetPixel(x, y);
                if (p.Red < 128 && p.Green < 128 && p.Blue < 128)
                    count++;
            }
        return count;
    }

    private static double InkFraction(SKBitmap bitmap)
    {
        long ink = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var p = bitmap.GetPixel(x, y);
                if (p.Red < 200 || p.Green < 200 || p.Blue < 200)
                    ink++;
            }
        return (double)ink / (bitmap.Width * (long)bitmap.Height);
    }

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
}
