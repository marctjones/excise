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
        var decoded = DecodeFlateData(data, StreamDecodeLimits.MaxDecompressedBytes);
        return PdfPredictor.ApplyIfNeeded(decoded, context.DecodeParms);
    }

    /// <summary>
    /// Exposed with an explicit cap for <see cref="StreamDecodeLimits"/>'s own
    /// tests: a production-sized (512 MiB) bomb is real memory and CPU work to
    /// generate and decode, so the resource-guard test passes a small cap here
    /// instead of waiting for the production ceiling.
    /// </summary>
    internal static byte[] DecodeFlateData(byte[] data, long maxDecodedBytes)
    {
        var attempts = GetAttemptOrder(data, maxDecodedBytes);
        Exception? firstError = null;

        foreach (var attempt in attempts)
        {
            try
            {
                return attempt(data);
            }
            // A resource-guard trip (StreamDecodeLimits) is deliberately NOT
            // caught here: it must propagate, not be treated as "try the next
            // decode strategy" and retried against an equally hostile input.
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                firstError ??= ex;
            }
        }

        throw new PdfParseException("Could not decode Flate stream", firstError!);
    }

    private static IReadOnlyList<Func<byte[], byte[]>> GetAttemptOrder(byte[] data, long maxDecodedBytes)
    {
        byte[] Zlib(byte[] d) => DecodeZlib(d, maxDecodedBytes);
        byte[] RawDeflate(byte[] d) => DecodeRawDeflate(d, maxDecodedBytes);
        byte[] Gzip(byte[] d) => DecodeGzip(d, maxDecodedBytes);

        var looksLikeGzip = data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B;
        var looksLikeZlib = LooksLikeZlibHeader(data);

        if (looksLikeGzip)
            return new Func<byte[], byte[]>[] { Gzip, RawDeflate, Zlib };

        if (looksLikeZlib)
            return new Func<byte[], byte[]>[] { Zlib, RawDeflate, Gzip };

        return new Func<byte[], byte[]>[] { RawDeflate, Zlib, Gzip };
    }

    private static bool LooksLikeZlibHeader(byte[] data)
    {
        if (data.Length < 2)
            return false;

        int cmf = data[0];
        int flg = data[1];
        return (cmf & 0x0F) == 8 && ((cmf << 8) + flg) % 31 == 0;
    }

    private static byte[] DecodeZlib(byte[] data, long maxDecodedBytes)
    {
        using var input = new MemoryStream(data);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        return CopyToArray(zlib, maxDecodedBytes);
    }

    private static byte[] DecodeRawDeflate(byte[] data, long maxDecodedBytes)
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
        return CopyToArray(deflate, maxDecodedBytes);
    }

    private static byte[] DecodeGzip(byte[] data, long maxDecodedBytes)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return CopyToArray(gzip, maxDecodedBytes);
    }

    /// <summary>
    /// Reads in bounded chunks rather than <c>stream.CopyTo</c>, checking the
    /// running total against <paramref name="maxDecodedBytes"/> on every
    /// chunk (#1408) -- a tiny compressed stream can expand to gigabytes via
    /// Flate, and a single unbounded <c>CopyTo</c> would allocate every one
    /// of them before this method ever got a chance to say no.
    /// </summary>
    private static byte[] CopyToArray(Stream stream, long maxDecodedBytes)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            StreamDecodeLimits.ThrowIfExceeded(total, maxDecodedBytes);
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
