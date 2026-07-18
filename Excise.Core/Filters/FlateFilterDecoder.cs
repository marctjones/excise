using System.IO.Compression;
using Excise.Core.Parsing;

namespace Excise.Core.Filters;

internal sealed class FlateFilterDecoder : AliasedFilterDecoder
{
    public FlateFilterDecoder()
        : base("FlateDecode", "Fl")
    {
    }

    public override byte[] Decode(byte[] data, PdfFilterDecodeContext context)
    {
        var decoded = DecodeFlateData(data);
        return PdfPredictor.ApplyIfNeeded(decoded, context.DecodeParms);
    }

    private static byte[] DecodeFlateData(byte[] data)
    {
        var attempts = GetAttemptOrder(data);
        Exception? firstError = null;

        foreach (var attempt in attempts)
        {
            try
            {
                return attempt(data);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                firstError ??= ex;
            }
        }

        throw new PdfParseException("Could not decode Flate stream", firstError!);
    }

    private static IReadOnlyList<Func<byte[], byte[]>> GetAttemptOrder(byte[] data)
    {
        var looksLikeGzip = data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B;
        var looksLikeZlib = LooksLikeZlibHeader(data);

        if (looksLikeGzip)
            return new Func<byte[], byte[]>[] { DecodeGzip, DecodeRawDeflate, DecodeZlib };

        if (looksLikeZlib)
            return new Func<byte[], byte[]>[] { DecodeZlib, DecodeRawDeflate, DecodeGzip };

        return new Func<byte[], byte[]>[] { DecodeRawDeflate, DecodeZlib, DecodeGzip };
    }

    private static bool LooksLikeZlibHeader(byte[] data)
    {
        if (data.Length < 2)
            return false;

        int cmf = data[0];
        int flg = data[1];
        return (cmf & 0x0F) == 8 && ((cmf << 8) + flg) % 31 == 0;
    }

    private static byte[] DecodeZlib(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        return CopyToArray(zlib);
    }

    private static byte[] DecodeRawDeflate(byte[] data)
    {
        int offset = 0;
        if (LooksLikeZlibHeader(data))
        {
            offset = 2;
            if ((data[1] & 0x20) != 0)
                offset += 4;
        }

        using var input = new MemoryStream(data, offset, data.Length - offset);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        return CopyToArray(deflate);
    }

    private static byte[] DecodeGzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return CopyToArray(gzip);
    }

    private static byte[] CopyToArray(Stream stream)
    {
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
