using AwesomeAssertions;
using Pdfe.Core.Filters.Jbig2;
using Xunit;

namespace Pdfe.Core.Tests.Filters.Jbig2;

public class Jbig2BitmapCompositorTests
{
    [Fact]
    public void Composite_WithReplace_PlacesSourceAtCoordinates()
    {
        byte[] destination = { 0x00, 0x00, 0x00 };
        byte[] source = { 0xC0, 0x40 }; // 2x2: 11 / 01

        Jbig2BitmapCompositor.Composite(
            destination,
            destinationWidth: 8,
            destinationHeight: 3,
            source,
            sourceWidth: 2,
            sourceHeight: 2,
            x: 2,
            y: 1,
            Jbig2CombinationOperator.Replace);

        destination.Should().Equal(0x00, 0x30, 0x10);
    }

    [Theory]
    [InlineData(0x80, 0, 0xC0)]
    [InlineData(0x80, 1, 0x80)]
    [InlineData(0x80, 2, 0x40)]
    [InlineData(0x80, 3, 0x80)]
    [InlineData(0x80, 4, 0xC0)]
    public void Composite_AppliesCombinationOperator(byte initial, int combinationOperator, byte expected)
    {
        byte[] destination = { initial };
        byte[] source = { 0xC0 };

        Jbig2BitmapCompositor.Composite(
            destination,
            destinationWidth: 8,
            destinationHeight: 1,
            source,
            sourceWidth: 2,
            sourceHeight: 1,
            x: 0,
            y: 0,
            (Jbig2CombinationOperator)combinationOperator);

        destination[0].Should().Be(expected);
    }

    [Fact]
    public void Composite_ClipsSourceOutsideDestination()
    {
        byte[] destination = { 0x00 };
        byte[] source = { 0xE0, 0xE0, 0xE0 };

        Jbig2BitmapCompositor.Composite(
            destination,
            destinationWidth: 3,
            destinationHeight: 1,
            source,
            sourceWidth: 3,
            sourceHeight: 3,
            x: -1,
            y: -1,
            Jbig2CombinationOperator.Replace);

        destination[0].Should().Be(0xC0);
    }
}
