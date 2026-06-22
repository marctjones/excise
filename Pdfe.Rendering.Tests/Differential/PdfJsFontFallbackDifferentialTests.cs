using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Rendering.Differential;
using Xunit;

namespace Pdfe.Rendering.Tests.Differential;

public sealed class PdfJsFontFallbackDifferentialTests
{
    private const int RenderDpi = 72;
    private const int SmallTextRenderDpi = 150;

    [Fact]
    public void HighlightsPage5_LoadsEmbeddedUrwType1FontFile()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable,
            "mutool not on PATH - install mupdf-tools to run this differential fixture.");

        var root = LocateRepoRoot();
        Assert.SkipWhen(root == null, "Could not find repo root.");

        var pdfPath = Path.Combine(root!, "test-pdfs", "pdfjs", "highlights.pdf");
        Assert.SkipUnless(File.Exists(pdfPath),
            "pdf.js fixture not found at test-pdfs/pdfjs/highlights.pdf. Run scripts/download-pdfjs-corpus.sh.");

        using var doc = PdfDocument.Open(pdfPath);
        using var pdfe = new SkiaRenderer().RenderPage(
            doc.GetPage(5),
            new RenderOptions { Dpi = RenderDpi });

        using var mutool = MutoolReferenceRenderer.RenderPage(pdfPath, 5, RenderDpi);
        mutool.Should().NotBeNull("mutool should render the pdf.js highlights fixture");

        using var aligned = DifferentialMetrics.ResizeMatch(pdfe, mutool!.Width, mutool.Height);
        var report = DifferentialMetrics.Compare(aligned, mutool);

        report.DifferingPixelFraction.Should().BeLessThan(0.005,
            "URW Nimbus Roman raw Type 1 /FontFile programs should load through the embedded-font path");
        report.MeanAbsoluteError.Should().BeLessThan(6.0);
    }

    [Fact]
    public void Issue16316_LoadsEmbeddedRawType1FontFile()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable,
            "mutool not on PATH - install mupdf-tools to run this differential fixture.");

        var root = LocateRepoRoot();
        Assert.SkipWhen(root == null, "Could not find repo root.");

        var pdfPath = Path.Combine(root!, "test-pdfs", "pdfjs", "issue16316.pdf");
        Assert.SkipUnless(File.Exists(pdfPath),
            "pdf.js fixture not found at test-pdfs/pdfjs/issue16316.pdf. Run scripts/download-pdfjs-corpus.sh.");

        using var doc = PdfDocument.Open(pdfPath);
        using var pdfe = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = SmallTextRenderDpi, BackgroundColor = SkiaSharp.SKColors.White });

        using var mutool = MutoolReferenceRenderer.RenderPage(pdfPath, 1, SmallTextRenderDpi);
        mutool.Should().NotBeNull("mutool should render the pdf.js issue16316 fixture");

        using var aligned = DifferentialMetrics.ResizeMatch(pdfe, mutool!.Width, mutool.Height);
        var report = DifferentialMetrics.Compare(aligned, mutool);

        report.DifferingPixelFraction.Should().BeLessThan(0.02,
            "raw Type 1 /FontFile text should render from embedded outlines rather than metric-compatible fallback");
        report.MeanAbsoluteError.Should().BeLessThan(9.5);
    }

    private static string? LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "pdfe.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
