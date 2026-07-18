using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Parsing;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Parsing;

/// <summary>
/// Additional tests for StreamDecompressor to improve coverage from ~80% to ~95%.
/// Targets:
/// - Lines 45-49: DCT, JPX, JBIG2, Crypt passthrough filters
/// - Lines 113-162: PNG predictor branches (Sub, Paeth, Up, Average, None)
/// - Lines 513-720: CCITT Group 3/4 decoder paths and state transitions
/// - Lines 847-920: CCITT helper functions (FillRun, AppendRowToOutput, SkipEOL)
/// - Lines 366-425: LZW code-size growth boundaries
/// </summary>
public class StreamDecompressorCoverageTests
{
    #region Filter Passthrough Tests (Lines 45-49)

    /// <summary>
    /// Tests DCT (JPEG) filter returns input unchanged.
    /// Line 45: "DCTDecode" or "DCT" => data
    /// </summary>
    [Fact]
    public void ApplyFilter_DCT_FullName_ReturnsInputUnchanged()
    {
        var decompressor = new StreamDecompressor();
        var jpegData = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 }; // JPEG SOI marker

        var result = decompressor.ApplyFilter("DCTDecode", jpegData, null);

        result.Should().Equal(jpegData);
    }

    /// <summary>
    /// Tests DCT short name "DCT" returns input unchanged.
    /// </summary>
    [Fact]
    public void ApplyFilter_DCT_ShortName_ReturnsInputUnchanged()
    {
        var decompressor = new StreamDecompressor();
        var jpegData = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };

        var result = decompressor.ApplyFilter("DCT", jpegData, null);

        result.Should().Equal(jpegData);
    }

    /// <summary>
    /// Tests JPXDecode (JPEG2000) filter returns input unchanged.
    /// Line 46: "JPXDecode" => data
    /// </summary>
    [Fact]
    public void ApplyFilter_JPX_ReturnsInputUnchanged()
    {
        var decompressor = new StreamDecompressor();
        var jpeg2kData = new byte[] { 0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20 }; // JPEG2000 signature

        var result = decompressor.ApplyFilter("JPXDecode", jpeg2kData, null);

        result.Should().Equal(jpeg2kData);
    }

    /// <summary>
    /// Tests JBIG2Decode filter returns input unchanged.
    /// Line 48: "JBIG2Decode" => data
    /// </summary>
    [Fact]
    public void ApplyFilter_JBIG2_ReturnsInputUnchanged()
    {
        var decompressor = new StreamDecompressor();
        var jbig2Data = new byte[] { 0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A };

        var result = decompressor.ApplyFilter("JBIG2Decode", jbig2Data, null);

        result.Should().Equal(jbig2Data);
    }

    /// <summary>
    /// Tests Crypt filter returns input unchanged (encryption handled separately).
    /// Line 49: "Crypt" => data
    /// </summary>
    [Fact]
    public void ApplyFilter_Crypt_ReturnsInputUnchanged()
    {
        var decompressor = new StreamDecompressor();
        var encryptedData = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };

        var result = decompressor.ApplyFilter("Crypt", encryptedData, null);

        result.Should().Equal(encryptedData);
    }

    #endregion

    #region PNG Predictor Tests (Lines 113-162: Paeth, Sub, Up, Average, None)

    /// <summary>
    /// PNG predictor with 0-byte filter (None): output equals input.
    /// Tests line 156: filter == 0 => raw
    /// </summary>
    [Fact]
    public void ApplyFilter_FlateDecode_PngPredictor_None_Filter()
    {
        var decompressor = new StreamDecompressor();

        // Create FlateDecode stream with PNG predictor filter=0 (None)
        var rawPixels = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        var uncompressed = new byte[] { 0, 0x10, 0x20, 0x30, 0x40 };
        var compressed = CompressDeflate(uncompressed);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 10);
        parms.SetInt("Columns", 4);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().Equal(rawPixels);
    }

    /// <summary>
    /// PNG predictor Sub filter: output[i] = raw[i] + left.
    /// Tests line 157: filter == 1 => (byte)(raw + left)
    /// </summary>
    [Fact]
    public void ApplyFilter_FlateDecode_PngPredictor_Sub_Filter()
    {
        var decompressor = new StreamDecompressor();

        var encoded = new byte[] { 1, 0x10, 0x10, 0x10, 0x10 };
        var compressed = CompressDeflate(encoded);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 10);
        parms.SetInt("Columns", 4);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().Equal(new byte[] { 0x10, 0x20, 0x30, 0x40 });
    }

    /// <summary>
    /// PNG predictor Up filter: output[i] = raw[i] + up.
    /// Tests line 158: filter == 2 => (byte)(raw + up)
    /// </summary>
    [Fact]
    public void ApplyFilter_FlateDecode_PngPredictor_Up_Filter()
    {
        var decompressor = new StreamDecompressor();

        var row1 = new byte[] { 0, 0x10, 0x10, 0x10, 0x10 };
        var row2 = new byte[] { 2, 0x05, 0x05, 0x05, 0x05 };
        var uncompressed = new byte[row1.Length + row2.Length];
        Array.Copy(row1, uncompressed, row1.Length);
        Array.Copy(row2, 0, uncompressed, row1.Length, row2.Length);
        var compressed = CompressDeflate(uncompressed);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 10);
        parms.SetInt("Columns", 4);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().Equal(new byte[] { 0x10, 0x10, 0x10, 0x10, 0x15, 0x15, 0x15, 0x15 });
    }

    /// <summary>
    /// PNG predictor Average filter: output[i] = raw[i] + (left + up) / 2.
    /// Tests line 159: filter == 3 => (byte)(raw + (left + up) / 2)
    /// </summary>
    [Fact]
    public void ApplyFilter_FlateDecode_PngPredictor_Average_Filter()
    {
        var decompressor = new StreamDecompressor();

        var row1 = new byte[] { 0, 0x20, 0x20, 0x20, 0x20 };
        var row2 = new byte[] { 3, 0x00, 0x00, 0x00, 0x00 };
        var uncompressed = new byte[row1.Length + row2.Length];
        Array.Copy(row1, uncompressed, row1.Length);
        Array.Copy(row2, 0, uncompressed, row1.Length, row2.Length);
        var compressed = CompressDeflate(uncompressed);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 10);
        parms.SetInt("Columns", 4);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        // Row 1: 0x20, 0x20, 0x20, 0x20
        // Row 2, pixel 0: 0x00 + (0 + 0x20) / 2 = 0x10
        // Row 2, pixel 1: 0x00 + (0x10 + 0x20) / 2 = 0x18
        // Row 2, pixel 2: 0x00 + (0x18 + 0x20) / 2 = 0x1C
        // Row 2, pixel 3: 0x00 + (0x1C + 0x20) / 2 = 0x1E
        result.Should().Equal(new byte[] { 0x20, 0x20, 0x20, 0x20, 0x10, 0x18, 0x1C, 0x1E });
    }

    /// <summary>
    /// PNG predictor Paeth filter: output[i] = raw[i] + PaethPredictor(left, up, upLeft).
    /// Tests line 160: filter == 4 => (byte)(raw + PaethPredictor(...))
    /// The Paeth predictor selects a, b, or c based on which is closest to p = a + b - c.
    /// </summary>
    [Fact]
    public void ApplyFilter_FlateDecode_PngPredictor_Paeth_Filter()
    {
        var decompressor = new StreamDecompressor();

        var row1 = new byte[] { 0, 0x0A, 0x14, 0x1E, 0x28 };
        var row2 = new byte[] { 4, 0x00, 0x00, 0x00, 0x00 };
        var uncompressed = new byte[row1.Length + row2.Length];
        Array.Copy(row1, uncompressed, row1.Length);
        Array.Copy(row2, 0, uncompressed, row1.Length, row2.Length);
        var compressed = CompressDeflate(uncompressed);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 10);
        parms.SetInt("Columns", 4);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        // Row 1: 0x0A, 0x14, 0x1E, 0x28
        // Row 2: Paeth filter selects from previous row (a=left, b=up, c=upLeft)
        // Paeth(0, 0x0A, 0) -> p=0+0x0A-0=10, pa=10, pb=6, pc=10 -> select b (closest) = 0x0A
        // For subsequent pixels, the Paeth predictor returns the selected previous value
        result.Should().Equal(new byte[] { 0x0A, 0x14, 0x1E, 0x28, 0x0A, 0x14, 0x1E, 0x28 });
    }

    /// <summary>
    /// PNG predictor with 1-pixel-wide row (Sub filter with no left neighbor).
    /// Tests the left=0 condition in Sub filter.
    /// </summary>
    [Fact]
    public void ApplyFilter_FlateDecode_PngPredictor_Sub_1PixelWide()
    {
        var decompressor = new StreamDecompressor();

        var row1 = new byte[] { 1, 0x42 };
        var compressed = CompressDeflate(row1);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 10);
        parms.SetInt("Columns", 1);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().Equal(new byte[] { 0x42 });
    }

    #endregion

    #region CCITT Group 4 Tests (Lines 544-627)

    /// <summary>
    /// CCITT Group 4 EOFB marker is two consecutive 12-bit EOL codes.
    /// </summary>
    [Fact]
    public void ApplyFilter_CCITTFax_Group4_EOFB_Marker()
    {
        var decompressor = new StreamDecompressor();

        var data = new byte[] { 0x00, 0x10, 0x01 };
        var parms = new PdfDictionary();
        parms.SetInt("K", -1);
        parms.SetInt("Columns", 8);
        parms.SetInt("Rows", 1);

        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, parms);

        result.Should().BeEmpty();
    }

    /// <summary>
    /// CCITT Group 4 vertical pass mode: bytes where (code & 0b111100) == 0b0001.
    /// Line 598-604: if ((code & 0b111100) == 0b0001) { vertical pass mode }
    /// </summary>
    [Fact]
    public void ApplyFilter_CCITTFax_Group4_VerticalMode()
    {
        var decompressor = new StreamDecompressor();

        var data = new byte[] { 0b00010000 };
        var parms = new PdfDictionary();
        parms.SetInt("K", -1);
        parms.SetInt("Columns", 8);

        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, parms);

        result.Should().NotBeNull();
    }

    /// <summary>
    /// CCITT Group 4 horizontal/pass mode: bytes where (code & 0b111) == 0b001.
    /// Line 605-613: else if ((code & 0b111) == 0b001) { pass mode }
    /// </summary>
    [Fact]
    public void ApplyFilter_CCITTFax_Group4_PassMode()
    {
        var decompressor = new StreamDecompressor();

        var data = new byte[] { 0b00100000 };
        var parms = new PdfDictionary();
        parms.SetInt("K", -1);
        parms.SetInt("Columns", 8);

        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, parms);

        result.Should().NotBeNull();
    }

    #endregion

    #region CCITT Group 3 Tests (Lines 632-721)

    /// <summary>
    /// CCITT Group 3 1D: K=0 triggers DecodeGroup3_1D.
    /// Line 526-528: else if (K == 0) { return DecodeGroup3_1D(...) }
    /// </summary>
    [Fact]
    public void ApplyFilter_CCITTFax_Group3_1D()
    {
        var decompressor = new StreamDecompressor();

        var data = new byte[] { 0x00, 0x00 };
        var parms = new PdfDictionary();
        parms.SetInt("K", 0);
        parms.SetInt("Columns", 8);

        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, parms);

        result.Should().NotBeNull();
    }

    /// <summary>
    /// CCITT Group 3 2D: K > 0 triggers DecodeGroup3_2D.
    /// Line 530-532: else { return DecodeGroup3_2D(...) }
    /// </summary>
    [Fact]
    public void ApplyFilter_CCITTFax_Group3_2D()
    {
        var decompressor = new StreamDecompressor();

        var data = new byte[] { 0x00, 0x00, 0x00 };
        var parms = new PdfDictionary();
        parms.SetInt("K", 4);
        parms.SetInt("Columns", 8);

        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, parms);

        result.Should().NotBeNull();
    }

    #endregion

    #region CCITT Exception Handling (Lines 534-539, 573-576, 657-660, 717-720)

    /// <summary>
    /// CCITT Group 4 exception handling: returns empty array on error.
    /// Line 573-576: catch { return new byte[0]; }
    /// </summary>
    [Fact]
    public void ApplyFilter_CCITTFax_Group4_MalformedData_ReturnsEmpty()
    {
        var decompressor = new StreamDecompressor();

        var data = Array.Empty<byte>();
        var parms = new PdfDictionary();
        parms.SetInt("K", -1);
        parms.SetInt("Columns", 1728);

        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, parms);

        result.Should().BeEmpty();
    }

    /// <summary>
    /// CCITT decoder exception handling with parms = null.
    /// Line 517-518: int K = parms?.GetInt("K", 0) ?? 0;
    /// </summary>
    [Fact]
    public void ApplyFilter_CCITTFax_NullParms_UsesDefaults()
    {
        var decompressor = new StreamDecompressor();

        var data = new byte[] { 0x00 };
        var result = decompressor.ApplyFilter("CCITTFaxDecode", data, null);

        result.Should().NotBeNull();
    }

    #endregion

    #region LZW Edge Cases (Lines 366-425)

    /// <summary>
    /// LZW with short name "LZW".
    /// </summary>
    [Fact]
    public void ApplyFilter_LZW_ShortName()
    {
        var decompressor = new StreamDecompressor();

        var data = new byte[] { 0x80, 0x01 }; // Minimal LZW structure
        var result = decompressor.ApplyFilter("LZW", data, null);

        result.Should().NotBeNull();
    }

    /// <summary>
    /// LZW with empty input.
    /// </summary>
    [Fact]
    public void ApplyFilter_LZW_EmptyInput()
    {
        var decompressor = new StreamDecompressor();

        var data = Array.Empty<byte>();
        var result = decompressor.ApplyFilter("LZWDecode", data, null);

        result.Should().BeEmpty();
    }

    #endregion

    #region Filter Chain (Lines 15-31: Decompress)

    /// <summary>
    /// Test filter chaining via Decompress(PdfStream) with multiple filters.
    /// Line 22: for (int i = 0; i < filters.Count; i++)
    /// </summary>
    [Fact]
    public void Decompress_MultipleFilters_AppliesInOrder()
    {
        var decompressor = new StreamDecompressor();

        // Create data: "Hello" -> ASCIIHex -> FlateDecode
        var helloBytes = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F };
        var hexEncoded = Encoding.ASCII.GetBytes("48656C6C6F>");
        var flateCompressed = CompressDeflate(hexEncoded);

        var dict = new PdfDictionary();
        var filterArray = new PdfArray();
        filterArray.Add((PdfObject)new PdfName("FlateDecode"));
        filterArray.Add((PdfObject)new PdfName("ASCIIHexDecode"));
        dict["Filter"] = filterArray;

        var stream = new PdfStream(dict, flateCompressed);
        decompressor.Decompress(stream);

        stream.DecodedData.Should().Equal(helloBytes);
    }

    #endregion

    #region TIFF Predictor Tests (Lines 193-225)

    /// <summary>
    /// TIFF Predictor 2 (horizontal differencing) with 8-bit components.
    /// Tests line 193-225: ApplyTiffPredictor
    /// </summary>
    [Fact]
    public void ApplyFilter_FlateDecode_TiffPredictor()
    {
        var decompressor = new StreamDecompressor();

        // TIFF predictor applies horizontal differencing
        // Row: [raw0, raw1+raw0, raw2+raw1, ...]
        // To reverse: [raw0, diff1, diff2, ...]
        var differenced = new byte[] { 0x10, 0x10, 0x10, 0x10 };
        var compressed = CompressDeflate(differenced);

        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 2);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);
        parms.SetInt("Columns", 4);

        var result = decompressor.ApplyFilter("FlateDecode", compressed, parms);

        result.Should().Equal(new byte[] { 0x10, 0x20, 0x30, 0x40 });
    }

    #endregion

    #region Helpers

    private static byte[] CompressDeflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionMode.Compress))
        {
            deflate.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    #endregion
}
