using AwesomeAssertions;
using Pdfe.Core.Filters.Jbig2;
using Xunit;

namespace Pdfe.Core.Tests.Filters.Jbig2;

public class Jbig2SegmentBodyTests
{
    private static byte[] BuildRegionSegmentPrefix(uint width = 12, uint height = 34, uint x = 56, uint y = 78, byte flags = 0)
    {
        byte[] data = new byte[Jbig2RegionSegmentInformation.ByteLength];
        WriteUInt32(data, 0, width);
        WriteUInt32(data, 4, height);
        WriteUInt32(data, 8, x);
        WriteUInt32(data, 12, y);
        data[16] = flags;
        return data;
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

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

    [Fact]
    public void GenericRegionSegment_Parse_Template0ReadsFourAdaptiveTemplatePixels()
    {
        var data = new List<byte>();
        data.AddRange(BuildRegionSegmentPrefix());
        data.Add(0x00);
        data.AddRange(new byte[]
        {
            0x03, 0xFF,
            0xFD, 0xFF,
            0x02, 0xFE,
            0xFE, 0xFE,
            0xAA, 0xBB, 0xCC,
        });

        var segment = Jbig2GenericRegionSegment.Parse(data.ToArray());

        segment.Region.BitmapWidth.Should().Be(12u);
        segment.Region.BitmapHeight.Should().Be(34u);
        segment.Region.XLocation.Should().Be(56u);
        segment.Region.YLocation.Should().Be(78u);
        segment.Template.Should().Be(0);
        segment.UsesExtendedTemplates.Should().BeFalse();
        segment.TypicalPredictionGenericDecodingOn.Should().BeFalse();
        segment.IsMmrEncoded.Should().BeFalse();
        segment.AdaptiveTemplatePixels.Should().Equal(
            new Jbig2AdaptiveTemplatePixel(3, -1),
            new Jbig2AdaptiveTemplatePixel(-3, -1),
            new Jbig2AdaptiveTemplatePixel(2, -2),
            new Jbig2AdaptiveTemplatePixel(-2, -2));
        segment.BitmapDataOffset.Should().Be(26);
        segment.BitmapDataLength.Should().Be(3);
    }

    [Fact]
    public void GenericRegionSegment_Parse_Template0ExtendedTemplatesReadsTwelveAdaptiveTemplatePixels()
    {
        var data = new List<byte>();
        data.AddRange(BuildRegionSegmentPrefix());
        data.Add(0x10);
        for (int i = 0; i < 12; i++)
        {
            data.Add((byte)i);
            data.Add(unchecked((byte)-i));
        }
        data.Add(0xAA);

        var segment = Jbig2GenericRegionSegment.Parse(data.ToArray());

        segment.Template.Should().Be(0);
        segment.UsesExtendedTemplates.Should().BeTrue();
        segment.AdaptiveTemplatePixels.Should().HaveCount(12);
        segment.AdaptiveTemplatePixels[11].Should().Be(new Jbig2AdaptiveTemplatePixel(11, -11));
        segment.BitmapDataOffset.Should().Be(42);
        segment.BitmapDataLength.Should().Be(1);
    }

    [Fact]
    public void GenericRegionSegment_Parse_NonZeroTemplateReadsOneAdaptiveTemplatePixel()
    {
        var data = new List<byte>();
        data.AddRange(BuildRegionSegmentPrefix());
        data.Add(0x0A); // Template 1 with typical prediction enabled.
        data.AddRange(new byte[] { 0x02, 0xFF, 0xAA });

        var segment = Jbig2GenericRegionSegment.Parse(data.ToArray());

        segment.Template.Should().Be(1);
        segment.TypicalPredictionGenericDecodingOn.Should().BeTrue();
        segment.AdaptiveTemplatePixels.Should().Equal(new Jbig2AdaptiveTemplatePixel(2, -1));
        segment.BitmapDataOffset.Should().Be(20);
        segment.BitmapDataLength.Should().Be(1);
    }

    [Fact]
    public void GenericRegionSegment_Parse_MmrEncodedDataHasNoAdaptiveTemplatePixels()
    {
        var data = new List<byte>();
        data.AddRange(BuildRegionSegmentPrefix(flags: 0x02));
        data.Add(0x13); // Template 1 + MMR + extended-template bit, but MMR has no AT pixels.
        data.AddRange(new byte[] { 0xAA, 0xBB });

        var segment = Jbig2GenericRegionSegment.Parse(data.ToArray());

        segment.Region.CombinationOperator.Should().Be(Jbig2CombinationOperator.Xor);
        segment.Template.Should().Be(1);
        segment.UsesExtendedTemplates.Should().BeTrue();
        segment.IsMmrEncoded.Should().BeTrue();
        segment.AdaptiveTemplatePixels.Should().BeEmpty();
        segment.BitmapDataOffset.Should().Be(18);
        segment.BitmapDataLength.Should().Be(2);
    }

    [Fact]
    public void GenericRegionSegment_Parse_WithTruncatedFlags_Throws()
    {
        var act = () => Jbig2GenericRegionSegment.Parse(new byte[Jbig2GenericRegionSegment.MinimumByteLength - 1]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*generic region segment*");
    }

    [Fact]
    public void GenericRegionSegment_Parse_WithTruncatedAdaptiveTemplatePixels_Throws()
    {
        var data = new List<byte>();
        data.AddRange(BuildRegionSegmentPrefix());
        data.Add(0x00);
        data.AddRange(new byte[7]);

        var act = () => Jbig2GenericRegionSegment.Parse(data.ToArray());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*adaptive template*");
    }
}
