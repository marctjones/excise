using System.IO.Compression;
using AwesomeAssertions;
using Excise.Core.Filters;
using Excise.Core.Parsing;
using Xunit;

namespace Excise.Core.Tests.Parsing;

/// <summary>
/// #1626: an unhinted Flate decode rents its first chunk from the shared pool
/// instead of allocating a fresh 80 KB array per stream. The bytes must be
/// identical at every size around the chunk boundaries, the guard must still
/// fire, and a small stream must cost about its own size.
/// </summary>
public class FlateScratchChunkTests
{
    private const long Max = StreamDecodeLimits.MaxDecompressedBytes;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2_000)]
    [InlineData(FlateFilterDecoder.InitialChunkBytes - 1)]
    [InlineData(FlateFilterDecoder.InitialChunkBytes)]
    [InlineData(FlateFilterDecoder.InitialChunkBytes + 1)]
    [InlineData(131_072)]
    [InlineData(131_073)]
    [InlineData(3 * FlateFilterDecoder.InitialChunkBytes + 17)]
    [InlineData(1 << 20)]
    public void Unhinted_IsByteIdentical_AtEverySizeAroundTheChunkBoundaries(int size)
    {
        var random = new Random(size);
        var decoded = new byte[size];
        random.NextBytes(decoded);
        // Half compressible, half not, so both inflater paths are exercised.
        for (var i = 0; i < size / 2; i++) decoded[i] = (byte)(i % 7);

        FlateFilterDecoder.DecodeFlateData(Flate(decoded), Max).Should().Equal(decoded);
        // Twice: the second decode reuses a pooled array the first one dirtied.
        FlateFilterDecoder.DecodeFlateData(Flate(decoded), Max).Should().Equal(decoded);
    }

    [Fact]
    public void ASmallStream_AllocatesAboutItsOwnSize_NotAFreshScratchChunk()
    {
        var decoded = new byte[2_000];
        new Random(7).NextBytes(decoded);
        var encoded = Flate(decoded);
        FlateFilterDecoder.DecodeFlateData(encoded, Max); // warm the pool and the JIT

        var before = GC.GetAllocatedBytesForCurrentThread();
        FlateFilterDecoder.DecodeFlateData(encoded, Max);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().BeLessThan(FlateFilterDecoder.InitialChunkBytes / 2,
            "a 2 KB stream allocated an 80 KB scratch chunk per decode before #1626");
    }

    [Fact]
    public void TheGuard_StillFires_InsideTheRentedChunk()
    {
        var decoded = new byte[50_000];
        var act = () => FlateFilterDecoder.DecodeFlateData(Flate(decoded), maxDecodedBytes: 10_000);
        act.Should().Throw<Excise.Core.Parsing.PdfParseException>();
    }

    private static byte[] Flate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return output.ToArray();
    }
}
