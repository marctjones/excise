using AwesomeAssertions;
using Excise.Core.Primitives;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

public sealed class RenderResourceScopeTests
{
    private static readonly ImageBitmapCacheKey CacheKey = new(
        Width: 2,
        Height: 2,
        BitsPerComponent: 8,
        ColorSpace: "DeviceRGB",
        TargetWidth: 2,
        TargetHeight: 2,
        ImageMask: false,
        FillRed: 0,
        FillGreen: 0,
        FillBlue: 0,
        FillAlpha: 0,
        DctColorTransform: null);

    [Fact]
    public void BorrowersShareTheOwnersIndirectImageEntry()
    {
        using var scope = new RenderResourceScope();
        var ownerView = new PdfStream { ObjectNumber = 17, GenerationNumber = 2 };
        var borrowerView = new PdfStream { ObjectNumber = 17, GenerationNumber = 2 };
        var bitmap = new SKBitmap(2, 2);

        scope.CacheDecodedImage(ownerView, CacheKey, bitmap);

        scope.TryGetDecodedImage(borrowerView, CacheKey, out var cached).Should().BeTrue();
        cached.Should().BeSameAs(bitmap);
    }

    [Fact]
    public void FailedDecodeIsCachedAndScopeRejectsUseAfterOwnerDisposes()
    {
        var scope = new RenderResourceScope();
        var stream = new PdfStream();

        scope.CacheDecodedImage(stream, CacheKey, bitmap: null);

        scope.TryGetDecodedImage(stream, CacheKey, out var cached).Should().BeTrue();
        cached.Should().BeNull("a failed decode remains a cache hit instead of being retried");

        scope.Dispose();
        scope.Dispose();

        Action useAfterDispose = () => scope.TryGetDecodedImage(stream, CacheKey, out _);
        useAfterDispose.Should().Throw<ObjectDisposedException>();
    }
}
