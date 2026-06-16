using System;

namespace Pdfe.Core.Filters.Jbig2;

internal static class Jbig2BitmapCompositor
{
    public static void Composite(
        byte[] destination,
        int destinationWidth,
        int destinationHeight,
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int x,
        int y,
        Jbig2CombinationOperator combinationOperator)
    {
        if (destinationWidth <= 0 || destinationHeight <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
            throw new ArgumentException("Bitmap dimensions must be positive");

        int destinationStride = (destinationWidth + 7) / 8;
        int sourceStride = (sourceWidth + 7) / 8;
        if (destination.Length < destinationStride * destinationHeight)
            throw new ArgumentException("Destination bitmap is shorter than its dimensions require", nameof(destination));
        if (source.Length < sourceStride * sourceHeight)
            throw new ArgumentException("Source bitmap is shorter than its dimensions require", nameof(source));

        Composite(
            new Jbig2Bitmap(destinationWidth, destinationHeight, destination),
            new Jbig2Bitmap(sourceWidth, sourceHeight, source),
            x,
            y,
            combinationOperator);
    }

    public static void Composite(
        Jbig2Bitmap destination,
        Jbig2Bitmap source,
        int x,
        int y,
        Jbig2CombinationOperator combinationOperator)
    {
        if (destination == null)
            throw new ArgumentNullException(nameof(destination));
        if (source == null)
            throw new ArgumentNullException(nameof(source));

        for (int sourceY = 0; sourceY < source.Height; sourceY++)
        {
            int destinationY = y + sourceY;
            if (destinationY < 0 || destinationY >= destination.Height)
                continue;

            for (int sourceX = 0; sourceX < source.Width; sourceX++)
            {
                int destinationX = x + sourceX;
                if (destinationX < 0 || destinationX >= destination.Width)
                    continue;

                bool sourcePixel = source.GetPixel(sourceX, sourceY);
                bool destinationPixel = destination.GetPixel(destinationX, destinationY);
                destination.SetPixel(destinationX, destinationY,
                    Combine(destinationPixel, sourcePixel, combinationOperator));
            }
        }
    }

    private static bool Combine(bool destination, bool source, Jbig2CombinationOperator combinationOperator)
        => combinationOperator switch
        {
            Jbig2CombinationOperator.Or => destination | source,
            Jbig2CombinationOperator.And => destination & source,
            Jbig2CombinationOperator.Xor => destination ^ source,
            Jbig2CombinationOperator.Xnor => !(destination ^ source),
            Jbig2CombinationOperator.Replace => source,
            _ => source,
        };
}
