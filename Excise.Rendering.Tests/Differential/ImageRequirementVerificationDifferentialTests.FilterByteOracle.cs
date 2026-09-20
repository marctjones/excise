using System;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// PARSE-mode independent-oracle evidence for the image-requirements.json
/// stream filters: excise's DECODED BYTES against qpdf's, with no rasteriser
/// in between.
///
/// WHY THIS EXISTS ALONGSIDE THE RENDER-MODE SIBLING. The other half of this
/// class already compares excise's raster against mutool's for the same
/// fixtures, which is strong evidence — but it is evidence about pixels, and
/// it passes through Skia's scan conversion, a tolerance, and a colour
/// pipeline. A decoder that is wrong in the low bits of a gradient, or that
/// drops a trailing byte, can survive a tolerant raster comparison. The
/// remaining parse-mode evidence was <c>StreamFilterCoverageTests</c>, which
/// encodes a fixture with .NET's zlib (or excise's own writer) and decodes it
/// with excise — CLAUDE.md's no-self-oracle rule says that proves only that
/// excise's two halves agree.
///
/// THE ASSERTION ORDER IS LOAD-BEARING, and it is the rule the sibling file's
/// header states: the property is asserted about the ORACLE FIRST. Each test
/// checks that qpdf decodes the fixture to the plaintext the fixture claims to
/// encode, and only then that excise agrees with qpdf. A fixture that does not
/// actually exercise its filter therefore cannot pass silently — it fails on
/// the oracle line, before excise is consulted at all. The plaintexts are
/// derived from the filter's published algorithm by a third implementation
/// (Python), so neither side of the excise/qpdf comparison authored them.
///
/// ⚠️ SCOPE — measured against qpdf 12.3.2, not assumed. qpdf decodes the
/// GENERALIZED filters (§7.4.2–§7.4.5) and <c>/Crypt /Identity</c>. It
/// REFUSES CCITTFaxDecode, JBIG2Decode and JPXDecode, so requirements 010,
/// 011, 014, 015 and 016 get no parse evidence here and keep the render-mode
/// mutool comparison as their only independent check.
/// DCTDecode (012/013) is excluded for a different and more interesting
/// reason: qpdf DOES decode it, but excise's <c>StreamDecompressor</c> treats
/// DCTDecode as a <c>PassThroughFilterDecoder</c> — the JPEG is decoded at the
/// image layer, not the stream layer — so excise's <c>DecodedData</c> for such
/// a stream is the JPEG codestream and qpdf's is a raster. The two are not
/// comparable, and pretending otherwise would be a fixture that measures
/// nothing.
/// </summary>
public partial class ImageRequirementVerificationDifferentialTests
{
    // ── stream-filter:ASCIIHexDecode (pdf.20.image.requirement-001) ────────

    [Fact]
    public void AsciiHexStream_DecodesToTheSameBytesQpdfDoes()
        => AssertFilterDecodesTo(
            Encoding.ASCII.GetBytes("FF0000FF0000FF0000FF0000>"),
            "/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /ASCIIHexDecode",
            Repeat(new byte[] { 0xFF, 0x00, 0x00 }, 4),
            "ASCIIHexDecode (§7.4.2) maps each pair of hex digits to one byte and stops at '>'");

    // ── stream-filter:ASCII85Decode (pdf.20.image.requirement-002) ─────────

    [Fact]
    public void Ascii85Stream_DecodesToTheSameBytesQpdfDoes()
        => AssertFilterDecodesTo(
            Encoding.ASCII.GetBytes("rr<'!!!*$!!<3$!~>"),
            "/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /ASCII85Decode",
            Repeat(new byte[] { 0xFF, 0x00, 0x00 }, 4),
            "ASCII85Decode (§7.4.3) maps each group of five base-85 digits to four bytes, " +
            "with a short final group and the '~>' EOD");

    // ── stream-filter:FlateDecode (pdf.20.image.requirement-003) ───────────

    [Fact]
    public void PlainFlateStream_DecodesToTheSameBytesQpdfDoes()
    {
        byte[] plaintext = { 0, 40, 80, 120, 160, 200 };
        AssertFilterDecodesTo(
            ZlibCompress(plaintext),
            "/Width 6 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode",
            plaintext,
            "FlateDecode (§7.4.4) must recover the exact pre-compression bytes");
    }

    // ── stream-filter:FlateDecode.predictor-tiff (requirement-004) ─────────

    /// <summary>
    /// The same Pillow/libtiff strip bytes the render-mode sibling uses. The
    /// expected plaintext is the horizontal-differencing rule of §7.4.4.4
    /// applied by hand: each row starts at 0/15/30 and steps by 25.
    /// </summary>
    [Fact]
    public void TiffPredictorFlateStream_DecodesToTheSameBytesQpdfDoes()
        => AssertFilterDecodesTo(
            Convert.FromHexString("789c639004017e30290726010ea901a5"),
            "/Width 6 /Height 3 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode " +
            "/DecodeParms << /Predictor 2 /Colors 1 /BitsPerComponent 8 /Columns 6 >>",
            new byte[]
            {
                0, 25, 50, 75, 100, 125,
                15, 40, 65, 90, 115, 140,
                30, 55, 80, 105, 130, 155,
            },
            "a TIFF Predictor 2 must undo horizontal differencing after inflating, " +
            "and must NOT leave the raw deltas (25,25,25,…) in place");

    // ── stream-filter:FlateDecode.predictor-png (requirement-005) ──────────

    /// <summary>
    /// Rows 2 and 3 use PNG filter type 2 (Up), row 1 type 1 (Sub) — so a
    /// decoder that ignores the per-scanline filter byte, or that applies one
    /// rule to every row, produces different bytes rather than merely
    /// shifted ones.
    /// </summary>
    [Fact]
    public void PngPredictorFlateStream_DecodesToTheSameBytesQpdfDoes()
        => AssertFilterDecodesTo(
            Convert.FromHexString("789c6364d00001262e3080520010e40146"),
            "/Width 6 /Height 3 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode " +
            "/DecodeParms << /Predictor 15 /Colors 1 /BitsPerComponent 8 /Columns 6 >>",
            new byte[]
            {
                0, 40, 80, 120, 160, 200,
                10, 50, 90, 130, 170, 210,
                20, 60, 100, 140, 180, 220,
            },
            "a PNG predictor must consume the leading filter-type byte of every scanline " +
            "and apply that row's rule; the decoded output carries no filter-type bytes");

    // ── stream-filter:LZWDecode (requirement-006) ───────────────────────────

    [Fact]
    public void PlainLzwStream_DecodesToTheSameBytesQpdfDoes()
        => AssertFilterDecodesTo(
            Convert.FromHexString("8000010100c08050301c10090502c180d0703c20111010"),
            "/Width 6 /Height 3 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /LZWDecode",
            new byte[]
            {
                0, 8, 16, 24, 32, 40,
                48, 56, 64, 72, 80, 88,
                96, 104, 112, 120, 128, 136,
            },
            "LZWDecode (§7.4.4.2) with no predictor must recover the 18-byte ramp");

    // ── stream-filter:LZWDecode.predictors (requirement-008) ───────────────

    [Fact]
    public void LzwWithTiffPredictorStream_DecodesToTheSameBytesQpdfDoes()
        => AssertFilterDecodesTo(
            Convert.FromHexString("800003d03820780b058202a110307c2c3d01"),
            "/Width 8 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /LZWDecode " +
            "/DecodeParms << /Predictor 2 /Colors 1 /BitsPerComponent 8 /Columns 8 >>",
            new byte[]
            {
                0, 30, 60, 90, 120, 150, 180, 210,
                5, 35, 65, 95, 125, 155, 185, 215,
                10, 40, 70, 100, 130, 160, 190, 220,
                15, 45, 75, 105, 135, 165, 195, 225,
            },
            "LZW followed by a TIFF predictor must apply BOTH stages, in that order");

    // ── stream-filter:RunLengthDecode (requirement-009) ─────────────────────

    [Fact]
    public void RunLengthStream_DecodesToTheSameBytesQpdfDoes()
        => AssertFilterDecodesTo(
            new byte[] { 0xF1, 0xC8, 0x80 },
            "/Width 4 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /RunLengthDecode",
            Enumerable.Repeat((byte)0xC8, 16).ToArray(),
            "a length byte of 241 means 257-241 = 16 copies of the next byte (§7.4.5), " +
            "and 128 terminates");

    // ── stream-filter:Crypt (requirement-017) ───────────────────────────────

    [Fact]
    public void CryptIdentityStream_DecodesToTheSameBytesQpdfDoes()
    {
        var plaintext = Repeat(new byte[] { 0x00, 0x00, 0xFF }, 4);
        AssertFilterDecodesTo(
            plaintext,
            "/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /Crypt " +
            "/DecodeParms << /Name /Identity >>",
            plaintext,
            "/Crypt with /Name /Identity (§7.4.10) is a pass-through: the bytes must come " +
            "back unchanged, and must not be treated as an unknown filter and dropped");
    }

    // ── stream-filter-arrays (requirement-018) ──────────────────────────────

    [Fact]
    public void FilterChainStream_DecodesToTheSameBytesQpdfDoes()
        => AssertFilterDecodesTo(
            Encoding.ASCII.GetBytes("GhW&@_!iG#!$1J0r;~>"),
            "/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 " +
            "/Filter [/ASCII85Decode /FlateDecode]",
            Repeat(new byte[] { 0xFF, 0x00, 0x00 }, 4),
            "a /Filter array must be applied left to right (§7.4): ASCII85 first, then Flate");

    // ── stream-filter:LZWDecode.early-change (requirement-007) ──────────────

    /// <summary>
    /// A REAL 2700-byte LZW stream from the Isartor suite, not a synthetic
    /// one. <c>LzwEarlyChangeTests</c> explains at length why: an encoder
    /// written by the hand that just changed the decoder is not independent
    /// evidence, and encoder and decoder hold different next-code values at
    /// the same instant — which is the very thing <c>/EarlyChange</c>
    /// reconciles, so getting the encoder's width rule wrong fails the test
    /// for reasons that have nothing to do with the product. (Confirmed the
    /// hard way while writing this file: a hand-rolled encoder produced
    /// streams qpdf read correctly only under the OPPOSITE /EarlyChange to
    /// the one they were written for.)
    ///
    /// That existing test asserts only that excise's decode reaches the full
    /// 5805 bytes. This adds what it could not: the CONTENT, judged by a
    /// decoder that is not excise. A width rule that desynchronises late —
    /// after the length check is already satisfied — is invisible to a length
    /// assertion and visible here.
    /// </summary>
    [Fact]
    public void RealEarlyChangeLzwImage_DecodesToTheSameBytesQpdfDoes()
    {
        const string fixture =
            "test-pdfs/isartor/Isartor testsuite/PDFA-1b/6.1 File structure/6.1.10 Filters/" +
            "isartor-6-1-10-t01-fail-a.pdf";
        var path = TestRepoLayout.FindFile(fixture);
        Assert.SkipWhen(path == null,
            TestRepoLayout.AbsenceReason("gitignored Isartor corpus (scripts/download-test-pdfs.sh)", fixture));

        var oracle = RequireQpdfDecode(path!, LzwImageObjectNumber);
        oracle.Length.Should().Be(215 * 27,
            "the fixture's image dictionary declares 215x27 at 8 bpc, so a complete decode is " +
            "5805 bytes — an independent check on the LENGTH that does not come from either decoder");

        using var doc = PdfDocument.Open(path!);
        var stream = doc.GetObject(LzwImageObjectNumber) as PdfStream;
        stream.Should().NotBeNull($"object {LzwImageObjectNumber} of the fixture is the /LZWDecode image");

        stream!.DecodedData.Should().Equal(oracle,
            "excise's /EarlyChange handling must recover the same 5805 bytes qpdf's independent " +
            "LZW decoder does — a width rule that desynchronises after the length is already " +
            "reached passes a length assertion and fails here");
    }

    /// <summary>
    /// The /LZWDecode image XObject in the Isartor fixture, located by hand
    /// once (qpdf reports 5805 filtered bytes for it, which is 215x27) rather
    /// than searched for at run time: a search that silently landed on a
    /// different stream would make this test assert something else and still
    /// look green.
    /// </summary>
    private const int LzwImageObjectNumber = 6;

    // ── The oracle's own scope, asserted rather than described ─────────────

    /// <summary>
    /// The three filters qpdf will not decode must report
    /// <see cref="QpdfStreamDataStatus.Refused"/> — not silently produce empty
    /// bytes, and not look like "qpdf is missing".
    ///
    /// This is a #1527 guard on the oracle itself. The scope note in this
    /// file's header is a claim about a tool on someone's PATH; if a future
    /// qpdf gains a JPX decoder, or a future refactor collapses Refused back
    /// into a skip, the claim becomes false and every test above could start
    /// passing vacuously. Here it is executable: the day it stops being true,
    /// this fails and the header gets corrected.
    /// </summary>
    [Theory]
    [InlineData("/Filter /CCITTFaxDecode /DecodeParms << /K -1 /Columns 8 /Rows 8 >>")]
    [InlineData("/Filter /JBIG2Decode")]
    [InlineData("/Filter /JPXDecode")]
    public void QpdfRefusesTheImageOnlyFilters_AndSaysSoRatherThanSkipping(string filterEntry)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var pdf = BuildImagePdf(8, 8, "8 0 0 8 0 0",
            $"/Width 8 /Height 8 /ColorSpace /DeviceGray /BitsPerComponent 1 {filterEntry}",
            new byte[] { 0x00, 0x01, 0x02, 0x03 });
        var path = WriteTempPdf(pdf);
        try
        {
            var result = QpdfReferenceTool.FilteredStreamData(path, ImageObjectNumber);
            result.Status.Should().Be(QpdfStreamDataStatus.Refused,
                "qpdf 12.3.2 has no decoder for this filter, and the wrapper must report that " +
                "distinctly from 'qpdf is not installed' — collapsing the two is how a check " +
                "that cannot run comes to look like a check that passed");
        }
        finally { TryDelete(path); }
    }

    /// <summary>
    /// A decoded stream whose bytes happen to be CR, LF and CRLF must survive
    /// the subprocess boundary unchanged.
    ///
    /// This is the specific defect <c>RunBinary</c> exists to avoid, and it is
    /// invisible to every other test here because the image fixtures above
    /// contain no 0x0D. The line-based pump the tool's text methods share
    /// would rewrite these bytes to the platform newline and silently change
    /// the stream's length — turning the oracle into a corrupting one, which
    /// is worse than no oracle.
    /// </summary>
    [Fact]
    public void QpdfBinaryCapture_PreservesCrLfBytesInDecodedData()
    {
        byte[] plaintext = { 0x0D, 0x0A, 0x0D, 0x0D, 0x0A, 0x0A, 0x00, 0x0D, 0xFF };
        AssertFilterDecodesTo(
            ZlibCompress(plaintext),
            $"/Width {plaintext.Length} /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode",
            plaintext,
            "CR/LF/CRLF and NUL bytes must cross the qpdf subprocess boundary byte-for-byte");
    }

    // ── shared machinery ───────────────────────────────────────────────────

    /// <summary>The image XObject <see cref="BuildImagePdf"/> always writes as object 5.</summary>
    private const int ImageObjectNumber = 5;

    /// <summary>
    /// Asserts, IN THIS ORDER: qpdf decodes the fixture at all; qpdf's bytes
    /// are the plaintext the fixture claims to encode; excise's bytes are
    /// qpdf's.
    ///
    /// The middle assertion is what stops a fixture that does not exercise its
    /// filter from passing. Without it, a stream that excise and qpdf both
    /// mishandle identically — or a fixture whose /Filter entry is simply
    /// ignored by both — would compare equal and read as evidence.
    /// </summary>
    private static void AssertFilterDecodesTo(
        byte[] encoded, string imageDictExtra, byte[] expectedPlaintext, string because)
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");

        var path = WriteTempPdf(BuildImagePdf(8, 8, "8 0 0 8 0 0", imageDictExtra, encoded));
        try
        {
            var oracle = RequireQpdfDecode(path, ImageObjectNumber);
            oracle.Should().Equal(expectedPlaintext,
                $"qpdf's independent filter chain must recover the documented plaintext — {because}");

            using var doc = PdfDocument.Open(path);
            var stream = doc.GetObject(ImageObjectNumber) as PdfStream;
            stream.Should().NotBeNull($"object {ImageObjectNumber} is the image XObject");

            stream!.DecodedData.Should().Equal(oracle,
                "excise's decode must agree with qpdf's byte for byte; the two share no code, " +
                $"so agreement is evidence about the FILTER rather than about excise — {because}");
        }
        finally { TryDelete(path); }
    }

    /// <summary>
    /// qpdf's decoded bytes, or a FAILURE when qpdf ran and would not decode.
    /// A refusal is a fixture-design error (the oracle was asked a question it
    /// cannot answer) and must never degrade into a skip — see #1527.
    /// </summary>
    private static byte[] RequireQpdfDecode(string path, int objectNumber)
    {
        var result = QpdfReferenceTool.FilteredStreamData(path, objectNumber);
        if (result.Status == QpdfStreamDataStatus.ToolUnavailable)
            Assert.Skip($"qpdf unavailable: {result.Diagnostics}");

        result.Status.Should().Be(QpdfStreamDataStatus.Ok,
            $"qpdf must be able to decode object {objectNumber} of this fixture for the " +
            $"comparison to mean anything — {result.Diagnostics}");
        return result.Bytes;
    }
}
