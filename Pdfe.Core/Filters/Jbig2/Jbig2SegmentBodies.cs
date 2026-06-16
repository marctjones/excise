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
