using Pdfe.Core.Primitives;

namespace Pdfe.Core.Filters.Ccitt;

/// <summary>
/// Decodes PDF /CCITTFaxDecode image data.
/// ISO 32000-2:2020 Section 8.3.5.
/// </summary>
internal sealed class CcittFaxFilterDecoder : AliasedFilterDecoder
{
    private const int EolCode = 0b000000000001;
    private const int EolBitCount = 12;

    public CcittFaxFilterDecoder()
        : base("CCITTFaxDecode", "CCF")
    {
    }

    public override byte[] Decode(byte[] data, PdfFilterDecodeContext context)
        => DecodeCCITTFax(data, context.DecodeParms, context.Stream);

    /// <summary>
    /// Decode CCITTFaxDecode (Group 3 and Group 4 fax encoding).
    /// ISO 32000-2:2020 Section 8.3.5.
    /// </summary>
    private byte[] DecodeCCITTFax(byte[] data, PdfDictionary? parms, PdfStream? stream)
    {
        try
        {
            int K = parms?.GetInt("K", 0) ?? 0;
            int columns = parms?.GetInt("Columns", 1728) ?? 1728;
            int rows = parms?.GetInt("Rows", 0) ?? 0;
            if (rows <= 0)
                rows = stream?.GetInt("Height", 0) ?? 0;
            bool blackIs1 = parms?.GetBool("BlackIs1", false) ?? false;

            if (K < 0)
            {
                return DecodeGroup4(data, columns, rows, blackIs1);
            }
            else if (K == 0)
            {
                return DecodeGroup3_1D(data, columns, rows, blackIs1);
            }
            else
            {
                return DecodeGroup3_2D(data, columns, rows, K, blackIs1);
            }
        }
        catch (Exception __ex) when (__ex is not OutOfMemoryException)
        {
            return new byte[0];
        }
    }

    /// <summary>
    /// Decode Group 4 (Modified READ / MMR) fax data.
    /// </summary>
    private byte[] DecodeGroup4(byte[] data, int columns, int rows, bool blackIs1)
    {
        try
        {
            if (rows <= 0)
                return Array.Empty<byte>();

            var reader = new CcittBitReader(data);
            var output = new List<byte>();
            var bytesPerRow = (columns + 7) / 8;

            bool[] refRow = new bool[columns];
            int rowsDecoded = 0;

            while (reader.HasBits && (rows == 0 || rowsDecoded < rows))
            {
                int bitsBefore = reader.Position;
                var currentRow = DecodeGroup4Row(reader, refRow, columns);
                if (currentRow == null)
                    break;

                // Malformed input: row decoder couldn't make any progress. Stop instead of looping.
                if (reader.Position == bitsBefore)
                    break;

                AppendRowToOutput(output, currentRow, bytesPerRow, blackIs1);
                refRow = currentRow;
                rowsDecoded++;
            }

            return output.ToArray();
        }
        catch (Exception __ex) when (__ex is not OutOfMemoryException)
        {
            return new byte[0];
        }
    }

    /// <summary>
    /// Decode a single Group 4 row using 2D MMR encoding.
    /// </summary>
    private bool[] DecodeGroup4Row(CcittBitReader reader, bool[] refRow, int columns)
    {
        var row = new bool[columns];
        int a0 = -1;
        bool color = false;

        while (a0 < columns - 1 && reader.HasBits)
        {
            var mode = ReadTwoDimensionalMode(reader);
            if (mode.Kind == Ccitt2DModeKind.Invalid || mode.Kind == Ccitt2DModeKind.Eofb)
                break;

            if (mode.Kind == Ccitt2DModeKind.Pass)
            {
                int b1 = FindNextRun(refRow, a0 + 1, !color);
                int b2 = FindNextRun(refRow, b1 + 1, color);
                a0 = b2;
            }
            else if (mode.Kind == Ccitt2DModeKind.Horizontal)
            {
                int len1 = DecodeHuffmanRun(reader, color, CcittTables.WhiteTerminating);
                int len2 = DecodeHuffmanRun(reader, !color, CcittTables.WhiteTerminating);
                if (len1 < 0 || len2 < 0)
                    break;

                int firstEnd = Math.Min(a0 + 1 + len1, columns);
                int secondEnd = Math.Min(firstEnd + len2, columns);
                FillRun(row, a0 + 1, firstEnd, color);
                FillRun(row, firstEnd, secondEnd, !color);
                a0 = secondEnd;
            }
            else
            {
                int b1 = FindNextRun(refRow, a0 + 1, !color);
                int a1 = Math.Clamp(b1 + mode.VerticalOffset, a0 + 1, columns);
                FillRun(row, a0 + 1, a1, color);
                a0 = a1;
                color = !color;
            }
        }

        return row;
    }

    /// <summary>
    /// Decode Group 3 1D (each row independently encoded).
    /// </summary>
    private byte[] DecodeGroup3_1D(byte[] data, int columns, int rows, bool blackIs1)
    {
        try
        {
            if (rows <= 0)
                return Array.Empty<byte>();

            var reader = new CcittBitReader(data);
            var output = new List<byte>();
            var bytesPerRow = (columns + 7) / 8;
            int rowsDecoded = 0;

            while (reader.HasBits && (rows == 0 || rowsDecoded < rows))
            {
                int bitsBefore = reader.Position;
                var row = Decode1DRow(reader, columns, false);
                if (row == null)
                    break;
                if (reader.Position == bitsBefore)
                    break;

                AppendRowToOutput(output, row, bytesPerRow, blackIs1);
                rowsDecoded++;
                TrySkipEOL(reader, maxFillBits: 7);
            }

            return output.ToArray();
        }
        catch (Exception __ex) when (__ex is not OutOfMemoryException)
        {
            return new byte[0];
        }
    }

    /// <summary>
    /// Decode Group 3 2D (K > 0: mixed 1D/2D encoding).
    /// </summary>
    private byte[] DecodeGroup3_2D(byte[] data, int columns, int rows, int K, bool blackIs1)
    {
        try
        {
            if (rows <= 0)
                return Array.Empty<byte>();

            var reader = new CcittBitReader(data);
            var output = new List<byte>();
            var bytesPerRow = (columns + 7) / 8;

            bool[] refRow = new bool[columns];
            int rowsDecoded = 0;
            int rowsInGroup = 0;

            while (reader.HasBits && (rows == 0 || rowsDecoded < rows))
            {
                int bitsBefore = reader.Position;
                var lineMode = ReadGroup3LineMode(reader);
                if (lineMode == CcittGroup3LineMode.EndOfData)
                    break;

                bool hasTaggedLineMode = lineMode != CcittGroup3LineMode.Untagged;
                bool decodeOneDimensional =
                    lineMode == CcittGroup3LineMode.OneDimensional ||
                    (!hasTaggedLineMode && rowsInGroup == 0);

                if (decodeOneDimensional)
                {
                    var row = Decode1DRow(reader, columns, false);
                    if (row == null)
                        break;
                    if (reader.Position == bitsBefore)
                        break;

                    AppendRowToOutput(output, row, bytesPerRow, blackIs1);
                    refRow = row;
                    rowsDecoded++;
                    if (!hasTaggedLineMode)
                    {
                        SkipExpectedEOL(reader);
                        rowsInGroup++;
                    }
                    else
                    {
                        rowsInGroup = 1;
                    }
                }
                else if (hasTaggedLineMode || rowsInGroup < K)
                {
                    var row = DecodeGroup3_2DRow(reader, refRow, columns);
                    if (row == null)
                        break;
                    if (reader.Position == bitsBefore)
                        break;

                    AppendRowToOutput(output, row, bytesPerRow, blackIs1);
                    refRow = row;
                    rowsDecoded++;
                    if (!hasTaggedLineMode)
                    {
                        SkipExpectedEOL(reader);
                        rowsInGroup++;
                    }
                    else
                    {
                        rowsInGroup++;
                    }
                }
                else
                {
                    rowsInGroup = 0;
                }
            }

            return output.ToArray();
        }
        catch (Exception __ex) when (__ex is not OutOfMemoryException)
        {
            return new byte[0];
        }
    }

    private static CcittGroup3LineMode ReadGroup3LineMode(CcittBitReader reader)
    {
        if (reader.PeekBits(EolBitCount) != EolCode)
            return CcittGroup3LineMode.Untagged;

        reader.ReadBits(EolBitCount);
        if (!reader.HasBits)
            return CcittGroup3LineMode.EndOfData;

        return reader.ReadBits(1) == 1
            ? CcittGroup3LineMode.OneDimensional
            : CcittGroup3LineMode.TwoDimensional;
    }

    /// <summary>
    /// Decode a single Group 3 2D row.
    /// </summary>
    private bool[] DecodeGroup3_2DRow(CcittBitReader reader, bool[] refRow, int columns)
    {
        var row = new bool[columns];
        int a0 = -1;
        bool color = false;

        while (a0 < columns - 1)
        {
            if (!reader.HasBits)
                break;

            var mode = ReadTwoDimensionalMode(reader);
            if (mode.Kind == Ccitt2DModeKind.Invalid || mode.Kind == Ccitt2DModeKind.Eofb)
                break;

            if (mode.Kind == Ccitt2DModeKind.Pass)
            {
                int b1 = FindNextRun(refRow, a0 + 1, !color);
                int b2 = FindNextRun(refRow, b1 + 1, color);
                a0 = b2;
            }
            else if (mode.Kind == Ccitt2DModeKind.Horizontal)
            {
                int len1 = DecodeHuffmanRun(reader, color, CcittTables.WhiteTerminating);
                int len2 = DecodeHuffmanRun(reader, !color, CcittTables.WhiteTerminating);
                if (len1 < 0 || len2 < 0)
                    break;

                int firstEnd = Math.Min(a0 + 1 + len1, columns);
                int secondEnd = Math.Min(firstEnd + len2, columns);
                FillRun(row, a0 + 1, firstEnd, color);
                FillRun(row, firstEnd, secondEnd, !color);
                a0 = secondEnd;
            }
            else
            {
                int b1 = FindNextRun(refRow, a0 + 1, !color);
                int a1 = Math.Clamp(b1 + mode.VerticalOffset, a0 + 1, columns);
                FillRun(row, a0 + 1, a1, color);
                a0 = a1;
                color = !color;
            }
        }

        return row;
    }

    private static Ccitt2DMode ReadTwoDimensionalMode(CcittBitReader reader)
    {
        if (reader.PeekBits(1) == 0b1)
        {
            reader.ReadBits(1);
            return new Ccitt2DMode(Ccitt2DModeKind.Vertical, 0);
        }

        int threeBits = reader.PeekBits(3);
        if (threeBits == 0b011)
        {
            reader.ReadBits(3);
            return new Ccitt2DMode(Ccitt2DModeKind.Vertical, 1);
        }
        if (threeBits == 0b010)
        {
            reader.ReadBits(3);
            return new Ccitt2DMode(Ccitt2DModeKind.Vertical, -1);
        }
        if (threeBits == 0b001)
        {
            reader.ReadBits(3);
            return new Ccitt2DMode(Ccitt2DModeKind.Horizontal, 0);
        }

        if (reader.PeekBits(4) == 0b0001)
        {
            reader.ReadBits(4);
            return new Ccitt2DMode(Ccitt2DModeKind.Pass, 0);
        }

        int sixBits = reader.PeekBits(6);
        if (sixBits == 0b000011)
        {
            reader.ReadBits(6);
            return new Ccitt2DMode(Ccitt2DModeKind.Vertical, 2);
        }
        if (sixBits == 0b000010)
        {
            reader.ReadBits(6);
            return new Ccitt2DMode(Ccitt2DModeKind.Vertical, -2);
        }

        int sevenBits = reader.PeekBits(7);
        if (sevenBits == 0b0000011)
        {
            reader.ReadBits(7);
            return new Ccitt2DMode(Ccitt2DModeKind.Vertical, 3);
        }
        if (sevenBits == 0b0000010)
        {
            reader.ReadBits(7);
            return new Ccitt2DMode(Ccitt2DModeKind.Vertical, -3);
        }

        if (reader.PeekBits(12) == 0b000000000001)
        {
            reader.ReadBits(12);
            return new Ccitt2DMode(Ccitt2DModeKind.Eofb, 0);
        }

        return new Ccitt2DMode(Ccitt2DModeKind.Invalid, 0);
    }

    private readonly record struct Ccitt2DMode(Ccitt2DModeKind Kind, int VerticalOffset);

    private enum Ccitt2DModeKind
    {
        Invalid,
        Eofb,
        Pass,
        Horizontal,
        Vertical
    }

    private enum CcittGroup3LineMode
    {
        Untagged,
        OneDimensional,
        TwoDimensional,
        EndOfData
    }

    /// <summary>
    /// Decode a 1D row (T4 Huffman-encoded runs).
    /// </summary>
    private bool[] Decode1DRow(CcittBitReader reader, int columns, bool currentColor)
    {
        var row = new bool[columns];
        int pos = 0;
        bool color = !currentColor;

        while (pos < columns && reader.HasBits)
        {
            color = !color;
            int len = DecodeHuffmanRun(reader, color, CcittTables.WhiteTerminating);
            if (len < 0)
                break;

            int end = Math.Min(pos + len, columns);
            FillRun(row, pos, end, color);
            pos = end;
        }

        return row;
    }

    /// <summary>
    /// Decode a Huffman-encoded run length (T4/T6 tables).
    /// </summary>
    private int DecodeHuffmanRun(CcittBitReader reader, bool color, (int code, int bits)[] terminating)
    {
        int totalLen = 0;

        while (true)
        {
            bool found = false;

            for (int bits = 2; bits <= 13; bits++)
            {
                int masked = reader.PeekBits(bits);
                if (masked < 0)
                    break;

                var table = color ? CcittTables.BlackTerminating : CcittTables.WhiteTerminating;
                for (int runLength = 0; runLength < table.Length; runLength++)
                {
                    if (table[runLength].bits == bits && table[runLength].code == masked)
                    {
                        reader.ReadBits(bits);
                        totalLen += runLength;
                        return totalLen;
                    }
                }

                var makeupTable = color ? CcittTables.BlackMakeup : CcittTables.WhiteMakeup;
                foreach (var kvp in makeupTable)
                {
                    if (kvp.Value.bits == bits && kvp.Value.code == masked)
                    {
                        reader.ReadBits(bits);
                        totalLen += kvp.Key;
                        found = true;
                        break;
                    }
                }

                if (found)
                    break;
            }

            if (!found)
                return totalLen > 0 ? totalLen : -1;
            if (totalLen >= 2560)
                break;
        }

        return totalLen;
    }

    private int FindNextRun(bool[] row, int startIdx, bool color)
    {
        for (int i = startIdx; i < row.Length; i++)
        {
            if (row[i] == color)
                return i;
        }
        return row.Length;
    }

    private void FillRun(bool[] row, int start, int end, bool color)
    {
        for (int i = start; i < end && i < row.Length; i++)
        {
            row[i] = color;
        }
    }

    private static bool TrySkipEOL(CcittBitReader reader, int maxFillBits)
    {
        for (int fillBits = 0; fillBits <= maxFillBits; fillBits++)
        {
            int bitsToRead = fillBits + EolBitCount;
            if (reader.PeekBits(bitsToRead) != EolCode)
                continue;

            reader.ReadBits(bitsToRead);
            return true;
        }

        return false;
    }

    private static void SkipExpectedEOL(CcittBitReader reader)
    {
        for (int i = 0; i < EolBitCount; i++)
        {
            if (!reader.HasBits)
                break;
            int bit = reader.ReadBits(1);
            if (bit != 0)
            {
                for (int j = i + 1; j < EolBitCount; j++)
                {
                    if (reader.HasBits)
                        reader.ReadBits(1);
                }
                break;
            }
        }
    }

    private void AppendRowToOutput(List<byte> output, bool[] row, int bytesPerRow, bool blackIs1)
    {
        for (int i = 0; i < bytesPerRow; i++)
        {
            byte b = blackIs1 ? (byte)0 : (byte)0xFF;
            for (int j = 0; j < 8 && i * 8 + j < row.Length; j++)
            {
                bool bit = row[i * 8 + j];
                if (!blackIs1)
                {
                    bit = !bit;
                }
                byte mask = (byte)(0x80 >> j);
                if (bit)
                {
                    b |= mask;
                }
                else
                {
                    b &= (byte)~mask;
                }
            }
            output.Add(b);
        }
    }

    /// <summary>
    /// Bit reader for CCITT Fax data (MSB-first).
    /// </summary>
    private class CcittBitReader
    {
        private readonly byte[] _data;
        private int _bitPos;

        public CcittBitReader(byte[] data)
        {
            _data = data;
            _bitPos = 0;
        }

        public bool HasBits => _bitPos < _data.Length * 8;

        public int Position => _bitPos;

        public int PeekBits(int count)
        {
            if (_bitPos + count > _data.Length * 8)
                return -1;

            int result = 0;
            for (int i = 0; i < count; i++)
            {
                int byteIdx = (_bitPos + i) / 8;
                int bitIdx = 7 - ((_bitPos + i) % 8);
                if (((_data[byteIdx] >> bitIdx) & 1) != 0)
                {
                    result |= (1 << (count - 1 - i));
                }
            }
            return result;
        }

        public int ReadBits(int count)
        {
            int result = PeekBits(count);
            if (result >= 0)
            {
                _bitPos += count;
            }
            return result;
        }
    }

    /// <summary>
    /// CCITT Fax Huffman tables (T.4 / T.6 standard).
    /// </summary>
    private static class CcittTables
    {
        public static readonly (int code, int bits)[] WhiteTerminating = new (int, int)[64]
        {
            (0b00110101, 8), (0b000111, 6), (0b0111, 4), (0b1000, 4),
            (0b1011, 4), (0b1100, 4), (0b1110, 4), (0b1111, 4),
            (0b10011, 5), (0b10100, 5), (0b00111, 5), (0b01000, 5),
            (0b001000, 6), (0b000011, 6), (0b110100, 6), (0b110101, 6),
            (0b101010, 6), (0b101011, 6), (0b0100111, 7), (0b0001100, 7),
            (0b0001000, 7), (0b0010111, 7), (0b0000011, 7), (0b0000100, 7),
            (0b0101000, 7), (0b0101011, 7), (0b0010011, 7), (0b0100100, 7),
            (0b0011000, 7), (0b00000010, 8), (0b00000011, 8), (0b00011010, 8),
            (0b00011011, 8), (0b00010010, 8), (0b00010011, 8), (0b00010100, 8),
            (0b00010101, 8), (0b00010110, 8), (0b00010111, 8), (0b00101000, 8),
            (0b00101001, 8), (0b00101010, 8), (0b00101011, 8), (0b00101100, 8),
            (0b00101101, 8), (0b00000100, 8), (0b00000101, 8), (0b00001010, 8),
            (0b00001011, 8), (0b01010010, 8), (0b01010011, 8), (0b01010100, 8),
            (0b01010101, 8), (0b00100100, 8), (0b00100101, 8), (0b01011000, 8),
            (0b01011001, 8), (0b01011010, 8), (0b01011011, 8), (0b01001010, 8),
            (0b01001011, 8), (0b00110010, 8), (0b00110011, 8), (0b00110100, 8),
        };

        public static readonly (int code, int bits)[] BlackTerminating = new (int, int)[64]
        {
            (0b0000110111, 10), (0b010, 3), (0b11, 2), (0b10, 2),
            (0b011, 3), (0b0011, 4), (0b0010, 4), (0b00011, 5),
            (0b000101, 6), (0b000100, 6), (0b0000100, 7), (0b0000101, 7),
            (0b0000111, 7), (0b00000100, 8), (0b00000111, 8), (0b000011000, 9),
            (0b0000010111, 10), (0b0000011000, 10), (0b0000001000, 10), (0b00001100111, 11),
            (0b00001101000, 11), (0b00001101100, 11), (0b00000110111, 11), (0b00000101000, 11),
            (0b00000010111, 11), (0b00000011000, 11), (0b000011001010, 12), (0b000011001011, 12),
            (0b000011001100, 12), (0b000011001101, 12), (0b000001101000, 12), (0b000001101001, 12),
            (0b000001101010, 12), (0b000001101011, 12), (0b000011010010, 12), (0b000011010011, 12),
            (0b000011010100, 12), (0b000011010101, 12), (0b000011010110, 12), (0b000011010111, 12),
            (0b000001101100, 12), (0b000001101101, 12), (0b000011011010, 12), (0b000011011011, 12),
            (0b000001010100, 12), (0b000001010101, 12), (0b000001010110, 12), (0b000001010111, 12),
            (0b000001100100, 12), (0b000001100101, 12), (0b000001010010, 12), (0b000001010011, 12),
            (0b000000100100, 12), (0b000000110111, 12), (0b000000111000, 12), (0b000000100111, 12),
            (0b000000101000, 12), (0b000001011000, 12), (0b000001011001, 12), (0b000000101011, 12),
            (0b000000101100, 12), (0b000001011010, 12), (0b000001100110, 12), (0b000001100111, 12),
        };

        public static readonly Dictionary<int, (int code, int bits)> WhiteMakeup = new()
        {
            {64,(0b11011, 5)}, {128,(0b10010, 5)}, {192,(0b010111, 6)},
            {256,(0b0110111, 7)}, {320,(0b00110110, 8)}, {384,(0b00110111, 8)},
            {448,(0b01100100, 8)}, {512,(0b01100101, 8)}, {576,(0b01101000, 8)},
            {640,(0b01100111, 8)}, {704,(0b011001100, 9)}, {768,(0b011001101, 9)},
            {832,(0b011010010, 9)}, {896,(0b011010011, 9)}, {960,(0b011010100, 9)},
            {1024,(0b011010101, 9)}, {1088,(0b011010110, 9)}, {1152,(0b011010111, 9)},
            {1216,(0b011011000, 9)}, {1280,(0b011011001, 9)}, {1344,(0b011011010, 9)},
            {1408,(0b011011011, 9)}, {1472,(0b011100100, 9)}, {1536,(0b011100101, 9)},
            {1600,(0b011100110, 9)}, {1664,(0b011100111, 9)}, {1728,(0b011101000, 9)},
            {1792,(0b00000001000, 11)}, {1856,(0b00000001001, 11)},
            {1920,(0b00000001010, 11)}, {1984,(0b00000001011, 11)},
            {2048,(0b00000001100, 11)}, {2112,(0b00000001101, 11)},
            {2176,(0b00000001110, 11)}, {2240,(0b00000001111, 11)},
            {2304,(0b00000010000, 11)}, {2368,(0b00000010001, 11)},
            {2432,(0b00000010010, 11)}, {2496,(0b00000010011, 11)},
            {2560,(0b00000010100, 11)},
        };

        public static readonly Dictionary<int, (int code, int bits)> BlackMakeup = new()
        {
            {64,(0b0000001111, 10)}, {128,(0b000011001000, 12)}, {192,(0b000011001001, 12)},
            {256,(0b000001011011, 12)}, {320,(0b000000110011, 12)}, {384,(0b000000110100, 12)},
            {448,(0b000000110101, 12)}, {512,(0b0000001101100, 13)}, {576,(0b0000001101101, 13)},
            {640,(0b0000001001010, 13)}, {704,(0b0000001001011, 13)}, {768,(0b0000001001100, 13)},
            {832,(0b0000001001101, 13)}, {896,(0b0000001110010, 13)}, {960,(0b0000001110011, 13)},
            {1024,(0b0000001110100, 13)}, {1088,(0b0000001110101, 13)}, {1152,(0b0000001110110, 13)},
            {1216,(0b0000001110111, 13)}, {1280,(0b0000001010010, 13)}, {1344,(0b0000001010011, 13)},
            {1408,(0b0000001010100, 13)}, {1472,(0b0000001010101, 13)}, {1536,(0b0000001011010, 13)},
            {1600,(0b0000001011011, 13)}, {1664,(0b0000001100100, 13)}, {1728,(0b0000001100101, 13)},
            {1792,(0b00000001000, 11)}, {1856,(0b00000001001, 11)},
            {1920,(0b00000001010, 11)}, {1984,(0b00000001011, 11)},
            {2048,(0b00000001100, 11)}, {2112,(0b00000001101, 11)},
            {2176,(0b00000001110, 11)}, {2240,(0b00000001111, 11)},
            {2304,(0b00000010000, 11)}, {2368,(0b00000010001, 11)},
            {2432,(0b00000010010, 11)}, {2496,(0b00000010011, 11)},
            {2560,(0b00000010100, 11)},
        };

        public static readonly (int code, int bits)[] Group4Vertical = new (int, int)[7]
        {
            (0b1, 1),
            (0b011, 3),
            (0b010, 3),
            (0b000011, 6),
            (0b000010, 6),
            (0b0000011, 7),
            (0b0000010, 7),
        };
    }
}
