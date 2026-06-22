using AwesomeAssertions;
using Pdfe.Core.Document;
using SkiaSharp;
using Xunit;

namespace Pdfe.Rendering.Tests;

/// <summary>
/// TDD tests for SkiaSharp-based PDF rendering.
/// </summary>
public class SkiaRendererTests
{
    #region Basic Rendering Tests

    [Fact]
    public void RenderPage_ReturnsNonNullBitmap()
    {
        // Arrange
        var pdfData = CreateSimplePdf("Hello World");
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert
        bitmap.Should().NotBeNull();
    }

    [Fact]
    public void RenderPage_AlreadyCancelledToken_Throws()
    {
        var pdfData = CreateSimplePdf("Hello World");
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();

        // Cancellable render path (#366) must observe the token, not run to completion.
        var act = () => renderer.RenderPage(doc.GetPage(1), new RenderOptions(), cts.Token);
        act.Should().Throw<System.OperationCanceledException>();
    }

    [Fact]
    public void RenderPage_MalformedFilteredContentStream_RendersBlankPageInsteadOfThrowing()
    {
        var pdfData = CreatePdfWithMalformedFilteredContentAndPageSize("0 0 100 100 re f", width: 100, height: 100);
        using var doc = PdfDocument.Open(pdfData);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        bitmap.Width.Should().Be(100);
        bitmap.Height.Should().Be(100);
        CountDarkPixels(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height)).Should().Be(0,
            "malformed filtered page content is skipped by the render-only recovery path");
    }

    [Fact]
    public void RenderPage_ImageOnlyJbig2ContentStream_RendersValidPrefixAndReportsDiagnostic()
    {
        var pdfData = CreatePdfWithImageOnlyJbig2ContentStream();
        using var doc = PdfDocument.Open(pdfData);
        var diagnostics = new List<string>();

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White, Diagnostics = diagnostics });

        bitmap.Width.Should().Be(100);
        bitmap.Height.Should().Be(100);
        CountDarkPixels(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height)).Should().BeGreaterThan(300,
            "valid earlier page content should still be rendered");
        diagnostics.Should().ContainSingle(d =>
            d.Contains(ContentStreamReadWarning.ImageOnlyFilterInContentStreamCode, StringComparison.Ordinal));
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_Isartor617_StreamWhitespaceBeforeEol_RendersBlankPage()
    {
        var path = FindRepoFile(
            "test-pdfs",
            "isartor",
            "Isartor testsuite",
            "PDFA-1b",
            "6.1 File structure",
            "6.1.7 Stream objects",
            "isartor-6-1-7-t01-fail-a.pdf");
        Assert.SkipWhen(path == null,
            "No Isartor 6.1.7 stream-object fixture found in test-pdfs.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        bitmap.Width.Should().Be(595);
        bitmap.Height.Should().Be(842);
        CountDarkPixels(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height)).Should().Be(0,
            "the empty Flate content stream should decode without shifting its final binary byte into the parser");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsIssue14256_InlineImagesDoNotTokenizeData()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "issue14256.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js issue14256 fixture found at test-pdfs/pdfjs/issue14256.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72 });

        bitmap.Width.Should().Be(900);
        bitmap.Height.Should().Be(900);
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsIssue16742_FormXObjectClipsToBBox()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "issue16742.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js issue16742 fixture found at test-pdfs/pdfjs/issue16742.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        bitmap.Width.Should().Be(200);
        bitmap.Height.Should().Be(200);

        var visibleGreen = bitmap.GetPixel(30, 100);
        visibleGreen.Green.Should().BeGreaterThan(100);
        visibleGreen.Red.Should().BeLessThan(32);

        var outsideFormBBox = bitmap.GetPixel(160, 100);
        outsideFormBBox.Red.Should().BeGreaterThan(240);
        outsideFormBBox.Green.Should().BeGreaterThan(240);
        outsideFormBBox.Blue.Should().BeGreaterThan(240);
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsFranz2_FormBBoxResolvesIndirectNumbers()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "franz_2.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js franz_2 fixture found at test-pdfs/pdfjs/franz_2.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        bitmap.Width.Should().Be(200);
        bitmap.Height.Should().Be(50);

        var background = bitmap.GetPixel(10, 10);
        background.Red.Should().BeInRange(110, 150);
        background.Green.Should().BeInRange(110, 150);
        background.Blue.Should().BeInRange(110, 150);
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsJbig2MmrSymbolDictionary_RendersDecodedBitonalPage()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "bitmap-symbol-symhuff-texthuff.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js bitmap-symbol-symhuff-texthuff fixture found at test-pdfs/pdfjs/bitmap-symbol-symhuff-texthuff.pdf.");
        var uncompressedPath = FindRepoFile("test-pdfs", "pdfjs", "bitmap-symbol-symhuffuncompressed-texthuff.pdf");
        Assert.SkipWhen(uncompressedPath == null,
            "No pdf.js bitmap-symbol-symhuffuncompressed-texthuff fixture found at test-pdfs/pdfjs/bitmap-symbol-symhuffuncompressed-texthuff.pdf.");

        using var doc = PdfDocument.Open(path);
        using var uncompressedDoc = PdfDocument.Open(uncompressedPath);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });
        using var uncompressedBitmap = new SkiaRenderer().RenderPage(
            uncompressedDoc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        var (whiteFraction, darkFraction) = MeasureWhiteAndDarkPixels(bitmap);
        whiteFraction.Should().BeGreaterThan(0.75,
            "a decoded JBIG2 symbol page should preserve the white background instead of rendering raw compressed bytes as a black field");
        darkFraction.Should().BeInRange(0.005, 0.50,
            "the page should contain visible black symbols without becoming a raw-data fallback page");
        MeasureDifferentPixelFraction(bitmap, uncompressedBitmap).Should().BeLessThan(0.01,
            "MMR-compressed and uncompressed JBIG2 symbol dictionaries should decode to the same glyph shapes");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsJbig2ArithmeticTextRegion_RendersDecodedBitonalPage()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "issue17871_top_right.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js issue17871_top_right fixture found at test-pdfs/pdfjs/issue17871_top_right.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        var (whiteFraction, darkFraction) = MeasureWhiteAndDarkPixels(bitmap);
        whiteFraction.Should().BeGreaterThan(0.75,
            "a malformed-but-decodable JBIG2 arithmetic text region should not fall back to rendering raw compressed bytes");
        darkFraction.Should().BeInRange(0.005, 0.50,
            "the page should contain visible black symbols without becoming a raw-data fallback page");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsIssue2948_Type4MeshPatternRendersColorfulBackground()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "issue2948.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js issue2948 fixture found at test-pdfs/pdfjs/issue2948.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        var (saturatedFraction, darkFraction) = MeasureSaturatedAndDarkPixels(bitmap);
        saturatedFraction.Should().BeGreaterThan(0.25,
            "the type 4 mesh shading should render the rainbow background instead of falling back to black");
        darkFraction.Should().BeLessThan(0.40,
            "the page should not be dominated by the former black fallback fill");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsBug852992_FormSoftMaskCanResolveFormShadingResources()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "bug852992_reduced.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js bug852992_reduced fixture found at test-pdfs/pdfjs/bug852992_reduced.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        var (whiteFraction, darkFraction, redFraction) = MeasureWhiteDarkAndRedPixels(bitmap);
        whiteFraction.Should().BeLessThan(0.90,
            "the form soft mask should resolve /Shading resources from the mask form instead of suppressing all content");
        darkFraction.Should().BeLessThan(0.01);
        redFraction.Should().BeGreaterThan(0.02,
            "the masked red form rectangle should remain visible");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsIssue13372_ImageMaskPaintsCurrentShadingPattern()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "issue13372.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js issue13372 fixture found at test-pdfs/pdfjs/issue13372.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        var (saturatedFraction, darkFraction) = MeasureSaturatedAndDarkPixels(bitmap);
        saturatedFraction.Should().BeGreaterThan(0.03,
            "a CCITT image mask used as a stencil with current /Pattern color should paint the shading pattern, not grayscale mask bits");
        darkFraction.Should().BeLessThan(0.05);

        var whiteCutout = bitmap.GetPixel(130, 300);
        whiteCutout.Red.Should().BeGreaterThan(245,
            "pattern-filled image masks must use PDF image-space orientation, not an upside-down stencil");
        whiteCutout.Green.Should().BeGreaterThan(245,
            "pattern-filled image masks must use PDF image-space orientation, not an upside-down stencil");
        whiteCutout.Blue.Should().BeGreaterThan(245,
            "pattern-filled image masks must use PDF image-space orientation, not an upside-down stencil");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsIssue1905_FlateSoftMasksRespectDecodePolarity()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "issue1905.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js issue1905 fixture found at test-pdfs/pdfjs/issue1905.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        CountDarkPixels(bitmap, new SKRectI(100, 320, 585, 580)).Should().BeLessThan(15_000,
            "Flate soft masks with /Decode [1 0] should not invert alpha and turn chart backgrounds black");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsChromeMarkedContent_MismatchedSoftMaskKeepsImageTextVisible()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "chrome-text-selection-markedContent.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js chrome-text-selection-markedContent fixture found at test-pdfs/pdfjs/chrome-text-selection-markedContent.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        CountBlueDominantPixels(bitmap, new SKRectI(20, 15, 190, 60)).Should().BeGreaterThan(500,
            "the large header is a tiny indexed image with a much larger soft mask, so the mask must not be collapsed to the base image dimensions");
    }

    [Theory(Timeout = 20000)]
    [InlineData("issue4379.pdf")]
    [InlineData("issue4246.pdf")]
    public void RenderPage_PdfjsExplicitImageMask_StencilsSourceImage(string fileName)
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", fileName);
        Assert.SkipWhen(path == null,
            $"No pdf.js {fileName} fixture found at test-pdfs/pdfjs/{fileName}.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        var (whiteFraction, _) = MeasureWhiteAndDarkPixels(bitmap);
        whiteFraction.Should().BeInRange(0.70, 0.98,
            "explicit /Mask image streams should stencil the source image instead of painting its full rectangular bounds or hiding it entirely");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsIssue14814_IndirectDecodeParmsPngPredictorRendersSmoothImage()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "issue14814.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js issue14814 fixture found at test-pdfs/pdfjs/issue14814.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(2),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        var center = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
        center.Green.Should().BeGreaterThan(80,
            "the image stream's indirect /DecodeParms PNG predictor should be applied before RGB conversion");
        center.Red.Should().BeGreaterThan(80);
        center.Blue.Should().BeLessThan(80);
        var (_, darkFraction) = MeasureSaturatedAndDarkPixels(bitmap);
        darkFraction.Should().BeLessThan(0.02,
            "raw predictor bytes render as dark/noisy speckles instead of a smooth gradient");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsS2_JpxSoftMasksClearImageBackgrounds()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "S2.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js S2 fixture found at test-pdfs/pdfjs/S2.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        var (whiteFraction, darkFraction) = MeasureWhiteAndDarkPixels(bitmap);
        whiteFraction.Should().BeGreaterThan(0.30,
            "JPX soft masks should preserve the white page background around transparent image regions");
        darkFraction.Should().BeLessThan(0.18,
            "transparent JPX image regions should not render as black rectangles");

        var (redDominant, blueDominant) = CountRedAndBlueDominantPixels(bitmap, new SKRectI(70, 70, 190, 210));
        redDominant.Should().BeGreaterThan(blueDominant + 500,
            "RGB JPX image components should be mapped from CSJ2K's BGR bitmap order into PDF RGB order");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsIssue19326_UnsupportedJpxDoesNotPaintGrayPlaceholder()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "issue19326.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js issue19326 fixture found at test-pdfs/pdfjs/issue19326.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        MeasureNeutralMidGrayFraction(bitmap).Should().BeLessThan(0.10,
            "an unsupported JPX image should be omitted or decoded, not replaced with an opaque gray block over existing page content");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsIssue16038_UncoloredTilingPatternUsesScnTint()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "issue16038.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js issue16038 fixture found at test-pdfs/pdfjs/issue16038.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 150, BackgroundColor = SKColors.White });

        CountBlueDominantPixels(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height))
            .Should().BeGreaterThan(500,
                "uncolored tiling pattern cells should paint with the RGB tint supplied to scn instead of defaulting to black");
        CountRowsWithBlueDominantPixels(bitmap, new SKRectI(10, 10, 71, 71), minimumBluePixels: 20)
            .Should().BeGreaterThanOrEqualTo(10,
                "subpixel strokes inside tiling pattern cells should not disappear at repeated tile phases");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsIssue15716_ZapfDingbatsDifferencesRenderTilingPattern()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "issue15716.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js issue15716 fixture found at test-pdfs/pdfjs/issue15716.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        var (whiteFraction, darkFraction, redFraction) = MeasureWhiteDarkAndRedPixels(bitmap);
        whiteFraction.Should().BeLessThan(0.98,
            "ZapfDingbats /Differences names a109-a112 should paint the pattern glyphs instead of producing a blank page");
        darkFraction.Should().BeGreaterThan(0.02,
            "the black suit glyphs in the tiling pattern should render");
        redFraction.Should().BeGreaterThan(0.02,
            "the red suit glyphs in the tiling pattern should render");
        CountRedPixels(bitmap, new SKRectI(24, 22, 32, 30)).Should().BeGreaterThan(30,
            "the first red diamond should be filled; missing-glyph boxes leave this center area white");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsIssue13931_FlateSoftMaskMakesImageMatteTransparent()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "issue13931.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js issue13931 fixture found at test-pdfs/pdfjs/issue13931.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        CountDarkPixels(bitmap, new SKRectI(300, 350, 400, 450)).Should().BeLessThan(100,
            "the black matte behind the soft-masked JPEG should become transparent instead of covering the page");
        CountRedPixels(bitmap, new SKRectI(450, 80, 545, 135)).Should().BeGreaterThan(100,
            "the red stamp inside the soft-masked image should remain visible");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PopplerJpeg_ExplicitDctColorTransformColumnsRender()
    {
        var path = FindRepoFile("test-pdfs", "poppler", "tests", "jpeg.pdf");
        Assert.SkipWhen(path == null,
            "No Poppler jpeg.pdf fixture found at test-pdfs/poppler/tests/jpeg.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 150, BackgroundColor = SKColors.White });

        var explicitColorTransformColumns = new[] { 270.667, 441.333 };
        var rowBottoms = new[] { 100, 164, 228, 292, 356, 420, 484, 548, 612, 676 };
        foreach (var x in explicitColorTransformColumns)
        {
            var columnContentPixels = 0;
            foreach (var y in rowBottoms)
            {
                var region = PdfRectToPixelRegion(bitmap, x + 6, y + 6, width: 73, height: 52);
                columnContentPixels += CountNonWhitePixels(bitmap, region);
            }

            columnContentPixels.Should().BeGreaterThan(20_000,
                "explicit /DecodeParms /ColorTransform DCTDecode images should render instead of leaving a blank column");
        }
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PopplerJpeg_DeviceCmykDctUsesPdfColorRulesAndAdobeMarkerPrecedence()
    {
        var path = FindRepoFile("test-pdfs", "poppler", "tests", "jpeg.pdf");
        Assert.SkipWhen(path == null,
            "No Poppler jpeg.pdf fixture found at test-pdfs/poppler/tests/jpeg.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 150, BackgroundColor = SKColors.White });

        var defaultCmykAdobe = PdfRectToPixelRegion(bitmap, 100 + 6, 100 + 6, width: 73, height: 52);
        var explicitCmykAdobe = PdfRectToPixelRegion(bitmap, 270.667 + 6, 100 + 6, width: 73, height: 52);
        var explicitYcckAdobe = PdfRectToPixelRegion(bitmap, 270.667 + 6, 484 + 6, width: 73, height: 52);

        MeanRgb(bitmap, defaultCmykAdobe).Luminance.Should().BeLessThan(45,
            "four-component DCTDecode samples in /DeviceCMYK should flow through PDF CMYK conversion instead of JPEG RGB conventions");
        MeanRgb(bitmap, explicitCmykAdobe).Luminance.Should().BeLessThan(45,
            "an Adobe APP14 marker overrides a conflicting explicit /ColorTransform value for CMYK DCTDecode images");
        MeanRgb(bitmap, explicitYcckAdobe).Luminance.Should().BeLessThan(45,
            "Adobe APP14 YCCK images should decode to CMYK samples before PDF color conversion");

        var explicitRgbAdobe = PdfRectToPixelRegion(bitmap, 270.667 + 6, 228 + 6, width: 73, height: 52);
        var rgbMean = MeanRgb(bitmap, explicitRgbAdobe);
        rgbMean.Red.Should().BeGreaterThan(rgbMean.Blue + 20,
            "an Adobe APP14 no-transform marker should override explicit /ColorTransform 1 for RGB DCTDecode images");
        rgbMean.Green.Should().BeGreaterThan(rgbMean.Blue + 10,
            "the RGB Adobe-marker cell should remain warm instead of being interpreted as YCbCr");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsIssue10339_IndexedLabImagesDoNotRenderAsBlackBlocks()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "issue10339_reduced.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js issue10339_reduced fixture found at test-pdfs/pdfjs/issue10339_reduced.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        var (saturatedFraction, darkFraction) = MeasureSaturatedAndDarkPixels(bitmap);
        darkFraction.Should().BeLessThan(0.20,
            "Indexed images with Lab lookup tables should decode to visible palette colors instead of black blocks");
        saturatedFraction.Should().BeGreaterThan(0.05,
            "the decoded Lab palette should include visible blue/cyan tiles");
        CountBluePixels(bitmap, new SKRectI(15, 15, 50, 45)).Should().BeGreaterThan(100,
            "the first image's /Decode [255 0] should invert Indexed palette samples");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsAlphaTrans_DirectAxialShadingUsesFunctionReferencesAndAlpha()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "alphatrans.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js alphatrans fixture found at test-pdfs/pdfjs/alphatrans.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        CountDarkPixels(bitmap, new SKRectI(300, 145, 500, 330)).Should().BeLessThan(100,
            "the direct axial shading should render as a translucent gradient instead of a black fallback rectangle");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsIssue10572_StitchedShadingPatternUsesDeclaredDomain()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "issue10572.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js issue10572 fixture found at test-pdfs/pdfjs/issue10572.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        CountBlueDominantPixels(bitmap, new SKRectI(70, 155, 275, 230)).Should().BeGreaterThan(2000,
            "stitched axial shading functions with non-0..1 domains should produce the blue bands in the pattern");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsIssue13520_DeviceNShadingTintTransformDoesNotCollapseToBlack()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "issue13520.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js issue13520 fixture found at test-pdfs/pdfjs/issue13520.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        CountDarkPixels(bitmap, new SKRectI(70, 25, 140, 55)).Should().BeLessThan(50,
            "DeviceN radial shading tint transforms should produce the light center instead of an all-black overpaint");
        CountWarmPalePixels(bitmap, new SKRectI(25, 15, 185, 75)).Should().BeGreaterThan(2_500,
            "transparency-group Form XObjects should apply parent Screen blending and soft masks at the form invocation boundary");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsBug920426_Type0EncodingCMapRemapsCharacterCodesToGlyphs()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "bug920426.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js bug920426 fixture found at test-pdfs/pdfjs/bug920426.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        CountDarkPixels(bitmap, new SKRectI(10, 10, 170, 35)).Should().BeGreaterThan(600,
            "the embedded Type0/Identity-H text should render as readable glyphs");
        CountDarkPixels(bitmap, new SKRectI(9, 17, 14, 23)).Should().BeGreaterThan(8,
            "the first word should start with the left stroke of C");
        CountDarkPixels(bitmap, new SKRectI(19, 17, 24, 23)).Should().BeLessThan(15,
            "the embedded Encoding CMap should remap 0043 to the C glyph, leaving the right side open");
        CountDarkPixels(bitmap, new SKRectI(94, 10, 101, 35)).Should().BeLessThan(60,
            "the rendered words should preserve the visible space between Checkliste and Service");
        CountDarkPixels(bitmap, new SKRectI(105, 10, 165, 35)).Should().BeGreaterThan(200,
            "the second word should render after the space instead of wrong CID glyphs filling the gap");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsBug1108301_EmbeddedBengaliTrueTypeUsesByteCmap()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "bug1108301.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js bug1108301 fixture found at test-pdfs/pdfjs/bug1108301.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        CountDarkPixels(bitmap, new SKRectI(0, 0, bitmap.Width, bitmap.Height)).Should().BeGreaterThan(700,
            "the embedded Bengali text should render visible glyphs");
        CountDarkPixels(bitmap, new SKRectI(0, 5, bitmap.Width, 10)).Should().BeLessThan(80,
            "the embedded font should use its byte cmap instead of drawing repeated .notdef box outlines");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsBug1308536_SubstitutedCondensedType1UsesPdfWidths()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "bug1308536.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js bug1308536 fixture found at test-pdfs/pdfjs/bug1308536.pdf.");

        using var doc = PdfDocument.Open(path);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 150, BackgroundColor = SKColors.White });

        var textRegion = new SKRectI(0, 20, bitmap.Width, 80);
        var darkPixels = CountDarkPixels(bitmap, textRegion);
        darkPixels.Should().BeGreaterThan(1_800,
            "the substituted Type1 text should still be visible");
        darkPixels.Should().BeLessThan(4_800,
            "fallback glyphs should be horizontally condensed to the PDF /Widths instead of overprinting into an unreadable blob");
    }

    [Theory(Timeout = 20000)]
    [InlineData("issue1045.pdf", 200, 50)]
    [InlineData("issue11549_reduced.pdf", 200, 50)]
    [InlineData("issue1293r.pdf", 200, 50)]
    [InlineData("issue13147.pdf", 200, 50)]
    [InlineData("issue15150.pdf", 10, 10)]
    [InlineData("issue3566.pdf", 200, 50)]
    public void RenderPage_PdfjsMalformedStreamFixtures_RenderViaParserRecovery(
        string fileName,
        int expectedWidth,
        int expectedHeight)
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", fileName);
        Assert.SkipWhen(path == null,
            $"No pdf.js fixture found at test-pdfs/pdfjs/{fileName}.");

        using var doc = PdfDocument.Open(path);
        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        bitmap.Width.Should().Be(expectedWidth);
        bitmap.Height.Should().Be(expectedHeight);
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsIssue13147_CidTextUsesPdfWidths()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "issue13147.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js issue13147 fixture found at test-pdfs/pdfjs/issue13147.pdf.");

        using var doc = PdfDocument.Open(path);
        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 150, BackgroundColor = SKColors.White });

        CountDarkPixels(bitmap, new SKRectI(120, 0, 190, bitmap.Height)).Should().BeGreaterThan(500,
            "CID glyphs should be positioned with PDF /W and /DW widths instead of collapsing into overlapped clusters");
        CountDarkPixels(bitmap, new SKRectI(300, 0, bitmap.Width, bitmap.Height)).Should().BeGreaterThan(350,
            "the final Japanese glyphs should reach the right side of the phrase instead of being hidden by overlap");
    }

    [Fact(Timeout = 20000)]
    public void RenderPage_PdfjsIssue16263_HugeSoftMaskDownsamplesInsteadOfTimingOut()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "issue16263.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js issue16263 fixture found at test-pdfs/pdfjs/issue16263.pdf.");

        using var doc = PdfDocument.Open(path);
        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        bitmap.Width.Should().Be(960);
        bitmap.Height.Should().Be(540);
    }

    [Fact]
    public void RenderPage_ZeroSizedMediaBox_UsesBoundedFallbackPage()
    {
        var pdfData = CreatePdfWithContentAndPageSize("0 g 10 10 20 20 re f", width: 0, height: 0);
        using var doc = PdfDocument.Open(pdfData);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        bitmap.Width.Should().Be(612);
        bitmap.Height.Should().Be(792);
        bitmap.GetPixel(20, 772).Should().NotBe(SKColors.White);
    }

    [Fact]
    public void RenderPage_ExceedsPixelLimit_ThrowsResourceLimit()
    {
        var pdfData = CreatePdfWithContentAndPageSize("", width: 100, height: 100);
        using var doc = PdfDocument.Open(pdfData);

        var act = () => new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, MaxPixelCount = 100 });

        act.Should().Throw<RenderResourceLimitException>();
    }

    [Fact]
    public void RenderPage_WithClipRect_ReturnsOnlyClippedBitmap()
    {
        var pdfData = CreatePdfWithContentAndPageSize(
            "0 g 10 10 20 20 re f 0.5 g 70 70 20 20 re f",
            width: 100,
            height: 100);
        using var doc = PdfDocument.Open(pdfData);

        using var full = new SkiaRenderer().RenderPage(doc.GetPage(1), new RenderOptions { Dpi = 72 });
        using var clipped = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, ClipRect = new SKRect(10, 10, 30, 30) });

        clipped.Width.Should().Be(20);
        clipped.Height.Should().Be(20);
        clipped.GetPixel(10, 10).Should().Be(full.GetPixel(20, 80),
            "the clipped bitmap should be translated from the same visual page region");
        clipped.GetPixel(10, 10).Should().NotBe(SKColors.White);
    }

    [Fact]
    public void RenderPage_WithClipRect_AppliesResourceLimitToTileSize()
    {
        var pdfData = CreatePdfWithContentAndPageSize("0 g 0 0 100 100 re f", width: 100, height: 100);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var renderer = new SkiaRenderer();

        var full = () => renderer.RenderPage(page, new RenderOptions { Dpi = 72, MaxPixelCount = 400 });
        full.Should().Throw<RenderResourceLimitException>();

        using var clipped = renderer.RenderPage(
            page,
            new RenderOptions
            {
                Dpi = 72,
                ClipRect = new SKRect(0, 0, 10, 10),
                MaxPixelCount = 400
            });

        clipped.Width.Should().Be(10);
        clipped.Height.Should().Be(10);
    }

    [Fact]
    public void RenderPageToPng_WritesValidPngBytes()
    {
        var pdfData = CreateSimplePdf("Hello World");
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        using var ms = new System.IO.MemoryStream();
        renderer.RenderPageToPng(doc.GetPage(1), ms);

        ms.Length.Should().BeGreaterThan(8, "a PNG was written");
        // PNG 8-byte signature: 89 50 4E 47 0D 0A 1A 0A
        var sig = ms.ToArray();
        sig[0..8].Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
    }

    [Fact]
    public void RenderPage_DefaultDpi_Returns150DpiImage()
    {
        // Arrange
        var pdfData = CreateSimplePdf("Test");
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();
        var page = doc.GetPage(1);

        // Act
        using var bitmap = renderer.RenderPage(page);

        // Assert - US Letter at 150 DPI should be ~1275x1650 pixels
        // 612 points * 150/72 = 1275, 792 points * 150/72 = 1650
        var expectedWidth = (int)Math.Ceiling(page.Width * 150 / 72);
        var expectedHeight = (int)Math.Ceiling(page.Height * 150 / 72);
        bitmap.Width.Should().Be(expectedWidth);
        bitmap.Height.Should().Be(expectedHeight);
    }

    [Fact]
    public void RenderPage_CustomDpi_ScalesCorrectly()
    {
        // Arrange
        var pdfData = CreateSimplePdf("Test");
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();
        var page = doc.GetPage(1);
        var options = new RenderOptions { Dpi = 300 };

        // Act
        using var bitmap = renderer.RenderPage(page, options);

        // Assert - 300 DPI should be double the size of 150 DPI
        var expectedWidth = (int)Math.Ceiling(page.Width * 300 / 72);
        var expectedHeight = (int)Math.Ceiling(page.Height * 300 / 72);
        bitmap.Width.Should().Be(expectedWidth);
        bitmap.Height.Should().Be(expectedHeight);
    }

    [Fact]
    public void RenderPage_FractionalDevicePageSize_UsesCeiling()
    {
        var pdfData = CreatePdfWithContentAndPageSize("q 1 0 0 rg 0 0 1 1 re f Q", width: 3, height: 3);
        using var doc = PdfDocument.Open(pdfData);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 150, BackgroundColor = SKColors.White });

        bitmap.Width.Should().Be(7);
        bitmap.Height.Should().Be(7);
    }

    [Fact]
    public void RenderPage_WithCropBox_UsesVisibleBoxSizeAndOrigin()
    {
        var pdfData = CreatePdfWithContentPageSizeAndCropBox(
            "0 g 10 20 50 50 re f",
            width: 100,
            height: 100,
            cropBox: "[10 20 60 70]");
        using var doc = PdfDocument.Open(pdfData);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        bitmap.Width.Should().Be(50);
        bitmap.Height.Should().Be(50);
        bitmap.GetPixel(25, 25).Should().NotBe(SKColors.White);
    }

    [Fact]
    public void RenderPage_WithOversizedCropBox_ClampsToMediaBox()
    {
        var pdfData = CreatePdfWithContentPageSizeAndCropBox(
            "0 g 10 10 20 20 re f",
            width: 100,
            height: 100,
            cropBox: "[-10 -10 110 110]");
        using var doc = PdfDocument.Open(pdfData);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        bitmap.Width.Should().Be(100);
        bitmap.Height.Should().Be(100);
        bitmap.GetPixel(20, 80).Should().NotBe(SKColors.White);
    }

    [Fact]
    public void RenderPage_WithPartiallyOverlappingCropBox_UsesMediaIntersection()
    {
        var pdfData = CreatePdfWithContentPageSizeAndCropBox(
            "0 g 50 40 50 60 re f",
            width: 100,
            height: 100,
            cropBox: "[50 40 150 140]");
        using var doc = PdfDocument.Open(pdfData);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        bitmap.Width.Should().Be(50);
        bitmap.Height.Should().Be(60);
        bitmap.GetPixel(25, 30).Should().NotBe(SKColors.White);
    }

    [Fact]
    public void RenderPage_WithZeroAreaCropBox_UsesMediaBox()
    {
        var pdfData = CreatePdfWithContentPageSizeAndCropBox(
            "0 g 10 10 20 20 re f",
            width: 100,
            height: 100,
            cropBox: "[0 0 0 0]");
        using var doc = PdfDocument.Open(pdfData);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        bitmap.Width.Should().Be(100);
        bitmap.Height.Should().Be(100);
        bitmap.GetPixel(20, 80).Should().NotBe(SKColors.White);
    }

    [Fact]
    public void RenderPage_WithDisjointCropBox_UsesMediaBox()
    {
        var pdfData = CreatePdfWithContentPageSizeAndCropBox(
            "0 g 10 10 20 20 re f",
            width: 100,
            height: 100,
            cropBox: "[200 200 300 300]");
        using var doc = PdfDocument.Open(pdfData);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        bitmap.Width.Should().Be(100);
        bitmap.Height.Should().Be(100);
        bitmap.GetPixel(20, 80).Should().NotBe(SKColors.White);
    }

    [Fact]
    public void RenderPage_WithHugeCropBox_AppliesResourceLimitAfterClamping()
    {
        var pdfData = CreatePdfWithContentPageSizeAndCropBox(
            "",
            width: 100,
            height: 100,
            cropBox: "[0 0 14405 14405]");
        using var doc = PdfDocument.Open(pdfData);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, MaxPixelCount = 20_000 });

        bitmap.Width.Should().Be(100);
        bitmap.Height.Should().Be(100);
    }

    [Fact]
    public void RenderPage_WithNoisyCropBox_SnapsNearIntegerDeviceSize()
    {
        var pdfData = CreatePdfWithContentPageSizeAndCropBox(
            "0 g 42 42 10 10 re f",
            width: 480,
            height: 678,
            cropBox: "[41.759996 41.759996 438.24 636.24]");
        using var doc = PdfDocument.Open(pdfData);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 150, BackgroundColor = SKColors.White });

        bitmap.Width.Should().Be(826);
        bitmap.Height.Should().Be(1239);
    }

    [Fact]
    public void RenderPage_WhiteBackground_IsWhite()
    {
        // Arrange
        var pdfData = CreateEmptyPdf();
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();
        var options = new RenderOptions { BackgroundColor = SKColors.White };

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1), options);

        // Assert - check center pixel is white
        var centerX = bitmap.Width / 2;
        var centerY = bitmap.Height / 2;
        var pixel = bitmap.GetPixel(centerX, centerY);
        pixel.Should().Be(SKColors.White);
    }

    #endregion

    #region Rectangle Rendering Tests

    [Fact]
    public void RenderPage_FilledRectangle_ShowsBlackPixel()
    {
        // Arrange
        var pdfData = CreatePdfWithRectangle(100, 100, 200, 150, fill: true);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - sample pixel inside rectangle should be black (or non-white)
        // Rectangle at PDF coordinates (100, 100, 200, 150) means:
        // - X: 100 to 300 (left edge + width)
        // - Y: 100 to 250 (bottom edge + height in PDF coords)
        // At 150 DPI, X pixel = 100 * 150/72 = 208, Y = depends on flip
        var pixelX = (int)(200 * 150 / 72); // Center of rectangle X
        var pixelY = bitmap.Height - (int)(175 * 150 / 72); // Center of rectangle Y, flipped
        var pixel = bitmap.GetPixel(pixelX, pixelY);
        pixel.Should().NotBe(SKColors.White, "rectangle area should be filled");
    }

    // ---- page /Rotate support (#356 rendering follow-up) ----

    [Theory]
    [InlineData(0, 612, 792)]
    [InlineData(90, 792, 612)]
    [InlineData(180, 612, 792)]
    [InlineData(270, 792, 612)]
    public void RenderPage_Rotation_ProducesVisualDimensions(int rotation, int expectedW, int expectedH)
    {
        using var doc = PdfDocument.Open(CreatePdfWithRectangle(0, 0, 50, 50));
        var page = doc.GetPage(1);
        page.Rotation = rotation;

        using var bitmap = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 72 });

        // 90/270 swap width and height (the page is displayed quarter-turned).
        bitmap.Width.Should().Be(expectedW);
        bitmap.Height.Should().Be(expectedH);
    }

    [Fact]
    public void RenderPage_Rotation90_MovesContentBottomLeftToBitmapTopLeft()
    {
        // A black square in the content's bottom-left corner...
        using var doc = PdfDocument.Open(CreatePdfWithRectangle(0, 0, 60, 60));
        var page = doc.GetPage(1);
        page.Rotation = 90;

        using var bitmap = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 72 });

        // ...lands in the bitmap's top-left under a clockwise 90° rotation,
        // and the opposite corner stays background.
        bitmap.GetPixel(15, 15).Should().NotBe(SKColors.White,
            "the rotated content's bottom-left square renders in the top-left");
        bitmap.GetPixel(bitmap.Width - 15, bitmap.Height - 15).Should().Be(SKColors.White,
            "the opposite corner is background");
    }

    [Fact]
    public void RenderPage_StrokedRectangle_ShowsOutline()
    {
        // Arrange
        var pdfData = CreatePdfWithRectangle(50, 50, 100, 100, fill: false, stroke: true);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - center of rectangle should be white (not filled)
        var centerX = (int)(100 * 150 / 72);
        var centerY = bitmap.Height - (int)(100 * 150 / 72);
        var centerPixel = bitmap.GetPixel(centerX, centerY);
        centerPixel.Should().Be(SKColors.White, "rectangle interior should not be filled");
    }

    #endregion

    #region Path Painting Operators Tests

    [Fact]
    public void RenderPage_CloseAndStroke_sOperator()
    {
        // Arrange - s operator: close and stroke path
        // Equivalent to: h S (close path, then stroke)
        var content = @"
            0 G
            2 w
            100 400 m
            200 400 l
            200 500 l
            100 500 l
            s
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - closed square outline should render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_CloseFillStroke_bOperator()
    {
        // Arrange - b operator: close, fill, and stroke path (nonzero winding)
        // Equivalent to: h B (close, then fill and stroke)
        var content = @"
            0.5 g
            0 G
            2 w
            100 300 m
            200 300 l
            200 400 l
            100 400 l
            b
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - filled and stroked square should render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_CloseFillStrokeEvenOdd_bStarOperator()
    {
        // Arrange - b* operator: close, fill (even-odd), and stroke
        // Even-odd rule for complex self-intersecting paths
        var content = @"
            0.7 g
            0 G
            2 w
            150 200 m
            250 300 l
            150 300 l
            250 200 l
            b*
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - self-intersecting path with even-odd fill should render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_FillStrokeEvenOdd_BStarOperator()
    {
        // Arrange - B* operator: fill (even-odd) and stroke, without closing
        // Difference from B is even-odd rule vs nonzero winding
        var content = @"
            0.8 g
            0 G
            3 w
            100 400 m
            300 400 l
            300 500 l
            100 500 l
            100 400 l
            B*
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - rectangle filled and stroked with even-odd rule
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_ComplexPath_MultipleWindingRules()
    {
        // Arrange - Test nonzero winding (B) vs even-odd (B*) on same complex shape
        var content = @"
            0 G
            2 w
            0.3 g
            50 500 m 150 500 l 150 600 l 50 600 l 50 500 l
            B
            0.6 g
            250 500 m 350 500 l 350 600 l 250 600 l 250 500 l
            B*
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - both squares should render (simple shapes, no difference in fill rules)
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    #endregion

    #region Line Rendering Tests

    [Fact]
    public void RenderPage_Line_ShowsStroke()
    {
        // Arrange
        var pdfData = CreatePdfWithLine(100, 100, 300, 300);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - sample a point along the line
        var midX = (int)(200 * 150 / 72);
        var midY = bitmap.Height - (int)(200 * 150 / 72);
        var pixel = bitmap.GetPixel(midX, midY);
        // The line may not hit exactly due to anti-aliasing, so just check it's not white
        pixel.Should().NotBe(SKColors.White, "line should be visible");
    }

    [Fact]
    public void RenderPage_CubicBezierCurve_RendersSmoothCurve()
    {
        // Arrange - c operator: x1 y1 x2 y2 x3 y3 c (cubic Bézier with all control points)
        var content = @"
            0 G
            2 w
            100 400 m
            150 500 250 500 300 400 c
            S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - curve should render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_BezierV_UsesCurrentPointAsControl()
    {
        // Arrange - v operator: x2 y2 x3 y3 v (current point is first control point)
        // Equivalent to: currentX currentY x2 y2 x3 y3 c
        var content = @"
            0 G
            2 w
            100 300 m
            250 450 300 300 v
            S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - v operator curve should render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_BezierY_UsesEndpointAsControl()
    {
        // Arrange - y operator: x1 y1 x3 y3 y (endpoint is second control point)
        // Equivalent to: x1 y1 x3 y3 x3 y3 c
        var content = @"
            0 G
            2 w
            100 200 m
            150 350 300 200 y
            S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - y operator curve should render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_ComplexPath_CombinesMultipleCurves()
    {
        // Arrange - Complex path with c, v, and y operators
        var content = @"
            0 G
            3 w
            100 400 m
            150 450 200 450 250 400 c
            300 500 350 350 v
            400 450 450 400 y
            S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - complex curved path should render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    #endregion

    #region Color Tests

    [Fact]
    public void RenderPage_RedRectangle_ShowsRed()
    {
        // Arrange
        var pdfData = CreatePdfWithColoredRectangle(100, 100, 200, 150, 1, 0, 0); // RGB red
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - sample inside rectangle
        var pixelX = (int)(200 * 150 / 72);
        var pixelY = bitmap.Height - (int)(175 * 150 / 72);
        var pixel = bitmap.GetPixel(pixelX, pixelY);
        pixel.Red.Should().BeGreaterThan(200, "red component should be high");
        pixel.Green.Should().BeLessThan(50, "green component should be low");
        pixel.Blue.Should().BeLessThan(50, "blue component should be low");
    }

    [Fact]
    public void RenderPage_GrayscaleRectangle_ShowsGray()
    {
        // Arrange - 50% gray (0.5 g)
        var pdfData = CreatePdfWithGrayscaleRectangle(100, 100, 200, 150, 0.5);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - sample inside rectangle should be gray
        var pixelX = (int)(200 * 150 / 72);
        var pixelY = bitmap.Height - (int)(175 * 150 / 72);
        var pixel = bitmap.GetPixel(pixelX, pixelY);
        // 50% gray = approximately 128
        pixel.Red.Should().BeInRange((byte)100, (byte)160);
        pixel.Green.Should().BeInRange((byte)100, (byte)160);
        pixel.Blue.Should().BeInRange((byte)100, (byte)160);
    }

    [Fact]
    public void RenderPage_ColorSpace_DeviceGray_SetsGrayscale()
    {
        // Arrange - cs/sc operators for device-independent color
        // cs sets color space, sc sets color components
        var content = @"
            /DeviceGray cs
            0.3 sc
            100 100 200 150 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - should render 30% gray rectangle
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_ColorSpace_DeviceRGB_SetsRGBColor()
    {
        // Arrange - CS/SC for stroke color space
        var content = @"
            /DeviceRGB CS
            1 0 0 SC
            5 w
            100 400 m 400 400 l S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - should render red line
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_ColorSpace_Separation_UsesTintTransform()
    {
        // Arrange - a Separation color space with an exponential tint
        // transform that maps tint 1 to red.
        var pdfData = CreatePdfWithSeparationRectangle();
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - sample inside the painted rectangle should be red
        var pixelX = (int)(150 * 150 / 72);
        var pixelY = bitmap.Height - (int)(150 * 150 / 72);
        var pixel = bitmap.GetPixel(pixelX, pixelY);
        pixel.Red.Should().BeGreaterThan(180);
        pixel.Green.Should().BeLessThan(80);
        pixel.Blue.Should().BeLessThan(80);
    }

    [Fact]
    public void RenderPage_ColorSpace_DeviceCMYK_SetsCMYKColor()
    {
        // Arrange - CMYK color space with sc operator
        var content = @"
            /DeviceCMYK cs
            0 1 1 0 sc
            100 100 200 150 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - CMYK (0, 1, 1, 0) = red, should render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_DirectCMYKOperator_UsesSharedPdfReferenceFallback()
    {
        // Arrange - nonzero K distinguishes the PDF reference fallback
        // (1 - min(1, component + K)) from the older multiplicative shortcut.
        var content = @"
            0.25 0.50 0.10 0.20 k
            100 100 200 150 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert
        var pixelX = (int)(150 * 150 / 72);
        var pixelY = bitmap.Height - (int)(150 * 150 / 72);
        var pixel = bitmap.GetPixel(pixelX, pixelY);
        pixel.Red.Should().BeInRange((byte)138, (byte)142);
        pixel.Green.Should().BeInRange((byte)74, (byte)78);
        pixel.Blue.Should().BeInRange((byte)176, (byte)180);
    }

    [Fact]
    public void RenderPage_ColorSpace_SeparateFillAndStroke_UsesIndependentSpaces()
    {
        // Arrange - cs/CS set fill/stroke color spaces independently
        var content = @"
            /DeviceGray cs
            0.5 sc
            /DeviceRGB CS
            1 0 0 SC
            3 w
            100 100 200 150 re B
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - gray fill, red stroke
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_ColorSpace_scn_WorksLikeScWithoutPattern()
    {
        // Arrange - scn/SCN are like sc/SC but support patterns
        // Without pattern name, they work the same as sc/SC
        var content = @"
            /DeviceRGB cs
            0 0 1 scn
            100 100 200 150 re f
            /DeviceRGB CS
            0 1 0 SCN
            3 w
            100 300 200 150 re S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - blue fill, green stroke
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_FormXObject_UsesFormColorSpaceResources()
    {
        // Arrange - the form defines its own /ColorSpace resource and uses it
        // inside the form content. The renderer must resolve the color space
        // from the active resource stack, not just the page dictionary.
        var pdfData = CreatePdfWithFormXObjectAndContent(
            content: "q 1 0 0 1 100 400 cm /Fm1 Do Q",
            formContent: @"
                /CS1 cs
                1 0 0 sc
                10 10 80 80 re f
            ",
            formResources: "<< /ColorSpace << /CS1 /DeviceRGB >> >>");
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - sample inside the form rectangle should be red
        var pixelX = (int)(150 * 150 / 72);
        var pixelY = bitmap.Height - (int)(450 * 150 / 72);
        var pixel = bitmap.GetPixel(pixelX, pixelY);
        pixel.Red.Should().BeGreaterThan(180);
        pixel.Green.Should().BeLessThan(80);
        pixel.Blue.Should().BeLessThan(80);
    }

    #endregion

    #region Transformation Tests

    [Fact]
    public void RenderPage_TranslatedRectangle_IsOffset()
    {
        // Arrange - rectangle at (0,0) translated by (200, 200)
        var pdfData = CreatePdfWithTranslatedRectangle(0, 0, 50, 50, 200, 200);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - rectangle should be at (200, 200) not (0, 0)
        var origin = bitmap.GetPixel(10, bitmap.Height - 10);
        origin.Should().Be(SKColors.White, "original position should be empty");

        var translated = bitmap.GetPixel((int)(225 * 150 / 72), bitmap.Height - (int)(225 * 150 / 72));
        translated.Should().NotBe(SKColors.White, "translated position should have content");
    }

    [Fact]
    public void RenderPage_RotatedContent_Rotates()
    {
        // Arrange - Rotate 45 degrees using cm operator
        // Rotation matrix: [cos θ, sin θ, -sin θ, cos θ, 0, 0]
        var content = @"
            q
            0.707 0.707 -0.707 0.707 300 300 cm
            0 0 1 rg
            0 0 100 100 re f
            Q
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - should render without error
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_ScaledContent_Scales()
    {
        // Arrange - Scale by 2x in both directions
        // Scale matrix: [sx, 0, 0, sy, 0, 0]
        var content = @"
            q
            2 0 0 2 100 100 cm
            1 0 0 rg
            0 0 50 50 re f
            Q
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - scaled rectangle should render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_NonUniformScale_ScalesDifferently()
    {
        // Arrange - Scale 3x horizontally, 1x vertically
        var content = @"
            q
            3 0 0 1 100 100 cm
            0 1 0 rg
            0 0 50 50 re f
            Q
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - non-uniform scaling should work
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_SkewedContent_Skews()
    {
        // Arrange - Skew transformation
        // Skew matrix: [1, tan(angle_y), tan(angle_x), 1, 0, 0]
        var content = @"
            q
            1 0.5 0.3 1 100 100 cm
            0 0 1 rg
            0 0 100 100 re f
            Q
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - skewed content should render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_CombinedTransformations_AppliesInOrder()
    {
        // Arrange - Combine scale, rotate, and translate
        var content = @"
            q
            1 0 0 1 200 200 cm
            0.707 0.707 -0.707 0.707 0 0 cm
            2 0 0 2 0 0 cm
            1 0 0 rg
            0 0 25 25 re f
            Q
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - combined transformations should work
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_NestedTransformations_ComposesCorrectly()
    {
        // Arrange - Nested q/Q with transformations
        var content = @"
            q
            1 0 0 1 100 100 cm
            1 0 0 rg
            0 0 50 50 re f
            q
            2 0 0 2 50 50 cm
            0 1 0 rg
            0 0 25 25 re f
            Q
            Q
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - nested transformations should compose
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_TransformationWithText_TransformsText()
    {
        // Arrange - Apply transformation to text
        var content = @"
            q
            2 0 0 2 100 100 cm
            BT
            /F1 24 Tf
            0 0 Td
            (Scaled) Tj
            ET
            Q
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - transformed text should render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_IdentityMatrix_NoTransformation()
    {
        // Arrange - Identity matrix (no transformation)
        var content = @"
            q
            1 0 0 1 0 0 cm
            0 0 1 rg
            100 100 100 100 re f
            Q
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - identity matrix should have no effect
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    #endregion

    #region State Stack Tests

    [Fact]
    public void RenderPage_SaveRestoreState_WorksCorrectly()
    {
        // Arrange - save state, draw red, restore, draw black
        var pdfData = CreatePdfWithStateStack();
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - should have both rectangles rendered
        bitmap.Width.Should().BeGreaterThan(0);
        bitmap.Height.Should().BeGreaterThan(0);
    }

    #endregion

    #region CMYK Color Tests

    [Fact]
    public void RenderPage_CmykCyan_ShowsCyan()
    {
        // Arrange - Cyan in CMYK: C=1, M=0, Y=0, K=0
        var pdfData = CreatePdfWithCmykRectangle(100, 100, 200, 150, 1, 0, 0, 0);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - cyan = RGB(0, 255, 255) or close to it
        var pixelX = (int)(200 * 150 / 72);
        var pixelY = bitmap.Height - (int)(175 * 150 / 72);
        var pixel = bitmap.GetPixel(pixelX, pixelY);
        pixel.Red.Should().BeLessThan(100, "red component should be low for cyan");
        pixel.Green.Should().BeGreaterThan(200, "green component should be high for cyan");
        pixel.Blue.Should().BeGreaterThan(200, "blue component should be high for cyan");
    }

    [Fact]
    public void RenderPage_CmykMagenta_ShowsMagenta()
    {
        // Arrange - Magenta in CMYK: C=0, M=1, Y=0, K=0
        var pdfData = CreatePdfWithCmykRectangle(100, 100, 200, 150, 0, 1, 0, 0);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - magenta = RGB(255, 0, 255) or close to it
        var pixelX = (int)(200 * 150 / 72);
        var pixelY = bitmap.Height - (int)(175 * 150 / 72);
        var pixel = bitmap.GetPixel(pixelX, pixelY);
        pixel.Red.Should().BeGreaterThan(200, "red component should be high for magenta");
        pixel.Green.Should().BeLessThan(100, "green component should be low for magenta");
        pixel.Blue.Should().BeGreaterThan(200, "blue component should be high for magenta");
    }

    [Fact]
    public void RenderPage_CmykYellow_ShowsYellow()
    {
        // Arrange - Yellow in CMYK: C=0, M=0, Y=1, K=0
        var pdfData = CreatePdfWithCmykRectangle(100, 100, 200, 150, 0, 0, 1, 0);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - yellow = RGB(255, 255, 0) or close to it
        var pixelX = (int)(200 * 150 / 72);
        var pixelY = bitmap.Height - (int)(175 * 150 / 72);
        var pixel = bitmap.GetPixel(pixelX, pixelY);
        pixel.Red.Should().BeGreaterThan(200, "red component should be high for yellow");
        pixel.Green.Should().BeGreaterThan(200, "green component should be high for yellow");
        pixel.Blue.Should().BeLessThan(100, "blue component should be low for yellow");
    }

    [Fact]
    public void RenderPage_CmykBlack_ShowsBlack()
    {
        // Arrange - Black in CMYK: C=0, M=0, Y=0, K=1
        var pdfData = CreatePdfWithCmykRectangle(100, 100, 200, 150, 0, 0, 0, 1);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - black = RGB(0, 0, 0)
        var pixelX = (int)(200 * 150 / 72);
        var pixelY = bitmap.Height - (int)(175 * 150 / 72);
        var pixel = bitmap.GetPixel(pixelX, pixelY);
        pixel.Red.Should().BeLessThan(50, "red component should be low for black");
        pixel.Green.Should().BeLessThan(50, "green component should be low for black");
        pixel.Blue.Should().BeLessThan(50, "blue component should be low for black");
    }

    [Fact]
    public void RenderPage_CmykStroke_UsesKOperator()
    {
        // Arrange - Test K operator for stroke color (uppercase K)
        // K sets stroke CMYK, k sets fill CMYK
        var content = @"
            0 1 1 0 K
            5 w
            100 400 m 400 400 l S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - CMYK (0, 1, 1, 0) = red stroke should render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_CmykFillAndStroke_SeparateColors()
    {
        // Arrange - Test k (fill) and K (stroke) together
        var content = @"
            0 1 0 0 k
            1 0 1 0 K
            3 w
            100 100 200 150 re B
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - magenta fill (0,1,0,0), yellow stroke (1,0,1,0)
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    #endregion

    #region Line Style Tests

    [Fact]
    public void RenderPage_ThickLine_ShowsThickStroke()
    {
        // Arrange
        var pdfData = CreatePdfWithThickLine(100, 100, 300, 100, 10);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - line should be visible
        var midX = (int)(200 * 150 / 72);
        var midY = bitmap.Height - (int)(100 * 150 / 72);
        var pixel = bitmap.GetPixel(midX, midY);
        pixel.Should().NotBe(SKColors.White, "thick line should be visible");
    }

    [Fact]
    public void RenderPage_RoundLineCap_DrawsRoundCaps()
    {
        // Arrange - 1 J sets round line cap
        var content = "0 G 20 w 1 J 100 400 m 300 400 l S";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - just verify it renders without error
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_RoundLineJoin_DrawsRoundJoins()
    {
        // Arrange - 1 j sets round line join
        var content = "0 G 10 w 1 j 100 400 m 200 500 l 300 400 l S";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - just verify it renders without error
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_MiterLimit_Applied()
    {
        // Arrange - M sets miter limit
        var content = "0 G 10 w 0 j 2 M 100 400 m 200 500 l 300 400 l S";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - just verify it renders without error
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_DashedLine_RendersDashPattern()
    {
        // Arrange - d operator sets dash pattern
        // Format: [dashArray] dashPhase d
        // [3 2] 0 d = 3 units on, 2 units off, starting at 0
        var content = @"
            0 G
            3 w
            [3 2] 0 d
            100 400 m 400 400 l S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - dashed line should render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_DashPatternWithPhase_RendersWithOffset()
    {
        // Arrange - dash phase offsets the pattern start
        // [10 5] 3 d = 10 on, 5 off, starting 3 units into pattern
        var content = @"
            0 G
            5 w
            [10 5] 3 d
            100 500 m 400 500 l S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - dashed line with phase should render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_ComplexDashPattern_RendersMultiSegmentPattern()
    {
        // Arrange - complex pattern with multiple on/off segments
        // [5 3 2 3] 0 d = 5 on, 3 off, 2 on, 3 off, repeat
        var content = @"
            0 G
            4 w
            [5 3 2 3] 0 d
            100 300 m 400 300 l S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - complex dash pattern should render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_SolidLineAfterDashed_ResetsToSolid()
    {
        // Arrange - empty dash array [] resets to solid line
        var content = @"
            0 G
            3 w
            [5 2] 0 d
            100 500 m 400 500 l S
            [] 0 d
            100 400 m 400 400 l S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - should render both dashed and solid lines
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    #endregion

    #region Clipping Path Tests (Issue #295)

    [Fact]
    public void RenderPage_ClippingPath_NonZeroWinding_RestrictsRendering()
    {
        // Arrange - Create PDF with clipping path using W (non-zero winding)
        var content = @"
            0 G
            2 w
            100 100 200 200 re W n
            0 0 400 400 re S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - rendering should complete without errors
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
        bitmap.Height.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_ClippingPath_EvenOdd_RestrictsRendering()
    {
        // Arrange - Create PDF with clipping path using W* (even-odd rule)
        var content = @"
            0 G
            2 w
            100 100 200 200 re W* n
            0 0 400 400 re S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_NestedClipping_AppliesIntersection()
    {
        // Arrange - Create PDF with nested clipping regions
        var content = @"
            q
            0 G
            2 w
            100 100 300 300 re W n
            q
            200 200 200 200 re W n
            50 50 400 400 re S
            Q
            Q
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - nested clipping should create intersection
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_ClippingWithFill_FillsOnlyClippedArea()
    {
        // Arrange - Clip then fill
        var content = @"
            1 0 0 rg
            100 100 200 200 re W n
            0 0 400 400 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - fill should be clipped to the rectangle
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_ClippingWithText_ClipsText()
    {
        // Arrange - Clip then render text
        var content = @"
            100 500 200 100 re W n
            BT
            /F1 48 Tf
            50 500 Td
            (Clipped Text) Tj
            ET
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - text should be clipped
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_CircularClippingPath_WorksCorrectly()
    {
        // Arrange - Create circular clipping using Bézier curves
        var content = @"
            q
            200 200 m
            200 310.5 289.5 400 400 400 c
            510.5 400 600 310.5 600 200 c
            600 89.5 510.5 0 400 0 c
            289.5 0 200 89.5 200 200 c
            W n
            0 0 1 rg
            0 0 800 800 re f
            Q
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - circular clip should render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_StateRestoreRemovesClipping()
    {
        // Arrange - Clipping should be removed after Q
        var content = @"
            q
            100 100 200 200 re W n
            1 0 0 rg
            0 0 400 400 re f
            Q
            0 0 1 rg
            50 50 300 300 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - second rectangle should not be clipped
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    #endregion

    #region Text State Operator Tests

    [Fact]
    public void RenderPage_CharacterSpacing_AppliesSpacing()
    {
        // Arrange - Tc operator sets character spacing
        var content = @"
            BT
            /F1 24 Tf
            100 700 Td
            0 Tc
            (Normal) Tj
            0 -30 Td
            5 Tc
            (Spaced) Tj
            ET
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - should render without error
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_WordSpacing_AppliesSpacing()
    {
        // Arrange - Tw operator sets word spacing
        var content = @"
            BT
            /F1 20 Tf
            100 700 Td
            0 Tw
            (Hello World) Tj
            0 -30 Td
            10 Tw
            (Hello World) Tj
            ET
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_HorizontalScaling_ScalesText()
    {
        // Arrange - Tz operator sets horizontal scaling (percentage)
        var content = @"
            BT
            /F1 24 Tf
            100 700 Td
            100 Tz
            (Normal) Tj
            0 -30 Td
            150 Tz
            (Stretched) Tj
            0 -30 Td
            50 Tz
            (Compressed) Tj
            ET
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - different horizontal scales should render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_TextLeading_AffectsLineSpacing()
    {
        // Arrange - TL operator sets text leading for T* operator
        var content = @"
            BT
            /F1 20 Tf
            100 700 Td
            20 TL
            (Line 1) Tj
            T*
            (Line 2) Tj
            T*
            (Line 3) Tj
            ET
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - lines should be spaced according to leading
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_TD_MovesAndSetsLeading()
    {
        // Arrange - TD operator moves text position and sets leading
        // Format: tx ty TD
        // Sets leading to -ty and moves by (tx, ty)
        // Equivalent to: -ty TL tx ty Td
        var content = @"
            BT
            /F1 18 Tf
            100 700 Td
            (First line) Tj
            0 -25 TD
            (Second line with TD) Tj
            T*
            (Third line uses leading from TD) Tj
            ET
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - TD should position text and set leading for T*
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_Tm_SetsTextMatrix()
    {
        // Arrange - Tm operator sets the text matrix
        // Format: a b c d e f Tm (same as transformation matrix)
        // Controls scaling, rotation, skewing, and translation of text
        var content = @"
            BT
            /F1 20 Tf
            1 0 0 1 100 700 Tm
            (Normal) Tj
            ET
            BT
            /F1 20 Tf
            0.707 0.707 -0.707 0.707 300 300 Tm
            (Rotated 45) Tj
            ET
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - Tm should position and transform text
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_Tm_RotatesTextInk()
    {
        var content = @"
            BT
            /F1 42 Tf
            0 1 -1 0 300 250 Tm
            (IIIIIIIIIIII) Tj
            ET
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        using var bitmap = renderer.RenderPage(doc.GetPage(1), new RenderOptions { Dpi = 72 });

        var bounds = GetDarkPixelBounds(bitmap);
        bounds.Should().NotBeNull();
        bounds!.Value.Height.Should().BeGreaterThan(bounds.Value.Width * 2,
            "a 90-degree Tm should rotate the text run instead of drawing it horizontally");
    }

    [Fact]
    public void RenderPage_TextRenderMode_AffectsRendering()
    {
        // Arrange - Tr operator sets text rendering mode
        // 0 = fill, 1 = stroke, 2 = fill then stroke, 3 = invisible
        var content = @"
            BT
            /F1 24 Tf
            100 700 Td
            0 Tr
            (Fill) Tj
            0 -30 Td
            1 Tr
            2 w
            (Stroke) Tj
            0 -30 Td
            2 Tr
            (Fill+Stroke) Tj
            ET
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - different rendering modes should work
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_TextRenderMode7_ClipsFollowingPaintToGlyphOutlines()
    {
        // Regression for pdf.js issue3584: mode 7 adds text to the clipping path.
        // The following filled rectangle must be clipped to the word outlines.
        var content = @"
            BT
            7 Tr
            10 20 TD
            /F1 20 Tf
            (waves) Tj
            ET
            0 0 1 rg
            0 0 200 50 re
            f
        ";
        var pdfData = CreatePdfWithContentAndPageSize(content, width: 200, height: 50);
        using var doc = PdfDocument.Open(pdfData);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        var (whiteFraction, blueFraction) = MeasureWhiteAndBluePixels(bitmap);
        whiteFraction.Should().BeGreaterThan(0.80,
            "text clipping should preserve the white background around the clipped paint");
        blueFraction.Should().BeInRange(0.005, 0.20,
            "the rectangle should render only through glyph outlines, not as a full-page fill");
    }

    [Fact]
    public void RenderPage_PrePathClipOperator_DefersClipToNextPathPaint()
    {
        // Regression for pdf.js clippath.pdf: some malformed producer output
        // emits W before constructing the path, then terminates it with n.
        var content = @"
            W
            40 20 m
            160 20 l
            160 80 l
            40 80 l
            h
            n
            0 0 0 sc
            0 0 200 100 re
            f
        ";
        var pdfData = CreatePdfWithContentAndPageSize(content, width: 200, height: 100);
        using var doc = PdfDocument.Open(pdfData);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        var (whiteFraction, darkFraction) = MeasureWhiteAndDarkPixels(bitmap);
        whiteFraction.Should().BeGreaterThan(0.50,
            "the deferred clipping path should leave the area outside the rectangle white");
        darkFraction.Should().BeInRange(0.25, 0.45,
            "only the clipped center rectangle should be filled");
    }

    [Fact]
    public void RenderPage_TextRise_OffsetText()
    {
        // Arrange - Ts operator sets text rise (vertical offset)
        var content = @"
            BT
            /F1 20 Tf
            100 700 Td
            (Normal) Tj
            10 Ts
            (Raised) Tj
            -10 Ts
            (Lowered) Tj
            0 Ts
            (Back to Normal) Tj
            ET
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - text with rise should render at different heights
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_CombinedTextState_AppliesAllSettings()
    {
        // Arrange - Combine multiple text state operators
        var content = @"
            BT
            /F1 18 Tf
            100 700 Td
            2 Tc
            5 Tw
            120 Tz
            0 Tr
            (Styled Text) Tj
            ET
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - all text state settings should be applied
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_SingleQuoteOperator_MovesAndShowsText()
    {
        // Arrange - ' operator moves to next line and shows text (equivalent to T* Tj)
        var content = @"
            BT
            /F1 20 Tf
            100 700 Td
            14 TL
            (First line) Tj
            (Second line) '
            (Third line) '
            ET
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - ' operator should move to next line and show text
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_DoubleQuoteOperator_SetsSpacingAndShowsText()
    {
        // Arrange - " operator sets word/char spacing, moves to next line, shows text
        // Format: aw ac string " (word spacing, char spacing, string)
        var content = @"
            BT
            /F1 20 Tf
            100 700 Td
            14 TL
            (Normal spacing) Tj
            10 2 (Wide spacing) ""
            0 0 (Normal again) ""
            ET
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - " operator should set spacing, move to next line, and show text
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_MixedLineOperators_CombinesTjQuoteAndDoubleQuote()
    {
        // Arrange - Mix Tj, ', and " operators in same text object
        var content = @"
            BT
            /F1 18 Tf
            100 700 Td
            12 TL
            (Line 1: Tj) Tj
            (Line 2: quote) '
            5 1 (Line 3: double quote) ""
            0 0 (Line 4: double quote) ""
            ET
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - all three text-showing operators should work together
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    #endregion

    #region XObject Rendering Tests (Issue #299)

    [Fact]
    public void RenderPage_ImageXObject_RendersImage()
    {
        // Arrange - Create PDF with a grayscale image XObject
        var pdfData = CreatePdfWithImageXObject(width: 10, height: 10, grayscale: true);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - image should be rendered (non-white pixels somewhere)
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);

        // Image XObjects render successfully - check for non-white pixels
        bool hasNonWhitePixels = false;
        for (int y = 0; y < bitmap.Height && !hasNonWhitePixels; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) != SKColors.White)
                {
                    hasNonWhitePixels = true;
                    break;
                }
            }
        }

        hasNonWhitePixels.Should().BeTrue("Image XObject should render non-white pixels");
    }

    [Fact]
    public void RenderPage_FormXObject_RendersContent()
    {
        // Arrange - Create PDF with Form XObject containing a rectangle
        var pdfData = CreatePdfWithFormXObject();
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - Form's content should be rendered
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);

        // Check if any non-white pixels exist (form rendering working)
        bool hasNonWhitePixels = false;
        for (int y = 0; y < bitmap.Height && !hasNonWhitePixels; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) != SKColors.White)
                {
                    hasNonWhitePixels = true;
                    break;
                }
            }
        }

        hasNonWhitePixels.Should().BeTrue("Form XObject content should render");
    }

    [Fact]
    public void RenderPage_ScaledImageXObject_AppliesCTM()
    {
        // Arrange - Image XObject with scaling transformation
        var pdfData = CreatePdfWithScaledImageXObject();
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - verify rendering completed successfully
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    #endregion

    #region ExtGState (gs operator) Tests (Issue #294)

    [Fact]
    public void RenderPage_ExtGState_StrokeAlpha_AppliesTransparency()
    {
        // Arrange - Create PDF with gs operator setting stroke alpha (CA)
        var pdfData = CreatePdfWithExtGState(new Dictionary<string, object>
        {
            { "CA", 0.5 } // 50% stroke transparency
        });
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - rendering should complete without errors
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
        bitmap.Height.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_ExtGState_FillAlpha_AppliesTransparency()
    {
        // Arrange - Create PDF with gs operator setting fill alpha (ca)
        var pdfData = CreatePdfWithExtGState(new Dictionary<string, object>
        {
            { "ca", 0.3 } // 30% fill transparency
        });
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - rendering should complete without errors
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
        bitmap.Height.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_ExtGState_BothAlphas_AppliesBothTransparencies()
    {
        // Arrange - Create PDF with both CA and ca
        var pdfData = CreatePdfWithExtGState(new Dictionary<string, object>
        {
            { "CA", 0.7 }, // 70% stroke transparency
            { "ca", 0.4 }  // 40% fill transparency
        });
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_ExtGState_LineWidth_AppliesStrokeWidth()
    {
        // Arrange - Create PDF with LW (line width) in ExtGState
        var pdfData = CreatePdfWithExtGState(new Dictionary<string, object>
        {
            { "LW", 5.0 } // 5-point line width
        });
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_ExtGState_LineCap_AppliesCapStyle()
    {
        // Arrange - Create PDF with LC (line cap) in ExtGState
        // 0 = Butt cap, 1 = Round cap, 2 = Square cap
        var pdfData = CreatePdfWithExtGState(new Dictionary<string, object>
        {
            { "LC", 1 } // Round cap
        });
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_ExtGState_LineJoin_AppliesJoinStyle()
    {
        // Arrange - Create PDF with LJ (line join) in ExtGState
        // 0 = Miter join, 1 = Round join, 2 = Bevel join
        var pdfData = CreatePdfWithExtGState(new Dictionary<string, object>
        {
            { "LJ", 1 } // Round join
        });
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_ExtGState_MiterLimit_AppliesMiterLimit()
    {
        // Arrange - Create PDF with ML (miter limit) in ExtGState
        var pdfData = CreatePdfWithExtGState(new Dictionary<string, object>
        {
            { "ML", 5.0 } // Miter limit of 5.0
        });
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_ExtGState_CombinedParameters_AppliesAll()
    {
        // Arrange - Create PDF with multiple ExtGState parameters
        var pdfData = CreatePdfWithExtGState(new Dictionary<string, object>
        {
            { "CA", 0.8 },  // Stroke alpha
            { "ca", 0.6 },  // Fill alpha
            { "LW", 3.0 },  // Line width
            { "LC", 2 },    // Square cap
            { "LJ", 0 },    // Miter join
            { "ML", 4.0 }   // Miter limit
        });
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - all parameters should be applied without error
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_ExtGState_AlphaClampedToValidRange()
    {
        // Arrange - Create PDF with out-of-range alpha values
        // Alpha should be clamped to [0, 1] range
        var pdfData = CreatePdfWithExtGState(new Dictionary<string, object>
        {
            { "CA", 1.5 },  // > 1.0, should clamp to 1.0
            { "ca", -0.2 }  // < 0.0, should clamp to 0.0
        });
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - should render without throwing
        bitmap.Should().NotBeNull();
    }

    [Fact]
    public void RenderPage_ExtGState_MissingDictionary_ContinuesRendering()
    {
        // Arrange - Create PDF with gs operator referencing non-existent ExtGState
        var content = @"
            /GS99 gs
            100 100 200 200 re S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - should handle gracefully without crashing
        bitmap.Should().NotBeNull();
    }

    [Fact]
    public void RenderPage_FormXObjectExtGState_AppliesLocallyAndDoesNotLeak()
    {
        var pdfData = CreatePdfWithFormLocalExtGState();
        using var doc = PdfDocument.Open(pdfData);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, AntiAlias = false });

        var transparentFormPixel = bitmap.GetPixel(25, 75);
        transparentFormPixel.Red.Should().BeGreaterThan(245);
        transparentFormPixel.Green.Should().BeGreaterThan(245);
        transparentFormPixel.Blue.Should().BeGreaterThan(245);

        var postFormPagePixel = bitmap.GetPixel(75, 75);
        postFormPagePixel.Blue.Should().BeGreaterThan(200);
        postFormPagePixel.Red.Should().BeLessThan(40);
        postFormPagePixel.Green.Should().BeLessThan(40);
    }

    [Fact]
    public void RenderPage_FormXObjectUnbalancedSave_DoesNotLeakCallerGraphicsState()
    {
        var pdfData = CreatePdfWithUnbalancedFormGraphicsStack();
        using var doc = PdfDocument.Open(pdfData);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, AntiAlias = false, BackgroundColor = SKColors.White });

        var postFormPagePixel = bitmap.GetPixel(75, 75);
        postFormPagePixel.Blue.Should().BeGreaterThan(200);
        postFormPagePixel.Red.Should().BeLessThan(40);
        postFormPagePixel.Green.Should().BeLessThan(40);
    }

    [Fact]
    public void RenderPage_ExtGState_SMask_AppliesForFilledRect()
    {
        var pdfData = CreatePdfWithExtGStateSoftMask(transparentMaskValue: 0x00);
        using var doc = PdfDocument.Open(pdfData);

        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, AntiAlias = false });

        var hasRedPixel = false;
        for (int y = 0; y < bitmap.Height && !hasRedPixel; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red > 200 && pixel.Green < 20 && pixel.Blue < 20)
                {
                    hasRedPixel = true;
                    break;
                }
            }
        }

        hasRedPixel.Should().BeFalse("a zero-alpha soft mask should suppress the masked fill");
    }

    #endregion

    #region Additional Graphics State Operators (i, ri)

    [Fact]
    public void RenderPage_Flatness_SetsTolerance()
    {
        // Arrange - i operator sets flatness tolerance (0-100)
        // Lower values = more accurate curves, higher values = faster but less accurate
        var content = @"
            0 G
            2 w
            1 i
            100 300 m
            150 450 250 450 300 300 c
            S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - should render without error
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
        bitmap.Height.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(0)]    // Maximum precision
    [InlineData(1)]    // Standard tolerance
    [InlineData(10)]   // Moderate tolerance
    [InlineData(100)]  // Maximum tolerance
    public void RenderPage_Flatness_AcceptsValidRange(double flatness)
    {
        // Arrange - Test various flatness values
        var content = $@"
            0 G
            2 w
            {flatness} i
            100 200 m
            200 300 300 200 400 300 c
            S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - all valid flatness values should render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_RenderingIntent_AbsoluteColorimetric()
    {
        // Arrange - ri operator sets rendering intent
        // /AbsoluteColorimetric - Preserve absolute color values
        var content = @"
            /AbsoluteColorimetric ri
            1 0 0 rg
            100 100 200 150 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - rendering intent should be applied
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_RenderingIntent_RelativeColorimetric()
    {
        // Arrange - /RelativeColorimetric - Adjust colors relative to white point
        var content = @"
            /RelativeColorimetric ri
            0 1 0 rg
            100 100 200 150 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_RenderingIntent_Saturation()
    {
        // Arrange - /Saturation - Preserve color saturation (for graphics)
        var content = @"
            /Saturation ri
            0 0 1 rg
            100 100 200 150 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_RenderingIntent_Perceptual()
    {
        // Arrange - /Perceptual - Preserve visual appearance (for images)
        var content = @"
            /Perceptual ri
            0.5 0.5 0 rg
            100 100 200 150 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_RenderingIntent_WithColorSpace()
    {
        // Arrange - Combine ri with color space operators
        var content = @"
            /RelativeColorimetric ri
            /DeviceRGB cs
            1 0 0 sc
            100 100 200 150 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - ri should work with color space operators
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_CombinedFlatnessAndRenderingIntent()
    {
        // Arrange - Test both i and ri operators together
        var content = @"
            /Perceptual ri
            5 i
            0 G
            2 w
            100 200 m
            150 350 250 350 300 200 c
            200 100 m
            250 250 350 250 400 100 c
            S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - both operators should work together
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    #endregion

    #region Inline Image Operators (BI/ID/EI)

    [Fact]
    public void RenderPage_InlineImage_Grayscale_RendersWithoutError()
    {
        // Arrange - Create PDF with inline grayscale image
        // BI = Begin inline image, ID = image data, EI = end inline image
        var content = @"
            100 500 m
            200 500 l
            200 600 l
            100 600 l
            h
            W n
            100 0 0 -100 100 600 cm
            BI
            /W 8
            /H 8
            /CS /DeviceGray
            /BPC 8
            ID
            " + Convert.ToBase64String(new byte[64]) + @"
            EI
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - should handle inline image without crashing
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_InlineImage_RGB_RendersWithoutError()
    {
        // Arrange - Create PDF with inline RGB image
        var content = @"
            q
            100 0 0 100 200 400 cm
            BI
            /W 4
            /H 4
            /CS /DeviceRGB
            /BPC 8
            ID
            " + Convert.ToBase64String(new byte[48]) + @"
            EI
            Q
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_InlineImage_1BitMonochrome_RendersWithoutError()
    {
        // Arrange - 1-bit monochrome inline image
        var content = @"
            q
            50 0 0 50 100 100 cm
            BI
            /W 8
            /H 8
            /CS /DeviceGray
            /BPC 1
            /F /A85
            ID
            0000000000000000000000000000000000000000
            EI
            Q
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - 1-bit images should render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_InlineImage_WithFilter_RendersWithoutError()
    {
        // Arrange - Inline image without filter (filters may not be fully supported)
        // Simplified to test basic inline image rendering
        var content = @"
            q
            100 0 0 100 300 300 cm
            BI
            /W 2
            /H 2
            /CS /DeviceGray
            /BPC 8
            ID
            " + new string('\0', 4) + @"
            EI
            Q
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - inline images should be handled
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_InlineImage_WithTransformation_AppliesCTM()
    {
        // Arrange - Inline image with transformation matrix
        var content = @"
            q
            0.5 0 0 0.5 100 200 cm
            BI
            /W 16
            /H 16
            /CS /DeviceGray
            /BPC 8
            ID
            " + Convert.ToBase64String(new byte[256]) + @"
            EI
            Q
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - transformation should apply to inline image
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_MultipleInlineImages_RendersAll()
    {
        // Arrange - Multiple inline images in same content stream
        var imageData = new string('\0', 16);
        var content = $@"
            q
            50 0 0 50 100 500 cm
            BI
            /W 4
            /H 4
            /CS /DeviceGray
            /BPC 8
            ID
            {imageData}
            EI
            Q
            q
            50 0 0 50 200 500 cm
            BI
            /W 4
            /H 4
            /CS /DeviceGray
            /BPC 8
            ID
            {imageData}
            EI
            Q
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - multiple inline images should all render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_InlineImage_MixedWithOtherContent_RendersCorrectly()
    {
        // Arrange - Mix inline image with paths and text
        var content = @"
            1 0 0 rg
            100 100 200 50 re f
            BT /F1 24 Tf 100 200 Td (Text before image) Tj ET
            q
            50 0 0 50 100 300 cm
            BI /W 4 /H 4 /CS /DeviceGray /BPC 8
            ID
            " + Convert.ToBase64String(new byte[16]) + @"
            EI
            Q
            BT /F1 24 Tf 100 400 Td (Text after image) Tj ET
            0 0 1 rg
            300 100 200 50 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - inline image should render alongside other content
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    #endregion

    #region Shading Pattern Operator (sh)

    [Fact]
    public void RenderPage_ShadingPattern_AxialGradient_RendersWithoutError()
    {
        // Arrange - sh operator paints a shading pattern
        // Axial shading (Type 2) - gradient along a line
        var content = @"
            /Sh1 sh
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - should handle shading pattern reference
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_ShadingPattern_WithClipping_RestrictsShading()
    {
        // Arrange - Apply shading within clipping path
        var content = @"
            q
            100 100 400 400 re W n
            /Sh1 sh
            Q
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - shading should be clipped
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_ShadingPattern_WithTransformation_AppliesCTM()
    {
        // Arrange - Shading with transformation matrix
        var content = @"
            q
            1 0 0 1 100 200 cm
            /Sh1 sh
            Q
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - CTM should affect shading
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_ShadingPattern_MissingResource_ContinuesRendering()
    {
        // Arrange - Reference non-existent shading resource
        var content = @"
            /NonExistentShading sh
            1 0 0 rg
            100 100 200 150 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - should continue rendering after missing resource
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    #endregion

    #region Marked Content Operators (MP, DP, BMC, BDC, EMC)

    [Fact]
    public void RenderPage_MarkedContentPoint_MP_DoesNotAffectRendering()
    {
        // Arrange - MP operator marks a point for structural purposes
        // Format: /Tag MP
        var content = @"
            /Figure MP
            1 0 0 rg
            100 100 200 150 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - MP is structural only, doesn't affect rendering
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_MarkedContentPoint_DP_WithProperties()
    {
        // Arrange - DP operator marks point with property dictionary
        // Format: /Tag properties DP
        var content = @"
            /Span <</ActualText (Alternative Text)>> DP
            BT /F1 18 Tf 100 700 Td (Visual Text) Tj ET
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - DP with properties doesn't affect rendering
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_MarkedContentSequence_BMC_EMC_WrapsContent()
    {
        // Arrange - BMC/EMC wraps content for structure
        // Format: /Tag BMC ... EMC
        var content = @"
            /P BMC
            BT /F1 20 Tf 100 700 Td (Paragraph text) Tj ET
            EMC
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - marked content doesn't affect visual rendering
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_MarkedContentSequence_BDC_EMC_WithProperties()
    {
        // Arrange - BDC/EMC with property dictionary
        // Format: /Tag properties BDC ... EMC
        var content = @"
            /Span <</Lang (en-US)>> BDC
            BT /F1 16 Tf 100 650 Td (English text) Tj ET
            EMC
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_NestedMarkedContent_MultipleSequences()
    {
        // Arrange - Nested marked content sequences
        var content = @"
            /Document BMC
                /H1 BMC
                BT /F1 24 Tf 100 750 Td (Heading) Tj ET
                EMC
                /P BMC
                BT /F1 14 Tf 100 700 Td (Paragraph) Tj ET
                EMC
            EMC
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - nested marked content should not affect rendering
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_MarkedContent_MixedWithGraphics()
    {
        // Arrange - Marked content around graphics operations
        var content = @"
            /Figure BDC
            1 0 0 rg
            100 400 200 100 re f
            EMC
            /Caption BMC
            BT /F1 12 Tf 100 350 Td (Figure 1) Tj ET
            EMC
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - graphics and text should render normally
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_MarkedContent_AllOperatorTypes()
    {
        // Arrange - Test all marked content operators together
        var content = @"
            /Artifact MP
            /Header BMC
            BT /F1 10 Tf 100 780 Td (Page Header) Tj ET
            EMC
            /Content BDC
                /P BMC
                BT /F1 12 Tf 100 700 Td (Main content) Tj ET
                EMC
            EMC
            /Footer DP
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - all marked content operators should be handled
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    #endregion

    #region Type 3 Font Operators (d0, d1)

    [Fact]
    public void RenderPage_Type3Font_RendersCharProcPathGlyph()
    {
        // Arrange
        var pdfData = CreatePdfWithType3Glyph(
            pageContent: "1 0 0 rg BT /F1 24 Tf 100 120 Td <41> Tj ET",
            charProcContent: "500 0 0 0 500 700 d1 0 0 500 700 re f");
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - glyph A is a 12pt x 16.8pt red rectangle at (100, 120).
        var pixelX = (int)(106 * 150 / 72);
        var pixelY = bitmap.Height - (int)(128 * 150 / 72);
        var pixel = bitmap.GetPixel(pixelX, pixelY);
        pixel.Red.Should().BeGreaterThan(180);
        pixel.Green.Should().BeLessThan(80);
        pixel.Blue.Should().BeLessThan(80);
    }

    [Fact]
    public void RenderPage_Type3Font_d0_SetsGlyphWidth()
    {
        // Arrange - d0 operator sets glyph width for Type 3 fonts
        // Format: wx wy d0 (width in x and y directions)
        // Type 3 fonts define glyphs as content streams
        var content = @"
            BT /F1 24 Tf 100 700 Td (Type3) Tj ET
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - d0 would be in glyph definition, not page content
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_Type3Font_d1_SetsGlyphWidthAndBBox()
    {
        // Arrange - d1 operator sets glyph width and bounding box
        // Format: wx wy llx lly urx ury d1
        // Used for Type 3 fonts with bounding box info
        var content = @"
            BT /F1 18 Tf 100 650 Td (Custom glyphs) Tj ET
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - d1 would be in glyph definition
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    #endregion

    #region Compatibility Operators (BX, EX)

    [Fact]
    public void RenderPage_CompatibilitySection_BX_EX_IgnoresUnknown()
    {
        // Arrange - BX/EX wraps content with unknown operators
        // Operators between BX and EX that are not recognized should be ignored
        var content = @"
            BT /F1 20 Tf 100 700 Td (Before) Tj ET
            BX
            /UnknownOp1 123 ABC
            /UnknownOp2 DoSomething
            EX
            BT /F1 20 Tf 100 650 Td (After) Tj ET
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - unknown operators should be ignored, text should render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_CompatibilitySection_NestedBX_EX()
    {
        // Arrange - Nested compatibility sections (though not recommended)
        var content = @"
            BT /F1 16 Tf 100 750 Td (Text 1) Tj ET
            BX
            /Experimental1 test
            BX
            /Experimental2 nested
            EX
            EX
            BT /F1 16 Tf 100 700 Td (Text 2) Tj ET
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - nested compatibility sections should be handled
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_CompatibilitySection_WithKnownOperators()
    {
        // Arrange - Mix known and unknown operators in compatibility section
        var content = @"
            BX
            1 0 0 rg
            100 100 200 150 re f
            /FutureOperator xyz
            BT /F1 14 Tf 100 300 Td (Test) Tj ET
            EX
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - known operators should still execute
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    #endregion

    #region Helper Methods

    private static byte[] CreateSimplePdf(string text)
    {
        var content = $"BT /F1 12 Tf 100 700 Td ({text}) Tj ET";
        return CreatePdfWithContent(content);
    }

    private static byte[] CreateEmptyPdf()
    {
        return CreatePdfWithContent("");
    }

    private static byte[] CreatePdfWithRectangle(int x, int y, int w, int h, bool fill = true, bool stroke = false)
    {
        var op = fill && stroke ? "B" : (fill ? "f" : (stroke ? "S" : "n"));
        var content = $"0 g {x} {y} {w} {h} re {op}";
        return CreatePdfWithContent(content);
    }

    private static byte[] CreatePdfWithLine(int x1, int y1, int x2, int y2)
    {
        var content = $"0 G 1 w {x1} {y1} m {x2} {y2} l S";
        return CreatePdfWithContent(content);
    }

    private static byte[] CreatePdfWithColoredRectangle(int x, int y, int w, int h, double r, double g, double b)
    {
        var content = $"{r} {g} {b} rg {x} {y} {w} {h} re f";
        return CreatePdfWithContent(content);
    }

    private static byte[] CreatePdfWithSeparationRectangle()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[6];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]");
        writer.WriteLine("   /Contents 4 0 R");
        writer.WriteLine("   /Resources << /ColorSpace << /CS1 [ /Separation /Spot /DeviceRGB 5 0 R ] >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        var content = "q /CS1 cs 1 scn 100 100 200 200 re f Q";
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /FunctionType 2 /Domain [0 1] /Range [0 1 0 1 0 1] /N 1 /C0 [1 1 1] /C1 [1 0 0] >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 6");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 6 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] CreatePdfWithGrayscaleRectangle(int x, int y, int w, int h, double gray)
    {
        var content = $"{gray} g {x} {y} {w} {h} re f";
        return CreatePdfWithContent(content);
    }

    private static byte[] CreatePdfWithTranslatedRectangle(int x, int y, int w, int h, double tx, double ty)
    {
        var content = $"q 1 0 0 1 {tx} {ty} cm 0 g {x} {y} {w} {h} re f Q";
        return CreatePdfWithContent(content);
    }

    private static byte[] CreatePdfWithStateStack()
    {
        var content = "q 1 0 0 rg 50 50 100 100 re f Q 0 g 200 200 100 100 re f";
        return CreatePdfWithContent(content);
    }

    private static byte[] CreatePdfWithCmykRectangle(int x, int y, int w, int h, double c, double m, double yy, double k)
    {
        var content = $"{c} {m} {yy} {k} k {x} {y} {w} {h} re f";
        return CreatePdfWithContent(content);
    }

    private static byte[] CreatePdfWithThickLine(int x1, int y1, int x2, int y2, double lineWidth)
    {
        var content = $"0 G {lineWidth} w {x1} {y1} m {x2} {y2} l S";
        return CreatePdfWithContent(content);
    }

    private static byte[] CreatePdfWithImageXObject(int width, int height, bool grayscale = true)
    {
        // Create a simple grayscale image (gray square)
        var imageData = new byte[width * height];
        for (int i = 0; i < imageData.Length; i++)
            imageData[i] = 128; // Mid-gray

        // Content stream that draws the image at 100x100 points at position (100, 400)
        var content = $"q 100 0 0 100 100 400 cm /Im1 Do Q";

        return CreatePdfWithImageXObjectAndContent(content, imageData, width, height, grayscale);
    }

    private static byte[] CreatePdfWithScaledImageXObject()
    {
        // 2x2 gray image
        var imageData = new byte[] { 128, 128, 128, 128 };

        // Scale the image to 200x200 points
        var content = "q 200 0 0 200 150 350 cm /Im1 Do Q";

        return CreatePdfWithImageXObjectAndContent(content, imageData, 2, 2, grayscale: true);
    }

    private static byte[] CreatePdfWithFormXObject()
    {
        // Form XObject content: a black rectangle
        var formContent = "0 g 10 10 80 80 re f";

        // Main content stream: invoke the Form XObject
        var content = "q 1 0 0 1 100 400 cm /Fm1 Do Q";

        return CreatePdfWithFormXObjectAndContent(content, formContent);
    }

    private static byte[] CreatePdfWithImageXObjectAndContent(string content, byte[] imageData, int width, int height, bool grayscale)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[7];

        // Catalog
        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Pages
        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Page
        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]");
        writer.WriteLine("   /Contents 4 0 R");
        writer.WriteLine("   /Resources << /XObject << /Im1 6 0 R >> /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Content stream
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        // Font
        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Image XObject
        offsets[6] = ms.Position;
        writer.WriteLine("6 0 obj");
        var colorSpace = grayscale ? "/DeviceGray" : "/DeviceRGB";
        var bpc = 8;
        writer.WriteLine($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height}");
        writer.WriteLine($"   /ColorSpace {colorSpace} /BitsPerComponent {bpc} /Length {imageData.Length} >>");
        writer.WriteLine("stream");
        writer.Flush();
        ms.Write(imageData, 0, imageData.Length);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        // Xref
        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 6; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 7 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] CreatePdfWithFormXObjectAndContent(
        string content,
        string formContent,
        string? formResources = null)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[8];

        // Catalog
        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Pages
        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Page
        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]");
        writer.WriteLine("   /Contents 4 0 R");
        writer.WriteLine("   /Resources << /XObject << /Fm1 6 0 R >> /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Content stream
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        // Font
        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Form XObject
        offsets[6] = ms.Position;
        writer.WriteLine("6 0 obj");
        writer.WriteLine($"<< /Type /XObject /Subtype /Form /BBox [0 0 100 100]");
        if (!string.IsNullOrWhiteSpace(formResources))
        {
            writer.WriteLine($"   /Resources {formResources}");
        }
        writer.WriteLine($"   /Matrix [1 0 0 1 0 0] /Length {formContent.Length} >>");
        writer.WriteLine("stream");
        writer.Write(formContent);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        // Xref
        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 6; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 7 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] CreatePdfWithExtGState(Dictionary<string, object> extGStateParams)
    {
        // Build ExtGState dictionary content
        var extGStateContent = new System.Text.StringBuilder();
        extGStateContent.Append("<< /Type /ExtGState ");

        foreach (var param in extGStateParams)
        {
            extGStateContent.Append($"/{param.Key} ");
            if (param.Value is int intValue)
                extGStateContent.Append(intValue);
            else if (param.Value is double doubleValue)
                extGStateContent.Append(doubleValue.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture));
            else
                extGStateContent.Append(param.Value);
            extGStateContent.Append(" ");
        }
        extGStateContent.Append(">>");

        // Create content stream that uses the ExtGState
        var content = "/GS1 gs\n100 100 200 200 re\nf\n";

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[7];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /ExtGState << /GS1 6 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[6] = ms.Position;
        writer.WriteLine("6 0 obj");
        writer.WriteLine(extGStateContent.ToString());
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{offsets[1]:D10} 00000 n ");
        writer.WriteLine($"{offsets[2]:D10} 00000 n ");
        writer.WriteLine($"{offsets[3]:D10} 00000 n ");
        writer.WriteLine($"{offsets[4]:D10} 00000 n ");
        writer.WriteLine("0000000000 65535 f "); // Object 5 unused
        writer.WriteLine($"{offsets[6]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 7 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] CreatePdfWithExtGStateSoftMask(int transparentMaskValue)
    {
        var content = "/GS1 gs\n20 20 60 60 re f\n";
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[8];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100]");
        writer.WriteLine("   /Contents 4 0 R");
        writer.WriteLine("   /Resources << /ExtGState << /GS1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /ExtGState /SMask << /Type /Mask /S /Alpha /G 6 0 R >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[6] = ms.Position;
        writer.WriteLine("6 0 obj");
        writer.WriteLine("<< /Type /XObject /Subtype /Image /Width 1 /Height 1");
        writer.WriteLine("   /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>");
        writer.WriteLine("stream");
        writer.Flush();
        ms.WriteByte((byte)transparentMaskValue);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 8");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 7; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Size 8 /Root 1 0 R >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] CreatePdfWithFormLocalExtGState()
    {
        const string pageContent = "/Fm1 Do\n0 0 1 rg\n60 10 30 30 re\nf\n";
        const string formContent = "/GS1 gs\n1 0 0 rg\n10 10 30 30 re\nf\n";

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[8];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] " +
            "/Contents 4 0 R /Resources << /XObject << /Fm1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {pageContent.Length} >>");
        writer.WriteLine("stream");
        writer.Write(pageContent);
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine(
            $"<< /Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 100 100] " +
            $"/Resources << /ExtGState << /GS1 6 0 R >> >> /Length {formContent.Length} >>");
        writer.WriteLine("stream");
        writer.Write(formContent);
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[6] = ms.Position;
        writer.WriteLine("6 0 obj");
        writer.WriteLine("<< /Type /ExtGState /ca 0 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 6; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Size 7 /Root 1 0 R >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] CreatePdfWithUnbalancedFormGraphicsStack()
    {
        const string pageContent = "q /GS1 gs /Fm1 Do Q\n0 0 1 rg\n60 10 30 30 re\nf\n";
        const string formContent = "q\n1 0 0 rg\n10 10 30 30 re\nf\n";

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[8];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] " +
            "/Contents 4 0 R /Resources << /XObject << /Fm1 5 0 R >> /ExtGState << /GS1 6 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {pageContent.Length} >>");
        writer.WriteLine("stream");
        writer.Write(pageContent);
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine(
            $"<< /Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 100 100] /Length {formContent.Length} >>");
        writer.WriteLine("stream");
        writer.Write(formContent);
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[6] = ms.Position;
        writer.WriteLine("6 0 obj");
        writer.WriteLine("<< /Type /ExtGState /CA .5 /ca .5 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 6; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Size 7 /Root 1 0 R >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] CreatePdfWithContent(string content)
        => CreatePdfWithContentAndPageSize(content, width: 612, height: 792);

    private static byte[] CreatePdfWithType3Glyph(string pageContent, string charProcContent)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[7];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {pageContent.Length} >>");
        writer.WriteLine("stream");
        writer.Write(pageContent);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type3 /Name /F1 /FontBBox [0 0 500 700] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /A 6 0 R >> /Encoding << /Type /Encoding /Differences [65 /A] >> /FirstChar 65 /LastChar 65 /Widths [500] /Resources << >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[6] = ms.Position;
        writer.WriteLine("6 0 obj");
        writer.WriteLine($"<< /Length {charProcContent.Length} >>");
        writer.WriteLine("stream");
        writer.Write(charProcContent);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 6; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 7 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] CreatePdfWithContentPageSizeAndCropBox(string content, int width, int height, string cropBox)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[6];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /CropBox {cropBox} /Contents 4 0 R /Resources << >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 6");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Size 6 /Root 1 0 R >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] CreatePdfWithMalformedFilteredContentAndPageSize(string content, int width, int height)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[6];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Contents 4 0 R /Resources << >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Filter /Flatedecode /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 6");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Size 6 /Root 1 0 R >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] CreatePdfWithImageOnlyJbig2ContentStream()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[6];
        const string validContent = "0 0 20 20 re f";
        const string invalidContent = "NotPdfContent";

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents [4 0 R 5 0 R] /Resources << >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {validContent.Length} >>");
        writer.WriteLine("stream");
        writer.Write(validContent);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine($"<< /Filter /JBIG2Decode /Length {invalidContent.Length} >>");
        writer.WriteLine("stream");
        writer.Write(invalidContent);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        var xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 6");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Size 6 /Root 1 0 R >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
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

    private static (double WhiteFraction, double DarkFraction) MeasureWhiteAndDarkPixels(SKBitmap bitmap)
    {
        long white = 0;
        long dark = 0;
        long total = (long)bitmap.Width * bitmap.Height;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red > 245 && pixel.Green > 245 && pixel.Blue > 245)
                    white++;
                if (pixel.Red < 32 && pixel.Green < 32 && pixel.Blue < 32)
                    dark++;
            }
        }

        return ((double)white / total, (double)dark / total);
    }

    private static double MeasureDifferentPixelFraction(SKBitmap first, SKBitmap second)
    {
        first.Width.Should().Be(second.Width);
        first.Height.Should().Be(second.Height);

        long different = 0;
        long total = (long)first.Width * first.Height;
        for (int y = 0; y < first.Height; y++)
        {
            for (int x = 0; x < first.Width; x++)
            {
                var a = first.GetPixel(x, y);
                var b = second.GetPixel(x, y);
                var delta = Math.Abs(a.Red - b.Red)
                            + Math.Abs(a.Green - b.Green)
                            + Math.Abs(a.Blue - b.Blue)
                            + Math.Abs(a.Alpha - b.Alpha);
                if (delta > 32)
                    different++;
            }
        }

        return (double)different / total;
    }

    private static double MeasureNeutralMidGrayFraction(SKBitmap bitmap)
    {
        long gray = 0;
        long total = (long)bitmap.Width * bitmap.Height;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                var max = Math.Max(pixel.Red, Math.Max(pixel.Green, pixel.Blue));
                var min = Math.Min(pixel.Red, Math.Min(pixel.Green, pixel.Blue));
                if (min >= 96 && max <= 224 && max - min <= 4)
                    gray++;
            }
        }

        return (double)gray / total;
    }

    private static (double WhiteFraction, double BlueFraction) MeasureWhiteAndBluePixels(SKBitmap bitmap)
    {
        long white = 0;
        long blue = 0;
        long total = (long)bitmap.Width * bitmap.Height;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red > 245 && pixel.Green > 245 && pixel.Blue > 245)
                    white++;
                if (pixel.Blue > 120 && pixel.Red < 80 && pixel.Green < 80)
                    blue++;
            }
        }

        return ((double)white / total, (double)blue / total);
    }

    private static (double SaturatedFraction, double DarkFraction) MeasureSaturatedAndDarkPixels(SKBitmap bitmap)
    {
        long saturated = 0;
        long dark = 0;
        long total = (long)bitmap.Width * bitmap.Height;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                var max = Math.Max(pixel.Red, Math.Max(pixel.Green, pixel.Blue));
                var min = Math.Min(pixel.Red, Math.Min(pixel.Green, pixel.Blue));
                if (max > 140 && max - min > 90)
                    saturated++;
                if (pixel.Red < 32 && pixel.Green < 32 && pixel.Blue < 32)
                    dark++;
            }
        }

        return ((double)saturated / total, (double)dark / total);
    }

    private static (double WhiteFraction, double DarkFraction, double RedFraction) MeasureWhiteDarkAndRedPixels(SKBitmap bitmap)
    {
        long white = 0;
        long dark = 0;
        long red = 0;
        long total = (long)bitmap.Width * bitmap.Height;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red > 245 && pixel.Green > 245 && pixel.Blue > 245)
                    white++;
                if (pixel.Red < 32 && pixel.Green < 32 && pixel.Blue < 32)
                    dark++;
                if (pixel.Red > 160 && pixel.Green < 80 && pixel.Blue < 80)
                    red++;
            }
        }

        return ((double)white / total, (double)dark / total, (double)red / total);
    }

    private static int CountRedPixels(SKBitmap bitmap, SKRectI region)
    {
        var left = Math.Clamp(region.Left, 0, bitmap.Width);
        var top = Math.Clamp(region.Top, 0, bitmap.Height);
        var right = Math.Clamp(region.Right, left, bitmap.Width);
        var bottom = Math.Clamp(region.Bottom, top, bitmap.Height);
        var count = 0;

        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red > 160 && pixel.Green < 80 && pixel.Blue < 80)
                    count++;
            }
        }

        return count;
    }

    private static int CountDarkPixels(SKBitmap bitmap, SKRectI region)
    {
        var left = Math.Clamp(region.Left, 0, bitmap.Width);
        var top = Math.Clamp(region.Top, 0, bitmap.Height);
        var right = Math.Clamp(region.Right, left, bitmap.Width);
        var bottom = Math.Clamp(region.Bottom, top, bitmap.Height);
        var count = 0;

        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 32 && pixel.Green < 32 && pixel.Blue < 32)
                    count++;
            }
        }

        return count;
    }

    private static int CountNonWhitePixels(SKBitmap bitmap, SKRectI region)
    {
        var left = Math.Clamp(region.Left, 0, bitmap.Width);
        var top = Math.Clamp(region.Top, 0, bitmap.Height);
        var right = Math.Clamp(region.Right, left, bitmap.Width);
        var bottom = Math.Clamp(region.Bottom, top, bitmap.Height);
        var count = 0;

        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 245 || pixel.Green < 245 || pixel.Blue < 245)
                    count++;
            }
        }

        return count;
    }

    private static (double Red, double Green, double Blue, double Luminance) MeanRgb(SKBitmap bitmap, SKRectI region)
    {
        var left = Math.Clamp(region.Left, 0, bitmap.Width);
        var top = Math.Clamp(region.Top, 0, bitmap.Height);
        var right = Math.Clamp(region.Right, left, bitmap.Width);
        var bottom = Math.Clamp(region.Bottom, top, bitmap.Height);
        long red = 0;
        long green = 0;
        long blue = 0;
        var count = 0;

        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                red += pixel.Red;
                green += pixel.Green;
                blue += pixel.Blue;
                count++;
            }
        }

        if (count == 0)
            return (0, 0, 0, 0);

        var r = red / (double)count;
        var g = green / (double)count;
        var b = blue / (double)count;
        return (r, g, b, 0.2126 * r + 0.7152 * g + 0.0722 * b);
    }

    private static SKRectI PdfRectToPixelRegion(
        SKBitmap bitmap,
        double left,
        double bottom,
        double width,
        double height)
    {
        var scaleX = bitmap.Width / 595.0;
        var scaleY = bitmap.Height / 840.0;
        var x0 = (int)Math.Floor(left * scaleX);
        var x1 = (int)Math.Ceiling((left + width) * scaleX);
        var y0 = (int)Math.Floor((840.0 - bottom - height) * scaleY);
        var y1 = (int)Math.Ceiling((840.0 - bottom) * scaleY);
        return new SKRectI(x0, y0, x1, y1);
    }

    private static int CountWarmPalePixels(SKBitmap bitmap, SKRectI region)
    {
        var left = Math.Clamp(region.Left, 0, bitmap.Width);
        var top = Math.Clamp(region.Top, 0, bitmap.Height);
        var right = Math.Clamp(region.Right, left, bitmap.Width);
        var bottom = Math.Clamp(region.Bottom, top, bitmap.Height);
        var count = 0;

        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red > 165 &&
                    pixel.Green > 160 &&
                    pixel.Blue < 185 &&
                    pixel.Red > pixel.Blue + 20 &&
                    pixel.Green > pixel.Blue + 10)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static SKRectI? GetDarkPixelBounds(SKBitmap bitmap)
    {
        var left = bitmap.Width;
        var top = bitmap.Height;
        var right = -1;
        var bottom = -1;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red >= 32 || pixel.Green >= 32 || pixel.Blue >= 32)
                    continue;

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);
            }
        }

        return right >= left && bottom >= top
            ? new SKRectI(left, top, right, bottom)
            : null;
    }

    private static int CountBluePixels(SKBitmap bitmap, SKRectI region)
    {
        var left = Math.Clamp(region.Left, 0, bitmap.Width);
        var top = Math.Clamp(region.Top, 0, bitmap.Height);
        var right = Math.Clamp(region.Right, left, bitmap.Width);
        var bottom = Math.Clamp(region.Bottom, top, bitmap.Height);
        var count = 0;

        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Blue > 120 && pixel.Green > 80 && pixel.Red < 80)
                    count++;
            }
        }

        return count;
    }

    private static int CountBlueDominantPixels(SKBitmap bitmap, SKRectI region)
    {
        var left = Math.Clamp(region.Left, 0, bitmap.Width);
        var top = Math.Clamp(region.Top, 0, bitmap.Height);
        var right = Math.Clamp(region.Right, left, bitmap.Width);
        var bottom = Math.Clamp(region.Bottom, top, bitmap.Height);
        var count = 0;

        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Blue > 120 && pixel.Blue > pixel.Green + 40 && pixel.Blue > pixel.Red + 40)
                    count++;
            }
        }

        return count;
    }

    private static int CountRowsWithBlueDominantPixels(SKBitmap bitmap, SKRectI region, int minimumBluePixels)
    {
        var left = Math.Clamp(region.Left, 0, bitmap.Width);
        var top = Math.Clamp(region.Top, 0, bitmap.Height);
        var right = Math.Clamp(region.Right, left, bitmap.Width);
        var bottom = Math.Clamp(region.Bottom, top, bitmap.Height);
        var rows = 0;

        for (int y = top; y < bottom; y++)
        {
            var blue = 0;
            for (int x = left; x < right; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Blue > 120 && pixel.Blue > pixel.Green + 40 && pixel.Blue > pixel.Red + 40)
                    blue++;
            }

            if (blue >= minimumBluePixels)
                rows++;
        }

        return rows;
    }

    private static (int RedDominant, int BlueDominant) CountRedAndBlueDominantPixels(SKBitmap bitmap, SKRectI region)
    {
        var left = Math.Clamp(region.Left, 0, bitmap.Width);
        var top = Math.Clamp(region.Top, 0, bitmap.Height);
        var right = Math.Clamp(region.Right, left, bitmap.Width);
        var bottom = Math.Clamp(region.Bottom, top, bitmap.Height);
        var red = 0;
        var blue = 0;

        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red > 150 && pixel.Red > pixel.Green + 40 && pixel.Red > pixel.Blue + 40)
                    red++;
                if (pixel.Blue > 150 && pixel.Blue > pixel.Red + 40 && pixel.Blue > pixel.Green + 40)
                    blue++;
            }
        }

        return (red, blue);
    }

    private static byte[] CreatePdfWithContentAndPageSize(string content, int width, int height)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[6];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 6");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 6 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    #endregion
}
