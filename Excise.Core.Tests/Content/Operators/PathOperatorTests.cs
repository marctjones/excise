using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Xunit;

namespace Excise.Core.Tests.Content.Operators;

/// <summary>
/// Tests for path construction and painting operators.
/// Path Construction: m, l, c, v, y, h, re (Table 58, Section 8.5.2)
/// Path Painting: S, s, f, F, f*, B, B*, b, b*, n (Table 59, Section 8.5.3)
/// Clipping: W, W* (Table 60, Section 8.5.4)
/// </summary>
public class PathOperatorTests
{
    #region m - Move To (Begin Subpath)

    [Fact]
    public void Parse_m_MoveTo()
    {
        var content = "100 200 m";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().ContainSingle();
        var op = result.Operators[0];
        op.Name.Should().Be("m");
        op.Operands.Should().HaveCount(2);
        op.GetNumber(0).Should().Be(100);
        op.GetNumber(1).Should().Be(200);
    }

    [Fact]
    public void Parse_m_FloatingPointCoordinates()
    {
        var content = "100.5 200.75 m";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.GetNumber(0).Should().BeApproximately(100.5, 0.001);
        op.GetNumber(1).Should().BeApproximately(200.75, 0.001);
    }

    [Fact]
    public void Parse_m_NegativeCoordinates()
    {
        var content = "-50 -100 m";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.GetNumber(0).Should().Be(-50);
        op.GetNumber(1).Should().Be(-100);
    }

    [Fact]
    public void Parse_m_ZeroCoordinates()
    {
        var content = "0 0 m";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.GetNumber(0).Should().Be(0);
        op.GetNumber(1).Should().Be(0);
    }

    #endregion

    #region l - Line To

    [Fact]
    public void Parse_l_LineTo()
    {
        var content = "0 0 m 100 100 l";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().HaveCount(2);
        var lineOp = result.Operators[1];
        lineOp.Name.Should().Be("l");
        lineOp.GetNumber(0).Should().Be(100);
        lineOp.GetNumber(1).Should().Be(100);
    }

    [Fact]
    public void Parse_MultipleLines()
    {
        var content = "0 0 m 100 0 l 100 100 l 0 100 l";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().HaveCount(4);
        result.Operators.Skip(1).Should().AllSatisfy(op => op.Name.Should().Be("l"));
    }

    #endregion

    #region c - Cubic Bézier Curve

    [Fact]
    public void Parse_c_CubicBezier()
    {
        // c x1 y1 x2 y2 x3 y3 - curve with two control points to endpoint
        var content = "0 0 m 25 100 75 100 100 0 c";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var curveOp = result.Operators[1];
        curveOp.Name.Should().Be("c");
        curveOp.Operands.Should().HaveCount(6);
        curveOp.GetNumber(0).Should().Be(25);   // x1
        curveOp.GetNumber(1).Should().Be(100);  // y1
        curveOp.GetNumber(2).Should().Be(75);   // x2
        curveOp.GetNumber(3).Should().Be(100);  // y2
        curveOp.GetNumber(4).Should().Be(100);  // x3
        curveOp.GetNumber(5).Should().Be(0);    // y3
    }

    [Fact]
    public void Parse_c_MultipleCurves()
    {
        var content = @"
            0 0 m
            50 100 100 100 150 0 c
            200 -100 250 -100 300 0 c
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Where(op => op.Name == "c").Should().HaveCount(2);
    }

    #endregion

    #region v - Cubic Bézier (Current Point as First Control)

    [Fact]
    public void Parse_v_BezierVariant()
    {
        // v x2 y2 x3 y3 - first control point = current point
        var content = "0 0 m 50 100 100 50 v";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var curveOp = result.Operators[1];
        curveOp.Name.Should().Be("v");
        curveOp.Operands.Should().HaveCount(4);
        curveOp.GetNumber(0).Should().Be(50);   // x2
        curveOp.GetNumber(1).Should().Be(100);  // y2
        curveOp.GetNumber(2).Should().Be(100);  // x3
        curveOp.GetNumber(3).Should().Be(50);   // y3
    }

    #endregion

    #region y - Cubic Bézier (Endpoint as Second Control)

    [Fact]
    public void Parse_y_BezierVariant()
    {
        // y x1 y1 x3 y3 - second control point = endpoint
        var content = "0 0 m 50 100 100 50 y";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var curveOp = result.Operators[1];
        curveOp.Name.Should().Be("y");
        curveOp.Operands.Should().HaveCount(4);
    }

    #endregion

    #region h - Close Subpath

    [Fact]
    public void Parse_h_ClosePath()
    {
        var content = "0 0 m 100 0 l 100 100 l h";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Last().Name.Should().Be("h");
        result.Operators.Last().Operands.Should().BeEmpty();
    }

    #endregion

    #region re - Rectangle

    [Fact]
    public void Parse_re_Rectangle()
    {
        var content = "100 200 50 75 re";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var rectOp = result.Operators.Single();
        rectOp.Name.Should().Be("re");
        rectOp.Operands.Should().HaveCount(4);
        rectOp.GetNumber(0).Should().Be(100);  // x
        rectOp.GetNumber(1).Should().Be(200);  // y
        rectOp.GetNumber(2).Should().Be(50);   // width
        rectOp.GetNumber(3).Should().Be(75);   // height
    }

    [Fact]
    public void Parse_re_CalculatesBounds()
    {
        var content = "100 200 50 75 re f";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        // Fill operator should have bounds from rectangle
        var fillOp = result.Operators.First(op => op.Name == "f");
        fillOp.BoundingBox.Should().NotBeNull();
        fillOp.BoundingBox!.Value.Left.Should().BeApproximately(100, 1);
        fillOp.BoundingBox!.Value.Bottom.Should().BeApproximately(200, 1);
        fillOp.BoundingBox!.Value.Right.Should().BeApproximately(150, 1);  // 100 + 50
        fillOp.BoundingBox!.Value.Top.Should().BeApproximately(275, 1);    // 200 + 75
    }

    [Fact]
    public void Parse_re_NegativeDimensions()
    {
        // PDF allows negative width/height (draws in opposite direction)
        var content = "100 200 -50 -75 re";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var rectOp = result.Operators.Single();
        rectOp.GetNumber(2).Should().Be(-50);
        rectOp.GetNumber(3).Should().Be(-75);
    }

    [Fact]
    public void Parse_re_MultipleRectangles()
    {
        var content = @"
            10 10 50 50 re
            100 100 50 50 re
            f
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Where(op => op.Name == "re").Should().HaveCount(2);
    }

    #endregion

    #region S - Stroke Path

    [Fact]
    public void Parse_S_Stroke()
    {
        var content = "0 0 m 100 100 l S";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Last().Name.Should().Be("S");
        result.Operators.Last().Operands.Should().BeEmpty();
    }

    [Fact]
    public void Parse_S_CalculatesBounds()
    {
        var content = "50 100 m 200 300 l S";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var strokeOp = result.Operators.Last();
        strokeOp.BoundingBox.Should().NotBeNull();
        strokeOp.BoundingBox!.Value.Left.Should().BeApproximately(50, 1);
        strokeOp.BoundingBox!.Value.Bottom.Should().BeApproximately(100, 1);
        strokeOp.BoundingBox!.Value.Right.Should().BeApproximately(200, 1);
        strokeOp.BoundingBox!.Value.Top.Should().BeApproximately(300, 1);
    }

    #endregion

    #region s - Close and Stroke

    [Fact]
    public void Parse_s_CloseAndStroke()
    {
        var content = "0 0 m 100 0 l 100 100 l s";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Last().Name.Should().Be("s");
    }

    #endregion

    #region f/F - Fill Path

    [Fact]
    public void Parse_f_Fill()
    {
        var content = "0 0 m 100 0 l 50 100 l f";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Last().Name.Should().Be("f");
    }

    [Fact]
    public void Parse_F_FillObsolete()
    {
        // F is equivalent to f (obsolete form)
        var content = "0 0 m 100 0 l 50 100 l F";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Last().Name.Should().Be("F");
    }

    [Fact]
    public void Parse_fStar_EvenOddFill()
    {
        var content = "0 0 m 100 0 l 50 100 l f*";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Last().Name.Should().Be("f*");
    }

    #endregion

    #region B/B* - Fill and Stroke

    [Fact]
    public void Parse_B_FillAndStroke()
    {
        var content = "0 0 m 100 0 l 50 100 l h B";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Last().Name.Should().Be("B");
    }

    [Fact]
    public void Parse_BStar_EvenOddFillAndStroke()
    {
        var content = "0 0 m 100 0 l 50 100 l h B*";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Last().Name.Should().Be("B*");
    }

    #endregion

    #region b/b* - Close, Fill and Stroke

    [Fact]
    public void Parse_b_CloseFillAndStroke()
    {
        var content = "0 0 m 100 0 l 50 100 l b";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Last().Name.Should().Be("b");
    }

    [Fact]
    public void Parse_bStar_CloseEvenOddFillAndStroke()
    {
        var content = "0 0 m 100 0 l 50 100 l b*";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Last().Name.Should().Be("b*");
    }

    #endregion

    #region n - End Path (No Paint)

    [Fact]
    public void Parse_n_EndPath()
    {
        var content = "0 0 m 100 100 l n";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Last().Name.Should().Be("n");
    }

    [Fact]
    public void Parse_n_UsedWithClipping()
    {
        // n is commonly used after W (clipping) to end path without painting
        var content = "0 0 m 100 0 l 100 100 l h W n";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var ops = result.Operators.TakeLast(2).ToList();
        ops[0].Name.Should().Be("W");
        ops[1].Name.Should().Be("n");
    }

    #endregion

    #region W/W* - Clipping Path

    [Fact]
    public void Parse_W_ClipNonzero()
    {
        var content = "0 0 m 100 0 l 100 100 l h W n";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().Contain(op => op.Name == "W");
    }

    [Fact]
    public void Parse_WStar_ClipEvenOdd()
    {
        var content = "0 0 m 100 0 l 100 100 l h W* n";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().Contain(op => op.Name == "W*");
    }

    #endregion

    #region Complex Path Sequences

    [Fact]
    public void Parse_Triangle()
    {
        var content = @"
            0 0 m
            100 0 l
            50 86.6 l
            h f
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().HaveCount(5);
        result.Operators.Select(op => op.Name)
            .Should().ContainInOrder("m", "l", "l", "h", "f");
    }

    [Fact]
    public void Parse_MultipleSubpaths()
    {
        var content = @"
            0 0 m 50 0 l 50 50 l 0 50 l h
            100 0 m 150 0 l 150 50 l 100 50 l h
            f
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        // Should have 2 'm' operators for 2 subpaths
        result.Operators.Where(op => op.Name == "m").Should().HaveCount(2);
    }

    [Fact]
    public void Parse_RoundedRectangle()
    {
        // Rounded rectangle using Bézier curves
        var content = @"
            10 0 m
            90 0 l
            100 0 100 10 100 10 c
            100 90 l
            100 100 90 100 90 100 c
            10 100 l
            0 100 0 90 0 90 c
            0 10 l
            0 0 10 0 10 0 c
            h f
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Where(op => op.Name == "c").Should().HaveCount(4);
        result.Operators.Where(op => op.Name == "l").Should().HaveCount(4);
    }

    [Fact]
    public void Parse_CircleApproximation()
    {
        // Circle using 4 Bézier curves (common approximation)
        // Control point distance = r * 0.5523 for best approximation
        var r = 50.0;
        var k = r * 0.5523;
        var content = $@"
            {r} 0 m
            {r} {k} {k} {r} 0 {r} c
            -{k} {r} -{r} {k} -{r} 0 c
            -{r} -{k} -{k} -{r} 0 -{r} c
            {k} -{r} {r} -{k} {r} 0 c
            h f
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Where(op => op.Name == "c").Should().HaveCount(4);
    }

    #endregion

    #region Bounds Calculation

    [Fact]
    public void Bounds_Line_CalculatesCorrectly()
    {
        var content = "10 20 m 100 150 l S";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var strokeOp = result.Operators.Last();
        strokeOp.BoundingBox.Should().NotBeNull();
        var bounds = strokeOp.BoundingBox!.Value;
        bounds.Left.Should().BeApproximately(10, 1);
        bounds.Bottom.Should().BeApproximately(20, 1);
        bounds.Right.Should().BeApproximately(100, 1);
        bounds.Top.Should().BeApproximately(150, 1);
    }

    [Fact]
    public void Bounds_BezierCurve_IncludesControlPoints()
    {
        // Bounds should encompass control points for conservative estimate
        var content = "0 0 m 50 200 100 -100 150 0 c S";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var strokeOp = result.Operators.Last();
        strokeOp.BoundingBox.Should().NotBeNull();
        var bounds = strokeOp.BoundingBox!.Value;
        // Should include control point at y=200
        bounds.Top.Should().BeGreaterThanOrEqualTo(200);
        // Should include control point at y=-100
        bounds.Bottom.Should().BeLessThanOrEqualTo(-100);
    }

    [Fact]
    public void Bounds_WithTransformation_AppliesCTM()
    {
        // Rectangle at origin, then translated
        var content = @"
            1 0 0 1 100 100 cm
            0 0 50 50 re f
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var fillOp = result.Operators.Last();
        fillOp.BoundingBox.Should().NotBeNull();
        var bounds = fillOp.BoundingBox!.Value;
        // Rectangle should be translated by (100, 100)
        bounds.Left.Should().BeApproximately(100, 1);
        bounds.Bottom.Should().BeApproximately(100, 1);
    }

    #endregion
}
