using Excise.Core.Primitives;
using Xunit;

namespace Excise.Rendering.Tests;

public class SoftMaskAlphaSamplingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ByteAndIntSamplingMatchIndependentGridGolden(bool reverseDecode, bool truncated)
    {
        byte[] bytes = truncated ? [0, 64, 128] : [0, 64, 128, 255];
        var integers = bytes.Select(value => (int)value).ToArray();
        var stream = new PdfStream();
        if (reverseDecode)
            stream["Decode"] = new PdfArray(new PdfInteger(1), new PdfInteger(0));
        byte[] expected = (reverseDecode, truncated) switch
        {
            (false, false) => [0, 64, 64, 128, 255, 255],
            (false, true) => [0, 64, 64, 128, 0, 0],
            (true, false) => [255, 191, 191, 127, 0, 0],
            _ => [255, 191, 191, 127, 255, 255]
        };
        Assert.Equal(expected, RenderContext.CreateSoftMaskAlpha<byte>(bytes, 2, 2, 3, 2, stream));
        Assert.Equal(expected, RenderContext.CreateSoftMaskAlpha<int>(integers, 2, 2, 3, 2, stream));
    }

    [Fact]
    public void DownsamplingUsesPixelCentersAndDecodeRange()
    {
        var stream = new PdfStream();
        stream["Decode"] = new PdfArray(new PdfReal(0.25), new PdfReal(0.75));
        Assert.Equal(new byte[] { 191 }, RenderContext.CreateSoftMaskAlpha<byte>([0, 64, 128, 255], 2, 2, 1, 1, stream));
        Assert.Equal(new byte[] { 64, 96, 128, 191 },
            RenderContext.CreateSoftMaskAlpha<int>([0, 64, 128, 255], 2, 2, 2, 2, stream));
    }

    [Fact]
    public void OutOfRangeIntSamplesKeepLegacyClampingAndEmptyInputUsesZero()
    {
        var stream = new PdfStream();
        Assert.Equal(new byte[] { 0, 255 }, RenderContext.CreateSoftMaskAlpha<int>([-5, 300], 2, 1, 2, 1, stream));
        Assert.Equal(new byte[] { 0, 0 }, RenderContext.CreateSoftMaskAlpha<byte>([], 2, 1, 2, 1, stream));
    }

    [Fact]
    public void SamplingAllocatesOnlyTheSmallTargetBuffer()
    {
        var stream = new PdfStream();
        var source = new int[2048 * 2048];
        RenderContext.CreateSoftMaskAlpha<int>(source, 2048, 2048, 64, 64, stream);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var alpha = RenderContext.CreateSoftMaskAlpha<int>(source, 2048, 2048, 64, 64, stream);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(4096, alpha.Length);
        Assert.True(allocated < 8192, $"Target-only allocation expected, got {allocated} bytes");
    }
}
