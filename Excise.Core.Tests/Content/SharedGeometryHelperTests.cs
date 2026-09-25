using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Content;

/// <summary>
/// The matrix product, rectangle transform and operand reads that seven redaction
/// and content consumers each carried a private copy of (#1830 F1830c). Where the
/// copies had drifted, the surviving behaviour is the one the walker and the spec
/// have, and is pinned here.
/// </summary>
public class SharedGeometryHelperTests
{
    // §9.4.3: an operator takes the operands directly before it. The walker
    // trims a longer stack from the END; the constructor's own read took the
    // first, so a junk-prefixed `1 2 (x) Tj` reported no text.
    [Theory]
    [InlineData("Tj")]
    [InlineData("'")]
    public void ShowString_ReadsTheOperandNearestTheOperator(string name)
    {
        var op = new ContentOperator(name,
            new PdfObject[] { new PdfInteger(1), new PdfInteger(2), new PdfString("x") });

        op.TextContent.Should().Be("x");
    }

    [Fact]
    public void ShowStringWithSpacing_ReadsTheLastOperand()
    {
        var op = new ContentOperator("\"",
            new PdfObject[] { new PdfInteger(9), new PdfInteger(1), new PdfInteger(2), new PdfString("x") });

        op.TextContent.Should().Be("x");
    }

    [Fact]
    public void ShowStringWithSpacing_NeedsAllThreeOperands()
    {
        // The walker draws nothing for a `"` with fewer than three, so it has no text.
        new ContentOperator("\"", new PdfObject[] { new PdfInteger(1), new PdfString("x") })
            .TextContent.Should().BeNull();
    }

    [Fact]
    public void ShowArray_ConcatenatesTheStringsOfTheLastOperandAndIgnoresAdjustments()
    {
        var op = new ContentOperator("TJ", new PdfObject[]
        {
            new PdfInteger(7),
            new PdfArray(new PdfString("a"), new PdfInteger(-120), new PdfString("b")),
        });

        op.TextContent.Should().Be("ab");
    }

    [Fact]
    public void ShowArray_WithNoStrings_HasNoText()
    {
        new ContentOperator("TJ", new PdfObject[] { new PdfArray(new PdfInteger(-120)) })
            .TextContent.Should().BeNull();
    }

    [Fact]
    public void Identity_IsNotTheZeroMatrix()
    {
        ContentTransform.Identity.Should().NotBe(default(ContentTransform));
        ContentTransform.Identity.TransformPoint(3, 4).Should().Be((3.0, 4.0));
    }

    [Fact]
    public void Multiply_AgreesWithTheWalkersCtmAcrossTwoCm()
    {
        // The walker is an independent state machine, so its stamped CTM is the
        // oracle for the product every consumer used to hand-roll.
        var ops = new ContentStreamParser(
            "2 0 0 3 10 20 cm 0 1 -1 0 5 6 cm 0 0 m"u8.ToArray(), null).Parse().Operators;
        var first = ContentTransform.FromOperands(ops[0]);
        var second = ContentTransform.FromOperands(ops[1]);

        ops[2].GraphicsTransform.Should().Be(second.Multiply(first));
    }

    [Fact]
    public void TransformBounds_IsTheFourCornerExtentUnderRotation()
    {
        var turn45 = new ContentTransform(Math.Cos(Math.PI / 4), Math.Sin(Math.PI / 4),
            -Math.Sin(Math.PI / 4), Math.Cos(Math.PI / 4), 0, 0);

        var box = turn45.TransformBounds(new PdfRectangle(0, 0, 10, 10));

        box.Width.Should().BeApproximately(10 * Math.Sqrt(2), 1e-9);
        box.Height.Should().BeApproximately(10 * Math.Sqrt(2), 1e-9);
        box.Left.Should().BeApproximately(-10 / Math.Sqrt(2), 1e-9);
    }

    [Fact]
    public void TransformBounds_ReturnsANormalisedRectForAnInvertedInputAndAFlippingMatrix()
    {
        new ContentTransform(1, 0, 0, -1, 0, 100).TransformBounds(new PdfRectangle(10, 30, 0, 0))
            .Should().Be(new PdfRectangle(0, 70, 10, 100));
    }

    [Fact]
    public void UnitSquareBounds_IsWhereAnImageLands()
    {
        new ContentTransform(2, 0, 0, 3, 10, 20).UnitSquareBounds()
            .Should().Be(new PdfRectangle(10, 20, 12, 23));
    }

    [Fact]
    public void FromArray_ReadsTheFirstSixNumbers_AndIsIdentityWhenAbsentOrShort()
    {
        ContentTransform.FromArray(null).Should().Be(ContentTransform.Identity);
        ContentTransform.FromArray(new PdfArray(new PdfInteger(1), new PdfInteger(0)))
            .Should().Be(ContentTransform.Identity);
        ContentTransform.FromArray(new PdfArray(
                new PdfInteger(2), new PdfReal(0.5), new PdfName("x"), new PdfInteger(4),
                new PdfInteger(5), new PdfInteger(6), new PdfInteger(7)))
            .Should().Be(new ContentTransform(2, 0.5, 0, 4, 5, 6));
    }

    [Fact]
    public void Contains_IncludesEdges_AndNormalisesBothSides()
    {
        var area = new PdfRectangle(0, 0, 10, 10);

        area.Contains(new PdfRectangle(0, 0, 10, 10)).Should().BeTrue();
        area.Contains(new PdfRectangle(2, 8, 8, 2)).Should().BeTrue("an inverted inner rectangle is the same rectangle");
        new PdfRectangle(10, 10, 0, 0).Contains(new PdfRectangle(2, 2, 8, 8)).Should().BeTrue();
        area.Contains(new PdfRectangle(5, 5, 11, 8)).Should().BeFalse();
    }

    [Theory]
    // glyph (l, b, r, t) against the area (0,0,10,10): any, fully, centre
    [InlineData(2, 2, 8, 8, true, true, true)]      // inside
    [InlineData(6, 0, 16, 10, true, false, false)]  // straddles the right edge, centre outside
    [InlineData(4, 0, 14, 10, true, false, true)]   // straddles it, centre inside
    [InlineData(20, 0, 30, 10, false, false, false)] // apart
    [InlineData(10, 0, 20, 10, false, false, false)] // touching the edge only
    public void Selects_AppliesEachStrategysOwnRule(
        double l, double b, double r, double t, bool any, bool fully, bool centre)
    {
        var glyph = new PdfRectangle(l, b, r, t);
        var area = new PdfRectangle(0, 0, 10, 10);

        GlyphRemovalStrategy.AnyOverlap.Selects(glyph, area).Should().Be(any);
        GlyphRemovalStrategy.FullyContained.Selects(glyph, area).Should().Be(fully);
        GlyphRemovalStrategy.CenterPoint.Selects(glyph, area).Should().Be(centre);
    }

    [Fact]
    public void Selects_ReadsAnInvertedAreaAndGlyphAsTheirNormalisedSelves()
    {
        foreach (var strategy in Enum.GetValues<GlyphRemovalStrategy>())
            strategy.Selects(new PdfRectangle(8, 8, 2, 2), new PdfRectangle(10, 10, 0, 0)).Should().BeTrue();
    }
}
