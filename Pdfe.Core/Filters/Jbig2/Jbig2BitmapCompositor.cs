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

        for (int sourceY = 0; sourceY < sourceHeight; sourceY++)
        {
            int destinationY = y + sourceY;
            if (destinationY < 0 || destinationY >= destinationHeight)
                continue;

            for (int sourceX = 0; sourceX < sourceWidth; sourceX++)
            {
                int destinationX = x + sourceX;
                if (destinationX < 0 || destinationX >= destinationWidth)
                    continue;

                bool sourcePixel = GetBit(source, sourceStride, sourceX, sourceY);
                bool destinationPixel = GetBit(destination, destinationStride, destinationX, destinationY);
                SetBit(destination, destinationStride, destinationX, destinationY,
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

    private static bool GetBit(byte[] data, int stride, int x, int y)
    {
        int byteIndex = (y * stride) + (x / 8);
        int bitIndex = 7 - (x % 8);
        return (data[byteIndex] & (1 << bitIndex)) != 0;
    }

    private static void SetBit(byte[] data, int stride, int x, int y, bool value)
    {
        int byteIndex = (y * stride) + (x / 8);
        int bitIndex = 7 - (x % 8);
        byte mask = (byte)(1 << bitIndex);

        if (value)
            data[byteIndex] |= mask;
        else
            data[byteIndex] &= (byte)~mask;
    }
}
