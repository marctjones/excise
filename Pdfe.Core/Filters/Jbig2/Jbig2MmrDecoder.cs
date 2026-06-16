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
        if (output.Length != expectedLength)
            throw new InvalidOperationException(
                $"MMR-encoded JBIG2 bitmap decoded to {output.Length} bytes, expected {expectedLength}.");

        return output;
    }
}
