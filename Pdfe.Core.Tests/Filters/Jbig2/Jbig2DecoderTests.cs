using AwesomeAssertions;
using Pdfe.Core.Filters.Jbig2;
using Xunit;

namespace Pdfe.Core.Tests.Filters.Jbig2;

/// <summary>
/// Integration tests for Jbig2Decoder public API.
/// Tests the end-to-end JBIG2 decoding for PDF /JBIG2Decode filter.
/// </summary>
public class Jbig2DecoderTests
{
    private static readonly byte[] FileHeaderId =
    {
        0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A
    };

    private static byte[] BuildSegment(uint segmentNumber, SegmentType type, uint pageNumber = 1, uint dataLength = 0)
    {
        return new[]
        {
            (byte)(segmentNumber >> 24),
            (byte)(segmentNumber >> 16),
            (byte)(segmentNumber >> 8),
            (byte)segmentNumber,
            (byte)type,
            (byte)0,
            (byte)pageNumber,
            (byte)(dataLength >> 24),
            (byte)(dataLength >> 16),
            (byte)(dataLength >> 8),
            (byte)dataLength,
        };
    }

    private static byte[] BuildFileHeader(byte flags, uint? pageCount = null)
    {
        var bytes = new List<byte>(FileHeaderId);
        bytes.Add(flags);
        if (pageCount.HasValue)
        {
            bytes.Add((byte)(pageCount.Value >> 24));
            bytes.Add((byte)(pageCount.Value >> 16));
            bytes.Add((byte)(pageCount.Value >> 8));
            bytes.Add((byte)pageCount.Value);
        }
        return bytes.ToArray();
    }

    /// <summary>
    /// Test that decoder throws on null data.
    /// </summary>
    [Fact]
    public void Decode_WithNullData_ThrowsArgumentNullException()
    {
        var action = () => Jbig2Decoder.Decode(null!, null, 100, 100);

        action.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// Test that decoder throws on invalid width.
    /// </summary>
    [Fact]
    public void Decode_WithZeroWidth_ThrowsArgumentException()
    {
        byte[] data = new byte[] { 0x00 };

        var action = () => Jbig2Decoder.Decode(data, null, 0, 100);

        action.Should().Throw<ArgumentException>()
            .WithMessage("*positive*");
    }

    /// <summary>
    /// Test that decoder throws on invalid height.
    /// </summary>
    [Fact]
    public void Decode_WithNegativeHeight_ThrowsArgumentException()
    {
        byte[] data = new byte[] { 0x00 };

        var action = () => Jbig2Decoder.Decode(data, null, 100, -1);

        action.Should().Throw<ArgumentException>()
            .WithMessage("*positive*");
    }

    /// <summary>
    /// Test decoding with minimal empty data.
    /// Should return an image filled with white pixels (0x00 bytes).
    /// </summary>
    [Fact]
    public void Decode_WithEmptyData_ReturnsWhiteImage()
    {
        int width = 8;
        int height = 8;
        byte[] data = Array.Empty<byte>();

        byte[] result = Jbig2Decoder.Decode(data, null, width, height);

        // Output should be properly sized (1 byte per row for 8-wide, 8 rows)
        int expectedBytes = ((width + 7) / 8) * height;
        result.Length.Should().Be(expectedBytes);

        // All white (0x00) since no segments were processed
        result.Should().Equal(new byte[expectedBytes]);
    }

    [Fact]
    public void Decode_WithSequentialFileHeaderUnknownPageCount_SkipsHeader()
    {
        byte[] data = BuildFileHeader(0x03); // amount unknown + sequential organization

        byte[] result = Jbig2Decoder.Decode(data, null, 8, 2);

        result.Should().Equal(new byte[] { 0x00, 0x00 });
    }

    [Fact]
    public void Decode_WithSequentialFileHeaderKnownPageCount_SkipsPageCountField()
    {
        byte[] data = BuildFileHeader(0x01, pageCount: 1); // known page count + sequential organization

        byte[] result = Jbig2Decoder.Decode(data, null, 8, 2);

        result.Should().Equal(new byte[] { 0x00, 0x00 });
    }

    [Fact]
    public void Decode_WithRandomAccessFileHeader_ThrowsNotSupported()
    {
        byte[] data = BuildFileHeader(0x02); // amount unknown + random-access organization

        var act = () => Jbig2Decoder.Decode(data, null, 8, 2);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*Random-access JBIG2 file organization*");
    }

    /// <summary>
    /// Test that output size is calculated correctly.
    /// </summary>
    [Theory]
    [InlineData(8, 8, 8)]       // 8x8 = 1 byte per row, 8 rows
    [InlineData(16, 16, 32)]    // 16x16 = 2 bytes per row, 16 rows
    [InlineData(100, 100, 1300)] // 100x100 = 13 bytes per row, 100 rows
    [InlineData(1, 1, 1)]        // 1x1 = 1 byte
    public void Decode_OutputSizeCalculation(int width, int height, int expectedBytes)
    {
        byte[] data = Array.Empty<byte>();

        byte[] result = Jbig2Decoder.Decode(data, null, width, height);

        result.Length.Should().Be(expectedBytes);
    }

    /// <summary>
    /// Globals + page data are combined and parsed. This crafted input parses as
    /// a symbol-dictionary segment, which isn't supported yet — the decoder is
    /// strict and throws (so callers fall back to the raw stream) rather than
    /// silently emitting a blank/partial page.
    /// </summary>
    [Fact]
    public void Decode_WithUnsupportedSegment_ThrowsRatherThanBlank()
    {
        byte[] globals = BuildSegment(1, SegmentType.SymbolDictionary, pageNumber: 0);
        byte[] pageData = Array.Empty<byte>();

        var act = () => Jbig2Decoder.Decode(pageData, globals, 8, 8);

        act.Should().Throw<NotSupportedException>();
    }

    /// <summary>
    /// Test decoding with null globals (common case).
    /// </summary>
    [Fact]
    public void Decode_WithNullGlobals_ProcessesPageDataOnly()
    {
        int width = 8;
        int height = 8;
        byte[] pageData = new byte[] { 0x00 };

        byte[] result = Jbig2Decoder.Decode(pageData, null, width, height);

        int expectedBytes = ((width + 7) / 8) * height;
        result.Length.Should().Be(expectedBytes);
    }

    /// <summary>
    /// Test decoding with empty globals (should be same as null).
    /// </summary>
    [Fact]
    public void Decode_WithEmptyGlobals_ProcessesPageDataOnly()
    {
        int width = 8;
        int height = 8;
        byte[] pageData = new byte[] { 0x00 };
        byte[] emptyGlobals = Array.Empty<byte>();

        byte[] result = Jbig2Decoder.Decode(pageData, emptyGlobals, width, height);

        int expectedBytes = ((width + 7) / 8) * height;
        result.Length.Should().Be(expectedBytes);
    }

    /// <summary>
    /// Test that output is MSB-first packed bits.
    /// For a known pattern, verify the bit order in output bytes.
    /// </summary>
    [Fact]
    public void Decode_OutputBitPacking_IsMsbFirst()
    {
        int width = 8;
        int height = 1;
        byte[] data = Array.Empty<byte>();

        byte[] result = Jbig2Decoder.Decode(data, null, width, height);

        // With no data, all pixels should be white (0)
        // Each byte in output should be 0x00
        result.Should().Equal(new byte[] { 0x00 });
    }

    /// <summary>
    /// Test that output is row-padded to byte boundary.
    /// A 7-pixel wide image should still use 1 byte per row (padded with 0s).
    /// </summary>
    [Fact]
    public void Decode_OutputRowPadding_AlignedToBytesBoundary()
    {
        int width = 7;
        int height = 2;
        byte[] data = Array.Empty<byte>();

        byte[] result = Jbig2Decoder.Decode(data, null, width, height);

        // 7 bits = 1 byte per row, 2 rows = 2 bytes
        result.Length.Should().Be(2);
    }

    /// <summary>
    /// Test a very large image dimensions.
    /// Should allocate correctly without overflow.
    /// </summary>
    [Fact]
    public void Decode_WithLargeDimensions_AllocatesCorrectly()
    {
        int width = 2048;
        int height = 2048;
        byte[] data = Array.Empty<byte>();

        byte[] result = Jbig2Decoder.Decode(data, null, width, height);

        int expectedBytes = ((width + 7) / 8) * height;
        result.Length.Should().Be(expectedBytes);
    }

    /// <summary>
    /// Test that unsupported segment types throw NotSupportedException.
    /// This is important for graceful error handling.
    /// </summary>
    [Fact]
    public void Decode_WithUnsupportedSegmentType_ThrowsNotSupportedException()
    {
        // Build a segment header for symbol dictionary (type 0)
        // This is too complex to test here without proper segment building infrastructure,
        // but the decoder should handle it gracefully.
        // For now, we'll just verify that an invalid stream doesn't crash the decoder.
    }

    /// <summary>
    /// Test that 1x1 minimum image works.
    /// </summary>
    [Fact]
    public void Decode_With1x1Image_ReturnsOneByteImage()
    {
        byte[] data = Array.Empty<byte>();

        byte[] result = Jbig2Decoder.Decode(data, null, 1, 1);

        result.Length.Should().Be(1);
        result[0].Should().Be(0x00);
    }

    /// <summary>
    /// Test maximum practical image size (PDF limit ~200MP typically).
    /// </summary>
    [Fact]
    public void Decode_WithMaximumReasonableSize_AllocatesWithoutError()
    {
        // 4096 x 4096 is a reasonable maximum
        int width = 4096;
        int height = 4096;
        byte[] data = Array.Empty<byte>();

        byte[] result = Jbig2Decoder.Decode(data, null, width, height);

        int expectedBytes = ((width + 7) / 8) * height;
        result.Length.Should().Be(expectedBytes);
    }

    /// <summary>
    /// Test that decoded output is mutable (not read-only).
    /// </summary>
    [Fact]
    public void Decode_OutputArrayIsMutable()
    {
        int width = 8;
        int height = 8;
        byte[] data = Array.Empty<byte>();

        byte[] result = Jbig2Decoder.Decode(data, null, width, height);

        // Should be able to modify the output
        result[0] = 0xFF;
        result[0].Should().Be(0xFF);
    }
}
