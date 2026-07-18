using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #633: ShadingType 5 and 7 used as PatternType 2 shading-pattern fills
/// rendered nothing at all — silently. Per CLAUDE.md's no-self-oracle rule,
/// "excise now draws something" is not sufficient evidence the fix is real;
/// an independent renderer (mutool) must agree ink appears where excise
/// previously left a blank page. These tests fail closed (Skip) rather than
/// pass green when mutool is unavailable, matching every other differential
/// test in this directory.
/// </summary>
public sealed class ShadingPatternMeshDifferentialTests
{
    private const int Dpi = 72;

    [Fact]
    public void Type5LatticeMeshPatternFill_MutoolAgreesInkIsDrawn()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable,
            "mutool not on PATH - install mupdf-tools to run this differential fixture.");

        var pdfPath = WriteTempPdf(MeshShadingPdfFixtures.CreateShadingPatternFillPdf(
            MeshShadingPdfFixtures.Type5ShadingDictExtra,
            MeshShadingPdfFixtures.BuildType5MeshBytes()));
        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            using var excise = new SkiaRenderer().RenderPage(
                doc.GetPage(1),
                new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White });

            using var mutool = MutoolReferenceRenderer.RenderPage(pdfPath, 1, Dpi);
            mutool.Should().NotBeNull("mutool should render a minimal ShadingType 5 pattern fill");

            var exciseInk = NonWhiteFraction(excise);
            var mutoolInk = NonWhiteFraction(mutool!);

            exciseInk.Should().BeGreaterThan(0.5,
                "#633: the type 5 lattice mesh covers the whole page — the pattern-fill dispatch " +
                "used to fall through `_ => false` and draw nothing at all for ShadingType 5");
            mutoolInk.Should().BeGreaterThan(0.5,
                "mutool (independent of excise) should also see the lattice mesh cover the page, " +
                "confirming the fixture itself is valid and not just something excise hallucinates");
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    [Fact]
    public void Type7TensorPatchMeshPatternFill_MutoolAgreesInkIsDrawn()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable,
            "mutool not on PATH - install mupdf-tools to run this differential fixture.");

        var pdfPath = WriteTempPdf(MeshShadingPdfFixtures.CreateShadingPatternFillPdf(
            MeshShadingPdfFixtures.Type7ShadingDictExtra,
            MeshShadingPdfFixtures.BuildType7TensorPatchBytes()));
        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            using var excise = new SkiaRenderer().RenderPage(
                doc.GetPage(1),
                new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White });

            using var mutool = MutoolReferenceRenderer.RenderPage(pdfPath, 1, Dpi);
            mutool.Should().NotBeNull("mutool should render a minimal ShadingType 7 pattern fill");

            var exciseInk = NonWhiteFraction(excise);
            var mutoolInk = NonWhiteFraction(mutool!);

            exciseInk.Should().BeGreaterThan(0.2,
                "#633: the type 7 tensor patch decode/rasterize path already existed for the direct " +
                "`sh` operator, but the pattern-fill dispatch never routed ShadingType 7 to it — " +
                "the pattern fill drew nothing while the identical shading painted fine via `sh`");
            mutoolInk.Should().BeGreaterThan(0.2,
                "mutool (independent of excise) should also see the tensor patch cover a substantial " +
                "part of the page, confirming the fixture itself is valid");
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    private static double NonWhiteFraction(SKBitmap bitmap)
    {
        long nonWhite = 0;
        long total = (long)bitmap.Width * bitmap.Height;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 245 || pixel.Green < 245 || pixel.Blue < 245)
                    nonWhite++;
            }
        }

        return (double)nonWhite / total;
    }

    private static string WriteTempPdf(byte[] pdfBytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-shading-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdfBytes);
        return path;
    }
}
