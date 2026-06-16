using System;
using System.Collections.Generic;

namespace Pdfe.Core.Filters.Jbig2;

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
/// Decodes JBIG2 symbol dictionary segments for the Huffman no-refinement path.
/// Standard tables, referenced user tables, raw collective bitmaps, MMR
/// collective bitmaps, and imported symbols are handled here; arithmetic and
/// refinement/aggregate procedures stay gated until implemented separately.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class Jbig2SymbolDictionaryDecoder
{
    public static Jbig2DecodedSymbolDictionary Decode(
        Jbig2SymbolDictionarySegment segment,
        ReadOnlySpan<byte> payload,
        IReadOnlyList<Jbig2Bitmap> importedSymbols,
        IReadOnlyList<Jbig2HuffmanTable>? userTables = null)
    {
        if (!segment.IsHuffmanEncoded)
            throw new NotSupportedException("Arithmetic-coded JBIG2 symbol dictionaries are not yet supported");
        if (segment.UseRefinementAggregation)
            throw new NotSupportedException("Refinement/aggregate JBIG2 symbol dictionaries are not yet supported");
        if (segment.NewSymbolCount > int.MaxValue || segment.ExportedSymbolCount > int.MaxValue)
            throw new InvalidOperationException("JBIG2 symbol dictionary count exceeds supported limits");

        importedSymbols ??= Array.Empty<Jbig2Bitmap>();
        var tableCursor = new UserHuffmanTableCursor(userTables);
        Jbig2HuffmanTable heightTable = ResolveDecodeHeightTable(segment, tableCursor);
        Jbig2HuffmanTable widthTable = ResolveDecodeWidthTable(segment, tableCursor);
        Jbig2HuffmanTable bitmapSizeTable = ResolveBitmapSizeTable(segment, tableCursor);

        int newSymbolCount = checked((int)segment.NewSymbolCount);
        var newSymbols = new Jbig2Bitmap[newSymbolCount];
        var allSymbols = new List<Jbig2Bitmap>(importedSymbols.Count + newSymbolCount);
        allSymbols.AddRange(importedSymbols);

        var reader = new Jbig2BitReader(payload.ToArray());
        int decodedSymbols = 0;
        int heightClassHeight = 0;

        while (decodedSymbols < newSymbolCount)
        {
            long? deltaHeight = heightTable.Decode(reader);
            if (!deltaHeight.HasValue)
                throw new InvalidOperationException("JBIG2 symbol dictionary height class delta decoded as OOB");

            checked { heightClassHeight += (int)deltaHeight.Value; }
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

                checked { symbolWidth += (int)deltaWidth.Value; }
                if (symbolWidth <= 0)
                    throw new InvalidOperationException("Invalid JBIG2 symbol dictionary symbol width");

                widths.Add(symbolWidth);
                checked { totalWidth += symbolWidth; }
                decodedSymbols++;
            }

            if (widths.Count == 0)
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
}
