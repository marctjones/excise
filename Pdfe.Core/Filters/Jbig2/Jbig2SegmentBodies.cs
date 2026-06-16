using System;
using System.Buffers.Binary;

namespace Pdfe.Core.Filters.Jbig2;

internal enum Jbig2CombinationOperator
{
    Or,
    And,
    Xor,
    Xnor,
    Replace,
}

internal static class Jbig2CombinationOperatorParser
{
    public static Jbig2CombinationOperator FromCode(int code)
        => code switch
        {
            0 => Jbig2CombinationOperator.Or,
            1 => Jbig2CombinationOperator.And,
            2 => Jbig2CombinationOperator.Xor,
            3 => Jbig2CombinationOperator.Xnor,
            _ => Jbig2CombinationOperator.Replace,
        };
}

internal readonly record struct Jbig2PageInformation(
    uint Width,
    uint Height,
    uint ResolutionX,
    uint ResolutionY,
    bool CombinationOperatorOverrideAllowed,
    bool RequiresAuxiliaryBuffer,
    Jbig2CombinationOperator CombinationOperator,
    byte DefaultPixelValue,
    bool MightContainRefinements,
    bool IsLossless,
    bool IsStriped,
    ushort MaxStripeSize)
{
    public const int ByteLength = 19;

    public static Jbig2PageInformation Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < ByteLength)
            throw new InvalidOperationException("Truncated JBIG2 page information segment");

        byte flags = data[16];
        ushort striping = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(17, 2));

        return new Jbig2PageInformation(
            BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(data.Slice(12, 4)),
            (flags & 0x40) != 0,
            (flags & 0x20) != 0,
            Jbig2CombinationOperatorParser.FromCode((flags >> 3) & 0x03),
            (byte)((flags >> 2) & 0x01),
            (flags & 0x02) != 0,
            (flags & 0x01) != 0,
            (striping & 0x8000) != 0,
            (ushort)(striping & 0x7FFF));
    }
}

internal readonly record struct Jbig2RegionSegmentInformation(
    uint BitmapWidth,
    uint BitmapHeight,
    uint XLocation,
    uint YLocation,
    Jbig2CombinationOperator CombinationOperator)
{
    public const int ByteLength = 17;

    public static Jbig2RegionSegmentInformation Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < ByteLength)
            throw new InvalidOperationException("Truncated JBIG2 region segment information field");

        return new Jbig2RegionSegmentInformation(
            BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(data.Slice(12, 4)),
            Jbig2CombinationOperatorParser.FromCode(data[16] & 0x07));
    }
}

internal readonly record struct Jbig2AdaptiveTemplatePixel(sbyte X, sbyte Y);

internal readonly record struct Jbig2GenericRegionSegment(
    Jbig2RegionSegmentInformation Region,
    bool UsesExtendedTemplates,
    bool TypicalPredictionGenericDecodingOn,
    int Template,
    bool IsMmrEncoded,
    Jbig2AdaptiveTemplatePixel[] AdaptiveTemplatePixels,
    int BitmapDataOffset,
    int BitmapDataLength)
{
    public const int FlagsOffset = Jbig2RegionSegmentInformation.ByteLength;
    public const int MinimumByteLength = FlagsOffset + 1;

    public static Jbig2GenericRegionSegment Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < MinimumByteLength)
            throw new InvalidOperationException("Truncated JBIG2 generic region segment");

        var region = Jbig2RegionSegmentInformation.Parse(data);
        byte flags = data[FlagsOffset];
        bool usesExtendedTemplates = (flags & 0x10) != 0;
        bool typicalPredictionGenericDecodingOn = (flags & 0x08) != 0;
        int template = (flags >> 1) & 0x03;
        bool isMmrEncoded = (flags & 0x01) != 0;

        int adaptiveTemplatePixelCount = GetAdaptiveTemplatePixelCount(template, usesExtendedTemplates, isMmrEncoded);
        int bitmapDataOffset = MinimumByteLength + (adaptiveTemplatePixelCount * 2);
        if (data.Length < bitmapDataOffset)
            throw new InvalidOperationException("Truncated JBIG2 generic region adaptive template pixels");

        var adaptiveTemplatePixels = new Jbig2AdaptiveTemplatePixel[adaptiveTemplatePixelCount];
        for (int i = 0; i < adaptiveTemplatePixels.Length; i++)
        {
            int offset = MinimumByteLength + (i * 2);
            adaptiveTemplatePixels[i] = new Jbig2AdaptiveTemplatePixel(
                unchecked((sbyte)data[offset]),
                unchecked((sbyte)data[offset + 1]));
        }

        return new Jbig2GenericRegionSegment(
            region,
            usesExtendedTemplates,
            typicalPredictionGenericDecodingOn,
            template,
            isMmrEncoded,
            adaptiveTemplatePixels,
            bitmapDataOffset,
            data.Length - bitmapDataOffset);
    }

    private static int GetAdaptiveTemplatePixelCount(int template, bool usesExtendedTemplates, bool isMmrEncoded)
    {
        if (isMmrEncoded)
            return 0;

        return template == 0
            ? usesExtendedTemplates ? 12 : 4
            : 1;
    }
}
