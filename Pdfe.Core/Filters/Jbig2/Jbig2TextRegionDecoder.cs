using System;
using System.Collections.Generic;

namespace Pdfe.Core.Filters.Jbig2;

/// <summary>
/// Decodes JBIG2 text region segments for Huffman and arithmetic paths.
/// Text regions place symbols from referenced dictionaries onto a region bitmap.
/// Standard and referenced user Huffman tables are supported for FS/DS/DT;
/// arithmetic FS/DS/DT/IT/ID and symbol refinement procedures are supported.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class Jbig2TextRegionDecoder
{
    private const int IadtContextBase = 0;
    private const int IafsContextBase = IadtContextBase + 512;
    private const int IadsContextBase = IafsContextBase + 512;
    private const int IaitContextBase = IadsContextBase + 512;
    private const int IaidContextBase = IaitContextBase + 512;
    private const int MaxIaidContextBits = 20;
    private const int IariContextBase = IaidContextBase + (1 << MaxIaidContextBits);
    private const int IardwContextBase = IariContextBase + 512;
    private const int IardhContextBase = IardwContextBase + 512;
    private const int IardxContextBase = IardhContextBase + 512;
    private const int IardyContextBase = IardxContextBase + 512;
    private const int GenericRefinementContextBase = IardyContextBase + 512;
    internal static readonly Jbig2ArithmeticTextContextLayout DefaultArithmeticContextLayout = new(
        IadtContextBase,
        IafsContextBase,
        IadsContextBase,
        IaitContextBase,
        IaidContextBase,
        IariContextBase,
        IardwContextBase,
        IardhContextBase,
        IardxContextBase,
        IardyContextBase,
        GenericRefinementContextBase);

    public static Jbig2Bitmap Decode(
        Jbig2TextRegionSegment segment,
        ReadOnlySpan<byte> payload,
        IReadOnlyList<Jbig2Bitmap> symbols,
        IReadOnlyList<Jbig2HuffmanTable>? userTables = null)
    {
        if (segment.Region.BitmapWidth > int.MaxValue || segment.Region.BitmapHeight > int.MaxValue)
            throw new InvalidOperationException("JBIG2 text region dimensions exceed supported limits");
        if (symbols == null || symbols.Count == 0)
        {
            if (segment.SymbolInstanceCount == 0)
                return CreateDefaultRegion(segment);

            throw new InvalidOperationException("JBIG2 text region has no referenced symbols");
        }

        return segment.IsHuffmanEncoded
            ? DecodeHuffman(segment, payload, symbols, userTables)
            : DecodeArithmetic(segment, payload, symbols);
    }

    internal static Jbig2Bitmap DecodeArithmeticForTest(
        Jbig2TextRegionSegment segment,
        IJbig2ArithmeticDecoder decoder,
        IReadOnlyList<Jbig2Bitmap> symbols)
        => DecodeArithmetic(segment, decoder, symbols);

    internal static Jbig2Bitmap DecodeArithmeticWithContextLayout(
        Jbig2TextRegionSegment segment,
        IJbig2ArithmeticDecoder decoder,
        IReadOnlyList<Jbig2Bitmap> symbols,
        Jbig2ArithmeticTextContextLayout contextLayout,
        int? symbolCodeLength = null)
        => DecodeArithmetic(segment, decoder, symbols, contextLayout, symbolCodeLength);

    internal static Jbig2Bitmap DecodeHuffmanAggregateWithReader(
        Jbig2TextRegionSegment segment,
        Jbig2BitReader reader,
        IReadOnlyList<Jbig2Bitmap> symbols,
        int fixedSymbolCodeLength,
        Jbig2ArithmeticContextState refinementContextState)
        => DecodeHuffmanWithReader(
            segment,
            reader,
            new HuffmanSymbolIdDecoder(null, fixedSymbolCodeLength),
            symbols,
            userTables: null,
            refinementContextState);

    private static Jbig2Bitmap DecodeHuffman(
        Jbig2TextRegionSegment segment,
        ReadOnlySpan<byte> payload,
        IReadOnlyList<Jbig2Bitmap> symbols,
        IReadOnlyList<Jbig2HuffmanTable>? userTables)
    {
        var reader = new Jbig2BitReader(payload.ToArray());
        var symbolIdDecoder = new HuffmanSymbolIdDecoder(ReadSymbolCodeTable(reader, symbols.Count), 0);

        return DecodeHuffmanWithReader(
            segment,
            reader,
            symbolIdDecoder,
            symbols,
            userTables,
            segment.UseRefinement
                ? new Jbig2ArithmeticContextState(Jbig2GenericRefinementRegionDecoder.ContextCount)
                : null);
    }

    private static Jbig2Bitmap DecodeHuffmanWithReader(
        Jbig2TextRegionSegment segment,
        Jbig2BitReader reader,
        HuffmanSymbolIdDecoder symbolIdDecoder,
        IReadOnlyList<Jbig2Bitmap> symbols,
        IReadOnlyList<Jbig2HuffmanTable>? userTables,
        Jbig2ArithmeticContextState? refinementContextState)
    {
        if (segment.HuffmanFlags == null)
            throw new InvalidOperationException("JBIG2 Huffman text region is missing Huffman flags");

        var tableCursor = new UserHuffmanTableCursor(userTables);
        Jbig2TextRegionHuffmanFlags huffmanFlags = segment.HuffmanFlags.Value;
        Jbig2HuffmanTable firstSTable = ResolveFirstSTable(huffmanFlags, tableCursor);
        Jbig2HuffmanTable deltaSTable = ResolveDeltaSTable(huffmanFlags, tableCursor);
        Jbig2HuffmanTable deltaTTable = ResolveDeltaTTable(huffmanFlags, tableCursor);
        HuffmanTextRefinementTables? refinementTables = segment.UseRefinement
            ? ResolveRefinementTables(huffmanFlags, tableCursor)
            : null;

        var region = new Jbig2Bitmap((int)segment.Region.BitmapWidth, (int)segment.Region.BitmapHeight);
        if (segment.DefaultPixel != 0)
            region.Fill(true);

        DecodeSymbolInstances(
            segment,
            reader,
            symbolIdDecoder,
            symbols,
            region,
            firstSTable,
            deltaSTable,
            deltaTTable,
            refinementTables,
            refinementContextState);
        return region;
    }

    private static Jbig2Bitmap CreateDefaultRegion(Jbig2TextRegionSegment segment)
    {
        var region = new Jbig2Bitmap((int)segment.Region.BitmapWidth, (int)segment.Region.BitmapHeight);
        if (segment.DefaultPixel != 0)
            region.Fill(true);

        return region;
    }

    private static Jbig2Bitmap DecodeArithmetic(
        Jbig2TextRegionSegment segment,
        ReadOnlySpan<byte> payload,
        IReadOnlyList<Jbig2Bitmap> symbols)
    {
        int symbolCodeLength = GetSymbolCodeLength(symbols.Count);
        int iaidContextCount = GetIaidContextCount(symbolCodeLength);
        int contextCount = segment.UseRefinement
            ? GenericRefinementContextBase + Jbig2GenericRefinementRegionDecoder.ContextCount
            : IaidContextBase + iaidContextCount;
        var decoder = new Jbig2MqArithmeticDecoder(payload.ToArray(), contextCount);
        return DecodeArithmetic(segment, decoder, symbols);
    }

    private static Jbig2Bitmap DecodeArithmetic(
        Jbig2TextRegionSegment segment,
        IJbig2ArithmeticDecoder decoder,
        IReadOnlyList<Jbig2Bitmap> symbols)
        => DecodeArithmetic(segment, decoder, symbols, DefaultArithmeticContextLayout);

    private static Jbig2Bitmap DecodeArithmetic(
        Jbig2TextRegionSegment segment,
        IJbig2ArithmeticDecoder decoder,
        IReadOnlyList<Jbig2Bitmap> symbols,
        Jbig2ArithmeticTextContextLayout contextLayout,
        int? symbolCodeLength = null)
    {
        var region = new Jbig2Bitmap((int)segment.Region.BitmapWidth, (int)segment.Region.BitmapHeight);
        if (segment.DefaultPixel != 0)
            region.Fill(true);

        var arithmetic = new ArithmeticTextDecoders(decoder, symbolCodeLength ?? GetSymbolCodeLength(symbols.Count), contextLayout);
        DecodeArithmeticSymbolInstances(segment, arithmetic, symbols, region);
        return region;
    }

    private static Jbig2HuffmanTable ReadSymbolCodeTable(Jbig2BitReader reader, int symbolCount)
    {
        if (symbolCount <= 0)
            throw new InvalidOperationException("JBIG2 text region symbol count must be positive");

        var runCodeLines = new List<Jbig2HuffmanTableLine>();
        for (int i = 0; i < 35; i++)
        {
            int prefixLength = checked((int)reader.ReadBits(4));
            if (prefixLength > 0)
                runCodeLines.Add(new Jbig2HuffmanTableLine(prefixLength, 0, i));
        }

        var runCodeTable = Jbig2HuffmanTable.FromCanonicalLines(runCodeLines);
        var symbolCodeLines = new List<Jbig2HuffmanTableLine>();
        long previousCodeLength = 0;
        int counter = 0;

        while (counter < symbolCount)
        {
            long? codeValue = runCodeTable.Decode(reader);
            if (!codeValue.HasValue)
                throw new InvalidOperationException("JBIG2 text-region symbol code length decoded as OOB");

            long code = codeValue.Value;
            if (code < 32)
            {
                if (code > 0)
                    symbolCodeLines.Add(new Jbig2HuffmanTableLine((int)code, 0, counter));

                previousCodeLength = code;
                counter++;
                continue;
            }

            long runLength;
            long currentCodeLength = 0;
            if (code == 32)
            {
                runLength = 3 + reader.ReadBits(2);
                if (counter > 0)
                    currentCodeLength = previousCodeLength;
            }
            else if (code == 33)
            {
                runLength = 3 + reader.ReadBits(3);
            }
            else if (code == 34)
            {
                runLength = 11 + reader.ReadBits(7);
            }
            else
            {
                throw new InvalidOperationException("Invalid JBIG2 text-region symbol code-length run code");
            }

            for (int i = 0; i < runLength && counter < symbolCount; i++)
            {
                if (currentCodeLength > 0)
                    symbolCodeLines.Add(new Jbig2HuffmanTableLine((int)currentCodeLength, 0, counter));
                counter++;
            }
        }

        reader.AlignToByte();
        return Jbig2HuffmanTable.FromCanonicalLines(symbolCodeLines);
    }

    private static void DecodeSymbolInstances(
        Jbig2TextRegionSegment segment,
        Jbig2BitReader reader,
        HuffmanSymbolIdDecoder symbolIdDecoder,
        IReadOnlyList<Jbig2Bitmap> symbols,
        Jbig2Bitmap region,
        Jbig2HuffmanTable firstSTable,
        Jbig2HuffmanTable deltaSTable,
        Jbig2HuffmanTable deltaTTable,
        HuffmanTextRefinementTables? refinementTables,
        Jbig2ArithmeticContextState? refinementContextState)
    {
        int sbStrips = 1 << segment.LogSbStrips;
        long stripT = DecodeStripT(reader, deltaTTable, sbStrips);
        long firstS = 0;
        long instanceCounter = 0;

        while (instanceCounter < segment.SymbolInstanceCount)
        {
            stripT += DecodeDeltaT(reader, deltaTTable, sbStrips);
            bool first = true;
            long currentS = 0;

            while (true)
            {
                if (first)
                {
                    firstS += DecodeFirstS(reader, firstSTable);
                    currentS = firstS;
                    first = false;
                }
                else
                {
                    long? idS = DecodeDeltaS(reader, deltaSTable);
                    if (!idS.HasValue || instanceCounter >= segment.SymbolInstanceCount)
                        break;
                    currentS += idS.Value + segment.SbDsOffset;
                }

                long currentT = sbStrips == 1 ? 0 : reader.ReadBits(segment.LogSbStrips);
                long t = stripT + currentT;
                long id = symbolIdDecoder.Decode(reader);
                if ((uint)id >= (uint)symbols.Count)
                    throw new InvalidOperationException("JBIG2 text-region symbol id is outside the referenced symbol set");

                var symbol = DecodeHuffmanInstanceBitmap(
                    segment,
                    reader,
                    symbols[(int)id],
                    refinementTables,
                    refinementContextState);
                Blit(symbol, region, ref currentS, t, segment);
                instanceCounter++;
            }
        }
    }

    private static void DecodeArithmeticSymbolInstances(
        Jbig2TextRegionSegment segment,
        ArithmeticTextDecoders decoders,
        IReadOnlyList<Jbig2Bitmap> symbols,
        Jbig2Bitmap region)
    {
        int sbStrips = 1 << segment.LogSbStrips;
        long stripT = -DecodeRequired(decoders.Iadt, "text-region initial T") * sbStrips;
        long firstS = 0;
        long instanceCounter = 0;

        while (instanceCounter < segment.SymbolInstanceCount)
        {
            stripT += DecodeRequired(decoders.Iadt, "text-region T delta") * sbStrips;
            bool first = true;
            long currentS = 0;

            while (true)
            {
                if (first)
                {
                    firstS += DecodeRequired(decoders.Iafs, "text-region first S");
                    currentS = firstS;
                    first = false;
                }
                else
                {
                    long? idS = decoders.Iads.Decode();
                    if (!idS.HasValue || instanceCounter >= segment.SymbolInstanceCount)
                        break;
                    currentS += idS.Value + segment.SbDsOffset;
                }

                long currentT = segment.LogSbStrips == 0
                    ? 0
                    : DecodeRequired(decoders.Iait, "text-region current T");
                long t = stripT + currentT;
                long id = decoders.DecodeSymbolId();
                if ((uint)id >= (uint)symbols.Count)
                    throw new InvalidOperationException("JBIG2 text-region symbol id is outside the referenced symbol set");

                var symbol = DecodeArithmeticInstanceBitmap(segment, decoders, symbols[(int)id]);
                Blit(symbol, region, ref currentS, t, segment);
                instanceCounter++;
            }
        }
    }

    private static Jbig2Bitmap DecodeArithmeticInstanceBitmap(
        Jbig2TextRegionSegment segment,
        ArithmeticTextDecoders decoders,
        Jbig2Bitmap referenceSymbol)
    {
        if (!segment.UseRefinement)
            return referenceSymbol;

        long refinementIndicator = DecodeRequired(decoders.Iari, "text-region refinement indicator");
        if (refinementIndicator == 0)
            return referenceSymbol;
        if (refinementIndicator != 1)
            throw new InvalidOperationException("JBIG2 text-region refinement indicator must be 0 or 1");

        long rdw = DecodeRequired(decoders.Iardw, "text-region refinement width delta");
        long rdh = DecodeRequired(decoders.Iardh, "text-region refinement height delta");
        long rdx = DecodeRequired(decoders.Iardx, "text-region refinement x delta");
        long rdy = DecodeRequired(decoders.Iardy, "text-region refinement y delta");
        long refinedWidth = referenceSymbol.Width + rdw;
        long refinedHeight = referenceSymbol.Height + rdh;
        if (refinedWidth <= 0 || refinedWidth > int.MaxValue || refinedHeight <= 0 || refinedHeight > int.MaxValue)
            throw new InvalidOperationException("JBIG2 text-region refined symbol dimensions exceed supported limits");

        long referenceDx = (rdw >> 1) + rdx;
        long referenceDy = (rdh >> 1) + rdy;
        if (referenceDx < int.MinValue || referenceDx > int.MaxValue || referenceDy < int.MinValue || referenceDy > int.MaxValue)
            throw new InvalidOperationException("JBIG2 text-region refinement reference offset exceeds supported limits");

        return Jbig2GenericRefinementRegionDecoder.Decode(
            decoders.Decoder,
            (int)refinedWidth,
            (int)refinedHeight,
            segment.SbrTemplate,
            typicalPredictionGenericRefinementOn: false,
            referenceSymbol,
            (int)referenceDx,
            (int)referenceDy,
            segment.RefinementAdaptiveTemplatePixels,
            decoders.GenericRefinementContextBase);
    }

    private static Jbig2Bitmap DecodeHuffmanInstanceBitmap(
        Jbig2TextRegionSegment segment,
        Jbig2BitReader reader,
        Jbig2Bitmap referenceSymbol,
        HuffmanTextRefinementTables? refinementTables,
        Jbig2ArithmeticContextState? refinementContextState)
    {
        if (!segment.UseRefinement)
            return referenceSymbol;
        if (!reader.ReadBit())
            return referenceSymbol;
        if (!refinementTables.HasValue || refinementContextState == null)
            throw new InvalidOperationException("JBIG2 Huffman text refinement is missing refinement decoding state");

        var tables = refinementTables.Value;
        long rdw = DecodeRequired(tables.RefinementWidthDelta, reader, "text-region refinement width delta");
        long rdh = DecodeRequired(tables.RefinementHeightDelta, reader, "text-region refinement height delta");
        long rdx = DecodeRequired(tables.RefinementXDelta, reader, "text-region refinement x delta");
        long rdy = DecodeRequired(tables.RefinementYDelta, reader, "text-region refinement y delta");
        long refinedWidth = referenceSymbol.Width + rdw;
        long refinedHeight = referenceSymbol.Height + rdh;
        if (refinedWidth <= 0 || refinedWidth > int.MaxValue || refinedHeight <= 0 || refinedHeight > int.MaxValue)
            throw new InvalidOperationException("JBIG2 text-region refined symbol dimensions exceed supported limits");

        long referenceDx = (rdw >> 1) + rdx;
        long referenceDy = (rdh >> 1) + rdy;
        if (referenceDx < int.MinValue || referenceDx > int.MaxValue || referenceDy < int.MinValue || referenceDy > int.MaxValue)
            throw new InvalidOperationException("JBIG2 text-region refinement reference offset exceeds supported limits");

        long encodedSize = DecodeRequired(tables.RefinementBitmapSize, reader, "text-region refinement bitmap size");
        if (encodedSize < 0 || encodedSize > int.MaxValue)
            throw new InvalidOperationException("JBIG2 text-region refinement bitmap size exceeds supported limits");

        reader.AlignToByte();
        byte[] encodedBitmap = reader.ReadAlignedBytes((int)encodedSize);
        var refinementDecoder = new Jbig2MqArithmeticDecoder(encodedBitmap, refinementContextState);
        return Jbig2GenericRefinementRegionDecoder.Decode(
            refinementDecoder,
            (int)refinedWidth,
            (int)refinedHeight,
            segment.SbrTemplate,
            typicalPredictionGenericRefinementOn: false,
            referenceSymbol,
            (int)referenceDx,
            (int)referenceDy,
            segment.RefinementAdaptiveTemplatePixels);
    }

    private static Jbig2HuffmanTable ResolveFirstSTable(
        Jbig2TextRegionHuffmanFlags flags,
        UserHuffmanTableCursor userTables)
        => flags.SbHuffFs switch
        {
            0 => Jbig2StandardHuffmanTables.Get(6),
            1 => Jbig2StandardHuffmanTables.Get(7),
            2 => Jbig2StandardHuffmanTables.Get(8),
            3 => userTables.Next("text-region first S"),
            _ => throw new InvalidOperationException("Invalid JBIG2 text-region first-S Huffman table selector"),
        };

    private static Jbig2HuffmanTable ResolveDeltaSTable(
        Jbig2TextRegionHuffmanFlags flags,
        UserHuffmanTableCursor userTables)
        => flags.SbHuffDs switch
        {
            0 => Jbig2StandardHuffmanTables.Get(8),
            1 => Jbig2StandardHuffmanTables.Get(9),
            2 => Jbig2StandardHuffmanTables.Get(10),
            3 => userTables.Next("text-region delta S"),
            _ => throw new InvalidOperationException("Invalid JBIG2 text-region delta-S Huffman table selector"),
        };

    private static Jbig2HuffmanTable ResolveDeltaTTable(
        Jbig2TextRegionHuffmanFlags flags,
        UserHuffmanTableCursor userTables)
        => flags.SbHuffDt switch
        {
            0 => Jbig2StandardHuffmanTables.Get(11),
            1 => Jbig2StandardHuffmanTables.Get(12),
            2 => Jbig2StandardHuffmanTables.Get(13),
            3 => userTables.Next("text-region delta T"),
            _ => throw new InvalidOperationException("Invalid JBIG2 text-region delta-T Huffman table selector"),
        };

    private static HuffmanTextRefinementTables ResolveRefinementTables(
        Jbig2TextRegionHuffmanFlags flags,
        UserHuffmanTableCursor userTables)
        => new(
            ResolveRefinementDeltaTable(flags.SbHuffRdWidth, userTables, "text-region refinement width delta"),
            ResolveRefinementDeltaTable(flags.SbHuffRdHeight, userTables, "text-region refinement height delta"),
            ResolveRefinementDeltaTable(flags.SbHuffRdx, userTables, "text-region refinement x delta"),
            ResolveRefinementDeltaTable(flags.SbHuffRdy, userTables, "text-region refinement y delta"),
            ResolveRefinementBitmapSizeTable(flags, userTables));

    private static Jbig2HuffmanTable ResolveRefinementDeltaTable(
        int selector,
        UserHuffmanTableCursor userTables,
        string fieldName)
        => selector switch
        {
            0 => Jbig2StandardHuffmanTables.Get(14),
            1 => Jbig2StandardHuffmanTables.Get(15),
            2 => throw new InvalidOperationException($"Invalid JBIG2 {fieldName} Huffman table selector"),
            3 => userTables.Next(fieldName),
            _ => throw new InvalidOperationException($"Invalid JBIG2 {fieldName} Huffman table selector"),
        };

    private static Jbig2HuffmanTable ResolveRefinementBitmapSizeTable(
        Jbig2TextRegionHuffmanFlags flags,
        UserHuffmanTableCursor userTables)
        => flags.SbHuffRSize switch
        {
            0 => Jbig2StandardHuffmanTables.Get(1),
            1 => userTables.Next("text-region refinement bitmap size"),
            _ => throw new InvalidOperationException("Invalid JBIG2 text-region refinement bitmap-size Huffman table selector"),
        };

    private static long DecodeStripT(Jbig2BitReader reader, Jbig2HuffmanTable deltaTTable, int sbStrips)
        => -DecodeDeltaTValue(reader, deltaTTable) * sbStrips;

    private static long DecodeDeltaT(Jbig2BitReader reader, Jbig2HuffmanTable deltaTTable, int sbStrips)
        => DecodeDeltaTValue(reader, deltaTTable) * sbStrips;

    private static long DecodeDeltaTValue(Jbig2BitReader reader, Jbig2HuffmanTable deltaTTable)
        => DecodeRequired(deltaTTable, reader, "text-region T delta");

    private static long DecodeFirstS(Jbig2BitReader reader, Jbig2HuffmanTable firstSTable)
        => DecodeRequired(firstSTable, reader, "text-region first S");

    private static long? DecodeDeltaS(Jbig2BitReader reader, Jbig2HuffmanTable deltaSTable)
        => deltaSTable.Decode(reader);

    private static long DecodeRequired(Jbig2HuffmanTable table, Jbig2BitReader reader, string fieldName)
        => table.Decode(reader) ?? throw new InvalidOperationException($"JBIG2 {fieldName} decoded as OOB");

    private static long DecodeRequired(Jbig2ArithmeticIntegerDecoder decoder, string fieldName)
        => decoder.Decode() ?? throw new InvalidOperationException($"JBIG2 {fieldName} decoded as OOB");

    private static int GetSymbolCodeLength(int symbolCount)
    {
        if (symbolCount <= 1)
            return 0;

        int length = 0;
        int value = 1;
        while (value < symbolCount)
        {
            length++;
            value <<= 1;
        }

        return length;
    }

    private static int GetIaidContextCount(int symbolCodeLength)
    {
        if (symbolCodeLength == 0)
            return 1;
        if (symbolCodeLength > MaxIaidContextBits)
            throw new InvalidOperationException("JBIG2 arithmetic text-region symbol-code length exceeds supported limits");

        return 1 << symbolCodeLength;
    }

    private static void Blit(Jbig2Bitmap symbol, Jbig2Bitmap region, ref long currentS, long tValue, Jbig2TextRegionSegment segment)
    {
        long t = tValue;
        if (!segment.IsTransposed && segment.ReferenceCorner is 2 or 3)
            currentS += symbol.Width - 1;
        else if (segment.IsTransposed && segment.ReferenceCorner is 0 or 2)
            currentS += symbol.Height - 1;

        long s = currentS;
        if (segment.IsTransposed)
            (s, t) = (t, s);

        if (segment.ReferenceCorner != 1)
        {
            switch (segment.ReferenceCorner)
            {
                case 0:
                    t -= symbol.Height - 1;
                    break;
                case 2:
                    t -= symbol.Height - 1;
                    s -= symbol.Width - 1;
                    break;
                case 3:
                    s -= symbol.Width - 1;
                    break;
            }
        }

        Jbig2BitmapCompositor.Composite(region, symbol, checked((int)s), checked((int)t), segment.CombinationOperator);

        if (!segment.IsTransposed && segment.ReferenceCorner is 0 or 1)
            currentS += symbol.Width - 1;
        if (segment.IsTransposed && segment.ReferenceCorner is 1 or 3)
            currentS += symbol.Height - 1;
    }

    private sealed class UserHuffmanTableCursor
    {
        private readonly IReadOnlyList<Jbig2HuffmanTable> _tables;
        private int _position;

        public UserHuffmanTableCursor(IReadOnlyList<Jbig2HuffmanTable>? tables)
        {
            _tables = tables ?? Array.Empty<Jbig2HuffmanTable>();
        }

        public Jbig2HuffmanTable Next(string fieldName)
        {
            if (_position >= _tables.Count)
                throw new InvalidOperationException($"JBIG2 {fieldName} requires a referenced user Huffman table");

            return _tables[_position++];
        }
    }

    private readonly record struct HuffmanSymbolIdDecoder(
        Jbig2HuffmanTable? SymbolCodeTable,
        int FixedCodeLength)
    {
        public long Decode(Jbig2BitReader reader)
        {
            if (SymbolCodeTable != null)
                return DecodeRequired(SymbolCodeTable, reader, "text-region symbol id");
            if (FixedCodeLength < 0 || FixedCodeLength > 32)
                throw new InvalidOperationException("JBIG2 text-region fixed symbol-code length exceeds supported limits");

            return FixedCodeLength == 0 ? 0 : reader.ReadBits(FixedCodeLength);
        }
    }

    private readonly record struct HuffmanTextRefinementTables(
        Jbig2HuffmanTable RefinementWidthDelta,
        Jbig2HuffmanTable RefinementHeightDelta,
        Jbig2HuffmanTable RefinementXDelta,
        Jbig2HuffmanTable RefinementYDelta,
        Jbig2HuffmanTable RefinementBitmapSize);

    private sealed class ArithmeticTextDecoders
    {
        public IJbig2ArithmeticDecoder Decoder { get; }
        private readonly Jbig2IaidDecoder? _iaid;
        private readonly int _symbolCodeLength;

        public ArithmeticTextDecoders(
            IJbig2ArithmeticDecoder decoder,
            int symbolCodeLength,
            Jbig2ArithmeticTextContextLayout contextLayout)
        {
            Decoder = decoder;
            Iadt = new Jbig2ArithmeticIntegerDecoder(decoder, contextLayout.IadtContextBase);
            Iafs = new Jbig2ArithmeticIntegerDecoder(decoder, contextLayout.IafsContextBase);
            Iads = new Jbig2ArithmeticIntegerDecoder(decoder, contextLayout.IadsContextBase);
            Iait = new Jbig2ArithmeticIntegerDecoder(decoder, contextLayout.IaitContextBase);
            Iari = new Jbig2ArithmeticIntegerDecoder(decoder, contextLayout.IariContextBase);
            Iardw = new Jbig2ArithmeticIntegerDecoder(decoder, contextLayout.IardwContextBase);
            Iardh = new Jbig2ArithmeticIntegerDecoder(decoder, contextLayout.IardhContextBase);
            Iardx = new Jbig2ArithmeticIntegerDecoder(decoder, contextLayout.IardxContextBase);
            Iardy = new Jbig2ArithmeticIntegerDecoder(decoder, contextLayout.IardyContextBase);
            _symbolCodeLength = symbolCodeLength;
            _iaid = symbolCodeLength == 0
                ? null
                : new Jbig2IaidDecoder(decoder, contextLayout.IaidContextBase);
            GenericRefinementContextBase = contextLayout.GenericRefinementContextBase;
        }

        public Jbig2ArithmeticIntegerDecoder Iadt { get; }
        public Jbig2ArithmeticIntegerDecoder Iafs { get; }
        public Jbig2ArithmeticIntegerDecoder Iads { get; }
        public Jbig2ArithmeticIntegerDecoder Iait { get; }
        public Jbig2ArithmeticIntegerDecoder Iari { get; }
        public Jbig2ArithmeticIntegerDecoder Iardw { get; }
        public Jbig2ArithmeticIntegerDecoder Iardh { get; }
        public Jbig2ArithmeticIntegerDecoder Iardx { get; }
        public Jbig2ArithmeticIntegerDecoder Iardy { get; }
        public int GenericRefinementContextBase { get; }

        public uint DecodeSymbolId()
            => _iaid == null ? 0 : _iaid.Decode(_symbolCodeLength);
    }
}

internal readonly record struct Jbig2ArithmeticTextContextLayout(
    int IadtContextBase,
    int IafsContextBase,
    int IadsContextBase,
    int IaitContextBase,
    int IaidContextBase,
    int IariContextBase,
    int IardwContextBase,
    int IardhContextBase,
    int IardxContextBase,
    int IardyContextBase,
    int GenericRefinementContextBase);
