using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Xunit;

namespace Excise.Core.Tests.Content.Operators;

/// <summary>
/// Tests for color operators.
/// Tables 72-73, Section 8.6 of ISO 32000-2:2020
///
/// DeviceGray: g (fill), G (stroke)
/// DeviceRGB: rg (fill), RG (stroke)
/// DeviceCMYK: k (fill), K (stroke)
/// Color Space: cs (fill), CS (stroke)
/// Color: sc/scn (fill), SC/SCN (stroke)
/// </summary>
public class ColorOperatorTests
{
    #region g/G - DeviceGray

    [Fact]
    public void Parse_g_SetFillGray()
    {
        var content = "0.5 g";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().ContainSingle();
        var op = result.Operators[0];
        op.Name.Should().Be("g");
        op.GetNumber(0).Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void Parse_G_SetStrokeGray()
    {
        var content = "0.75 G";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.Name.Should().Be("G");
        op.GetNumber(0).Should().BeApproximately(0.75, 0.01);
    }

    [Theory]
    [InlineData(0, "Black")]
    [InlineData(1, "White")]
    [InlineData(0.5, "50% Gray")]
    public void Parse_g_GrayLevels(double gray, string description)
    {
        var content = $"{gray} g";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.GetNumber(0).Should().BeApproximately(gray, 0.01, because: description);
    }

    [Fact]
    public void Parse_g_G_Combined()
    {
        var content = "0.3 g 0.7 G";  // Different fill and stroke colors
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().HaveCount(2);
        result.Operators[0].Name.Should().Be("g");
        result.Operators[0].GetNumber(0).Should().BeApproximately(0.3, 0.01);
        result.Operators[1].Name.Should().Be("G");
        result.Operators[1].GetNumber(0).Should().BeApproximately(0.7, 0.01);
    }

    #endregion

    #region rg/RG - DeviceRGB

    [Fact]
    public void Parse_rg_SetFillRgb()
    {
        var content = "1 0 0 rg";  // Red
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.Name.Should().Be("rg");
        op.Operands.Should().HaveCount(3);
        op.GetNumber(0).Should().Be(1);  // R
        op.GetNumber(1).Should().Be(0);  // G
        op.GetNumber(2).Should().Be(0);  // B
    }

    [Fact]
    public void Parse_RG_SetStrokeRgb()
    {
        var content = "0 0 1 RG";  // Blue
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.Name.Should().Be("RG");
        op.GetNumber(0).Should().Be(0);
        op.GetNumber(1).Should().Be(0);
        op.GetNumber(2).Should().Be(1);
    }

    [Theory]
    [InlineData(1, 0, 0, "Red")]
    [InlineData(0, 1, 0, "Green")]
    [InlineData(0, 0, 1, "Blue")]
    [InlineData(1, 1, 0, "Yellow")]
    [InlineData(1, 0, 1, "Magenta")]
    [InlineData(0, 1, 1, "Cyan")]
    [InlineData(0, 0, 0, "Black")]
    [InlineData(1, 1, 1, "White")]
    public void Parse_rg_CommonColors(double r, double g, double b, string colorName)
    {
        var content = $"{r} {g} {b} rg";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.GetNumber(0).Should().Be(r, because: $"R component for {colorName}");
        op.GetNumber(1).Should().Be(g, because: $"G component for {colorName}");
        op.GetNumber(2).Should().Be(b, because: $"B component for {colorName}");
    }

    [Fact]
    public void Parse_rg_FloatingPoint()
    {
        var content = "0.2 0.4 0.6 rg";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.GetNumber(0).Should().BeApproximately(0.2, 0.01);
        op.GetNumber(1).Should().BeApproximately(0.4, 0.01);
        op.GetNumber(2).Should().BeApproximately(0.6, 0.01);
    }

    #endregion

    #region k/K - DeviceCMYK

    [Fact]
    public void Parse_k_SetFillCmyk()
    {
        var content = "0 0 0 1 k";  // Black in CMYK
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.Name.Should().Be("k");
        op.Operands.Should().HaveCount(4);
        op.GetNumber(0).Should().Be(0);  // C
        op.GetNumber(1).Should().Be(0);  // M
        op.GetNumber(2).Should().Be(0);  // Y
        op.GetNumber(3).Should().Be(1);  // K
    }

    [Fact]
    public void Parse_K_SetStrokeCmyk()
    {
        var content = "1 0 0 0 K";  // Cyan
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.Name.Should().Be("K");
        op.GetNumber(0).Should().Be(1);  // C
        op.GetNumber(1).Should().Be(0);  // M
        op.GetNumber(2).Should().Be(0);  // Y
        op.GetNumber(3).Should().Be(0);  // K
    }

    [Theory]
    [InlineData(1, 0, 0, 0, "Cyan")]
    [InlineData(0, 1, 0, 0, "Magenta")]
    [InlineData(0, 0, 1, 0, "Yellow")]
    [InlineData(0, 0, 0, 1, "Black")]
    [InlineData(0, 0, 0, 0, "White (no ink)")]
    public void Parse_k_CmykColors(double c, double m, double y, double k, string colorName)
    {
        var content = $"{c} {m} {y} {k} k";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.GetNumber(0).Should().Be(c, because: $"C component for {colorName}");
        op.GetNumber(1).Should().Be(m, because: $"M component for {colorName}");
        op.GetNumber(2).Should().Be(y, because: $"Y component for {colorName}");
        op.GetNumber(3).Should().Be(k, because: $"K component for {colorName}");
    }

    #endregion

    #region cs/CS - Set Color Space

    [Fact]
    public void Parse_cs_SetFillColorSpace()
    {
        var content = "/DeviceRGB cs";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.Name.Should().Be("cs");
        op.GetName(0).Should().Be("DeviceRGB");
    }

    [Fact]
    public void Parse_CS_SetStrokeColorSpace()
    {
        var content = "/DeviceCMYK CS";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.Name.Should().Be("CS");
        op.GetName(0).Should().Be("DeviceCMYK");
    }

    [Theory]
    [InlineData("/DeviceGray")]
    [InlineData("/DeviceRGB")]
    [InlineData("/DeviceCMYK")]
    [InlineData("/Pattern")]
    [InlineData("/CS1")]  // Named color space from resources
    public void Parse_cs_ColorSpaces(string colorSpace)
    {
        var content = $"{colorSpace} cs";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var op = result.Operators.Single();
        op.Name.Should().Be("cs");
    }

    #endregion

    #region sc/SC - Set Color (General)

    [Fact]
    public void Parse_sc_SetFillColor_SingleValue()
    {
        var content = "/DeviceGray cs 0.5 sc";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var scOp = result.Operators.First(op => op.Name == "sc");
        scOp.GetNumber(0).Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void Parse_SC_SetStrokeColor_ThreeValues()
    {
        var content = "/DeviceRGB CS 1 0.5 0 SC";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var scOp = result.Operators.First(op => op.Name == "SC");
        scOp.Operands.Should().HaveCount(3);
    }

    #endregion

    #region scn/SCN - Set Color (with Pattern)

    [Fact]
    public void Parse_scn_SetFillColorWithPattern()
    {
        var content = "/Pattern cs /P1 scn";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var scnOp = result.Operators.First(op => op.Name == "scn");
        scnOp.GetName(0).Should().Be("P1");
    }

    [Fact]
    public void Parse_SCN_SetStrokeColorWithPattern()
    {
        var content = "/Pattern CS /P1 SCN";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var scnOp = result.Operators.First(op => op.Name == "SCN");
        scnOp.GetName(0).Should().Be("P1");
    }

    [Fact]
    public void Parse_scn_ColorAndPattern()
    {
        // For uncolored tiling patterns: color values followed by pattern name
        var content = "/CS1 cs 0.5 0.5 0.5 /P1 scn";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var scnOp = result.Operators.First(op => op.Name == "scn");
        scnOp.Operands.Should().HaveCount(4);  // 3 color components + pattern name
    }

    #endregion

    #region Color with Graphics Operations

    [Fact]
    public void Parse_ColoredRectangle()
    {
        var content = @"
            1 0 0 rg
            100 100 200 150 re
            f
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().HaveCount(3);
        result.Operators[0].Name.Should().Be("rg");
        result.Operators[1].Name.Should().Be("re");
        result.Operators[2].Name.Should().Be("f");
    }

    [Fact]
    public void Parse_StrokedAndFilledRectangle()
    {
        var content = @"
            0 0 1 rg
            1 0 0 RG
            100 100 200 150 re
            B
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().HaveCount(4);
        // Blue fill
        result.Operators[0].Name.Should().Be("rg");
        result.Operators[0].GetNumber(2).Should().Be(1);
        // Red stroke
        result.Operators[1].Name.Should().Be("RG");
        result.Operators[1].GetNumber(0).Should().Be(1);
    }

    [Fact]
    public void Parse_ColoredText()
    {
        var content = @"
            BT
            0 0 1 rg
            /F1 12 Tf
            100 700 Td
            (Blue text) Tj
            ET
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        result.Operators.Should().Contain(op => op.Name == "rg");
        result.Operators.Should().Contain(op => op.Name == "Tj");
    }

    [Fact]
    public void Parse_MultipleColorChanges()
    {
        var content = @"
            1 0 0 rg 100 100 50 50 re f
            0 1 0 rg 200 100 50 50 re f
            0 0 1 rg 300 100 50 50 re f
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        var colorOps = result.Operators.Where(op => op.Name == "rg").ToList();
        colorOps.Should().HaveCount(3);
        // Red
        colorOps[0].GetNumber(0).Should().Be(1);
        // Green
        colorOps[1].GetNumber(1).Should().Be(1);
        // Blue
        colorOps[2].GetNumber(2).Should().Be(1);
    }

    [Fact]
    public void Parse_ColorInGraphicsState()
    {
        var content = @"
            q
            1 0 0 rg
            100 100 50 50 re f
            Q
            0 g
            200 100 50 50 re f
        ";
        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));

        var result = parser.Parse();

        // First rectangle should be red, second should be black (after Q restores state)
        var colorOps = result.Operators.Where(op => op.Name == "rg" || op.Name == "g").ToList();
        colorOps.Should().HaveCount(2);
    }

    #endregion

    #region Operator Category

    [Theory]
    [InlineData("g")]
    [InlineData("G")]
    [InlineData("rg")]
    [InlineData("RG")]
    [InlineData("k")]
    [InlineData("K")]
    [InlineData("cs")]
    [InlineData("CS")]
    [InlineData("sc")]
    [InlineData("SC")]
    [InlineData("scn")]
    [InlineData("SCN")]
    public void Parse_ColorOperator_HasCorrectCategory(string operatorName)
    {
        // Build appropriate content for the operator
        var content = operatorName switch
        {
            "g" or "G" => $"0.5 {operatorName}",
            "rg" or "RG" => $"1 0 0 {operatorName}",
            "k" or "K" => $"0 0 0 1 {operatorName}",
            "cs" or "CS" => $"/DeviceGray {operatorName}",
            "sc" or "SC" => $"0.5 {operatorName}",
            "scn" or "SCN" => $"/P1 {operatorName}",
            _ => throw new ArgumentException($"Unknown operator: {operatorName}")
        };

        var parser = new ContentStreamParser(Encoding.UTF8.GetBytes(content));
        var result = parser.Parse();

        var op = result.Operators.First(o => o.Name == operatorName);
        op.Category.Should().Be(OperatorCategory.Color);
    }

    #endregion
}
