using AwesomeAssertions;
using Pdfe.Core.Filters.Jbig2;
using Xunit;

namespace Pdfe.Core.Tests.Filters.Jbig2;

public class Jbig2SegmentBodyTests
{
    [Fact]
    public void PageInformation_Parse_ReadsDimensionsResolutionFlagsAndStriping()
    {
        byte[] data =
        {
            0, 0, 0, 100,
            0, 0, 0, 200,
            0, 0, 1, 44,
            0, 0, 1, 144,
            0x77,
            0x80, 0x0A,
        };

        var page = Jbig2PageInformation.Parse(data);

        page.Width.Should().Be(100u);
        page.Height.Should().Be(200u);
        page.ResolutionX.Should().Be(300u);
        page.ResolutionY.Should().Be(400u);
        page.CombinationOperatorOverrideAllowed.Should().BeTrue();
        page.RequiresAuxiliaryBuffer.Should().BeTrue();
        page.CombinationOperator.Should().Be(Jbig2CombinationOperator.Xor);
        page.DefaultPixelValue.Should().Be(1);
        page.MightContainRefinements.Should().BeTrue();
        page.IsLossless.Should().BeTrue();
        page.IsStriped.Should().BeTrue();
        page.MaxStripeSize.Should().Be(10);
    }

    [Fact]
    public void PageInformation_Parse_WithZeroFlags_UsesOrAndUnstripedDefaults()
    {
        byte[] data = new byte[Jbig2PageInformation.ByteLength];

        var page = Jbig2PageInformation.Parse(data);

        page.CombinationOperatorOverrideAllowed.Should().BeFalse();
        page.RequiresAuxiliaryBuffer.Should().BeFalse();
        page.CombinationOperator.Should().Be(Jbig2CombinationOperator.Or);
        page.DefaultPixelValue.Should().Be(0);
        page.MightContainRefinements.Should().BeFalse();
        page.IsLossless.Should().BeFalse();
        page.IsStriped.Should().BeFalse();
        page.MaxStripeSize.Should().Be(0);
    }

    [Fact]
    public void PageInformation_Parse_WithTruncatedData_Throws()
    {
        var act = () => Jbig2PageInformation.Parse(new byte[Jbig2PageInformation.ByteLength - 1]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*page information*");
    }

    [Fact]
    public void RegionSegmentInformation_Parse_ReadsCoordinatesAndCombinationOperator()
    {
        byte[] data =
        {
            0, 0, 0, 12,
            0, 0, 0, 34,
            0, 0, 0, 56,
            0, 0, 0, 78,
            0x03,
        };

        var region = Jbig2RegionSegmentInformation.Parse(data);

        region.BitmapWidth.Should().Be(12u);
        region.BitmapHeight.Should().Be(34u);
        region.XLocation.Should().Be(56u);
        region.YLocation.Should().Be(78u);
        region.CombinationOperator.Should().Be(Jbig2CombinationOperator.Xnor);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(7, 4)]
    public void RegionSegmentInformation_Parse_MapsCombinationOperatorCodes(int code, int expected)
    {
        byte[] data = new byte[Jbig2RegionSegmentInformation.ByteLength];
        data[16] = (byte)code;

        var region = Jbig2RegionSegmentInformation.Parse(data);

        region.CombinationOperator.Should().Be((Jbig2CombinationOperator)expected);
    }

    [Fact]
    public void RegionSegmentInformation_Parse_WithTruncatedData_Throws()
    {
        var act = () => Jbig2RegionSegmentInformation.Parse(new byte[Jbig2RegionSegmentInformation.ByteLength - 1]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*region segment information*");
    }
}
