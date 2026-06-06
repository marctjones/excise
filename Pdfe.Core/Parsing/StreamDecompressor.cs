using System.IO.Compression;
using Pdfe.Core.Primitives;

namespace Pdfe.Core.Parsing;

/// <summary>
/// Decompresses PDF streams by applying filters.
/// ISO 32000-2:2020 Section 7.4.
/// </summary>
public class StreamDecompressor
{
    /// <summary>
    /// Decompress a stream in place, setting its DecodedData property.
    /// </summary>
    public void Decompress(PdfStream stream)
    {
        var data = stream.EncodedData;
        var filters = stream.Filters;
        var parms = stream.DecodeParams;

        // Apply filters in order
        for (int i = 0; i < filters.Count; i++)
        {
            var filter = filters[i];
            var filterParms = i < parms.Count ? parms[i] : null;

            // JBIG2/JPX need the image's pixel dimensions (from the stream dict,
            // not the filter parms), so they're handled here where the dict is
            // in scope rather than in the generic ApplyFilter.
            data = filter switch
            {
                "JBIG2Decode" => DecodeJBIG2(data, stream),
                "JPXDecode" => DecodeJPX(data),
                _ => ApplyFilter(filter, data, filterParms)
            };
        }

        stream.SetDecodedData(data);
    }

    /// <summary>
    /// Apply a single filter to data.
    /// </summary>
    public byte[] ApplyFilter(string filterName, byte[] data, PdfDictionary? parms)
    {
        return filterName switch
        {
            "FlateDecode" or "Fl" => DecodeFlateDecode(data, parms),
            "ASCIIHexDecode" or "AHx" => DecodeASCIIHex(data),
            "ASCII85Decode" or "A85" => DecodeASCII85(data),
            "LZWDecode" or "LZW" => DecodeLZW(data, parms),
            "RunLengthDecode" or "RL" => DecodeRunLength(data),
            "DCTDecode" or "DCT" => data, // JPEG — decoded by the renderer (SkiaSharp)
            "JPXDecode" => DecodeJPX(data),
            "CCITTFaxDecode" or "CCF" => DecodeCCITTFax(data, parms),
            "JBIG2Decode" => data, // handled in Decompress (needs image /Width /Height)
            "BrotliDecode" => DecodeBrotli(data),
            "Crypt" => data, // Encryption handled separately
            _ => throw new NotSupportedException($"Unknown filter: {filterName}")
        };
    }

    /// <summary>
    /// Decode a /JBIG2Decode image to a 1-bpp bitmap. Width/Height come from the
    /// image stream dict. /JBIG2Globals (shared symbol dictionaries) isn't
    /// resolved here — it's only needed by the symbol/text-region path, which the
    /// decoder doesn't yet support; generic-region images decode without it.
    /// Any unsupported feature or malformed data falls back to the encoded bytes
    /// (so a renderer can skip the image rather than crash). #325.
    /// </summary>
    private byte[] DecodeJBIG2(byte[] data, PdfStream stream)
    {
        int width = stream.GetInt("Width", 0);
        int height = stream.GetInt("Height", 0);
        if (width <= 0 || height <= 0) return data;
        try
        {
            return Pdfe.Core.Filters.Jbig2.Jbig2Decoder.Decode(data, null, width, height);
        }
        catch
        {
            return data;
        }
    }

    /// <summary>
    /// Decode a /JPXDecode (JPEG2000) image. Full entropy/wavelet decode is not
    /// implemented (a large, rare codec Skia can't help with either), so this
    /// currently falls back to the raw codestream; the parser/metadata exist for
    /// callers via <see cref="Pdfe.Core.Filters.Jpx.JpxDecoder.ReadInfo"/>. #325.
    /// </summary>
    private byte[] DecodeJPX(byte[] data)
    {
        try
        {
            return Pdfe.Core.Filters.Jpx.JpxDecoder.Decode(data).Pixels;
        }
        catch
        {
            return data;
        }
    }

    /// <summary>
    /// Decompress a Brotli-encoded stream. PDF 2.0 added /BrotliDecode
    /// as an experimental filter; pdf.js's <c>Brotli-Prototype-FileA.pdf</c>
    /// fixture exercises this path. .NET's BrotliStream gives us the
    /// decoder for free — same shape as DecodeFlateDecode.
    /// </summary>
    private static byte[] DecodeBrotli(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var brotli = new System.IO.Compression.BrotliStream(
            input, System.IO.Compression.CompressionMode.Decompress, leaveOpen: false);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// Decode FlateDecode (zlib/deflate compression).
    /// </summary>
    private byte[] DecodeFlateDecode(byte[] data, PdfDictionary? parms)
    {
        // Skip zlib header if present (2 bytes: CMF, FLG)
        int offset = 0;
        if (data.Length >= 2)
        {
            int cmf = data[0];
            int flg = data[1];
            // Check for valid zlib header
            if ((cmf * 256 + flg) % 31 == 0)
            {
                int cm = cmf & 0x0F;
                if (cm == 8) // Deflate
                {
                    offset = 2;
                    // Check for dictionary
                    if ((flg & 0x20) != 0)
                        offset += 4;
                }
            }
        }

        using var input = new MemoryStream(data, offset, data.Length - offset);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        deflate.CopyTo(output);
        var decompressed = output.ToArray();

        // Apply predictor if specified
        if (parms != null && parms.ContainsKey("Predictor"))
        {
            int predictor = parms.GetInt("Predictor", 1);
            if (predictor > 1)
            {
                decompressed = ApplyPredictor(decompressed, parms);
            }
        }

        return decompressed;
    }

    /// <summary>
    /// Apply PNG predictor to decompressed data.
    /// </summary>
    private byte[] ApplyPredictor(byte[] data, PdfDictionary parms)
    {
        int predictor = parms.GetInt("Predictor", 1);
        int colors = parms.GetInt("Colors", 1);
        int bitsPerComponent = parms.GetInt("BitsPerComponent", 8);
        int columns = parms.GetInt("Columns", 1);

        // Calculate bytes per row
        int bytesPerPixel = (colors * bitsPerComponent + 7) / 8;
        int rowBytes = (colors * columns * bitsPerComponent + 7) / 8;

        if (predictor == 2)
        {
            // TIFF Predictor 2
            return ApplyTiffPredictor(data, colors, columns, bitsPerComponent);
        }
        else if (predictor >= 10 && predictor <= 15)
        {
            // PNG predictors
            return ApplyPngPredictor(data, rowBytes, bytesPerPixel);
        }

        return data;
    }

    /// <summary>
    /// Apply PNG predictor filters.
    /// </summary>
    private byte[] ApplyPngPredictor(byte[] data, int rowBytes, int bytesPerPixel)
    {
        // PNG predictor: each row starts with a filter byte
        int rowStride = rowBytes + 1; // +1 for filter byte
        int rows = data.Length / rowStride;

        var output = new byte[rows * rowBytes];
        var prevRow = new byte[rowBytes];

        for (int row = 0; row < rows; row++)
        {
            int srcOffset = row * rowStride;
            int dstOffset = row * rowBytes;

            int filter = data[srcOffset];
            var currentRow = new byte[rowBytes];

            for (int i = 0; i < rowBytes; i++)
            {
                byte raw = data[srcOffset + 1 + i];
                byte left = i >= bytesPerPixel ? currentRow[i - bytesPerPixel] : (byte)0;
                byte up = prevRow[i];
                byte upLeft = i >= bytesPerPixel ? prevRow[i - bytesPerPixel] : (byte)0;

                currentRow[i] = filter switch
                {
                    0 => raw, // None
                    1 => (byte)(raw + left), // Sub
                    2 => (byte)(raw + up), // Up
                    3 => (byte)(raw + (left + up) / 2), // Average
                    4 => (byte)(raw + PaethPredictor(left, up, upLeft)), // Paeth
                    _ => raw
                };

                output[dstOffset + i] = currentRow[i];
            }

            Array.Copy(currentRow, prevRow, rowBytes);
        }

        return output;
    }

    /// <summary>
    /// Paeth predictor function used in PNG filtering.
    /// </summary>
    private static byte PaethPredictor(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);

        if (pa <= pb && pa <= pc)
            return a;
        if (pb <= pc)
            return b;
        return c;
    }

    /// <summary>
    /// Apply TIFF Predictor 2 (horizontal differencing).
    /// </summary>
    private byte[] ApplyTiffPredictor(byte[] data, int colors, int columns, int bitsPerComponent)
    {
        if (bitsPerComponent != 8)
            return data; // Only support 8-bit for now

        int bytesPerRow = colors * columns;
        int rows = data.Length / bytesPerRow;
        var output = new byte[data.Length];

        for (int row = 0; row < rows; row++)
        {
            int rowOffset = row * bytesPerRow;

            for (int col = 0; col < columns; col++)
            {
                for (int comp = 0; comp < colors; comp++)
                {
                    int idx = rowOffset + col * colors + comp;
                    if (col == 0)
                    {
                        output[idx] = data[idx];
                    }
                    else
                    {
                        int prevIdx = rowOffset + (col - 1) * colors + comp;
                        output[idx] = (byte)(data[idx] + output[prevIdx]);
                    }
                }
            }
        }

        return output;
    }

    /// <summary>
    /// Decode ASCIIHexDecode (hex encoding).
    /// </summary>
    private byte[] DecodeASCIIHex(byte[] data)
    {
        var output = new List<byte>();
        int highNibble = -1;

        foreach (byte b in data)
        {
            if (b == '>') // End of data
                break;

            if (char.IsWhiteSpace((char)b))
                continue;

            int nibble = HexValue((char)b);
            if (nibble < 0)
                throw new PdfParseException($"Invalid hex digit in ASCIIHexDecode: {(char)b}");

            if (highNibble < 0)
            {
                highNibble = nibble;
            }
            else
            {
                output.Add((byte)((highNibble << 4) | nibble));
                highNibble = -1;
            }
        }

        // Handle odd number of digits
        if (highNibble >= 0)
        {
            output.Add((byte)(highNibble << 4));
        }

        return output.ToArray();
    }

    /// <summary>
    /// Decode ASCII85Decode (base-85 encoding).
    /// </summary>
    private byte[] DecodeASCII85(byte[] data)
    {
        var output = new List<byte>();
        var group = new List<int>();

        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];

            // Skip whitespace
            if (char.IsWhiteSpace((char)b))
                continue;

            // End of data
            if (b == '~' && i + 1 < data.Length && data[i + 1] == '>')
                break;

            // Special case: 'z' represents four zero bytes
            if (b == 'z')
            {
                if (group.Count > 0)
                    throw new PdfParseException("Invalid 'z' in ASCII85 group");

                output.AddRange(new byte[4]);
                continue;
            }

            if (b < 33 || b > 117)
                throw new PdfParseException($"Invalid character in ASCII85: {(char)b}");

            group.Add(b - 33);

            if (group.Count == 5)
            {
                DecodeASCII85Group(group, output, 4);
                group.Clear();
            }
        }

        // Handle partial group at end
        if (group.Count > 0)
        {
            int originalCount = group.Count;
            while (group.Count < 5)
                group.Add(84); // Pad with 'u' (highest value)

            int outBytes = originalCount - 1; // Partial group produces n-1 bytes
            if (outBytes > 0)
                DecodeASCII85Group(group, output, outBytes);
        }

        return output.ToArray();
    }

    private void DecodeASCII85Group(List<int> group, List<byte> output, int numBytes)
    {
        long value = 0;
        for (int i = 0; i < 5; i++)
        {
            value = value * 85 + group[i];
        }

        // Extract bytes (big-endian)
        var bytes = new byte[4];
        for (int i = 3; i >= 0; i--)
        {
            bytes[i] = (byte)(value & 0xFF);
            value >>= 8;
        }

        output.AddRange(bytes.Take(numBytes));
    }

    /// <summary>
    /// Decode LZWDecode compression.
    /// </summary>
    private byte[] DecodeLZW(byte[] data, PdfDictionary? parms)
    {
        // LZW implementation
        var output = new List<byte>();
        var table = new Dictionary<int, byte[]>();

        // Initialize table with single-byte codes
        for (int i = 0; i < 256; i++)
        {
            table[i] = new[] { (byte)i };
        }

        int clearCode = 256;
        int eoiCode = 257;
        int nextCode = 258;
        int codeSize = 9;

        int bitPos = 0;
        int prevCode = -1;

        while (bitPos + codeSize <= data.Length * 8)
        {
            int code = ReadBits(data, bitPos, codeSize);
            bitPos += codeSize;

            if (code == eoiCode)
                break;

            if (code == clearCode)
            {
                // Reset table
                table.Clear();
                for (int i = 0; i < 256; i++)
                {
                    table[i] = new[] { (byte)i };
                }
                nextCode = 258;
                codeSize = 9;
                prevCode = -1;
                continue;
            }

            byte[] entry;
            if (table.TryGetValue(code, out var existing))
            {
                entry = existing;
            }
            else if (code == nextCode && prevCode >= 0)
            {
                // Special case: code not in table yet
                var prev = table[prevCode];
                entry = new byte[prev.Length + 1];
                Array.Copy(prev, entry, prev.Length);
                entry[prev.Length] = prev[0];
            }
            else
            {
                throw new PdfParseException($"Invalid LZW code: {code}");
            }

            output.AddRange(entry);

            // Add new entry to table
            if (prevCode >= 0 && nextCode < 4096)
            {
                var prev = table[prevCode];
                var newEntry = new byte[prev.Length + 1];
                Array.Copy(prev, newEntry, prev.Length);
                newEntry[prev.Length] = entry[0];
                table[nextCode] = newEntry;
                nextCode++;

                // Increase code size if needed
                if (nextCode >= (1 << codeSize) && codeSize < 12)
                    codeSize++;
            }

            prevCode = code;
        }

        var result = output.ToArray();

        // Apply predictor if specified
        if (parms != null && parms.ContainsKey("Predictor"))
        {
            int predictor = parms.GetInt("Predictor", 1);
            if (predictor > 1)
            {
                result = ApplyPredictor(result, parms);
            }
        }

        return result;
    }

    private static int ReadBits(byte[] data, int bitPos, int numBits)
    {
        int result = 0;
        for (int i = 0; i < numBits; i++)
        {
            int byteIdx = (bitPos + i) / 8;
            int bitIdx = 7 - ((bitPos + i) % 8);
            if (byteIdx < data.Length)
            {
                if ((data[byteIdx] & (1 << bitIdx)) != 0)
                {
                    result |= (1 << (numBits - 1 - i));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Decode RunLengthDecode compression.
    /// </summary>
    private byte[] DecodeRunLength(byte[] data)
    {
        var output = new List<byte>();
        int i = 0;

        while (i < data.Length)
        {
            int length = data[i++];

            if (length == 128) // EOD
                break;

            if (length < 128)
            {
                // Copy next (length + 1) bytes literally
                int count = length + 1;
                for (int j = 0; j < count && i < data.Length; j++)
                {
                    output.Add(data[i++]);
                }
            }
            else
            {
                // Repeat next byte (257 - length) times
                int count = 257 - length;
                if (i < data.Length)
                {
                    byte b = data[i++];
                    for (int j = 0; j < count; j++)
                    {
                        output.Add(b);
                    }
                }
            }
        }

        return output.ToArray();
    }

    private static int HexValue(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        return -1;
    }

    /// <summary>
    /// Decode CCITTFaxDecode (Group 3 and Group 4 fax encoding).
    /// ISO 32000-2:2020 Section 8.3.5.
    /// </summary>
    private byte[] DecodeCCITTFax(byte[] data, PdfDictionary? parms)
    {
        try
        {
            int K = parms?.GetInt("K", 0) ?? 0;
            int columns = parms?.GetInt("Columns", 1728) ?? 1728;
            int rows = parms?.GetInt("Rows", 0) ?? 0;
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
            int code = reader.PeekBits(6);

            if (code == 0b000001)
            {
                reader.ReadBits(6);
                break;
            }

            if ((code & 0b111100) == 0b0001)
            {
                reader.ReadBits(4);
                int b1 = FindNextRun(refRow, a0 + 1, !color);
                int b2 = FindNextRun(refRow, b1 + 1, color);
                a0 = b2;
            }
            else if ((code & 0b111) == 0b001)
            {
                reader.ReadBits(3);
                int a1 = FindNextRun(row, a0 + 1, color);
                int a2 = FindNextRun(row, a1 + 1, !color);
                FillRun(row, a0 + 1, a1, color);
                a0 = a2;
                color = !color;
            }
            else
            {
                int len = DecodeHuffmanRun(reader, color, CcittTables.Group4Vertical);
                if (len < 0)
                    break;

                FillRun(row, a0 + 1, a0 + 1 + len, color);
                a0 = a0 + len;
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
                SkipEOL(reader);
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
            var reader = new CcittBitReader(data);
            var output = new List<byte>();
            var bytesPerRow = (columns + 7) / 8;

            bool[] refRow = new bool[columns];
            int rowsDecoded = 0;
            int rowsInGroup = 0;

            while (reader.HasBits && (rows == 0 || rowsDecoded < rows))
            {
                int bitsBefore = reader.Position;
                if (rowsInGroup == 0)
                {
                    var row = Decode1DRow(reader, columns, false);
                    if (row == null)
                        break;
                    if (reader.Position == bitsBefore)
                        break;

                    AppendRowToOutput(output, row, bytesPerRow, blackIs1);
                    refRow = row;
                    rowsDecoded++;
                    SkipEOL(reader);
                    rowsInGroup++;
                }
                else if (rowsInGroup < K)
                {
                    var row = DecodeGroup3_2DRow(reader, refRow, columns);
                    if (row == null)
                        break;
                    if (reader.Position == bitsBefore)
                        break;

                    AppendRowToOutput(output, row, bytesPerRow, blackIs1);
                    refRow = row;
                    rowsDecoded++;
                    SkipEOL(reader);
                    rowsInGroup++;
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

            int code = reader.PeekBits(6);

            if ((code & 0b111) == 0b001)
            {
                reader.ReadBits(3);
                int a1 = FindNextRun(row, a0 + 1, color);
                int a2 = FindNextRun(row, a1 + 1, !color);
                FillRun(row, a0 + 1, a1, color);
                a0 = a2;
                color = !color;
            }
            else
            {
                int b1 = FindNextRun(refRow, a0 + 1, !color);
                if (b1 >= columns)
                {
                    a0 = columns;
                    break;
                }

                int diff = b1 - a0 - 1;
                if (diff == 0)
                {
                    reader.ReadBits(1);
                    a0 = b1;
                }
                else if (diff > 0 && diff <= 3)
                {
                    int v = reader.ReadBits(3);
                    a0 = b1 + (v - 1);
                }
                else if (diff < 0 && diff >= -3)
                {
                    int v = reader.ReadBits(3);
                    a0 = b1 - (v - 1);
                }
                else
                {
                    int len = DecodeHuffmanRun(reader, color, CcittTables.WhiteTerminating);
                    if (len < 0)
                        break;

                    FillRun(row, a0 + 1, a0 + 1 + len, color);
                    a0 = a0 + len;
                    color = !color;
                }
            }
        }

        return row;
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
            int code = reader.PeekBits(12);
            if (code < 0)
                return -1;

            bool found = false;

            for (int bits = 2; bits <= 12; bits++)
            {
                int masked = (code >> (12 - bits)) & ((1 << bits) - 1);

                if (bits <= 9)
                {
                    var table = color ? CcittTables.BlackTerminating : CcittTables.WhiteTerminating;
                    if (masked < table.Length && table[masked].bits == bits)
                    {
                        reader.ReadBits(bits);
                        totalLen += masked;
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

    private void SkipEOL(CcittBitReader reader)
    {
        for (int i = 0; i < 12; i++)
        {
            if (!reader.HasBits)
                break;
            int b = reader.ReadBits(1);
            if (b != 0)
            {
                for (int j = i + 1; j < 12; j++)
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
            byte b = 0;
            for (int j = 0; j < 8 && i * 8 + j < row.Length; j++)
            {
                bool bit = row[i * 8 + j];
                if (blackIs1)
                {
                    bit = !bit;
                }
                if (bit)
                {
                    b |= (byte)(0x80 >> j);
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
