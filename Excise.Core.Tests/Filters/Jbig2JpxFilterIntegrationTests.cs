using AwesomeAssertions;
using Excise.Core.Filters;
using Excise.Core.Parsing;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Filters;

/// <summary>
/// Integration tests for wiring JBIG2Decode/JPXDecode into StreamDecompressor (#325).
///
/// ⚠️ The contract these tests pin CHANGED in #1396, and the old one was the
/// defect. "Unsupported image data is left as the raw encoded bytes" is safe
/// only for a filter whose codestream something downstream still understands —
/// DCTDecode (a registered pass-through) and JPXDecode (decoded at the image
/// layer, see SkiaRenderer.Images.GetTerminalJpxData). For JBIG2 nothing
/// downstream recognises the codestream, so the bytes reached the rasteriser as
/// one-bit image SAMPLES and were painted as noise. JBIG2 now REFUSES an
/// attempted decode it cannot finish; JPX keeps its pass-through, deliberately.
/// </summary>
public class Jbig2JpxFilterIntegrationTests
{
    private static byte[] BuildJbig2Segment(uint segmentNumber, byte segmentType, uint pageNumber = 1, uint dataLength = 0)
    {
        return new[]
        {
            (byte)(segmentNumber >> 24),
            (byte)(segmentNumber >> 16),
            (byte)(segmentNumber >> 8),
            (byte)segmentNumber,
            segmentType,
            (byte)0,
            (byte)pageNumber,
            (byte)(dataLength >> 24),
            (byte)(dataLength >> 16),
            (byte)(dataLength >> 8),
            (byte)dataLength,
        };
    }

    private static byte[] BuildJbig2Segment(uint segmentNumber, byte segmentType, byte[] segmentData, uint pageNumber = 1)
    {
        byte[] header = BuildJbig2Segment(segmentNumber, segmentType, pageNumber, (uint)segmentData.Length);
        byte[] result = new byte[header.Length + segmentData.Length];
        Array.Copy(header, 0, result, 0, header.Length);
        Array.Copy(segmentData, 0, result, header.Length, segmentData.Length);
        return result;
    }

    private static byte[] BuildGenericRegionBody(uint width, uint height, byte regionFlags, byte genericRegionFlags, params byte[] bitmapData)
    {
        var body = new List<byte>
        {
            (byte)(width >> 24),
            (byte)(width >> 16),
            (byte)(width >> 8),
            (byte)width,
            (byte)(height >> 24),
            (byte)(height >> 16),
            (byte)(height >> 8),
            (byte)height,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            regionFlags,
            genericRegionFlags,
        };
        body.AddRange(bitmapData);
        return body.ToArray();
    }

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

    /// <summary>
    /// ⚠️ This test asserted the OPPOSITE until #1396, and the old assertion —
    /// "unsupported JBIG2 must pass through unchanged" — was the defect written
    /// down as a contract.
    ///
    /// <para>Passing the bytes through is not a neutral no-op for a filter whose
    /// output is image SAMPLES. The stream was marked decoded, and the image
    /// path rasterised the still-compressed JBIG2 codestream as one-bit pixels:
    /// visual noise presented as page content, with no diagnostic. On pdfium's
    /// bug_867501.pdf that painted 184,589 of 185,724 pixels black.</para>
    ///
    /// <para>#878's guard caught only the extreme shape — it refuses a buffer
    /// supplying under half the samples the image needs. A codestream that
    /// compresses poorly, or a small image, clears that bar and still gets
    /// painted, which is why the guess is replaced by a refusal here.</para>
    /// </summary>
    [Fact]
    public void Jbig2_AttemptedDecodeThatFails_RefusesInsteadOfReturningItsInput()
    {
        byte[] raw = BuildJbig2Segment(1, 63);
        var stream = MakeImage("JBIG2Decode", raw, 8, 8);

        var decode = () => new StreamDecompressor().Decompress(stream);

        var thrown = decode.Should().Throw<Exception>(
            "a decoder that tried and failed must FAIL — handing back the encoded " +
            "bytes makes them image samples, and they get painted").Which;
        thrown.Message.Should().Contain("JBIG2Decode",
            "the report has to name which filter refused");
        stream.IsDecoded.Should().BeFalse(
            "a refused stream must not read back as decoded, or the codestream is " +
            "still available to be painted as samples");
    }

    /// <summary>
    /// The two causes are reported distinctly because only one of them is
    /// excise's bug: an unimplemented-but-legal JBIG2 feature is a gap worth a
    /// bug report, corrupt data is the file's problem (#1396).
    /// </summary>
    [Fact]
    public void FilterRefusal_DistinguishesAnUnimplementedFeatureFromCorruptInput()
    {
        var ourGap = new PdfFilterDecodeException(
            "JBIG2Decode", PdfFilterDecodeFailureKind.Unimplemented, "symbol dictionary context retention");
        var theirFile = new PdfFilterDecodeException(
            "JBIG2Decode", PdfFilterDecodeFailureKind.CorruptInput, "truncated page information segment");

        ourGap.Message.Should().Contain("JBIG2Decode")
            .And.Contain("unimplemented feature")
            .And.Contain("symbol dictionary context retention");
        theirFile.Message.Should().Contain("JBIG2Decode")
            .And.Contain("corrupt or non-conforming data")
            .And.Contain("truncated page information segment");
        ourGap.Message.Should().NotBe(theirFile.Message,
            "a reader has to be able to tell excise's gap from the file's defect");
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
    public void Jpx_MalformedData_FallsBackToRawBytes()
    {
        byte[] raw = { 0x6E, 0x6F, 0x74, 0x6A, 0x70, 0x78 };
        var stream = MakeImage("JPXDecode", raw, 16, 16);

        new StreamDecompressor().Decompress(stream);

        stream.DecodedData.Should().Equal(raw, "malformed JPX data is a known codec fallback, not a dispatcher failure");
    }

    /// <summary>
    /// The refusal added in #1396 is narrow: a decode that SUCCEEDS still
    /// produces samples, and the sample count is the page bitmap's, never the
    /// codestream's length. This is the guard against over-correcting the
    /// pass-through fix into "JBIG2 never decodes".
    /// </summary>
    [Fact]
    public void Jbig2_DecodableStream_StillProducesPageSizedSamples()
    {
        var stream = MakeImage("JBIG2Decode", new byte[] { 0xFF, 0xAC, 0x01 }, 4, 4);

        new StreamDecompressor().Decompress(stream);

        stream.IsDecoded.Should().BeTrue();
        stream.DecodedData.Length.Should().Be(4,
            "a 4x4 one-bit image is one byte per row — a length equal to the " +
            "ENCODED stream would mean the codestream had been passed through " +
            "as samples again (#1396)");
    }

    [Fact]
    public void Jbig2_UsesDirectDecodeParmsGlobalsForFallbackDecision()
    {
        byte[] globals = BuildJbig2Segment(1, 0, pageNumber: 0);
        byte[] raw = Array.Empty<byte>();
        var stream = MakeImage("JBIG2Decode", raw, 8, 8);
        var parms = new PdfDictionary();
        parms["JBIG2Globals"] = new PdfStream(globals);
        stream["DecodeParms"] = parms;

        var act = () => new StreamDecompressor().Decompress(stream);

        act.Should().Throw<PdfFilterDecodeException>(
            "unsupported global JBIG2 segments used to pass the image bytes through " +
            "as samples, and those bytes were painted (#1396)");
    }

    [Fact]
    public void Jbig2_UnsupportedGenericRegionMode_Refuses()
    {
        byte[] segmentData = BuildGenericRegionBody(width: 1, height: 1, regionFlags: 0, genericRegionFlags: 0x02, 0x00);
        byte[] raw = BuildJbig2Segment(1, 38, segmentData);
        var stream = MakeImage("JBIG2Decode", raw, 1, 1);

        var act = () => new StreamDecompressor().Decompress(stream);

        act.Should().Throw<PdfFilterDecodeException>(
            "an unsupported arithmetic template mode is a decode excise ATTEMPTED " +
            "and could not finish; passing the codestream through made it samples (#1396)");
    }
}
