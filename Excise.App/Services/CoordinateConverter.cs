using Avalonia;
using System;

namespace Excise.App.Services;

/// <summary>
/// Centralized coordinate conversion utility for PDF operations.
///
/// ================================================================================
/// COORDINATE SYSTEMS OVERVIEW
/// ================================================================================
///
/// This application deals with FOUR coordinate systems:
///
/// 1. IMAGE PIXELS (Rendered PDF)
///    - Origin: Top-left corner
///    - Units: Pixels at render DPI (typically 150 DPI)
///    - Y-axis: Increases downward
///    - Used by: Canvas mouse events, rendered image display
///
/// 2. PDF POINTS - AVALONIA CONVENTION (Top-Left Origin)
///    - Origin: Top-left corner
///    - Units: Points (72 per inch)
///    - Y-axis: Increases downward
///    - Used by: TextBoundsCalculator output, redaction area matching, XGraphics
///
/// 3. PDF POINTS - PDF CONVENTION (Bottom-Left Origin)
///    - Origin: Bottom-left corner
///    - Units: Points (72 per inch)
///    - Y-axis: Increases upward
///    - Used by: PDF content streams, content-stream text extraction, PDF spec coordinates
///
/// 4. SCREEN PIXELS (Display)
///    - Origin: Top-left corner
///    - Units: Physical pixels (device DPI, typically 96 or scaled)
///    - Y-axis: Increases downward
///    - Used by: Window/control positioning (usually handled by framework)
///
/// ================================================================================
/// CONVERSION RULES
/// ================================================================================
///
/// DPI Scaling:
///   PDF Points = Image Pixels × (72 / RenderDPI)
///   Image Pixels = PDF Points × (RenderDPI / 72)
///
/// Y-Axis Flip (between top-left and bottom-left origins):
///   AvaloniaY = PageHeight - PdfY
///   PdfY = PageHeight - AvaloniaY
///
/// For rectangles with height:
///   AvaloniaY = PageHeight - PdfY - Height  (to position top of rect)
///   PdfY = PageHeight - AvaloniaY - Height  (to position bottom of rect)
///
/// ================================================================================
/// </summary>
public static class CoordinateConverter
{
    /// <summary>
    /// Default render DPI used by legacy editor service paths.
    /// Viewer interactions should pass the actual rendered-page DPI instead of relying on this fallback.
    /// </summary>
    public const int DefaultRenderDpi = 150;

    /// <summary>
    /// PDF standard: 72 points per inch.
    /// This is defined by the PDF specification and never changes.
    /// </summary>
    public const int PdfPointsPerInch = 72;

    // ========================================================================
    // DPI SCALING: Image Pixels ↔ PDF Points
    // ========================================================================

    /// <summary>
    /// Convert a value from rendered image pixels to PDF points.
    ///
    /// Use when: Converting mouse coordinates from rendered image to PDF space.
    ///
    /// Example: At 150 DPI, 150 pixels = 72 points = 1 inch
    /// </summary>
    /// <param name="imagePixels">Value in image pixel coordinates</param>
    /// <param name="renderDpi">DPI at which image was rendered (default 150)</param>
    /// <returns>Value in PDF points (72 DPI)</returns>
    public static double ImagePixelsToPdfPoints(double imagePixels, int renderDpi = DefaultRenderDpi)
    {
        if (renderDpi <= 0)
            throw new ArgumentException("Render DPI must be positive", nameof(renderDpi));

        return imagePixels * PdfPointsPerInch / renderDpi;
    }

    /// <summary>
    /// Convert a value from PDF points to rendered image pixels.
    ///
    /// Use when: Converting PDF coordinates for display on rendered image.
    ///
    /// Example: At 150 DPI, 72 points = 150 pixels = 1 inch
    /// </summary>
    /// <param name="pdfPoints">Value in PDF points</param>
    /// <param name="renderDpi">DPI at which image was rendered (default 150)</param>
    /// <returns>Value in image pixels</returns>
    public static double PdfPointsToImagePixels(double pdfPoints, int renderDpi = DefaultRenderDpi)
    {
        if (renderDpi <= 0)
            throw new ArgumentException("Render DPI must be positive", nameof(renderDpi));

        return pdfPoints * renderDpi / PdfPointsPerInch;
    }

    /// <summary>
    /// Convert rectangle from rendered image pixels to PDF points.
    /// Both use top-left origin, so only scaling is applied.
    ///
    /// Use when: Converting mouse selection rectangle to PDF coordinate space.
    /// </summary>
    /// <param name="imageRect">Rectangle in image pixels (top-left origin)</param>
    /// <param name="renderDpi">DPI at which image was rendered</param>
    /// <returns>Rectangle in PDF points (top-left origin)</returns>
    public static Rect ImagePixelsToPdfPoints(Rect imageRect, int renderDpi = DefaultRenderDpi)
    {
        var scale = (double)PdfPointsPerInch / renderDpi;
        return new Rect(
            imageRect.X * scale,
            imageRect.Y * scale,
            imageRect.Width * scale,
            imageRect.Height * scale);
    }

    /// <summary>
    /// Convert rectangle from PDF points to rendered image pixels.
    /// Both use top-left origin, so only scaling is applied.
    ///
    /// Use when: Converting PDF bounds for display on rendered image.
    /// </summary>
    /// <param name="pdfRect">Rectangle in PDF points (top-left origin)</param>
    /// <param name="renderDpi">DPI at which image was rendered</param>
    /// <returns>Rectangle in image pixels (top-left origin)</returns>
    public static Rect PdfPointsToImagePixels(Rect pdfRect, int renderDpi = DefaultRenderDpi)
    {
        var scale = (double)renderDpi / PdfPointsPerInch;
        return new Rect(
            pdfRect.X * scale,
            pdfRect.Y * scale,
            pdfRect.Width * scale,
            pdfRect.Height * scale);
    }

    // ========================================================================
    // Y-AXIS ORIGIN FLIP: Top-Left (Avalonia) ↔ Bottom-Left (PDF Native)
    // ========================================================================

    /// <summary>
    /// Convert Y coordinate from PDF convention (bottom-left, Y up) to Avalonia convention (top-left, Y down).
    ///
    /// Use when: Converting text position from PDF content stream for display/comparison.
    ///
    /// Example: On 792pt page, PDF Y=720 (near top) → Avalonia Y=72 (near top)
    /// </summary>
    /// <param name="pdfY">Y coordinate where 0 = bottom of page</param>
    /// <param name="pageHeight">Page height in PDF points</param>
    /// <returns>Y coordinate where 0 = top of page</returns>
    public static double PdfYToAvaloniaY(double pdfY, double pageHeight)
    {
        return pageHeight - pdfY;
    }

    /// <summary>
    /// Convert Y coordinate from Avalonia convention (top-left, Y down) to PDF convention (bottom-left, Y up).
    ///
    /// Use when: Converting screen selection Y for content-stream queries or PDF writing.
    ///
    /// Example: On 792pt page, Avalonia Y=72 (near top) → PDF Y=720 (near top)
    /// </summary>
    /// <param name="avaloniaY">Y coordinate where 0 = top of page</param>
    /// <param name="pageHeight">Page height in PDF points</param>
    /// <returns>Y coordinate where 0 = bottom of page</returns>
    public static double AvaloniaYToPdfY(double avaloniaY, double pageHeight)
    {
        return pageHeight - avaloniaY;
    }

    /// <summary>
    /// Convert a point from PDF coordinates to Avalonia coordinates.
    /// X is unchanged, Y is flipped.
    /// </summary>
    public static Point PdfPointToAvalonia(double pdfX, double pdfY, double pageHeight)
    {
        return new Point(pdfX, pageHeight - pdfY);
    }

    /// <summary>
    /// Convert a point from Avalonia coordinates to PDF coordinates.
    /// X is unchanged, Y is flipped.
    /// </summary>
    public static (double X, double Y) AvaloniaPointToPdf(Point avaloniaPoint, double pageHeight)
    {
        return (avaloniaPoint.X, pageHeight - avaloniaPoint.Y);
    }

    // ========================================================================
    // RECTANGLE CONVERSIONS WITH Y-FLIP
    // ========================================================================

    /// <summary>
    /// Convert rectangle from Avalonia coordinates (top-left origin) to PDF coordinates (bottom-left origin).
    ///
    /// Use when: Converting selection rectangle for content-stream text queries.
    ///
    /// The rectangle position changes because:
    /// - Avalonia Y is distance from TOP
    /// - PDF Y is distance from BOTTOM
    /// </summary>
    /// <param name="avaloniaRect">Rectangle in Avalonia coords (Y from top)</param>
    /// <param name="pageHeight">Page height in PDF points</param>
    /// <returns>Rectangle as (Left, Bottom, Right, Top) in PDF coords</returns>
    public static (double Left, double Bottom, double Right, double Top) AvaloniaRectToPdfRect(
        Rect avaloniaRect,
        double pageHeight)
    {
        // Avalonia: Y is from top, so avaloniaRect.Y is top edge, avaloniaRect.Bottom is bottom edge
        // PDF: Y is from bottom, so we need to flip
        var pdfTop = pageHeight - avaloniaRect.Y;                    // Avalonia top → PDF top
        var pdfBottom = pageHeight - avaloniaRect.Y - avaloniaRect.Height;  // Avalonia bottom → PDF bottom

        return (
            Left: avaloniaRect.X,
            Bottom: pdfBottom,
            Right: avaloniaRect.X + avaloniaRect.Width,
            Top: pdfTop
        );
    }

    /// <summary>
    /// Convert rectangle from PDF coordinates (bottom-left origin) to Avalonia coordinates (top-left origin).
    ///
    /// Use when: Converting extracted text bounds for display or intersection testing.
    /// </summary>
    /// <param name="pdfLeft">Left edge in PDF coords</param>
    /// <param name="pdfBottom">Bottom edge in PDF coords (Y from page bottom)</param>
    /// <param name="pdfRight">Right edge in PDF coords</param>
    /// <param name="pdfTop">Top edge in PDF coords (Y from page bottom)</param>
    /// <param name="pageHeight">Page height in PDF points</param>
    /// <returns>Rectangle in Avalonia coords (Y from top)</returns>
    public static Rect PdfRectToAvaloniaRect(
        double pdfLeft,
        double pdfBottom,
        double pdfRight,
        double pdfTop,
        double pageHeight)
    {
        // PDF top (high Y) becomes Avalonia Y (low value = near top)
        var avaloniaY = pageHeight - pdfTop;
        var width = pdfRight - pdfLeft;
        var height = pdfTop - pdfBottom;

        return new Rect(pdfLeft, avaloniaY, width, height);
    }

    // ========================================================================
    // COMBINED CONVERSIONS: Full Pipeline Helpers
    // ========================================================================

    /// <summary>
    /// Convert selection from image pixels (top-left) to PDF points (top-left).
    ///
    /// Use when: Converting mouse selection for redaction - both text bounds and
    /// selection use Avalonia convention (top-left origin).
    ///
    /// Pipeline: Image Pixels → PDF Points (both top-left, just scale)
    /// </summary>
    public static Rect ImageSelectionToPdfPointsTopLeft(Rect imageSelection, int renderDpi = DefaultRenderDpi)
    {
        return ImagePixelsToPdfPoints(imageSelection, renderDpi);
    }

    /// <summary>
    /// Convert selection from image pixels (top-left) to PDF coordinates (bottom-left).
    ///
    /// Use when: Converting mouse selection for content-stream text extraction queries.
    ///
    /// Pipeline: Image Pixels → PDF Points (top-left) → PDF Coords (bottom-left)
    /// </summary>
    public static (double Left, double Bottom, double Right, double Top) ImageSelectionToPdfCoords(
        Rect imageSelection,
        double pageHeightPoints,
        int renderDpi = DefaultRenderDpi)
    {
        // Step 1: Scale from image pixels to PDF points (top-left origin preserved)
        var pdfPointsTopLeft = ImagePixelsToPdfPoints(imageSelection, renderDpi);

        // Step 2: Convert from top-left to bottom-left origin
        return AvaloniaRectToPdfRect(pdfPointsTopLeft, pageHeightPoints);
    }

    /// <summary>
    /// Convert text bounding box from PDF coordinates (bottom-left) to Avalonia coordinates (top-left)
    /// in PDF points.
    ///
    /// Use when: Converting text position from ContentStreamParser for intersection with selection.
    ///
    /// Pipeline: PDF Text Coords (bottom-left) → Avalonia Coords (top-left)
    /// </summary>
    public static Rect TextBoundsToPdfPointsTopLeft(
        double textX,
        double textY,      // PDF Y = baseline position from bottom
        double textWidth,
        double textHeight,
        double pageHeight)
    {
        // Text in PDF: Y is baseline from bottom, text extends upward
        // So the top of the text is at textY + textHeight
        var pdfTop = textY + textHeight;

        // Convert to Avalonia: Y=0 at top
        var avaloniaY = pageHeight - pdfTop;

        return new Rect(textX, avaloniaY, textWidth, textHeight);
    }

    // ========================================================================
    // XGRAPHICS COORDINATE HELPERS
    // ========================================================================

    /// <summary>
    /// Convert coordinates for XGraphics drawing.
    ///
    /// VERIFIED BY VISUAL TESTING: XGraphics.FromPdfPage uses TOP-LEFT origin,
    /// same as global::Avalonia. No Y-flip is needed.
    ///
    /// Evidence: Drawing at Avalonia Y=100 with Y-flip produced black box at
    /// image pixel Y=1337 (near bottom of 1649px image). Without flip, it
    /// appears at the expected Y=208 pixels (near top).
    /// </summary>
    /// <param name="avaloniaRect">Rectangle in Avalonia coordinates (PDF points, top-left origin)</param>
    /// <param name="pageHeight">Page height in PDF points (kept for API compatibility)</param>
    /// <returns>Coordinates for XGraphics.DrawRectangle(brush, x, y, width, height)</returns>
    public static (double X, double Y, double Width, double Height) ForXGraphics(Rect avaloniaRect, double pageHeight)
    {
        // XGraphics uses TOP-LEFT origin (same as Avalonia)
        // No coordinate transformation needed
        return (avaloniaRect.X, avaloniaRect.Y, avaloniaRect.Width, avaloniaRect.Height);
    }

    /// <summary>
    /// Convert coordinates for XGraphics drawing.
    /// XGraphics uses top-left origin same as Avalonia, so no conversion needed.
    /// </summary>
    public static (double X, double Y, double Width, double Height) ForXGraphics(Rect avaloniaRect)
    {
        // XGraphics uses TOP-LEFT origin (same as Avalonia)
        return (avaloniaRect.X, avaloniaRect.Y, avaloniaRect.Width, avaloniaRect.Height);
    }

    /// <summary>
    /// Convert coordinates for XGraphics drawing with explicit coordinate system selection.
    /// Use this when you need to verify or override coordinate system behavior.
    /// </summary>
    /// <param name="avaloniaRect">Rectangle in Avalonia coordinates (PDF points, top-left origin)</param>
    /// <param name="pageHeight">Page height in PDF points</param>
    /// <param name="xGraphicsUsesTopLeft">
    /// True (default, verified by visual testing): XGraphics uses top-left origin.
    /// Set to false only if testing reveals different behavior.
    /// </param>
    public static (double X, double Y, double Width, double Height) ForXGraphicsWithVerification(
        Rect avaloniaRect,
        double pageHeight,
        bool xGraphicsUsesTopLeft = true)  // Changed to true based on visual testing
    {
        if (xGraphicsUsesTopLeft)
        {
            return (avaloniaRect.X, avaloniaRect.Y, avaloniaRect.Width, avaloniaRect.Height);
        }
        else
        {
            // If XGraphics uses bottom-left origin, flip Y
            var xGraphicsY = pageHeight - avaloniaRect.Y - avaloniaRect.Height;
            return (avaloniaRect.X, xGraphicsY, avaloniaRect.Width, avaloniaRect.Height);
        }
    }

    // ========================================================================
    // VALIDATION HELPERS
    // ========================================================================

    /// <summary>
    /// Check if coordinates appear to be in a valid range for the given page.
    /// Useful for debugging coordinate conversion issues.
    /// </summary>
    public static bool IsValidForPage(Rect rect, double pageWidth, double pageHeight, double tolerance = 50)
    {
        return rect.X >= -tolerance &&
               rect.Y >= -tolerance &&
               rect.Right <= pageWidth + tolerance &&
               rect.Bottom <= pageHeight + tolerance &&
               rect.Width > 0 &&
               rect.Height > 0;
    }

    /// <summary>
    /// Get a debug string describing the coordinate conversion.
    /// </summary>
    public static string DescribeConversion(
        Rect input,
        string inputSystem,
        Rect output,
        string outputSystem)
    {
        return $"Converted from {inputSystem} ({input.X:F2},{input.Y:F2},{input.Width:F2}x{input.Height:F2}) " +
                $"to {outputSystem} ({output.X:F2},{output.Y:F2},{output.Width:F2}x{output.Height:F2})";
    }

    // ========================================================================
    // PAGE ROTATION SUPPORT
    // ========================================================================

    /// <summary>
    /// Transform a rectangle to account for page rotation.
    /// PDF pages can have /Rotate values of 0, 90, 180, or 270 degrees.
    /// This transforms the selection area from screen coordinates to
    /// the coordinate system of the rotated page content.
    /// </summary>
    /// <param name="area">Selection area in PDF points (top-left origin)</param>
    /// <param name="rotation">Page rotation in degrees (0, 90, 180, 270)</param>
    /// <param name="pageWidth">Original page width in PDF points</param>
    /// <param name="pageHeight">Original page height in PDF points</param>
    /// <returns>Transformed rectangle in the content's coordinate system</returns>
    public static Rect TransformForRotation(Rect area, int rotation, double pageWidth, double pageHeight)
    {
        // Normalize rotation to 0, 90, 180, 270
        rotation = ((rotation % 360) + 360) % 360;

        return rotation switch
        {
            0 => area,  // No transformation needed
            90 => TransformRotation90(area, pageWidth, pageHeight),
            180 => TransformRotation180(area, pageWidth, pageHeight),
            270 => TransformRotation270(area, pageWidth, pageHeight),
            _ => area  // Invalid rotation, return unchanged
        };
    }

    /// <summary>
    /// Transform coordinates for 90-degree clockwise rotation.
    /// When page is rotated 90°, the page coordinate system is rotated.
    /// </summary>
    private static Rect TransformRotation90(Rect area, double pageWidth, double pageHeight)
    {
        // Visual → content (unrotated) mapping derived from inverting the helper used in tests:
        // visualX = pageWidth - pageY - pageH
        // visualY = pageX
        // visualW = pageH
        // visualH = pageW
        // Therefore:
        // pageX = visualY
        // pageY = pageWidth - visualX - visualW
        // pageW = visualH
        // pageH = visualW
        var newX = area.Y;
        var newY = pageWidth - area.X - area.Width;
        var newWidth = area.Height;
        var newHeight = area.Width;

        return new Rect(newX, newY, newWidth, newHeight);
    }

    /// <summary>
    /// Transform coordinates for 180-degree rotation.
    /// When page is rotated 180°, both X and Y are flipped.
    /// </summary>
    private static Rect TransformRotation180(Rect area, double pageWidth, double pageHeight)
    {
        // 180° rotation: (x, y) -> (width - x - width, height - y - height)
        var newX = pageWidth - area.X - area.Width;
        var newY = pageHeight - area.Y - area.Height;

        return new Rect(newX, newY, area.Width, area.Height);
    }

    /// <summary>
    /// Transform coordinates for 270-degree clockwise rotation.
    /// When page is rotated 270° (or -90°), coordinates transform differently.
    /// </summary>
    private static Rect TransformRotation270(Rect area, double pageWidth, double pageHeight)
    {
        // Visual → content mapping inverted from helper:
        // visualX = pageY
        // visualY = pageHeight - pageX - pageW
        // visualW = pageH
        // visualH = pageW
        // Therefore:
        // pageX = pageHeight - visualY - visualH
        // pageY = visualX
        // pageW = visualH
        // pageH = visualW
        var newX = pageHeight - area.Y - area.Height;
        var newY = area.X;
        var newWidth = area.Height;
        var newHeight = area.Width;

        return new Rect(newX, newY, newWidth, newHeight);
    }

    /// <summary>
    /// Get the effective page dimensions after rotation.
    /// For 90° and 270° rotations, width and height are swapped.
    /// </summary>
    public static (double Width, double Height) GetRotatedPageDimensions(
        double pageWidth,
        double pageHeight,
        int rotation)
    {
        rotation = ((rotation % 360) + 360) % 360;

        return rotation switch
        {
            90 or 270 => (pageHeight, pageWidth),  // Dimensions swapped
            _ => (pageWidth, pageHeight)           // 0° or 180°, dimensions unchanged
        };
    }
}
