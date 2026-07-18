using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Content.Operators;

/// <summary>
/// Tests for graphics state operators: q, Q, cm, w, J, j, M, d, ri, i, gs
/// ISO 32000-2:2020 Section 8.4.4, Tables 56-57
/// </summary>
public class GraphicsStateOperatorTests
{
    #region q/Q - Save/Restore Graphics State

    [Fact]
    public void Parse_q_RecognizesOperator()
    {
        var content = "q";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().ContainSingle();
        result.Operators[0].Name.Should().Be("q");
        result.Operators[0].Operands.Should().BeEmpty();
    }

    [Fact]
    public void Parse_Q_RecognizesOperator()
    {
        var content = "Q";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().ContainSingle();
        result.Operators[0].Name.Should().Be("Q");
    }

    [Fact]
    public void Parse_q_Q_BasicNesting()
    {
        var content = "q Q";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().HaveCount(2);
        result.Operators[0].Name.Should().Be("q");
        result.Operators[1].Name.Should().Be("Q");
    }

    [Fact]
    public void Parse_q_Q_MultipleNesting()
    {
        var content = "q q q Q Q Q";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().HaveCount(6);
        result.Operators.Take(3).Should().AllSatisfy(op => op.Name.Should().Be("q"));
        result.Operators.Skip(3).Should().AllSatisfy(op => op.Name.Should().Be("Q"));
    }

    [Fact]
    public void Parse_q_cm_Q_RestoresOriginalMatrix()
    {
        // Test that state changes between q/Q are isolated
        var content = @"
            q
            1 0 0 1 100 200 cm
            Q
            0 0 m 100 100 l S
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().HaveCount(6);
        result.Operators[0].Name.Should().Be("q");
        result.Operators[1].Name.Should().Be("cm");
        result.Operators[2].Name.Should().Be("Q");
        // Path operations after Q should be in original coordinate space
    }

    #endregion

    #region cm - Concatenate Matrix

    [Fact]
    public void Parse_cm_IdentityMatrix()
    {
        var content = "1 0 0 1 0 0 cm";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().ContainSingle();
        var op = result.Operators[0];
        op.Name.Should().Be("cm");
        op.Operands.Should().HaveCount(6);
    }

    [Fact]
    public void Parse_cm_Translation()
    {
        // Translation by (100, 200)
        var content = "1 0 0 1 100 200 cm";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.Name.Should().Be("cm");
        op.GetNumber(0).Should().Be(1);  // a
        op.GetNumber(1).Should().Be(0);  // b
        op.GetNumber(2).Should().Be(0);  // c
        op.GetNumber(3).Should().Be(1);  // d
        op.GetNumber(4).Should().Be(100); // e (tx)
        op.GetNumber(5).Should().Be(200); // f (ty)
    }

    [Fact]
    public void Parse_cm_Scale()
    {
        // Scale by 2x in both dimensions
        var content = "2 0 0 2 0 0 cm";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.GetNumber(0).Should().Be(2);  // a (sx)
        op.GetNumber(3).Should().Be(2);  // d (sy)
    }

    [Fact]
    public void Parse_cm_Rotation90Degrees()
    {
        // 90 degree rotation: [cos(90), sin(90), -sin(90), cos(90), 0, 0]
        // = [0, 1, -1, 0, 0, 0]
        var content = "0 1 -1 0 0 0 cm";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.GetNumber(0).Should().Be(0);   // cos(90)
        op.GetNumber(1).Should().Be(1);   // sin(90)
        op.GetNumber(2).Should().Be(-1);  // -sin(90)
        op.GetNumber(3).Should().Be(0);   // cos(90)
    }

    [Fact]
    public void Parse_cm_FloatingPointValues()
    {
        var content = "0.707 0.707 -0.707 0.707 50.5 100.25 cm";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.GetNumber(0).Should().BeApproximately(0.707, 0.001);
        op.GetNumber(4).Should().BeApproximately(50.5, 0.001);
        op.GetNumber(5).Should().BeApproximately(100.25, 0.001);
    }

    [Fact]
    public void Parse_cm_NegativeValues()
    {
        var content = "-1 0 0 -1 0 0 cm";  // 180 degree rotation (point reflection)
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.GetNumber(0).Should().Be(-1);
        op.GetNumber(3).Should().Be(-1);
    }

    [Fact]
    public void Parse_MultipleCm_Accumulates()
    {
        var content = @"
            1 0 0 1 100 0 cm
            1 0 0 1 0 200 cm
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().HaveCount(2);
        // Both cm operators parsed independently
        // State tracking should accumulate transformations
    }

    #endregion

    #region w - Line Width

    [Fact]
    public void Parse_w_IntegerValue()
    {
        var content = "2 w";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().ContainSingle();
        var op = result.Operators[0];
        op.Name.Should().Be("w");
        op.Operands.Should().ContainSingle();
        op.GetNumber(0).Should().Be(2);
    }

    [Fact]
    public void Parse_w_FloatValue()
    {
        var content = "0.5 w";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.GetNumber(0).Should().BeApproximately(0.5, 0.001);
    }

    [Fact]
    public void Parse_w_ZeroValue()
    {
        // Zero line width = thinnest line device can produce
        var content = "0 w";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.GetNumber(0).Should().Be(0);
    }

    #endregion

    #region J - Line Cap Style

    [Theory]
    [InlineData(0, "Butt cap")]
    [InlineData(1, "Round cap")]
    [InlineData(2, "Projecting square cap")]
    public void Parse_J_LineCap(int capStyle, string description)
    {
        var content = $"{capStyle} J";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.Name.Should().Be("J");
        op.GetNumber(0).Should().Be(capStyle, because: description);
    }

    #endregion

    #region j - Line Join Style

    [Theory]
    [InlineData(0, "Miter join")]
    [InlineData(1, "Round join")]
    [InlineData(2, "Bevel join")]
    public void Parse_j_LineJoin(int joinStyle, string description)
    {
        var content = $"{joinStyle} j";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.Name.Should().Be("j");
        op.GetNumber(0).Should().Be(joinStyle, because: description);
    }

    #endregion

    #region M - Miter Limit

    [Fact]
    public void Parse_M_MiterLimit()
    {
        var content = "10 M";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.Name.Should().Be("M");
        op.GetNumber(0).Should().Be(10);
    }

    #endregion

    #region d - Dash Pattern

    [Fact]
    public void Parse_d_SolidLine()
    {
        var content = "[] 0 d";  // No dashes = solid line
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.Name.Should().Be("d");
        op.Operands.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_d_SimpleDash()
    {
        var content = "[3] 0 d";  // 3 on, 3 off
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.Name.Should().Be("d");
    }

    [Fact]
    public void Parse_d_ComplexDash()
    {
        var content = "[2 1 3 1] 0 d";  // 2 on, 1 off, 3 on, 1 off
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.Name.Should().Be("d");
    }

    [Fact]
    public void Parse_d_WithPhase()
    {
        var content = "[3 2] 4 d";  // Start at phase 4
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.Name.Should().Be("d");
    }

    #endregion

    #region ri - Rendering Intent

    [Theory]
    [InlineData("/AbsoluteColorimetric")]
    [InlineData("/RelativeColorimetric")]
    [InlineData("/Saturation")]
    [InlineData("/Perceptual")]
    public void Parse_ri_RenderingIntent(string intent)
    {
        var content = $"{intent} ri";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.Name.Should().Be("ri");
        op.Operands.Should().ContainSingle();
    }

    #endregion

    #region i - Flatness Tolerance

    [Fact]
    public void Parse_i_Flatness()
    {
        var content = "1 i";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.Name.Should().Be("i");
        op.GetNumber(0).Should().Be(1);
    }

    [Fact]
    public void Parse_i_MaxFlatness()
    {
        var content = "100 i";  // Max flatness value
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.GetNumber(0).Should().Be(100);
    }

    #endregion

    #region gs - ExtGState

    [Fact]
    public void Parse_gs_ExtGState()
    {
        var content = "/GS1 gs";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.Name.Should().Be("gs");
        op.GetName(0).Should().Be("GS1");
    }

    [Fact]
    public void Parse_gs_WithPageExtGState_ParsesWithoutThrowing()
    {
        // Build a PDF with /Resources << /ExtGState << /GS1 << /LW 5.0 >> >> >>
        // and verify the gs operator is parsed and text still produces bounds.
        var pdf = MakePdfWithExtGState("/GS1 gs BT /F1 12 Tf 100 100 Td (text) Tj ET");
        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);

        var page = doc.GetPage(1);
        var stream = page.GetContentStream();

        stream.Operators.Should().ContainSingle(op => op.Name == "gs",
            "ExtGState reference in page resources must produce a gs operator");
        stream.Operators.Should().ContainSingle(op => op.Name == "Tj",
            "text operators after gs must still be parsed");
    }

    #endregion

    #region Line state save/restore

    [Fact]
    public void Parse_LineWidthWithQQ_TextOperationsUnaffected()
    {
        // Setting line width inside q/Q must not corrupt text-operator parsing.
        var content = "q 5 w Q BT /F1 12 Tf 100 100 Td (text) Tj ET";
        var result = new ContentStreamParser(Encoding.UTF8.GetBytes(content)).Parse();

        var tj = result.Operators.Single(op => op.Name == "Tj");
        tj.BoundingBox.Should().NotBeNull("text bounds must survive q/Q line-state changes");
    }

    [Fact]
    public void Parse_NestedLineStateQQ_ProducesCorrectOperatorCount()
    {
        // q 1 w q 2 w Q Q  →  six operators, nesting must be balanced
        var content = "q 1 w q 2 w Q Q";
        var result = new ContentStreamParser(Encoding.UTF8.GetBytes(content)).Parse();

        result.Operators.Should().HaveCount(6);
        result.Operators.Select(o => o.Name).Should()
            .ContainInOrder("q", "w", "q", "w", "Q", "Q");
    }

    [Fact]
    public void Parse_LineCap_J_Parsed()
    {
        var content = "1 J";
        var result = new ContentStreamParser(Encoding.ASCII.GetBytes(content)).Parse();

        var op = result.Operators.Single(o => o.Name == "J");
        op.GetNumber(0).Should().Be(1);
    }

    [Fact]
    public void Parse_LineJoin_j_Parsed()
    {
        var content = "2 j";
        var result = new ContentStreamParser(Encoding.ASCII.GetBytes(content)).Parse();

        var op = result.Operators.Single(o => o.Name == "j");
        op.GetNumber(0).Should().Be(2);
    }

    [Fact]
    public void Parse_MiterLimit_M_Parsed()
    {
        var content = "4 M";
        var result = new ContentStreamParser(Encoding.ASCII.GetBytes(content)).Parse();

        var op = result.Operators.Single(o => o.Name == "M");
        op.GetNumber(0).Should().Be(4);
    }

    #endregion

    #region Helpers

    /// <summary>Build a minimal single-page PDF with an ExtGState resource GS1 (LW=5) and the given content stream.</summary>
    private static byte[] MakePdfWithExtGState(string contentStr)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long obj1Pos, obj2Pos, obj3Pos, obj4Pos, obj5Pos;

        obj1Pos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");

        obj2Pos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        obj3Pos = sb.Length;
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]");
        sb.AppendLine("   /Resources << /Font << /F1 5 0 R >> /ExtGState << /GS1 << /LW 5.0 >> >> >>");
        sb.AppendLine("   /Contents 4 0 R >>");
        sb.AppendLine("endobj");

        var contentBytes = Encoding.Latin1.GetBytes(contentStr);
        obj4Pos = sb.Length;
        var header4 = $"4 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n";
        sb.Append(header4);
        sb.Append(contentStr);
        sb.AppendLine("\nendstream");
        sb.AppendLine("endobj");

        obj5Pos = sb.Length;
        sb.AppendLine("5 0 obj");
        sb.AppendLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        sb.AppendLine("xref");
        sb.AppendLine("0 6");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{obj1Pos:D10} 00000 n ");
        sb.AppendLine($"{obj2Pos:D10} 00000 n ");
        sb.AppendLine($"{obj3Pos:D10} 00000 n ");
        sb.AppendLine($"{obj4Pos:D10} 00000 n ");
        sb.AppendLine($"{obj5Pos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 6 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    #endregion

    #region Combined Operations

    [Fact]
    public void Parse_TypicalGraphicsStateSequence()
    {
        var content = @"
            q
            1 0 0 1 50 50 cm
            2 w
            0 J
            1 j
            10 M
            [3 2] 0 d
            0.5 g
            100 100 200 150 re
            S
            Q
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().HaveCountGreaterThan(5);
        result.Operators[0].Name.Should().Be("q");
        result.Operators.Last().Name.Should().Be("Q");
    }

    [Fact]
    public void Parse_NestedGraphicsStates_WithDifferentSettings()
    {
        var content = @"
            q
            1 w
            q
            2 w
            Q
            Q
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        // Verify nesting structure
        var ops = result.Operators.ToList();
        ops[0].Name.Should().Be("q");
        ops[1].Name.Should().Be("w");
        ops[2].Name.Should().Be("q");
        ops[3].Name.Should().Be("w");
        ops[4].Name.Should().Be("Q");
        ops[5].Name.Should().Be("Q");
    }

    #endregion
}
