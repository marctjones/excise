using Xunit;

namespace Excise.Rendering.Tests;

public class ImageTargetSizeTests
{
    [Theory]
    [InlineData(100, 500, 300, 2000, 100, 500)]
    [InlineData(100, 500, 10, 20, 10, 20)]
    [InlineData(100, 500, 0, -20, 1, 1)]
    [InlineData(0, -1, 100, 100, 1, 1)]
    [InlineData(8192, 4096, 8192, 4096, 8192, 4096)]
    [InlineData(16384, 8192, 16384, 8192, 8192, 4096)]
    [InlineData(8192, 8192, 8192, 8192, 5792, 5792)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, 5792, 5792)]
    public void PreservesSourceLimitBudgetBoundaryAndFloorRounding(
        int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, int width, int height)
    {
        Assert.Equal((width, height),
            RenderContext.ClampBudgetedImageTargetSize(sourceWidth, sourceHeight, targetWidth, targetHeight));
    }
}
