using System;
using System.Runtime.InteropServices;
using AwesomeAssertions;
using Excise.Avalonia.Imaging;
using SkiaSharp;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// #1496: <see cref="SkiaInterop.CopyPixelsAsBgraPremul"/> replaced
/// <c>SKBitmap.CopyTo(Bgra8888)</c>, which allocated three page-sized buffers
/// per render. The replacement must write exactly the bytes the old path wrote,
/// so every case here compares against the old path run on the same input.
/// </summary>
public class SkiaInteropConversionTests
{
    private const int Width = 173;   // odd sizes: no stride alignment luck
    private const int Height = 91;

    /// <summary>The conversion as it was before #1496, kept as the reference.</summary>
    private static byte[] ConvertTheOldWay(SKBitmap source)
    {
        using var owned = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        source.CopyTo(owned, SKColorType.Bgra8888).Should().BeTrue("the reference path must succeed");
        return Rows(owned.GetPixels(), owned.RowBytes, source.Width, source.Height);
    }

    private static byte[] ConvertTheNewWay(SKBitmap source, int destinationRowBytes)
    {
        var buffer = Marshal.AllocHGlobal(destinationRowBytes * source.Height);
        try
        {
            SkiaInterop.CopyPixelsAsBgraPremul(source, buffer, destinationRowBytes).Should().BeTrue();
            return Rows(buffer, destinationRowBytes, source.Width, source.Height);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The visible bytes of each row (width x 4), stride padding excluded.</summary>
    private static byte[] Rows(IntPtr pixels, int rowBytes, int width, int height)
    {
        var result = new byte[width * 4 * height];
        for (int y = 0; y < height; y++)
            Marshal.Copy(pixels + y * rowBytes, result, y * width * 4, width * 4);
        return result;
    }

    /// <summary>Every premultiplied value, including alpha 0 and channels equal to alpha.</summary>
    private static SKBitmap RandomPremul(SKColorType colorType, int seed)
    {
        var bitmap = new SKBitmap(Width, Height, colorType, SKAlphaType.Premul);
        var rng = new Random(seed);
        var bytes = new byte[bitmap.RowBytes * Height];
        for (int i = 0; i < Width * Height; i++)
        {
            int o = (i / Width) * bitmap.RowBytes + (i % Width) * 4;
            byte a = (byte)(i % 7 == 0 ? 0 : i % 5 == 0 ? 255 : rng.Next(256));
            bytes[o] = (byte)rng.Next(a + 1);
            bytes[o + 1] = (byte)(i % 3 == 0 ? a : rng.Next(a + 1));
            bytes[o + 2] = (byte)rng.Next(a + 1);
            bytes[o + 3] = a;
        }
        Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
        return bitmap;
    }

    /// <summary>Anti-aliased text and paths on a white page, as the renderer produces.</summary>
    private static SKBitmap RenderedLikePage()
    {
        var bitmap = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(20, 40, 200, 180) };
        canvas.DrawCircle(60, 45, 37.3f, paint);
        paint.Color = new SKColor(200, 10, 30, 90);
        canvas.DrawRect(new SKRect(30.5f, 10.25f, 150.75f, 70.1f), paint);
        using var font = new SKFont(SKTypeface.Default, 17);
        paint.Color = SKColors.Black;
        canvas.DrawText("Line 1a, Form 1040", 3, 80, font, paint);
        using var paper = new SKPaint { Color = SKColors.White, BlendMode = SKBlendMode.DstOver };
        canvas.DrawRect(new SKRect(0, 0, Width, Height / 2), paper);
        return bitmap;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RandomPremulRgba_MatchesTheOldCopyToPath(int seed)
    {
        using var source = RandomPremul(SKColorType.Rgba8888, seed);
        ConvertTheNewWay(source, source.RowBytes).Should().Equal(ConvertTheOldWay(source));
    }

    [Fact]
    public void RenderedRgbaPage_MatchesTheOldCopyToPath()
    {
        using var source = RenderedLikePage();
        var expected = ConvertTheOldWay(source);
        expected.Should().Contain(b => b != 0 && b != 255, "the fixture must have anti-aliased edges");
        ConvertTheNewWay(source, source.RowBytes).Should().Equal(expected);
    }

    [Fact]
    public void Subset_ConvertsOnlyTheSubsetPixels()
    {
        // SliceBandIntoCells hands over ExtractSubset views of a band render.
        using var band = RenderedLikePage();
        using var sub = new SKBitmap();
        band.ExtractSubset(sub, new SKRectI(17, 9, 17 + 64, 9 + 50)).Should().BeTrue();
        var expected = ConvertTheOldWay(sub);
        ConvertTheNewWay(sub, sub.Width * 4).Should().Equal(expected);
    }

    [Fact]
    public void WiderDestinationStride_LeavesRowsAligned()
    {
        using var source = RandomPremul(SKColorType.Rgba8888, 7);
        ConvertTheNewWay(source, source.RowBytes + 12).Should().Equal(ConvertTheOldWay(source));
    }

    [Fact]
    public void BgraPremulSource_IsCopiedVerbatim()
    {
        using var source = RandomPremul(SKColorType.Bgra8888, 11);
        var expected = Rows(source.GetPixels(), source.RowBytes, Width, Height);
        ConvertTheNewWay(source, source.RowBytes).Should().Equal(expected);
        ConvertTheNewWay(source, source.RowBytes + 8).Should().Equal(expected);
    }

    [Fact]
    public void BitmapWithoutPixels_ReportsFailure()
    {
        using var empty = new SKBitmap(); // no pixel ref: nothing to read
        var buffer = Marshal.AllocHGlobal(16);
        try
        {
            SkiaInterop.CopyPixelsAsBgraPremul(empty, buffer, 4).Should().BeFalse();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void ToAvaloniaBitmap_ReturnsNullForEmptyInput()
    {
        SkiaInterop.ToAvaloniaBitmap(null).Should().BeNull();
        using var empty = new SKBitmap();
        SkiaInterop.ToAvaloniaBitmap(empty).Should().BeNull();
    }
}
