using Excise.App.Services;
using SkiaSharp;
using Xunit;

namespace Excise.App.Tests.Unit;

public class PdfImagePixelConverterTests
{
    [Theory]
    [InlineData(SKColorType.Rgba8888, SKAlphaType.Unpremul)]
    [InlineData(SKColorType.Bgra8888, SKAlphaType.Unpremul)]
    [InlineData(SKColorType.Rgba8888, SKAlphaType.Premul)]
    [InlineData(SKColorType.Bgra8888, SKAlphaType.Premul)]
    public void ExtractKeepsStraightColourOpacityAndRowOrder(SKColorType colourType, SKAlphaType alphaType)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(2, 2, colourType, alphaType));
        bitmap.SetPixel(0, 0, new SKColor(255, 0, 0, 128));
        bitmap.SetPixel(1, 0, new SKColor(0, 255, 0, 255));
        bitmap.SetPixel(0, 1, new SKColor(0, 0, 255, 64));
        bitmap.SetPixel(1, 1, new SKColor(0, 0, 0, 0));

        var (rgb, alpha) = PdfImagePixelConverter.Extract(bitmap);

        Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0 }, rgb);
        Assert.Equal(new byte[] { 128, 255, 64, 0 }, alpha);
        Assert.Equal(2, bitmap.Width); // Conversion does not take ownership of the bitmap.
    }

    [Theory]
    [InlineData(SKColorType.Rgba8888, SKAlphaType.Unpremul)]
    [InlineData(SKColorType.Bgra8888, SKAlphaType.Premul)]
    [InlineData(SKColorType.Rgba8888, SKAlphaType.Opaque)]
    public void EntirelyOpaqueImagesOmitAlpha(SKColorType colourType, SKAlphaType alphaType)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(2, 1, colourType, alphaType));
        bitmap.SetPixel(0, 0, new SKColor(12, 34, 56, 255));
        bitmap.SetPixel(1, 0, new SKColor(78, 90, 123, 255));
        var (rgb, alpha) = PdfImagePixelConverter.Extract(bitmap);
        Assert.Equal(new byte[] { 12, 34, 56, 78, 90, 123 }, rgb);
        Assert.Null(alpha);
    }
}
