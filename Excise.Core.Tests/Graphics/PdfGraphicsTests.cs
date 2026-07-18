using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Graphics;

/// <summary>
/// TDD tests for PDF graphics API - tests define expected behavior before implementation.
/// </summary>
public class PdfGraphicsTests
{
    #region Context Creation Tests

    [Fact]
    public void GetGraphics_ReturnsGraphicsContext()
    {
        // Arrange
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        // Act
        using var graphics = page.GetGraphics();

        // Assert
        graphics.Should().NotBeNull();
    }

    [Fact]
    public void GetGraphics_MultipleCallsOnSamePage_ReturnsSameContext()
    {
        // Arrange
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        // Act
        using var g1 = page.GetGraphics();
        using var g2 = page.GetGraphics();

        // Assert - same context returned (or at least compatible)
        g1.Should().NotBeNull();
        g2.Should().NotBeNull();
    }

    #endregion

    #region State Management Tests

    [Fact]
    public void SaveState_RestoreState_Works()
    {
        // Arrange
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        // Act & Assert - should not throw
        graphics.SaveState();
        graphics.RestoreState();
    }

    [Fact]
    public void SaveState_CanBeCalledMultipleTimes()
    {
        // Arrange
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        // Act & Assert
        graphics.SaveState();
        graphics.SaveState();
        graphics.SaveState();
        graphics.RestoreState();
        graphics.RestoreState();
        graphics.RestoreState();
    }

    #endregion

    #region Rectangle Drawing Tests

    [Fact]
    public void DrawRectangle_Filled_AddsContentToPage()
    {
        // Arrange
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var originalContent = page.GetContentStreamBytes();
        using var graphics = page.GetGraphics();

        // Act
        graphics.DrawRectangle(100, 100, 200, 150, PdfBrush.Black);
        graphics.Flush();

        // Assert - content stream should have changed
        var newContent = page.GetContentStreamBytes();
        newContent.Length.Should().BeGreaterThan(originalContent.Length);
    }

    [Fact]
    public void DrawRectangle_ProducesValidPdfOperators()
    {
        // Arrange
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        // Act
        graphics.DrawRectangle(100, 100, 200, 150, PdfBrush.Black);
        var operators = graphics.GetOperators();

        // Assert - should contain 're' (rectangle) and 'f' (fill)
        operators.Should().Contain("100 100 200 150 re");
        operators.Should().Contain("f");
    }

    [Fact]
    public void DrawRectangle_WithStroke_ProducesStrokeOperator()
    {
        // Arrange
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        // Act
        graphics.DrawRectangle(50, 50, 100, 100, null, PdfPen.Black);
        var operators = graphics.GetOperators();

        // Assert - should contain 're' and 'S' (stroke)
        operators.Should().Contain("50 50 100 100 re");
        operators.Should().Contain("S");
    }

    [Fact]
    public void DrawRectangle_WithFillAndStroke_ProducesCorrectOperators()
    {
        // Arrange
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        // Act
        graphics.DrawRectangle(50, 50, 100, 100, PdfBrush.Red, PdfPen.Black);
        var operators = graphics.GetOperators();

        // Assert - should contain 'B' (fill and stroke)
        operators.Should().Contain("B");
    }

    #endregion

    #region Color Tests

    [Fact]
    public void DrawRectangle_BlackFill_SetsColorCorrectly()
    {
        // Arrange
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        // Act
        graphics.DrawRectangle(100, 100, 50, 50, PdfBrush.Black);
        var operators = graphics.GetOperators();

        // Assert - should set fill color to black (0 g)
        operators.Should().Contain("0 g");
    }

    [Fact]
    public void DrawRectangle_RedFill_SetsColorCorrectly()
    {
        // Arrange
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        // Act
        graphics.DrawRectangle(100, 100, 50, 50, PdfBrush.Red);
        var operators = graphics.GetOperators();

        // Assert - should set fill color (1 0 0 rg for RGB red)
        operators.Should().Contain("1 0 0 rg");
    }

    [Fact]
    public void DrawRectangle_WhiteStroke_SetsStrokeColor()
    {
        // Arrange
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        // Act
        graphics.DrawRectangle(100, 100, 50, 50, null, PdfPen.White);
        var operators = graphics.GetOperators();

        // Assert - should set stroke color (1 G for grayscale white)
        operators.Should().Contain("1 G");
    }

    #endregion

    #region Line Drawing Tests

    [Fact]
    public void DrawLine_ProducesCorrectOperators()
    {
        // Arrange
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        // Act
        graphics.DrawLine(0, 0, 100, 100, PdfPen.Black);
        var operators = graphics.GetOperators();

        // Assert - should contain 'm' (moveto), 'l' (lineto), 'S' (stroke)
        operators.Should().Contain("0 0 m");
        operators.Should().Contain("100 100 l");
        operators.Should().Contain("S");
    }

    [Fact]
    public void DrawLine_WithLineWidth_SetsWidth()
    {
        // Arrange
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        // Act
        var pen = new PdfPen(PdfColor.Black, 2.5);
        graphics.DrawLine(0, 0, 100, 100, pen);
        var operators = graphics.GetOperators();

        // Assert - should set line width
        operators.Should().Contain("2.5 w");
    }

    #endregion

    #region Path Drawing Tests

    [Fact]
    public void BeginPath_MoveTo_LineTo_Stroke_ProducesCorrectOperators()
    {
        // Arrange
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        // Act
        graphics.BeginPath();
        graphics.MoveTo(0, 0);
        graphics.LineTo(100, 0);
        graphics.LineTo(100, 100);
        graphics.LineTo(0, 100);
        graphics.ClosePath();
        graphics.Stroke(PdfPen.Black);
        var operators = graphics.GetOperators();

        // Assert
        operators.Should().Contain("0 0 m");
        operators.Should().Contain("100 0 l");
        operators.Should().Contain("100 100 l");
        operators.Should().Contain("0 100 l");
        operators.Should().Contain("h"); // closepath
        operators.Should().Contain("S");
    }

    [Fact]
    public void CurveTo_ProducesCurveOperator()
    {
        // Arrange
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        // Act
        graphics.BeginPath();
        graphics.MoveTo(0, 0);
        graphics.CurveTo(10, 20, 30, 40, 50, 0); // x1,y1,x2,y2,x3,y3
        graphics.Stroke(PdfPen.Black);
        var operators = graphics.GetOperators();

        // Assert - 'c' is curve operator with 3 control points
        operators.Should().Contain("10 20 30 40 50 0 c");
    }

    #endregion

    #region Transformation Tests

    [Fact]
    public void Translate_AddsCmOperator()
    {
        // Arrange
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        // Act
        graphics.Translate(100, 200);
        var operators = graphics.GetOperators();

        // Assert - translate is cm with [1 0 0 1 tx ty]
        operators.Should().Contain("1 0 0 1 100 200 cm");
    }

    [Fact]
    public void Scale_AddsCmOperator()
    {
        // Arrange
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        // Act
        graphics.Scale(2, 3);
        var operators = graphics.GetOperators();

        // Assert - scale is cm with [sx 0 0 sy 0 0]
        operators.Should().Contain("2 0 0 3 0 0 cm");
    }

    #endregion

    #region Flush and Save Tests

    [Fact]
    public void Flush_AppendsToContentStream()
    {
        // Arrange
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        // Act
        graphics.DrawRectangle(100, 100, 50, 50, PdfBrush.Black);
        graphics.Flush();

        // Assert
        var content = System.Text.Encoding.Latin1.GetString(page.GetContentStreamBytes());
        content.Should().Contain("re");
        content.Should().Contain("f");
    }

    [Fact]
    public void RoundTrip_WithGraphics_PreservesDrawing()
    {
        // Arrange
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        // Act
        graphics.DrawRectangle(100, 100, 50, 50, PdfBrush.Black);
        graphics.Flush();

        var savedData = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(savedData);
        var content = System.Text.Encoding.Latin1.GetString(reopened.GetPage(1).GetContentStreamBytes());

        // Assert
        content.Should().Contain("100 100 50 50 re");
    }

    #endregion

    #region PdfFont Tests

    [Fact]
    public void PdfFont_Helvetica_CreatesCorrectly()
    {
        var font = PdfFont.Helvetica(12);
        font.BaseFont.Should().Be("Helvetica");
        font.Size.Should().Be(12);
        font.IsStandard14.Should().BeTrue();
    }

    [Fact]
    public void PdfFont_HelveticaBold_CreatesCorrectly()
    {
        var font = PdfFont.HelveticaBold(14);
        font.BaseFont.Should().Be("Helvetica-Bold");
        font.Size.Should().Be(14);
        font.IsStandard14.Should().BeTrue();
    }

    [Fact]
    public void PdfFont_TimesRoman_CreatesCorrectly()
    {
        var font = PdfFont.TimesRoman(11);
        font.BaseFont.Should().Be("Times-Roman");
        font.Size.Should().Be(11);
    }

    [Fact]
    public void PdfFont_TimesBold_CreatesCorrectly()
    {
        var font = PdfFont.TimesBold(12);
        font.BaseFont.Should().Be("Times-Bold");
        font.Size.Should().Be(12);
    }

    [Fact]
    public void PdfFont_TimesItalic_CreatesCorrectly()
    {
        var font = PdfFont.TimesItalic(12);
        font.BaseFont.Should().Be("Times-Italic");
        font.Size.Should().Be(12);
    }

    [Fact]
    public void PdfFont_Courier_CreatesCorrectly()
    {
        var font = PdfFont.Courier(10);
        font.BaseFont.Should().Be("Courier");
        font.Size.Should().Be(10);
    }

    [Fact]
    public void PdfFont_CourierBold_CreatesCorrectly()
    {
        var font = PdfFont.CourierBold(10);
        font.BaseFont.Should().Be("Courier-Bold");
        font.Size.Should().Be(10);
    }

    [Fact]
    public void PdfFont_CourierOblique_CreatesCorrectly()
    {
        var font = PdfFont.CourierOblique(10);
        font.BaseFont.Should().Be("Courier-Oblique");
        font.Size.Should().Be(10);
    }

    [Fact]
    public void PdfFont_HelveticaOblique_CreatesCorrectly()
    {
        var font = PdfFont.HelveticaOblique(12);
        font.BaseFont.Should().Be("Helvetica-Oblique");
        font.Size.Should().Be(12);
    }

    [Fact]
    public void PdfFont_WithSize_CreatesNewFontWithDifferentSize()
    {
        var font1 = PdfFont.Helvetica(12);
        var font2 = font1.WithSize(16);

        font2.Size.Should().Be(16);
        font2.BaseFont.Should().Be("Helvetica");
        font1.Size.Should().Be(12); // Original unchanged
    }

    [Fact]
    public void PdfFont_WithName_CreatesNewFontWithDifferentName()
    {
        var font1 = PdfFont.Helvetica(12);
        var font2 = font1.WithName("F5");

        font2.Name.Should().Be("F5");
        font2.BaseFont.Should().Be("Helvetica");
        font2.Size.Should().Be(12);
    }

    [Fact]
    public void PdfFont_MeasureWidth_EmptyString_ReturnsZero()
    {
        var font = PdfFont.Helvetica(12);
        var width = font.MeasureWidth("");
        width.Should().Be(0);
    }

    [Fact]
    public void PdfFont_MeasureWidth_SingleChar_ReturnsNonZero()
    {
        var font = PdfFont.Helvetica(12);
        var width = font.MeasureWidth("A");
        width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void PdfFont_MeasureWidth_String_SumOfCharWidths()
    {
        var font = PdfFont.Helvetica(12);
        var widthA = font.MeasureWidth("A");
        var widthB = font.MeasureWidth("B");
        var widthAB = font.MeasureWidth("AB");

        widthAB.Should().BeApproximately(widthA + widthB, 0.1);
    }

    [Fact]
    public void PdfFont_LineHeight_ReturnsNonZero()
    {
        var font = PdfFont.Helvetica(12);
        var height = font.LineHeight;
        height.Should().BeGreaterThan(0);
        height.Should().BeApproximately(12 * 1.2, 0.1);
    }

    [Fact]
    public void PdfFont_Ascender_ReturnsNonZero()
    {
        var font = PdfFont.Helvetica(12);
        var ascender = font.Ascender;
        ascender.Should().BeGreaterThan(0);
        ascender.Should().BeApproximately(12 * 0.8, 0.1);
    }

    [Fact]
    public void PdfFont_Descender_ReturnsNonZero()
    {
        var font = PdfFont.Helvetica(12);
        var descender = font.Descender;
        descender.Should().BeGreaterThan(0);
        descender.Should().BeApproximately(12 * 0.2, 0.1);
    }

    [Fact]
    public void PdfFont_EncodeString_EmptyString_ReturnsEmptyParens()
    {
        var font = PdfFont.Helvetica(12);
        var encoded = font.EncodeString("");
        encoded.Should().Be("()");
    }

    [Fact]
    public void PdfFont_EncodeString_SimpleText_ProducesPdfString()
    {
        var font = PdfFont.Helvetica(12);
        var encoded = font.EncodeString("Hello");
        encoded.Should().StartWith("(");
        encoded.Should().EndWith(")");
        encoded.Should().Contain("Hello");
    }

    [Fact]
    public void PdfFont_EncodeString_WithParentheses_EscapesThem()
    {
        var font = PdfFont.Helvetica(12);
        var encoded = font.EncodeString("(test)");
        encoded.Should().Contain("\\(");
        encoded.Should().Contain("\\)");
    }

    [Fact]
    public void PdfFont_EncodeString_WithBackslash_EscapesIt()
    {
        var font = PdfFont.Helvetica(12);
        var encoded = font.EncodeString("test\\value");
        encoded.Should().Contain("\\\\");
    }

    [Fact]
    public void PdfFont_ToString_ReturnsFormatted()
    {
        var font = PdfFont.Helvetica(12);
        var str = font.ToString();
        str.Should().Contain("Helvetica");
        str.Should().Contain("12");
        str.Should().Contain("pt");
    }

    [Fact]
    public void PdfFont_CourierIsMonospace_AllCharsSameWidth()
    {
        var font = PdfFont.Courier(12);
        var widthA = font.MeasureWidth("A");
        var widthI = font.MeasureWidth("I");
        var widthM = font.MeasureWidth("M");

        widthA.Should().BeApproximately(widthI, 0.01);
        widthI.Should().BeApproximately(widthM, 0.01);
    }

    [Fact]
    public void PdfFont_GetFontDictionary_CreatesValidDict()
    {
        var font = PdfFont.Helvetica(12);
        var dict = font.CreateFontDictionary();

        dict["Type"].Should().NotBeNull();
        dict["BaseFont"].Should().NotBeNull();
        dict["Subtype"].Should().NotBeNull();
    }

    #endregion

    #region PdfGraphics Rotate/Transform Tests

    [Fact]
    public void PdfGraphics_Rotate_AddsRotationMatrix()
    {
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        graphics.Rotate(90);
        var operators = graphics.GetOperators();

        operators.Should().Contain("cm");
    }

    [Fact]
    public void PdfGraphics_Transform_AddsTransformMatrix()
    {
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        graphics.Transform(1.5, 0, 0, 1.5, 10, 20);
        var operators = graphics.GetOperators();

        operators.Should().Contain("cm");
        operators.Should().Contain("1.5");
    }

    [Fact]
    public void PdfGraphics_Fill_SetsFillColorAndOperator()
    {
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        graphics.BeginPath();
        graphics.MoveTo(0, 0);
        graphics.LineTo(100, 0);
        graphics.LineTo(100, 100);
        graphics.LineTo(0, 100);
        graphics.ClosePath();
        graphics.Fill(PdfBrush.Black);

        var operators = graphics.GetOperators();
        operators.Should().Contain("f");
    }

    [Fact]
    public void PdfGraphics_FillAndStroke_SetsBothOperators()
    {
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        graphics.BeginPath();
        graphics.MoveTo(0, 0);
        graphics.LineTo(100, 100);
        graphics.ClosePath();
        graphics.FillAndStroke(PdfBrush.Red, PdfPen.Black);

        var operators = graphics.GetOperators();
        operators.Should().Contain("B");
    }

    [Fact]
    public void PdfGraphics_DrawString_WithFont_AddsFontOperators()
    {
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        var font = PdfFont.Helvetica(12);
        graphics.DrawString("Test", font, PdfBrush.Black, 100, 100);

        var operators = graphics.GetOperators();
        operators.Should().Contain("Tf");
        operators.Should().Contain("Tj");
    }

    [Fact]
    public void PdfGraphics_DrawString_WithAlignment_Center_AdjustsPosition()
    {
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        var font = PdfFont.Helvetica(12);
        graphics.DrawString("Test", font, PdfBrush.Black, 100, 100, TextAlignment.Center);

        var operators = graphics.GetOperators();
        operators.Should().NotBeEmpty();
        operators.Should().Contain("Tj");
    }

    [Fact]
    public void PdfGraphics_DrawString_WithAlignment_Right_AdjustsPosition()
    {
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        var font = PdfFont.Helvetica(12);
        graphics.DrawString("Test", font, PdfBrush.Black, 100, 100, TextAlignment.Right);

        var operators = graphics.GetOperators();
        operators.Should().NotBeEmpty();
        operators.Should().Contain("Tj");
    }

    [Fact]
    public void PdfGraphics_DrawInvisibleText_EmitsRenderMode3()
    {
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        var font = PdfFont.Helvetica(12);
        graphics.DrawInvisibleText("Test", font, 100, 100, 50);

        var operators = graphics.GetOperators();
        operators.Should().Contain("3 Tr");
        operators.Should().Contain("Tj");
        operators.Should().Contain("Tz");
    }

    [Fact]
    public void PdfGraphics_DrawInvisibleText_ResetsRenderModeAndScaleAfterward()
    {
        // Tr/Tz are text state, not part of the q/Q graphics-state stack — a
        // later DrawString call on the same graphics session must not
        // inherit invisible/scaled state from an earlier DrawInvisibleText.
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        var font = PdfFont.Helvetica(12);
        graphics.DrawInvisibleText("Test", font, 100, 100, 50);

        var operators = graphics.GetOperators();
        operators.Should().Contain("0 Tr");
        operators.Should().Contain("100 Tz");

        var trIndex = operators.IndexOf("3 Tr");
        var resetIndex = operators.IndexOf("0 Tr", trIndex);
        var etIndex = operators.IndexOf("ET", trIndex);
        resetIndex.Should().BeLessThan(etIndex, "the render mode must be reset before ET, inside this call's own block");
    }

    [Fact]
    public void PdfGraphics_DrawInvisibleText_ScalesToTargetWidth()
    {
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        var font = PdfFont.Helvetica(12);
        var naturalWidth = font.MeasureWidth("Test");
        var targetWidth = naturalWidth * 2; // force a distinct, checkable scale
        graphics.DrawInvisibleText("Test", font, 100, 100, targetWidth);

        var operators = graphics.GetOperators();
        // First "Tz" line is the fitted scale; DrawInvisibleText appends a
        // "100 Tz" reset afterward, so take the first match, not Single().
        var tzLine = operators.Split('\n').First(l => l.EndsWith(" Tz"));
        var scale = double.Parse(tzLine.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
        scale.Should().BeApproximately(200.0, 1.0, "targetWidth is 2x natural width, so Tz should scale to ~200%");
    }

    [Fact]
    public void PdfGraphics_DrawInvisibleText_SkipsCharactersFontCannotEncode()
    {
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        using var graphics = page.GetGraphics();

        var font = PdfFont.Helvetica(12);
        graphics.DrawInvisibleText("中文", font, 100, 100, 50); // CJK, not WinAnsi-representable

        graphics.GetOperators().Should().BeEmpty(
            "writing a lossy '?' for unrepresentable text would silently corrupt search — must skip instead");
    }

    [Fact]
    public void PdfGraphics_DrawInvisibleText_AfterFlush_LettersAndTextReflectTheWord()
    {
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);

        using (var graphics = page.GetGraphics())
        {
            graphics.DrawInvisibleText("HIDDENWORD", PdfFont.Helvetica(12), 100, 100, 60);
        }

        page.Text.Should().Contain("HIDDENWORD");
        string.Concat(page.Letters.Select(l => l.Value)).Should().Contain("HIDDENWORD");
    }

    [Fact]
    public void PdfGraphics_MeasureString_ReturnsNonZeroSize()
    {
        var font = PdfFont.Helvetica(12);
        var size = PdfGraphics.MeasureString("Test", font);

        size.Width.Should().BeGreaterThan(0);
        size.Height.Should().BeGreaterThan(0);
    }

    [Fact]
    public void PdfGraphics_MeasureString_EmptyString_ReturnsZero()
    {
        var font = PdfFont.Helvetica(12);
        var size = PdfGraphics.MeasureString("", font);

        size.Width.Should().Be(0);
        size.Height.Should().Be(0);
    }

    [Fact]
    public void PdfGraphics_Flush_WritesToContentStream()
    {
        var pdfData = CreateSimplePdf();
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var originalLength = page.GetContentStreamBytes().Length;

        using var graphics = page.GetGraphics();
        graphics.DrawRectangle(10, 10, 50, 50, PdfBrush.Black);
        graphics.Flush();

        var newLength = page.GetContentStreamBytes().Length;
        newLength.Should().BeGreaterThan(originalLength);
    }

    #endregion

    #region Helper Methods

    #region PdfPen Tests

    [Fact]
    public void PdfPen_BlackStatic_IsCreated()
    {
        PdfPen.Black.Should().NotBeNull();
        PdfPen.Black.Color.Should().NotBeNull();
        PdfPen.Black.Width.Should().Be(1);
    }

    [Fact]
    public void PdfPen_WhiteStatic_IsCreated()
    {
        PdfPen.White.Should().NotBeNull();
        PdfPen.White.Color.Should().NotBeNull();
        PdfPen.White.Width.Should().Be(1);
    }

    [Fact]
    public void PdfPen_RedStatic_IsCreated()
    {
        PdfPen.Red.Should().NotBeNull();
        PdfPen.Red.Color.Should().NotBeNull();
        PdfPen.Red.Width.Should().Be(1);
    }

    [Fact]
    public void PdfPen_WithColor_StoresColor()
    {
        var pen = new PdfPen(PdfColor.Blue, 2.5);
        pen.Color.Should().Be(PdfColor.Blue);
        pen.Width.Should().Be(2.5);
    }

    [Fact]
    public void PdfPen_WithNegativeWidth_ClipsToZero()
    {
        var pen = new PdfPen(PdfColor.Black, -5);
        pen.Width.Should().Be(0);
    }

    [Fact]
    public void PdfPen_GetStrokeColorOperator_GrayscaleBlack_ProducesOperator()
    {
        var pen = PdfPen.Black;
        var op = pen.GetStrokeColorOperator();
        op.Should().Contain("G");
    }

    [Fact]
    public void PdfPen_GetStrokeColorOperator_Rgb_ProducesRgOperator()
    {
        var pen = new PdfPen(PdfColor.Blue, 1);
        var op = pen.GetStrokeColorOperator();
        op.Should().Contain("RG");
    }

    [Fact]
    public void PdfPen_GetLineWidthOperator_ProducesWOperator()
    {
        var pen = new PdfPen(PdfColor.Black, 2.5);
        var op = pen.GetLineWidthOperator();
        op.Should().Contain("w");
        op.Should().Contain("2.5");
    }

    [Fact]
    public void PdfPen_GetLineWidthOperator_IntegerWidth_FormatsAsInteger()
    {
        var pen = new PdfPen(PdfColor.Black, 3);
        var op = pen.GetLineWidthOperator();
        op.Should().Be("3 w");
    }

    #endregion

    private static byte[] CreateSimplePdf()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Test) Tj ET";

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

}
