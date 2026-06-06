using AwesomeAssertions;
using Pdfe.Core.Parsing;
using Pdfe.Core.Primitives;
using Xunit;

namespace Pdfe.Core.Tests.Filters;

/// <summary>
/// Integration tests for wiring JBIG2Decode/JPXDecode into StreamDecompressor (#325).
/// Verifies the safe-fallback contract: unsupported/incomplete image data is left
/// as the raw encoded bytes rather than producing a crash or a silently-wrong image.
/// </summary>
public class Jbig2JpxFilterIntegrationTests
{
    private static PdfStream MakeImage(string filter, byte[] data, int width, int height)
    {
        var dict = new PdfDictionary();
        dict.SetName("Type", "XObject");
        dict.SetName("Subtype", "Image");
        dict.SetName("Filter", filter);
        dict.SetInt("Width", width);
        dict.SetInt("Height", height);
        dict.SetInt("Length", data.Length);
        return new PdfStream(dict, data);
    }

    [Fact]
    public void Jbig2_UnsupportedSegment_FallsBackToRawBytes()
    {
        // Crafted bytes that parse as a symbol-dictionary segment (unsupported).
        byte[] raw = { 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        var stream = MakeImage("JBIG2Decode", raw, 8, 8);

        new StreamDecompressor().Decompress(stream);

        stream.DecodedData.Should().Equal(raw, "unsupported JBIG2 must pass through unchanged");
    }

    [Fact]
    public void Jbig2_NoDimensions_FallsBackToRawBytes()
    {
        byte[] raw = { 0x01, 0x02, 0x03 };
        var dict = new PdfDictionary();
        dict.SetName("Filter", "JBIG2Decode");
        var stream = new PdfStream(dict, raw); // no /Width /Height
        new StreamDecompressor().Decompress(stream);
        stream.DecodedData.Should().Equal(raw);
    }

    [Fact]
    public void Jpx_FallsBackToRawCodestream()
    {
        // JPEG2000 pixel decode isn't implemented → raw codestream passes through.
        byte[] raw = { 0x00, 0x00, 0x00, 0x0C, (byte)'j', (byte)'P', 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A };
        var stream = MakeImage("JPXDecode", raw, 16, 16);
        new StreamDecompressor().Decompress(stream);
        stream.DecodedData.Should().Equal(raw);
    }

    [Fact]
    public void Jbig2_DoesNotThrowFromDecompress()
    {
        var stream = MakeImage("JBIG2Decode", new byte[] { 0xFF, 0xAC, 0x01 }, 4, 4);
        var act = () => new StreamDecompressor().Decompress(stream);
        act.Should().NotThrow();
    }
}
