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

    public static Jbig2Bitmap Decode(
        Jbig2TextRegionSegment segment,
        ReadOnlySpan<byte> payload,
        IReadOnlyList<Jbig2Bitmap> symbols,
        IReadOnlyList<Jbig2HuffmanTable>? userTables = null)
    {
        if (segment.UseRefinement && segment.IsHuffmanEncoded)
            throw new NotSupportedException("Huffman refinement JBIG2 text regions are not yet supported");
        if (symbols == null || symbols.Count == 0)
            throw new InvalidOperationException("JBIG2 text region has no referenced symbols");
        if (segment.Region.BitmapWidth > int.MaxValue || segment.Region.BitmapHeight > int.MaxValue)
            throw new InvalidOperationException("JBIG2 text region dimensions exceed supported limits");

        return segment.IsHuffmanEncoded
            ? DecodeHuffman(segment, payload, symbols, userTables)
            : DecodeArithmetic(segment, payload, symbols);
    }

    internal static Jbig2Bitmap DecodeArithmeticForTest(
        Jbig2TextRegionSegment segment,
        IJbig2ArithmeticDecoder decoder,
        IReadOnlyList<Jbig2Bitmap> symbols)
        => DecodeArithmetic(segment, decoder, symbols);

    private static Jbig2Bitmap DecodeHuffman(
        Jbig2TextRegionSegment segment,
        ReadOnlySpan<byte> payload,
        IReadOnlyList<Jbig2Bitmap> symbols,
        IReadOnlyList<Jbig2HuffmanTable>? userTables)
    {
        if (segment.HuffmanFlags == null)
            throw new InvalidOperationException("JBIG2 Huffman text region is missing Huffman flags");

        var tableCursor = new UserHuffmanTableCursor(userTables);
        Jbig2TextRegionHuffmanFlags huffmanFlags = segment.HuffmanFlags.Value;
        Jbig2HuffmanTable firstSTable = ResolveFirstSTable(huffmanFlags, tableCursor);
        Jbig2HuffmanTable deltaSTable = ResolveDeltaSTable(huffmanFlags, tableCursor);
        Jbig2HuffmanTable deltaTTable = ResolveDeltaTTable(huffmanFlags, tableCursor);
        var reader = new Jbig2BitReader(payload.ToArray());
        var symbolCodeTable = ReadSymbolCodeTable(reader, symbols.Count);

        var region = new Jbig2Bitmap((int)segment.Region.BitmapWidth, (int)segment.Region.BitmapHeight);
        if (segment.DefaultPixel != 0)
            region.Fill(true);

        DecodeSymbolInstances(
            segment,
            reader,
            symbolCodeTable,
            symbols,
            region,
            firstSTable,
            deltaSTable,
            deltaTTable);
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
    {
        var region = new Jbig2Bitmap((int)segment.Region.BitmapWidth, (int)segment.Region.BitmapHeight);
        if (segment.DefaultPixel != 0)
            region.Fill(true);

        var arithmetic = new ArithmeticTextDecoders(decoder, GetSymbolCodeLength(symbols.Count));
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
        Jbig2HuffmanTable symbolCodeTable,
        IReadOnlyList<Jbig2Bitmap> symbols,
        Jbig2Bitmap region,
        Jbig2HuffmanTable firstSTable,
        Jbig2HuffmanTable deltaSTable,
        Jbig2HuffmanTable deltaTTable)
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

            while (instanceCounter < segment.SymbolInstanceCount)
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
                    if (!idS.HasValue)
                        break;
                    currentS += idS.Value + segment.SbDsOffset;
                }

                long currentT = sbStrips == 1 ? 0 : reader.ReadBits(segment.LogSbStrips);
                long t = stripT + currentT;
                long id = DecodeSymbolId(reader, symbolCodeTable);
                if ((uint)id >= (uint)symbols.Count)
                    throw new InvalidOperationException("JBIG2 text-region symbol id is outside the referenced symbol set");

                Blit(symbols[(int)id], region, ref currentS, t, segment);
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

            while (instanceCounter < segment.SymbolInstanceCount)
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
                    if (!idS.HasValue)
                        break;
                    currentS += idS.Value + segment.SbDsOffset;
                }

                long currentT = segment.LogSbStrips == 0
                    ? 0
                    : DecodeRequired(decoders.Iait, "text-region current T");
                if (currentT < 0 || currentT >= sbStrips)
                    throw new InvalidOperationException("JBIG2 text-region current T is outside the strip");

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
            GenericRefinementContextBase);
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

    private static long DecodeSymbolId(Jbig2BitReader reader, Jbig2HuffmanTable symbolCodeTable)
        => DecodeRequired(symbolCodeTable, reader, "text-region symbol id");

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

    private sealed class ArithmeticTextDecoders
    {
        public IJbig2ArithmeticDecoder Decoder { get; }
        private readonly Jbig2IaidDecoder? _iaid;
        private readonly int _symbolCodeLength;

        public ArithmeticTextDecoders(IJbig2ArithmeticDecoder decoder, int symbolCodeLength)
        {
            Decoder = decoder;
            Iadt = new Jbig2ArithmeticIntegerDecoder(decoder, IadtContextBase);
            Iafs = new Jbig2ArithmeticIntegerDecoder(decoder, IafsContextBase);
            Iads = new Jbig2ArithmeticIntegerDecoder(decoder, IadsContextBase);
            Iait = new Jbig2ArithmeticIntegerDecoder(decoder, IaitContextBase);
            Iari = new Jbig2ArithmeticIntegerDecoder(decoder, IariContextBase);
            Iardw = new Jbig2ArithmeticIntegerDecoder(decoder, IardwContextBase);
            Iardh = new Jbig2ArithmeticIntegerDecoder(decoder, IardhContextBase);
            Iardx = new Jbig2ArithmeticIntegerDecoder(decoder, IardxContextBase);
            Iardy = new Jbig2ArithmeticIntegerDecoder(decoder, IardyContextBase);
            _symbolCodeLength = symbolCodeLength;
            _iaid = symbolCodeLength == 0
                ? null
                : new Jbig2IaidDecoder(decoder, IaidContextBase);
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

        public uint DecodeSymbolId()
            => _iaid == null ? 0 : _iaid.Decode(_symbolCodeLength);
    }
}
