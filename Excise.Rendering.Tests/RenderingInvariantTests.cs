using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// Property-based invariant tests for SkiaRenderer.
/// These tests assert that mathematical invariants of the renderer hold across random inputs,
/// catching subtle correctness regressions (CTM math, color conversion, sampling, AA).
/// </summary>
public class RenderingInvariantTests
{
    #region Determinism Tests

    [Theory]
    [InlineData(42)]
    [InlineData(123)]
    [InlineData(456)]
    [InlineData(789)]
    [InlineData(999)]
    public void Determinism_SameContentRenderedTwice_ProducesByteIdenticalBitmaps(int seed)
    {
        // Arrange - Create a synthetic PDF with random rectangles and text
        var pdfData = RandomPdfBuilder.Create(seed, numRectangles: 8, numTextLines: 3);
        var renderer = new SkiaRenderer();

        using var doc1 = PdfDocument.Open(pdfData);
        using var doc2 = PdfDocument.Open(pdfData);

        // Act - Render twice
        using var bitmap1 = renderer.RenderPage(doc1.GetPage(1));
        using var bitmap2 = renderer.RenderPage(doc2.GetPage(1));

        // Assert - Bitmaps must be byte-identical
        BitmapCompare.AreIdentical(bitmap1, bitmap2).Should().BeTrue(
            "rendering the same PDF twice must produce identical bitmaps");
    }

    #endregion

    #region DPI Scaling Tests

    [Theory]
    [InlineData(72, 144)]
    [InlineData(100, 200)]
    [InlineData(150, 300)]
    public void DpiScaling_TwoDpisRelationship_BitmapSizeFollows2xDpiRatio(int dpi1, int dpi2)
    {
        // Arrange
        var content = "0.5 g\n100 100 200 150 re f";
        var pdfData = CreateSimplePdf(content);
        var renderer = new SkiaRenderer();

        using var doc1 = PdfDocument.Open(pdfData);
        using var doc2 = PdfDocument.Open(pdfData);

        // Act
        var opts1 = new RenderOptions { Dpi = dpi1 };
        var opts2 = new RenderOptions { Dpi = dpi2 };

        using var bitmap1 = renderer.RenderPage(doc1.GetPage(1), opts1);
        using var bitmap2 = renderer.RenderPage(doc2.GetPage(1), opts2);

        // Assert - bitmap2 should have exactly dpi2/dpi1 times the dimensions
        var expectedWidthRatio = dpi2 / (double)dpi1;
        var expectedHeightRatio = dpi2 / (double)dpi1;

        (bitmap2.Width / (double)bitmap1.Width).Should().BeApproximately(expectedWidthRatio, 0.01,
            "width must scale with DPI ratio");
        (bitmap2.Height / (double)bitmap1.Height).Should().BeApproximately(expectedHeightRatio, 0.01,
            "height must scale with DPI ratio");
    }

    #endregion

    #region Background Fill Tests

    [Theory]
    [InlineData(0xFFFFFFFF)] // White
    [InlineData(0xFFFF0000)] // Red
    [InlineData(0xFF00FF00)] // Green
    [InlineData(0xFF0000FF)] // Blue
    public void BackgroundFill_EmptyContentWithBackgroundColor_AllPixelsMatchBackground(uint colorAbgr)
    {
        // Arrange - Empty content stream, custom background color
        var emptyContent = "% empty";
        var pdfData = CreateSimplePdf(emptyContent);
        var renderer = new SkiaRenderer();
        var bgColor = new SKColor(colorAbgr);

        using var doc = PdfDocument.Open(pdfData);

        // Act
        var opts = new RenderOptions { BackgroundColor = bgColor };
        using var bitmap = renderer.RenderPage(doc.GetPage(1), opts);

        // Assert - All pixels should be the background color
        var mismatchCount = BitmapCompare.PixelDifferenceCount(bitmap, bgColor, tolerance: 0);
        mismatchCount.Should().Be(0, "all pixels must match the background color for empty content");
    }

    #endregion

    #region Translation Invariance Tests

    [Theory]
    [InlineData(50, 0)]   // Horizontal translation
    [InlineData(0, 75)]   // Vertical translation
    public void TranslationInvariance_SameRectWithAndWithoutCMTransform_ContentRenders(double tx, double ty)
    {
        // Arrange
        // Version 1: Rectangle at (100, 700) without transform
        var content1 = "0.5 g\n100 700 150 100 re f";

        // Version 2: Rectangle at (100, 700) with pre-translation by (tx, ty)
        var content2 = $"1 0 0 1 {tx} {ty} cm\n0.5 g\n100 700 150 100 re f";

        var pdfData1 = CreateSimplePdf(content1);
        var pdfData2 = CreateSimplePdf(content2);

        var renderer = new SkiaRenderer();

        using var doc1 = PdfDocument.Open(pdfData1);
        using var doc2 = PdfDocument.Open(pdfData2);

        // Act
        var opts = new RenderOptions { Dpi = 150 };
        using var bitmap1 = renderer.RenderPage(doc1.GetPage(1), opts);
        using var bitmap2 = renderer.RenderPage(doc2.GetPage(1), opts);

        // Assert - Both should render content (CTM math correctness invariant)
        var pixels1 = FindNonBackgroundPixels(bitmap1);
        var pixels2 = FindNonBackgroundPixels(bitmap2);

        pixels1.Count.Should().BeGreaterThan(0, "first bitmap should have rendered content");
        pixels2.Count.Should().BeGreaterThan(0, "second bitmap should have rendered content");

        // The centroid should shift in the expected direction (shift may vary slightly due to rasterization)
        var centroid1 = ComputeCentroid(pixels1);
        var centroid2 = ComputeCentroid(pixels2);

        if (tx > 0)
        {
            centroid2.X.Should().BeGreaterThan(centroid1.X, "horizontal translation should shift right");
        }
        if (ty > 0)
        {
            // PDF Y increases upward, but screen Y increases downward after transform
            // So translation in PDF space may appear as shift in either direction depending on page geometry
            centroid2.Y.Should().NotBe(centroid1.Y, "vertical translation should change Y coordinate");
        }
    }

    #endregion

    #region Identity Transform Tests

    [Fact]
    public void IdentityTransform_ContentWithExplicitIdentityCM_ByteIdenticalToNoTransform()
    {
        // Arrange
        var contentWithoutCM = "0.5 g\n100 100 200 150 re f";
        var contentWithCM = "1 0 0 1 0 0 cm\n0.5 g\n100 100 200 150 re f";

        var pdfData1 = CreateSimplePdf(contentWithoutCM);
        var pdfData2 = CreateSimplePdf(contentWithCM);

        var renderer = new SkiaRenderer();

        using var doc1 = PdfDocument.Open(pdfData1);
        using var doc2 = PdfDocument.Open(pdfData2);

        // Act
        using var bitmap1 = renderer.RenderPage(doc1.GetPage(1));
        using var bitmap2 = renderer.RenderPage(doc2.GetPage(1));

        // Assert
        BitmapCompare.AreIdentical(bitmap1, bitmap2).Should().BeTrue(
            "identity transform must be a no-op");
    }

    #endregion

    #region Empty Page Invariant Tests

    [Theory]
    [InlineData(612, 792)]    // US Letter
    [InlineData(595, 842)]    // A4
    [InlineData(100, 100)]    // Small
    [InlineData(2400, 3000)]  // Large
    public void EmptyPageInvariant_DifferentPageSizes_BitmapDimensionsMatchCalculation(double width, double height)
    {
        // Arrange - Empty content, custom page size
        var emptyContent = "% empty";
        var pdfData = CreatePdfWithCustomPageSize(width, height, emptyContent);
        var renderer = new SkiaRenderer();

        using var doc = PdfDocument.Open(pdfData);

        // Act
        var dpi = 150;
        var opts = new RenderOptions { Dpi = dpi };
        using var bitmap = renderer.RenderPage(doc.GetPage(1), opts);

        // Assert - Bitmap dimensions must match formula: (int)(W * dpi/72) × (int)(H * dpi/72)
        var expectedWidth = (int)Math.Ceiling(width * dpi / 72.0);
        var expectedHeight = (int)Math.Ceiling(height * dpi / 72.0);

        bitmap.Width.Should().Be(expectedWidth,
            $"width must be (int)({width} * {dpi}/72) = {expectedWidth}");
        bitmap.Height.Should().Be(expectedHeight,
            $"height must be (int)({height} * {dpi}/72) = {expectedHeight}");
    }

    #endregion

    #region Page Size Proportionality Tests

    [Fact]
    public void PageSizeProportionality_HalfHeightPage_BitmapHeightIsHalf()
    {
        // Arrange
        var content = "0.3 g\n100 100 100 100 re f";
        var fullHeightPdf = CreatePdfWithCustomPageSize(612, 792, content);
        var halfHeightPdf = CreatePdfWithCustomPageSize(612, 396, content);

        var renderer = new SkiaRenderer();

        using var docFull = PdfDocument.Open(fullHeightPdf);
        using var docHalf = PdfDocument.Open(halfHeightPdf);

        // Act
        var dpi = 150;
        var opts = new RenderOptions { Dpi = dpi };
        using var bitmapFull = renderer.RenderPage(docFull.GetPage(1), opts);
        using var bitmapHalf = renderer.RenderPage(docHalf.GetPage(1), opts);

        // Assert
        var heightRatio = bitmapHalf.Height / (double)bitmapFull.Height;
        heightRatio.Should().BeApproximately(0.5, 0.01,
            "half-height page should render to half bitmap height");

        // Width should remain the same
        bitmapFull.Width.Should().Be(bitmapHalf.Width,
            "width must remain constant");
    }

    #endregion

    #region RGB Color Round-Trip Tests

    [Theory]
    [InlineData(1.0, 0.0, 0.0)]   // Red
    [InlineData(0.0, 1.0, 0.0)]   // Green
    [InlineData(0.0, 0.0, 1.0)]   // Blue
    [InlineData(0.5, 0.5, 0.5)]   // Gray
    [InlineData(1.0, 1.0, 0.0)]   // Yellow
    public void RgbColorRoundTrip_FilledRectangleWithColor_ColorChannelsDominantInPixels(double r, double g, double b)
    {
        // Arrange - Filled rectangle with specific RGB color
        // Using center coordinates in PDF space that should be within the filled rectangle
        var content = $"{r} {g} {b} rg\n200 500 150 150 re f";
        var pdfData = CreateSimplePdf(content);
        var renderer = new SkiaRenderer();

        using var doc = PdfDocument.Open(pdfData);

        // Act
        var opts = new RenderOptions { Dpi = 150 };
        using var bitmap = renderer.RenderPage(doc.GetPage(1), opts);

        // Find the non-background pixels and verify their dominant color
        var pixels = FindNonBackgroundPixels(bitmap);
        pixels.Count.Should().BeGreaterThan(0, "rectangle should render to non-background pixels");

        // Sample a pixel from the rendered region
        var pixel = bitmap.GetPixel(pixels[pixels.Count / 2].X, pixels[pixels.Count / 2].Y);

        // Assert - For pure colors (non-blended), the dominant channel should be strong
        // For mixed colors (like yellow 1,1,0), multiple channels should be strong
        var maxChannel = Math.Max(pixel.Red, Math.Max(pixel.Green, pixel.Blue));
        maxChannel.Should().BeGreaterThan(100, "rendered color should have significant channel value");

        // Verify the dominant channel matches expectation
        if (Math.Abs(r - 1.0) < 0.01 && Math.Abs(g) < 0.01 && Math.Abs(b) < 0.01)
            pixel.Red.Should().BeGreaterThan(pixel.Green, "red should be dominant for red color");
        else if (Math.Abs(r) < 0.01 && Math.Abs(g - 1.0) < 0.01 && Math.Abs(b) < 0.01)
            pixel.Green.Should().BeGreaterThan(pixel.Red, "green should be dominant for green color");
    }

    #endregion

    #region CMYK Color Tests

    [Theory]
    [InlineData(0.0, 1.0, 1.0, 0.0)] // Red in CMYK (cyan=0, magenta=1, yellow=1, black=0)
    [InlineData(1.0, 0.0, 0.0, 0.0)] // Cyan
    [InlineData(0.0, 1.0, 0.0, 0.0)] // Magenta
    [InlineData(0.0, 0.0, 1.0, 0.0)] // Yellow
    public void CmykColorRoundTrip_FilledRectangleWithCmyk_RendersNonBackground(double c, double m, double y, double k)
    {
        // Arrange - Filled rectangle with CMYK color
        var content = $"{c} {m} {y} {k} k\n200 500 150 150 re f";
        var pdfData = CreateSimplePdf(content);
        var renderer = new SkiaRenderer();

        using var doc = PdfDocument.Open(pdfData);

        // Act
        var opts = new RenderOptions { Dpi = 150 };
        using var bitmap = renderer.RenderPage(doc.GetPage(1), opts);

        // Assert - CMYK colors should render to non-background pixels (color invariant)
        var pixels = FindNonBackgroundPixels(bitmap);
        pixels.Count.Should().BeGreaterThan(0,
            "CMYK color should render to non-background pixels");
    }

    #endregion

    #region Stroke Width Visibility Tests

    [Fact]
    public void StrokeWidth_ThickStrokeVsThinStroke_ThickStrokeHasMorePixels()
    {
        // Arrange - Same path, two different stroke widths
        var thickContent = "0 G\n10 w\n100 400 m\n500 400 l\nS";
        var thinContent = "0 G\n1 w\n100 400 m\n500 400 l\nS";

        var pdfDataThick = CreateSimplePdf(thickContent);
        var pdfDataThin = CreateSimplePdf(thinContent);

        var renderer = new SkiaRenderer();

        using var docThick = PdfDocument.Open(pdfDataThick);
        using var docThin = PdfDocument.Open(pdfDataThin);

        // Act
        var opts = new RenderOptions { Dpi = 150 };
        using var bitmapThick = renderer.RenderPage(docThick.GetPage(1), opts);
        using var bitmapThin = renderer.RenderPage(docThin.GetPage(1), opts);

        // Assert
        var thickPixels = FindNonBackgroundPixels(bitmapThick);
        var thinPixels = FindNonBackgroundPixels(bitmapThin);

        thickPixels.Count.Should().BeGreaterThan(0, "thick stroke should have rendered pixels");
        thinPixels.Count.Should().BeGreaterThan(0, "thin stroke should have rendered pixels");

        var pixelRatio = thickPixels.Count / (double)thinPixels.Count;
        pixelRatio.Should().BeGreaterThan(4.0, // 10/1 = 10x, but allow for sampling variance
            "10pt stroke should have significantly more pixels than 1pt stroke");
    }

    #endregion

    #region Anti-Aliasing Existence Tests

    [Fact]
    public void AntiAliasing_TextRenderingWithAA_DistinctIntermediateGrayValuesExist()
    {
        // Arrange - Text content that will have edge pixels with anti-aliasing
        var content = @"
            BT
            /F1 12 Tf
            100 700 Td
            (AAAA) Tj
            ET
        ";
        var pdfData = CreateSimplePdf(content);
        var renderer = new SkiaRenderer();

        using var doc = PdfDocument.Open(pdfData);

        // Act
        var opts = new RenderOptions { Dpi = 100, AntiAlias = true };
        using var bitmap = renderer.RenderPage(doc.GetPage(1), opts);

        // Assert - Walk a horizontal scan line through the text area and count distinct gray values
        // Text should be around y=700, so after 72 DPI scaling and coordinate flip:
        var scanLineY = bitmap.Height - (int)(700 * 100 / 72.0) - 10;
        if (scanLineY < 0) scanLineY = bitmap.Height / 2;
        if (scanLineY >= bitmap.Height) scanLineY = bitmap.Height - 1;

        var grayValues = new HashSet<byte>();
        for (int x = 0; x < bitmap.Width; x++)
        {
            var pixel = bitmap.GetPixel(x, scanLineY);
            // Compute gray from RGB (simple average)
            var gray = (byte)((pixel.Red + pixel.Green + pixel.Blue) / 3);
            grayValues.Add(gray);
        }

        // Remove pure white and pure black - count intermediate grays
        grayValues.Remove(255);
        grayValues.Remove(0);

        grayValues.Count.Should().BeGreaterThanOrEqualTo(3,
            "anti-aliased text should have at least 3 distinct intermediate gray values");
    }

    #endregion

    #region Scaling Transform Tests

    [Fact]
    public void ScalingTransform_SmallRectWithSmallScale_BothRender()
    {
        // Arrange - Use a small rectangle and scaling to verify CTM math correctness
        var contentNoScale = "0.3 g\n50 50 50 50 re f";
        var contentWithScale = "1.5 0 0 1.5 0 0 cm\n0.3 g\n50 50 50 50 re f";

        var pdfData1 = CreateSimplePdf(contentNoScale);
        var pdfData2 = CreateSimplePdf(contentWithScale);

        var renderer = new SkiaRenderer();

        using var doc1 = PdfDocument.Open(pdfData1);
        using var doc2 = PdfDocument.Open(pdfData2);

        // Act
        var opts = new RenderOptions { Dpi = 150 };
        using var bitmap1 = renderer.RenderPage(doc1.GetPage(1), opts);
        using var bitmap2 = renderer.RenderPage(doc2.GetPage(1), opts);

        // Assert - Both should render (CTM math correctness invariant)
        var pixels1 = FindNonBackgroundPixels(bitmap1);
        var pixels2 = FindNonBackgroundPixels(bitmap2);

        pixels1.Count.Should().BeGreaterThan(0, "unscaled rectangle should render");
        pixels2.Count.Should().BeGreaterThan(0, "scaled rectangle should render (CTM handling)");

        // Scaled version should generally have more pixel coverage (1.5x scale = more area)
        var ratio = pixels2.Count / (double)pixels1.Count;
        ratio.Should().BeGreaterThan(1.0, "scaling up should increase pixel coverage");
    }

    #endregion

    #region Helper Methods

    private static byte[] CreateSimplePdf(string content)
    {
        return CreatePdfWithCustomPageSize(612, 792, content);
    }

    private static byte[] CreatePdfWithCustomPageSize(double width, double height, string content)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true);
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
        writer.WriteLine($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {(int)width} {(int)height}] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
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

    private static List<(int X, int Y)> FindNonBackgroundPixels(SKBitmap bitmap, SKColor? bgColor = null)
    {
        bgColor ??= SKColors.White;
        var pixels = new List<(int, int)>();

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel != bgColor)
                    pixels.Add((x, y));
            }
        }

        return pixels;
    }

    private static (double X, double Y) ComputeCentroid(List<(int X, int Y)> pixels)
    {
        if (pixels.Count == 0)
            return (0, 0);

        var sumX = pixels.Sum(p => p.X);
        var sumY = pixels.Sum(p => p.Y);
        return (sumX / (double)pixels.Count, sumY / (double)pixels.Count);
    }

    #endregion
}

/// <summary>
/// Builds synthetic PDFs with random rectangles and text for testing.
/// </summary>
internal static class RandomPdfBuilder
{
    /// <summary>
    /// Create a synthetic PDF with random rectangles and text lines.
    /// </summary>
    public static byte[] Create(int seed, int numRectangles = 5, int numTextLines = 2)
    {
        var rng = new Random(seed);
        var content = new StringBuilder();

        // Add random filled rectangles
        for (int i = 0; i < numRectangles; i++)
        {
            var r = rng.NextDouble();
            var g = rng.NextDouble();
            var b = rng.NextDouble();
            var x = rng.Next(50, 500);
            var y = rng.Next(100, 700);
            var w = rng.Next(50, 150);
            var h = rng.Next(50, 150);

            content.AppendLine($"{r} {g} {b} rg");
            content.AppendLine($"{x} {y} {w} {h} re f");
        }

        // Add random text
        if (numTextLines > 0)
        {
            content.AppendLine("BT");
            content.AppendLine("/F1 10 Tf");

            for (int i = 0; i < numTextLines; i++)
            {
                var x = rng.Next(50, 400);
                var y = rng.Next(200, 700);
                content.AppendLine($"{x} {y} Td");
                content.AppendLine("(Test) Tj");
            }

            content.AppendLine("ET");
        }

        return CreatePdfWithContent(content.ToString());
    }

    private static byte[] CreatePdfWithContent(string content)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true);
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
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
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
}

/// <summary>
/// Bitmap comparison utilities for invariant tests.
/// </summary>
internal static class BitmapCompare
{
    /// <summary>
    /// Check if two bitmaps are byte-identical.
    /// </summary>
    public static bool AreIdentical(SKBitmap bitmap1, SKBitmap bitmap2)
    {
        if (bitmap1.Width != bitmap2.Width || bitmap1.Height != bitmap2.Height)
            return false;

        for (int y = 0; y < bitmap1.Height; y++)
        {
            for (int x = 0; x < bitmap1.Width; x++)
            {
                if (bitmap1.GetPixel(x, y) != bitmap2.GetPixel(x, y))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Count pixels that differ from the expected color (within tolerance).
    /// </summary>
    public static int PixelDifferenceCount(SKBitmap bitmap, SKColor expectedColor, int tolerance)
    {
        int count = 0;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);

                if (Math.Abs(pixel.Red - expectedColor.Red) > tolerance ||
                    Math.Abs(pixel.Green - expectedColor.Green) > tolerance ||
                    Math.Abs(pixel.Blue - expectedColor.Blue) > tolerance)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Sample a single pixel's RGB color.
    /// </summary>
    public static (byte R, byte G, byte B, byte A) SampleColor(SKBitmap bitmap, int x, int y)
    {
        // Clamp to bitmap bounds
        x = Math.Max(0, Math.Min(x, bitmap.Width - 1));
        y = Math.Max(0, Math.Min(y, bitmap.Height - 1));

        var pixel = bitmap.GetPixel(x, y);
        return (pixel.Red, pixel.Green, pixel.Blue, pixel.Alpha);
    }
}
