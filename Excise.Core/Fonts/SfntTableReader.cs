using System.Text;

namespace Excise.Core.Fonts;

internal static class SfntTableReader
{
    public static byte[]? ExtractTable(byte[] data, string tag)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(tag);

        if (data.Length < 12 || tag.Length != 4)
            return null;

        int numTables = ReadU16(data, 4);
        for (int i = 0, p = 12; i < numTables && p + 16 <= data.Length; i++, p += 16)
        {
            if (Encoding.ASCII.GetString(data, p, 4) != tag)
                continue;

            int offset = ReadU32(data, p + 8);
            int length = ReadU32(data, p + 12);
            if (offset < 0 || length <= 0 || (long)offset + length > data.Length)
                return null;

            var table = new byte[length];
            Array.Copy(data, offset, table, 0, length);
            return table;
        }

        return null;
    }

    private static int ReadU16(byte[] data, int offset) =>
        (data[offset] << 8) | data[offset + 1];

    private static int ReadU32(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
}
