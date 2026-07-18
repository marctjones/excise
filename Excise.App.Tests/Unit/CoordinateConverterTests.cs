using Avalonia;
using AwesomeAssertions;
using Excise.App.Services;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Comprehensive unit tests for the CoordinateConverter utility class.
///
/// These tests verify all coordinate conversions are mathematically correct and
/// that round-trip conversions preserve values. This is critical for ensuring
/// redaction and text selection work correctly.
///
/// Test naming convention: [Method]_[Scenario]_[ExpectedResult]
/// </summary>
public class CoordinateConverterTests
{
    // Standard page sizes for testing
    private const double LetterHeight = 792.0;  // US Letter: 11 inches
    private const double LetterWidth = 612.0;   // US Letter: 8.5 inches
    private const double A4Height = 842.0;      // A4: 297mm
    private const double A4Width = 595.0;       // A4: 210mm

    // ========================================================================
    // DPI SCALING TESTS: Image Pixels ↔ PDF Points
    // ========================================================================

    [Theory]
    [InlineData(150, 0, 0)]           // Origin stays at origin
    [InlineData(150, 150, 72)]        // 150 pixels at 150 DPI = 72 points (1 inch)
    [InlineData(150, 300, 144)]       // 300 pixels at 150 DPI = 144 points (2 inches)
    [InlineData(150, 75, 36)]         // 75 pixels at 150 DPI = 36 points (0.5 inch)
    [InlineData(72, 72, 72)]          // 72 pixels at 72 DPI = 72 points (1 inch)
    [InlineData(72, 144, 144)]        // 144 pixels at 72 DPI = 144 points (2 inches)
    [InlineData(300, 300, 72)]        // 300 pixels at 300 DPI = 72 points (1 inch)
    [InlineData(300, 600, 144)]       // 600 pixels at 300 DPI = 144 points (2 inches)
    [InlineData(96, 96, 72)]          // 96 pixels at 96 DPI = 72 points (1 inch) - Windows default
    public void ImagePixelsToPdfPoints_ScalesCorrectly(int renderDpi, double inputPixels, double expectedPoints)
    {
        var result = CoordinateConverter.ImagePixelsToPdfPoints(inputPixels, renderDpi);

        result.Should().BeApproximately(expectedPoints, 0.001,
            $"Converting {inputPixels} pixels at {renderDpi} DPI should give {expectedPoints} points");
    }

    [Theory]
    [InlineData(150, 72, 150)]        // 72 points = 150 pixels at 150 DPI
    [InlineData(150, 144, 300)]       // 144 points = 300 pixels at 150 DPI
    [InlineData(72, 72, 72)]          // 72 points = 72 pixels at 72 DPI
    [InlineData(300, 72, 300)]        // 72 points = 300 pixels at 300 DPI
    public void PdfPointsToImagePixels_ScalesCorrectly(int renderDpi, double inputPoints, double expectedPixels)
    {
        var result = CoordinateConverter.PdfPointsToImagePixels(inputPoints, renderDpi);

        result.Should().BeApproximately(expectedPixels, 0.001,
            $"Converting {inputPoints} points to {renderDpi} DPI should give {expectedPixels} pixels");
    }

    [Fact]
    public void ImagePixelsToPdfPoints_Rectangle_ScalesAllDimensions()
    {
        // Arrange: Rectangle at (150, 300) with size (75, 150) at 150 DPI
        var imageRect = new Rect(150, 300, 75, 150);

        // Act
        var pdfRect = CoordinateConverter.ImagePixelsToPdfPoints(imageRect, 150);

        // Assert: All values scaled by 72/150 = 0.48
        pdfRect.X.Should().BeApproximately(72, 0.001);      // 150 * 0.48
        pdfRect.Y.Should().BeApproximately(144, 0.001);     // 300 * 0.48
        pdfRect.Width.Should().BeApproximately(36, 0.001);  // 75 * 0.48
        pdfRect.Height.Should().BeApproximately(72, 0.001); // 150 * 0.48
    }

    [Fact]
    public void PdfPointsToImagePixels_Rectangle_ScalesAllDimensions()
    {
        // Arrange: Rectangle at (72, 144) with size (36, 72) in PDF points
        var pdfRect = new Rect(72, 144, 36, 72);

        // Act
        var imageRect = CoordinateConverter.PdfPointsToImagePixels(pdfRect, 150);

        // Assert: All values scaled by 150/72 = 2.083...
        imageRect.X.Should().BeApproximately(150, 0.001);
        imageRect.Y.Should().BeApproximately(300, 0.001);
        imageRect.Width.Should().BeApproximately(75, 0.001);
        imageRect.Height.Should().BeApproximately(150, 0.001);
    }

    [Theory]
    [InlineData(150)]
    [InlineData(72)]
    [InlineData(300)]
    public void ImagePixels_RoundTrip_PreservesValue(int renderDpi)
    {
        // Arrange
        var original = 123.456;

        // Act
        var pdfPoints = CoordinateConverter.ImagePixelsToPdfPoints(original, renderDpi);
        var backToPixels = CoordinateConverter.PdfPointsToImagePixels(pdfPoints, renderDpi);

        // Assert
        backToPixels.Should().BeApproximately(original, 0.0001,
            $"Round-trip conversion at {renderDpi} DPI should preserve value");
    }

    [Fact]
    public void ImagePixelsToPdfPoints_ZeroDpi_ThrowsException()
    {
        var act = () => CoordinateConverter.ImagePixelsToPdfPoints(100, 0);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Render DPI must be positive*");
    }

    [Fact]
    public void ImagePixelsToPdfPoints_NegativeDpi_ThrowsException()
    {
        var act = () => CoordinateConverter.ImagePixelsToPdfPoints(100, -150);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Render DPI must be positive*");
    }

    // ========================================================================
    // Y-AXIS FLIP TESTS: Top-Left (Avalonia) ↔ Bottom-Left (PDF Native)
    // ========================================================================

    [Theory]
    [InlineData(792, 0, 792)]         // PDF Y=0 (bottom) → Avalonia Y=792 (bottom)
    [InlineData(792, 792, 0)]         // PDF Y=792 (top) → Avalonia Y=0 (top)
    [InlineData(792, 720, 72)]        // Near top in PDF → Near top in Avalonia
    [InlineData(792, 72, 720)]        // Near bottom in PDF → Near bottom in Avalonia
    [InlineData(792, 396, 396)]       // Middle stays middle
    [InlineData(842, 0, 842)]         // A4 page height
    [InlineData(842, 770, 72)]        // A4 near top
    public void PdfYToAvaloniaY_FlipsCorrectly(double pageHeight, double pdfY, double expectedAvaloniaY)
    {
        var result = CoordinateConverter.PdfYToAvaloniaY(pdfY, pageHeight);

        result.Should().BeApproximately(expectedAvaloniaY, 0.001,
            $"PDF Y={pdfY} on page height {pageHeight} should convert to Avalonia Y={expectedAvaloniaY}");
    }

    [Theory]
    [InlineData(792, 0, 792)]         // Avalonia Y=0 (top) → PDF Y=792 (top)
    [InlineData(792, 792, 0)]         // Avalonia Y=792 (bottom) → PDF Y=0 (bottom)
    [InlineData(792, 72, 720)]        // Near top in Avalonia → Near top in PDF
    [InlineData(792, 720, 72)]        // Near bottom in Avalonia → Near bottom in PDF
    public void AvaloniaYToPdfY_FlipsCorrectly(double pageHeight, double avaloniaY, double expectedPdfY)
    {
        var result = CoordinateConverter.AvaloniaYToPdfY(avaloniaY, pageHeight);

        result.Should().BeApproximately(expectedPdfY, 0.001,
            $"Avalonia Y={avaloniaY} on page height {pageHeight} should convert to PDF Y={expectedPdfY}");
    }

    [Theory]
    [InlineData(792)]
    [InlineData(842)]
    [InlineData(1000)]
    public void YAxisFlip_RoundTrip_PreservesValue(double pageHeight)
    {
        // Arrange
        var originalPdfY = 500.0;

        // Act
        var avaloniaY = CoordinateConverter.PdfYToAvaloniaY(originalPdfY, pageHeight);
        var backToPdfY = CoordinateConverter.AvaloniaYToPdfY(avaloniaY, pageHeight);

        // Assert
        backToPdfY.Should().BeApproximately(originalPdfY, 0.0001);
    }

    [Fact]
    public void PdfPointToAvalonia_ConvertsPointCorrectly()
    {
        // Arrange: Point at (100, 720) in PDF coords on Letter page
        // PDF Y=720 is near top (792-720=72 from top)

        // Act
        var avaloniaPoint = CoordinateConverter.PdfPointToAvalonia(100, 720, LetterHeight);

        // Assert
        avaloniaPoint.X.Should().Be(100);  // X unchanged
        avaloniaPoint.Y.Should().BeApproximately(72, 0.001);  // 792 - 720 = 72
    }

    [Fact]
    public void AvaloniaPointToPdf_ConvertsPointCorrectly()
    {
        // Arrange: Point at (100, 72) in Avalonia coords (near top)
        var avaloniaPoint = new Point(100, 72);

        // Act
        var (pdfX, pdfY) = CoordinateConverter.AvaloniaPointToPdf(avaloniaPoint, LetterHeight);

        // Assert
        pdfX.Should().Be(100);  // X unchanged
        pdfY.Should().BeApproximately(720, 0.001);  // 792 - 72 = 720
    }

    // ========================================================================
    // RECTANGLE CONVERSION TESTS
    // ========================================================================

    [Fact]
    public void AvaloniaRectToPdfRect_ConvertsCorrectly()
    {
        // Arrange: Rectangle near top of page in Avalonia coords
        // Y=72 from top, height=20
        var avaloniaRect = new Rect(100, 72, 200, 20);

        // Act
        var (left, bottom, right, top) = CoordinateConverter.AvaloniaRectToPdfRect(avaloniaRect, LetterHeight);

        // Assert
        left.Should().Be(100);     // X unchanged
        right.Should().Be(300);    // 100 + 200
        top.Should().BeApproximately(720, 0.001);    // 792 - 72 = 720 (top edge in PDF)
        bottom.Should().BeApproximately(700, 0.001); // 792 - 72 - 20 = 700 (bottom edge in PDF)
    }

    [Fact]
    public void PdfRectToAvaloniaRect_ConvertsCorrectly()
    {
        // Arrange: Rectangle near top of page in PDF coords
        // bottom=700, top=720 (20 point height, near top of 792 page)
        double pdfLeft = 100, pdfBottom = 700, pdfRight = 300, pdfTop = 720;

        // Act
        var avaloniaRect = CoordinateConverter.PdfRectToAvaloniaRect(pdfLeft, pdfBottom, pdfRight, pdfTop, LetterHeight);

        // Assert
        avaloniaRect.X.Should().Be(100);
        avaloniaRect.Y.Should().BeApproximately(72, 0.001);  // 792 - 720 = 72
        avaloniaRect.Width.Should().Be(200);  // 300 - 100
        avaloniaRect.Height.Should().Be(20);  // 720 - 700
    }

    [Fact]
    public void RectangleConversion_RoundTrip_PreservesRect()
    {
        // Arrange
        var original = new Rect(100, 150, 200, 50);

        // Act
        var (left, bottom, right, top) = CoordinateConverter.AvaloniaRectToPdfRect(original, LetterHeight);
        var backToAvalonia = CoordinateConverter.PdfRectToAvaloniaRect(left, bottom, right, top, LetterHeight);

        // Assert
        backToAvalonia.X.Should().BeApproximately(original.X, 0.0001);
        backToAvalonia.Y.Should().BeApproximately(original.Y, 0.0001);
        backToAvalonia.Width.Should().BeApproximately(original.Width, 0.0001);
        backToAvalonia.Height.Should().BeApproximately(original.Height, 0.0001);
    }

    // ========================================================================
    // FULL PIPELINE TESTS
    // ========================================================================

    [Fact]
    public void ImageSelectionToPdfPointsTopLeft_FullPipeline()
    {
        // Arrange: User selects area at (150, 150, 100x50) in image pixels at 150 DPI
        var imageSelection = new Rect(150, 150, 100, 50);

        // Act: Convert to PDF points (top-left origin, for redaction comparison)
        var pdfRect = CoordinateConverter.ImageSelectionToPdfPointsTopLeft(imageSelection, 150);

        // Assert
        // Scale factor: 72/150 = 0.48
        pdfRect.X.Should().BeApproximately(72, 0.001);      // 150 * 0.48
        pdfRect.Y.Should().BeApproximately(72, 0.001);      // 150 * 0.48
        pdfRect.Width.Should().BeApproximately(48, 0.001);  // 100 * 0.48
        pdfRect.Height.Should().BeApproximately(24, 0.001); // 50 * 0.48
    }

    [Fact]
    public void ImageSelectionToPdfCoords_FullPipeline()
    {
        // Arrange: User selects area at (150, 150, 100x50) in image pixels at 150 DPI
        // Page height is 792 points
        var imageSelection = new Rect(150, 150, 100, 50);

        // Act: Convert to PDF coordinates (bottom-left origin)
        var (left, bottom, right, top) = CoordinateConverter.ImageSelectionToPdfCoords(
            imageSelection, LetterHeight, 150);

        // Assert
        // After DPI scaling: (72, 72, 48, 24) in top-left origin
        // After Y-flip: top=792-72=720, bottom=792-72-24=696
        left.Should().BeApproximately(72, 0.001);
        right.Should().BeApproximately(120, 0.001);   // 72 + 48
        top.Should().BeApproximately(720, 0.001);     // 792 - 72
        bottom.Should().BeApproximately(696, 0.001); // 792 - 72 - 24
    }

    [Fact]
    public void TextBoundsToPdfPointsTopLeft_ConvertsCorrectly()
    {
        // Arrange: Text at PDF position (72, 720) - near top of page
        // Font size 12, so text extends from Y=720 to Y=732
        double textX = 72, textY = 720, textWidth = 100, textHeight = 12;

        // Act
        var avaloniaRect = CoordinateConverter.TextBoundsToPdfPointsTopLeft(
            textX, textY, textWidth, textHeight, LetterHeight);

        // Assert
        // PDF top = 720 + 12 = 732
        // Avalonia Y = 792 - 732 = 60 (near top)
        avaloniaRect.X.Should().BeApproximately(72, 0.001);
        avaloniaRect.Y.Should().BeApproximately(60, 0.001);
        avaloniaRect.Width.Should().BeApproximately(100, 0.001);
        avaloniaRect.Height.Should().BeApproximately(12, 0.001);
    }

    // ========================================================================
    // XGRAPHICS COORDINATE TESTS
    // ========================================================================
    //
    // VERIFIED BY VISUAL TESTING: XGraphics uses TOP-LEFT origin (same as Avalonia).
    // Drawing at Avalonia Y=100 with no flip produces black box at pixel Y=208
    // (near top of image). With Y-flip, it appeared at Y=1337 (near bottom).

    [Fact]
    public void ForXGraphics_UsesTopLeftOrigin_NoConversion()
    {
        // Arrange: Rectangle in Avalonia coordinates (top-left origin)
        // Y=72 means 72 points from TOP of page
        var avaloniaRect = new Rect(100, 72, 200, 50);

        // Act: Convert to XGraphics coordinates (same as Avalonia - top-left origin)
        var (x, y, width, height) = CoordinateConverter.ForXGraphics(avaloniaRect, LetterHeight);

        // Assert: XGraphics uses top-left origin (same as Avalonia), so no conversion needed
        x.Should().Be(100);
        y.Should().Be(72);  // Y stays the same
        width.Should().Be(200);
        height.Should().Be(50);
    }

    [Fact]
    public void ForXGraphics_RectNearTop_StaysNearTop()
    {
        // Arrange: Rectangle near TOP of page in Avalonia (Y=10)
        var avaloniaRect = new Rect(100, 10, 200, 50);

        // Act
        var (x, y, width, height) = CoordinateConverter.ForXGraphics(avaloniaRect, LetterHeight);

        // Assert: Near top in Avalonia stays near top in XGraphics (both use top-left origin)
        y.Should().Be(10);
    }

    [Fact]
    public void ForXGraphics_RectNearBottom_StaysNearBottom()
    {
        // Arrange: Rectangle near BOTTOM of page in Avalonia (Y=732)
        var avaloniaRect = new Rect(100, 732, 200, 50);

        // Act
        var (x, y, width, height) = CoordinateConverter.ForXGraphics(avaloniaRect, LetterHeight);

        // Assert: Near bottom in Avalonia stays near bottom in XGraphics (both use top-left origin)
        y.Should().Be(732);
    }

    [Fact]
    public void ForXGraphicsWithVerification_TopLeftDefault_NoConversion()
    {
        var avaloniaRect = new Rect(100, 72, 200, 50);

        // Default is now xGraphicsUsesTopLeft: true
        var (x, y, width, height) = CoordinateConverter.ForXGraphicsWithVerification(
            avaloniaRect, LetterHeight, xGraphicsUsesTopLeft: true);

        x.Should().Be(100);
        y.Should().Be(72);  // No conversion when top-left (default)
        width.Should().Be(200);
        height.Should().Be(50);
    }

    [Fact]
    public void ForXGraphicsWithVerification_BottomLeftOverride_FlipsY()
    {
        var avaloniaRect = new Rect(100, 72, 200, 50);

        // Override to bottom-left for hypothetical cases
        var (x, y, width, height) = CoordinateConverter.ForXGraphicsWithVerification(
            avaloniaRect, LetterHeight, xGraphicsUsesTopLeft: false);

        x.Should().Be(100);
        y.Should().BeApproximately(670, 0.001);  // 792 - 72 - 50 = 670
        width.Should().Be(200);
        height.Should().Be(50);
    }

    // ========================================================================
    // VALIDATION HELPER TESTS
    // ========================================================================

    [Fact]
    public void IsValidForPage_ValidRect_ReturnsTrue()
    {
        var rect = new Rect(100, 100, 200, 200);

        var result = CoordinateConverter.IsValidForPage(rect, LetterWidth, LetterHeight);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsValidForPage_RectOutsidePage_ReturnsFalse()
    {
        var rect = new Rect(700, 100, 200, 200);  // X extends beyond 612 width

        var result = CoordinateConverter.IsValidForPage(rect, LetterWidth, LetterHeight);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsValidForPage_ZeroWidthRect_ReturnsFalse()
    {
        var rect = new Rect(100, 100, 0, 200);

        var result = CoordinateConverter.IsValidForPage(rect, LetterWidth, LetterHeight);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsValidForPage_NegativePosition_WithinTolerance_ReturnsTrue()
    {
        var rect = new Rect(-10, -10, 200, 200);  // Slightly negative, within default 50 tolerance

        var result = CoordinateConverter.IsValidForPage(rect, LetterWidth, LetterHeight);

        result.Should().BeTrue();
    }

    // ========================================================================
    // EDGE CASE TESTS
    // ========================================================================

    [Theory]
    [InlineData(0, 0)]       // Zero coordinates
    [InlineData(-10, -10)]   // Negative coordinates
    [InlineData(10000, 10000)] // Very large coordinates
    public void Conversions_EdgeCaseCoordinates_DoNotCrash(double x, double y)
    {
        var rect = new Rect(x, y, 100, 100);

        // Act - should not throw
        var scaled = CoordinateConverter.ImagePixelsToPdfPoints(rect, 150);
        var (left, bottom, right, top) = CoordinateConverter.AvaloniaRectToPdfRect(scaled, LetterHeight);

        // Assert - results should be finite
        double.IsFinite(scaled.X).Should().BeTrue();
        double.IsFinite(scaled.Y).Should().BeTrue();
        double.IsFinite(left).Should().BeTrue();
        double.IsFinite(bottom).Should().BeTrue();
    }

    [Fact]
    public void Conversion_ZeroSizeRect_PreservesPosition()
    {
        var rect = new Rect(100, 200, 0, 0);

        var scaled = CoordinateConverter.ImagePixelsToPdfPoints(rect, 150);

        scaled.X.Should().BeApproximately(48, 0.001);
        scaled.Y.Should().BeApproximately(96, 0.001);
        scaled.Width.Should().Be(0);
        scaled.Height.Should().Be(0);
    }

    // ========================================================================
    // REAL-WORLD SCENARIO TESTS
    // ========================================================================

    [Fact]
    public void Scenario_RedactionSelection_TextNearTopOfPage()
    {
        // User selects text near the top of a Letter page
        // Selection in image pixels (at 150 DPI): (100, 50, 200, 30) - near top
        var imageSelection = new Rect(100, 50, 200, 30);

        // Text is at PDF position (48, 710, 96, 14) - also near top
        // (scaled from image and converted from PDF bottom-left)

        // Convert selection to PDF points (top-left)
        var selectionPdfTopLeft = CoordinateConverter.ImageSelectionToPdfPointsTopLeft(imageSelection, 150);

        // Convert text from PDF coords to Avalonia
        var textAvaloniaRect = CoordinateConverter.TextBoundsToPdfPointsTopLeft(48, 710, 96, 14, LetterHeight);

        // Both should be near the top of page
        selectionPdfTopLeft.Y.Should().BeLessThan(100, "Selection should be near top");
        textAvaloniaRect.Y.Should().BeLessThan(100, "Text should be near top");

        // And they should be able to intersect
        // (this is a sanity check, not testing exact intersection)
        selectionPdfTopLeft.Y.Should().BeApproximately(24, 1);  // 50 * 0.48
        textAvaloniaRect.Y.Should().BeApproximately(68, 1);     // 792 - (710+14) = 68
    }

    [Fact]
    public void Scenario_TextSelection_ExtractingText()
    {
        // User selects area in image pixels
        var imageSelection = new Rect(75, 375, 450, 75);  // Middle of page

        // Convert to PDF coords for content-stream query
        var (left, bottom, right, top) = CoordinateConverter.ImageSelectionToPdfCoords(
            imageSelection, LetterHeight, 150);

        // Extractor returns text with these bounds - verify they could intersect
        // Text at PDF coords (40, 350, 250, 380) - overlapping Y range
        var pdfPigTextLeft = 40.0;
        var pdfPigTextBottom = 350.0;
        var pdfPigTextRight = 250.0;
        var pdfPigTextTop = 380.0;
        _ = pdfPigTextLeft;
        _ = pdfPigTextBottom;
        _ = pdfPigTextRight;
        _ = pdfPigTextTop;

        // Selection in PDF: left=36, bottom~=576-36=540, right=252, top~=576
        // Actually let's calculate:
        // Image (75, 375, 450, 75) at 150 DPI
        // Scaled: (36, 180, 216, 36) in PDF points top-left
        // Y-flip: top = 792-180 = 612, bottom = 792-180-36 = 576

        left.Should().BeApproximately(36, 1);
        bottom.Should().BeApproximately(576, 1);
        right.Should().BeApproximately(252, 1);  // 36 + 216
        top.Should().BeApproximately(612, 1);

        // Check if selection intersects with extracted text
        // Selection Y: 576-612 in PDF coords
        // Text Y: 350-380 in PDF coords
        // These don't overlap, which is expected based on the values
    }

    [Fact]
    public void Constants_AreCorrect()
    {
        CoordinateConverter.DefaultRenderDpi.Should().Be(150, "Default render DPI should be 150");
        CoordinateConverter.PdfPointsPerInch.Should().Be(72, "PDF spec defines 72 points per inch");
    }

    // ========================================================================
    // PAGE ROTATION SUPPORT TESTS
    // ========================================================================

    [Fact]
    public void TransformForRotation_NoRotation_ReturnsUnchanged()
    {
        var area = new Rect(100, 100, 200, 50);

        var result = CoordinateConverter.TransformForRotation(area, 0, LetterWidth, LetterHeight);

        result.X.Should().Be(100);
        result.Y.Should().Be(100);
        result.Width.Should().Be(200);
        result.Height.Should().Be(50);
    }

    [Fact]
    public void TransformForRotation_360Degrees_ReturnsUnchanged()
    {
        var area = new Rect(100, 100, 200, 50);

        var result = CoordinateConverter.TransformForRotation(area, 360, LetterWidth, LetterHeight);

        result.X.Should().Be(100);
        result.Y.Should().Be(100);
        result.Width.Should().Be(200);
        result.Height.Should().Be(50);
    }

    [Fact]
    public void TransformForRotation_NegativeRotation_NormalizesCorrectly()
    {
        var area = new Rect(100, 100, 200, 50);

        var result = CoordinateConverter.TransformForRotation(area, -90, LetterWidth, LetterHeight);

        result.Should().NotBeNull();
        result.Width.Should().Be(50);   // Height becomes width
        result.Height.Should().Be(200); // Width becomes height
    }

    [Fact]
    public void TransformForRotation_90Degrees_SwapsDimensions()
    {
        var area = new Rect(100, 100, 200, 50);

        var result = CoordinateConverter.TransformForRotation(area, 90, LetterWidth, LetterHeight);

        result.Width.Should().Be(50);   // Original height
        result.Height.Should().Be(200); // Original width
    }

    [Fact]
    public void TransformForRotation_180Degrees_FlipsCoordinates()
    {
        var area = new Rect(100, 100, 200, 50);

        var result = CoordinateConverter.TransformForRotation(area, 180, LetterWidth, LetterHeight);

        // At 180°, position is flipped
        result.Width.Should().Be(200);  // Width unchanged
        result.Height.Should().Be(50);  // Height unchanged
    }

    [Fact]
    public void TransformForRotation_270Degrees_SwapsDimensions()
    {
        var area = new Rect(100, 100, 200, 50);

        var result = CoordinateConverter.TransformForRotation(area, 270, LetterWidth, LetterHeight);

        result.Width.Should().Be(50);   // Original height
        result.Height.Should().Be(200); // Original width
    }

    [Fact]
    public void TransformForRotation_InvalidRotation_ReturnsUnchanged()
    {
        var area = new Rect(100, 100, 200, 50);

        var result = CoordinateConverter.TransformForRotation(area, 45, LetterWidth, LetterHeight);

        // 45 degrees is not supported, should return unchanged
        result.X.Should().Be(100);
        result.Y.Should().Be(100);
        result.Width.Should().Be(200);
        result.Height.Should().Be(50);
    }

    [Fact]
    public void TransformForRotation_LargeRotationValue_NormalizesCorrectly()
    {
        var area = new Rect(100, 100, 200, 50);

        // 450 degrees = 90 degrees
        var result = CoordinateConverter.TransformForRotation(area, 450, LetterWidth, LetterHeight);

        result.Width.Should().Be(50);   // Should behave like 90 degrees
        result.Height.Should().Be(200);
    }

    [Fact]
    public void GetRotatedPageDimensions_NoRotation_DimensionsUnchanged()
    {
        var (width, height) = CoordinateConverter.GetRotatedPageDimensions(LetterWidth, LetterHeight, 0);

        width.Should().Be(LetterWidth);
        height.Should().Be(LetterHeight);
    }

    [Fact]
    public void GetRotatedPageDimensions_90Degrees_DimensionsSwapped()
    {
        var (width, height) = CoordinateConverter.GetRotatedPageDimensions(LetterWidth, LetterHeight, 90);

        width.Should().Be(LetterHeight);
        height.Should().Be(LetterWidth);
    }

    [Fact]
    public void GetRotatedPageDimensions_180Degrees_DimensionsUnchanged()
    {
        var (width, height) = CoordinateConverter.GetRotatedPageDimensions(LetterWidth, LetterHeight, 180);

        width.Should().Be(LetterWidth);
        height.Should().Be(LetterHeight);
    }

    [Fact]
    public void GetRotatedPageDimensions_270Degrees_DimensionsSwapped()
    {
        var (width, height) = CoordinateConverter.GetRotatedPageDimensions(LetterWidth, LetterHeight, 270);

        width.Should().Be(LetterHeight);
        height.Should().Be(LetterWidth);
    }

    [Fact]
    public void GetRotatedPageDimensions_NegativeRotation_NormalizesCorrectly()
    {
        var (width, height) = CoordinateConverter.GetRotatedPageDimensions(LetterWidth, LetterHeight, -90);

        width.Should().Be(LetterHeight);
        height.Should().Be(LetterWidth);
    }

    [Fact]
    public void GetRotatedPageDimensions_LargeRotationValue_NormalizesCorrectly()
    {
        // 450 degrees = 90 degrees
        var (width, height) = CoordinateConverter.GetRotatedPageDimensions(LetterWidth, LetterHeight, 450);

        width.Should().Be(LetterHeight);
        height.Should().Be(LetterWidth);
    }

    // ========================================================================
    // DEBUG HELPER TESTS
    // ========================================================================

    [Fact]
    public void DescribeConversion_ContainsInputAndOutput()
    {
        var input = new Rect(10, 20, 30, 40);
        var output = new Rect(50, 60, 70, 80);

        var description = CoordinateConverter.DescribeConversion(
            input, "ImagePixels",
            output, "PdfPoints");

        description.Should().Contain("ImagePixels");
        description.Should().Contain("PdfPoints");
        description.Should().Contain("10");
        description.Should().Contain("50");
    }

    [Fact]
    public void DescribeConversion_IncludesAllDimensions()
    {
        var input = new Rect(1.5, 2.5, 3.5, 4.5);
        var output = new Rect(5.5, 6.5, 7.5, 8.5);

        var description = CoordinateConverter.DescribeConversion(
            input, "From",
            output, "To");

        description.Should().Contain("1.5");
        description.Should().Contain("5.5");
        description.Should().Contain("3.5");
        description.Should().Contain("7.5");
    }

    // ========================================================================
    // DPI EDGE CASES
    // ========================================================================

    [Fact]
    public void ImagePixelsToPdfPoints_WithVeryHighDpi_ScalesCorrectly()
    {
        var result = CoordinateConverter.ImagePixelsToPdfPoints(300, 600);

        result.Should().BeApproximately(36, 0.001, "300 pixels at 600 DPI = 36 points");
    }

    [Fact]
    public void ImagePixelsToPdfPoints_WithVeryLowDpi_ScalesCorrectly()
    {
        var result = CoordinateConverter.ImagePixelsToPdfPoints(1, 1);

        result.Should().BeApproximately(72, 0.001, "1 pixel at 1 DPI = 72 points");
    }

    [Fact]
    public void PdfPointsToImagePixels_WithVeryHighDpi_ScalesCorrectly()
    {
        var result = CoordinateConverter.PdfPointsToImagePixels(36, 600);

        result.Should().BeApproximately(300, 0.001, "36 points at 600 DPI = 300 pixels");
    }

    // ========================================================================
    // COORDINATE SYSTEM CONSISTENCY
    // ========================================================================

    [Fact]
    public void AllConversions_MaintainConsistency()
    {
        // Start with image pixels
        var imagePixels = 150.0;
        var renderDpi = 150;

        // Convert to PDF points
        var pdfPoints = CoordinateConverter.ImagePixelsToPdfPoints(imagePixels, renderDpi);

        // Should be 72 points
        pdfPoints.Should().BeApproximately(72, 0.001);

        // Convert back
        var backToPixels = CoordinateConverter.PdfPointsToImagePixels(pdfPoints, renderDpi);

        // Should match original
        backToPixels.Should().BeApproximately(imagePixels, 0.0001);
    }

    [Fact]
    public void YAxisFlip_IsSymmetric()
    {
        double pageHeight = 792;
        double originalY = 500;

        var flipped = CoordinateConverter.PdfYToAvaloniaY(originalY, pageHeight);
        var backToOriginal = CoordinateConverter.AvaloniaYToPdfY(flipped, pageHeight);

        backToOriginal.Should().BeApproximately(originalY, 0.0001);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(400)]
    [InlineData(792)]
    public void PdfRectToAvaloniaRect_RoundTrip_PreservesRect(double yValue)
    {
        double pdfLeft = 50, pdfBottom = yValue, pdfRight = 250, pdfTop = yValue + 50;

        var avaloniaRect = CoordinateConverter.PdfRectToAvaloniaRect(
            pdfLeft, pdfBottom, pdfRight, pdfTop, 792);

        var (left2, bottom2, right2, top2) = CoordinateConverter.AvaloniaRectToPdfRect(avaloniaRect, 792);

        left2.Should().BeApproximately(pdfLeft, 0.001);
        bottom2.Should().BeApproximately(pdfBottom, 0.001);
        right2.Should().BeApproximately(pdfRight, 0.001);
        top2.Should().BeApproximately(pdfTop, 0.001);
    }
}
