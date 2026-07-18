using System.IO.Compression;
using Excise.Core.Parsing;

namespace Excise.Core.Filters;

/// <summary>
/// Stateless PDF stream filters that do not need document or image-stream context.
/// </summary>
internal static class BasicStreamFilters
{
    public static byte[] DecodeAsciiHex(byte[] data)
    {
        var output = new List<byte>();
        int highNibble = -1;

        foreach (byte b in data)
        {
            if (b == '>')
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

        if (highNibble >= 0)
            output.Add((byte)(highNibble << 4));

        return output.ToArray();
    }

    public static byte[] DecodeAscii85(byte[] data)
    {
        var output = new List<byte>();
        var group = new List<int>();

        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];

            if (char.IsWhiteSpace((char)b))
                continue;

            if (b == '~' && i + 1 < data.Length && data[i + 1] == '>')
                break;

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
                DecodeAscii85Group(group, output, 4);
                group.Clear();
            }
        }

        if (group.Count > 0)
        {
            int originalCount = group.Count;
            while (group.Count < 5)
                group.Add(84);

            int outBytes = originalCount - 1;
            if (outBytes > 0)
                DecodeAscii85Group(group, output, outBytes);
        }

        return output.ToArray();
    }

    public static byte[] DecodeRunLength(byte[] data)
    {
        var output = new List<byte>();
        int i = 0;

        while (i < data.Length)
        {
            int length = data[i++];

            if (length == 128)
                break;

            if (length < 128)
            {
                int count = length + 1;
                for (int j = 0; j < count && i < data.Length; j++)
                    output.Add(data[i++]);
            }
            else
            {
                int count = 257 - length;
                if (i < data.Length)
                {
                    byte b = data[i++];
                    for (int j = 0; j < count; j++)
                        output.Add(b);
                }
            }
        }

        return output.ToArray();
    }

    public static byte[] DecodeBrotli(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: false);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    private static void DecodeAscii85Group(List<int> group, List<byte> output, int numBytes)
    {
        long value = 0;
        for (int i = 0; i < 5; i++)
            value = value * 85 + group[i];

        var bytes = new byte[4];
        for (int i = 3; i >= 0; i--)
        {
            bytes[i] = (byte)(value & 0xFF);
            value >>= 8;
        }

        output.AddRange(bytes.Take(numBytes));
    }

    private static int HexValue(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        return -1;
    }
}
