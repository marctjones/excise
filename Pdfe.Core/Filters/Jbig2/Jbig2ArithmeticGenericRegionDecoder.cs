using System;
using System.Collections.Generic;

namespace Pdfe.Core.Filters.Jbig2;

internal static class Jbig2ArithmeticGenericRegionDecoder
{
    public const int ContextCount = 65536;

    public static Jbig2Bitmap Decode(
        IJbig2ArithmeticDecoder decoder,
        int width,
        int height,
        int template,
        IReadOnlyList<Jbig2AdaptiveTemplatePixel> adaptiveTemplatePixels,
        int contextBase = 0)
    {
        if (decoder == null)
            throw new ArgumentNullException(nameof(decoder));
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Invalid JBIG2 arithmetic generic region dimensions");
        if (template != 0)
            throw new NotSupportedException($"JBIG2 arithmetic generic region template {template} is not yet supported");
        if (!UsesDefaultTemplate0AdaptivePixels(adaptiveTemplatePixels))
            throw new NotSupportedException("Custom JBIG2 arithmetic generic-region adaptive template pixels are not yet supported");

        return DecodeTemplate0Default(decoder, width, height, contextBase);
    }

    private static Jbig2Bitmap DecodeTemplate0Default(
        IJbig2ArithmeticDecoder decoder,
        int width,
        int height,
        int contextBase)
    {
        var bitmap = new Jbig2Bitmap(width, height);
        int paddedWidth = (width + 7) & ~7;

        for (int line = 0; line < height; line++)
        {
            int byteIndex = line * bitmap.Stride;
            int previousLineIndex = byteIndex - bitmap.Stride;

            int line1 = line >= 1
                ? bitmap.Data[previousLineIndex]
                : 0;
            int line2 = line >= 2
                ? bitmap.Data[previousLineIndex - bitmap.Stride] << 6
                : 0;
            int context = (line1 & 0xF0) | (line2 & 0x3800);

            for (int x = 0; x < paddedWidth; x += 8)
            {
                byte result = 0;
                int nextByteX = x + 8;
                int minorWidth = width - x > 8 ? 8 : width - x;

                if (line > 0)
                {
                    line1 = (line1 << 8)
                        | (nextByteX < width ? bitmap.Data[previousLineIndex + 1] : 0);
                }

                if (line > 1)
                {
                    line2 = (line2 << 8)
                        | (nextByteX < width ? bitmap.Data[previousLineIndex - bitmap.Stride + 1] << 6 : 0);
                }

                for (int minorX = 0; minorX < minorWidth; minorX++)
                {
                    int shift = 7 - minorX;
                    int decodeContext = contextBase + context;
                    int bit = decoder.Decode(ref decodeContext) ? 1 : 0;
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

        return bitmap;
    }

    private static bool UsesDefaultTemplate0AdaptivePixels(IReadOnlyList<Jbig2AdaptiveTemplatePixel> pixels)
    {
        var defaults = new[]
        {
            new Jbig2AdaptiveTemplatePixel(3, -1),
            new Jbig2AdaptiveTemplatePixel(-3, -1),
            new Jbig2AdaptiveTemplatePixel(2, -2),
            new Jbig2AdaptiveTemplatePixel(-2, -2),
        };

        if (pixels.Count != defaults.Length)
            return false;

        for (int i = 0; i < defaults.Length; i++)
        {
            if (pixels[i] != defaults[i])
                return false;
        }

        return true;
    }
}
