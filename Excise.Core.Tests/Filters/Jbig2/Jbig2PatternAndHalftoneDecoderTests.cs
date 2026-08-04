using AwesomeAssertions;
using Excise.Core.Filters.Jbig2;
using Xunit;

namespace Excise.Core.Tests.Filters.Jbig2;

public class Jbig2PatternAndHalftoneDecoderTests
{
    [Fact]
    public void PatternDictionary_Arithmetic_DecodesCollectiveBitmapAndSplitsPatterns()
    {
        var segment = new Jbig2PatternDictionarySegment(
            IsMmrEncoded: false,
            Template: 0,
            PatternWidth: 2,
            PatternHeight: 2,
            GrayMax: 1,
            BitmapDataOffset: 0,
            BitmapDataLength: 0);
        var decoder = new ScriptedArithmeticDecoder(
            true, false, false, true,
            false, true, true, false);

        var patterns = Jbig2PatternDictionaryDecoder.DecodeArithmeticForTest(segment, decoder);

        patterns.Should().HaveCount(2);
        patterns[0].GetPixel(0, 0).Should().BeTrue();
        patterns[0].GetPixel(1, 0).Should().BeFalse();
        patterns[0].GetPixel(0, 1).Should().BeFalse();
        patterns[0].GetPixel(1, 1).Should().BeTrue();
        patterns[1].GetPixel(0, 0).Should().BeFalse();
        patterns[1].GetPixel(1, 0).Should().BeTrue();
        patterns[1].GetPixel(0, 1).Should().BeTrue();
        patterns[1].GetPixel(1, 1).Should().BeFalse();
    }

    [Fact]
    public void HalftoneRegion_Arithmetic_UsesSkipMaskWithoutConsumingSkippedPixels()
    {
        var whitePattern = new Jbig2Bitmap(1, 1);
        var blackPattern = new Jbig2Bitmap(1, 1);
        blackPattern.SetPixel(0, 0, true);
        var segment = new Jbig2HalftoneRegionSegment(
            Region: new Jbig2RegionSegmentInformation(1, 1, 0, 0, Jbig2CombinationOperator.Replace),
            DefaultPixel: 0,
            CombinationOperator: Jbig2CombinationOperator.Replace,
            SkipEnabled: true,
            Template: 0,
            IsMmrEncoded: false,
            GridWidth: 2,
            GridHeight: 1,
            GridX: 0,
            GridY: 0,
            RegionX: 256,
            RegionY: 0,
            BitmapDataOffset: 0,
            BitmapDataLength: 0);
        var decoder = new ScriptedArithmeticDecoder(true);

        var bitmap = Jbig2HalftoneRegionDecoder.DecodeArithmeticForTest(
            segment,
            decoder,
            [whitePattern, blackPattern]);

        bitmap.GetPixel(0, 0).Should().BeTrue();
        decoder.Contexts.Should().HaveCount(1);
    }

    [Fact]
    public void HalftoneRegion_Mmr_DecodesGrayScalePlanesAndRendersPatterns()
    {
        var whitePattern = new Jbig2Bitmap(1, 1);
        var blackPattern = new Jbig2Bitmap(1, 1);
        blackPattern.SetPixel(0, 0, true);
        var segment = new Jbig2HalftoneRegionSegment(
            Region: new Jbig2RegionSegmentInformation(8, 1, 0, 0, Jbig2CombinationOperator.Replace),
            DefaultPixel: 0,
            CombinationOperator: Jbig2CombinationOperator.Replace,
            SkipEnabled: false,
            Template: 0,
            IsMmrEncoded: true,
            GridWidth: 8,
            GridHeight: 1,
            GridX: 0,
            GridY: 0,
            RegionX: 256,
            RegionY: 0,
            BitmapDataOffset: 0,
            BitmapDataLength: 0);

        var bitmap = Jbig2HalftoneRegionDecoder.Decode(
            segment,
            [0b00110110, 0b11000000],
            [whitePattern, blackPattern]);

        bitmap.Data.Should().Equal(0x0F);
    }

    /// <summary>
    /// ISO 14492 Annex C.5: with GSMMR=1 the gray-scale bitplanes are decoded
    /// SEQUENTIALLY from one MMR stream, each plane terminated by EOFB, most
    /// significant plane first — not as one side-by-side collective bitmap
    /// (#874). Payload here: plane1 (V0-coded: 4 white + 4 black), EOFB,
    /// zero-fill to the byte boundary, plane0 (all black), EOFB.
    /// A side-by-side 16x1 reading of the same bits stops at the EOFB and
    /// yields gray values 0/3 instead of 1/2, so this test fails against the
    /// collective-bitmap decode.
    /// </summary>
    [Fact]
    public void HalftoneRegion_MmrMultiPlane_DecodesSequentialEofbSeparatedPlanes()
    {
        var patterns = new Jbig2Bitmap[4];
        for (int i = 0; i < 4; i++)
            patterns[i] = new Jbig2Bitmap(1, 1);
        patterns[1].SetPixel(0, 0, true); // gray value 1 -> black
        var segment = new Jbig2HalftoneRegionSegment(
            Region: new Jbig2RegionSegmentInformation(8, 1, 0, 0, Jbig2CombinationOperator.Replace),
            DefaultPixel: 0,
            CombinationOperator: Jbig2CombinationOperator.Replace,
            SkipEnabled: false,
            Template: 0,
            IsMmrEncoded: true,
            GridWidth: 8,
            GridHeight: 1,
            GridX: 0,
            GridY: 0,
            RegionX: 256,
            RegionY: 0,
            BitmapDataOffset: 0,
            BitmapDataLength: 0);

        // Plane 1 (MSB, decoded first): H(001) white-run-4(1011) black-run-4(011)
        //   -> row 00001111, 10 bits.
        // EOFB (24 bits), then 6 zero fill bits to the byte boundary.
        // Plane 0: H(001) white-run-0(00110101) black-run-8(000101)
        //   -> row 11111111, 17 bits. EOFB. Zero-padded to 11 bytes.
        var payload = BitsToBytes(
            "0011011011" + Eofb + "000000" +
            "00100110101000101" + Eofb);

        var bitmap = Jbig2HalftoneRegionDecoder.Decode(segment, payload, patterns);

        // Gray-code combination (C.5): plane0 ^= plane1.
        //   columns 0-3: plane1=0, raw plane0=1 -> value 1 -> black pattern
        //   columns 4-7: plane1=1, plane0=1^1=0 -> value 2 -> white pattern
        bitmap.Data.Should().Equal(0xF0);
    }

    [Fact]
    public void HalftoneRegion_MmrPlaneTruncated_ThrowsInsteadOfBlankPlanes()
    {
        // Two planes declared (4 patterns), but the payload ends after the
        // first plane's EOFB. The old path zero-padded the missing data into
        // a full-size silently blank bitmap; a truncation must surface.
        var patterns = new Jbig2Bitmap[4];
        for (int i = 0; i < 4; i++)
            patterns[i] = new Jbig2Bitmap(1, 1);
        var segment = new Jbig2HalftoneRegionSegment(
            Region: new Jbig2RegionSegmentInformation(8, 1, 0, 0, Jbig2CombinationOperator.Replace),
            DefaultPixel: 0,
            CombinationOperator: Jbig2CombinationOperator.Replace,
            SkipEnabled: false,
            Template: 0,
            IsMmrEncoded: true,
            GridWidth: 8,
            GridHeight: 1,
            GridX: 0,
            GridY: 0,
            RegionX: 256,
            RegionY: 0,
            BitmapDataOffset: 0,
            BitmapDataLength: 0);
        var payload = BitsToBytes("0011011011" + Eofb);

        var act = () => Jbig2HalftoneRegionDecoder.Decode(segment, payload, patterns);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*truncated*", "a missing gray plane must fail loudly, not render blank");
    }

    [Fact]
    public void HalftoneRegion_MmrTenPlaneFixture_DecodesRealInk()
    {
        // pdf.js's bitmap-halftone-10bpp-mmr.pdf: a 399x400 halftone region,
        // 25x25 grid, 10 MMR gray planes. The side-by-side collective reading
        // decoded a fraction of plane 0 and rendered the page blank.
        // Independent oracle: mutool draw measures 0.0686 dark fraction.
        const string fixture = "../../../../test-pdfs/pdfjs/bitmap-halftone-10bpp-mmr.pdf";
        Assert.SkipWhen(!File.Exists(fixture), "pdf.js corpus fixture not available");

        using var doc = Excise.Core.Document.PdfDocument.Open(File.ReadAllBytes(fixture));
        var image = (Excise.Core.Primitives.PdfStream)doc.GetObject(5);

        image.IsDecoded.Should().BeTrue();
        int expectedBytes = ((399 + 7) / 8) * 400;
        image.DecodedData.Length.Should().Be(expectedBytes);

        long dark = 0;
        foreach (var b in image.DecodedData)
            dark += 8 - System.Numerics.BitOperations.PopCount(b);
        double darkFraction = (double)dark / (expectedBytes * 8L);
        darkFraction.Should().BeInRange(0.05, 0.09,
            "mutool measures 0.0686 dark on this page; a blank or inverted decode is outside these bounds");
    }

    private const string Eofb = "000000000001000000000001";

    private static byte[] BitsToBytes(string bits)
    {
        var result = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i] == '1')
                result[i / 8] |= (byte)(0x80 >> (i % 8));
        }

        return result;
    }

    private sealed class ScriptedArithmeticDecoder : IJbig2ArithmeticDecoder
    {
        private readonly Queue<bool> _bits;

        public ScriptedArithmeticDecoder(params bool[] bits)
        {
            _bits = new Queue<bool>(bits);
        }

        public List<int> Contexts { get; } = new();

        public bool Decode(ref int context)
        {
            if (_bits.Count == 0)
                throw new InvalidOperationException("Scripted arithmetic decoder exhausted");

            Contexts.Add(context);
            return _bits.Dequeue();
        }
    }
}
