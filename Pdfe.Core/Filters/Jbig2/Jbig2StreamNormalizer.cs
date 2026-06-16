using System;

namespace Pdfe.Core.Filters.Jbig2;

internal static class Jbig2StreamNormalizer
{
    private static readonly byte[] FileHeaderId =
    {
        0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A
    };

    public static byte[] CombineGlobalsAndData(byte[]? globals, byte[] data)
    {
        if (globals == null || globals.Length == 0)
            return data;

        byte[] combined = new byte[globals.Length + data.Length];
        Array.Copy(globals, 0, combined, 0, globals.Length);
        Array.Copy(data, 0, combined, globals.Length, data.Length);
        return combined;
    }

    public static byte[] NormalizeFileHeader(byte[] data)
    {
        if (!HasFileHeader(data))
            return data;

        if (data.Length < 9)
            throw new InvalidOperationException("Truncated JBIG2 file header");

        var headerFlags = data[8];
        var isSequential = (headerFlags & 0x01) != 0;
        if (!isSequential)
            throw new NotSupportedException("Random-access JBIG2 file organization is not supported");

        var amountOfPagesUnknown = (headerFlags & 0x02) != 0;
        var headerLength = amountOfPagesUnknown ? 9 : 13;
        if (data.Length < headerLength)
            throw new InvalidOperationException("Truncated JBIG2 page-count field");

        var normalized = new byte[data.Length - headerLength];
        Array.Copy(data, headerLength, normalized, 0, normalized.Length);
        return normalized;
    }

    private static bool HasFileHeader(byte[] data)
    {
        if (data.Length < FileHeaderId.Length)
            return false;

        for (var i = 0; i < FileHeaderId.Length; i++)
        {
            if (data[i] != FileHeaderId[i])
                return false;
        }

        return true;
    }
}
