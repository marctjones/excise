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

    /// <summary>First chunk size: below the 85 KB large-object-heap threshold,
    /// so a small content stream never touches the LOH until its final array.</summary>
    internal const int InitialChunkBytes = 81920;

    /// <summary>Chunks double up to this size, then stay at it.</summary>
    internal const int MaxChunkBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Reads in bounded chunks rather than <c>stream.CopyTo</c>, checking the
    /// running total against <paramref name="maxDecodedBytes"/> on every
    /// chunk (#1408) -- a tiny compressed stream can expand to gigabytes via
    /// Flate, and a single unbounded <c>CopyTo</c> would allocate every one
    /// of them before this method ever got a chance to say no.
    /// </summary>
    /// <remarks>
    /// #1207/#1482: the decompressor fills a list of geometrically growing
    /// chunks directly, and the result is copied ONCE into an exact-size
    /// array. This replaced a <see cref="MemoryStream"/> whose doubling
    /// capacity plus the final <c>ToArray</c> copy allocated ~4x the decoded
    /// size and held up to 3x live at the end: on the Altona technical2 x4
    /// page that was 2.2 GiB of large-object-heap churn for ~526 MiB of
    /// images. Now the total allocation is ~2x the decoded size and at most
    /// 2x is live. The output bytes are the same bytes in the same order.
    ///
    /// Each new chunk is clamped to the remaining ceiling plus one byte, so a
    /// pre-allocated chunk can never outrun the #1408 guard: the read that
    /// crosses the ceiling is the next one, not an allocation past it.
    /// </remarks>
    private static byte[] CopyToArray(Stream stream, long maxDecodedBytes)
    {
        List<byte[]>? full = null;
        long total = 0;
        var current = new byte[NextChunkSize(InitialChunkBytes, total, maxDecodedBytes)];
        var used = 0;

        while (true)
        {
            if (used == current.Length)
            {
                (full ??= new List<byte[]>()).Add(current);
                current = new byte[NextChunkSize(Math.Min((long)current.Length * 2, MaxChunkBytes), total, maxDecodedBytes)];
                used = 0;
            }

            var read = stream.Read(current, used, current.Length - used);
            if (read <= 0)
                break;

            total += read;
            StreamDecodeLimits.ThrowIfExceeded(total, maxDecodedBytes);
            used += read;
        }

        var result = GC.AllocateUninitializedArray<byte>(checked((int)total));
        var offset = 0;
        if (full is not null)
        {
            foreach (var chunk in full)
            {
                Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
                offset += chunk.Length;
            }
        }

        Buffer.BlockCopy(current, 0, result, offset, used);
        return result;
    }

    private static int NextChunkSize(long preferred, long decodedSoFar, long maxDecodedBytes)
    {
        var remainingPlusOne = maxDecodedBytes - decodedSoFar + 1;
        return (int)Math.Max(1, Math.Min(preferred, remainingPlusOne));
    }
}
