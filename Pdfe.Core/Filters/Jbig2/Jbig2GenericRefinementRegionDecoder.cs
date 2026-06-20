using System;
using System.Collections.Generic;

namespace Pdfe.Core.Filters.Jbig2;

internal static class Jbig2GenericRefinementRegionDecoder
{
    // Generic refinement regions share the arithmetic context space used by page-level
    // region decoders (including line-typical-prediction contexts such as 0x9B25).
    // Using the larger generic-region table avoids out-of-range context access when
    // TPGRON is enabled and still covers all per-pixel context indices.
    public const int ContextCount = 65536;

    public static Jbig2Bitmap Decode(
        IJbig2ArithmeticDecoder decoder,
        int width,
        int height,
        int template,
        bool typicalPredictionGenericRefinementOn,
        Jbig2Bitmap referenceBitmap,
        int referenceDx,
        int referenceDy,
        IReadOnlyList<Jbig2AdaptiveTemplatePixel> adaptiveTemplatePixels,
        int contextBase = 0)
    {
        if (decoder == null)
            throw new ArgumentNullException(nameof(decoder));
        if (referenceBitmap == null)
            throw new ArgumentNullException(nameof(referenceBitmap));
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Invalid JBIG2 generic refinement region dimensions");
        if (template is < 0 or > 1)
            throw new NotSupportedException($"JBIG2 generic refinement region template {template} is not supported");
        if (template == 0 && adaptiveTemplatePixels.Count != 2)
            throw new InvalidOperationException("JBIG2 generic refinement template 0 requires two adaptive template pixels");
        if (template == 1 && adaptiveTemplatePixels.Count != 0)
            throw new InvalidOperationException("JBIG2 generic refinement template 1 must not carry adaptive template pixels");

        var bitmap = new Jbig2Bitmap(width, height);
        bool[] overrides = GetTemplate0OverrideFlags(template, adaptiveTemplatePixels);
        int lineTypicalPrediction = 0;

        for (int y = 0; y < height; y++)
        {
            if (typicalPredictionGenericRefinementOn)
            {
                lineTypicalPrediction ^= DecodeTypicalPredictionLineToggle(
                    decoder,
                    template,
                    contextBase) ? 1 : 0;
            }

            if (lineTypicalPrediction == 1)
            {
                if (template == 1)
                    DecodeTypicalPredictionLineTemplate1(decoder, bitmap, referenceBitmap, width, y, referenceDx, referenceDy, contextBase);
                else
                    DecodeTypicalPredictionLineTemplate0(decoder, bitmap, referenceBitmap, width, y, referenceDx, referenceDy, contextBase, overrides, adaptiveTemplatePixels);
                continue;
            }

            for (int x = 0; x < width; x++)
            {
                DecodePixel(decoder, bitmap, referenceBitmap, x, y, template, contextBase, adaptiveTemplatePixels, overrides, referenceDx, referenceDy);
            }
        }

        return bitmap;
    }

    private static void DecodePixel(
        IJbig2ArithmeticDecoder decoder,
        Jbig2Bitmap bitmap,
        Jbig2Bitmap referenceBitmap,
        int x,
        int y,
        int template,
        int contextBase,
        IReadOnlyList<Jbig2AdaptiveTemplatePixel> adaptiveTemplatePixels,
        bool[] overrides,
        int referenceDx,
        int referenceDy)
    {
        int context = template == 0
            ? BuildTemplate0Context(bitmap, referenceBitmap, x, y, referenceDx, referenceDy)
            : BuildTemplate1Context(bitmap, referenceBitmap, x, y, referenceDx, referenceDy);
        if (template == 0)
            context = ApplyTemplate0Overrides(
                bitmap,
                referenceBitmap,
                context,
                x,
                y,
                referenceDx,
                referenceDy,
                adaptiveTemplatePixels,
                overrides);

        int decodeContext = contextBase + context;
        int bit = decoder.Decode(ref decodeContext) ? 1 : 0;
        bitmap.SetPixel(x, y, bit != 0);
    }

    private static bool DecodeTypicalPredictionLineToggle(
        IJbig2ArithmeticDecoder decoder,
        int template,
        int contextBase)
    {
        int context = contextBase + (template switch
        {
            // Spec §6.3.5.6: template-specific TPGRON context seeds.
            0 => 0x0100,
            1 => 0x0008,
            _ => throw new ArgumentOutOfRangeException(nameof(template)),
        });

        return decoder.Decode(ref context);
    }

    private static void CopyLineAbove(Jbig2Bitmap bitmap, int line)
    {
        int destination = line * bitmap.Stride;
        int source = destination - bitmap.Stride;
        Array.Copy(bitmap.Data, source, bitmap.Data, destination, bitmap.Stride);
    }

    private static void DecodeTypicalPredictionLineTemplate1(
        IJbig2ArithmeticDecoder decoder,
        Jbig2Bitmap bitmap,
        Jbig2Bitmap referenceBitmap,
        int width,
        int line,
        int referenceDx,
        int referenceDy,
        int contextBase)
    {
        for (int x = 0; x < width; x++)
        {
            if (IsReferenceNeighborhoodUniform(referenceBitmap, x, line, referenceDx, referenceDy))
            {
                bitmap.SetPixel(x, line, GetReferenceBit(referenceBitmap, x, line, referenceDx, referenceDy) != 0);
                continue;
            }

            int context = BuildTemplate1Context(bitmap, referenceBitmap, x, line, referenceDx, referenceDy);
            int decodeContext = contextBase + context;
            int bit = decoder.Decode(ref decodeContext) ? 1 : 0;
            bitmap.SetPixel(x, line, bit != 0);
        }
    }

    private static void DecodeTypicalPredictionLineTemplate0(
        IJbig2ArithmeticDecoder decoder,
        Jbig2Bitmap bitmap,
        Jbig2Bitmap referenceBitmap,
        int width,
        int line,
        int referenceDx,
        int referenceDy,
        int contextBase,
        bool[] overrides,
        IReadOnlyList<Jbig2AdaptiveTemplatePixel> adaptiveTemplatePixels)
    {
        for (int x = 0; x < width; x++)
        {
            if (IsReferenceNeighborhoodUniform(referenceBitmap, x, line, referenceDx, referenceDy))
            {
                bitmap.SetPixel(x, line, GetReferenceBit(referenceBitmap, x, line, referenceDx, referenceDy) != 0);
                continue;
            }

            int context = BuildTemplate0Context(bitmap, referenceBitmap, x, line, referenceDx, referenceDy);
            context = ApplyTemplate0Overrides(bitmap, referenceBitmap, context, x, line, referenceDx, referenceDy, adaptiveTemplatePixels, overrides);
            int decodeContext = contextBase + context;
            int bit = decoder.Decode(ref decodeContext) ? 1 : 0;
            bitmap.SetPixel(x, line, bit != 0);
        }
    }

    private static bool IsReferenceNeighborhoodUniform(
        Jbig2Bitmap referenceBitmap,
        int x,
        int y,
        int referenceDx,
        int referenceDy)
    {
        int center = GetReferenceBit(referenceBitmap, x, y, referenceDx, referenceDy);

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (GetReferenceBit(referenceBitmap, x + dx, y + dy, referenceDx, referenceDy) != center)
                    return false;
            }
        }

        return true;
    }

    private static int BuildTemplate0Context(
        Jbig2Bitmap bitmap,
        Jbig2Bitmap referenceBitmap,
        int x,
        int y,
        int referenceDx,
        int referenceDy)
    {
        int c1 = BuildReferenceThree(referenceBitmap, x, y - 1, referenceDx, referenceDy);
        int c2 = BuildReferenceThree(referenceBitmap, x, y, referenceDx, referenceDy);
        int c3 = BuildReferenceThree(referenceBitmap, x, y + 1, referenceDx, referenceDy);
        int c4 = BuildRegionThree(bitmap, x, y - 1);
        int c5 = bitmap.GetPixel(x - 1, y) ? 1 : 0;
        return (c1 << 10) | (c2 << 7) | (c3 << 4) | (c4 << 1) | c5;
    }

    private static int BuildTemplate1Context(
        Jbig2Bitmap bitmap,
        Jbig2Bitmap referenceBitmap,
        int x,
        int y,
        int referenceDx,
        int referenceDy)
        => (Pixel(bitmap, x - 1, y - 1) << 9)
            | (Pixel(bitmap, x, y - 1) << 8)
            | (Pixel(bitmap, x + 1, y - 1) << 7)
            | (Pixel(bitmap, x - 1, y) << 6)
            | (GetReferenceBit(referenceBitmap, x, y - 1, referenceDx, referenceDy) << 5)
            | (GetReferenceBit(referenceBitmap, x - 1, y, referenceDx, referenceDy) << 4)
            | (GetReferenceBit(referenceBitmap, x, y, referenceDx, referenceDy) << 3)
            | (GetReferenceBit(referenceBitmap, x + 1, y, referenceDx, referenceDy) << 2)
            | (GetReferenceBit(referenceBitmap, x, y + 1, referenceDx, referenceDy) << 1)
            | GetReferenceBit(referenceBitmap, x + 1, y + 1, referenceDx, referenceDy);

    private static int BuildReferenceThree(
        Jbig2Bitmap referenceBitmap,
        int x,
        int y,
        int referenceDx,
        int referenceDy)
        => (GetReferenceBit(referenceBitmap, x - 1, y, referenceDx, referenceDy) << 2)
            | (GetReferenceBit(referenceBitmap, x, y, referenceDx, referenceDy) << 1)
            | GetReferenceBit(referenceBitmap, x + 1, y, referenceDx, referenceDy);

    private static int BuildRegionThree(Jbig2Bitmap bitmap, int x, int y)
        => (Pixel(bitmap, x - 1, y) << 2)
            | (Pixel(bitmap, x, y) << 1)
            | Pixel(bitmap, x + 1, y);

    private static int ApplyTemplate0Overrides(
        Jbig2Bitmap bitmap,
        Jbig2Bitmap referenceBitmap,
        int context,
        int x,
        int y,
        int referenceDx,
        int referenceDy,
        IReadOnlyList<Jbig2AdaptiveTemplatePixel> adaptiveTemplatePixels,
        bool[] overrides)
    {
        if (overrides[0])
        {
            context &= 0xFFF7;
            context |= Pixel(bitmap, x + adaptiveTemplatePixels[0].X, y + adaptiveTemplatePixels[0].Y) << 3;
        }

        if (overrides[1])
        {
            context &= 0xEFFF;
            context |= Pixel(referenceBitmap, x + adaptiveTemplatePixels[1].X + referenceDx, y + adaptiveTemplatePixels[1].Y + referenceDy) << 12;
        }

        return context;
    }

    private static bool[] GetTemplate0OverrideFlags(int template, IReadOnlyList<Jbig2AdaptiveTemplatePixel> pixels)
    {
        if (template != 0)
            return Array.Empty<bool>();

        return
        [
            pixels[0] != new Jbig2AdaptiveTemplatePixel(-1, -1),
            pixels[1] != new Jbig2AdaptiveTemplatePixel(-1, -1),
        ];
    }

    private static int GetReferenceBit(Jbig2Bitmap referenceBitmap, int x, int y, int referenceDx, int referenceDy)
        => Pixel(referenceBitmap, x - referenceDx, y - referenceDy);

    private static int Pixel(Jbig2Bitmap bitmap, int x, int y)
        => bitmap.GetPixel(x, y) ? 1 : 0;
}
