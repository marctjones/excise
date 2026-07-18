using System;
using System.Collections.Generic;

namespace Excise.Core.Filters.Jbig2;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class Jbig2PatternDictionaryDecoder
{
    public static IReadOnlyList<Jbig2Bitmap> Decode(
        Jbig2PatternDictionarySegment segment,
        ReadOnlySpan<byte> payload)
    {
        var collectiveBitmap = segment.IsMmrEncoded
            ? DecodeMmrCollectiveBitmap(segment, payload)
            : DecodeArithmeticCollectiveBitmap(
                segment,
                new Jbig2MqArithmeticDecoder(payload.ToArray(), Jbig2ArithmeticGenericRegionDecoder.ContextCount));

        return ExtractPatterns(segment, collectiveBitmap);
    }

    internal static IReadOnlyList<Jbig2Bitmap> DecodeArithmeticForTest(
        Jbig2PatternDictionarySegment segment,
        IJbig2ArithmeticDecoder decoder)
        => ExtractPatterns(segment, DecodeArithmeticCollectiveBitmap(segment, decoder));

    private static Jbig2Bitmap DecodeMmrCollectiveBitmap(
        Jbig2PatternDictionarySegment segment,
        ReadOnlySpan<byte> payload)
    {
        int collectiveWidth = GetCollectiveWidth(segment);
        int height = segment.PatternHeight;
        return new Jbig2Bitmap(
            collectiveWidth,
            height,
            Jbig2MmrDecoder.Decode(payload.ToArray(), collectiveWidth, height));
    }

    private static Jbig2Bitmap DecodeArithmeticCollectiveBitmap(
        Jbig2PatternDictionarySegment segment,
        IJbig2ArithmeticDecoder decoder)
        => Jbig2ArithmeticGenericRegionDecoder.Decode(
            decoder,
            GetCollectiveWidth(segment),
            segment.PatternHeight,
            segment.Template,
            GetAdaptiveTemplatePixels(segment),
            typicalPredictionGenericDecodingOn: false);

    private static IReadOnlyList<Jbig2Bitmap> ExtractPatterns(
        Jbig2PatternDictionarySegment segment,
        Jbig2Bitmap collectiveBitmap)
    {
        int patternCount = GetPatternCount(segment);
        int patternWidth = segment.PatternWidth;
        int patternHeight = segment.PatternHeight;
        var patterns = new Jbig2Bitmap[patternCount];

        for (int gray = 0; gray < patterns.Length; gray++)
        {
            var pattern = new Jbig2Bitmap(patternWidth, patternHeight);
            int xOffset = gray * patternWidth;
            for (int y = 0; y < patternHeight; y++)
            {
                for (int x = 0; x < patternWidth; x++)
                    pattern.SetPixel(x, y, collectiveBitmap.GetPixel(xOffset + x, y));
            }

            patterns[gray] = pattern;
        }

        return patterns;
    }

    private static Jbig2AdaptiveTemplatePixel[] GetAdaptiveTemplatePixels(
        Jbig2PatternDictionarySegment segment)
    {
        int previousPatternX = -segment.PatternWidth;
        return segment.Template == 0
            ?
            [
                new(previousPatternX, 0),
                new(-3, -1),
                new(2, -2),
                new(-2, -2),
            ]
            : [new(previousPatternX, 0)];
    }

    private static int GetCollectiveWidth(Jbig2PatternDictionarySegment segment)
        => checked(GetPatternCount(segment) * segment.PatternWidth);

    private static int GetPatternCount(Jbig2PatternDictionarySegment segment)
    {
        if (segment.GrayMax >= int.MaxValue)
            throw new InvalidOperationException("JBIG2 pattern dictionary contains too many patterns");

        return (int)segment.GrayMax + 1;
    }
}
