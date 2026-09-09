using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Xunit;

namespace Excise.Core.Tests.Content;

/// <summary>
/// Guards the invariant behind the renderer's metadata-free parse mode
/// (#598): with <see cref="ContentStreamParser.ComputeOperatorMetadata"/>
/// off, the parsed operator sequence — names, operands, inline-image bytes —
/// must be byte-for-byte identical to a full parse. Only the annotations
/// (BoundingBox, decoded TextContent) may differ. This is what makes the
/// renderer's use of the mode pixel-identical: its input operators are
/// provably the same.
/// </summary>
public class ContentStreamParserMetadataModeTests
{
    private const string RepresentativeStream =
        "q 0.5 0 0 0.5 10 20 cm " +
        "1 0 0 rg 10 10 80 80 re f " +
        "BT /F1 12 Tf 100 700 Td (Hello \\(world\\)) Tj " +
        "[(kerned) -120 (text)] TJ ET " +
        "0 0 100 100 re W n " +
        "BI /W 2 /H 2 /CS /G /BPC 8 ID \x01\x02\x03\x04 EI " +
        "/Fm1 Do Q";

    [Fact]
    public void MetadataFreeParse_YieldsIdenticalOperatorsAndOperands()
    {
        var bytes = Encoding.Latin1.GetBytes(RepresentativeStream);

        var full = new ContentStreamParser(bytes).Parse();
        var lean = new ContentStreamParser(bytes) { ComputeOperatorMetadata = false }.Parse();

        lean.Operators.Count.Should().Be(full.Operators.Count);
        for (var i = 0; i < full.Operators.Count; i++)
        {
            lean.Operators[i].Name.Should().Be(full.Operators[i].Name);
            // ToString serializes operands; identical text means identical
            // operand values in identical order.
            lean.Operators[i].ToString().Should().Be(full.Operators[i].ToString(),
                $"operator #{i} must be unaffected by the metadata mode");
            (lean.Operators[i].InlineImageData ?? Array.Empty<byte>())
                .Should().Equal(full.Operators[i].InlineImageData ?? Array.Empty<byte>());
        }

        // Round-trip through the writer must be identical too — the writer
        // reads only names/operands/inline bytes, exactly what a renderer
        // replay consumes.
        var writer = new ContentStreamWriter();
        writer.Write(lean).Should().Equal(writer.Write(full));
    }

    [Fact]
    public void MetadataFreeParse_SkipsBoundsAnnotations()
    {
        var bytes = Encoding.Latin1.GetBytes(RepresentativeStream);

        var full = new ContentStreamParser(bytes).Parse();
        var lean = new ContentStreamParser(bytes) { ComputeOperatorMetadata = false }.Parse();

        full.Operators.Should().Contain(op => op.BoundingBox != null,
            "the full parse computes bounds (redaction depends on them)");
        lean.Operators.Should().OnlyContain(op => op.BoundingBox == null,
            "the metadata-free parse must not emit bounds computed from untracked state");
        lean.Operators.Should().OnlyContain(op =>
                op.GraphicsTransform == null && op.TextTransform == null,
            "transform snapshots are metadata too");
    }

    [Fact]
    public void FullParse_CapturesTransformsAtTextShowingOperator()
    {
        var bytes = Encoding.Latin1.GetBytes(
            "2 0 0 3 10 20 cm BT /F1 1 Tf 4 0 0 -5 6 7 Tm (X) Tj ET");

        var text = new ContentStreamParser(bytes).Parse().Operators
            .Single(op => op.Name == "Tj");

        text.GraphicsTransform.Should().Be(new ContentTransform(2, 0, 0, 3, 10, 20));
        text.TextTransform.Should().Be(new ContentTransform(4, 0, 0, -5, 6, 7));
    }

    [Fact]
    public void FullParse_CapturesTransformAtEveryPathConstructionOperator()
    {
        // #1433: page-space coordinates for individual path-construction
        // operators, not just the aggregate paint-operator bbox. Each of
        // m/l/re gets its own GraphicsTransform snapshot -- not only
        // text-showing operators -- so a caller transforms the raw Operands
        // itself instead of needing a second CTM tracker outside the library.
        var bytes = Encoding.Latin1.GetBytes(
            "2 0 0 3 10 20 cm 1 1 m 2 2 l 5 6 80 90 re f");

        var ops = new ContentStreamParser(bytes).Parse().Operators;
        var expectedCtm = new ContentTransform(2, 0, 0, 3, 10, 20);

        var moveTo = ops.Single(op => op.Name == "m");
        var lineTo = ops.Single(op => op.Name == "l");
        var rect = ops.Single(op => op.Name == "re");

        moveTo.GraphicsTransform.Should().Be(expectedCtm,
            "m must carry the CTM in effect when it executed, same as a text-showing operator does");
        lineTo.GraphicsTransform.Should().Be(expectedCtm);
        rect.GraphicsTransform.Should().Be(expectedCtm);

        // A caller applies the transform to the raw operands itself: re's
        // (x,y) corner (5,6) maps to page space as
        // (A*x + C*y + E, B*x + D*y + F) = (2*5+0*6+10, 0*5+3*6+20) = (20, 38).
        var t = rect.GraphicsTransform!.Value;
        var (x, y) = (5.0, 6.0);
        var pageX = t.A * x + t.C * y + t.E;
        var pageY = t.B * x + t.D * y + t.F;
        pageX.Should().Be(20);
        pageY.Should().Be(38);

        // #1436: the library exposes that operation, so a caller does not have
        // to reimplement the two lines above.
        t.TransformPoint(x, y).Should().Be((20.0, 38.0));
    }

    [Fact]
    public void TransformPoint_AppliesTheOffDiagonalTermsToTheRightCoordinate()
    {
        // #1436. The CTMs in the tests above are axis-aligned (B = C = 0), so
        // they cannot tell A*x + C*y from A*x + B*y. A quarter-turn rotation
        // (A=0, B=1, C=-1, D=0) plus a translation does: (5,6) maps to
        // (0*5 + -1*6 + 100, 1*5 + 0*6 + 200) = (94, 205).
        new ContentTransform(0, 1, -1, 0, 100, 200)
            .TransformPoint(5, 6).Should().Be((94.0, 205.0));
    }

    [Fact]
    public void FullParse_CmOperatorItself_CarriesThePreCmCtm()
    {
        // The CTM a "cm" operator's own GraphicsTransform carries is the CTM
        // BEFORE that cm's product is folded in -- ExecuteOperator applies
        // the state change only after the sink callback returns (verified
        // directly against ContentStreamWalker's dispatch order), so a
        // second "cm" must show the identity, not its own product.
        var bytes = Encoding.Latin1.GetBytes("2 0 0 3 10 20 cm 1 0 0 1 5 5 cm");

        var ops = new ContentStreamParser(bytes).Parse().Operators
            .Where(op => op.Name == "cm").ToList();

        ops[0].GraphicsTransform.Should().Be(new ContentTransform(1, 0, 0, 1, 0, 0),
            "the first cm executes against the identity CTM");
        ops[1].GraphicsTransform.Should().Be(new ContentTransform(2, 0, 0, 3, 10, 20),
            "the second cm's snapshot is the first cm's product, not its own");
    }

    [Fact]
    public void DefaultMode_ComputesMetadata_RedactionContractUnchanged()
    {
        // Redaction and extraction construct the parser without touching
        // ComputeOperatorMetadata; the default must stay ON.
        new ContentStreamParser(Array.Empty<byte>()).ComputeOperatorMetadata.Should().BeTrue();
    }
}
