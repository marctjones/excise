using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Xunit;

namespace Excise.Core.Tests.Content.Operators;

/// <summary>
/// Tests for text operators.
/// Text Objects: BT, ET (Table 105, Section 9.4)
/// Text State: Tc, Tw, Tz, TL, Tf, Tr, Ts (Table 103, Section 9.3)
/// Text Positioning: Td, TD, Tm, T* (Table 106, Section 9.4.2)
/// Text Showing: Tj, TJ, ', " (Table 107, Section 9.4.3)
/// </summary>
public class TextOperatorTests
{
    #region BT/ET - Text Object Delimiters

    [Fact]
    public void Parse_BT_BeginText()
    {
        var content = "BT ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().HaveCount(2);
        result.Operators[0].Name.Should().Be("BT");
        result.Operators[0].Operands.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ET_EndText()
    {
        var content = "BT ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators[1].Name.Should().Be("ET");
        result.Operators[1].Operands.Should().BeEmpty();
    }

    [Fact]
    public void Parse_BT_ET_MultipleBlocks()
    {
        var content = @"
            BT
            /F1 12 Tf
            100 700 Td
            (First) Tj
            ET
            BT
            /F1 12 Tf
            100 680 Td
            (Second) Tj
            ET
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Where(op => op.Name == "BT").Should().HaveCount(2);
        result.Operators.Where(op => op.Name == "ET").Should().HaveCount(2);
    }

    #endregion

    #region Tf - Set Font and Size

    [Fact]
    public void Parse_Tf_SetFont()
    {
        var content = "BT /F1 12 Tf ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tfOp = result.Operators.First(op => op.Name == "Tf");
        tfOp.Operands.Should().HaveCount(2);
        tfOp.GetName(0).Should().Be("F1");
        tfOp.GetNumber(1).Should().Be(12);
    }

    [Fact]
    public void Parse_Tf_LargeFontSize()
    {
        var content = "BT /Helvetica 72 Tf ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tfOp = result.Operators.First(op => op.Name == "Tf");
        tfOp.GetNumber(1).Should().Be(72);
    }

    [Fact]
    public void Parse_Tf_SmallFontSize()
    {
        var content = "BT /F1 6.5 Tf ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tfOp = result.Operators.First(op => op.Name == "Tf");
        tfOp.GetNumber(1).Should().BeApproximately(6.5, 0.01);
    }

    [Fact]
    public void Parse_Tf_MultipleFontChanges()
    {
        var content = @"
            BT
            /F1 12 Tf
            (Normal) Tj
            /F2 12 Tf
            (Bold) Tj
            ET
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Where(op => op.Name == "Tf").Should().HaveCount(2);
    }

    #endregion

    #region Tc - Character Spacing

    [Fact]
    public void Parse_Tc_CharacterSpacing()
    {
        var content = "BT /F1 12 Tf 2 Tc (Spaced) Tj ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tcOp = result.Operators.First(op => op.Name == "Tc");
        tcOp.GetNumber(0).Should().Be(2);
    }

    [Fact]
    public void Parse_Tc_NegativeSpacing()
    {
        var content = "BT /F1 12 Tf -0.5 Tc (Condensed) Tj ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tcOp = result.Operators.First(op => op.Name == "Tc");
        tcOp.GetNumber(0).Should().BeApproximately(-0.5, 0.01);
    }

    #endregion

    #region Tw - Word Spacing

    [Fact]
    public void Parse_Tw_WordSpacing()
    {
        var content = "BT /F1 12 Tf 5 Tw (Word spacing test) Tj ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var twOp = result.Operators.First(op => op.Name == "Tw");
        twOp.GetNumber(0).Should().Be(5);
    }

    #endregion

    #region Tz - Horizontal Scaling

    [Fact]
    public void Parse_Tz_HorizontalScaling()
    {
        var content = "BT /F1 12 Tf 150 Tz (Wide) Tj ET";  // 150% width
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tzOp = result.Operators.First(op => op.Name == "Tz");
        tzOp.GetNumber(0).Should().Be(150);
    }

    [Fact]
    public void Parse_Tz_Condensed()
    {
        var content = "BT /F1 12 Tf 75 Tz (Narrow) Tj ET";  // 75% width
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tzOp = result.Operators.First(op => op.Name == "Tz");
        tzOp.GetNumber(0).Should().Be(75);
    }

    #endregion

    #region TL - Text Leading

    [Fact]
    public void Parse_TL_TextLeading()
    {
        var content = "BT /F1 12 Tf 14 TL (Line 1) ' (Line 2) ' ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tlOp = result.Operators.First(op => op.Name == "TL");
        tlOp.GetNumber(0).Should().Be(14);
    }

    #endregion

    #region Tr - Text Rendering Mode

    [Theory]
    [InlineData(0, "Fill")]
    [InlineData(1, "Stroke")]
    [InlineData(2, "Fill then stroke")]
    [InlineData(3, "Invisible")]
    [InlineData(4, "Fill and add to path")]
    [InlineData(5, "Stroke and add to path")]
    [InlineData(6, "Fill, stroke, add to path")]
    [InlineData(7, "Add to path")]
    public void Parse_Tr_RenderingMode(int mode, string description)
    {
        var content = $"BT /F1 12 Tf {mode} Tr (Text) Tj ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var trOp = result.Operators.First(op => op.Name == "Tr");
        trOp.GetNumber(0).Should().Be(mode, because: description);
    }

    [Fact]
    public void Parse_Tr_InvisibleMode3_BoundingBoxStillGenerated()
    {
        // Render mode 3 = invisible text, but bounding boxes must still be
        // computed — text extraction and redaction need glyph positions
        // regardless of whether the text is visually rendered.
        var content = "BT /F1 12 Tf 100 100 Td 3 Tr (hidden) Tj ET";
        var result = new ContentStreamParser(Encoding.UTF8.GetBytes(content)).Parse();

        var tj = result.Operators.Single(op => op.Name == "Tj");
        tj.BoundingBox.Should().NotBeNull(
            "invisible text (Tr=3) still requires a bounding box for extraction/redaction");
    }

    #endregion

    #region Ts - Text Rise

    [Fact]
    public void Parse_Ts_TextRise()
    {
        var content = "BT /F1 12 Tf 5 Ts (Superscript) Tj 0 Ts (Normal) Tj ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tsOps = result.Operators.Where(op => op.Name == "Ts").ToList();
        tsOps.Should().HaveCount(2);
        tsOps[0].GetNumber(0).Should().Be(5);
        tsOps[1].GetNumber(0).Should().Be(0);
    }

    [Fact]
    public void Parse_Ts_NegativeRise()
    {
        var content = "BT /F1 12 Tf -3 Ts (Subscript) Tj ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tsOp = result.Operators.First(op => op.Name == "Ts");
        tsOp.GetNumber(0).Should().Be(-3);
    }

    [Fact]
    public void Parse_Ts_PositiveRise_OffsetsBoundingBoxUp()
    {
        // Baseline text at (100, 100); superscript with Ts=5 should have
        // a higher Y-origin than the baseline text.
        var baseline  = "BT /F1 12 Tf 100 100 Td (A) Tj ET";
        var risen     = "BT /F1 12 Tf 100 100 Td 5 Ts (A) Tj ET";

        var baseResult  = new ContentStreamParser(Encoding.UTF8.GetBytes(baseline)).Parse();
        var risenResult = new ContentStreamParser(Encoding.UTF8.GetBytes(risen)).Parse();

        var baseTj  = baseResult.Operators.First(op => op.Name == "Tj");
        var risenTj = risenResult.Operators.First(op => op.Name == "Tj");

        baseTj.BoundingBox.Should().NotBeNull();
        risenTj.BoundingBox.Should().NotBeNull();

        risenTj.BoundingBox!.Value.Bottom.Should()
            .BeGreaterThan(baseTj.BoundingBox!.Value.Bottom,
                "text rise of +5 should shift the glyph baseline upward");
    }

    #endregion

    #region Td - Text Position

    [Fact]
    public void Parse_Td_MoveTextPosition()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Hello) Tj ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tdOp = result.Operators.First(op => op.Name == "Td");
        tdOp.Operands.Should().HaveCount(2);
        tdOp.GetNumber(0).Should().Be(100);  // tx
        tdOp.GetNumber(1).Should().Be(700);  // ty
    }

    [Fact]
    public void Parse_Td_MultipleMoves()
    {
        var content = @"
            BT
            /F1 12 Tf
            100 700 Td (Line 1) Tj
            0 -14 Td (Line 2) Tj
            0 -14 Td (Line 3) Tj
            ET
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Where(op => op.Name == "Td").Should().HaveCount(3);
    }

    #endregion

    #region TD - Move and Set Leading

    [Fact]
    public void Parse_TD_MoveAndSetLeading()
    {
        // TD is equivalent to: -ty TL tx ty Td
        var content = "BT /F1 12 Tf 100 700 TD (Hello) Tj ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tdOp = result.Operators.First(op => op.Name == "TD");
        tdOp.GetNumber(0).Should().Be(100);
        tdOp.GetNumber(1).Should().Be(700);
    }

    #endregion

    #region Tm - Text Matrix

    [Fact]
    public void Parse_Tm_SetTextMatrix()
    {
        var content = "BT /F1 12 Tf 1 0 0 1 100 700 Tm (Hello) Tj ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tmOp = result.Operators.First(op => op.Name == "Tm");
        tmOp.Operands.Should().HaveCount(6);
        tmOp.GetNumber(0).Should().Be(1);   // a
        tmOp.GetNumber(1).Should().Be(0);   // b
        tmOp.GetNumber(2).Should().Be(0);   // c
        tmOp.GetNumber(3).Should().Be(1);   // d
        tmOp.GetNumber(4).Should().Be(100); // e
        tmOp.GetNumber(5).Should().Be(700); // f
    }

    [Fact]
    public void Parse_Tm_RotatedText()
    {
        // 45 degree rotation
        var cos45 = 0.707;
        var sin45 = 0.707;
        var content = $"BT /F1 12 Tf {cos45} {sin45} {-sin45} {cos45} 100 100 Tm (Rotated) Tj ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tmOp = result.Operators.First(op => op.Name == "Tm");
        tmOp.GetNumber(0).Should().BeApproximately(0.707, 0.001);
        tmOp.GetNumber(1).Should().BeApproximately(0.707, 0.001);
    }

    [Fact]
    public void Parse_Tm_ScaledText()
    {
        // 2x horizontal scale
        var content = "BT /F1 12 Tf 2 0 0 1 100 700 Tm (Wide) Tj ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tmOp = result.Operators.First(op => op.Name == "Tm");
        tmOp.GetNumber(0).Should().Be(2);  // Horizontal scale
        tmOp.GetNumber(3).Should().Be(1);  // Vertical scale
    }

    #endregion

    #region T* - Move to Next Line

    [Fact]
    public void Parse_TStar_MoveToNextLine()
    {
        var content = @"
            BT
            /F1 12 Tf
            14 TL
            100 700 Td
            (Line 1) Tj
            T*
            (Line 2) Tj
            ET
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().Contain(op => op.Name == "T*");
        result.Operators.First(op => op.Name == "T*").Operands.Should().BeEmpty();
    }

    #endregion

    #region Tj - Show Text

    [Fact]
    public void Parse_Tj_ShowText()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Hello World) Tj ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tjOp = result.Operators.First(op => op.Name == "Tj");
        tjOp.TextContent.Should().Be("Hello World");
    }

    [Fact]
    public void Parse_Tj_ExtractsText()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Test String) Tj ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tjOp = result.Operators.First(op => op.Name == "Tj");
        tjOp.TextContent.Should().Be("Test String");
    }

    [Fact]
    public void Parse_Tj_EmptyString()
    {
        var content = "BT /F1 12 Tf 100 700 Td () Tj ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tjOp = result.Operators.First(op => op.Name == "Tj");
        tjOp.TextContent.Should().BeEmpty();
    }

    [Fact]
    public void Parse_Tj_EscapedParentheses()
    {
        var content = @"BT /F1 12 Tf 100 700 Td (Hello \(World\)) Tj ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tjOp = result.Operators.First(op => op.Name == "Tj");
        tjOp.TextContent.Should().Be("Hello (World)");
    }

    [Fact]
    public void Parse_Tj_EscapedBackslash()
    {
        var content = @"BT /F1 12 Tf 100 700 Td (C:\\path\\file) Tj ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tjOp = result.Operators.First(op => op.Name == "Tj");
        tjOp.TextContent.Should().Be(@"C:\path\file");
    }

    [Fact]
    public void Parse_Tj_OctalEscape()
    {
        // \101 = 'A' (octal 101 = decimal 65)
        var content = @"BT /F1 12 Tf 100 700 Td (\101BC) Tj ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tjOp = result.Operators.First(op => op.Name == "Tj");
        tjOp.TextContent.Should().Be("ABC");
    }

    [Fact]
    public void Parse_Tj_SpecialCharacters()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Line1\\nLine2\\tTabbed) Tj ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tjOp = result.Operators.First(op => op.Name == "Tj");
        tjOp.TextContent.Should().Contain("\n");
        tjOp.TextContent.Should().Contain("\t");
    }

    [Fact]
    public void Parse_Tj_HexString()
    {
        var content = "BT /F1 12 Tf 100 700 Td <48656C6C6F> Tj ET";  // "Hello" in hex
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tjOp = result.Operators.First(op => op.Name == "Tj");
        tjOp.TextContent.Should().Be("Hello");
    }

    [Fact]
    public void Parse_Tj_CalculatesBounds()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Hello) Tj ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tjOp = result.Operators.First(op => op.Name == "Tj");
        tjOp.BoundingBox.Should().NotBeNull();
        tjOp.BoundingBox!.Value.Left.Should().BeApproximately(100, 10);
    }

    #endregion

    #region TJ - Show Text with Positioning

    [Fact]
    public void Parse_TJ_SimpleArray()
    {
        var content = "BT /F1 12 Tf 100 700 Td [(Hello) (World)] TJ ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tjOp = result.Operators.First(op => op.Name == "TJ");
        tjOp.TextContent.Should().Be("HelloWorld");
    }

    [Fact]
    public void Parse_TJ_WithPositioning()
    {
        // Negative numbers move right, positive move left (in 1/1000 em)
        var content = "BT /F1 12 Tf 100 700 Td [(H) -100 (ello)] TJ ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tjOp = result.Operators.First(op => op.Name == "TJ");
        tjOp.TextContent.Should().Be("Hello");
    }

    [Fact]
    public void Parse_TJ_KerningSimulation()
    {
        // Simulating kerning with TJ positioning
        var content = "BT /F1 12 Tf 100 700 Td [(A) -50 (V) -50 (A)] TJ ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tjOp = result.Operators.First(op => op.Name == "TJ");
        tjOp.TextContent.Should().Be("AVA");
    }

    [Fact]
    public void Parse_TJ_LargeGaps()
    {
        // Large negative = word spacing simulation
        var content = "BT /F1 12 Tf 100 700 Td [(Hello) -500 (World)] TJ ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tjOp = result.Operators.First(op => op.Name == "TJ");
        tjOp.TextContent.Should().Be("HelloWorld");
    }

    [Fact]
    public void Parse_TJ_FloatingPointPositioning()
    {
        var content = "BT /F1 12 Tf 100 700 Td [(Te) -55.5 (st)] TJ ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tjOp = result.Operators.First(op => op.Name == "TJ");
        tjOp.TextContent.Should().Be("Test");
    }

    [Fact]
    public void Parse_TJ_MixedContent()
    {
        var content = "BT /F1 12 Tf 100 700 Td [(A) 10 (B) 20 (C)] TJ ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tjOp = result.Operators.First(op => op.Name == "TJ");
        tjOp.TextContent.Should().Be("ABC");
    }

    #endregion

    #region ' - Move and Show Text

    [Fact]
    public void Parse_SingleQuote_MoveAndShow()
    {
        // ' is equivalent to: T* (string) Tj
        var content = "BT /F1 12 Tf 14 TL 100 700 Td (Line 1) Tj (Line 2) ' ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var quoteOp = result.Operators.First(op => op.Name == "'");
        quoteOp.TextContent.Should().Be("Line 2");
    }

    #endregion

    #region " - Set Spacing, Move, and Show

    [Fact]
    public void Parse_DoubleQuote_SetSpacingAndShow()
    {
        // " is equivalent to: aw Tw ac Tc (string) '
        var content = "BT /F1 12 Tf 14 TL 100 700 Td 2 1 (Text) \" ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var quoteOp = result.Operators.First(op => op.Name == "\"");
        quoteOp.Operands.Should().HaveCount(3);  // aw, ac, string
        quoteOp.TextContent.Should().Be("Text");
    }

    #endregion

    #region Complex Text Sequences

    [Fact]
    public void Parse_MultilineText()
    {
        var content = @"
            BT
            /F1 12 Tf
            14 TL
            72 720 Td
            (This is line 1.) Tj
            T*
            (This is line 2.) Tj
            T*
            (This is line 3.) Tj
            ET
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var textOps = result.Operators.Where(op => op.Name == "Tj").ToList();
        textOps.Should().HaveCount(3);
        textOps[0].TextContent.Should().Be("This is line 1.");
        textOps[1].TextContent.Should().Be("This is line 2.");
        textOps[2].TextContent.Should().Be("This is line 3.");
    }

    [Fact]
    public void Parse_FormattedText()
    {
        var content = @"
            BT
            /F1 12 Tf
            72 720 Td
            (Normal text) Tj
            /F2 12 Tf
            ( Bold text) Tj
            /F1 12 Tf
            ( Normal again) Tj
            ET
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Where(op => op.Name == "Tf").Should().HaveCount(3);
        result.Operators.Where(op => op.Name == "Tj").Should().HaveCount(3);
    }

    [Fact]
    public void Parse_TextWithGraphicsState()
    {
        var content = @"
            q
            1 0 0 1 50 50 cm
            BT
            /F1 12 Tf
            100 700 Td
            (Transformed text) Tj
            ET
            Q
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators[0].Name.Should().Be("q");
        result.Operators.Last().Name.Should().Be("Q");
        result.Operators.Should().Contain(op => op.Name == "BT");
    }

    #endregion

    #region Bounds Calculation for Text

    [Fact]
    public void Bounds_Tj_ReflectsPosition()
    {
        var content = "BT /F1 12 Tf 100 500 Td (Test) Tj ET";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var tjOp = result.Operators.First(op => op.Name == "Tj");
        tjOp.BoundingBox.Should().NotBeNull();
        // X should start near 100
        tjOp.BoundingBox!.Value.Left.Should().BeGreaterThanOrEqualTo(90);
    }

    [Fact]
    public void Bounds_Tj_WidthDependsOnText()
    {
        var content1 = "BT /F1 12 Tf 100 500 Td (Hi) Tj ET";
        var content2 = "BT /F1 12 Tf 100 500 Td (Hello World) Tj ET";

        var result1 = new ContentStreamParser(Encoding.UTF8.GetBytes(content1)).Parse();
        var result2 = new ContentStreamParser(Encoding.UTF8.GetBytes(content2)).Parse();

        var bounds1 = result1.Operators.First(op => op.Name == "Tj").BoundingBox;
        var bounds2 = result2.Operators.First(op => op.Name == "Tj").BoundingBox;

        bounds1.Should().NotBeNull();
        bounds2.Should().NotBeNull();
        // Longer text should have wider bounds
        var width1 = bounds1!.Value.Right - bounds1.Value.Left;
        var width2 = bounds2!.Value.Right - bounds2.Value.Left;
        width2.Should().BeGreaterThan(width1);
    }

    [Fact]
    public void Bounds_Tj_HeightDependsOnFontSize()
    {
        var content1 = "BT /F1 12 Tf 100 500 Td (Test) Tj ET";
        var content2 = "BT /F1 24 Tf 100 500 Td (Test) Tj ET";

        var result1 = new ContentStreamParser(Encoding.UTF8.GetBytes(content1)).Parse();
        var result2 = new ContentStreamParser(Encoding.UTF8.GetBytes(content2)).Parse();

        var bounds1 = result1.Operators.First(op => op.Name == "Tj").BoundingBox;
        var bounds2 = result2.Operators.First(op => op.Name == "Tj").BoundingBox;

        bounds1.Should().NotBeNull();
        bounds2.Should().NotBeNull();
        // Larger font should have taller bounds
        var height1 = bounds1!.Value.Top - bounds1.Value.Bottom;
        var height2 = bounds2!.Value.Top - bounds2.Value.Bottom;
        height2.Should().BeGreaterThan(height1);
    }

    #endregion
}
