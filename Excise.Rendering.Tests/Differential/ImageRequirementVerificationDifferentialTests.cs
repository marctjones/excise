using System;
using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Independent-oracle evidence for the image-requirements.json filter/mask/
/// placement capabilities that were previously verified only by excise
/// decoding its own output and excise's own parser confirming the match (see
/// CLAUDE.md's "no-self-oracle" principle — #636/#608/#637). Every fixture
/// here embeds bytes produced by a tool OTHER than excise (Python's
/// zlib/Pillow/base64, or bytes hand-derived directly from the filter's
/// published spec algorithm, verified against mutool BEFORE being pasted into
/// this file — see the prototyping trail in the commit message), and every
/// assertion compares excise's SkiaRenderer output against mutool's
/// independent render of the SAME bytes. A decoder bug that is self-consistent
/// between excise's own encoder and excise's own decoder cannot pass here,
/// because neither side of the comparison is excise's own encoder.
/// </summary>
public class ImageRequirementVerificationDifferentialTests
{
    private const int Dpi = 72;

    // ── stream-filter:ASCIIHexDecode (pdf.20.image.requirement-001) ────────

    /// <summary>
    /// "FF0000FF0000FF0000FF0000&gt;" is the literal ASCII-hex encoding (Annex
    /// A / §7.4.2) of four solid-red RGB samples, followed by the EOD marker
    /// — not excise's own hex writer, just the spec's trivial character
    /// mapping typed out by hand and confirmed against mutool.
    /// </summary>
    [Fact]
    public void AsciiHexEncodedRedImage_MatchesMutoolDecode()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var data = Encoding.ASCII.GetBytes("FF0000FF0000FF0000FF0000>");
        var pdf = BuildImagePdf(2, 2, "2 0 0 2 0 0",
            "/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /ASCIIHexDecode", data);

        AssertExciseMatchesMutool(pdf, 2, 2, tolerance: 2,
            "ASCIIHexDecode of a solid-red image must match mutool's independent decode of the same hex text");
    }

    // ── stream-filter:ASCII85Decode (pdf.20.image.requirement-002) ─────────

    [Fact]
    public void Ascii85EncodedRedImage_MatchesMutoolDecode()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var data = Encoding.ASCII.GetBytes("rr<'!!!*$!!<3$!~>");
        var pdf = BuildImagePdf(2, 2, "2 0 0 2 0 0",
            "/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /ASCII85Decode", data);

        AssertExciseMatchesMutool(pdf, 2, 2, tolerance: 2,
            "ASCII85Decode of a solid-red image must match mutool's independent decode of the same base-85 text");
    }

    // ── stream-filter:FlateDecode (pdf.20.image.requirement-003) ───────────

    /// <summary>
    /// Compressed with .NET's <see cref="System.IO.Compression.ZLibStream"/>
    /// — a zlib implementation excise's FlateDecode reader never touches on
    /// the encode side — not hand-derived like the hex/85 fixtures above,
    /// because plain deflate has no small spec-literal form worth typing.
    /// </summary>
    [Fact]
    public void PlainFlateEncodedGradient_MatchesMutoolDecode()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        byte[] raw = { 0, 40, 80, 120, 160, 200 }; // 6x1 grayscale gradient
        var data = ZlibCompress(raw);
        var pdf = BuildImagePdf(6, 1, "6 0 0 1 0 0",
            "/Width 6 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode", data);

        AssertExciseMatchesMutool(pdf, 6, 1, tolerance: 2,
            "plain FlateDecode of a grayscale gradient must match mutool's independent zlib inflate");
    }

    // ── stream-filter:FlateDecode.predictor-tiff (requirement-004) ─────────

    /// <summary>
    /// Literal IDAT/strip payload bytes are shared with
    /// ImageFilterRenderTests.cs's self-consistency checks (real TIFF strip
    /// data written by Pillow/libtiff, Predictor=2/horizontal) — reused here
    /// specifically to add the missing half: an INDEPENDENT decoder judging
    /// the same bytes, not just excise reading back its own understanding.
    /// </summary>
    [Fact]
    public void TiffPredictorFlateGradient_MatchesMutoolDecode()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var data = Convert.FromHexString("789c639004017e30290726010ea901a5");
        var pdf = BuildImagePdf(6, 3, "6 0 0 3 0 0",
            "/Width 6 /Height 3 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode " +
            "/DecodeParms << /Predictor 2 /Colors 1 /BitsPerComponent 8 /Columns 6 >>", data);

        AssertExciseMatchesMutool(pdf, 6, 3, tolerance: 3,
            "FlateDecode with a TIFF (horizontal-differencing) predictor must match mutool's independent decode");
    }

    // ── stream-filter:FlateDecode.predictor-png (requirement-005) ──────────

    [Fact]
    public void PngPredictorFlateGradient_MatchesMutoolDecode()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var data = Convert.FromHexString("789c6364d00001262e3080520010e40146");
        var pdf = BuildImagePdf(6, 3, "6 0 0 3 0 0",
            "/Width 6 /Height 3 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode " +
            "/DecodeParms << /Predictor 15 /Colors 1 /BitsPerComponent 8 /Columns 6 >>", data);

        AssertExciseMatchesMutool(pdf, 6, 3, tolerance: 3,
            "FlateDecode with a PNG (per-scanline) predictor must match mutool's independent decode");
    }

    // ── stream-filter:LZWDecode (requirement-006) ───────────────────────────

    /// <summary>
    /// Real LZW-compressed (no predictor, i.e. Predictor 1) TIFF strip bytes
    /// written by Pillow/libtiff for a 6x3 grayscale gradient — plain
    /// LZWDecode with no predictor layered on top, so this isolates the LZW
    /// codec itself from requirement-008's LZW+TIFF-predictor combination.
    /// </summary>
    [Fact]
    public void PlainLzwEncodedGradient_MatchesMutoolDecode()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var data = Convert.FromHexString("8000010100c08050301c10090502c180d0703c20111010");
        var pdf = BuildImagePdf(6, 3, "6 0 0 3 0 0",
            "/Width 6 /Height 3 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /LZWDecode", data);

        AssertExciseMatchesMutool(pdf, 6, 3, tolerance: 3,
            "plain LZWDecode (no predictor) of a grayscale gradient must match mutool's independent decode");
    }

    // ── stream-filter:LZWDecode.predictors (requirement-008) ───────────────

    [Fact]
    public void LzwWithTiffPredictorGradient_MatchesMutoolDecode()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var data = Convert.FromHexString("800003d03820780b058202a110307c2c3d01");
        var pdf = BuildImagePdf(8, 4, "8 0 0 4 0 0",
            "/Width 8 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /LZWDecode " +
            "/DecodeParms << /Predictor 2 /Colors 1 /BitsPerComponent 8 /Columns 8 >>", data);

        AssertExciseMatchesMutool(pdf, 8, 4, tolerance: 3,
            "LZWDecode with a TIFF predictor must match mutool's independent decode");
    }

    // ── stream-filter:RunLengthDecode (requirement-009) ─────────────────────

    /// <summary>
    /// 0xF1 0xC8 0x80 is the literal RunLengthDecode encoding (§7.4.5) of 16
    /// repeats of byte 0xC8 followed by EOD: length byte 241 = 257-16 means
    /// "copy the next byte 16 times", then the terminating 128. Spec algebra,
    /// not excise's encoder.
    /// </summary>
    [Fact]
    public void RunLengthEncodedSolidGray_MatchesMutoolDecode()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var data = new byte[] { 0xF1, 0xC8, 0x80 };
        var pdf = BuildImagePdf(4, 4, "4 0 0 4 0 0",
            "/Width 4 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /RunLengthDecode", data);

        AssertExciseMatchesMutool(pdf, 4, 4, tolerance: 2,
            "RunLengthDecode of a repeated byte run must match mutool's independent decode");
    }

    // ── image-filter:DCTDecode (requirement-012) ────────────────────────────

    /// <summary>
    /// A real baseline JPEG for an 8x8 solid (30,180,40) swatch, written by
    /// Python Pillow/libjpeg at quality=95 — an encoder excise never calls.
    /// excise itself delegates DCTDecode to SkiaSharp's own JPEG decoder
    /// (see FilterSupportMapTests.DctDecode_IsNotDecodedByExcise), so this
    /// specifically checks that the PIPELINE around that delegation (filter
    /// dispatch, colour-space handling, compositing) lands on the same pixels
    /// mutool's independent libjpeg decode does — not a re-check of
    /// SkiaSharp's codec in isolation. Tolerance is widened to 12 for JPEG's
    /// own lossy rounding (measured: mutool decodes to (29,180,39) against
    /// the source (30,180,40)).
    /// </summary>
    [Fact]
    public void DctEncodedSolidColor_MatchesMutoolDecode()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var jpeg = Convert.FromHexString(
            "ffd8ffe000104a46494600010100000100010000ffdb0043000201010101010201010102020202020403020202020504" +
            "040304060506060605060606070908060709070606080b08090a0a0a0a0a06080b0c0b0a0c090a0a0affdb0043010202" +
            "02020202050303050a0706070a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a" +
            "0a0a0a0a0a0a0a0a0a0a0a0a0a0affc00011080008000803012200021101031101ffc4001f0000010501010101010100" +
            "000000000000000102030405060708090a0bffc400b5100002010303020403050504040000017d010203000411051221" +
            "31410613516107227114328191a1082342b1c11552d1f02433627282090a161718191a25262728292a3435363738393a" +
            "434445464748494a535455565758595a636465666768696a737475767778797a838485868788898a9293949596979899" +
            "9aa2a3a4a5a6a7a8a9aab2b3b4b5b6b7b8b9bac2c3c4c5c6c7c8c9cad2d3d4d5d6d7d8d9dae1e2e3e4e5e6e7e8e9eaf1" +
            "f2f3f4f5f6f7f8f9faffc4001f0100030101010101010101010000000000000102030405060708090a0bffc400b51100" +
            "020102040403040705040400010277000102031104052131061241510761711322328108144291a1b1c109233352f015" +
            "6272d10a162434e125f11718191a262728292a35363738393a434445464748494a535455565758595a63646566676869" +
            "6a737475767778797a82838485868788898a92939495969798999aa2a3a4a5a6a7a8a9aab2b3b4b5b6b7b8b9bac2c3c4" +
            "c5c6c7c8c9cad2d3d4d5d6d7d8d9dae2e3e4e5e6e7e8e9eaf2f3f4f5f6f7f8f9faffda000c03010002110311003f00e6" +
            "e8a28afe4b3fcff3ffd9");
        var pdf = BuildImagePdf(8, 8, "8 0 0 8 0 0",
            "/Width 8 /Height 8 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", jpeg);

        AssertExciseMatchesMutool(pdf, 8, 8, tolerance: 15,
            "DCTDecode of a solid-colour JPEG must match mutool's independent libjpeg decode " +
            "within ordinary JPEG rounding");
    }

    // ── image-filter:JPXDecode (requirement-014) ────────────────────────────

    /// <summary>
    /// A real JP2 (ISO/IEC 15444-1) file for an 8x8 solid (10,20,200) swatch,
    /// written by Python Pillow/OpenJPEG — measured lossless at this rate
    /// setting (mutool decodes it to the exact source colour, no rounding).
    /// The two pdfium/pdfjs JPX corpus fixtures used elsewhere in this suite
    /// mix JPX with other complex content or a second filter and were not
    /// used here deliberately: this fixture isolates the codec so a mismatch
    /// can only mean a JPX decode problem, not a compositing or layout one.
    /// </summary>
    [Fact]
    public void JpxEncodedSolidColor_MatchesMutoolDecode()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var jp2 = Convert.FromHexString(
            "0000000c6a5020200d0a870a00000014667479706a703220000000006a7032200000002d6a703268000000166" +
            "968647200000008000000080003070700000000000f636f6c7201000000000010000000a76a703263ff4fff5" +
            "1002f000000000008000000080000000000000000000000080000000800000000000000000003070101070101" +
            "070101ff52000c00000001000304040001ff5c000d4040484850484850484850ff6400250001437265617465" +
            "64206279204f70656e4a5045472076657273696f6e20322e352e34ff90000a0000000000260001ff93cfb0080" +
            "997cfb008081fcfb008044f808080808080808080ffd9");
        var pdf = BuildImagePdf(8, 8, "8 0 0 8 0 0",
            "/Width 8 /Height 8 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /JPXDecode", jp2);

        AssertExciseMatchesMutool(pdf, 8, 8, tolerance: 6,
            "JPXDecode of a solid-colour JP2 must match mutool's independent OpenJPEG decode");
    }

    // ── stream-filter:Crypt (requirement-017) ───────────────────────────────

    [Fact]
    public void CryptIdentityFilteredImage_MatchesMutoolDecode()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var data = new byte[] { 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0, 255 }; // 2x2 solid blue RGB
        var pdf = BuildImagePdf(2, 2, "2 0 0 2 0 0",
            "/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /Crypt " +
            "/DecodeParms << /Name /Identity >>", data);

        AssertExciseMatchesMutool(pdf, 2, 2, tolerance: 2,
            "the /Crypt /Identity pass-through must match mutool's independent (no-op) decode");
    }

    // ── stream-filter-arrays (requirement-018) ──────────────────────────────

    /// <summary>
    /// "GhW&amp;@_!iG#!$1J0r;~&gt;" is Python's base64.a85encode (adobe=False)
    /// of zlib.compress'd bytes for a solid-red 2x2 RGB image — an
    /// INDEPENDENT chain-encoder, confirmed decoding to solid red via mutool
    /// before being pasted here. /Filter [/ASCII85Decode /FlateDecode] must
    /// apply both filters IN ORDER (ASCII85 first, then Flate) to recover it.
    /// </summary>
    [Fact]
    public void Ascii85ThenFlateFilterChain_MatchesMutoolDecode()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var data = Encoding.ASCII.GetBytes("GhW&@_!iG#!$1J0r;~>");
        var pdf = BuildImagePdf(2, 2, "2 0 0 2 0 0",
            "/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 " +
            "/Filter [/ASCII85Decode /FlateDecode]", data);

        AssertExciseMatchesMutool(pdf, 2, 2, tolerance: 2,
            "a two-filter chain [/ASCII85Decode /FlateDecode] must apply both filters in order, " +
            "matching mutool's independent decode of the same bytes");
    }

    // ── image-dictionary:ImageMask (requirement-019) ────────────────────────

    /// <summary>
    /// A 4x4 stencil mask (/ImageMask true, default Decode [0 1]: sample 0
    /// paints, sample 1 leaves unmarked). Row 0 = 0b0111xxxx (only column 0
    /// paints), rows 1-3 = 0b1111xxxx (nothing paints) — hand-derived
    /// directly from §8.9.6.2's bit meaning, confirmed against mutool: only
    /// the top-left quadrant of the scaled-up canvas paints red.
    /// </summary>
    [Fact]
    public void StencilImageMask_PaintsSameRegionMutoolDoes()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var maskData = new byte[] { 0x70, 0xF0, 0xF0, 0xF0 };
        var pdf = BuildStencilMaskPdf(40, 40, "1 0 0 rg", maskData);

        var path = WriteTempPdf(pdf);
        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        try
        {
            Assert.SkipWhen(mutool == null, "mutool failed to render the stencil-mask fixture");

            var mutoolRedFraction = RedFraction(mutool!, new SKRectI(0, 0, 10, 10));
            mutoolRedFraction.Should().BeGreaterThan(0.9,
                "oracle sanity: mutool must paint the top-left quadrant red per §8.9.6.2's default Decode");
            var mutoolElsewhereRed = RedFraction(mutool!, new SKRectI(10, 10, 40, 40));
            mutoolElsewhereRed.Should().BeLessThan(0.05,
                "oracle sanity: mutool must leave the rest of the canvas unpainted");

            using var doc = PdfDocument.Open(path);
            using var excise = new SkiaRenderer().RenderPage(doc.GetPage(1),
                new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

            RedFraction(excise, new SKRectI(0, 0, 10, 10)).Should().BeGreaterThan(0.9,
                "excise must paint the same top-left quadrant mutool paints from this stencil mask");
            RedFraction(excise, new SKRectI(10, 10, 40, 40)).Should().BeLessThan(0.05,
                "excise must leave the same region unpainted mutool leaves unpainted");
        }
        finally { TryDelete(path); }
    }

    // ── image-dictionary:Mask.image (requirement-021) ───────────────────────

    /// <summary>
    /// A 4x4 solid-green base image with an explicit /Mask 6 0 R stencil
    /// (same bit pattern as the ImageMask test above, only column 0 of row 0
    /// paints): the base image's pixels are painted only where the mask
    /// stream says to paint, revealing the white page background elsewhere.
    /// Confirmed against mutool: green top-left 10x10 device-pixel block
    /// (one mask sample, scaled 10x), white everywhere else.
    /// </summary>
    [Fact]
    public void ExplicitMaskStream_PaintsSameRegionMutoolDoes()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var baseData = Repeat(new byte[] { 0, 255, 0 }, 16); // 4x4 solid green
        var maskData = new byte[] { 0x70, 0xF0, 0xF0, 0xF0 };
        var pdf = BuildImageWithSecondaryPdf(40, 40,
            "/Width 4 /Height 4 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Mask 6 0 R", baseData,
            "/ImageMask true /Width 4 /Height 4 /BitsPerComponent 1", maskData);

        var path = WriteTempPdf(pdf);
        try
        {
            using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
            Assert.SkipWhen(mutool == null, "mutool failed to render the explicit-mask fixture");

            GreenFraction(mutool!, new SKRectI(0, 0, 10, 10)).Should().BeGreaterThan(0.9,
                "oracle sanity: mutool must paint the single masked-in sample (row 0, column 0) green");
            WhiteFraction(mutool!, new SKRectI(10, 10, 40, 40)).Should().BeGreaterThan(0.9,
                "oracle sanity: mutool must leave the masked-out region showing the white page background");

            using var doc = PdfDocument.Open(path);
            using var excise = new SkiaRenderer().RenderPage(doc.GetPage(1),
                new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

            GreenFraction(excise, new SKRectI(0, 0, 10, 10)).Should().BeGreaterThan(0.9,
                "excise must paint the same masked-in region mutool paints from the explicit /Mask stream");
            WhiteFraction(excise, new SKRectI(10, 10, 40, 40)).Should().BeGreaterThan(0.9,
                "excise must leave the same masked-out region mutool leaves showing background");
        }
        finally { TryDelete(path); }
    }

    // ── image-dictionary:SMask (requirement-020) ────────────────────────────

    /// <summary>
    /// A 4x4 solid-red base image with a /SMask 6 0 R alpha ramp (rows of
    /// alpha 255, 170, 85, 0 top to bottom). Confirmed against mutool's
    /// independent compositing over the white page background: row 0 stays
    /// pure red, row 3 fully disappears to white, and the middle rows land at
    /// the arithmetic straight-alpha blend (measured (254,84,84) and
    /// (255,171,171)).
    /// </summary>
    [Fact]
    public void SoftMaskAlphaRamp_CompositesSameAsMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var baseData = Repeat(new byte[] { 255, 0, 0 }, 16); // 4x4 solid red
        var smaskData = new byte[] { 255, 255, 255, 255, 170, 170, 170, 170, 85, 85, 85, 85, 0, 0, 0, 0 };
        var pdf = BuildImageWithSecondaryPdf(40, 40,
            "/Width 4 /Height 4 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 6 0 R", baseData,
            "/Width 4 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 8", smaskData);

        var path = WriteTempPdf(pdf);
        try
        {
            using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
            Assert.SkipWhen(mutool == null, "mutool failed to render the soft-mask fixture");

            using var doc = PdfDocument.Open(path);
            using var excise = new SkiaRenderer().RenderPage(doc.GetPage(1),
                new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

            foreach (var y in new[] { 5, 15, 25, 35 })
            {
                var mutoolPixel = mutool!.GetPixel(5, y);
                var excisePixel = excise.GetPixel(5, y);
                Math.Abs(excisePixel.Red - mutoolPixel.Red).Should().BeLessThanOrEqualTo(10,
                    $"row y={y}: excise's soft-mask composite (R={excisePixel.Red}) must match " +
                    $"mutool's (R={mutoolPixel.Red})");
                Math.Abs(excisePixel.Green - mutoolPixel.Green).Should().BeLessThanOrEqualTo(10,
                    $"row y={y}: excise's soft-mask composite (G={excisePixel.Green}) must match " +
                    $"mutool's (G={mutoolPixel.Green})");
            }
        }
        finally { TryDelete(path); }
    }

    // ── image-placement:CTM (requirement-034) ───────────────────────────────

    /// <summary>
    /// A 4x4 solid-blue image placed via <c>40 0 0 40 20 30 cm</c> on a
    /// 100x100 page — scaled to 40x40 device units and translated off the
    /// origin, rather than the full-page fill every other fixture in this
    /// file uses. Confirmed against mutool: the painted bounding box is
    /// exactly x=[20,59] y=[30,69] (PDF-space [20,60]x[30,70], Y-flipped for
    /// a 100pt-tall page) — an independent check that excise's CTM
    /// composition (not just its decode) lands an image in the same place an
    /// unrelated renderer does.
    /// </summary>
    [Fact]
    public void ScaledAndTranslatedImage_BoundingBoxMatchesMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var data = Repeat(new byte[] { 0, 0, 255 }, 16); // 4x4 solid blue
        var pdf = BuildImagePdf(100, 100, "40 0 0 40 20 30",
            "/Width 4 /Height 4 /ColorSpace /DeviceRGB /BitsPerComponent 8", data);

        var path = WriteTempPdf(pdf);
        try
        {
            using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
            Assert.SkipWhen(mutool == null, "mutool failed to render the CTM-placement fixture");

            var mutoolBox = BlueBoundingBox(mutool!);
            mutoolBox.Should().NotBeNull("oracle sanity: mutool must paint some blue pixels");
            mutoolBox!.Value.Should().Be((20, 59, 30, 69),
                "oracle sanity: mutool's own bounding box must land where the CTM arithmetic predicts");

            using var doc = PdfDocument.Open(path);
            using var excise = new SkiaRenderer().RenderPage(doc.GetPage(1),
                new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

            var exciseBox = BlueBoundingBox(excise);
            exciseBox.Should().NotBeNull("excise must paint some blue pixels");
            var (mx0, mx1, my0, my1) = mutoolBox!.Value;
            var (ex0, ex1, ey0, ey1) = exciseBox!.Value;
            Math.Abs(ex0 - mx0).Should().BeLessThanOrEqualTo(1, "left edge must match mutool's within 1px");
            Math.Abs(ex1 - mx1).Should().BeLessThanOrEqualTo(1, "right edge must match mutool's within 1px");
            Math.Abs(ey0 - my0).Should().BeLessThanOrEqualTo(1, "top edge must match mutool's within 1px");
            Math.Abs(ey1 - my1).Should().BeLessThanOrEqualTo(1, "bottom edge must match mutool's within 1px");
        }
        finally { TryDelete(path); }
    }

    // ------------------------------------------------------------------ helpers --

    private static void AssertExciseMatchesMutool(byte[] pdfBytes, int width, int height, int tolerance,
        string because)
    {
        var path = WriteTempPdf(pdfBytes);
        try
        {
            using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
            Assert.SkipWhen(mutool == null, "mutool failed to render this fixture");

            using var doc = PdfDocument.Open(path);
            using var excise = new SkiaRenderer().RenderPage(doc.GetPage(1),
                new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

            // Sample every image-space pixel position (device pixels equal
            // image samples 1:1 at this DPI/size combination) rather than
            // just one — a filter bug that corrupts only some samples must
            // not hide behind a single lucky pixel.
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var m = mutool!.GetPixel(x, y);
                    var e = excise.GetPixel(x, y);
                    Math.Abs(e.Red - m.Red).Should().BeLessThanOrEqualTo(tolerance,
                        $"pixel ({x},{y}) red channel: {because} (excise={e} mutool={m})");
                    Math.Abs(e.Green - m.Green).Should().BeLessThanOrEqualTo(tolerance,
                        $"pixel ({x},{y}) green channel: {because} (excise={e} mutool={m})");
                    Math.Abs(e.Blue - m.Blue).Should().BeLessThanOrEqualTo(tolerance,
                        $"pixel ({x},{y}) blue channel: {because} (excise={e} mutool={m})");
                }
            }
        }
        finally { TryDelete(path); }
    }

    private static byte[] ZlibCompress(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private static byte[] Repeat(byte[] pattern, int times)
    {
        var result = new byte[pattern.Length * times];
        for (var i = 0; i < times; i++)
            Array.Copy(pattern, 0, result, i * pattern.Length, pattern.Length);
        return result;
    }

    private static string WriteTempPdf(byte[] pdfBytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-imgreq-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdfBytes);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private static double RedFraction(SKBitmap bmp, SKRectI region)
    {
        long red = 0, total = 0;
        for (var y = Math.Max(0, region.Top); y < Math.Min(bmp.Height, region.Bottom); y++)
            for (var x = Math.Max(0, region.Left); x < Math.Min(bmp.Width, region.Right); x++)
            {
                total++;
                var p = bmp.GetPixel(x, y);
                if (p.Red > 200 && p.Green < 60 && p.Blue < 60) red++;
            }
        return total == 0 ? 0 : (double)red / total;
    }

    private static double GreenFraction(SKBitmap bmp, SKRectI region)
    {
        long green = 0, total = 0;
        for (var y = Math.Max(0, region.Top); y < Math.Min(bmp.Height, region.Bottom); y++)
            for (var x = Math.Max(0, region.Left); x < Math.Min(bmp.Width, region.Right); x++)
            {
                total++;
                var p = bmp.GetPixel(x, y);
                if (p.Green > 200 && p.Red < 60 && p.Blue < 60) green++;
            }
        return total == 0 ? 0 : (double)green / total;
    }

    private static double WhiteFraction(SKBitmap bmp, SKRectI region)
    {
        long white = 0, total = 0;
        for (var y = Math.Max(0, region.Top); y < Math.Min(bmp.Height, region.Bottom); y++)
            for (var x = Math.Max(0, region.Left); x < Math.Min(bmp.Width, region.Right); x++)
            {
                total++;
                var p = bmp.GetPixel(x, y);
                if (p.Red > 240 && p.Green > 240 && p.Blue > 240) white++;
            }
        return total == 0 ? 0 : (double)white / total;
    }

    private static (int X0, int X1, int Y0, int Y1)? BlueBoundingBox(SKBitmap bmp)
    {
        int x0 = int.MaxValue, x1 = -1, y0 = int.MaxValue, y1 = -1;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.Blue > 200 && p.Red < 60 && p.Green < 60)
                {
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
            }
        return x1 < 0 ? null : (x0, x1, y0, y1);
    }

    /// <summary>
    /// Builds a minimal single-image-XObject PDF: a page of size
    /// <paramref name="pageW"/>x<paramref name="pageH"/> whose content stream
    /// is <c>q &lt;cm&gt; cm /Im0 Do Q</c>, and one Image XObject (object 5)
    /// carrying <paramref name="imageDictExtra"/> and <paramref name="streamData"/>
    /// verbatim. Deliberately hand-rolled (not excise's writer) with a plain
    /// non-compressed xref, the same shape ImageFilterRenderTests.cs uses —
    /// confirmed to open in mutool during fixture prototyping.
    /// </summary>
    private static byte[] BuildImagePdf(int pageW, int pageH, string cm, string imageDictExtra, byte[] streamData)
    {
        var content = $"q {cm} cm /Im0 Do Q";
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {pageW} {pageH}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R " +
            "/Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            $"5 0 obj\n<< /Type /XObject /Subtype /Image {imageDictExtra} /Length {streamData.Length} >>\nstream\n",
        };
        return AssemblePdf(objects, ("/Subtype /Image", streamData));
    }

    /// <summary>
    /// Same shape as <see cref="BuildImagePdf"/> but for a bare stencil mask
    /// (§8.9.6.2): sets <paramref name="fillOp"/> (e.g. "1 0 0 rg") before
    /// drawing the /ImageMask XObject, which paints in the current colour
    /// wherever the mask says to paint.
    /// </summary>
    private static byte[] BuildStencilMaskPdf(int pageW, int pageH, string fillOp, byte[] maskData)
    {
        var content = $"{fillOp} q {pageW} 0 0 {pageH} 0 0 cm /Im0 Do Q";
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {pageW} {pageH}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R " +
            "/Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            $"5 0 obj\n<< /Type /XObject /Subtype /Image /ImageMask true /Width 4 /Height 4 " +
            $"/BitsPerComponent 1 /Length {maskData.Length} >>\nstream\n",
        };
        return AssemblePdf(objects, ("/Subtype /Image", maskData));
    }

    /// <summary>
    /// A page drawing one Image XObject (object 5, carrying
    /// <paramref name="primaryDictExtra"/> — expected to reference object 6
    /// via <c>/Mask 6 0 R</c> or <c>/SMask 6 0 R</c>) plus a second Image
    /// XObject (object 6, <paramref name="secondaryDictExtra"/>) that is
    /// never drawn directly, only referenced from the first.
    /// </summary>
    private static byte[] BuildImageWithSecondaryPdf(int pageW, int pageH,
        string primaryDictExtra, byte[] primaryData, string secondaryDictExtra, byte[] secondaryData)
    {
        var content = $"q {pageW} 0 0 {pageH} 0 0 cm /Im0 Do Q";
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {pageW} {pageH}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R " +
            "/Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            $"5 0 obj\n<< /Type /XObject /Subtype /Image {primaryDictExtra} " +
            $"/Length {primaryData.Length} >>\nstream\n",
            $"6 0 obj\n<< /Type /XObject /Subtype /Image {secondaryDictExtra} " +
            $"/Length {secondaryData.Length} >>\nstream\n",
        };
        return AssemblePdf(objects, ("/Subtype /Image", primaryData), ("/Subtype /Image", secondaryData));
    }

    /// <summary>
    /// Shared low-level assembler: writes each object body verbatim, and for
    /// every object whose text contains the matching marker (in order),
    /// appends the corresponding raw stream bytes plus
    /// "\nendstream\nendobj\n" before moving to the next object. Builds a
    /// plain (non-compressed, non-cross-reference-stream) xref table, which
    /// every reference renderer used in this suite accepts.
    /// </summary>
    private static byte[] AssemblePdf(string[] objects, params (string Marker, byte[] Data)[] streams)
    {
        var bytes = new System.Collections.Generic.List<byte>(Encoding.ASCII.GetBytes("%PDF-1.7\n"));
        var offsets = new System.Collections.Generic.List<int>();
        var streamIndex = 0;
        foreach (var o in objects)
        {
            offsets.Add(bytes.Count);
            bytes.AddRange(Encoding.ASCII.GetBytes(o));
            if (streamIndex < streams.Length && o.Contains(streams[streamIndex].Marker))
            {
                bytes.AddRange(streams[streamIndex].Data);
                bytes.AddRange(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
                streamIndex++;
            }
        }

        var sb = new StringBuilder();
        var xref = bytes.Count;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        bytes.AddRange(Encoding.ASCII.GetBytes(sb.ToString()));
        return bytes.ToArray();
    }
}
