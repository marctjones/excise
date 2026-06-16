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
        var adaptiveTemplatePixels = Jbig2SegmentBodyReader.ReadAdaptiveTemplatePixels(
            data,
            MinimumByteLength,
            adaptiveTemplatePixelCount,
            "generic region adaptive template pixels");

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

internal readonly record struct Jbig2SymbolDictionarySegment(
    bool IsHuffmanEncoded,
    bool UseRefinementAggregation,
    int SdHuffDecodeHeightSelection,
    int SdHuffDecodeWidthSelection,
    int SdHuffBmSizeSelection,
    int SdHuffAggInstanceSelection,
    bool IsCodingContextUsed,
    bool IsCodingContextRetained,
    int SdTemplate,
    int SdrTemplate,
    Jbig2AdaptiveTemplatePixel[] AdaptiveTemplatePixels,
    Jbig2AdaptiveTemplatePixel[] RefinementAdaptiveTemplatePixels,
    uint ExportedSymbolCount,
    uint NewSymbolCount,
    int PayloadDataOffset,
    int PayloadDataLength)
{
    public const int FlagsByteLength = 2;

    public static Jbig2SymbolDictionarySegment Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < FlagsByteLength)
            throw new InvalidOperationException("Truncated JBIG2 symbol dictionary segment");

        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(data);
        bool isHuffmanEncoded = (flags & 0x0001) != 0;
        bool useRefinementAggregation = (flags & 0x0002) != 0;
        int sdHuffDecodeHeightSelection = (flags >> 2) & 0x03;
        int sdHuffDecodeWidthSelection = (flags >> 4) & 0x03;
        int sdHuffBmSizeSelection = (flags >> 6) & 0x01;
        int sdHuffAggInstanceSelection = (flags >> 7) & 0x01;
        bool isCodingContextUsed = (flags & 0x0100) != 0;
        bool isCodingContextRetained = (flags & 0x0200) != 0;
        int sdTemplate = (flags >> 10) & 0x03;
        int sdrTemplate = (flags >> 12) & 0x01;

        int offset = FlagsByteLength;
        int adaptiveTemplatePixelCount = !isHuffmanEncoded
            ? sdTemplate == 0 ? 4 : 1
            : 0;
        var adaptiveTemplatePixels = Jbig2SegmentBodyReader.ReadAdaptiveTemplatePixels(
            data,
            offset,
            adaptiveTemplatePixelCount,
            "symbol dictionary adaptive template pixels");
        offset += adaptiveTemplatePixelCount * 2;

        int refinementAdaptiveTemplatePixelCount = useRefinementAggregation && sdrTemplate == 0 ? 2 : 0;
        var refinementAdaptiveTemplatePixels = Jbig2SegmentBodyReader.ReadAdaptiveTemplatePixels(
            data,
            offset,
            refinementAdaptiveTemplatePixelCount,
            "symbol dictionary refinement adaptive template pixels");
        offset += refinementAdaptiveTemplatePixelCount * 2;

        if (data.Length < offset + 8)
            throw new InvalidOperationException("Truncated JBIG2 symbol dictionary symbol counts");

        uint exportedSymbolCount = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        uint newSymbolCount = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 4, 4));
        offset += 8;

        return new Jbig2SymbolDictionarySegment(
            isHuffmanEncoded,
            useRefinementAggregation,
            sdHuffDecodeHeightSelection,
            sdHuffDecodeWidthSelection,
            sdHuffBmSizeSelection,
            sdHuffAggInstanceSelection,
            isCodingContextUsed,
            isCodingContextRetained,
            sdTemplate,
            sdrTemplate,
            adaptiveTemplatePixels,
            refinementAdaptiveTemplatePixels,
            exportedSymbolCount,
            newSymbolCount,
            offset,
            data.Length - offset);
    }
}

internal readonly record struct Jbig2TextRegionHuffmanFlags(
    int SbHuffRSize,
    int SbHuffRdy,
    int SbHuffRdx,
    int SbHuffRdHeight,
    int SbHuffRdWidth,
    int SbHuffDt,
    int SbHuffDs,
    int SbHuffFs)
{
    public static Jbig2TextRegionHuffmanFlags Parse(ushort flags)
        => new(
            (flags >> 14) & 0x01,
            (flags >> 12) & 0x03,
            (flags >> 10) & 0x03,
            (flags >> 8) & 0x03,
            (flags >> 6) & 0x03,
            (flags >> 4) & 0x03,
            (flags >> 2) & 0x03,
            flags & 0x03);
}

internal readonly record struct Jbig2TextRegionSegment(
    Jbig2RegionSegmentInformation Region,
    bool IsHuffmanEncoded,
    bool UseRefinement,
    int LogSbStrips,
    int ReferenceCorner,
    bool IsTransposed,
    Jbig2CombinationOperator CombinationOperator,
    byte DefaultPixel,
    int SbDsOffset,
    int SbrTemplate,
    Jbig2TextRegionHuffmanFlags? HuffmanFlags,
    Jbig2AdaptiveTemplatePixel[] RefinementAdaptiveTemplatePixels,
    uint DeclaredSymbolInstanceCount,
    uint SymbolInstanceCount,
    int PayloadDataOffset,
    int PayloadDataLength)
{
    public const int FlagsOffset = Jbig2RegionSegmentInformation.ByteLength;
    public const int MinimumByteLength = FlagsOffset + 2 + 4;

    public static Jbig2TextRegionSegment Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < FlagsOffset + 2)
            throw new InvalidOperationException("Truncated JBIG2 text region segment");

        var region = Jbig2RegionSegmentInformation.Parse(data);
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(FlagsOffset, 2));
        bool isHuffmanEncoded = (flags & 0x0001) != 0;
        bool useRefinement = (flags & 0x0002) != 0;
        int logSbStrips = (flags >> 2) & 0x03;
        int referenceCorner = (flags >> 4) & 0x03;
        bool isTransposed = (flags & 0x0040) != 0;
        var combinationOperator = Jbig2CombinationOperatorParser.FromCode((flags >> 7) & 0x03);
        byte defaultPixel = (byte)((flags >> 9) & 0x01);
        int sbDsOffset = (flags >> 10) & 0x1F;
        if (sbDsOffset > 0x0F)
            sbDsOffset -= 0x20;
        int sbrTemplate = (flags >> 15) & 0x01;

        int offset = FlagsOffset + 2;
        Jbig2TextRegionHuffmanFlags? huffmanFlags = null;
        if (isHuffmanEncoded)
        {
            if (data.Length < offset + 2)
                throw new InvalidOperationException("Truncated JBIG2 text region Huffman flags");

            huffmanFlags = Jbig2TextRegionHuffmanFlags.Parse(
                BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2)));
            offset += 2;
        }

        int refinementAdaptiveTemplatePixelCount = useRefinement && sbrTemplate == 0 ? 2 : 0;
        var refinementAdaptiveTemplatePixels = Jbig2SegmentBodyReader.ReadAdaptiveTemplatePixels(
            data,
            offset,
            refinementAdaptiveTemplatePixelCount,
            "text region refinement adaptive template pixels");
        offset += refinementAdaptiveTemplatePixelCount * 2;

        if (data.Length < offset + 4)
            throw new InvalidOperationException("Truncated JBIG2 text region symbol instance count");

        uint declaredSymbolInstanceCount = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        uint symbolInstanceCount = declaredSymbolInstanceCount;
        ulong pixels = (ulong)region.BitmapWidth * region.BitmapHeight;
        if ((ulong)symbolInstanceCount > pixels)
            symbolInstanceCount = pixels > uint.MaxValue ? uint.MaxValue : (uint)pixels;
        offset += 4;

        return new Jbig2TextRegionSegment(
            region,
            isHuffmanEncoded,
            useRefinement,
            logSbStrips,
            referenceCorner,
            isTransposed,
            combinationOperator,
            defaultPixel,
            sbDsOffset,
            sbrTemplate,
            huffmanFlags,
            refinementAdaptiveTemplatePixels,
            declaredSymbolInstanceCount,
            symbolInstanceCount,
            offset,
            data.Length - offset);
    }
}

internal static class Jbig2SegmentBodyReader
{
    public static Jbig2AdaptiveTemplatePixel[] ReadAdaptiveTemplatePixels(
        ReadOnlySpan<byte> data,
        int offset,
        int count,
        string fieldName)
    {
        int byteLength = count * 2;
        if (data.Length < offset + byteLength)
            throw new InvalidOperationException($"Truncated JBIG2 {fieldName}");

        var adaptiveTemplatePixels = new Jbig2AdaptiveTemplatePixel[count];
        for (int i = 0; i < adaptiveTemplatePixels.Length; i++)
        {
            int pixelOffset = offset + (i * 2);
            adaptiveTemplatePixels[i] = new Jbig2AdaptiveTemplatePixel(
                unchecked((sbyte)data[pixelOffset]),
                unchecked((sbyte)data[pixelOffset + 1]));
        }

        return adaptiveTemplatePixels;
    }
}

internal readonly record struct Jbig2HuffmanTableSegment(
    bool HasOutOfBand,
    int PrefixSizeBits,
    int RangeSizeBits,
    int LowValue,
    int HighValue,
    int PayloadDataOffset,
    int PayloadDataLength)
{
    public const int ByteLength = 9;

    public static Jbig2HuffmanTableSegment Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < ByteLength)
            throw new InvalidOperationException("Truncated JBIG2 Huffman table segment");

        byte flags = data[0];
        if ((flags & 0x80) != 0)
            throw new InvalidOperationException("Invalid JBIG2 Huffman table flags: reserved bit 7 must be zero");

        int rangeSizeBits = ((flags >> 4) & 0x07) + 1;
        int prefixSizeBits = ((flags >> 1) & 0x07) + 1;
        bool hasOutOfBand = (flags & 0x01) != 0;
        int lowValue = BinaryPrimitives.ReadInt32BigEndian(data.Slice(1, 4));
        int highValue = BinaryPrimitives.ReadInt32BigEndian(data.Slice(5, 4));
        if (highValue < lowValue)
            throw new InvalidOperationException("Invalid JBIG2 Huffman table segment: high value is below low value");

        return new Jbig2HuffmanTableSegment(
            hasOutOfBand,
            prefixSizeBits,
            rangeSizeBits,
            lowValue,
            highValue,
            ByteLength,
            data.Length - ByteLength);
    }
}

internal readonly record struct Jbig2GenericRefinementRegionSegment(
    Jbig2RegionSegmentInformation Region,
    bool TypicalPredictionGenericRefinementOn,
    int Template,
    Jbig2AdaptiveTemplatePixel[] AdaptiveTemplatePixels,
    int BitmapDataOffset,
    int BitmapDataLength)
{
    public const int FlagsOffset = Jbig2RegionSegmentInformation.ByteLength;
    public const int MinimumByteLength = FlagsOffset + 1;

    public static Jbig2GenericRefinementRegionSegment Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < MinimumByteLength)
            throw new InvalidOperationException("Truncated JBIG2 generic refinement region segment");

        var region = Jbig2RegionSegmentInformation.Parse(data);
        byte flags = data[FlagsOffset];
        bool typicalPrediction = (flags & 0x02) != 0;
        int template = flags & 0x01;

        int adaptiveTemplatePixelCount = template == 0 ? 2 : 0;
        int bitmapDataOffset = MinimumByteLength + (adaptiveTemplatePixelCount * 2);
        var adaptiveTemplatePixels = Jbig2SegmentBodyReader.ReadAdaptiveTemplatePixels(
            data,
            MinimumByteLength,
            adaptiveTemplatePixelCount,
            "generic refinement adaptive template pixels");

        return new Jbig2GenericRefinementRegionSegment(
            region,
            typicalPrediction,
            template,
            adaptiveTemplatePixels,
            bitmapDataOffset,
            data.Length - bitmapDataOffset);
    }
}

internal readonly record struct Jbig2PatternDictionarySegment(
    bool IsMmrEncoded,
    int Template,
    byte PatternWidth,
    byte PatternHeight,
    uint GrayMax,
    int BitmapDataOffset,
    int BitmapDataLength)
{
    public const int ByteLength = 7;

    public static Jbig2PatternDictionarySegment Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < ByteLength)
            throw new InvalidOperationException("Truncated JBIG2 pattern dictionary segment");

        byte flags = data[0];
        bool isMmrEncoded = (flags & 0x01) != 0;
        int template = (flags >> 1) & 0x03;
        byte patternWidth = data[1];
        byte patternHeight = data[2];
        if (patternWidth == 0 || patternHeight == 0)
            throw new InvalidOperationException("Invalid JBIG2 pattern dictionary dimensions");

        uint grayMax = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(3, 4));

        return new Jbig2PatternDictionarySegment(
            isMmrEncoded,
            template,
            patternWidth,
            patternHeight,
            grayMax,
            ByteLength,
            data.Length - ByteLength);
    }
}

internal readonly record struct Jbig2HalftoneRegionSegment(
    Jbig2RegionSegmentInformation Region,
    byte DefaultPixel,
    Jbig2CombinationOperator CombinationOperator,
    bool SkipEnabled,
    int Template,
    bool IsMmrEncoded,
    uint GridWidth,
    uint GridHeight,
    int GridX,
    int GridY,
    ushort RegionX,
    ushort RegionY,
    int BitmapDataOffset,
    int BitmapDataLength)
{
    public const int FlagsOffset = Jbig2RegionSegmentInformation.ByteLength;
    public const int ByteLength = FlagsOffset + 21;

    public static Jbig2HalftoneRegionSegment Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < ByteLength)
            throw new InvalidOperationException("Truncated JBIG2 halftone region segment");

        var region = Jbig2RegionSegmentInformation.Parse(data);
        byte flags = data[FlagsOffset];
        byte defaultPixel = (byte)((flags >> 7) & 0x01);
        var combinationOperator = Jbig2CombinationOperatorParser.FromCode((flags >> 4) & 0x07);
        bool skipEnabled = (flags & 0x08) != 0;
        int template = (flags >> 1) & 0x03;
        bool isMmrEncoded = (flags & 0x01) != 0;
        int offset = FlagsOffset + 1;

        uint gridWidth = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        uint gridHeight = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 4, 4));
        int gridX = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset + 8, 4));
        int gridY = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset + 12, 4));
        ushort regionX = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 16, 2));
        ushort regionY = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 18, 2));

        return new Jbig2HalftoneRegionSegment(
            region,
            defaultPixel,
            combinationOperator,
            skipEnabled,
            template,
            isMmrEncoded,
            gridWidth,
            gridHeight,
            gridX,
            gridY,
            regionX,
            regionY,
            ByteLength,
            data.Length - ByteLength);
    }
}
