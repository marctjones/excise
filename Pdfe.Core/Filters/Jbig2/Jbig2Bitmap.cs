using System;

namespace Pdfe.Core.Filters.Jbig2;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal sealed class Jbig2Bitmap
{
    public Jbig2Bitmap(int width, int height)
        : this(width, height, new byte[CheckedLength(width, height)])
    {
    }

    public Jbig2Bitmap(int width, int height, byte[] data)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Bitmap dimensions must be positive");

        Width = width;
        Height = height;
        Stride = (width + 7) / 8;
        Data = data ?? throw new ArgumentNullException(nameof(data));
        if (Data.Length < Stride * Height)
            throw new ArgumentException("Bitmap data is shorter than its dimensions require", nameof(data));
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Data { get; }

    public bool GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return false;

        int byteIndex = (y * Stride) + (x / 8);
        int bitIndex = 7 - (x % 8);
        return (Data[byteIndex] & (1 << bitIndex)) != 0;
    }

    public void SetPixel(int x, int y, bool value)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return;

        int byteIndex = (y * Stride) + (x / 8);
        int bitIndex = 7 - (x % 8);
        byte mask = (byte)(1 << bitIndex);

        if (value)
            Data[byteIndex] |= mask;
        else
            Data[byteIndex] &= (byte)~mask;
    }

    public void Fill(bool value)
        => Array.Fill(Data, value ? (byte)0xFF : (byte)0x00, 0, Stride * Height);

    private static int CheckedLength(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Bitmap dimensions must be positive");

        checked
        {
            return ((width + 7) / 8) * height;
        }
    }
}
