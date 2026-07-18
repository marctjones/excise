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
