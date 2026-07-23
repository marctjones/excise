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
