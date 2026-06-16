using System;
using System.Collections.Generic;

namespace Pdfe.Core.Filters.Jbig2;

internal static class Jbig2GenericRefinementRegionDecoder
{
    public const int ContextCount = 8192;

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
        if (typicalPredictionGenericRefinementOn)
            throw new NotSupportedException("JBIG2 generic refinement TPGRON is not yet supported");
        if (template == 0 && adaptiveTemplatePixels.Count != 2)
            throw new InvalidOperationException("JBIG2 generic refinement template 0 requires two adaptive template pixels");
        if (template == 1 && adaptiveTemplatePixels.Count != 0)
            throw new InvalidOperationException("JBIG2 generic refinement template 1 must not carry adaptive template pixels");

        var bitmap = new Jbig2Bitmap(width, height);
        bool[] overrides = GetTemplate0OverrideFlags(template, adaptiveTemplatePixels);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int context = template == 0
                    ? BuildTemplate0Context(bitmap, referenceBitmap, x, y, referenceDx, referenceDy)
                    : BuildTemplate1Context(bitmap, referenceBitmap, x, y, referenceDx, referenceDy);
                if (template == 0)
                    context = ApplyTemplate0Overrides(bitmap, referenceBitmap, context, x, y, referenceDx, referenceDy, adaptiveTemplatePixels, overrides);

                int decodeContext = contextBase + context;
                int bit = decoder.Decode(ref decodeContext) ? 1 : 0;
                bitmap.SetPixel(x, y, bit != 0);
            }
        }

        return bitmap;
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
