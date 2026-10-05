using AwesomeAssertions;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

public class ImageSoftMaskPixelBufferTests
{
    [Fact]
    public void MatchingImageAndMaskGrids_UseTwoSamplesPerDevicePixelForAThumbnail()
    {
        var target = RenderContext.EstimateImageSoftMaskTargetSize(
            SKMatrix.CreateScale(0.025f, 0.025f), 2048, 2048, 2048, 2048, 2048, 2048);

        target.Should().Be((104, 104), "matching source grids must not force full-resolution composition (#1821)");
    }

    [Fact]
    public void MatchingImageAndMaskGrids_PreserveNativeResolutionWhenUpscaling()
    {
        var target = RenderContext.EstimateImageSoftMaskTargetSize(
            SKMatrix.CreateScale(2, 2), 64, 64, 64, 64, 64, 64);

        target.Should().Be((64, 64), "the quality floor must not expand the native source grid");
    }

    [Theory]
    [InlineData(SKColorType.Rgba8888, SKAlphaType.Premul)]
    [InlineData(SKColorType.Bgra8888, SKAlphaType.Premul)]
    [InlineData(SKColorType.Rgba8888, SKAlphaType.Unpremul)]
    [InlineData(SKColorType.Bgra8888, SKAlphaType.Unpremul)]
    public void Composition_PreservesChannelOrderAndStraightAlpha(SKColorType type, SKAlphaType alphaType)
    {
        using var source = new SKBitmap(2, 2, type, alphaType);
        source.SetPixel(0, 0, new SKColor(128, 64, 32, 128));
        source.SetPixel(1, 0, new SKColor(32, 64, 128, 255));
        source.SetPixel(0, 1, SKColors.Red);
        source.SetPixel(1, 1, SKColors.Blue);

        using var result = RenderContext.CreateSoftMaskedImageBitmap(
            source, new SoftMaskAlpha([128, 255, 0, 255], 2, 2));

        result.Should().NotBeNull();
        result!.GetPixel(0, 0).Should().Be(new SKColor(128, 64, 32, 64));
        result.GetPixel(1, 0).Should().Be(new SKColor(32, 64, 128, 255));
        result.GetPixel(0, 1).Alpha.Should().Be(0);
        result.GetPixel(1, 1).Should().Be(SKColors.Blue);
    }

    [Fact]
    public void ThumbnailComposition_DoesNotAllocateAWholeSourcePixelArray()
    {
        // #1821: a small output must not copy a 16 MiB source into a managed SKColor[].
        using var source = new SKBitmap(2048, 2048, SKColorType.Rgba8888, SKAlphaType.Opaque);
        source.Erase(SKColors.Red);
        var mask = new SoftMaskAlpha(Enumerable.Repeat((byte)128, 64 * 64).ToArray(), 64, 64);
        using var warmup = RenderContext.CreateSoftMaskedImageBitmap(source, mask);

        var before = GC.GetAllocatedBytesForCurrentThread();
        using var result = RenderContext.CreateSoftMaskedImageBitmap(source, mask);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        result.Should().NotBeNull();
        result!.GetPixel(20, 20).Should().Be(new SKColor(255, 0, 0, 128));
        allocated.Should().BeLessThan(64 * 1024, "pixel buffers should stay in native bitmap memory");
    }
}
