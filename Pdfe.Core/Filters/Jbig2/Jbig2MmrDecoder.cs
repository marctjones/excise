using System;
using Pdfe.Core.Filters.Ccitt;
using Pdfe.Core.Primitives;

namespace Pdfe.Core.Filters.Jbig2;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class Jbig2MmrDecoder
{
    public static byte[] Decode(byte[] data, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Invalid JBIG2 MMR bitmap dimensions");

        var parms = new PdfDictionary();
        parms.SetInt("K", -1);
        parms.SetInt("Columns", width);
        parms.SetInt("Rows", height);
        parms.SetBool("BlackIs1", true);

        var decoder = new CcittFaxFilterDecoder();
        byte[] output = decoder.Decode(data, new PdfFilterDecodeContext(parms, Stream: null));
        int expectedLength = checked(((width + 7) / 8) * height);
        if (output.Length == expectedLength)
            return output;
        if (output.Length == 0)
            throw new InvalidOperationException("MMR-encoded JBIG2 bitmap decoded to no bytes.");

        // See issue #491. JBIG2 symbol and halftone dictionaries carry their
        // bitmap dimensions out-of-band. Keep a partially decoded MMR bitmap
        // usable instead of causing the whole JBIG2 filter to fall back to raw
        // compressed bytes.
        var normalized = new byte[expectedLength];
        Array.Copy(output, normalized, Math.Min(output.Length, expectedLength));
        return normalized;
    }
}
