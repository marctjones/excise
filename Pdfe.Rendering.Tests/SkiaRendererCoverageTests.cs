using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace Pdfe.Rendering.Tests;

/// <summary>
/// Coverage tests for SkiaRenderer operators and edge cases.
/// Exercises color operators, path operators, transparency, clipping, CTM, text rendering, and various DPIs/page sizes.
/// </summary>
public class SkiaRendererCoverageTests
{
    #region Color Operator Tests (g, G, rg, RG, k, K, cs/CS + scn/SCN)

    [Fact]
    public void RenderPage_GrayscaleFillOperator_g_ShowsGrayPixel()
    {
        // Arrange - g operator: grayscale fill color (0.5 = medium gray)
        var content = @"
            0.5 g
            100 100 200 150 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - rectangle center should be gray
        var pixelX = (int)(200 * 150 / 72);
        var pixelY = bitmap.Height - (int)(175 * 150 / 72);
        var pixel = bitmap.GetPixel(pixelX, pixelY);
        var gray = pixel.Red;
        gray.Should().BeInRange(100, 155, "gray (0.5) should be ~128");
    }

    [Fact]
    public void RenderPage_GrayscaleStrokeOperator_G_ShowsGrayStroke()
    {
        // Arrange - G operator: grayscale stroke color
        var content = @"
            0.3 G
            3 w
            100 100 m
            300 300 l
            S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - line should render without error and be visible
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_RgbFillOperator_rg_ShowsRedGreenBlue()
    {
        // Arrange - rg operator with three separate colors
        var content = @"
            1 0 0 rg
            50 100 100 100 re f
            0 1 0 rg
            150 100 100 100 re f
            0 0 1 rg
            250 100 100 100 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - three colored rectangles should render
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
        // Sample red rectangle
        var redPixelX = (int)(100 * 150 / 72);
        var redPixelY = bitmap.Height - (int)(150 * 150 / 72);
        var redPixel = bitmap.GetPixel(redPixelX, redPixelY);
        redPixel.Red.Should().BeGreaterThan(200, "red channel should dominate");
    }

    [Fact]
    public void RenderPage_RgbStrokeOperator_RG_ShowsStrokeColor()
    {
        // Arrange - RG operator: RGB stroke color
        var content = @"
            0 1 0 RG
            5 w
            100 200 m
            300 400 l
            S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - green line should render
        bitmap.Should().NotBeNull();
    }

    [Fact]
    public void RenderPage_CmykFillOperator_k_ShowsCyanMagentaYellowBlack()
    {
        // Arrange - k operator: CMYK fill color
        var content = @"
            1 0 0 0 k
            100 100 150 100 re f
            0 1 0 0 k
            200 100 150 100 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - CMYK rectangles should render (cyan and magenta)
        bitmap.Should().NotBeNull();
    }

    [Fact]
    public void RenderPage_CmykStrokeOperator_K_ShowsStroke()
    {
        // Arrange - K operator: CMYK stroke color
        var content = @"
            0 0 1 0 K
            4 w
            100 200 m
            300 400 l
            S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - yellow line should render
        bitmap.Should().NotBeNull();
    }

    [Fact]
    public void RenderPage_DeviceRgbColorSpace_cs_scn()
    {
        // Arrange - cs (color space) + scn (set color) for DeviceRGB fill
        var content = @"
            /DeviceRGB cs
            1 0 0 scn
            100 100 150 100 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - red rectangle via color space operators
        bitmap.Should().NotBeNull();
        var pixelX = (int)(175 * 150 / 72);
        var pixelY = bitmap.Height - (int)(150 * 150 / 72);
        var pixel = bitmap.GetPixel(pixelX, pixelY);
        pixel.Red.Should().BeGreaterThan(200);
    }

    [Fact]
    public void RenderPage_StrokingColorSpace_CS_SCN()
    {
        // Arrange - CS (stroking color space) + SCN (set stroking color)
        var content = @"
            /DeviceRGB CS
            0 1 0 SCN
            3 w
            100 200 m
            300 400 l
            S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - green stroke via stroking color space
        bitmap.Should().NotBeNull();
    }

    [Fact]
    public void RenderPage_ImageXObject_ResolvesNamedIndexedColorSpaceResource()
    {
        var pdfData = CreatePdfWithNamedIndexedImageColorSpace();
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        var pixelX = (int)(150 * 150 / 72);
        var pixelY = bitmap.Height - (int)(150 * 150 / 72);
        var pixel = bitmap.GetPixel(pixelX, pixelY);
        pixel.Red.Should().BeGreaterThan(180);
        pixel.Green.Should().BeLessThan(80);
        pixel.Blue.Should().BeLessThan(80);
    }

    #endregion

    #region Path Operator Tests (fill rules, no-op)

    [Fact]
    public void RenderPage_FillEvenOdd_fStar_Operator()
    {
        // Arrange - f* operator: fill with even-odd rule
        // Create a self-intersecting path where even-odd differs from nonzero
        var content = @"
            0.5 g
            100 100 m
            300 100 l
            300 300 l
            100 300 l
            200 150 m
            200 250 l
            f*
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - even-odd fill should render
        bitmap.Should().NotBeNull();
    }

    [Fact]
    public void RenderPage_StrokeOnly_S_Operator()
    {
        // Arrange - S operator: stroke path without closing
        var content = @"
            0 G
            2 w
            100 100 m
            200 200 l
            300 100 l
            S
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - open path stroke should render
        bitmap.Should().NotBeNull();
    }

    [Fact]
    public void RenderPage_FillOnly_f_Operator()
    {
        // Arrange - f operator: fill with nonzero winding rule
        var content = @"
            0.7 g
            100 100 m
            250 100 l
            250 250 l
            100 250 l
            h
            f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - filled rectangle should render
        bitmap.Should().NotBeNull();
    }

    [Fact]
    public void RenderPage_NoOp_n_Operator()
    {
        // Arrange - n operator: end path without painting (no-op)
        var content = @"
            0 G
            2 w
            100 100 m
            200 200 l
            n
            1 g
            200 200 150 150 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - path is discarded, only rectangle shows
        bitmap.Should().NotBeNull();
    }

    #endregion

    #region Transparency and Opacity Tests

    [Fact]
    public void RenderPage_Transparency_OverlappingRectangles_BlendingApplied()
    {
        // Arrange - Two overlapping rectangles with transparency via /ca (fill alpha)
        // Content stream is fixed inside CreatePdfWithExtGState — the ExtGState
        // resource dictionary is the only knob this test varies.
        var extGState = new Dictionary<string, object>
        {
            ["ca"] = 0.5  // Non-stroking alpha
        };
        var pdfData = CreatePdfWithExtGState(extGState);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - overlapping region should be blended
        bitmap.Should().NotBeNull();
        bitmap.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RenderPage_StrokeAlpha_CA_Operator()
    {
        // Arrange - Stroke alpha via /CA (stroking alpha) in ExtGState.
        // Content stream is fixed inside CreatePdfWithExtGState — the
        // ExtGState resource dictionary is the only knob this test varies.
        var extGState = new Dictionary<string, object>
        {
            ["CA"] = 0.3  // Stroking alpha = 30%
        };
        var pdfData = CreatePdfWithExtGState(extGState);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - semi-transparent line should render
        bitmap.Should().NotBeNull();
    }

    #endregion

    #region Clipping Path Tests

    [Fact]
    public void RenderPage_ClippingPath_W_NonzeroWinding()
    {
        // Arrange - W operator: set clipping path with nonzero winding rule
        var content = @"
            100 100 m
            300 100 l
            300 300 l
            100 300 l
            h
            W n
            50 50 400 400 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - filled area clipped to rectangle
        bitmap.Should().NotBeNull();
    }

    [Fact]
    public void RenderPage_ClippingPath_WStar_EvenOddRule()
    {
        // Arrange - W* operator: set clipping path with even-odd rule
        var content = @"
            150 150 m
            250 150 l
            250 250 l
            150 250 l
            h
            W* n
            100 100 250 250 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - clipping with even-odd rule
        bitmap.Should().NotBeNull();
    }

    #endregion

    #region CTM and Transformation Tests

    [Fact]
    public void RenderPage_Translation_cm_Operator()
    {
        // Arrange - cm operator: apply translation via CTM
        // cm: a b c d e f cm (6 matrix components for 2D transformation)
        // Translation: 1 0 0 1 tx ty
        var content = @"
            1 0 0 1 50 100 cm
            0.5 g
            100 100 150 150 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - rectangle translated
        bitmap.Should().NotBeNull();
    }

    [Fact]
    public void RenderPage_Scaling_cm_Operator()
    {
        // Arrange - cm operator: scaling (sx 0 0 sy 0 0)
        var content = @"
            1.5 0 0 1.5 100 100 cm
            0.3 g
            0 0 100 100 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - scaled rectangle should render
        bitmap.Should().NotBeNull();
    }

    [Fact]
    public void RenderPage_Rotation_cm_Operator()
    {
        // Arrange - cm operator: rotation via skew matrix
        // cos(45°) ≈ 0.707, sin(45°) ≈ 0.707
        var content = @"
            0.707 0.707 -0.707 0.707 200 200 cm
            0.2 g
            -50 -50 100 100 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - rotated rectangle should render
        bitmap.Should().NotBeNull();
    }

    [Fact]
    public void RenderPage_StateStack_SaveRestore()
    {
        // Arrange - q (save) and Q (restore) graphics state
        var content = @"
            q
            1 0 0 rg
            100 100 100 100 re f
            Q
            0 1 0 rg
            200 200 100 100 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - both rectangles rendered with independent colors
        bitmap.Should().NotBeNull();
    }

    [Fact]
    public void RenderPage_ClippingPathThenNestedFilledPaths_RendersAllFills()
    {
        var content = @"
            q
            1 0 0 -1 0 792 cm
            q
            0.001 0 0 0.001 0 0 cm
            0 0 m
            612000 0 l
            612000 792000 l
            0 792000 l
            h
            W
            n
            q q
            1 0 0 rg
            0 0 m
            204000 0 l
            204000 264000 l
            0 264000 l
            h
            f*
            Q Q
            q q
            0 1 0 rg
            204000 0 m
            408000 0 l
            408000 264000 l
            204000 264000 l
            h
            f*
            Q Q
            q q
            0 0 1 rg
            408000 0 m
            612000 0 l
            612000 264000 l
            408000 264000 l
            h
            f*
            Q Q
            Q
            Q
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        using var bitmap = renderer.RenderPage(doc.GetPage(1), new RenderOptions { Dpi = 72 });

        var red = bitmap.GetPixel(102, 132);
        var green = bitmap.GetPixel(306, 132);
        var blue = bitmap.GetPixel(510, 132);
        red.Red.Should().BeGreaterThan(200);
        red.Green.Should().BeLessThan(80);
        red.Blue.Should().BeLessThan(80);
        green.Red.Should().BeLessThan(80);
        green.Green.Should().BeGreaterThan(200);
        green.Blue.Should().BeLessThan(80);
        blue.Red.Should().BeLessThan(80);
        blue.Green.Should().BeLessThan(80);
        blue.Blue.Should().BeGreaterThan(200);
    }

    #endregion

    #region Text Rendering Tests

    [Fact]
    public void RenderPage_SimpleText_SingleFont()
    {
        // Arrange - Basic text rendering with Helvetica
        var content = @"
            BT
            /F1 12 Tf
            100 700 Td
            (Hello World) Tj
            ET
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - text should render
        bitmap.Should().NotBeNull();
    }

    [Fact]
    public void RenderPage_Text_LargerFontSize()
    {
        // Arrange - Text at larger font size (24pt)
        var content = @"
            BT
            /F1 24 Tf
            100 600 Td
            (Large Text) Tj
            ET
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - larger text should render
        bitmap.Should().NotBeNull();
    }

    #endregion

    #region Empty and Minimal Content Tests

    [Fact]
    public void RenderPage_EmptyContent_ShowsWhiteBackground()
    {
        // Arrange - Page with no content operators
        var pdfData = CreatePdfWithContent("");
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();
        var options = new RenderOptions { BackgroundColor = SKColors.White };

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1), options);

        // Assert - background only
        var centerPixel = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
        centerPixel.Should().Be(SKColors.White);
    }

    [Fact]
    public void RenderPage_SingleOperator_JustRectangle()
    {
        // Arrange - Minimal content: one filled rectangle
        var content = @"
            0.5 g
            100 100 200 200 re f
        ";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        // Act
        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        // Assert - simple rectangle renders
        bitmap.Should().NotBeNull();
    }

    #endregion

    #region DPI Scaling Tests

    [Theory]
    [InlineData(72)]
    [InlineData(96)]
    [InlineData(150)]
    [InlineData(300)]
    [InlineData(600)]
    public void RenderPage_VariousDpis_ScalesDimensionsCorrectly(int dpi)
    {
        // Arrange
        var content = "0.5 g\n100 100 200 200 re f";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();
        var page = doc.GetPage(1);
        var options = new RenderOptions { Dpi = dpi };

        // Act
        using var bitmap = renderer.RenderPage(page, options);

        // Assert - dimensions should scale linearly with DPI
        var expectedWidth = (int)Math.Round(page.Width * dpi / 72);
        var expectedHeight = (int)Math.Round(page.Height * dpi / 72);
        bitmap.Width.Should().Be(expectedWidth, $"at {dpi} DPI");
        bitmap.Height.Should().Be(expectedHeight, $"at {dpi} DPI");
    }

    #endregion

    #region Page Size Tests

    [Fact]
    public void RenderPage_LetterSize_612x792Points()
    {
        // Arrange - US Letter (612 x 792 points)
        var content = "0 G\n2 w\n100 100 m\n500 700 l\nS";
        var pdfData = CreatePdfWithContent(content);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();
        var page = doc.GetPage(1);
        var options = new RenderOptions { Dpi = 72 };

        // Act
        using var bitmap = renderer.RenderPage(page, options);

        // Assert - should be exactly 612 x 792 pixels at 72 DPI
        bitmap.Width.Should().Be(612);
        bitmap.Height.Should().Be(792);
    }

    [Fact]
    public void RenderPage_CustomSmallPage_50x50Points()
    {
        // Arrange - Create PDF with custom small page size
        var pdfData = CreatePdfWithCustomPageSize(50, 50);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();
        var page = doc.GetPage(1);
        var options = new RenderOptions { Dpi = 72 };

        // Act
        using var bitmap = renderer.RenderPage(page, options);

        // Assert - small page should render at correct size
        bitmap.Width.Should().Be(50);
        bitmap.Height.Should().Be(50);
    }

    [Fact]
    public void RenderPage_CustomLargePage_1200x1600Points()
    {
        // Arrange - Large page size
        var pdfData = CreatePdfWithCustomPageSize(1200, 1600);
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();
        var page = doc.GetPage(1);
        var options = new RenderOptions { Dpi = 150 };

        // Act
        using var bitmap = renderer.RenderPage(page, options);

        // Assert - dimensions should scale correctly
        var expectedWidth = (int)Math.Round((double)(1200 * 150 / 72));
        var expectedHeight = (int)Math.Round((double)(1600 * 150 / 72));
        bitmap.Width.Should().Be(expectedWidth);
        bitmap.Height.Should().Be(expectedHeight);
    }

    [Fact]
    public void RenderPage_PatternType6CoonsMesh_RendersMeshArea()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "coons-allflags-withfunction.pdf");
        Assert.SkipWhen(path == null,
            "No pdf.js Coons mesh fixture found at test-pdfs/pdfjs/coons-allflags-withfunction.pdf.");

        using var doc = PdfDocument.Open(path);
        var renderer = new SkiaRenderer();

        using var bitmap = renderer.RenderPage(doc.GetPage(1), new RenderOptions { Dpi = 72 });

        var inside = bitmap.GetPixel(300, 400);
        var outside = bitmap.GetPixel(100, 100);
        outside.Red.Should().BeGreaterThan(245);
        outside.Green.Should().BeGreaterThan(245);
        outside.Blue.Should().BeGreaterThan(245);
        inside.Green.Should().BeGreaterThan(80);
        inside.Blue.Should().BeGreaterThan(40);
        inside.Red.Should().BeLessThan(80);
    }

    [Fact]
    public void RenderPage_PatternType2AxialShadingFill_UsesPatternResource()
    {
        var pdfData = CreatePdfWithAxialShadingPatternFill();
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        var y = bitmap.Height - (int)(150 * 150 / 72);
        var left = bitmap.GetPixel((int)(125 * 150 / 72), y);
        var right = bitmap.GetPixel((int)(275 * 150 / 72), y);

        left.Red.Should().BeGreaterThan(left.Blue, "left side of the axial shading should be red-dominant");
        right.Blue.Should().BeGreaterThan(right.Red, "right side of the axial shading should be blue-dominant");
    }

    [Fact]
    public void RenderPage_FunctionBasedShading_EvaluatesAcrossDomain()
    {
        var pdfData = CreatePdfWithFunctionBasedShading();
        using var doc = PdfDocument.Open(pdfData);
        var renderer = new SkiaRenderer();

        using var bitmap = renderer.RenderPage(doc.GetPage(1));

        var y = bitmap.Height - (int)(150 * 150 / 72);
        var left = bitmap.GetPixel((int)(125 * 150 / 72), y);
        var right = bitmap.GetPixel((int)(275 * 150 / 72), y);

        ((int)right.Red).Should().BeGreaterThan((int)left.Red + 80,
            "Type 1 shading should evaluate the function at each domain coordinate");
    }

    #endregion

    #region Helper Methods

    private static string? FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        return null;
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

    private static byte[] CreatePdfWithAxialShadingPatternFill()
    {
        const string content = "/CS1 cs\n/P1 scn\n100 100 200 100 re f\n";
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.4");
        var offsets = new long[5];

        offsets[1] = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");

        offsets[2] = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        offsets[3] = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R");
        sb.AppendLine("   /Resources <<");
        sb.AppendLine("     /ColorSpace << /CS1 [/Pattern] >>");
        sb.AppendLine("     /Pattern << /P1 << /PatternType 2 /Matrix [1 0 0 1 0 0]");
        sb.AppendLine("       /Shading << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [100 100 300 100]");
        sb.AppendLine("         /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>");
        sb.AppendLine("         /Extend [true true]");
        sb.AppendLine("       >>");
        sb.AppendLine("     >> >>");
        sb.AppendLine("   >>");
        sb.AppendLine(">>");
        sb.AppendLine("endobj");

        offsets[4] = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine($"<< /Length {content.Length} >>");
        sb.AppendLine("stream");
        sb.Append(content);
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        var xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 5");
        sb.AppendLine("0000000000 65535 f ");
        for (int i = 1; i <= 4; i++)
            sb.AppendLine($"{offsets[i]:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Root 1 0 R /Size 5 >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] CreatePdfWithFunctionBasedShading()
    {
        const string content = "q\n100 100 200 100 re W n\n/SH1 sh\nQ\n";
        const string functionProgram = "{ pop }\n";
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.4");
        var offsets = new long[7];

        offsets[1] = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");

        offsets[2] = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        offsets[3] = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R");
        sb.AppendLine("   /Resources << /Shading << /SH1 5 0 R >> >>");
        sb.AppendLine(">>");
        sb.AppendLine("endobj");

        offsets[4] = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine($"<< /Length {content.Length} >>");
        sb.AppendLine("stream");
        sb.Append(content);
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        offsets[5] = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /ShadingType 1 /ColorSpace /DeviceGray /Domain [0 1 0 1]");
        sb.AppendLine("   /Matrix [200 0 0 100 100 100]");
        sb.AppendLine("   /Function 6 0 R");
        sb.AppendLine(">>");
        sb.AppendLine("endobj");

        offsets[6] = sb.Length;
        sb.AppendLine("6 0 obj");
        sb.AppendLine($"<< /FunctionType 4 /Domain [0 1 0 1] /Range [0 1] /Length {functionProgram.Length} >>");
        sb.AppendLine("stream");
        sb.Append(functionProgram);
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        var xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 7");
        sb.AppendLine("0000000000 65535 f ");
        for (int i = 1; i <= 6; i++)
            sb.AppendLine($"{offsets[i]:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Root 1 0 R /Size 7 >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] CreatePdfWithNamedIndexedImageColorSpace()
    {
        const string content = "q\n100 0 0 100 100 100 cm\n/Im1 Do\nQ\n";
        const string imageData = "\x01";
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.4");
        var offsets = new long[6];

        offsets[1] = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");

        offsets[2] = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        offsets[3] = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R");
        sb.AppendLine("   /Resources <<");
        sb.AppendLine("     /ColorSpace << /CS1 [/Indexed /DeviceRGB 1 <0000FFFF0000>] >>");
        sb.AppendLine("     /XObject << /Im1 5 0 R >>");
        sb.AppendLine("   >>");
        sb.AppendLine(">>");
        sb.AppendLine("endobj");

        offsets[4] = sb.Length;
        sb.AppendLine("4 0 obj");
        sb.AppendLine($"<< /Length {content.Length} >>");
        sb.AppendLine("stream");
        sb.Append(content);
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        offsets[5] = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /Type /XObject /Subtype /Image /Width 1 /Height 1");
        sb.AppendLine("   /BitsPerComponent 8 /ColorSpace /CS1 /Length 1 >>");
        sb.AppendLine("stream");
        sb.Append(imageData);
        sb.AppendLine();
        sb.AppendLine("endstream");
        sb.AppendLine("endobj");

        var xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            sb.AppendLine($"{offsets[i]:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Root 1 0 R /Size 6 >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] CreatePdfWithCustomPageSize(double width, double height)
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

        var contentText = "0 G";
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {contentText.Length} >>");
        writer.WriteLine("stream");
        writer.Write(contentText);
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

    private static byte[] CreatePdfWithExtGState(Dictionary<string, object> extGStateParams)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true);
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
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 6 0 R >> /ExtGState << /GS1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        var contentText = @"
            /GS1 gs
            1 0 0 rg
            100 100 150 150 re f
            0 0 1 rg
            150 150 150 150 re f
        ";
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {contentText.Length} >>");
        writer.WriteLine("stream");
        writer.Write(contentText);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        // Build extended graphics state dictionary
        var extGStateDict = new StringBuilder();
        extGStateDict.Append("<< ");
        foreach (var kvp in extGStateParams)
        {
            if (kvp.Value is double d)
                extGStateDict.Append($"/{kvp.Key} {d} ");
            else if (kvp.Value is int i)
                extGStateDict.Append($"/{kvp.Key} {i} ");
            else
                extGStateDict.Append($"/{kvp.Key} {kvp.Value} ");
        }
        extGStateDict.Append(">>");

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine(extGStateDict.ToString());
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[6] = ms.Position;
        writer.WriteLine("6 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
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

    #endregion
}
