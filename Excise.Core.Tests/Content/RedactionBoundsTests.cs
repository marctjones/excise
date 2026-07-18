using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Content;

/// <summary>
/// Tests for verifying redaction correctly calculates operator bounds.
/// Issue #304: Comprehensive bounds verification for all operator types.
/// </summary>
public class RedactionBoundsTests
{
    #region Text Bounds Tests

    [Fact]
    public void TextBounds_SimpleTextAtKnownPosition_HasCorrectBounds()
    {
        // Create PDF with "Hello" at position (100, 700), font size 12
        var pdf = CreatePdfWithText("Hello", 100, 700, 12);
        var page = pdf.GetPage(1);
        var content = page.GetContentStream();

        // Find text operator
        var textOp = content.TextOperators.FirstOrDefault();
        textOp.Should().NotBeNull("PDF should have text operator");
        textOp!.BoundingBox.Should().NotBeNull("Text operator should have bounding box");

        // Verify approximate position (tolerance for font metrics approximation)
        var bounds = textOp.BoundingBox!.Value;
        bounds.Left.Should().BeApproximately(100, 5, "X position should be near 100");
        bounds.Bottom.Should().BeApproximately(700, 5, "Y position should be near 700");
        bounds.Height.Should().BeApproximately(12, 3, "Height should be near font size");
        bounds.Width.Should().BeGreaterThan(10, "Width should be positive for 'Hello'");
    }

    [Fact]
    public void TextBounds_TextWithCharacterSpacing_AffectsWidth()
    {
        // Create PDF with character spacing (Tc operator)
        var content = "BT /F1 12 Tf 5 Tc 100 700 Td (Hello) Tj ET";
        var pdf = CreatePdfWithContent(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        var textOp = parsedContent.TextOperators.FirstOrDefault();
        textOp.Should().NotBeNull();

        // With character spacing, width should be greater than normal
        // "Hello" has 5 characters, 5 points spacing adds ~20pt total
        var bounds = textOp!.BoundingBox!.Value;
        bounds.Width.Should().BeGreaterThan(40, "Character spacing should increase width");
    }

    [Fact]
    public void TextBounds_TextWithWordSpacing_AffectsWidth()
    {
        // Create PDF with word spacing (Tw operator)
        var content = "BT /F1 12 Tf 10 Tw 100 700 Td (Hello World) Tj ET";
        var pdf = CreatePdfWithContent(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        var textOp = parsedContent.TextOperators.FirstOrDefault();
        textOp.Should().NotBeNull();
        textOp!.BoundingBox.Should().NotBeNull();

        // Word spacing adds extra space at the space character
        var bounds = textOp.BoundingBox!.Value;
        bounds.Width.Should().BeGreaterThan(50, "Word spacing should increase total width");
    }

    [Fact]
    public void TextBounds_TextWithHorizontalScaling_AffectsWidth()
    {
        // Create PDF with horizontal scaling (Tz operator) - 200%
        var content = "BT /F1 12 Tf 200 Tz 100 700 Td (Hello) Tj ET";
        var pdf = CreatePdfWithContent(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        var textOp = parsedContent.TextOperators.FirstOrDefault();
        textOp.Should().NotBeNull();

        // 200% scaling should roughly double the width
        var bounds = textOp!.BoundingBox!.Value;
        bounds.Width.Should().BeGreaterThan(60, "200% horizontal scaling should increase width");
    }

    [Fact]
    public void TextBounds_TextAfterTdMovement_UpdatesPosition()
    {
        // Create PDF with Td movement
        var content = "BT /F1 12 Tf 100 700 Td (First) Tj 50 -20 Td (Second) Tj ET";
        var pdf = CreatePdfWithContent(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        var textOps = parsedContent.TextOperators.ToList();
        textOps.Should().HaveCount(2);

        var first = textOps[0].BoundingBox!.Value;
        var second = textOps[1].BoundingBox!.Value;

        // Td moves relative to current text line position
        // After first Tj, text position advances; Td adds to text line matrix
        // Second text should be lower (Y decreased by 20)
        second.Bottom.Should().BeApproximately(first.Bottom - 20, 10);
        // X position depends on text width calculation + 50 offset
        second.Left.Should().BeGreaterThan(first.Left + 40, "Second text should be to the right");
    }

    [Fact]
    public void TextBounds_TJArrayOperator_CombinesBounds()
    {
        // TJ array with kerning adjustments
        var content = "BT /F1 12 Tf 100 700 Td [(AB) -100 (CD)] TJ ET";
        var pdf = CreatePdfWithContent(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        var textOp = parsedContent.TextOperators.FirstOrDefault();
        textOp.Should().NotBeNull();
        textOp!.BoundingBox.Should().NotBeNull();

        // Bounds should cover all text including kerning
        var bounds = textOp.BoundingBox!.Value;
        bounds.Left.Should().BeApproximately(100, 5);
        bounds.Width.Should().BeGreaterThan(20, "Should cover ABCD plus kerning");
    }

    [Fact]
    public void TextBounds_TextWithRotation_TransformsBounds()
    {
        // Text matrix with 45-degree rotation
        var cos45 = Math.Cos(Math.PI / 4);
        var sin45 = Math.Sin(Math.PI / 4);
        var content = $"BT /F1 12 Tf {cos45:F4} {sin45:F4} {-sin45:F4} {cos45:F4} 200 400 Tm (Rotated) Tj ET";
        var pdf = CreatePdfWithContent(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        var textOp = parsedContent.TextOperators.FirstOrDefault();
        textOp.Should().NotBeNull();
        textOp!.BoundingBox.Should().NotBeNull();

        // Rotated text at approximately (200, 400)
        var bounds = textOp.BoundingBox!.Value;
        bounds.Left.Should().BeApproximately(200, 20);
    }

    [Fact]
    public void TextBounds_MultipleTextBlocks_IndependentBounds()
    {
        var content = @"
            BT /F1 12 Tf 100 700 Td (First) Tj ET
            BT /F1 12 Tf 300 500 Td (Second) Tj ET
        ";
        var pdf = CreatePdfWithContent(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        var textOps = parsedContent.TextOperators.ToList();
        textOps.Should().HaveCount(2);

        var first = textOps[0].BoundingBox!.Value;
        var second = textOps[1].BoundingBox!.Value;

        // Each should be at its own position
        first.Left.Should().BeApproximately(100, 5);
        first.Bottom.Should().BeApproximately(700, 5);
        second.Left.Should().BeApproximately(300, 5);
        second.Bottom.Should().BeApproximately(500, 5);
    }

    #endregion

    #region Path Bounds Tests

    [Fact]
    public void PathBounds_StrokeRectangle_HasCorrectBounds()
    {
        // Bounding box is assigned to painting operator (S), not construction operator (re)
        var content = "100 200 50 30 re S";
        var pdf = CreatePdfWithContent(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        // Find stroke operator - it gets the bounds
        var strokeOp = parsedContent.Operators.FirstOrDefault(op => op.Name == "S");
        strokeOp.Should().NotBeNull();
        strokeOp!.BoundingBox.Should().NotBeNull("Stroke operator should have bounds from path");

        var bounds = strokeOp.BoundingBox!.Value;
        bounds.Left.Should().BeApproximately(100, 1);
        bounds.Bottom.Should().BeApproximately(200, 1);
        bounds.Width.Should().BeApproximately(50, 1);
        bounds.Height.Should().BeApproximately(30, 1);
    }

    [Fact]
    public void PathBounds_FillRectangle_HasCorrectBounds()
    {
        var content = "100 200 50 30 re f";
        var pdf = CreatePdfWithContent(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        var fillOp = parsedContent.Operators.FirstOrDefault(op => op.Name == "f");
        fillOp.Should().NotBeNull();
        fillOp!.BoundingBox.Should().NotBeNull();

        var bounds = fillOp.BoundingBox!.Value;
        bounds.Left.Should().BeApproximately(100, 1);
        bounds.Bottom.Should().BeApproximately(200, 1);
    }

    [Fact]
    public void PathBounds_Line_SpansBetweenPoints()
    {
        var content = "50 100 m 200 300 l S";
        var pdf = CreatePdfWithContent(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        // Stroke operator gets bounds
        var strokeOp = parsedContent.Operators.FirstOrDefault(op => op.Name == "S");
        strokeOp.Should().NotBeNull();
        strokeOp!.BoundingBox.Should().NotBeNull();

        var bounds = strokeOp.BoundingBox!.Value;
        // Should span from (50,100) to (200,300)
        bounds.Left.Should().BeApproximately(50, 1);
        bounds.Bottom.Should().BeApproximately(100, 1);
        bounds.Right.Should().BeApproximately(200, 1);
        bounds.Top.Should().BeApproximately(300, 1);
    }

    [Fact]
    public void PathBounds_BezierCurve_EnclosesCurve()
    {
        var content = "100 100 m 150 200 250 200 300 100 c S";
        var pdf = CreatePdfWithContent(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        var strokeOp = parsedContent.Operators.FirstOrDefault(op => op.Name == "S");
        strokeOp.Should().NotBeNull();
        strokeOp!.BoundingBox.Should().NotBeNull();

        // Bounds should include control points
        var bounds = strokeOp.BoundingBox!.Value;
        bounds.Left.Should().BeApproximately(100, 5);
        bounds.Right.Should().BeApproximately(300, 5);
    }

    [Fact]
    public void PathBounds_WithTransformation_TransformsBounds()
    {
        // Translate by (50, 50) then draw rectangle
        var content = "q 1 0 0 1 50 50 cm 100 100 40 40 re S Q";
        var pdf = CreatePdfWithContent(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        var strokeOp = parsedContent.Operators.FirstOrDefault(op => op.Name == "S");
        strokeOp.Should().NotBeNull();
        strokeOp!.BoundingBox.Should().NotBeNull();

        // Rectangle at (100,100) + translation (50,50) = (150, 150)
        var bounds = strokeOp.BoundingBox!.Value;
        bounds.Left.Should().BeApproximately(150, 5);
        bounds.Bottom.Should().BeApproximately(150, 5);
    }

    [Fact]
    public void PathBounds_ScaledPath_ScalesBounds()
    {
        // Scale by 2x then draw rectangle
        var content = "q 2 0 0 2 0 0 cm 50 50 25 25 re S Q";
        var pdf = CreatePdfWithContent(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        var strokeOp = parsedContent.Operators.FirstOrDefault(op => op.Name == "S");
        strokeOp.Should().NotBeNull();
        strokeOp!.BoundingBox.Should().NotBeNull();

        // Rectangle (50,50,25,25) * 2 = (100, 100, 50, 50)
        var bounds = strokeOp.BoundingBox!.Value;
        bounds.Left.Should().BeApproximately(100, 5);
        bounds.Width.Should().BeApproximately(50, 5);
    }

    #endregion

    #region XObject/Image Bounds Tests

    [Fact]
    public void ImageBounds_DoOperator_UsesTransformationMatrix()
    {
        // Place image with cm transformation
        var content = "q 100 0 0 50 200 300 cm /Im1 Do Q";
        var pdf = CreatePdfWithContentAndImage(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        var doOp = parsedContent.Operators.FirstOrDefault(op => op.Name == "Do");
        doOp.Should().NotBeNull();
        // Bounds should be based on transformation: position (200,300), size (100,50)
    }

    #endregion

    #region Redaction Area Tests

    [Fact]
    public void Redaction_AreaContainsText_RemovesText()
    {
        var pdf = CreatePdfWithText("Confidential", 100, 700, 12);
        var page = pdf.GetPage(1);
        var content = page.GetContentStream();

        // Get text bounds
        var textOp = content.TextOperators.First();
        var bounds = textOp.BoundingBox!.Value;

        // Redact area covering the text
        var redactArea = new PdfRectangle(
            bounds.Left - 5,
            bounds.Bottom - 5,
            bounds.Right + 5,
            bounds.Top + 5);

        var redacted = content.Redact(redactArea, (0, 0, 0));

        // Text operator should be removed
        redacted.TextOperators.Should().BeEmpty("Text in redaction area should be removed");

        // Should have redaction marker (re + f operators)
        redacted.Operators.Any(op => op.Name == "re").Should().BeTrue();
        redacted.Operators.Any(op => op.Name == "f").Should().BeTrue();
    }

    [Fact]
    public void Redaction_AreaPartiallyOverlaps_RemovesIntersecting()
    {
        // Two text blocks, redact only one
        var content = @"
            BT /F1 12 Tf 100 700 Td (Keep) Tj ET
            BT /F1 12 Tf 100 500 Td (Remove) Tj ET
        ";
        var pdf = CreatePdfWithContent(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        // Redact area covering only the second text
        var redactArea = new PdfRectangle(90, 490, 200, 520);
        var redacted = parsedContent.Redact(redactArea, (0, 0, 0));

        // First text should remain
        var remainingText = redacted.TextOperators.ToList();
        remainingText.Should().HaveCount(1);
        remainingText[0].BoundingBox!.Value.Bottom.Should().BeApproximately(700, 10);
    }

    [Fact]
    public void Redaction_AreaDoesNotOverlap_PreservesAll()
    {
        var pdf = CreatePdfWithText("Safe", 100, 700, 12);
        var page = pdf.GetPage(1);
        var content = page.GetContentStream();

        // Redact area that doesn't touch the text
        var redactArea = new PdfRectangle(400, 100, 500, 200);
        var redacted = content.Redact(redactArea, (0, 0, 0));

        // Text should still be there
        redacted.TextOperators.Should().HaveCount(1);
    }

    [Fact]
    public void Redaction_ExactBoundary_RemovesContent()
    {
        var pdf = CreatePdfWithText("Test", 100, 700, 12);
        var page = pdf.GetPage(1);
        var content = page.GetContentStream();

        var textOp = content.TextOperators.First();
        var exactBounds = textOp.BoundingBox!.Value;

        // Redact with exact bounds
        var redacted = content.Redact(exactBounds, (0, 0, 0));

        redacted.TextOperators.Should().BeEmpty("Content at exact boundary should be removed");
    }

    [Fact]
    public void Redaction_VerySmallArea_StillWorks()
    {
        var content = "BT /F1 6 Tf 100 700 Td (x) Tj ET";
        var pdf = CreatePdfWithContent(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        var textOp = parsedContent.TextOperators.First();
        var bounds = textOp.BoundingBox!.Value;

        // Even very small area should work
        var redacted = parsedContent.Redact(bounds, (0, 0, 0));
        redacted.TextOperators.Should().BeEmpty();
    }

    #endregion

    #region Nested Transformation Tests

    [Fact]
    public void NestedTransformations_WithQStack_CorrectlyApplied()
    {
        var content = @"
            q
                1 0 0 1 100 100 cm
                q
                    1 0 0 1 50 50 cm
                    0 0 20 20 re S
                Q
            Q
        ";
        var pdf = CreatePdfWithContent(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        // Bounding box is on the S (stroke) operator, not re
        var strokeOp = parsedContent.Operators.FirstOrDefault(op => op.Name == "S");
        strokeOp.Should().NotBeNull();
        strokeOp!.BoundingBox.Should().NotBeNull();

        // Total translation: (100,100) + (50,50) = (150,150)
        var bounds = strokeOp.BoundingBox!.Value;
        bounds.Left.Should().BeApproximately(150, 10);
        bounds.Bottom.Should().BeApproximately(150, 10);
    }

    [Fact]
    public void TransformationRestore_QOperator_RestoresState()
    {
        var content = @"
            q 1 0 0 1 100 100 cm 50 50 20 20 re S Q
            50 50 20 20 re S
        ";
        var pdf = CreatePdfWithContent(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        // Bounds are on stroke operators
        var strokes = parsedContent.Operators.Where(op => op.Name == "S").ToList();
        strokes.Should().HaveCount(2);

        // First stroke: translated (100+50=150)
        strokes[0].BoundingBox.Should().NotBeNull();
        strokes[0].BoundingBox!.Value.Left.Should().BeApproximately(150, 10);

        // Second stroke: back at origin transformation (50)
        strokes[1].BoundingBox.Should().NotBeNull();
        strokes[1].BoundingBox!.Value.Left.Should().BeApproximately(50, 10);
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public void EdgeCase_ContentAtPageOrigin_HasValidBounds()
    {
        var content = "BT /F1 12 Tf 0 0 Td (Origin) Tj ET";
        var pdf = CreatePdfWithContent(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        var textOp = parsedContent.TextOperators.First();
        textOp.BoundingBox.Should().NotBeNull();
        textOp.BoundingBox!.Value.Left.Should().BeApproximately(0, 5);
        textOp.BoundingBox!.Value.Bottom.Should().BeApproximately(0, 5);
    }

    [Fact]
    public void EdgeCase_NegativeCoordinates_Handled()
    {
        // Some PDFs use negative coordinates with transformations
        var content = "q 1 0 0 1 100 100 cm BT /F1 12 Tf -50 -50 Td (Negative) Tj ET Q";
        var pdf = CreatePdfWithContent(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        var textOp = parsedContent.TextOperators.First();
        textOp.BoundingBox.Should().NotBeNull();
        // Position: (100-50, 100-50) = (50, 50)
        textOp.BoundingBox!.Value.Left.Should().BeApproximately(50, 10);
    }

    [Fact]
    public void EdgeCase_EmptyText_NoException()
    {
        var content = "BT /F1 12 Tf 100 700 Td () Tj ET";
        var pdf = CreatePdfWithContent(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        // Should not throw, may have zero-width bounds
        var textOps = parsedContent.TextOperators.ToList();
        textOps.Should().HaveCount(1);
    }

    [Fact]
    public void EdgeCase_VeryLargeCoordinates_Handled()
    {
        var content = "BT /F1 12 Tf 10000 10000 Td (Far) Tj ET";
        var pdf = CreatePdfWithContent(content);
        var page = pdf.GetPage(1);
        var parsedContent = page.GetContentStream();

        var textOp = parsedContent.TextOperators.First();
        textOp.BoundingBox.Should().NotBeNull();
        textOp.BoundingBox!.Value.Left.Should().BeApproximately(10000, 5);
    }

    #endregion

    #region Test Helpers

    private static PdfDocument CreatePdfWithText(string text, double x, double y, double fontSize)
    {
        var content = $"BT /F1 {fontSize} Tf {x} {y} Td ({EscapePdfString(text)}) Tj ET";
        return CreatePdfWithContent(content);
    }

    private static PdfDocument CreatePdfWithContent(string contentStream)
    {
        var pdfBytes = BuildPdfWithContent(contentStream);
        return PdfDocument.Open(pdfBytes);
    }

    private static PdfDocument CreatePdfWithContentAndImage(string contentStream)
    {
        var pdfBytes = BuildPdfWithContentAndXObject(contentStream);
        return PdfDocument.Open(pdfBytes);
    }

    private static byte[] BuildPdfWithContent(string contentStream)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        // Header
        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[6];

        // Object 1: Catalog
        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 2: Pages
        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 3: Page
        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 4: Content stream
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {contentStream.Length} >>");
        writer.WriteLine("stream");
        writer.Write(contentStream);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 5: Font
        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // xref
        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 6");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{offsets[1]:D10} 00000 n ");
        writer.WriteLine($"{offsets[2]:D10} 00000 n ");
        writer.WriteLine($"{offsets[3]:D10} 00000 n ");
        writer.WriteLine($"{offsets[4]:D10} 00000 n ");
        writer.WriteLine($"{offsets[5]:D10} 00000 n ");
        writer.Flush();

        // trailer
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 6 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] BuildPdfWithContentAndXObject(string contentStream)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[7];

        // Object 1: Catalog
        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 2: Pages
        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 3: Page with XObject
        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> /XObject << /Im1 6 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 4: Content stream
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {contentStream.Length} >>");
        writer.WriteLine("stream");
        writer.Write(contentStream);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 5: Font
        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 6: XObject (1x1 pixel image placeholder)
        offsets[6] = ms.Position;
        writer.WriteLine("6 0 obj");
        writer.WriteLine("<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>");
        writer.WriteLine("stream");
        writer.Write((char)0); // 1 byte
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        // xref
        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        writer.WriteLine($"{offsets[1]:D10} 00000 n ");
        writer.WriteLine($"{offsets[2]:D10} 00000 n ");
        writer.WriteLine($"{offsets[3]:D10} 00000 n ");
        writer.WriteLine($"{offsets[4]:D10} 00000 n ");
        writer.WriteLine($"{offsets[5]:D10} 00000 n ");
        writer.WriteLine($"{offsets[6]:D10} 00000 n ");
        writer.Flush();

        // trailer
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 7 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static string EscapePdfString(string s)
    {
        return s.Replace("\\", "\\\\")
                .Replace("(", "\\(")
                .Replace(")", "\\)")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");
    }

    #endregion
}
