using System;
using System.Collections.Generic;

namespace Excise.Core.Filters.Jbig2;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal sealed class Jbig2DecodedSymbolDictionary
{
    public Jbig2DecodedSymbolDictionary(IReadOnlyList<Jbig2Bitmap> exportedSymbols, IReadOnlyList<Jbig2Bitmap> newSymbols)
    {
        ExportedSymbols = exportedSymbols ?? throw new ArgumentNullException(nameof(exportedSymbols));
        NewSymbols = newSymbols ?? throw new ArgumentNullException(nameof(newSymbols));
    }

    public IReadOnlyList<Jbig2Bitmap> ExportedSymbols { get; }
    public IReadOnlyList<Jbig2Bitmap> NewSymbols { get; }
}

/// <summary>
/// Decodes JBIG2 symbol dictionary segments for the no-refinement paths.
/// Standard Huffman tables, referenced user tables, raw collective bitmaps,
/// MMR collective bitmaps, imported symbols, and direct arithmetic-coded
/// symbols with default template-0 AT pixels are handled here. Arithmetic and
/// Huffman refinement aggregation both delegate multi-instance aggregates
/// through the text-region decoder so the placement/refinement logic stays
/// shared; retained arithmetic contexts remain explicitly gated.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class Jbig2SymbolDictionaryDecoder
{
    private const int GenericRegionContextBase = 0;
    private const int IadhContextBase = GenericRegionContextBase + Jbig2ArithmeticGenericRegionDecoder.ContextCount;
    private const int IadwContextBase = IadhContextBase + 512;
    private const int IaexContextBase = IadwContextBase + 512;
    private const int ArithmeticDirectContextCount = IaexContextBase + 512;
    private const int IaaiContextBase = ArithmeticDirectContextCount;
    private const int IaidContextBase = IaaiContextBase + 512;
    private const int MaxIaidContextBits = 20;
    private const int IardxContextBase = IaidContextBase + (1 << MaxIaidContextBits);
    private const int IardyContextBase = IardxContextBase + 512;
    private const int GenericRefinementContextBase = IardyContextBase + 512;
    private const int ArithmeticRefinementContextCount = GenericRefinementContextBase + Jbig2GenericRefinementRegionDecoder.ContextCount;
    private const int AggregateIadtContextBase = ArithmeticRefinementContextCount;
    private const int AggregateIafsContextBase = AggregateIadtContextBase + 512;
    private const int AggregateIadsContextBase = AggregateIafsContextBase + 512;
    private const int AggregateIaitContextBase = AggregateIadsContextBase + 512;
    private const int AggregateIariContextBase = AggregateIaitContextBase + 512;
    private const int AggregateIardwContextBase = AggregateIariContextBase + 512;
    private const int AggregateIardhContextBase = AggregateIardwContextBase + 512;
    private const int AggregateIardxContextBase = AggregateIardhContextBase + 512;
    private const int AggregateIardyContextBase = AggregateIardxContextBase + 512;
    private const int AggregateContextCount = AggregateIardyContextBase + 512;
    private static readonly Jbig2ArithmeticTextContextLayout AggregateTextContextLayout = new(
        AggregateIadtContextBase,
        AggregateIafsContextBase,
        AggregateIadsContextBase,
        AggregateIaitContextBase,
        IaidContextBase,
        AggregateIariContextBase,
        AggregateIardwContextBase,
        AggregateIardhContextBase,
        AggregateIardxContextBase,
        AggregateIardyContextBase,
        GenericRefinementContextBase);

    public static Jbig2DecodedSymbolDictionary Decode(
        Jbig2SymbolDictionarySegment segment,
        ReadOnlySpan<byte> payload,
        IReadOnlyList<Jbig2Bitmap> importedSymbols,
        IReadOnlyList<Jbig2HuffmanTable>? userTables = null)
    {
        if (segment.NewSymbolCount > int.MaxValue || segment.ExportedSymbolCount > int.MaxValue)
            throw new InvalidOperationException("JBIG2 symbol dictionary count exceeds supported limits");

        importedSymbols ??= Array.Empty<Jbig2Bitmap>();
        return segment.IsHuffmanEncoded
            ? DecodeHuffman(segment, payload, importedSymbols, userTables)
            : DecodeArithmetic(segment, payload, importedSymbols);
    }

    internal static Jbig2DecodedSymbolDictionary DecodeArithmeticForTest(
        Jbig2SymbolDictionarySegment segment,
        IJbig2ArithmeticDecoder decoder,
        IReadOnlyList<Jbig2Bitmap> importedSymbols)
        => DecodeArithmetic(segment, decoder, importedSymbols);

    private static Jbig2DecodedSymbolDictionary DecodeHuffman(
        Jbig2SymbolDictionarySegment segment,
        ReadOnlySpan<byte> payload,
        IReadOnlyList<Jbig2Bitmap> importedSymbols,
        IReadOnlyList<Jbig2HuffmanTable>? userTables)
    {
        var tableCursor = new UserHuffmanTableCursor(userTables);
        Jbig2HuffmanTable heightTable = ResolveDecodeHeightTable(segment, tableCursor);
        Jbig2HuffmanTable widthTable = ResolveDecodeWidthTable(segment, tableCursor);
        Jbig2HuffmanTable bitmapSizeTable = ResolveBitmapSizeTable(segment, tableCursor);
        Jbig2HuffmanTable? aggregateInstanceTable = segment.UseRefinementAggregation
            ? ResolveAggregateInstanceTable(segment, tableCursor)
            : null;

        int newSymbolCount = checked((int)segment.NewSymbolCount);
        var newSymbols = new Jbig2Bitmap[newSymbolCount];
        var allSymbols = new List<Jbig2Bitmap>(importedSymbols.Count + newSymbolCount);
        allSymbols.AddRange(importedSymbols);
        int aggregateSymbolCodeLength = segment.UseRefinementAggregation
            ? GetHuffmanFixedSymbolCodeLength(importedSymbols.Count + newSymbolCount)
            : 0;
        var refinementContextState = segment.UseRefinementAggregation
            ? new Jbig2ArithmeticContextState(Jbig2GenericRefinementRegionDecoder.ContextCount)
            : null;

        var reader = new Jbig2BitReader(payload.ToArray());
        int decodedSymbols = 0;
        int heightClassHeight = 0;

        while (decodedSymbols < newSymbolCount)
        {
            long? deltaHeight = heightTable.Decode(reader);
            if (!deltaHeight.HasValue)
                throw new InvalidOperationException("JBIG2 symbol dictionary height class delta decoded as OOB");

            if (deltaHeight.Value < int.MinValue || deltaHeight.Value > int.MaxValue)
                throw new InvalidOperationException("JBIG2 symbol dictionary height delta exceeds supported limits");
            try
            {
                checked { heightClassHeight += (int)deltaHeight.Value; }
            }
            catch (OverflowException ex)
            {
                throw new InvalidOperationException("JBIG2 symbol dictionary height class exceeds supported limits", ex);
            }
            if (heightClassHeight <= 0)
                throw new InvalidOperationException("Invalid JBIG2 symbol dictionary height class");

            int heightClassFirstSymbol = decodedSymbols;
            int symbolWidth = 0;
            int totalWidth = 0;
            var widths = new List<int>();

            while (true)
            {
                long? deltaWidth = widthTable.Decode(reader);
                if (!deltaWidth.HasValue || decodedSymbols >= newSymbolCount)
                    break;

                if (deltaWidth.Value < int.MinValue || deltaWidth.Value > int.MaxValue)
                    throw new InvalidOperationException("JBIG2 symbol dictionary width delta exceeds supported limits");
                try
                {
                    checked { symbolWidth += (int)deltaWidth.Value; }
                }
                catch (OverflowException ex)
                {
                    throw new InvalidOperationException("JBIG2 symbol dictionary symbol width exceeds supported limits", ex);
                }
                if (symbolWidth <= 0)
                    throw new InvalidOperationException("Invalid JBIG2 symbol dictionary symbol width");

                widths.Add(symbolWidth);
                checked { totalWidth += symbolWidth; }
                if (segment.UseRefinementAggregation)
                {
                    var symbol = DecodeHuffmanRefinementAggregateSymbol(
                        segment,
                        reader,
                        allSymbols,
                        symbolWidth,
                        heightClassHeight,
                        aggregateSymbolCodeLength,
                        aggregateInstanceTable!,
                        refinementContextState!);
                    newSymbols[decodedSymbols] = symbol;
                    allSymbols.Add(symbol);
                }

                decodedSymbols++;
            }

            if (widths.Count == 0 || segment.UseRefinementAggregation)
                continue;

            long? bitmapSize = bitmapSizeTable.Decode(reader);
            if (!bitmapSize.HasValue)
                throw new InvalidOperationException("JBIG2 symbol dictionary bitmap size decoded as OOB");

            reader.AlignToByte();
            var collectiveBitmap = DecodeHeightClassCollectiveBitmap(reader, bitmapSize.Value, totalWidth, heightClassHeight);
            reader.AlignToByte();

            for (int i = 0; i < widths.Count; i++)
            {
                var symbol = ExtractSymbol(collectiveBitmap, widths, i, heightClassHeight);
                newSymbols[heightClassFirstSymbol + i] = symbol;
                allSymbols.Add(symbol);
            }
        }

        var exportFlags = DecodeExportFlags(reader, allSymbols.Count);
        var exportedSymbols = new List<Jbig2Bitmap>(checked((int)segment.ExportedSymbolCount));
        for (int i = 0; i < exportFlags.Length; i++)
        {
            if (exportFlags[i])
                exportedSymbols.Add(allSymbols[i]);
        }

        if (exportedSymbols.Count != segment.ExportedSymbolCount)
            throw new InvalidOperationException("JBIG2 symbol dictionary exported-symbol count did not match export flags");

        return new Jbig2DecodedSymbolDictionary(exportedSymbols, newSymbols);
    }

    private static Jbig2DecodedSymbolDictionary DecodeArithmetic(
        Jbig2SymbolDictionarySegment segment,
        ReadOnlySpan<byte> payload,
        IReadOnlyList<Jbig2Bitmap> importedSymbols)
    {
        int contextCount = segment.UseRefinementAggregation
            ? AggregateContextCount
            : ArithmeticDirectContextCount;
        var decoder = new Jbig2MqArithmeticDecoder(payload.ToArray(), contextCount);
        return DecodeArithmetic(segment, decoder, importedSymbols);
    }

    private static Jbig2DecodedSymbolDictionary DecodeArithmetic(
        Jbig2SymbolDictionarySegment segment,
        IJbig2ArithmeticDecoder decoder,
        IReadOnlyList<Jbig2Bitmap> importedSymbols)
    {
        if (segment.IsCodingContextUsed || segment.IsCodingContextRetained)
            throw new NotSupportedException("JBIG2 symbol-dictionary arithmetic context retention is not yet supported");

        int newSymbolCount = checked((int)segment.NewSymbolCount);
        var newSymbols = new Jbig2Bitmap[newSymbolCount];
        var allSymbols = new List<Jbig2Bitmap>(importedSymbols.Count + newSymbolCount);
        allSymbols.AddRange(importedSymbols);

        var heightDecoder = new Jbig2ArithmeticIntegerDecoder(decoder, IadhContextBase);
        var widthDecoder = new Jbig2ArithmeticIntegerDecoder(decoder, IadwContextBase);
        ArithmeticSymbolRefinementDecoders? refinementDecoders = null;
        if (segment.UseRefinementAggregation)
            refinementDecoders = new ArithmeticSymbolRefinementDecoders(decoder, GetSymbolCodeLength(importedSymbols.Count + newSymbolCount));
        int decodedSymbols = 0;
        int heightClassHeight = 0;

        while (decodedSymbols < newSymbolCount)
        {
            long? deltaHeight = heightDecoder.Decode();
            if (!deltaHeight.HasValue)
                throw new InvalidOperationException("JBIG2 symbol dictionary height class delta decoded as OOB");

            checked { heightClassHeight += (int)deltaHeight.Value; }
            if (heightClassHeight <= 0)
                throw new InvalidOperationException("Invalid JBIG2 symbol dictionary height class");

            int symbolWidth = 0;
            while (true)
            {
                long? deltaWidth = widthDecoder.Decode();
                if (!deltaWidth.HasValue || decodedSymbols >= newSymbolCount)
                    break;

                checked { symbolWidth += (int)deltaWidth.Value; }
                if (symbolWidth <= 0)
                    throw new InvalidOperationException("Invalid JBIG2 symbol dictionary symbol width");

                var symbol = segment.UseRefinementAggregation
                    ? DecodeArithmeticRefinedSymbol(segment, refinementDecoders!, allSymbols, symbolWidth, heightClassHeight)
                    : Jbig2ArithmeticGenericRegionDecoder.Decode(
                        decoder,
                        symbolWidth,
                        heightClassHeight,
                        segment.SdTemplate,
                        segment.AdaptiveTemplatePixels,
                        GenericRegionContextBase);

                newSymbols[decodedSymbols] = symbol;
                allSymbols.Add(symbol);
                decodedSymbols++;
            }
        }

        var exportFlags = DecodeArithmeticExportFlagsOrAll(decoder, allSymbols.Count, segment.ExportedSymbolCount);
        var exportedSymbols = new List<Jbig2Bitmap>(checked((int)segment.ExportedSymbolCount));
        for (int i = 0; i < exportFlags.Length; i++)
        {
            if (exportFlags[i])
                exportedSymbols.Add(allSymbols[i]);
        }

        if (exportedSymbols.Count != segment.ExportedSymbolCount)
            throw new InvalidOperationException("JBIG2 symbol dictionary exported-symbol count did not match export flags");

        return new Jbig2DecodedSymbolDictionary(exportedSymbols, newSymbols);
    }

    private static Jbig2Bitmap DecodeArithmeticRefinedSymbol(
        Jbig2SymbolDictionarySegment segment,
        ArithmeticSymbolRefinementDecoders decoders,
        IReadOnlyList<Jbig2Bitmap> symbols,
        int symbolWidth,
        int symbolHeight)
    {
        long instanceCount = DecodeRequired(decoders.AggregationInstanceCount, "symbol-dictionary refinement aggregate instance count");
        if (symbols.Count == 0)
            throw new InvalidOperationException("JBIG2 symbol-dictionary refinement has no referenced symbols");
        if (instanceCount <= 0)
            throw new InvalidOperationException("Invalid JBIG2 symbol-dictionary refinement aggregate instance count");
        if (instanceCount > int.MaxValue)
            throw new InvalidOperationException("JBIG2 symbol-dictionary refinement aggregate instance count exceeds supported limits");
        if (instanceCount != 1)
            return DecodeArithmeticAggregateSymbol(segment, decoders, symbols, symbolWidth, symbolHeight, (uint)instanceCount);

        uint symbolId = decoders.DecodeSymbolId();
        if (symbolId >= symbols.Count)
            throw new InvalidOperationException("JBIG2 symbol-dictionary refinement symbol id is outside the referenced symbol set");

        long rdx = DecodeRequired(decoders.ReferenceDeltaX, "symbol-dictionary refinement x delta");
        long rdy = DecodeRequired(decoders.ReferenceDeltaY, "symbol-dictionary refinement y delta");
        if (rdx < int.MinValue || rdx > int.MaxValue || rdy < int.MinValue || rdy > int.MaxValue)
            throw new InvalidOperationException("JBIG2 symbol-dictionary refinement reference offset exceeds supported limits");

        return Jbig2GenericRefinementRegionDecoder.Decode(
            decoders.Decoder,
            symbolWidth,
            symbolHeight,
            segment.SdrTemplate,
            typicalPredictionGenericRefinementOn: false,
            symbols[(int)symbolId],
            (int)rdx,
            (int)rdy,
            segment.RefinementAdaptiveTemplatePixels,
            GenericRefinementContextBase);
    }

    private static Jbig2Bitmap DecodeArithmeticAggregateSymbol(
        Jbig2SymbolDictionarySegment segment,
        ArithmeticSymbolRefinementDecoders decoders,
        IReadOnlyList<Jbig2Bitmap> symbols,
        int symbolWidth,
        int symbolHeight,
        uint instanceCount)
    {
        var aggregateSegment = new Jbig2TextRegionSegment(
            Region: new Jbig2RegionSegmentInformation(
                (uint)symbolWidth,
                (uint)symbolHeight,
                0,
                0,
                Jbig2CombinationOperator.Or),
            IsHuffmanEncoded: false,
            UseRefinement: true,
            LogSbStrips: 0,
            ReferenceCorner: 1,
            IsTransposed: false,
            CombinationOperator: Jbig2CombinationOperator.Or,
            DefaultPixel: 0,
            SbDsOffset: 0,
            SbrTemplate: segment.SdrTemplate,
            HuffmanFlags: null,
            RefinementAdaptiveTemplatePixels: segment.RefinementAdaptiveTemplatePixels,
            DeclaredSymbolInstanceCount: instanceCount,
            SymbolInstanceCount: instanceCount,
            PayloadDataOffset: 0,
            PayloadDataLength: 0);

        return Jbig2TextRegionDecoder.DecodeArithmeticWithContextLayout(
            aggregateSegment,
            decoders.Decoder,
            symbols,
            AggregateTextContextLayout,
            decoders.SymbolCodeLength);
    }

    private static Jbig2Bitmap DecodeHuffmanRefinementAggregateSymbol(
        Jbig2SymbolDictionarySegment segment,
        Jbig2BitReader reader,
        IReadOnlyList<Jbig2Bitmap> symbols,
        int symbolWidth,
        int symbolHeight,
        int symbolCodeLength,
        Jbig2HuffmanTable aggregateInstanceTable,
        Jbig2ArithmeticContextState refinementContextState)
    {
        long instanceCount = DecodeRequired(
            aggregateInstanceTable,
            reader,
            "symbol-dictionary refinement aggregate instance count");
        if (symbols.Count == 0)
            throw new InvalidOperationException("JBIG2 symbol-dictionary refinement has no referenced symbols");
        if (instanceCount <= 0)
            throw new InvalidOperationException("Invalid JBIG2 symbol-dictionary refinement aggregate instance count");
        if (instanceCount > int.MaxValue)
            throw new InvalidOperationException("JBIG2 symbol-dictionary refinement aggregate instance count exceeds supported limits");

        return instanceCount == 1
            ? DecodeHuffmanSingleRefinedSymbol(
                segment,
                reader,
                symbols,
                symbolWidth,
                symbolHeight,
                symbolCodeLength,
                refinementContextState)
            : DecodeHuffmanAggregateSymbol(
                segment,
                reader,
                symbols,
                symbolWidth,
                symbolHeight,
                (uint)instanceCount,
                symbolCodeLength,
                refinementContextState);
    }

    private static Jbig2Bitmap DecodeHuffmanSingleRefinedSymbol(
        Jbig2SymbolDictionarySegment segment,
        Jbig2BitReader reader,
        IReadOnlyList<Jbig2Bitmap> symbols,
        int symbolWidth,
        int symbolHeight,
        int symbolCodeLength,
        Jbig2ArithmeticContextState refinementContextState)
    {
        uint symbolId = symbolCodeLength == 0 ? 0 : reader.ReadBits(symbolCodeLength);
        if (symbolId >= symbols.Count)
            throw new InvalidOperationException("JBIG2 symbol-dictionary refinement symbol id is outside the referenced symbol set");

        long rdx = DecodeRequired(Jbig2StandardHuffmanTables.Get(15), reader, "symbol-dictionary refinement x delta");
        long rdy = DecodeRequired(Jbig2StandardHuffmanTables.Get(15), reader, "symbol-dictionary refinement y delta");
        if (rdx < int.MinValue || rdx > int.MaxValue || rdy < int.MinValue || rdy > int.MaxValue)
            throw new InvalidOperationException("JBIG2 symbol-dictionary refinement reference offset exceeds supported limits");

        long encodedSize = DecodeRequired(Jbig2StandardHuffmanTables.Get(1), reader, "symbol-dictionary refinement bitmap size");
        if (encodedSize < 0 || encodedSize > int.MaxValue)
            throw new InvalidOperationException("JBIG2 symbol-dictionary refinement bitmap size exceeds supported limits");

        reader.AlignToByte();
        var refinementDecoder = new Jbig2MqArithmeticDecoder(
            reader.ReadAlignedBytes((int)encodedSize),
            refinementContextState);
        return Jbig2GenericRefinementRegionDecoder.Decode(
            refinementDecoder,
            symbolWidth,
            symbolHeight,
            segment.SdrTemplate,
            typicalPredictionGenericRefinementOn: false,
            symbols[(int)symbolId],
            (int)rdx,
            (int)rdy,
            segment.RefinementAdaptiveTemplatePixels);
    }

    private static Jbig2Bitmap DecodeHuffmanAggregateSymbol(
        Jbig2SymbolDictionarySegment segment,
        Jbig2BitReader reader,
        IReadOnlyList<Jbig2Bitmap> symbols,
        int symbolWidth,
        int symbolHeight,
        uint instanceCount,
        int symbolCodeLength,
        Jbig2ArithmeticContextState refinementContextState)
    {
        var aggregateSegment = new Jbig2TextRegionSegment(
            Region: new Jbig2RegionSegmentInformation(
                (uint)symbolWidth,
                (uint)symbolHeight,
                0,
                0,
                Jbig2CombinationOperator.Or),
            IsHuffmanEncoded: true,
            UseRefinement: true,
            LogSbStrips: 0,
            ReferenceCorner: 1,
            IsTransposed: false,
            CombinationOperator: Jbig2CombinationOperator.Or,
            DefaultPixel: 0,
            SbDsOffset: 0,
            SbrTemplate: segment.SdrTemplate,
            HuffmanFlags: new Jbig2TextRegionHuffmanFlags(0, 0, 0, 0, 0, 0, 0, 0),
            RefinementAdaptiveTemplatePixels: segment.RefinementAdaptiveTemplatePixels,
            DeclaredSymbolInstanceCount: instanceCount,
            SymbolInstanceCount: instanceCount,
            PayloadDataOffset: 0,
            PayloadDataLength: 0);

        return Jbig2TextRegionDecoder.DecodeHuffmanAggregateWithReader(
            aggregateSegment,
            reader,
            symbols,
            symbolCodeLength,
            refinementContextState);
    }

    private static bool[] DecodeArithmeticExportFlags(IJbig2ArithmeticDecoder decoder, int totalSymbols)
    {
        var runLengthDecoder = new Jbig2ArithmeticIntegerDecoder(decoder, IaexContextBase);
        var flags = new bool[totalSymbols];
        int index = 0;
        bool currentFlag = false;

        while (index < totalSymbols)
        {
            long? runLengthValue = runLengthDecoder.Decode();
            if (!runLengthValue.HasValue)
                throw new InvalidOperationException("JBIG2 symbol dictionary export run length decoded as OOB");
            if (runLengthValue.Value < 0 || runLengthValue.Value > totalSymbols - index)
                throw new InvalidOperationException("Invalid JBIG2 symbol dictionary export run length");

            int runLength = (int)runLengthValue.Value;
            for (int i = 0; i < runLength; i++)
                flags[index + i] = currentFlag;

            index += runLength;
            currentFlag = !currentFlag;
        }

        return flags;
    }

    private static bool[] DecodeArithmeticExportFlagsOrAll(
        IJbig2ArithmeticDecoder decoder,
        int totalSymbols,
        uint exportedSymbolCount)
    {
        try
        {
            return DecodeArithmeticExportFlags(decoder, totalSymbols);
        }
        // Some producer streams omit/underrun the final arithmetic export-run
        // when every available symbol is exported. Do not infer partial exports.
        catch (InvalidOperationException) when (exportedSymbolCount == totalSymbols)
        {
            return Enumerable.Repeat(true, totalSymbols).ToArray();
        }
    }

    private static long DecodeRequired(Jbig2ArithmeticIntegerDecoder decoder, string fieldName)
        => decoder.Decode() ?? throw new InvalidOperationException($"JBIG2 {fieldName} decoded as OOB");

    private static long DecodeRequired(Jbig2HuffmanTable table, Jbig2BitReader reader, string fieldName)
        => table.Decode(reader) ?? throw new InvalidOperationException($"JBIG2 {fieldName} decoded as OOB");

    private static int GetSymbolCodeLength(int symbolCount)
    {
        if (symbolCount <= 1)
            return 1;

        int length = 0;
        int value = 1;
        while (value < symbolCount)
        {
            length++;
            value <<= 1;
        }

        if (length > MaxIaidContextBits)
            throw new InvalidOperationException("JBIG2 symbol-dictionary symbol-code length exceeds supported limits");

        return length;
    }

    private static int GetHuffmanFixedSymbolCodeLength(int symbolCount)
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

        if (length > 32)
            throw new InvalidOperationException("JBIG2 symbol-dictionary fixed symbol-code length exceeds supported limits");

        return length;
    }

    private static Jbig2HuffmanTable ResolveDecodeHeightTable(
        Jbig2SymbolDictionarySegment segment,
        UserHuffmanTableCursor userTables)
        => segment.SdHuffDecodeHeightSelection switch
        {
            0 => Jbig2StandardHuffmanTables.Get(4),
            1 => Jbig2StandardHuffmanTables.Get(5),
            2 => Jbig2StandardHuffmanTables.Get(6),
            3 => userTables.Next("symbol dictionary height delta"),
            _ => throw new InvalidOperationException("Invalid JBIG2 symbol dictionary height Huffman table selector"),
        };

    private static Jbig2HuffmanTable ResolveDecodeWidthTable(
        Jbig2SymbolDictionarySegment segment,
        UserHuffmanTableCursor userTables)
        => segment.SdHuffDecodeWidthSelection switch
        {
            0 => Jbig2StandardHuffmanTables.Get(2),
            1 => Jbig2StandardHuffmanTables.Get(3),
            2 => Jbig2StandardHuffmanTables.Get(4),
            3 => userTables.Next("symbol dictionary width delta"),
            _ => throw new InvalidOperationException("Invalid JBIG2 symbol dictionary width Huffman table selector"),
        };

    private static Jbig2HuffmanTable ResolveBitmapSizeTable(
        Jbig2SymbolDictionarySegment segment,
        UserHuffmanTableCursor userTables)
        => segment.SdHuffBmSizeSelection switch
        {
            0 => Jbig2StandardHuffmanTables.Get(1),
            1 => userTables.Next("symbol dictionary collective bitmap size"),
            _ => throw new InvalidOperationException("Invalid JBIG2 symbol dictionary bitmap-size Huffman table selector"),
        };

    private static Jbig2HuffmanTable ResolveAggregateInstanceTable(
        Jbig2SymbolDictionarySegment segment,
        UserHuffmanTableCursor userTables)
        => segment.SdHuffAggInstanceSelection switch
        {
            0 => Jbig2StandardHuffmanTables.Get(1),
            1 => userTables.Next("symbol dictionary refinement aggregate instance count"),
            _ => throw new InvalidOperationException("Invalid JBIG2 symbol dictionary aggregate-instance Huffman table selector"),
        };

    private static Jbig2Bitmap DecodeHeightClassCollectiveBitmap(
        Jbig2BitReader reader,
        long bitmapSize,
        int totalWidth,
        int height)
    {
        if (bitmapSize != 0)
        {
            if (bitmapSize > int.MaxValue)
                throw new InvalidOperationException("JBIG2 symbol dictionary collective bitmap size exceeds supported limits");

            byte[] mmrData = reader.ReadAlignedBytes((int)bitmapSize);
            return new Jbig2Bitmap(totalWidth, height, Jbig2MmrDecoder.Decode(mmrData, totalWidth, height));
        }
        if (totalWidth <= 0 || height <= 0)
            throw new InvalidOperationException("Invalid JBIG2 symbol dictionary collective bitmap dimensions");

        var bitmap = new Jbig2Bitmap(totalWidth, height);
        for (int i = 0; i < bitmap.Data.Length; i++)
            bitmap.Data[i] = reader.ReadAlignedByte();

        return bitmap;
    }

    private static Jbig2Bitmap ExtractSymbol(Jbig2Bitmap collectiveBitmap, IReadOnlyList<int> widths, int index, int height)
    {
        int x = 0;
        for (int i = 0; i < index; i++)
            x += widths[i];

        var symbol = new Jbig2Bitmap(widths[index], height);
        for (int yy = 0; yy < height; yy++)
        {
            for (int xx = 0; xx < symbol.Width; xx++)
                symbol.SetPixel(xx, yy, collectiveBitmap.GetPixel(x + xx, yy));
        }

        return symbol;
    }

    private static bool[] DecodeExportFlags(Jbig2BitReader reader, int totalSymbols)
    {
        var flags = new bool[totalSymbols];
        int index = 0;
        bool currentFlag = false;

        while (index < totalSymbols)
        {
            long? runLengthValue = Jbig2StandardHuffmanTables.Get(1).Decode(reader);
            if (!runLengthValue.HasValue)
                throw new InvalidOperationException("JBIG2 symbol dictionary export run length decoded as OOB");
            if (runLengthValue.Value < 0 || runLengthValue.Value > totalSymbols - index)
                throw new InvalidOperationException("Invalid JBIG2 symbol dictionary export run length");

            int runLength = (int)runLengthValue.Value;
            for (int i = 0; i < runLength; i++)
                flags[index + i] = currentFlag;

            index += runLength;
            currentFlag = !currentFlag;
        }

        return flags;
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

    private sealed class ArithmeticSymbolRefinementDecoders
    {
        private readonly Jbig2IaidDecoder _iaid;
        private readonly int _symbolCodeLength;

        public ArithmeticSymbolRefinementDecoders(IJbig2ArithmeticDecoder decoder, int symbolCodeLength)
        {
            Decoder = decoder;
            AggregationInstanceCount = new Jbig2ArithmeticIntegerDecoder(decoder, IaaiContextBase);
            _iaid = new Jbig2IaidDecoder(decoder, IaidContextBase);
            _symbolCodeLength = symbolCodeLength;
            ReferenceDeltaX = new Jbig2ArithmeticIntegerDecoder(decoder, IardxContextBase);
            ReferenceDeltaY = new Jbig2ArithmeticIntegerDecoder(decoder, IardyContextBase);
        }

        public IJbig2ArithmeticDecoder Decoder { get; }
        public Jbig2ArithmeticIntegerDecoder AggregationInstanceCount { get; }
        public Jbig2ArithmeticIntegerDecoder ReferenceDeltaX { get; }
        public Jbig2ArithmeticIntegerDecoder ReferenceDeltaY { get; }

        public uint DecodeSymbolId()
            => _iaid.Decode(_symbolCodeLength);

        public int SymbolCodeLength => _symbolCodeLength;
    }
}
