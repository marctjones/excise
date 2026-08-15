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
    public void DefaultMode_ComputesMetadata_RedactionContractUnchanged()
    {
        // Redaction and extraction construct the parser without touching
        // ComputeOperatorMetadata; the default must stay ON.
        new ContentStreamParser(Array.Empty<byte>()).ComputeOperatorMetadata.Should().BeTrue();
    }
}
