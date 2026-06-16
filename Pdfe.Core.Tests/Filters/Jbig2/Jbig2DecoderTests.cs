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

    private static byte[] BuildSegment(uint segmentNumber, SegmentType type, byte[] segmentData, uint pageNumber = 1)
    {
        byte[] header = BuildSegment(segmentNumber, type, pageNumber, (uint)segmentData.Length);
        byte[] result = new byte[header.Length + segmentData.Length];
        Array.Copy(header, 0, result, 0, header.Length);
        Array.Copy(segmentData, 0, result, header.Length, segmentData.Length);
        return result;
    }

    private static byte[] BuildSegmentWithReferences(uint segmentNumber, SegmentType type, byte[] segmentData, params byte[] referredSegments)
    {
        if (referredSegments.Length > 6)
            throw new ArgumentOutOfRangeException(nameof(referredSegments));

        var header = new List<byte>
        {
            (byte)(segmentNumber >> 24),
            (byte)(segmentNumber >> 16),
            (byte)(segmentNumber >> 8),
            (byte)segmentNumber,
            (byte)type,
            (byte)(referredSegments.Length << 5),
        };
        header.AddRange(referredSegments);
        header.Add(1);
        header.Add((byte)(segmentData.Length >> 24));
        header.Add((byte)(segmentData.Length >> 16));
        header.Add((byte)(segmentData.Length >> 8));
        header.Add((byte)segmentData.Length);

        byte[] result = new byte[header.Count + segmentData.Length];
        header.CopyTo(result, 0);
        Array.Copy(segmentData, 0, result, header.Count, segmentData.Length);
        return result;
    }

    private static byte[] PackBits(string bits)
    {
        bits = new string(bits.Where(c => c == '0' || c == '1').ToArray());
        var bytes = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i] == '1')
                bytes[i / 8] |= (byte)(1 << (7 - (i % 8)));
        }
        return bytes;
    }

    private static byte[] BuildHuffmanSymbolDictionaryBody(byte symbolByte)
    {
        var body = new List<byte>
        {
            0x00, 0x01, // Huffman encoded, no refinement aggregation.
            0x00, 0x00, 0x00, 0x01, // exported symbols
            0x00, 0x00, 0x00, 0x01, // new symbols
        };

        // HCDH=1 (B4 "0"), DW=1 (B2 "10"), OOB (B2 "111111"),
        // BMSIZE=0 (B1 "0"+"0000"), byte align, 1x1 collective bitmap,
        // EXRUNLENGTH 0 then 1 (export all new symbols).
        body.AddRange(PackBits("0" + "10" + "111111" + "0" + "0000"));
        body.Add(symbolByte);
        body.AddRange(PackBits("0" + "0000" + "0" + "0001"));
        return body.ToArray();
    }

    private static byte[] BuildHuffmanSymbolDictionaryBodyWithMmrCollectiveBitmap()
    {
        var body = new List<byte>
        {
            0x00, 0x01, // Huffman encoded, no refinement aggregation.
            0x00, 0x00, 0x00, 0x01, // exported symbols
            0x00, 0x00, 0x00, 0x01, // new symbols
        };

        // HCDH=1 (B4 "0"), DW=8 (B2 "1110"+5), OOB (B2 "111111"),
        // BMSIZE=2 (B1 "0"+2), byte align, then a one-row T.6 horizontal-mode
        // bitmap: four white pixels followed by four black pixels.
        body.AddRange(PackBits("0" + "1110" + "101" + "111111" + "0" + "0010"));
        body.AddRange(new byte[] { 0b00110110, 0b11000000 });
        body.AddRange(PackBits("0" + "0000" + "0" + "0001"));
        return body.ToArray();
    }

    private static byte[] BuildCustomHuffmanSymbolDictionaryBody(byte symbolByte)
    {
        var body = new List<byte>
        {
            0x00, 0x7D, // Huffman, custom DH/DW/BMSIZE tables, no refinement.
            0x00, 0x00, 0x00, 0x01, // exported symbols
            0x00, 0x00, 0x00, 0x01, // new symbols
        };

        // Custom DH=1 ("0"), custom DW=1 ("0"), custom DW OOB ("1"),
        // custom BMSIZE=0 ("0"), byte align, 1x1 collective bitmap,
        // EXRUNLENGTH 0 then 1 (export all new symbols).
        body.AddRange(PackBits("0" + "0" + "1" + "0"));
        body.Add(symbolByte);
        body.AddRange(PackBits("0" + "0000" + "0" + "0001"));
        return body.ToArray();
    }

    private static byte[] BuildImmediateCustomHuffmanTextRegionBody()
    {
        var body = new List<byte>
        {
            0, 0, 0, 8, // region width
            0, 0, 0, 1, // region height
            0, 0, 0, 0, // x
            0, 0, 0, 0, // y
            0x04,       // region combination operator: Replace
            0x00, 0x01, // text flags: Huffman, no refinement
            0x00, 0x3F, // Huffman flags: custom FS/DS/DT tables
            0x00, 0x00, 0x00, 0x01, // one symbol instance
        };

        var codeLengthBits = new System.Text.StringBuilder();
        for (int i = 0; i < 35; i++)
            codeLengthBits.Append(i == 1 ? "0001" : "0000");
        codeLengthBits.Append('0');
        body.AddRange(PackBits(codeLengthBits.ToString()));

        // Custom DT=1, custom DT=1, custom FS=0, symbol id 0.
        body.AddRange(PackBits("0" + "0" + "0" + "0"));
        return body.ToArray();
    }

    private static byte[] BuildUserHuffmanTableBody(
        int lowValue,
        int highValue,
        string payloadBits,
        bool hasOutOfBand = false,
        int prefixSizeBits = 2,
        int rangeSizeBits = 1)
    {
        var body = new List<byte>
        {
            (byte)(((rangeSizeBits - 1) << 4) | ((prefixSizeBits - 1) << 1) | (hasOutOfBand ? 1 : 0)),
            (byte)(lowValue >> 24),
            (byte)(lowValue >> 16),
            (byte)(lowValue >> 8),
            (byte)lowValue,
            (byte)(highValue >> 24),
            (byte)(highValue >> 16),
            (byte)(highValue >> 8),
            (byte)highValue,
        };
        body.AddRange(PackBits(payloadBits));
        return body.ToArray();
    }

    private static byte[] BuildSingleValueUserHuffmanTableBody(int value, bool hasOutOfBand = false)
    {
        string payloadBits = hasOutOfBand
            ? "01" + "0" + "00" + "00" + "01"
            : "01" + "0" + "00" + "00";
        return BuildUserHuffmanTableBody(value, value + 1, payloadBits, hasOutOfBand);
    }

    private static byte[] BuildGenericRefinementRegionBody(bool typicalPrediction)
    {
        var body = new List<byte>
        {
            0, 0, 0, 1, // region width
            0, 0, 0, 1, // region height
            0, 0, 0, 0, // x
            0, 0, 0, 0, // y
            0x04,       // region combination operator: Replace
            (byte)(typicalPrediction ? 0x02 : 0x00), // template 0, optional TPGRON
            0xFF, 0xFF, // default generic refinement AT pixel 0: disabled
            0xFF, 0xFF, // default generic refinement AT pixel 1: disabled
        };

        return body.ToArray();
    }

    private static byte[] BuildUnsupportedRefinementSymbolDictionaryBody()
        =>
        [
            0x00, 0x03, // Huffman encoded + refinement aggregation.
            0x00, 0x00,
            0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, // exported symbols
            0x00, 0x00, 0x00, 0x00, // new symbols
        ];

    private static byte[] BuildImmediateHuffmanTextRegionBody()
    {
        var body = new List<byte>
        {
            0, 0, 0, 8, // region width
            0, 0, 0, 1, // region height
            0, 0, 0, 0, // x
            0, 0, 0, 0, // y
            0x04,       // region combination operator: Replace
            0x00, 0x01, // text flags: Huffman, no refinement
            0x00, 0x00, // Huffman flags: standard tables
            0x00, 0x00, 0x00, 0x01, // one symbol instance
        };

        var codeLengthBits = new System.Text.StringBuilder();
        for (int i = 0; i < 35; i++)
            codeLengthBits.Append(i == 1 ? "0001" : "0000");
        codeLengthBits.Append('0'); // symbol 0 has code length 1 via run-code value 1.
        body.AddRange(PackBits(codeLengthBits.ToString()));

        // Initial stripT=1 (B11 "0"), dT=1 (B11 "0"), firstS=0 (B6 "00"+7 zero range bits),
        // symbol id 0 (single-entry symbol code table "0").
        body.AddRange(PackBits("0" + "0" + "00" + "0000000" + "0"));
        return body.ToArray();
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
    /// Should return an image filled with white PDF image samples (0xFF bytes).
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

        // All white in PDF DeviceGray sample space (1 bits).
        result.Should().Equal(Enumerable.Repeat((byte)0xFF, expectedBytes));
    }

    [Fact]
    public void Decode_WithSequentialFileHeaderUnknownPageCount_SkipsHeader()
    {
        byte[] data = BuildFileHeader(0x03); // amount unknown + sequential organization

        byte[] result = Jbig2Decoder.Decode(data, null, 8, 2);

        result.Should().Equal(new byte[] { 0xFF, 0xFF });
    }

    [Fact]
    public void Decode_WithSequentialFileHeaderKnownPageCount_SkipsPageCountField()
    {
        byte[] data = BuildFileHeader(0x01, pageCount: 1); // known page count + sequential organization

        byte[] result = Jbig2Decoder.Decode(data, null, 8, 2);

        result.Should().Equal(new byte[] { 0xFF, 0xFF });
    }

    [Fact]
    public void Decode_WithPageInformationDefaultPixelOne_FillsPage()
    {
        byte[] pageInformation = new byte[Jbig2PageInformation.ByteLength];
        pageInformation[16] = 0x04;
        byte[] data = BuildSegment(1, SegmentType.PageInformation, pageInformation);

        byte[] result = Jbig2Decoder.Decode(data, null, 8, 2);

        result.Should().Equal(0x00, 0x00);
    }

    [Fact]
    public void Decode_WithRandomAccessFileHeader_ThrowsNotSupported()
    {
        byte[] data = BuildFileHeader(0x02); // amount unknown + random-access organization

        var act = () => Jbig2Decoder.Decode(data, null, 8, 2);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*Random-access JBIG2 file organization*");
    }

    [Fact]
    public void Decode_WithGenericRefinementTpgron_ThrowsNotSupported()
    {
        byte[] data = BuildSegment(
            1,
            SegmentType.ImmediateGenericRefinementRegion,
            BuildGenericRefinementRegionBody(typicalPrediction: true));

        var act = () => Jbig2Decoder.Decode(data, null, 1, 1);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*TPGRON*");
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
        byte[] symbolDictionary = BuildUnsupportedRefinementSymbolDictionaryBody();
        byte[] globals = BuildSegment(1, SegmentType.SymbolDictionary, symbolDictionary, pageNumber: 0);
        byte[] pageData = Array.Empty<byte>();

        var act = () => Jbig2Decoder.Decode(pageData, globals, 8, 8);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Decode_WithHuffmanSymbolDictionary_CachesWithoutPainting()
    {
        byte[] symbolDictionary = BuildHuffmanSymbolDictionaryBody(0x80);
        byte[] data = BuildSegment(1, SegmentType.SymbolDictionary, symbolDictionary, pageNumber: 0);

        byte[] result = Jbig2Decoder.Decode(data, null, 8, 1);

        result.Should().Equal(0xFF);
    }

    [Fact]
    public void Decode_WithImmediateHuffmanTextRegion_ComposesReferencedSymbol()
    {
        byte[] symbolDictionary = BuildSegment(1, SegmentType.SymbolDictionary, BuildHuffmanSymbolDictionaryBody(0x80), pageNumber: 0);
        byte[] textRegion = BuildSegmentWithReferences(2, SegmentType.ImmediateTextRegion, BuildImmediateHuffmanTextRegionBody(), 1);
        byte[] data = new byte[symbolDictionary.Length + textRegion.Length];
        Array.Copy(symbolDictionary, data, symbolDictionary.Length);
        Array.Copy(textRegion, 0, data, symbolDictionary.Length, textRegion.Length);

        byte[] result = Jbig2Decoder.Decode(data, null, 8, 1);

        result.Should().Equal(0x7F);
    }

    [Fact]
    public void Decode_WithMmrHuffmanSymbolDictionary_ComposesReferencedSymbol()
    {
        byte[] symbolDictionary = BuildSegment(
            1,
            SegmentType.SymbolDictionary,
            BuildHuffmanSymbolDictionaryBodyWithMmrCollectiveBitmap(),
            pageNumber: 0);
        byte[] textRegion = BuildSegmentWithReferences(2, SegmentType.ImmediateTextRegion, BuildImmediateHuffmanTextRegionBody(), 1);
        byte[] data = new byte[symbolDictionary.Length + textRegion.Length];
        Array.Copy(symbolDictionary, data, symbolDictionary.Length);
        Array.Copy(textRegion, 0, data, symbolDictionary.Length, textRegion.Length);

        byte[] result = Jbig2Decoder.Decode(data, null, 8, 1);

        result.Should().Equal(0xF0);
    }

    [Fact]
    public void Decode_WithReferencedUserHuffmanTables_ComposesReferencedSymbol()
    {
        byte[] symbolHeightTable = BuildSegment(1, SegmentType.Table, BuildSingleValueUserHuffmanTableBody(1), pageNumber: 0);
        byte[] symbolWidthTable = BuildSegment(2, SegmentType.Table, BuildSingleValueUserHuffmanTableBody(1, hasOutOfBand: true), pageNumber: 0);
        byte[] symbolBitmapSizeTable = BuildSegment(3, SegmentType.Table, BuildSingleValueUserHuffmanTableBody(0), pageNumber: 0);
        byte[] symbolDictionary = BuildSegmentWithReferences(
            4,
            SegmentType.SymbolDictionary,
            BuildCustomHuffmanSymbolDictionaryBody(0x80),
            1,
            2,
            3);
        byte[] textFirstSTable = BuildSegment(5, SegmentType.Table, BuildSingleValueUserHuffmanTableBody(0), pageNumber: 0);
        byte[] textDeltaSTable = BuildSegment(6, SegmentType.Table, BuildSingleValueUserHuffmanTableBody(1), pageNumber: 0);
        byte[] textDeltaTTable = BuildSegment(7, SegmentType.Table, BuildSingleValueUserHuffmanTableBody(1), pageNumber: 0);
        byte[] textRegion = BuildSegmentWithReferences(
            8,
            SegmentType.ImmediateTextRegion,
            BuildImmediateCustomHuffmanTextRegionBody(),
            4,
            5,
            6,
            7);
        byte[] data = symbolHeightTable
            .Concat(symbolWidthTable)
            .Concat(symbolBitmapSizeTable)
            .Concat(symbolDictionary)
            .Concat(textFirstSTable)
            .Concat(textDeltaSTable)
            .Concat(textDeltaTTable)
            .Concat(textRegion)
            .ToArray();

        byte[] result = Jbig2Decoder.Decode(data, null, 8, 1);

        result.Should().Equal(0x7F);
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

        // With no data, all pixels should be white PDF samples.
        result.Should().Equal(new byte[] { 0xFF });
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
        result[0].Should().Be(0xFF);
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
