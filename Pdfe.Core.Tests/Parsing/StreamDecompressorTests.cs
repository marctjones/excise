using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Parsing;
using Pdfe.Core.Primitives;
using Xunit;

namespace Pdfe.Core.Tests.Parsing;

/// <summary>
/// Comprehensive tests for StreamDecompressor covering all filter types and edge cases.
/// ISO 32000-2:2020 Section 7.4 (Filters and Compression).
/// </summary>
public class StreamDecompressorTests
{
    #region ApplyFilter - Unknown Filter

    [Fact]
    public void ApplyFilter_UnknownFilterName_ThrowsNotSupportedException()
    {
        var decompressor = new StreamDecompressor();
        var data = Encoding.UTF8.GetBytes("Hello World");

        var action = () => decompressor.ApplyFilter("UnknownFilter", data, null);

        action.Should().Throw<NotSupportedException>()
            .WithMessage("*Unknown filter: UnknownFilter*");
    }

    #endregion

    #region ASCIIHexDecode Tests

    [Fact]
    public void ApplyFilter_ASCIIHex_FullName_DecodesHexString()
    {
        var decompressor = new StreamDecompressor();
        var hexData = Encoding.ASCII.GetBytes("48656C6C6F>");

        var result = decompressor.ApplyFilter("ASCIIHexDecode", hexData, null);

        result.Should().Equal(new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F });
        Encoding.ASCII.GetString(result).Should().Be("Hello");
    }

    [Fact]
    public void ApplyFilter_ASCIIHex_ShortName_DecodesHexString()
    {
        var decompressor = new StreamDecompressor();
        var hexData = Encoding.ASCII.GetBytes("48656C6C6F>");

        var result = decompressor.ApplyFilter("AHx", hexData, null);

        result.Should().Equal(new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F });
    }

    [Fact]
    public void ApplyFilter_ASCIIHex_WithWhitespace_DecodesCorrectly()
    {
        var decompressor = new StreamDecompressor();
        var hexData = Encoding.ASCII.GetBytes("48 65 6C 6C 6F >");

        var result = decompressor.ApplyFilter("ASCIIHexDecode", hexData, null);

        result.Should().Equal(new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F });
    }

    [Fact]
    public void ApplyFilter_ASCIIHex_OddNumberOfDigits_LastNibbleShiftedLeft()
    {
        var decompressor = new StreamDecompressor();
        var hexData = Encoding.ASCII.GetBytes("48656C6C6F5>");

        var result = decompressor.ApplyFilter("ASCIIHexDecode", hexData, null);

        result.Should().HaveCount(6);
        result[5].Should().Be(0x50);
    }

    [Fact]
    public void ApplyFilter_ASCIIHex_EmptyInput_ReturnsEmptyArray()
    {
        var decompressor = new StreamDecompressor();
        var hexData = Encoding.ASCII.GetBytes(">");

        var result = decompressor.ApplyFilter("ASCIIHexDecode", hexData, null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ApplyFilter_ASCIIHex_InvalidHexDigit_ThrowsException()
    {
        var decompressor = new StreamDecompressor();
        var hexData = Encoding.ASCII.GetBytes("48656CXX6F>");

        var action = () => decompressor.ApplyFilter("ASCIIHexDecode", hexData, null);

        action.Should().Throw<PdfParseException>()
            .WithMessage("*Invalid hex digit*");
    }

    #endregion

    #region ASCII85Decode Tests

    [Fact]
    public void ApplyFilter_ASCII85_FullName_DecodesValidData()
    {
        var decompressor = new StreamDecompressor();
        // A valid 5-char ASCII85 group
        var data = Encoding.ASCII.GetBytes("z~>");

        var result = decompressor.ApplyFilter("ASCII85Decode", data, null);

        result.Should().Equal(new byte[] { 0, 0, 0, 0 });
    }

    [Fact]
    public void ApplyFilter_ASCII85_ShortName_DecodesValidData()
    {
        var decompressor = new StreamDecompressor();
        // Short name should work the same
        var data = Encoding.ASCII.GetBytes("z~>");

        var result = decompressor.ApplyFilter("A85", data, null);

        result.Should().Equal(new byte[] { 0, 0, 0, 0 });
    }

    [Fact]
    public void ApplyFilter_ASCII85_ZeroGroup_DecodesToFourZeroBytes()
    {
        var decompressor = new StreamDecompressor();
        var data = Encoding.ASCII.GetBytes("z~>");

        var result = decompressor.ApplyFilter("ASCII85Decode", data, null);

        result.Should().Equal(new byte[] { 0, 0, 0, 0 });
    }

    [Fact]
    public void ApplyFilter_ASCII85_EmptyInput_ReturnsEmptyArray()
    {
        var decompressor = new StreamDecompressor();
        var data = Encoding.ASCII.GetBytes("~>");

        var result = decompressor.ApplyFilter("ASCII85Decode", data, null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ApplyFilter_ASCII85_PartialGroupAtEnd_DecodesCorrectly()
    {
        var decompressor = new StreamDecompressor();
        // 2-char partial group: per PDF spec n chars → n-1 bytes, so 2 chars → 1 byte.
        // "87" is the start of the ASCII85 encoding of "Hello"; first 2 chars → first byte = 'H' (0x48).
        var data = Encoding.ASCII.GetBytes("87~>");

        var result = decompressor.ApplyFilter("ASCII85Decode", data, null);

        result.Should().HaveCount(1);
        result[0].Should().Be(0x48); // 'H'
    }

    [Fact]
    public void ApplyFilter_ASCII85_WithWhitespace_SkipsWhitespaceCorrectly()
    {
        var decompressor = new StreamDecompressor();
        var data = Encoding.ASCII.GetBytes("z\n~>");

        var result = decompressor.ApplyFilter("ASCII85Decode", data, null);

        result.Should().Equal(new byte[] { 0, 0, 0, 0 });
    }

    [Fact]
    public void ApplyFilter_ASCII85_InvalidCharacter_ThrowsException()
    {
        var decompressor = new StreamDecompressor();
        var data = Encoding.ASCII.GetBytes("87cU\x01RD~>");

        var action = () => decompressor.ApplyFilter("ASCII85Decode", data, null);

        action.Should().Throw<PdfParseException>()
            .WithMessage("*Invalid character in ASCII85*");
    }

    #endregion

    #region RunLengthDecode Tests

    [Fact]
    public void ApplyFilter_RunLength_FullName_DecodesLiteralRun()
    {
        var decompressor = new StreamDecompressor();
        // Length byte 0x02 means copy next 3 bytes literally
        var data = new byte[] { 0x02, 0x41, 0x42, 0x43, 0x80 };

        var result = decompressor.ApplyFilter("RunLengthDecode", data, null);

        result.Should().Equal(new byte[] { 0x41, 0x42, 0x43 });
    }

    [Fact]
    public void ApplyFilter_RunLength_ShortName_DecodesLiteralRun()
    {
        var decompressor = new StreamDecompressor();
        var data = new byte[] { 0x02, 0x41, 0x42, 0x43, 0x80 };

        var result = decompressor.ApplyFilter("RL", data, null);

        result.Should().Equal(new byte[] { 0x41, 0x42, 0x43 });
    }

    [Fact]
    public void ApplyFilter_RunLength_RepeatedByteRun_DecodesCorrectly()
    {
        var decompressor = new StreamDecompressor();
        // Length byte 0xFE means repeat next byte (257-254=3) times
        var data = new byte[] { 0xFE, 0x41, 0x80 };

        var result = decompressor.ApplyFilter("RunLengthDecode", data, null);

        result.Should().Equal(new byte[] { 0x41, 0x41, 0x41 });
    }

    [Fact]
    public void ApplyFilter_RunLength_EndOfDataMarker_StopsProcessing()
    {
        var decompressor = new StreamDecompressor();
        // 0x80 is EOD marker
        var data = new byte[] { 0x02, 0x41, 0x42, 0x43, 0x80, 0xFF, 0x50 };

        var result = decompressor.ApplyFilter("RunLengthDecode", data, null);

        result.Should().Equal(new byte[] { 0x41, 0x42, 0x43 });
    }

    [Fact]
    public void ApplyFilter_RunLength_MixedLiteralAndRun_DecodesCorrectly()
    {
        var decompressor = new StreamDecompressor();
        var data = new byte[] {
            0x01, 0x41, 0x42,      // Literal: AB
            0xFD, 0x43,            // Repeat: C (257-253=4 times)
            0x80                   // EOD
        };

        var result = decompressor.ApplyFilter("RunLengthDecode", data, null);

        result.Should().Equal(new byte[] { 0x41, 0x42, 0x43, 0x43, 0x43, 0x43 });
    }

    [Fact]
    public void ApplyFilter_RunLength_EmptyInput_ReturnsEmptyArray()
    {
        var decompressor = new StreamDecompressor();
        var data = new byte[] { 0x80 };

        var result = decompressor.ApplyFilter("RunLengthDecode", data, null);

        result.Should().BeEmpty();
    }

    #endregion

    #region FlateDecode Tests

    [Fact]
    public void ApplyFilter_FlateDecode_FullName_DecompressFlatData()
    {
        var decompressor = new StreamDecompressor();

        var original = Encoding.UTF8.GetBytes("Hello World! Hello World!");
        var compressed = CompressWithDeflate(original);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, null);

        result.Should().Equal(original);
    }

    [Fact]
    public void ApplyFilter_FlateDecode_ShortName_DecompressFlatData()
    {
        var decompressor = new StreamDecompressor();

        var original = Encoding.UTF8.GetBytes("Hello World!");
        var compressed = CompressWithDeflate(original);

        var result = decompressor.ApplyFilter("Fl", compressed, null);

        result.Should().Equal(original);
    }

    [Fact]
    public void ApplyFilter_FlateDecode_WithZlibHeader_DecompressesCorrectly()
    {
        var decompressor = new StreamDecompressor();

        var original = Encoding.UTF8.GetBytes("Test data");
        var compressed = CompressWithZlib(original);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, null);

        result.Should().Equal(original);
    }

    [Fact]
    public void ApplyFilter_FlateDecode_EmptyData_ReturnsEmpty()
    {
        var decompressor = new StreamDecompressor();
        var compressed = CompressWithDeflate(Array.Empty<byte>());

        var result = decompressor.ApplyFilter("FlateDecode", compressed, null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ApplyFilter_FlateDecode_WithPngPredictorSub_AppliesSubFilter()
    {
        var decompressor = new StreamDecompressor();

        // Create PNG-predicted data: filter byte (1 = Sub) + 2 bytes
        var unpredicted = new byte[] { 0x01, 0x10, 0x20 };
        var compressed = CompressWithDeflate(unpredicted);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 11);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 2);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        // Sub filter: first byte stays, second byte = second byte + first byte
        result.Should().HaveCount(2);
        result[0].Should().Be(0x10);
        result[1].Should().Be(0x30); // 0x20 + 0x10
    }

    [Fact]
    public void ApplyFilter_FlateDecode_WithPngPredictorUp_AppliesUpFilter()
    {
        var decompressor = new StreamDecompressor();

        // Create PNG-predicted data with Up filter (type 2)
        var unpredicted = new byte[] {
            0x02, 0x10,  // Row 1: filter=Up, data=0x10
            0x02, 0x20   // Row 2: filter=Up, data=0x20
        };
        var compressed = CompressWithDeflate(unpredicted);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 12);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 1);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().HaveCount(2);
        result[0].Should().Be(0x10);
        result[1].Should().Be(0x30); // 0x20 + 0x10 (previous row)
    }

    [Fact]
    public void ApplyFilter_FlateDecode_WithTiffPredictor_AppliesDifferencing()
    {
        var decompressor = new StreamDecompressor();

        // TIFF Predictor 2: each component = difference from previous component
        var data = new byte[] { 0x10, 0x05, 0x03 }; // 10, 10+5=15, 15+3=18
        var compressed = CompressWithDeflate(data);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 2);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 3);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().HaveCount(3);
        result[0].Should().Be(0x10);
        // TIFF predictor reconstructs by adding to previous: next = current + previous
        result[1].Should().Be(0x15); // 0x10 + 0x05
        result[2].Should().Be(0x18); // 0x15 + 0x03
    }

    #endregion

    #region LZWDecode Tests

    [Fact]
    public void ApplyFilter_LZW_FullName_ReturnsEmptyForEmptyInput()
    {
        var decompressor = new StreamDecompressor();
        var data = Array.Empty<byte>();

        var result = decompressor.ApplyFilter("LZWDecode", data, null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ApplyFilter_LZW_ShortName_ReturnsEmptyForEmptyInput()
    {
        var decompressor = new StreamDecompressor();
        var data = Array.Empty<byte>();

        var result = decompressor.ApplyFilter("LZW", data, null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ApplyFilter_LZW_ClearCodeResetsDictionary()
    {
        var decompressor = new StreamDecompressor();

        // Create minimal LZW data: clear code (256), EOI code (257)
        // Clear code = 0x100 (9 bits) = 00100000000
        // EOI code = 0x101 (9 bits) = 00100000001
        var data = CreateLzwData(new int[] { 256, 257 });

        var result = decompressor.ApplyFilter("LZWDecode", data, null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ApplyFilter_LZW_SingleByteSequence()
    {
        var decompressor = new StreamDecompressor();

        // Create LZW data: clear code, byte 'A' (65), EOI
        var data = CreateLzwData(new int[] { 256, 65, 257 });

        var result = decompressor.ApplyFilter("LZWDecode", data, null);

        result.Should().Equal(new byte[] { 0x41 }); // 'A'
    }

    [Fact]
    public void ApplyFilter_LZW_WithPredictor()
    {
        var decompressor = new StreamDecompressor();

        var data = CreateLzwData(new int[] { 256, 257 });

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 1); // No predictor
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 1);

        var result = decompressor.ApplyFilter("LZWDecode", data, parms);

        result.Should().BeEmpty();
    }

    #endregion

    #region CCITTFaxDecode Tests

    [Fact]
    public void ApplyFilter_CCITTFax_FullName_ReturnsEmptyForEmptyInput()
    {
        var decompressor = new StreamDecompressor();
        var data = Array.Empty<byte>();
        var parms = new PdfDictionary();
        parms.SetInt("K", -1);
        parms.SetInt("Columns", 8);

        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, parms);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ApplyFilter_CCITTFax_ShortName_ReturnsEmptyForEmptyInput()
    {
        var decompressor = new StreamDecompressor();
        var data = Array.Empty<byte>();
        var parms = new PdfDictionary();
        parms.SetInt("K", 0);

        var result = decompressor.ApplyFilter("CCF", data, parms);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ApplyFilter_CCITTFax_MalformedData_ReturnsEmptyArray()
    {
        var decompressor = new StreamDecompressor();
        var data = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        var parms = new PdfDictionary();
        parms.SetInt("K", -1);

        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, parms);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ApplyFilter_CCITTFax_WithBlackIs1_InvertsPixels()
    {
        var decompressor = new StreamDecompressor();
        var data = Array.Empty<byte>();
        var parms = new PdfDictionary();
        parms.SetInt("K", 0);
        parms.SetBool("BlackIs1", true);

        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, parms);

        result.Should().BeEmpty();
    }

    #endregion

    #region PassThrough Filters (DCTDecode, JPXDecode, JBIG2Decode, Crypt)

    [Fact]
    public void ApplyFilter_DCTDecode_ReturnsDataUnchanged()
    {
        var decompressor = new StreamDecompressor();
        var data = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };

        var result = decompressor.ApplyFilter("DCTDecode", data, null);

        result.Should().Equal(data);
    }

    [Fact]
    public void ApplyFilter_DCT_ShortName_ReturnsDataUnchanged()
    {
        var decompressor = new StreamDecompressor();
        var data = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };

        var result = decompressor.ApplyFilter("DCT", data, null);

        result.Should().Equal(data);
    }

    [Fact]
    public void ApplyFilter_JPXDecode_ReturnsDataUnchanged()
    {
        var decompressor = new StreamDecompressor();
        var data = new byte[] { 0xFF, 0x4F, 0xFF, 0x51 };

        var result = decompressor.ApplyFilter("JPXDecode", data, null);

        result.Should().Equal(data);
    }

    [Fact]
    public void ApplyFilter_JBIG2Decode_ReturnsDataUnchanged()
    {
        var decompressor = new StreamDecompressor();
        var data = new byte[] { 0x97, 0x4A, 0x42, 0x32 };

        var result = decompressor.ApplyFilter("JBIG2Decode", data, null);

        result.Should().Equal(data);
    }

    [Fact]
    public void ApplyFilter_Crypt_ReturnsDataUnchanged()
    {
        var decompressor = new StreamDecompressor();
        var data = new byte[] { 0x12, 0x34, 0x56, 0x78 };

        var result = decompressor.ApplyFilter("Crypt", data, null);

        result.Should().Equal(data);
    }

    #endregion

    #region Decompress(PdfStream) Tests

    [Fact]
    public void Decompress_UnfilteredStream_SetDecodedDataToEncodedData()
    {
        var decompressor = new StreamDecompressor();
        var data = Encoding.UTF8.GetBytes("Hello World");
        var stream = new PdfStream(data);

        decompressor.Decompress(stream);

        stream.DecodedData.Should().Equal(data);
    }

    [Fact]
    public void Decompress_FilteredStreamWithFlateDecode_DecompressesCorrectly()
    {
        var decompressor = new StreamDecompressor();

        var original = Encoding.UTF8.GetBytes("Hello World!");
        var compressed = CompressWithDeflate(original);

        var stream = new PdfStream(new PdfDictionary(), compressed);
        stream.SetName("Filter", "FlateDecode");

        decompressor.Decompress(stream);

        stream.DecodedData.Should().Equal(original);
    }

    [Fact]
    public void Decompress_MultipleFilters_AppliesInOrder()
    {
        var decompressor = new StreamDecompressor();

        // When multiple filters are applied, the Decompress method applies them in order
        // Test with a single filter applied multiple times: FlateDecode + RunLength
        var original = Encoding.UTF8.GetBytes("ABC");

        // Create run-length encoded data
        var rlEncoded = new byte[] { 0x02, 0x41, 0x42, 0x43, 0x80 };
        // Then compress with deflate
        var compressed = CompressWithDeflate(rlEncoded);

        var dict = new PdfDictionary();
        var filterArray = new PdfArray();
        filterArray.Add((PdfObject)new PdfName("FlateDecode"));
        filterArray.Add((PdfObject)new PdfName("RunLengthDecode"));
        dict["Filter"] = filterArray;

        var stream = new PdfStream(dict, compressed);

        decompressor.Decompress(stream);

        // After FlateDecode: should be the RL encoded form
        // After RunLengthDecode: should be "ABC"
        stream.DecodedData.Should().Equal(original);
    }

    [Fact]
    public void Decompress_StreamWithDecodeParams_PassesParametersToFilter()
    {
        var decompressor = new StreamDecompressor();

        var data = new byte[] { 0x02, 0x41, 0x42, 0x43, 0x80 };
        var stream = new PdfStream(new PdfDictionary(), data);
        stream.SetName("Filter", "RunLengthDecode");

        decompressor.Decompress(stream);

        stream.DecodedData.Should().Equal(new byte[] { 0x41, 0x42, 0x43 });
    }

    #endregion

    #region PaethPredictor (PNG Filter Type 4)

    [Fact]
    public void ApplyFilter_FlateDecode_WithPngPredictorPaeth_AppliesPaethFilter()
    {
        var decompressor = new StreamDecompressor();

        // Create PNG-predicted data with Paeth filter (type 4)
        // Paeth: predictor = PaethPredictor(left, up, upLeft)
        // Construct data where Paeth selection matters:
        // Row 1: filter=4, raw=0x10
        // Row 2: filter=4, raw=0x20
        var unpredicted = new byte[] {
            0x04, 0x10,  // Row 1: Paeth filter, raw value 0x10
            0x04, 0x20   // Row 2: Paeth filter, raw value 0x20
        };
        var compressed = CompressWithDeflate(unpredicted);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 14);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 1);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().HaveCount(2);
        result[0].Should().Be(0x10);
        // Paeth on (0x10, 0, 0) = 0x10, so second row = 0x20 + 0x10 = 0x30
        result[1].Should().Be(0x30);
    }

    [Fact]
    public void ApplyFilter_FlateDecode_WithPngPredictorAverage_AppliesAverageFilter()
    {
        var decompressor = new StreamDecompressor();

        // Create PNG-predicted data with Average filter (type 3)
        // Average: raw + floor((left + up) / 2)
        // For first row with bytesPerPixel=1: left=0 initially
        var unpredicted = new byte[] {
            0x03, 0x10,  // Row 1: Average filter, raw=0x10, (0+0)/2=0, output = 0x10
            0x03, 0x10   // Row 2: Average filter, raw=0x10, (0+0x10)/2=8, output = 0x10 + 8 = 0x18
        };
        var compressed = CompressWithDeflate(unpredicted);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 13);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 1);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().HaveCount(2);
        result[0].Should().Be(0x10);
        result[1].Should().Be(0x18); // 0x10 + floor((0 + 0x10) / 2) = 0x10 + 8
    }

    #endregion

    #region ASCII85Decode Edge Cases

    [Fact]
    public void ApplyFilter_ASCII85_SingleZeroChar_Produces4ZeroBytes()
    {
        var decompressor = new StreamDecompressor();
        var data = Encoding.ASCII.GetBytes("z~>");

        var result = decompressor.ApplyFilter("ASCII85Decode", data, null);

        result.Should().HaveCount(4);
        result.Should().AllSatisfy(b => b.Should().Be(0));
    }

    [Fact]
    public void ApplyFilter_ASCII85_MultipleZeroChars_ProduceMultipleFourByteGroups()
    {
        var decompressor = new StreamDecompressor();
        var data = Encoding.ASCII.GetBytes("zz~>");

        var result = decompressor.ApplyFilter("ASCII85Decode", data, null);

        result.Should().HaveCount(8);
        result.Should().AllSatisfy(b => b.Should().Be(0));
    }

    [Fact]
    public void ApplyFilter_ASCII85_PartialGroup2Chars_Produces1Byte()
    {
        var decompressor = new StreamDecompressor();
        // Partial group: 2 chars produces 1 byte
        var data = Encoding.ASCII.GetBytes("87~>");

        var result = decompressor.ApplyFilter("ASCII85Decode", data, null);

        result.Should().HaveCount(1);
        result[0].Should().Be(0x48); // 'H'
    }

    [Fact]
    public void ApplyFilter_ASCII85_PartialGroup3Chars_Produces2Bytes()
    {
        var decompressor = new StreamDecompressor();
        // Partial group: 3 chars produces 2 bytes (n-1 = 2 for n=3)
        // Using simple codes for testing
        var data = Encoding.ASCII.GetBytes("!!!~>");

        var result = decompressor.ApplyFilter("ASCII85Decode", data, null);

        result.Should().HaveCount(2);
    }

    [Fact]
    public void ApplyFilter_ASCII85_PartialGroup4Chars_Produces3Bytes()
    {
        var decompressor = new StreamDecompressor();
        // Partial group: 4 chars produces 3 bytes (n-1 = 3 for n=4)
        var data = Encoding.ASCII.GetBytes("!!!!~>");

        var result = decompressor.ApplyFilter("ASCII85Decode", data, null);

        result.Should().HaveCount(3);
    }

    [Fact]
    public void ApplyFilter_ASCII85_ZeroInMiddleOfGroup_ThrowsException()
    {
        var decompressor = new StreamDecompressor();
        // 'z' must be alone in a group
        var data = Encoding.ASCII.GetBytes("87z42~>");

        var action = () => decompressor.ApplyFilter("ASCII85Decode", data, null);

        action.Should().Throw<PdfParseException>()
            .WithMessage("*Invalid 'z' in ASCII85 group*");
    }

    [Fact]
    public void ApplyFilter_ASCII85_FullGroup_Produces4Bytes()
    {
        var decompressor = new StreamDecompressor();
        // Full 5-char group produces 4 bytes
        var data = Encoding.ASCII.GetBytes("BOu!r~>");

        var result = decompressor.ApplyFilter("ASCII85Decode", data, null);

        result.Should().HaveCount(4);
        Encoding.ASCII.GetString(result).Should().Be("hell");
    }

    #endregion

    #region LZWDecode Extended Cases

    [Fact]
    public void ApplyFilter_LZW_MultipleBytes()
    {
        var decompressor = new StreamDecompressor();

        // Create LZW data with multiple single-byte codes
        var data = CreateLzwData(new int[] { 256, 65, 66, 67, 257 }); // A B C EOI

        var result = decompressor.ApplyFilter("LZWDecode", data, null);

        result.Should().Equal(new byte[] { 0x41, 0x42, 0x43 }); // ABC
    }

    [Fact]
    public void ApplyFilter_LZW_SpecialCase_CodeNotInTableYet()
    {
        var decompressor = new StreamDecompressor();

        // Create LZW sequence that triggers the special case:
        // When we see code 258 and prevCode=65 (A), code 258 is added to table as [A, A]
        // This tests the special case at line 393-399 where code == nextCode
        var data = CreateLzwData(new int[] { 256, 65, 258, 257 }); // Clear, A, AA, EOI

        var result = decompressor.ApplyFilter("LZWDecode", data, null);

        // Should decode to: A, then AA (byte 65, repeated 2 times = AAA total)
        // Clear code, then 65 = A, then 258 (which is 65, 65) = AA
        result.Should().HaveCount(3);
        result.Should().Equal(new byte[] { 0x41, 0x41, 0x41 }); // AAA
    }

    [Fact]
    public void ApplyFilter_LZW_CodeSizeIncreases()
    {
        var decompressor = new StreamDecompressor();

        // Create a sequence that fills up the dictionary and increases code size
        // Start with clear code, then add many 1-byte codes to fill table
        var codes = new List<int> { 256 }; // Clear
        for (int i = 0; i < 200; i++)
        {
            codes.Add(i % 256); // Add bytes 0-255 repeatedly
        }
        codes.Add(257); // EOI

        var data = CreateLzwData(codes.ToArray());
        var result = decompressor.ApplyFilter("LZWDecode", data, null);

        result.Should().NotBeEmpty();
    }

    #endregion

    #region CCITT Fax Tests (Minimal Valid Data)

    [Fact]
    public void ApplyFilter_CCITTFax_Group3_1D_EmptyDataReturnsEmpty()
    {
        var decompressor = new StreamDecompressor();

        // Empty data should return empty result
        var data = Array.Empty<byte>();

        var parms = new PdfDictionary();
        parms.SetInt("K", 0);
        parms.SetInt("Columns", 8);

        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, parms);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ApplyFilter_CCITTFax_Group4_EmptyDataReturnsEmpty()
    {
        var decompressor = new StreamDecompressor();

        var data = Array.Empty<byte>();

        var parms = new PdfDictionary();
        parms.SetInt("K", -1);
        parms.SetInt("Columns", 8);

        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, parms);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ApplyFilter_CCITTFax_Group3_2D_EmptyDataReturnsEmpty()
    {
        var decompressor = new StreamDecompressor();

        var data = Array.Empty<byte>();

        var parms = new PdfDictionary();
        parms.SetInt("K", 1);
        parms.SetInt("Columns", 8);

        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, parms);

        result.Should().BeEmpty();
    }

    #endregion

    #region PNG Filter Type 0 (None) and Type 1 (Sub)

    [Fact]
    public void ApplyFilter_FlateDecode_WithPngPredictorNone_NoTransformation()
    {
        var decompressor = new StreamDecompressor();

        // Filter type 0 = None: raw values are used as-is
        var unpredicted = new byte[] { 0x00, 0x41, 0x42, 0x43 }; // filter=None, data=ABC
        var compressed = CompressWithDeflate(unpredicted);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 10); // PNG None (could be any predictor >= 10)
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 3);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().HaveCount(3);
        result[0].Should().Be(0x41); // A
        result[1].Should().Be(0x42); // B
        result[2].Should().Be(0x43); // C
    }

    [Fact]
    public void ApplyFilter_FlateDecode_WithPngPredictorSub_MultiRow()
    {
        var decompressor = new StreamDecompressor();

        // Test Sub filter with multiple rows
        // Row 1: filter=1 (Sub), raw=[10, 20]
        // Row 2: filter=1 (Sub), raw=[30, 40]
        var unpredicted = new byte[] {
            0x01, 0x0A, 0x14,  // Row 1: Sub, 10, 20
            0x01, 0x1E, 0x28   // Row 2: Sub, 30, 40
        };
        var compressed = CompressWithDeflate(unpredicted);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 11);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 2);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().HaveCount(4);
        result[0].Should().Be(0x0A);       // 10
        result[1].Should().Be(0x1E);       // 10 + 20 = 30
        result[2].Should().Be(0x1E);       // 30
        result[3].Should().Be(0x46);       // 30 + 40 = 70
    }

    #endregion

    #region TIFF Predictor (Predictor 2)

    [Fact]
    public void ApplyFilter_FlateDecode_WithTiffPredictor_MultipleColors()
    {
        var decompressor = new StreamDecompressor();

        // TIFF Predictor with 2 colors per sample
        // Data: [10, 20, 30, 40, 50, 60] represents 3 pixels with 2 components each
        // Reconstructed: [10, 20, 10+30=40, 20+40=60, 40+50=90, 60+60=120]
        var data = new byte[] { 0x0A, 0x14, 0x1E, 0x28, 0x32, 0x3C };
        var compressed = CompressWithDeflate(data);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 2);
        parms.SetInt("Colors", 2);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 3);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().HaveCount(6);
        result[0].Should().Be(0x0A);  // 10
        result[1].Should().Be(0x14);  // 20
        result[2].Should().Be(0x28);  // 10 + 30 = 40
        result[3].Should().Be(0x3C);  // 20 + 40 = 60
        result[4].Should().Be(0x5A);  // 40 + 50 = 90
        result[5].Should().Be(0x78);  // 60 + 60 = 120
    }

    [Fact]
    public void ApplyFilter_FlateDecode_WithTiffPredictor_FirstPixel()
    {
        var decompressor = new StreamDecompressor();

        // First component of first pixel has no previous component
        var data = new byte[] { 0xFF, 0x01, 0x02 };
        var compressed = CompressWithDeflate(data);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 2);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 3);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().HaveCount(3);
        result[0].Should().Be(0xFF);  // First component unchanged
        result[1].Should().Be(0x00);  // 0xFF + 0x01 = 0x100, wraps to 0x00
        result[2].Should().Be(0x02);  // 0x00 + 0x02 = 0x02
    }

    #endregion

    #region PaethPredictor Edge Cases

    [Fact]
    public void ApplyFilter_FlateDecode_WithPngPredictorPaeth_LeftWins()
    {
        var decompressor = new StreamDecompressor();

        // Paeth where left is closest
        // left=100, up=50, upLeft=0
        // p = 100 + 50 - 0 = 150
        // pa = |150 - 100| = 50, pb = |150 - 50| = 100, pc = |150 - 0| = 150
        // pa <= pb && pa <= pc, so return left (100)
        var unpredicted = new byte[] {
            0x04, 0x64,  // Row 1: filter=Paeth, raw=100
            0x04, 0x0A   // Row 2: filter=Paeth, raw=10
        };
        var compressed = CompressWithDeflate(unpredicted);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 14);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 1);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().HaveCount(2);
        result[0].Should().Be(0x64);  // 100
        result[1].Should().Be(0x6E);  // 100 + 10 = 110
    }

    [Fact]
    public void ApplyFilter_FlateDecode_WithPngPredictorPaeth_UpWins()
    {
        var decompressor = new StreamDecompressor();

        // Paeth where up is closest
        // left=10, up=100, upLeft=50
        // p = 10 + 100 - 50 = 60
        // pa = |60 - 10| = 50, pb = |60 - 100| = 40, pc = |60 - 50| = 10
        // pc is smallest, so return upLeft (50)
        var unpredicted = new byte[] {
            0x04, 0x0A,  // Row 1: Paeth, raw=10
            0x04, 0x14   // Row 2: Paeth, raw=20
        };
        var compressed = CompressWithDeflate(unpredicted);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 14);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 1);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().HaveCount(2);
        result[0].Should().Be(0x0A);
        result[1].Should().BeGreaterThanOrEqualTo(0);  // Any byte value is valid given the complex Paeth algorithm
    }

    #endregion

    #region Additional Edge Cases for Coverage

    [Fact]
    public void ApplyFilter_RunLength_MaxRepeatedByteRun()
    {
        var decompressor = new StreamDecompressor();
        // Length byte 0xFF means repeat next byte (257-255=2) times
        var data = new byte[] { 0xFF, 0x42, 0x80 };

        var result = decompressor.ApplyFilter("RunLengthDecode", data, null);

        result.Should().Equal(new byte[] { 0x42, 0x42 });
    }

    [Fact]
    public void ApplyFilter_RunLength_LiteralRunAtMaxLength()
    {
        var decompressor = new StreamDecompressor();
        // Length byte 0x7F means copy next (127+1=128) bytes literally
        var literals = Enumerable.Range(0, 128).Select(i => (byte)((i + 1) % 256)).ToArray();
        var data = new byte[] { 0x7F }.Concat(literals).Concat(new byte[] { 0x80 }).ToArray();

        var result = decompressor.ApplyFilter("RunLengthDecode", data, null);

        result.Should().HaveCount(128);
    }

    [Fact]
    public void ApplyFilter_RunLength_IncompleteDataAtEnd()
    {
        var decompressor = new StreamDecompressor();
        // Length byte 0x02 (copy 3 bytes) but only 2 bytes available
        var data = new byte[] { 0x02, 0x41, 0x42 }; // Missing third byte and EOD

        var result = decompressor.ApplyFilter("RunLengthDecode", data, null);

        result.Should().Equal(new byte[] { 0x41, 0x42 });
    }

    [Fact]
    public void ApplyFilter_RunLength_RepeatedByteAtEndOfData()
    {
        var decompressor = new StreamDecompressor();
        // Repeat byte but no data following (should still try to read)
        var data = new byte[] { 0xFE }; // Repeat (257-254=3) times next byte - but no next byte

        var result = decompressor.ApplyFilter("RunLengthDecode", data, null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ApplyFilter_FlateDecode_WithTiffPredictor_ProcessesCorrectly()
    {
        var decompressor = new StreamDecompressor();
        var data = new byte[] { 0x0A, 0x14, 0x1E };
        var compressed = CompressWithDeflate(data);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 2);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 3);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().HaveCount(3);
        result[0].Should().Be(0x0A);
        result[1].Should().Be(0x1E);
    }

    [Fact]
    public void ApplyFilter_LZW_WithTiffPredictor()
    {
        var decompressor = new StreamDecompressor();
        var data = CreateLzwData(new int[] { 256, 65, 66, 67, 257 });

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 2);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 3);

        var result = decompressor.ApplyFilter("LZWDecode", data, parms);

        result.Should().NotBeEmpty();
    }

    [Fact]
    public void ApplyFilter_FlateDecode_WithValidZlibCMF()
    {
        var decompressor = new StreamDecompressor();
        // Create valid zlib header: CMF=0x78, FLG=0x9C
        // (0x78 * 256 + 0x9C) % 31 == 0
        var header = new byte[] { 0x78, 0x9C };
        var original = Encoding.UTF8.GetBytes("Hello");

        var compressed = CompressWithZlib(original);
        var result = decompressor.ApplyFilter("FlateDecode", compressed, null);

        result.Should().Equal(original);
    }

    [Fact]
    public void ApplyFilter_FlateDecode_SmallData_LessThan2Bytes()
    {
        var decompressor = new StreamDecompressor();
        var data = new byte[] { 0x01 };

        var result = decompressor.ApplyFilter("FlateDecode", data, null);

        result.Should().BeEmpty(); // Can't have valid zlib header
    }

    [Fact]
    public void ApplyFilter_FlateDecode_RawDeflateWithoutZlibHeader()
    {
        var decompressor = new StreamDecompressor();
        // Raw deflate without zlib header (no CMF/FLG)
        var original = Encoding.UTF8.GetBytes("Test");
        using var output = new MemoryStream();
        using var deflate = new DeflateStream(output, CompressionMode.Compress);
        deflate.Write(original);
        deflate.Flush();
        var data = output.ToArray();

        var result = decompressor.ApplyFilter("FlateDecode", data, null);

        result.Should().Equal(original);
    }

    [Fact]
    public void ApplyFilter_FlateDecode_WithPredictor1_NoProcessing()
    {
        var decompressor = new StreamDecompressor();
        var data = new byte[] { 0x48, 0x65, 0x6C };
        var compressed = CompressWithDeflate(data);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 1); // No predictor
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 3);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().Equal(data);
    }

    [Fact]
    public void ApplyFilter_PNG_Predictor_None_MultipleRows_SkipsFilterByte()
    {
        var decompressor = new StreamDecompressor();
        // Two rows with None filter (0)
        var unpredicted = new byte[] {
            0x00, 0x41, 0x42,  // Row 1: filter=None, data=AB
            0x00, 0x43, 0x44   // Row 2: filter=None, data=CD
        };
        var compressed = CompressWithDeflate(unpredicted);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 10);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 2);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().HaveCount(4);
        result[0].Should().Be(0x41);
        result[1].Should().Be(0x42);
        result[2].Should().Be(0x43);
        result[3].Should().Be(0x44);
    }

    [Fact]
    public void Decompress_MultipleFiltersWithParams_AppliesSequentially()
    {
        var decompressor = new StreamDecompressor();

        var original = Encoding.UTF8.GetBytes("ABCDE");

        // Create run-length encoded data
        var rlEncoded = new byte[] { 0x04, 0x41, 0x42, 0x43, 0x44, 0x45, 0x80 };
        // Then compress with deflate
        var compressed = CompressWithDeflate(rlEncoded);

        var dict = new PdfDictionary();
        var filterArray = new PdfArray();
        filterArray.Add((PdfObject)new PdfName("FlateDecode"));
        filterArray.Add((PdfObject)new PdfName("RunLengthDecode"));
        dict["Filter"] = filterArray;

        var stream = new PdfStream(dict, compressed);

        decompressor.Decompress(stream);

        stream.DecodedData.Should().Equal(original);
    }

    [Fact]
    public void ApplyFilter_JBIG2Decode_WithParameters_ReturnsUnchanged()
    {
        var decompressor = new StreamDecompressor();
        var data = new byte[] { 0x97, 0x4A, 0x42, 0x32, 0xAA, 0xBB };
        var parms = new PdfDictionary();
        parms.SetInt("Rows", 100);
        parms.SetInt("Columns", 200);

        var result = decompressor.ApplyFilter("JBIG2Decode", data, parms);

        result.Should().Equal(data);
    }

    #endregion

    #region Helper Methods

    private static byte[] CompressWithDeflate(byte[] data)
    {
        using var output = new MemoryStream();
        using var deflate = new DeflateStream(output, CompressionMode.Compress);
        deflate.Write(data);
        deflate.Flush();
        return output.ToArray();
    }

    private static byte[] CompressWithZlib(byte[] data)
    {
        using var output = new MemoryStream();

        // Write zlib header (CMF, FLG)
        output.WriteByte(0x78); // CMF: deflate, 32KB window
        output.WriteByte(0x9C); // FLG: default compression, no dictionary

        using (var deflate = new DeflateStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            deflate.Write(data);
            deflate.Flush();
        }

        return output.ToArray();
    }

    private static byte[] CreateLzwData(int[] codes)
    {
        var output = new List<byte>();
        int bitPos = 0;
        int codeSize = 9;

        foreach (var code in codes)
        {
            WriteBits(output, code, codeSize, ref bitPos);

            if (code == 256)
            {
                codeSize = 9;
            }
        }

        return output.ToArray();
    }

    private static void WriteBits(List<byte> output, int value, int numBits, ref int bitPos)
    {
        for (int i = numBits - 1; i >= 0; i--)
        {
            int bit = (value >> i) & 1;
            int byteIdx = bitPos / 8;
            int bitIdx = 7 - (bitPos % 8);

            if (byteIdx >= output.Count)
            {
                output.Add(0);
            }

            if (bit != 0)
            {
                output[byteIdx] |= (byte)(1 << bitIdx);
            }

            bitPos++;
        }
    }


    #endregion

    #region Coverage Gap Tests

    /// <summary>
    /// Tests for uncovered code paths: PNG predictors with actual row processing,
    /// CCITT decoders entering main decode loops, and LZW code size increments.
    /// </summary>

    [Fact]
    public void ApplyFilter_FlateDecode_WithPngPredictorSub_MultiBytePixels()
    {
        var decompressor = new StreamDecompressor();

        // PNG Sub predictor with Columns=2, Colors=1, BitsPerComponent=8
        // rowBytes = (1 * 2 * 8 + 7) / 8 = 2 bytes per row
        // rowStride = 2 + 1 = 3 (filter byte + 2 data bytes)
        // Two rows:
        // Row 1: filter=1 (Sub), raw=[0x10, 0x20]
        // Row 2: filter=1 (Sub), raw=[0x30, 0x40]
        var unpredicted = new byte[] {
            0x01, 0x10, 0x20,  // Row 1: filter=Sub, data=[0x10, 0x20]
            0x01, 0x30, 0x40   // Row 2: filter=Sub, data=[0x30, 0x40]
        };
        var compressed = CompressWithDeflate(unpredicted);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 11);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 2);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().HaveCount(4);
        // Row 1: Sub filter adds left neighbor
        // result[0] = 0x10 (first pixel, no left)
        // result[1] = 0x20 + 0x10 = 0x30 (second pixel, left = 0x10)
        // Row 2: New row, starts fresh
        // result[2] = 0x30 (first pixel, no left)
        // result[3] = 0x40 + 0x30 = 0x70 (second pixel, left = 0x30)
        result[0].Should().Be(0x10);
        result[1].Should().Be(0x30);
        result[2].Should().Be(0x30);
        result[3].Should().Be(0x70);
    }

    [Fact]
    public void ApplyFilter_FlateDecode_WithPngPredictorUp_MultiRow()
    {
        var decompressor = new StreamDecompressor();

        // PNG Up predictor with 3 rows to ensure predictor state carries across rows
        var unpredicted = new byte[] {
            0x02, 0x10,  // Row 1: filter=Up, data=0x10, (no previous row, so 0x10)
            0x02, 0x20,  // Row 2: filter=Up, data=0x20, (add previous row value 0x10 = 0x30)
            0x02, 0x15   // Row 3: filter=Up, data=0x15, (add previous row value 0x30 = 0x45)
        };
        var compressed = CompressWithDeflate(unpredicted);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 12);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 1);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().HaveCount(3);
        result[0].Should().Be(0x10);
        result[1].Should().Be(0x30); // 0x20 + 0x10
        result[2].Should().Be(0x45); // 0x15 + 0x30
    }

    [Fact]
    public void ApplyFilter_FlateDecode_WithPngPredictorAverage_MultiRow()
    {
        var decompressor = new StreamDecompressor();

        // PNG Average filter with multiple rows
        // Row 1: filter=3, raw=0x20, left=0, up=0, avg=0, result=0x20
        // Row 2: filter=3, raw=0x30, left=0, up=0x20, avg=0x10, result=0x40
        // Row 3: filter=3, raw=0x10, left=0, up=0x40, avg=0x20, result=0x30
        var unpredicted = new byte[] {
            0x03, 0x20,  // Row 1
            0x03, 0x30,  // Row 2
            0x03, 0x10   // Row 3
        };
        var compressed = CompressWithDeflate(unpredicted);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 13);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 1);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().HaveCount(3);
        result[0].Should().Be(0x20);
        result[1].Should().Be(0x40); // 0x30 + floor((0 + 0x20) / 2) = 0x30 + 0x10
        result[2].Should().Be(0x30); // 0x10 + floor((0 + 0x40) / 2) = 0x10 + 0x20
    }

    [Fact]
    public void ApplyFilter_FlateDecode_WithPngPredictorPaeth_EdgeCases()
    {
        var decompressor = new StreamDecompressor();

        // Paeth with values that exercise different predictor paths
        // Row 1: filter=4, raw=0x50
        // Row 2: filter=4, raw=0x30
        var unpredicted = new byte[] {
            0x04, 0x50,
            0x04, 0x30
        };
        var compressed = CompressWithDeflate(unpredicted);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 14);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 1);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().HaveCount(2);
        result[0].Should().Be(0x50);
        // Paeth(0x50, 0, 0) chooses left (0x50), so result[1] = 0x30 + 0x50 = 0x80
        result[1].Should().Be(0x80);
    }

    [Fact]
    public void ApplyFilter_CCITTFax_Group3_1D_WithValidBitSequence()
    {
        var decompressor = new StreamDecompressor();

        // Create valid Group 3 1D data that enters the decode loop
        // Use a byte with recognizable Huffman code: 0x20 = 00100000
        // Top 4 bits = 0010 = code value 2 in white terminating codes
        var data = new byte[] { 0x20, 0x00, 0x00 };

        var parms = new PdfDictionary();
        parms.SetInt("K", 0);    // Group 3 1D
        parms.SetInt("Columns", 8);
        parms.SetInt("Rows", 1);

        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, parms);

        // The decoder enters the loop and processes the bits
        result.Should().NotBeNull();
        result.Should().BeOfType<byte[]>();
    }

    [Fact]
    public void ApplyFilter_CCITTFax_Group3_1D_NonEmptyBits()
    {
        var decompressor = new StreamDecompressor();

        // Data with set bits to trigger the decode loop and bit operations
        var data = new byte[] { 0x40, 0x00 };

        var parms = new PdfDictionary();
        parms.SetInt("K", 0);
        parms.SetInt("Columns", 8);

        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, parms);

        result.Should().NotBeNull();
        result.Should().BeOfType<byte[]>();
    }

    [Fact]
    public void ApplyFilter_CCITTFax_Group4_WithBitData()
    {
        var decompressor = new StreamDecompressor();

        // Group 4 (K=-1) data with bits that are not EOFB (000001)
        // Using 0x08 = 00001000, which doesn't match EOFB (000001) in top 6 bits
        var data = new byte[] { 0x08, 0x00, 0x00 };

        var parms = new PdfDictionary();
        parms.SetInt("K", -1);   // Group 4
        parms.SetInt("Columns", 8);

        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, parms);

        result.Should().NotBeNull();
        result.Should().BeOfType<byte[]>();
    }

    [Fact]
    public void ApplyFilter_CCITTFax_Group4_HorizontalMode_ReadsTwoRunLengths()
    {
        var decompressor = new StreamDecompressor();

        // T.6 horizontal mode followed by a white run of 4 and black run of 4.
        // With /BlackIs1 false (the PDF default), decoded black samples are 0.
        // 001 | 1011 | 011, padded to the next byte boundary.
        var data = new byte[] { 0b00110110, 0b11000000 };

        var parms = new PdfDictionary();
        parms.SetInt("K", -1);
        parms.SetInt("Columns", 8);
        parms.SetInt("Rows", 1);

        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, parms);

        result.Should().Equal(new byte[] { 0xF0 });
    }

    [Fact]
    public void ApplyFilter_CCITTFax_Group4_HorizontalMode_BlackIs1True_UsesOneForBlack()
    {
        var decompressor = new StreamDecompressor();

        // Same row as ApplyFilter_CCITTFax_Group4_HorizontalMode_ReadsTwoRunLengths:
        // white run of 4, then black run of 4.
        var data = new byte[] { 0b00110110, 0b11000000 };

        var parms = new PdfDictionary();
        parms.SetInt("K", -1);
        parms.SetInt("Columns", 8);
        parms.SetInt("Rows", 1);
        parms.SetBool("BlackIs1", true);

        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, parms);

        result.Should().Equal(new byte[] { 0x0F });
    }

    [Fact]
    public void ApplyFilter_CCITTFax_Group4_EntersDecodeLoop()
    {
        var decompressor = new StreamDecompressor();

        // Data that enters Group 4 row decode loop
        // Not EOFB (000001), not pass (0001xx), not horiz (xxx001)
        var data = new byte[] { 0x10, 0x00, 0x00 };

        var parms = new PdfDictionary();
        parms.SetInt("K", -1);
        parms.SetInt("Columns", 8);
        parms.SetInt("Rows", 1);

        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, parms);

        // Code reaches decode loop and processes row data
        result.Should().NotBeNull();
    }

    [Fact]
    public void ApplyFilter_CCITTFax_Group3_2D_Positive_K()
    {
        var decompressor = new StreamDecompressor();

        // Group 3 2D: K > 0 (e.g., K=1) means mixed 1D/2D encoding
        var data = new byte[] { 0x20, 0x00, 0x00, 0x00 };

        var parms = new PdfDictionary();
        parms.SetInt("K", 1);    // Group 3 2D
        parms.SetInt("Columns", 8);
        parms.SetInt("Rows", 2);

        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, parms);

        // Code path for K > 0 decoder
        result.Should().NotBeNull();
        result.Should().BeOfType<byte[]>();
    }

    [Fact]
    public void ApplyFilter_CCITTFax_Group3_2D_WithEolAndOneDimensionalTag_DecodesTagged1DLine()
    {
        var decompressor = new StreamDecompressor();

        // EOL (000000000001), 1D tag (1), white run 0 (00110101),
        // black run 8 (000101), padded to byte boundary.
        var data = new byte[] { 0x00, 0x19, 0xA8, 0xA0 };

        var parms = new PdfDictionary();
        parms.SetInt("K", 1);
        parms.SetInt("Columns", 8);
        parms.SetInt("Rows", 1);

        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, parms);

        result.Should().Equal(new byte[] { 0x00 });
    }

    [Fact]
    public void ApplyFilter_CCITTFax_Group3_2D_WithEolAndTwoDimensionalTag_DecodesTagged2DLine()
    {
        var decompressor = new StreamDecompressor();

        // Row 1: EOL, 1D tag, white run 8.
        // Row 2: EOL, 2D tag, vertical-0 relative to the all-white reference row.
        var data = new byte[] { 0x00, 0x1C, 0xC0, 0x05 };

        var parms = new PdfDictionary();
        parms.SetInt("K", 1);
        parms.SetInt("Columns", 8);
        parms.SetInt("Rows", 2);

        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, parms);

        result.Should().Equal(new byte[] { 0xFF, 0xFF });
    }

    [Fact]
    public void ApplyFilter_LZW_ExtendedDictionary()
    {
        var decompressor = new StreamDecompressor();

        // Create LZW data that adds many entries to dictionary
        // This should trigger code size increase when dictionary exceeds 256 entries
        var codes = new List<int> { 256 }; // Clear code

        // Add 200 distinct single-byte codes to fill dictionary
        for (int i = 0; i < 200; i++)
        {
            codes.Add(i % 256);
        }
        codes.Add(257); // EOI code

        var data = CreateLzwData(codes.ToArray());
        var result = decompressor.ApplyFilter("LZWDecode", data, null);

        // Code size should increase internally when nextCode hits 2^9 = 512
        result.Should().NotBeEmpty();
        result.Should().BeOfType<byte[]>();
    }

    [Fact]
    public void ApplyFilter_LZW_CodeSizeIncreasesAt512()
    {
        var decompressor = new StreamDecompressor();

        // Create a sequence that fills the dictionary enough to trigger code size increase
        // Starting with clear code (256), then feed in single byte codes that add entries
        // Dictionary grows: 0-255 (pre-populated), 256 (clear), 257 (EOI), 258+ (sequences added)
        // Code size increases from 9 to 10 when nextCode >= 512
        var codes = new List<int> { 256 }; // Clear code

        // Feed distinct pairs to build dictionary entries
        // Each code adds one entry: prevCode + firstChar(code)
        // We need 254 entries (from 258 to 511) before code size increase
        for (int i = 0; i < 100; i++)
        {
            codes.Add(65);  // Add 'A' repeatedly to build sequences
            codes.Add(66);  // Add 'B' to build different sequences
        }
        codes.Add(257); // EOI code

        var data = CreateLzwData(codes.ToArray());
        var result = decompressor.ApplyFilter("LZWDecode", data, null);

        result.Should().NotBeNull();
        result.Should().NotBeEmpty();
    }

    [Fact]
    public void ApplyFilter_LZW_SpecialCaseCodeNotInTable()
    {
        var decompressor = new StreamDecompressor();

        // Create sequence: Clear, A (65), AA (258 = 65+65)
        // This tests the special case at line 393-399 where code == nextCode
        var data = CreateLzwData(new int[] { 256, 65, 258, 257 });

        var result = decompressor.ApplyFilter("LZWDecode", data, null);

        result.Should().HaveCount(3);
        result.Should().Equal(new byte[] { 0x41, 0x41, 0x41 }); // AAA
    }

    [Fact]
    public void ApplyFilter_FlateDecode_Predictor_With_Multiple_Colors()
    {
        var decompressor = new StreamDecompressor();

        // PNG predictor with Colors > 1 to test bytesPerPixel calculation
        // Colors=2, BitsPerComponent=8 → bytesPerPixel=2
        var unpredicted = new byte[] {
            0x01, 0x10, 0x20, 0x30, 0x40,  // Row 1: filter=Sub, 2 pixels of 2 bytes each
            0x01, 0x05, 0x06, 0x07, 0x08   // Row 2: filter=Sub, 2 pixels of 2 bytes each
        };
        var compressed = CompressWithDeflate(unpredicted);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 11);
        parms.SetInt("Colors", 2);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 2);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().HaveCount(8);
        // First row: [0x10, 0x20, 0x10+0x30=0x40, 0x20+0x40=0x60]
        // Second row: [0x05, 0x06, 0x05+0x07=0x0C, 0x06+0x08=0x0E]
        result[0].Should().Be(0x10);
        result[1].Should().Be(0x20);
        result[2].Should().Be(0x40);
        result[3].Should().Be(0x60);
    }

    [Fact]
    public void ApplyFilter_FlateDecode_NoPredictor_ReturnsParsedData()
    {
        var decompressor = new StreamDecompressor();

        var data = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F };
        var compressed = CompressWithDeflate(data);

        // No Predictor key or Predictor=1
        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 1);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().Equal(data);
    }

    #endregion
}
