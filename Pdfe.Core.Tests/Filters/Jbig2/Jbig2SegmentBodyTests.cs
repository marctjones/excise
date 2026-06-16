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

    private static void WriteUInt16(List<byte> data, ushort value)
    {
        data.Add((byte)(value >> 8));
        data.Add((byte)value);
    }

    private static void WriteUInt32(List<byte> data, uint value)
    {
        data.Add((byte)(value >> 24));
        data.Add((byte)(value >> 16));
        data.Add((byte)(value >> 8));
        data.Add((byte)value);
    }

    private static void WriteInt32(List<byte> data, int value)
    {
        data.Add((byte)(value >> 24));
        data.Add((byte)(value >> 16));
        data.Add((byte)(value >> 8));
        data.Add((byte)value);
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

    [Fact]
    public void SymbolDictionarySegment_Parse_ArithmeticTemplate0ReadsAtPixelsRefinementPixelsAndCounts()
    {
        var data = new List<byte>();
        WriteUInt16(data, 0x03EE);
        data.AddRange(new byte[]
        {
            0x03, 0xFF,
            0xFD, 0xFF,
            0x02, 0xFE,
            0xFE, 0xFE,
            0x01, 0xFF,
            0xFF, 0x01,
        });
        WriteUInt32(data, 5);
        WriteUInt32(data, 7);
        data.AddRange(new byte[] { 0xAA, 0xBB });

        var segment = Jbig2SymbolDictionarySegment.Parse(data.ToArray());

        segment.IsHuffmanEncoded.Should().BeFalse();
        segment.UseRefinementAggregation.Should().BeTrue();
        segment.SdHuffDecodeHeightSelection.Should().Be(3);
        segment.SdHuffDecodeWidthSelection.Should().Be(2);
        segment.SdHuffBmSizeSelection.Should().Be(1);
        segment.SdHuffAggInstanceSelection.Should().Be(1);
        segment.IsCodingContextUsed.Should().BeTrue();
        segment.IsCodingContextRetained.Should().BeTrue();
        segment.SdTemplate.Should().Be(0);
        segment.SdrTemplate.Should().Be(0);
        segment.AdaptiveTemplatePixels.Should().Equal(
            new Jbig2AdaptiveTemplatePixel(3, -1),
            new Jbig2AdaptiveTemplatePixel(-3, -1),
            new Jbig2AdaptiveTemplatePixel(2, -2),
            new Jbig2AdaptiveTemplatePixel(-2, -2));
        segment.RefinementAdaptiveTemplatePixels.Should().Equal(
            new Jbig2AdaptiveTemplatePixel(1, -1),
            new Jbig2AdaptiveTemplatePixel(-1, 1));
        segment.ExportedSymbolCount.Should().Be(5);
        segment.NewSymbolCount.Should().Be(7);
        segment.PayloadDataOffset.Should().Be(22);
        segment.PayloadDataLength.Should().Be(2);
    }

    [Fact]
    public void SymbolDictionarySegment_Parse_HuffmanEncodedSegmentSkipsAtPixels()
    {
        var data = new List<byte>();
        WriteUInt16(data, 0x1841);
        WriteUInt32(data, 1);
        WriteUInt32(data, 2);

        var segment = Jbig2SymbolDictionarySegment.Parse(data.ToArray());

        segment.IsHuffmanEncoded.Should().BeTrue();
        segment.UseRefinementAggregation.Should().BeFalse();
        segment.SdHuffBmSizeSelection.Should().Be(1);
        segment.SdTemplate.Should().Be(2);
        segment.SdrTemplate.Should().Be(1);
        segment.AdaptiveTemplatePixels.Should().BeEmpty();
        segment.RefinementAdaptiveTemplatePixels.Should().BeEmpty();
        segment.ExportedSymbolCount.Should().Be(1);
        segment.NewSymbolCount.Should().Be(2);
        segment.PayloadDataOffset.Should().Be(10);
        segment.PayloadDataLength.Should().Be(0);
    }

    [Fact]
    public void SymbolDictionarySegment_Parse_WithTruncatedCounts_Throws()
    {
        var data = new List<byte>();
        WriteUInt16(data, 0x0001);
        data.AddRange(new byte[7]);

        var act = () => Jbig2SymbolDictionarySegment.Parse(data.ToArray());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*symbol dictionary symbol counts*");
    }

    [Fact]
    public void TextRegionSegment_Parse_ArithmeticRegionReadsFlagsRefinementPixelsAndCount()
    {
        var data = new List<byte>();
        data.AddRange(BuildRegionSegmentPrefix(width: 12, height: 34, x: 56, y: 78, flags: 0x02));
        WriteUInt16(data, 0x7B7A);
        data.AddRange(new byte[] { 0x01, 0xFF, 0xFF, 0x01 });
        WriteUInt32(data, 10);
        data.Add(0xAA);

        var segment = Jbig2TextRegionSegment.Parse(data.ToArray());

        segment.Region.BitmapWidth.Should().Be(12u);
        segment.Region.BitmapHeight.Should().Be(34u);
        segment.Region.CombinationOperator.Should().Be(Jbig2CombinationOperator.Xor);
        segment.IsHuffmanEncoded.Should().BeFalse();
        segment.UseRefinement.Should().BeTrue();
        segment.LogSbStrips.Should().Be(2);
        segment.ReferenceCorner.Should().Be(3);
        segment.IsTransposed.Should().BeTrue();
        segment.CombinationOperator.Should().Be(Jbig2CombinationOperator.Xor);
        segment.DefaultPixel.Should().Be(1);
        segment.SbDsOffset.Should().Be(-2);
        segment.SbrTemplate.Should().Be(0);
        segment.HuffmanFlags.Should().BeNull();
        segment.RefinementAdaptiveTemplatePixels.Should().Equal(
            new Jbig2AdaptiveTemplatePixel(1, -1),
            new Jbig2AdaptiveTemplatePixel(-1, 1));
        segment.DeclaredSymbolInstanceCount.Should().Be(10);
        segment.SymbolInstanceCount.Should().Be(10);
        segment.PayloadDataOffset.Should().Be(27);
        segment.PayloadDataLength.Should().Be(1);
    }

    [Fact]
    public void TextRegionSegment_Parse_HuffmanFlagsReadsAllSelections()
    {
        var data = new List<byte>();
        data.AddRange(BuildRegionSegmentPrefix(width: 8, height: 8));
        WriteUInt16(data, 0x8401);
        WriteUInt16(data, 0x6DB6);
        WriteUInt32(data, 3);

        var segment = Jbig2TextRegionSegment.Parse(data.ToArray());

        segment.IsHuffmanEncoded.Should().BeTrue();
        segment.SbrTemplate.Should().Be(1);
        segment.SbDsOffset.Should().Be(1);
        segment.HuffmanFlags.Should().NotBeNull();
        segment.HuffmanFlags!.Value.SbHuffRSize.Should().Be(1);
        segment.HuffmanFlags!.Value.SbHuffRdy.Should().Be(2);
        segment.HuffmanFlags!.Value.SbHuffRdx.Should().Be(3);
        segment.HuffmanFlags!.Value.SbHuffRdHeight.Should().Be(1);
        segment.HuffmanFlags!.Value.SbHuffRdWidth.Should().Be(2);
        segment.HuffmanFlags!.Value.SbHuffDt.Should().Be(3);
        segment.HuffmanFlags!.Value.SbHuffDs.Should().Be(1);
        segment.HuffmanFlags!.Value.SbHuffFs.Should().Be(2);
        segment.PayloadDataOffset.Should().Be(25);
        segment.PayloadDataLength.Should().Be(0);
    }

    [Fact]
    public void TextRegionSegment_Parse_CapsSymbolInstancesToRegionPixels()
    {
        var data = new List<byte>();
        data.AddRange(BuildRegionSegmentPrefix(width: 2, height: 3));
        WriteUInt16(data, 0x0000);
        WriteUInt32(data, 100);

        var segment = Jbig2TextRegionSegment.Parse(data.ToArray());

        segment.DeclaredSymbolInstanceCount.Should().Be(100);
        segment.SymbolInstanceCount.Should().Be(6);
    }

    [Fact]
    public void TextRegionSegment_Parse_WithTruncatedRefinementPixels_Throws()
    {
        var data = new List<byte>();
        data.AddRange(BuildRegionSegmentPrefix());
        WriteUInt16(data, 0x0002);
        data.AddRange(new byte[3]);

        var act = () => Jbig2TextRegionSegment.Parse(data.ToArray());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*text region refinement adaptive template pixels*");
    }

    [Fact]
    public void HuffmanTableSegment_Parse_ReadsFlagsBoundsAndPayload()
    {
        byte[] data =
        {
            0x15,
            0xFF, 0xFF, 0xFF, 0xFE,
            0x00, 0x00, 0x00, 0x0A,
            0xAA, 0xBB,
        };

        var segment = Jbig2HuffmanTableSegment.Parse(data);

        segment.HasOutOfBand.Should().BeTrue();
        segment.PrefixSizeBits.Should().Be(3);
        segment.RangeSizeBits.Should().Be(2);
        segment.LowValue.Should().Be(-2);
        segment.HighValue.Should().Be(10);
        segment.PayloadDataOffset.Should().Be(9);
        segment.PayloadDataLength.Should().Be(2);
    }

    [Fact]
    public void HuffmanTableSegment_Parse_WithReservedFlagBit_Throws()
    {
        byte[] data = new byte[Jbig2HuffmanTableSegment.ByteLength];
        data[0] = 0x80;

        var act = () => Jbig2HuffmanTableSegment.Parse(data);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*reserved bit 7*");
    }

    [Fact]
    public void GenericRefinementRegionSegment_Parse_ReadsFlagsAtPixelsAndPayloadOffset()
    {
        var data = new List<byte>();
        data.AddRange(BuildRegionSegmentPrefix(width: 9, height: 10, x: 11, y: 12, flags: 0x04));
        data.Add(0x02);
        data.AddRange(new byte[] { 0x01, 0xFF, 0xFE, 0x02, 0xAA });

        var segment = Jbig2GenericRefinementRegionSegment.Parse(data.ToArray());

        segment.Region.BitmapWidth.Should().Be(9);
        segment.Region.BitmapHeight.Should().Be(10);
        segment.Region.CombinationOperator.Should().Be(Jbig2CombinationOperator.Replace);
        segment.TypicalPredictionGenericRefinementOn.Should().BeTrue();
        segment.Template.Should().Be(0);
        segment.AdaptiveTemplatePixels.Should().Equal(
            new Jbig2AdaptiveTemplatePixel(1, -1),
            new Jbig2AdaptiveTemplatePixel(-2, 2));
        segment.BitmapDataOffset.Should().Be(22);
        segment.BitmapDataLength.Should().Be(1);
    }

    [Fact]
    public void PatternDictionarySegment_Parse_ReadsFlagsDimensionsAndPayloadOffset()
    {
        var data = new List<byte> { 0x05, 0x03, 0x04 };
        WriteUInt32(data, 6);
        data.AddRange(new byte[] { 0xAA, 0xBB });

        var segment = Jbig2PatternDictionarySegment.Parse(data.ToArray());

        segment.IsMmrEncoded.Should().BeTrue();
        segment.Template.Should().Be(2);
        segment.PatternWidth.Should().Be(3);
        segment.PatternHeight.Should().Be(4);
        segment.GrayMax.Should().Be(6);
        segment.BitmapDataOffset.Should().Be(7);
        segment.BitmapDataLength.Should().Be(2);
    }

    [Fact]
    public void PatternDictionarySegment_Parse_WithZeroDimensions_Throws()
    {
        var data = new byte[Jbig2PatternDictionarySegment.ByteLength];

        var act = () => Jbig2PatternDictionarySegment.Parse(data);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*pattern dictionary dimensions*");
    }

    [Fact]
    public void HalftoneRegionSegment_Parse_ReadsRegionGridVectorAndPayloadOffset()
    {
        var data = new List<byte>();
        data.AddRange(BuildRegionSegmentPrefix(width: 20, height: 21, x: 22, y: 23));
        data.Add(0xAF);
        WriteUInt32(data, 3);
        WriteUInt32(data, 4);
        WriteInt32(data, -5);
        WriteInt32(data, 6);
        WriteUInt16(data, 7);
        WriteUInt16(data, 8);
        data.Add(0xAA);

        var segment = Jbig2HalftoneRegionSegment.Parse(data.ToArray());

        segment.Region.BitmapWidth.Should().Be(20);
        segment.Region.BitmapHeight.Should().Be(21);
        segment.DefaultPixel.Should().Be(1);
        segment.CombinationOperator.Should().Be(Jbig2CombinationOperator.Xor);
        segment.SkipEnabled.Should().BeTrue();
        segment.Template.Should().Be(3);
        segment.IsMmrEncoded.Should().BeTrue();
        segment.GridWidth.Should().Be(3);
        segment.GridHeight.Should().Be(4);
        segment.GridX.Should().Be(-5);
        segment.GridY.Should().Be(6);
        segment.RegionX.Should().Be(7);
        segment.RegionY.Should().Be(8);
        segment.BitmapDataOffset.Should().Be(38);
        segment.BitmapDataLength.Should().Be(1);
    }
}
