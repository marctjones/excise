using System;
using System.Collections.Generic;

namespace Excise.Core.Filters.Jbig2;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class Jbig2HalftoneRegionDecoder
{
    public static Jbig2Bitmap Decode(
        Jbig2HalftoneRegionSegment segment,
        ReadOnlySpan<byte> payload,
        IReadOnlyList<Jbig2Bitmap> patterns)
    {
        if (segment.IsMmrEncoded)
            return DecodeMmr(segment, payload, patterns);

        var decoder = new Jbig2MqArithmeticDecoder(
            payload.ToArray(),
            Jbig2ArithmeticGenericRegionDecoder.ContextCount);
        return DecodeArithmetic(segment, decoder, patterns);
    }

    internal static Jbig2Bitmap DecodeArithmeticForTest(
        Jbig2HalftoneRegionSegment segment,
        IJbig2ArithmeticDecoder decoder,
        IReadOnlyList<Jbig2Bitmap> patterns)
        => DecodeArithmetic(segment, decoder, patterns);

    private static Jbig2Bitmap DecodeArithmetic(
        Jbig2HalftoneRegionSegment segment,
        IJbig2ArithmeticDecoder decoder,
        IReadOnlyList<Jbig2Bitmap> patterns)
    {
        ValidateInputs(segment, patterns);

        int regionWidth = checked((int)segment.Region.BitmapWidth);
        int regionHeight = checked((int)segment.Region.BitmapHeight);
        int gridWidth = checked((int)segment.GridWidth);
        int gridHeight = checked((int)segment.GridHeight);
        var bitmap = new Jbig2Bitmap(regionWidth, regionHeight);
        if (segment.DefaultPixel != 0)
            bitmap.Fill(true);

        var skipBitmap = segment.SkipEnabled
            ? ComputeSkipBitmap(segment, patterns[0], regionWidth, regionHeight, gridWidth, gridHeight)
            : null;
        int bitsPerValue = GetBitsPerValue(patterns.Count);
        var grayScalePlanes = DecodeGrayScalePlanes(
            segment,
            decoder,
            gridWidth,
            gridHeight,
            bitsPerValue,
            skipBitmap);

        RenderPatterns(segment, bitmap, patterns, grayScalePlanes, gridWidth, gridHeight, bitsPerValue);
        return bitmap;
    }

    private static Jbig2Bitmap DecodeMmr(
        Jbig2HalftoneRegionSegment segment,
        ReadOnlySpan<byte> payload,
        IReadOnlyList<Jbig2Bitmap> patterns)
    {
        ValidateInputs(segment, patterns);

        int regionWidth = checked((int)segment.Region.BitmapWidth);
        int regionHeight = checked((int)segment.Region.BitmapHeight);
        int gridWidth = checked((int)segment.GridWidth);
        int gridHeight = checked((int)segment.GridHeight);
        var bitmap = new Jbig2Bitmap(regionWidth, regionHeight);
        if (segment.DefaultPixel != 0)
            bitmap.Fill(true);

        int bitsPerValue = GetBitsPerValue(patterns.Count);
        var grayScalePlanes = DecodeMmrGrayScalePlanes(payload, gridWidth, gridHeight, bitsPerValue);

        RenderPatterns(segment, bitmap, patterns, grayScalePlanes, gridWidth, gridHeight, bitsPerValue);
        return bitmap;
    }

    private static Jbig2Bitmap[] DecodeGrayScalePlanes(
        Jbig2HalftoneRegionSegment segment,
        IJbig2ArithmeticDecoder decoder,
        int gridWidth,
        int gridHeight,
        int bitsPerValue,
        Jbig2Bitmap? skipBitmap)
    {
        if (bitsPerValue == 0)
            return Array.Empty<Jbig2Bitmap>();

        var planes = new Jbig2Bitmap[bitsPerValue];
        var adaptiveTemplatePixels = GetAdaptiveTemplatePixels(segment.Template);

        for (int j = bitsPerValue - 1; j >= 0; j--)
        {
            planes[j] = Jbig2ArithmeticGenericRegionDecoder.Decode(
                decoder,
                gridWidth,
                gridHeight,
                segment.Template,
                adaptiveTemplatePixels,
                typicalPredictionGenericDecodingOn: false,
                skipBitmap: skipBitmap);

            if (j < bitsPerValue - 1)
                XorInto(planes[j], planes[j + 1]);
        }

        return planes;
    }

    private static Jbig2Bitmap[] DecodeMmrGrayScalePlanes(
        ReadOnlySpan<byte> payload,
        int gridWidth,
        int gridHeight,
        int bitsPerValue)
    {
        if (bitsPerValue == 0)
            return Array.Empty<Jbig2Bitmap>();

        var collectiveBitmap = new Jbig2Bitmap(
            checked(gridWidth * bitsPerValue),
            gridHeight,
            Jbig2MmrDecoder.Decode(payload.ToArray(), checked(gridWidth * bitsPerValue), gridHeight));
        var planes = new Jbig2Bitmap[bitsPerValue];

        for (int j = bitsPerValue - 1; j >= 0; j--)
        {
            int sourceX = (bitsPerValue - 1 - j) * gridWidth;
            planes[j] = ExtractPlane(collectiveBitmap, sourceX, gridWidth);
            if (j < bitsPerValue - 1)
                XorInto(planes[j], planes[j + 1]);
        }

        return planes;
    }

    private static Jbig2Bitmap ExtractPlane(Jbig2Bitmap collectiveBitmap, int sourceX, int width)
    {
        var plane = new Jbig2Bitmap(width, collectiveBitmap.Height);
        for (int y = 0; y < collectiveBitmap.Height; y++)
        {
            for (int x = 0; x < width; x++)
                plane.SetPixel(x, y, collectiveBitmap.GetPixel(sourceX + x, y));
        }

        return plane;
    }

    private static void RenderPatterns(
        Jbig2HalftoneRegionSegment segment,
        Jbig2Bitmap destination,
        IReadOnlyList<Jbig2Bitmap> patterns,
        IReadOnlyList<Jbig2Bitmap> grayScalePlanes,
        int gridWidth,
        int gridHeight,
        int bitsPerValue)
    {
        for (int m = 0; m < gridHeight; m++)
        {
            for (int n = 0; n < gridWidth; n++)
            {
                int grayValue = ComputeGrayValue(grayScalePlanes, bitsPerValue, n, m);
                if ((uint)grayValue >= (uint)patterns.Count)
                    throw new InvalidOperationException("JBIG2 halftone gray value exceeds pattern dictionary size");

                Jbig2BitmapCompositor.Composite(
                    destination,
                    patterns[grayValue],
                    ComputeX(segment, m, n),
                    ComputeY(segment, m, n),
                    segment.CombinationOperator);
            }
        }
    }

    private static Jbig2Bitmap ComputeSkipBitmap(
        Jbig2HalftoneRegionSegment segment,
        Jbig2Bitmap pattern,
        int regionWidth,
        int regionHeight,
        int gridWidth,
        int gridHeight)
    {
        var skipBitmap = new Jbig2Bitmap(gridWidth, gridHeight);
        for (int m = 0; m < gridHeight; m++)
        {
            for (int n = 0; n < gridWidth; n++)
            {
                int x = ComputeX(segment, m, n);
                int y = ComputeY(segment, m, n);
                if (x + pattern.Width <= 0 || x >= regionWidth || y + pattern.Height <= 0 || y >= regionHeight)
                    skipBitmap.SetPixel(n, m, true);
            }
        }

        return skipBitmap;
    }

    private static int ComputeGrayValue(
        IReadOnlyList<Jbig2Bitmap> grayScalePlanes,
        int bitsPerValue,
        int x,
        int y)
    {
        int value = 0;
        for (int j = 0; j < bitsPerValue; j++)
        {
            if (grayScalePlanes[j].GetPixel(x, y))
                value |= 1 << j;
        }

        return value;
    }

    private static void XorInto(Jbig2Bitmap destination, Jbig2Bitmap source)
    {
        for (int i = 0; i < destination.Data.Length; i++)
            destination.Data[i] ^= source.Data[i];
    }

    private static int ComputeX(Jbig2HalftoneRegionSegment segment, int m, int n)
        => FixedPointToInt((long)segment.GridX + ((long)m * segment.RegionY) + ((long)n * segment.RegionX));

    private static int ComputeY(Jbig2HalftoneRegionSegment segment, int m, int n)
        => FixedPointToInt((long)segment.GridY + ((long)m * segment.RegionX) - ((long)n * segment.RegionY));

    private static int FixedPointToInt(long value)
    {
        long shifted = value >> 8;
        if (shifted > int.MaxValue)
            return int.MaxValue;
        if (shifted < int.MinValue)
            return int.MinValue;

        return (int)shifted;
    }

    private static int GetBitsPerValue(int patternCount)
    {
        int bits = 0;
        uint representedValues = 1;
        while (representedValues < (uint)patternCount)
        {
            bits++;
            representedValues <<= 1;
        }

        return bits;
    }

    private static Jbig2AdaptiveTemplatePixel[] GetAdaptiveTemplatePixels(int template)
        => template switch
        {
            0 =>
            [
                new(3, -1),
                new(-3, -1),
                new(2, -2),
                new(-2, -2),
            ],
            1 => [new(3, -1)],
            2 or 3 => [new(2, -1)],
            _ => throw new NotSupportedException($"JBIG2 halftone template {template} is not supported"),
        };

    private static void ValidateInputs(
        Jbig2HalftoneRegionSegment segment,
        IReadOnlyList<Jbig2Bitmap> patterns)
    {
        if (patterns.Count == 0)
            throw new InvalidOperationException("JBIG2 halftone region requires a pattern dictionary");
        if (segment.Region.BitmapWidth > int.MaxValue || segment.Region.BitmapHeight > int.MaxValue)
            throw new InvalidOperationException("JBIG2 halftone region dimensions exceed supported limits");
        if (segment.GridWidth > int.MaxValue || segment.GridHeight > int.MaxValue)
            throw new InvalidOperationException("JBIG2 halftone grid dimensions exceed supported limits");
        if (segment.Region.BitmapWidth == 0 || segment.Region.BitmapHeight == 0)
            throw new InvalidOperationException("Invalid JBIG2 halftone region dimensions");
        if (segment.GridWidth == 0 || segment.GridHeight == 0)
            throw new InvalidOperationException("Invalid JBIG2 halftone grid dimensions");
    }
}
