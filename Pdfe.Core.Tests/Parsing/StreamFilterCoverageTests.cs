using System.IO;
using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Parsing;
using Pdfe.Core.Primitives;
using Xunit;

namespace Pdfe.Core.Tests.Parsing;

/// <summary>
/// In-memory coverage for StreamDecompressor's filters and predictors (#350/#351)
/// — these are otherwise mostly exercised only by the corpus, which skips in CI.
/// </summary>
public class StreamFilterCoverageTests
{
    private static byte[] Decode(string filter, byte[] data, PdfDictionary? parms = null)
    {
        var dict = new PdfDictionary();
        dict.SetName("Filter", filter);
        if (parms != null) dict["DecodeParms"] = parms;
        dict.SetInt("Length", data.Length);
        var s = new PdfStream(dict, data);
        new StreamDecompressor().Decompress(s);
        return s.DecodedData;
    }

    private static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    [Fact]
    public void FlateDecode_RoundTrips()
    {
        var original = Encoding.ASCII.GetBytes("Hello, FlateDecode world! " + new string('x', 500));
        Decode("FlateDecode", Zlib(original)).Should().Equal(original);
        Decode("Fl", Zlib(original)).Should().Equal(original); // abbreviation
    }

    [Fact]
    public void FlateDecode_WithPngPredictor_RoundTrips()
    {
        // 3 rows x 4 columns, 1 byte/sample, PNG predictor 12 (Up) on row data.
        int columns = 4, rows = 3;
        var raw = new byte[rows * (columns + 1)];
        for (int r = 0; r < rows; r++)
        {
            raw[r * (columns + 1)] = 2; // PNG filter type "Up"
            for (int c = 0; c < columns; c++) raw[r * (columns + 1) + 1 + c] = (byte)(r == 0 ? c : 1);
        }
        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 12);
        parms.SetInt("Columns", columns);
        var outp = Decode("FlateDecode", Zlib(raw), parms);
        outp.Length.Should().Be(columns * rows);
    }

    [Fact]
    public void FlateDecode_WithTiffPredictor_RoundTrips()
    {
        int columns = 4, rows = 2;
        var raw = new byte[columns * rows];
        for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i % columns);
        var parms = new PdfDictionary();
        parms.SetInt("Predictor", 2);
        parms.SetInt("Columns", columns);
        parms.SetInt("Colors", 1);
        parms.SetInt("BitsPerComponent", 8);
        Decode("FlateDecode", Zlib(raw), parms).Length.Should().Be(raw.Length);
    }

    [Fact]
    public void ASCIIHexDecode_Works()
    {
        Decode("ASCIIHexDecode", Encoding.ASCII.GetBytes("48 65 6C 6C 6F>"))
            .Should().Equal(Encoding.ASCII.GetBytes("Hello"));
        Decode("AHx", Encoding.ASCII.GetBytes("4869>"))
            .Should().Equal(Encoding.ASCII.GetBytes("Hi"));
    }

    [Fact]
    public void RunLengthDecode_Works()
    {
        // 0x02 => copy next 3 literal bytes; 0x80 => EOD.
        var data = new byte[] { 0x02, (byte)'A', (byte)'B', (byte)'C', 0x80 };
        Decode("RunLengthDecode", data).Should().Equal(Encoding.ASCII.GetBytes("ABC"));
        // 0xFE (257-254=3) => repeat next byte 3 times.
        var rep = new byte[] { 0xFE, (byte)'Z', 0x80 };
        Decode("RL", rep).Should().Equal(Encoding.ASCII.GetBytes("ZZZ"));
    }

    [Fact]
    public void BrotliDecode_RoundTrips()
    {
        var original = Encoding.ASCII.GetBytes("brotli " + new string('y', 300));
        using var ms = new MemoryStream();
        using (var b = new BrotliStream(ms, CompressionLevel.Optimal, true)) b.Write(original, 0, original.Length);
        Decode("BrotliDecode", ms.ToArray()).Should().Equal(original);
    }

    [Fact]
    public void MultipleFilters_ChainInOrder()
    {
        // ASCIIHex then Flate: data is hex-encoded zlib of "chained".
        var inner = Zlib(Encoding.ASCII.GetBytes("chained"));
        var hex = new StringBuilder();
        foreach (var b in inner) hex.Append(b.ToString("X2"));
        hex.Append('>');
        var dict = new PdfDictionary();
        var filters = new PdfArray(); filters.Add((PdfObject)new PdfName("ASCIIHexDecode")); filters.Add((PdfObject)new PdfName("FlateDecode"));
        dict["Filter"] = filters;
        var s = new PdfStream(dict, Encoding.ASCII.GetBytes(hex.ToString()));
        new StreamDecompressor().Decompress(s);
        s.DecodedData.Should().Equal(Encoding.ASCII.GetBytes("chained"));
    }

    [Fact]
    public void MultipleFilters_UsesDecodeParmsForMatchingFilter()
    {
        // ASCIIHexDecode has no params; FlateDecode must receive the second
        // DecodeParms entry so its PNG predictor strips the per-row filter byte.
        var pngPredicted = new byte[] { 0, (byte)'O', (byte)'K' };
        var inner = Zlib(pngPredicted);
        var hex = new StringBuilder();
        foreach (var b in inner) hex.Append(b.ToString("X2"));
        hex.Append('>');

        var dict = new PdfDictionary();
        var filters = new PdfArray();
        filters.Add((PdfObject)new PdfName("ASCIIHexDecode"));
        filters.Add((PdfObject)new PdfName("FlateDecode"));
        dict["Filter"] = filters;

        var decodeParms = new PdfArray();
        decodeParms.Add((PdfObject)PdfNull.Instance);
        var flateParms = new PdfDictionary();
        flateParms.SetInt("Predictor", 10);
        flateParms.SetInt("Columns", 2);
        decodeParms.Add((PdfObject)flateParms);
        dict["DecodeParms"] = decodeParms;

        var stream = new PdfStream(dict, Encoding.ASCII.GetBytes(hex.ToString()));
        new StreamDecompressor().Decompress(stream);

        stream.DecodedData.Should().Equal(Encoding.ASCII.GetBytes("OK"));
    }
}
