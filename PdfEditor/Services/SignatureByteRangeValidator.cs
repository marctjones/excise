using Pdfe.Core.Primitives;
using System;

namespace PdfEditor.Services;

internal sealed class SignatureByteRangeValidationResult
{
    public bool IsValid { get; init; }
    public long[] ByteRange { get; init; } = Array.Empty<long>();
    public byte[] SignedContent { get; init; } = Array.Empty<byte>();
    public string Error { get; init; } = string.Empty;
    public bool CoversWholeDocument { get; init; }
}

internal static class SignatureByteRangeValidator
{
    public static SignatureByteRangeValidationResult Validate(PdfArray byteRangeArray, byte[] fileBytes)
    {
        if (!TryReadByteRange(byteRangeArray, out var byteRange, out var error) ||
            !TryExtractSignedContent(fileBytes, byteRange, out var signedContent, out error) ||
            !TryValidateContentsGap(fileBytes, byteRange, out error))
        {
            return new SignatureByteRangeValidationResult { Error = error };
        }

        return new SignatureByteRangeValidationResult
        {
            IsValid = true,
            ByteRange = byteRange,
            SignedContent = signedContent,
            CoversWholeDocument = byteRange[0] == 0 &&
                                  byteRange[2] + byteRange[3] == fileBytes.Length
        };
    }

    private static bool TryReadByteRange(PdfArray byteRangeArray, out long[] byteRange, out string error)
    {
        byteRange = Array.Empty<long>();
        error = string.Empty;

        if (byteRangeArray.Count != 4)
        {
            error = "expected exactly four numbers";
            return false;
        }

        var values = new long[4];
        for (var i = 0; i < byteRangeArray.Count; i++)
        {
            if (!TryReadInteger(byteRangeArray[i], out values[i]))
            {
                error = $"entry {i} is not an integer";
                return false;
            }
        }

        byteRange = values;
        return true;
    }

    private static bool TryReadInteger(PdfObject value, out long integer)
    {
        switch (value)
        {
            case PdfInteger pdfInteger:
                integer = pdfInteger.Value;
                return true;
            case PdfReal pdfReal when Math.Abs(pdfReal.Value - Math.Round(pdfReal.Value)) < 0.0000001:
                integer = (long)Math.Round(pdfReal.Value);
                return true;
            default:
                integer = 0;
                return false;
        }
    }

    private static bool TryExtractSignedContent(byte[] fileBytes, long[] byteRange, out byte[] signedContent, out string error)
    {
        signedContent = Array.Empty<byte>();
        error = string.Empty;

        var start1 = byteRange[0];
        var length1 = byteRange[1];
        var start2 = byteRange[2];
        var length2 = byteRange[3];

        if (start1 < 0 || length1 < 0 || start2 < 0 || length2 < 0)
        {
            error = "offsets and lengths must be non-negative";
            return false;
        }

        if (start1 != 0)
        {
            error = "first range must start at byte 0";
            return false;
        }

        if (!RangeWithinFile(start1, length1, fileBytes.Length) ||
            !RangeWithinFile(start2, length2, fileBytes.Length))
        {
            error = "range extends beyond end of file";
            return false;
        }

        if (start1 + length1 > start2)
        {
            error = "ranges overlap";
            return false;
        }

        var totalLength = length1 + length2;
        if (totalLength > int.MaxValue)
        {
            error = "signed byte ranges are too large to verify in memory";
            return false;
        }

        signedContent = new byte[checked((int)totalLength)];
        Buffer.BlockCopy(fileBytes, checked((int)start1), signedContent, 0, checked((int)length1));
        Buffer.BlockCopy(fileBytes, checked((int)start2), signedContent, checked((int)length1), checked((int)length2));
        return true;

        static bool RangeWithinFile(long start, long length, int fileLength) =>
            start <= fileLength &&
            length <= fileLength - start;
    }

    private static bool TryValidateContentsGap(byte[] fileBytes, long[] byteRange, out string error)
    {
        error = string.Empty;

        var gapStart = byteRange[0] + byteRange[1];
        var gapEnd = byteRange[2];
        if (gapStart > gapEnd)
        {
            error = "excluded signature gap is invalid";
            return false;
        }

        var searchStart = 0;
        var parsedContentsToken = false;
        while (TryFindAscii(fileBytes, "/Contents"u8, searchStart, out var contentsNameStart))
        {
            var valueStart = SkipWhiteSpace(fileBytes, contentsNameStart + "/Contents".Length);
            if (valueStart >= fileBytes.Length)
            {
                error = "could not locate /Contents value";
                return false;
            }

            if (TryReadStringTokenEnd(fileBytes, valueStart, out var valueEnd))
            {
                parsedContentsToken = true;
                if (valueStart == gapStart && valueEnd == gapEnd)
                {
                    return true;
                }
            }

            searchStart = contentsNameStart + "/Contents".Length;
        }

        error = parsedContentsToken
            ? "/Contents value does not exactly match the unsigned ByteRange gap"
            : "could not locate /Contents string in file bytes";
        return false;
    }

    private static bool TryFindAscii(byte[] fileBytes, ReadOnlySpan<byte> value, int startIndex, out int index)
    {
        for (var i = Math.Max(0, startIndex); i <= fileBytes.Length - value.Length; i++)
        {
            if (fileBytes.AsSpan(i, value.Length).SequenceEqual(value))
            {
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }

    private static int SkipWhiteSpace(byte[] fileBytes, int startIndex)
    {
        var index = startIndex;
        while (index < fileBytes.Length && IsPdfWhiteSpace(fileBytes[index]))
        {
            index++;
        }

        return index;
    }

    private static bool IsPdfWhiteSpace(byte value) =>
        value is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;

    private static bool TryReadStringTokenEnd(byte[] fileBytes, int valueStart, out long valueEnd)
    {
        valueEnd = 0;
        if (fileBytes[valueStart] == (byte)'<' &&
            valueStart + 1 < fileBytes.Length &&
            fileBytes[valueStart + 1] != (byte)'<')
        {
            for (var i = valueStart + 1; i < fileBytes.Length; i++)
            {
                if (fileBytes[i] == (byte)'>')
                {
                    valueEnd = i + 1L;
                    return true;
                }
            }

            return false;
        }

        if (fileBytes[valueStart] != (byte)'(')
        {
            return false;
        }

        var depth = 1;
        var escaped = false;
        for (var i = valueStart + 1; i < fileBytes.Length; i++)
        {
            var value = fileBytes[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (value == (byte)'\\')
            {
                escaped = true;
                continue;
            }

            if (value == (byte)'(')
            {
                depth++;
            }
            else if (value == (byte)')')
            {
                depth--;
                if (depth == 0)
                {
                    valueEnd = i + 1L;
                    return true;
                }
            }
        }

        return false;
    }
}
