using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using System;
using System.IO;
using System.Text;
using Xunit;

namespace Excise.Rendering.Tests.Visual;

/// <summary>
/// Visual regression tests for PDF rendering.
/// These tests verify that the renderer produces pixel-perfect output
/// by comparing against baseline images.
///
/// BASELINE GENERATION:
/// When adding new tests, run once to generate baseline, manually verify
/// the output looks correct, then commit the baseline image.
///
/// UPDATING BASELINES:
/// If rendering intentionally changes, regenerate baselines:
/// 1. Delete old baseline images
/// 2. Run tests (they will fail and generate new images)
/// 3. Manually verify new images look correct
/// 4. Rename from test-output/ to baselines/
/// 5. Commit new baselines
/// </summary>
public class RenderingVisualTests : IDisposable
{
    private readonly string _testOutputDir;
    private readonly string _baselinesDir;
    private readonly SkiaRenderer _renderer;

    public RenderingVisualTests()
    {
        // Set up directories
        var testDataDir = Path.Combine(Directory.GetCurrentDirectory(), "Visual");
        _baselinesDir = Path.Combine(testDataDir, "baselines");
        _testOutputDir = Path.Combine(testDataDir, "test-output");

        Directory.CreateDirectory(_baselinesDir);
        Directory.CreateDirectory(_testOutputDir);

        _renderer = new SkiaRenderer();
    }

    public void Dispose()
    {
        // Cleanup is handled by test infrastructure
    }

    #region Helper Methods

    private SKBitmap RenderTestPdf(string testName, string content)
    {
        var pdfBytes = CreatePdfWithContent(content);
        var pdfPath = Path.Combine(_testOutputDir, $"{testName}.pdf");
        File.WriteAllBytes(pdfPath, pdfBytes);

        // Render
        using var doc = PdfDocument.Open(pdfBytes);
        var page = doc.GetPage(1);
        var options = new RenderOptions { Dpi = 150 }; // 150 DPI for reasonable file size
        return _renderer.RenderPage(page, options);
    }

    private void AssertVisualMatch(SKBitmap actual, string testName, double maxDifference = 0.01)
    {
        var baselinePath = Path.Combine(_baselinesDir, $"{testName}.png");
        var actualPath = Path.Combine(_testOutputDir, $"{testName}-actual.png");
        var diffPath = Path.Combine(_testOutputDir, $"{testName}-diff.png");

        // Save actual output for debugging
        VisualAssertions.SavePng(actual, actualPath);

        // If baseline doesn't exist, save actual as baseline template
        if (!File.Exists(baselinePath))
        {
            VisualAssertions.SavePng(actual, baselinePath);
            Assert.Fail($"Baseline not found. Generated baseline at: {baselinePath}\n" +
                       $"Please manually verify the image looks correct and re-run the test.");
        }

        // Compare against baseline
        actual.ShouldVisuallyMatch(baselinePath, maxDifference, diffPath);
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

    #endregion

    #region Text Rendering Tests

    [Fact]
    public void SimpleText_RendersCorrectly()
    {
        // Arrange & Act
        var content = "BT /F1 24 Tf 100 700 Td (Hello, World!) Tj ET";
        var bitmap = RenderTestPdf("simple-text", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "simple-text");
        }
    }

    [Fact]
    public void MultilineText_RendersCorrectly()
    {
        // Arrange & Act
        var content = "BT /F1 14 Tf 50 700 Td (Line 1) Tj 0 -20 Td (Line 2) Tj 0 -20 Td (Line 3) Tj ET";
        var bitmap = RenderTestPdf("multiline-text", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "multiline-text");
        }
    }

    [Fact]
    public void DifferentFontSizes_RenderCorrectly()
    {
        // Arrange & Act
        var content = @"
            BT
            /F1 10 Tf 50 750 Td (10pt text) Tj
            /F1 16 Tf 0 -30 Td (16pt text) Tj
            /F1 24 Tf 0 -40 Td (24pt text) Tj
            /F1 36 Tf 0 -60 Td (36pt text) Tj
            ET
        ";
        var bitmap = RenderTestPdf("font-sizes", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "font-sizes");
        }
    }

    #endregion

    #region Graphics Rendering Tests

    [Fact]
    public void Rectangle_RendersCorrectly()
    {
        // Arrange & Act
        var content = "0 G 2 w 100 600 200 100 re S";
        var bitmap = RenderTestPdf("rectangle", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "rectangle");
        }
    }

    [Fact]
    public void FilledRectangle_RendersCorrectly()
    {
        // Arrange & Act
        var content = "0.5 g 100 600 200 100 re f";
        var bitmap = RenderTestPdf("filled-rectangle", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "filled-rectangle");
        }
    }

    [Fact]
    public void BlackRectangle_ForRedactionVerification()
    {
        // Arrange & Act
        var content = "0 g 100 650 300 50 re f";
        var bitmap = RenderTestPdf("black-rectangle-redaction", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "black-rectangle-redaction");
        }
    }

    #endregion

    #region Combined Tests

    [Fact]
    public void TextWithBackground_RendersCorrectly()
    {
        // Arrange & Act
        var content = "0.9 g 50 650 300 80 re f BT /F1 18 Tf 60 700 Td (Text on gray background) Tj ET";
        var bitmap = RenderTestPdf("text-with-background", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "text-with-background");
        }
    }

    #endregion

    #region Advanced Text Operators

    [Fact]
    public void TextArrayOperator_TJ_RendersCorrectly()
    {
        // Arrange & Act - TJ operator with array of strings and positioning adjustments
        var content = "BT /F1 20 Tf 100 700 Td [(H) -50 (e) -50 (l) -50 (l) -50 (o)] TJ ET";
        var bitmap = RenderTestPdf("text-array-tj", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "text-array-tj");
        }
    }

    [Fact]
    public void TextNextLine_Quote_RendersCorrectly()
    {
        // Arrange & Act - ' operator (move to next line and show text)
        var content = "BT /F1 16 Tf 100 700 Td 20 TL (Line 1) Tj (Line 2) ' (Line 3) ' ET";
        var bitmap = RenderTestPdf("text-next-line", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "text-next-line");
        }
    }

    [Fact]
    public void TextSpacingAndShow_DoubleQuote_RendersCorrectly()
    {
        // Arrange & Act - " operator (set word/char spacing and show)
        var content = "BT /F1 16 Tf 100 700 Td 20 TL 2 3 (Spaced Text) \" ET";
        var bitmap = RenderTestPdf("text-spacing-show", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "text-spacing-show");
        }
    }

    [Fact]
    public void TextCharacterSpacing_Tc_RendersCorrectly()
    {
        // Arrange & Act - Tc operator (character spacing)
        var content = @"
            BT
            /F1 14 Tf 50 720 Td (Normal spacing) Tj
            5 Tc 0 -30 Td (Wide spacing) Tj
            -2 Tc 0 -30 Td (Tight spacing) Tj
            ET
        ";
        var bitmap = RenderTestPdf("text-char-spacing", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "text-char-spacing");
        }
    }

    [Fact]
    public void TextWordSpacing_Tw_RendersCorrectly()
    {
        // Arrange & Act - Tw operator (word spacing)
        var content = @"
            BT
            /F1 14 Tf 50 720 Td (Hello World Test) Tj
            10 Tw 0 -30 Td (Hello World Test) Tj
            ET
        ";
        var bitmap = RenderTestPdf("text-word-spacing", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "text-word-spacing");
        }
    }

    [Fact]
    public void TextHorizontalScaling_Tz_RendersCorrectly()
    {
        // Arrange & Act - Tz operator (horizontal scaling)
        var content = @"
            BT
            /F1 16 Tf 50 720 Td (Normal Width) Tj
            150 Tz 0 -30 Td (Wide Text) Tj
            50 Tz 0 -30 Td (Narrow) Tj
            ET
        ";
        var bitmap = RenderTestPdf("text-horizontal-scaling", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "text-horizontal-scaling");
        }
    }

    [Fact]
    public void TextMatrix_Tm_RendersCorrectly()
    {
        // Arrange & Act - Tm operator (text matrix for rotation/scaling)
        var content = @"
            BT
            /F1 16 Tf
            1 0 0 1 100 700 Tm (Normal) Tj
            0.7071 0.7071 -0.7071 0.7071 200 600 Tm (Rotated 45deg) Tj
            ET
        ";
        var bitmap = RenderTestPdf("text-matrix", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "text-matrix");
        }
    }

    #endregion

    #region Graphics State Operators

    [Fact]
    public void GraphicsState_SaveRestore_RendersCorrectly()
    {
        // Arrange & Act - q/Q operators (save/restore graphics state)
        var content = @"
            2 w
            100 700 100 50 re S
            q
            5 w
            100 600 100 50 re S
            Q
            100 500 100 50 re S
        ";
        var bitmap = RenderTestPdf("graphics-state-save-restore", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "graphics-state-save-restore");
        }
    }

    [Fact]
    public void TransformationMatrix_cm_RendersCorrectly()
    {
        // Arrange & Act - cm operator (transformation matrix)
        var content = @"
            0 G 2 w
            100 600 50 50 re S
            1 0 0 1 150 0 cm
            100 600 50 50 re S
            0.7071 0.7071 -0.7071 0.7071 200 -100 cm
            100 600 50 50 re S
        ";
        var bitmap = RenderTestPdf("transformation-matrix", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "transformation-matrix");
        }
    }

    #endregion

    #region Path Construction Operators

    [Fact]
    public void PathMoveLine_m_l_RendersCorrectly()
    {
        // Arrange & Act - m (move to) and l (line to) operators
        var content = @"
            0 G 2 w
            100 700 m
            200 700 l
            200 650 l
            100 650 l
            h
            S
        ";
        var bitmap = RenderTestPdf("path-move-line", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "path-move-line");
        }
    }

    [Fact]
    public void PathBezierCurve_c_RendersCorrectly()
    {
        // Arrange & Act - c operator (cubic Bezier curve)
        var content = @"
            0 G 2 w
            100 600 m
            100 700 200 700 200 600 c
            S
        ";
        var bitmap = RenderTestPdf("path-bezier-curve", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "path-bezier-curve");
        }
    }

    [Fact]
    public void PathBezierVariations_v_y_RendersCorrectly()
    {
        // Arrange & Act - v and y operators (Bezier curve variations)
        var content = @"
            0 G 2 w
            100 650 m
            150 700 200 650 v
            S
            250 650 m
            250 700 300 650 y
            S
        ";
        var bitmap = RenderTestPdf("path-bezier-variations", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "path-bezier-variations");
        }
    }

    [Fact]
    public void PathClosePath_h_RendersCorrectly()
    {
        // Arrange & Act - h operator (close path)
        var content = @"
            0 G 3 w
            100 700 m
            200 700 l
            150 650 l
            h
            S
        ";
        var bitmap = RenderTestPdf("path-close-path", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "path-close-path");
        }
    }

    #endregion

    #region Path Painting Operators

    [Fact]
    public void PathFillAndStroke_B_RendersCorrectly()
    {
        // Arrange & Act - B operator (fill and stroke)
        var content = @"
            0.8 g
            0 G 2 w
            100 650 100 80 re
            B
        ";
        var bitmap = RenderTestPdf("path-fill-and-stroke", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "path-fill-and-stroke");
        }
    }

    [Fact]
    public void PathFillAndStrokeClosed_b_RendersCorrectly()
    {
        // Arrange & Act - b operator (close, fill, and stroke)
        var content = @"
            0.7 g
            0 G 2 w
            100 700 m
            200 700 l
            200 650 l
            100 650 l
            b
        ";
        var bitmap = RenderTestPdf("path-fill-stroke-close", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "path-fill-stroke-close");
        }
    }

    [Fact]
    public void PathEvenOddFill_fStar_RendersCorrectly()
    {
        // Arrange & Act - f* operator (even-odd fill rule)
        var content = @"
            0.5 g
            100 650 50 100 re
            125 660 50 80 re
            f*
        ";
        var bitmap = RenderTestPdf("path-evenodd-fill", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "path-evenodd-fill");
        }
    }

    [Fact]
    public void PathFillStrokeEvenOdd_BStar_RendersCorrectly()
    {
        // Arrange & Act - B* operator (fill with even-odd and stroke)
        var content = @"
            0.6 g
            0 G 2 w
            100 650 50 100 re
            125 660 50 80 re
            B*
        ";
        var bitmap = RenderTestPdf("path-fill-stroke-evenodd", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "path-fill-stroke-evenodd");
        }
    }

    #endregion

    #region Color Operators

    [Fact]
    public void ColorRGB_rg_RG_RendersCorrectly()
    {
        // Arrange & Act - rg/RG operators (RGB color)
        var content = @"
            1 0 0 rg
            50 700 80 50 re f
            0 1 0 rg
            150 700 80 50 re f
            0 0 1 rg
            250 700 80 50 re f
        ";
        var bitmap = RenderTestPdf("color-rgb", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "color-rgb");
        }
    }

    [Fact]
    public void ColorGray_g_G_RendersCorrectly()
    {
        // Arrange & Act - g/G operators (grayscale)
        var content = @"
            0 g
            50 700 50 50 re f
            0.3 g
            120 700 50 50 re f
            0.6 g
            190 700 50 50 re f
            0.9 g
            260 700 50 50 re f
        ";
        var bitmap = RenderTestPdf("color-gray", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "color-gray");
        }
    }

    #endregion

    #region Line Style Operators

    [Fact]
    public void LineWidth_w_RendersCorrectly()
    {
        // Arrange & Act - w operator (line width)
        var content = @"
            0 G
            1 w 50 750 m 200 750 l S
            3 w 50 720 m 200 720 l S
            5 w 50 690 m 200 690 l S
            10 w 50 660 m 200 660 l S
        ";
        var bitmap = RenderTestPdf("line-width", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "line-width");
        }
    }

    [Fact]
    public void LineCap_J_RendersCorrectly()
    {
        // Arrange & Act - J operator (line cap style)
        var content = @"
            0 G 10 w
            0 J 100 750 m 200 750 l S
            1 J 100 720 m 200 720 l S
            2 J 100 690 m 200 690 l S
        ";
        var bitmap = RenderTestPdf("line-cap", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "line-cap");
        }
    }

    [Fact]
    public void LineJoin_j_RendersCorrectly()
    {
        // Arrange & Act - j operator (line join style)
        var content = @"
            0 G 5 w
            0 j 50 750 m 100 750 l 100 700 l S
            1 j 150 750 m 200 750 l 200 700 l S
            2 j 250 750 m 300 750 l 300 700 l S
        ";
        var bitmap = RenderTestPdf("line-join", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "line-join");
        }
    }

    [Fact]
    public void DashPattern_d_RendersCorrectly()
    {
        // Arrange & Act - d operator (dash pattern)
        var content = @"
            0 G 2 w
            [] 0 d 50 750 m 300 750 l S
            [5 3] 0 d 50 720 m 300 720 l S
            [10 5 2 5] 0 d 50 690 m 300 690 l S
        ";
        var bitmap = RenderTestPdf("dash-pattern", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "dash-pattern");
        }
    }

    #endregion

    #region Complex Shapes

    [Fact]
    public void ComplexPath_Star_RendersCorrectly()
    {
        // Arrange & Act - Complex path forming a star
        var content = @"
            0.9 0.7 0 rg
            0 G 2 w
            200 750 m
            220 700 l
            270 700 l
            230 670 l
            250 620 l
            200 650 l
            150 620 l
            170 670 l
            130 700 l
            180 700 l
            h
            B
        ";
        var bitmap = RenderTestPdf("complex-star", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "complex-star");
        }
    }

    [Fact]
    public void ComplexPath_SmoothCurves_RendersCorrectly()
    {
        // Arrange & Act - Smooth curves using Bezier operators
        var content = @"
            0 0.6 0.8 rg
            0 G 2 w
            100 650 m
            100 720 150 750 200 750 c
            250 750 300 720 300 650 c
            300 580 250 550 200 550 c
            150 550 100 580 100 650 c
            B
        ";
        var bitmap = RenderTestPdf("complex-smooth-curves", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "complex-smooth-curves");
        }
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void EmptyPage_RendersWhite()
    {
        // Arrange & Act
        var content = "";
        var bitmap = RenderTestPdf("empty-page", content);

        // Assert
        using (bitmap)
        {
            AssertVisualMatch(bitmap, "empty-page");

            // Also verify it's actually white
            var centerPixel = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
            centerPixel.Red.Should().BeGreaterThan(250);
            centerPixel.Green.Should().BeGreaterThan(250);
            centerPixel.Blue.Should().BeGreaterThan(250);
        }
    }

    #endregion
}
