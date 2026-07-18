using System;
using System.Collections.Generic;
using System.Linq;

namespace Excise.Core.Filters.Jbig2;

internal static class Jbig2ArithmeticGenericRegionDecoder
{
    public const int ContextCount = 65536;

    public static Jbig2Bitmap Decode(
        IJbig2ArithmeticDecoder decoder,
        int width,
        int height,
        int template,
        IReadOnlyList<Jbig2AdaptiveTemplatePixel> adaptiveTemplatePixels,
        int contextBase = 0,
        bool typicalPredictionGenericDecodingOn = false,
        Jbig2Bitmap? skipBitmap = null)
    {
        if (decoder == null)
            throw new ArgumentNullException(nameof(decoder));
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Invalid JBIG2 arithmetic generic region dimensions");
        if (template is < 0 or > 3)
            throw new NotSupportedException($"JBIG2 arithmetic generic region template {template} is not supported");

        ValidateAdaptiveTemplatePixels(template, adaptiveTemplatePixels);

        var bitmap = new Jbig2Bitmap(width, height);
        int paddedWidth = (width + 7) & ~7;
        bool[] overrideFlags = GetAdaptiveTemplatePixelOverrideFlags(template, adaptiveTemplatePixels);
        bool hasOverrides = overrideFlags.Any(value => value);
        int lineTypicalPrediction = 0;

        for (int line = 0; line < height; line++)
        {
            if (typicalPredictionGenericDecodingOn)
                lineTypicalPrediction ^= DecodeTypicalPredictionLineToggle(decoder, template, contextBase) ? 1 : 0;

            if (lineTypicalPrediction == 1)
            {
                if (line > 0)
                    CopyLineAbove(bitmap, line);
                continue;
            }

            DecodeLine(
                decoder,
                bitmap,
                line,
                paddedWidth,
                template,
                adaptiveTemplatePixels,
                overrideFlags,
                hasOverrides,
                contextBase,
                skipBitmap);
        }

        return bitmap;
    }

    private static bool DecodeTypicalPredictionLineToggle(
        IJbig2ArithmeticDecoder decoder,
        int template,
        int contextBase)
    {
        int context = contextBase + (template switch
        {
            0 => 0x9B25,
            1 => 0x0795,
            2 => 0x00E5,
            3 => 0x0195,
            _ => throw new ArgumentOutOfRangeException(nameof(template)),
        });
        return decoder.Decode(ref context);
    }

    private static void DecodeLine(
        IJbig2ArithmeticDecoder decoder,
        Jbig2Bitmap bitmap,
        int line,
        int paddedWidth,
        int template,
        IReadOnlyList<Jbig2AdaptiveTemplatePixel> adaptiveTemplatePixels,
        bool[] overrideFlags,
        bool hasOverrides,
        int contextBase,
        Jbig2Bitmap? skipBitmap)
    {
        int byteIndex = line * bitmap.Stride;
        int previousLineIndex = byteIndex - bitmap.Stride;

        switch (template)
        {
            case 0:
                DecodeTemplate0Line(decoder, bitmap, line, paddedWidth, byteIndex, previousLineIndex, adaptiveTemplatePixels, overrideFlags, hasOverrides, contextBase, skipBitmap);
                break;
            case 1:
                DecodeTemplate1Line(decoder, bitmap, line, paddedWidth, byteIndex, previousLineIndex, adaptiveTemplatePixels, hasOverrides, contextBase, skipBitmap);
                break;
            case 2:
                DecodeTemplate2Line(decoder, bitmap, line, paddedWidth, byteIndex, previousLineIndex, adaptiveTemplatePixels, hasOverrides, contextBase, skipBitmap);
                break;
            case 3:
                DecodeTemplate3Line(decoder, bitmap, line, paddedWidth, byteIndex, previousLineIndex, adaptiveTemplatePixels, hasOverrides, contextBase, skipBitmap);
                break;
        }
    }

    private static void DecodeTemplate0Line(
        IJbig2ArithmeticDecoder decoder,
        Jbig2Bitmap bitmap,
        int line,
        int paddedWidth,
        int byteIndex,
        int previousLineIndex,
        IReadOnlyList<Jbig2AdaptiveTemplatePixel> adaptiveTemplatePixels,
        bool[] overrideFlags,
        bool hasOverrides,
        int contextBase,
        Jbig2Bitmap? skipBitmap)
    {
        int line1 = line >= 1 ? bitmap.Data[previousLineIndex] : 0;
        int line2 = line >= 2 ? bitmap.Data[previousLineIndex - bitmap.Stride] << 6 : 0;
        int context = (line1 & 0xF0) | (line2 & 0x3800);
        bool extendedTemplate = adaptiveTemplatePixels.Count == 12;

        for (int x = 0; x < paddedWidth; x += 8)
        {
            byte result = 0;
            int nextByteX = x + 8;
            int minorWidth = bitmap.Width - x > 8 ? 8 : bitmap.Width - x;

            if (line > 0)
            {
                line1 = (line1 << 8)
                    | (nextByteX < bitmap.Width ? bitmap.Data[previousLineIndex + 1] : 0);
            }

            if (line > 1)
            {
                line2 = (line2 << 8)
                    | (nextByteX < bitmap.Width ? bitmap.Data[previousLineIndex - bitmap.Stride + 1] << 6 : 0);
            }

            for (int minorX = 0; minorX < minorWidth; minorX++)
            {
                int shift = 7 - minorX;
                int activeContext = hasOverrides
                    ? ApplyTemplate0Overrides(bitmap, context, x + minorX, line, result, minorX, shift, adaptiveTemplatePixels, overrideFlags, extendedTemplate)
                    : context;
                int bit = ShouldSkipPixel(skipBitmap, x + minorX, line)
                    ? 0
                    : DecodeBit(decoder, contextBase + activeContext);
                result |= (byte)(bit << shift);
                context = ((context & 0x7BF7) << 1)
                    | bit
                    | ((line1 >> shift) & 0x10)
                    | ((line2 >> shift) & 0x800);
            }

            bitmap.Data[byteIndex++] = result;
            previousLineIndex++;
        }
    }

    private static void DecodeTemplate1Line(
        IJbig2ArithmeticDecoder decoder,
        Jbig2Bitmap bitmap,
        int line,
        int paddedWidth,
        int byteIndex,
        int previousLineIndex,
        IReadOnlyList<Jbig2AdaptiveTemplatePixel> adaptiveTemplatePixels,
        bool hasOverrides,
        int contextBase,
        Jbig2Bitmap? skipBitmap)
    {
        int line1 = line >= 1 ? bitmap.Data[previousLineIndex] : 0;
        int line2 = line >= 2 ? bitmap.Data[previousLineIndex - bitmap.Stride] << 5 : 0;
        int context = ((line1 >> 1) & 0x1F8) | ((line2 >> 1) & 0x1E00);

        for (int x = 0; x < paddedWidth; x += 8)
        {
            byte result = 0;
            int nextByteX = x + 8;
            int minorWidth = bitmap.Width - x > 8 ? 8 : bitmap.Width - x;

            if (line >= 1)
            {
                line1 = (line1 << 8)
                    | (nextByteX < bitmap.Width ? bitmap.Data[previousLineIndex + 1] : 0);
            }

            if (line >= 2)
            {
                line2 = (line2 << 8)
                    | (nextByteX < bitmap.Width ? bitmap.Data[previousLineIndex - bitmap.Stride + 1] << 5 : 0);
            }

            for (int minorX = 0; minorX < minorWidth; minorX++)
            {
                int activeContext = hasOverrides
                    ? ApplySingleAdaptivePixelOverride(bitmap, context & 0x1FF7, x + minorX, line, result, minorX, adaptiveTemplatePixels[0], 3)
                    : context;
                int bit = ShouldSkipPixel(skipBitmap, x + minorX, line)
                    ? 0
                    : DecodeBit(decoder, contextBase + activeContext);
                result |= (byte)(bit << (7 - minorX));

                int shift = 8 - minorX;
                context = ((context & 0xEFB) << 1)
                    | bit
                    | ((line1 >> shift) & 0x8)
                    | ((line2 >> shift) & 0x200);
            }

            bitmap.Data[byteIndex++] = result;
            previousLineIndex++;
        }
    }

    private static void DecodeTemplate2Line(
        IJbig2ArithmeticDecoder decoder,
        Jbig2Bitmap bitmap,
        int line,
        int paddedWidth,
        int byteIndex,
        int previousLineIndex,
        IReadOnlyList<Jbig2AdaptiveTemplatePixel> adaptiveTemplatePixels,
        bool hasOverrides,
        int contextBase,
        Jbig2Bitmap? skipBitmap)
    {
        int line1 = line >= 1 ? bitmap.Data[previousLineIndex] : 0;
        int line2 = line >= 2 ? bitmap.Data[previousLineIndex - bitmap.Stride] << 4 : 0;
        int context = ((line1 >> 3) & 0x7C) | ((line2 >> 3) & 0x380);

        for (int x = 0; x < paddedWidth; x += 8)
        {
            byte result = 0;
            int nextByteX = x + 8;
            int minorWidth = bitmap.Width - x > 8 ? 8 : bitmap.Width - x;

            if (line >= 1)
            {
                line1 = (line1 << 8)
                    | (nextByteX < bitmap.Width ? bitmap.Data[previousLineIndex + 1] : 0);
            }

            if (line >= 2)
            {
                line2 = (line2 << 8)
                    | (nextByteX < bitmap.Width ? bitmap.Data[previousLineIndex - bitmap.Stride + 1] << 4 : 0);
            }

            for (int minorX = 0; minorX < minorWidth; minorX++)
            {
                int activeContext = hasOverrides
                    ? ApplySingleAdaptivePixelOverride(bitmap, context & 0x3FB, x + minorX, line, result, minorX, adaptiveTemplatePixels[0], 2)
                    : context;
                int bit = ShouldSkipPixel(skipBitmap, x + minorX, line)
                    ? 0
                    : DecodeBit(decoder, contextBase + activeContext);
                result |= (byte)(bit << (7 - minorX));

                int shift = 10 - minorX;
                context = ((context & 0x1BD) << 1)
                    | bit
                    | ((line1 >> shift) & 0x4)
                    | ((line2 >> shift) & 0x80);
            }

            bitmap.Data[byteIndex++] = result;
            previousLineIndex++;
        }
    }

    private static void DecodeTemplate3Line(
        IJbig2ArithmeticDecoder decoder,
        Jbig2Bitmap bitmap,
        int line,
        int paddedWidth,
        int byteIndex,
        int previousLineIndex,
        IReadOnlyList<Jbig2AdaptiveTemplatePixel> adaptiveTemplatePixels,
        bool hasOverrides,
        int contextBase,
        Jbig2Bitmap? skipBitmap)
    {
        int line1 = line >= 1 ? bitmap.Data[previousLineIndex] : 0;
        int context = (line1 >> 1) & 0x70;

        for (int x = 0; x < paddedWidth; x += 8)
        {
            byte result = 0;
            int nextByteX = x + 8;
            int minorWidth = bitmap.Width - x > 8 ? 8 : bitmap.Width - x;

            if (line >= 1)
            {
                line1 = (line1 << 8)
                    | (nextByteX < bitmap.Width ? bitmap.Data[previousLineIndex + 1] : 0);
            }

            for (int minorX = 0; minorX < minorWidth; minorX++)
            {
                int activeContext = hasOverrides
                    ? ApplySingleAdaptivePixelOverride(bitmap, context & 0x3EF, x + minorX, line, result, minorX, adaptiveTemplatePixels[0], 4)
                    : context;
                int bit = ShouldSkipPixel(skipBitmap, x + minorX, line)
                    ? 0
                    : DecodeBit(decoder, contextBase + activeContext);
                result |= (byte)(bit << (7 - minorX));
                context = ((context & 0x1F7) << 1)
                    | bit
                    | ((line1 >> (8 - minorX)) & 0x010);
            }

            bitmap.Data[byteIndex++] = result;
            previousLineIndex++;
        }
    }

    private static bool ShouldSkipPixel(Jbig2Bitmap? skipBitmap, int x, int y)
        => skipBitmap?.GetPixel(x, y) == true;

    private static int DecodeBit(IJbig2ArithmeticDecoder decoder, int context)
        => decoder.Decode(ref context) ? 1 : 0;

    private static int ApplyTemplate0Overrides(
        Jbig2Bitmap bitmap,
        int context,
        int x,
        int y,
        byte result,
        int minorX,
        int currentBitShift,
        IReadOnlyList<Jbig2AdaptiveTemplatePixel> pixels,
        bool[] overrideFlags,
        bool extendedTemplate)
    {
        int[] masks = extendedTemplate
            ? [0xFFFD, 0xDFFF, 0xFDFF, 0xBFFF, 0xEFFF, 0xFFDF, 0xFFFB, 0xFFF7, 0xF7FF, 0xFFEF, 0x7FFF, 0xFBFF]
            : [0xFFEF, 0xFBFF, 0xF7FF, 0x7FFF];
        int[] bitPositions = extendedTemplate
            ? [1, 13, 9, 14, 12, 5, 2, 3, 11, 4, 15, 10]
            : [4, 10, 11, 15];

        for (int i = 0; i < pixels.Count; i++)
        {
            if (!overrideFlags[i])
                continue;

            context &= masks[i];
            context |= GetAdaptiveTemplatePixelValue(bitmap, x, y, result, minorX, currentBitShift, pixels[i]) << bitPositions[i];
        }

        return context;
    }

    private static int ApplySingleAdaptivePixelOverride(
        Jbig2Bitmap bitmap,
        int context,
        int x,
        int y,
        byte result,
        int minorX,
        Jbig2AdaptiveTemplatePixel pixel,
        int bitPosition)
        => context | (GetAdaptiveTemplatePixelValue(bitmap, x, y, result, minorX, 7 - minorX, pixel) << bitPosition);

    private static int GetAdaptiveTemplatePixelValue(
        Jbig2Bitmap bitmap,
        int x,
        int y,
        byte result,
        int minorX,
        int currentBitShift,
        Jbig2AdaptiveTemplatePixel pixel)
    {
        if (pixel.Y == 0 && pixel.X >= -minorX)
        {
            int shift = currentBitShift - pixel.X;
            if ((uint)shift < 8)
                return (result >> shift) & 0x01;
        }

        return bitmap.GetPixel(x + pixel.X, y + pixel.Y) ? 1 : 0;
    }

    private static void CopyLineAbove(Jbig2Bitmap bitmap, int line)
    {
        int destination = line * bitmap.Stride;
        int source = destination - bitmap.Stride;
        Array.Copy(bitmap.Data, source, bitmap.Data, destination, bitmap.Stride);
    }

    private static bool[] GetAdaptiveTemplatePixelOverrideFlags(int template, IReadOnlyList<Jbig2AdaptiveTemplatePixel> pixels)
    {
        var defaults = GetDefaultAdaptiveTemplatePixels(template, pixels.Count == 12);
        var flags = new bool[pixels.Count];
        for (int i = 0; i < pixels.Count; i++)
            flags[i] = pixels[i] != defaults[i];

        return flags;
    }

    private static void ValidateAdaptiveTemplatePixels(int template, IReadOnlyList<Jbig2AdaptiveTemplatePixel> pixels)
    {
        int expectedCount = template == 0
            ? pixels.Count == 12 ? 12 : 4
            : 1;
        if (pixels.Count != expectedCount)
            throw new InvalidOperationException("Invalid JBIG2 arithmetic generic-region adaptive template pixel count");
    }

    private static Jbig2AdaptiveTemplatePixel[] GetDefaultAdaptiveTemplatePixels(int template, bool extendedTemplate0)
    {
        if (template == 0 && !extendedTemplate0)
        {
            return
            [
                new(3, -1),
                new(-3, -1),
                new(2, -2),
                new(-2, -2),
            ];
        }

        if (template == 0)
        {
            return
            [
                new(-2, 0),
                new(0, -2),
                new(-2, -1),
                new(-1, -2),
                new(1, -2),
                new(2, -1),
                new(-3, 0),
                new(-4, 0),
                new(2, -2),
                new(3, -1),
                new(-2, -2),
                new(-3, -1),
            ];
        }

        return template switch
        {
            1 => [new(3, -1)],
            2 => [new(2, -1)],
            3 => [new(2, -1)],
            _ => throw new ArgumentOutOfRangeException(nameof(template)),
        };
    }
}
