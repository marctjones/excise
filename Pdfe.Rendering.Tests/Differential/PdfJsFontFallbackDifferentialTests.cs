using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Rendering.Differential;
using Xunit;

namespace Pdfe.Rendering.Tests.Differential;

public sealed class PdfJsFontFallbackDifferentialTests
{
    private const int RenderDpi = 72;

    [Fact]
    public void HighlightsPage5_UsesSerifFallbackForUrwType1Fonts()
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

        report.DifferingPixelFraction.Should().BeLessThan(0.075,
            "URW Nimbus Roman Type1 fallback should use a Times-compatible serif face, " +
            "not generic sans-serif");
        report.MeanAbsoluteError.Should().BeLessThan(12.0);
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
