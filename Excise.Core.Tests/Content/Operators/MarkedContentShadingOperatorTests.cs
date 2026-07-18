using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Content.Operators;

/// <summary>
/// Dedicated coverage for operators that were previously parsed but untested
/// (issue #350): shading (sh, §8.7.4), marked content (MP/DP/BMC/BDC/EMC,
/// §14.6), compatibility (BX/EX, §8.10.1), and Type 3 glyph metrics (d0/d1,
/// §9.6.5). The contract is that each is recognised, carries its operands, and
/// does not corrupt the surrounding operator stream.
/// </summary>
public class MarkedContentShadingOperatorTests
{
    private static ContentStream Parse(string content) =>
        new ContentStreamParser(Encoding.UTF8.GetBytes(content)).Parse();

    #region sh — shading

    [Fact]
    public void Parse_sh_CarriesShadingName()
    {
        var result = Parse("/Sh1 sh");

        var op = result.Operators.Should().ContainSingle(o => o.Name == "sh").Subject;
        op.Operands.Should().ContainSingle();
        ((PdfName)op.Operands[0]).Value.Should().Be("Sh1");
    }

    [Fact]
    public void Parse_sh_DoesNotDisruptSurroundingPaintOps()
    {
        var result = Parse("q /Sh1 sh Q");

        result.Operators.Select(o => o.Name).Should().ContainInOrder("q", "sh", "Q");
    }

    #endregion

    #region Marked content — MP / DP / BMC / BDC / EMC

    [Fact]
    public void Parse_BMC_EMC_Pair()
    {
        var result = Parse("/Span BMC (hi) Tj EMC");

        result.Operators.Should().Contain(o => o.Name == "BMC");
        result.Operators.Should().Contain(o => o.Name == "EMC");
        result.Operators.Should().Contain(o => o.Name == "Tj",
            "content between marked-content markers must still parse");
    }

    [Fact]
    public void Parse_BDC_WithPropertyDictionary_IsRecognised_DictSkipped()
    {
        var result = Parse("/OC << /MCID 0 >> BDC EMC");

        var bdc = result.Operators.Should().ContainSingle(o => o.Name == "BDC").Subject;
        // The content-stream parser deliberately skips inline dictionaries
        // (SkipDictionary) since they aren't needed for bounds/redaction, so
        // only the /OC tag operand is captured. Capturing the property dict
        // (e.g. /MCID for OCG/structure-aware work) is tracked in #336/#329.
        ((PdfName)bdc.Operands[0]).Value.Should().Be("OC");
        result.Operators.Should().Contain(o => o.Name == "EMC");
    }

    [Fact]
    public void Parse_MP_And_DP_PointMarkers()
    {
        var result = Parse("/P1 MP /P2 << /K 1 >> DP");

        result.Operators.Should().Contain(o => o.Name == "MP");
        result.Operators.Should().Contain(o => o.Name == "DP");
    }

    [Fact]
    public void Parse_NestedMarkedContent_KeepsAllOperators()
    {
        var result = Parse("/A BMC /B BMC (x) Tj EMC EMC");

        result.Operators.Count(o => o.Name == "BMC").Should().Be(2);
        result.Operators.Count(o => o.Name == "EMC").Should().Be(2);
    }

    #endregion

    #region Compatibility — BX / EX

    [Fact]
    public void Parse_BX_EX_AcceptsUnknownOperatorsInBetween()
    {
        // BX/EX brackets a region that may contain operators an older reader
        // doesn't know. Parsing must not choke; the bracket ops are recognised.
        var result = Parse("BX /Sh1 sh EX");

        result.Operators.Should().Contain(o => o.Name == "BX");
        result.Operators.Should().Contain(o => o.Name == "EX");
    }

    #endregion

    #region Type 3 glyph metrics — d0 / d1

    [Fact]
    public void Parse_d0_WidthOnly()
    {
        var result = Parse("100 0 d0");

        var op = result.Operators.Should().ContainSingle(o => o.Name == "d0").Subject;
        op.Operands.Should().HaveCount(2);
        op.GetNumber(0).Should().Be(100);
    }

    [Fact]
    public void Parse_d1_SetsGlyphBoundingBox()
    {
        // wx wy llx lly urx ury d1
        var result = Parse("100 0 10 20 90 80 d1");

        var op = result.Operators.Should().ContainSingle(o => o.Name == "d1").Subject;
        op.BoundingBox.Should().NotBeNull();
        var bb = op.BoundingBox!.Value;
        bb.Left.Should().Be(10);
        bb.Bottom.Should().Be(20);
        bb.Right.Should().Be(90);
        bb.Top.Should().Be(80);
    }

    #endregion
}
