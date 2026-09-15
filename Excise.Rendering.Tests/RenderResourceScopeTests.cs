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

    /// <summary>
    /// #1492: the image-sample sink gets every stream the render noted, once,
    /// when the scope is disposed — decoded or not, and independently of the
    /// release flag.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImageSampleSink_ReceivesEveryNotedStreamOnce_AtDispose_DecodedOrNot(bool releaseDecodedImageSamples)
    {
        var sink = new List<PdfStream>();
        var scope = new RenderResourceScope(releaseDecodedImageSamples, sink);
        var undecoded = new PdfStream(new PdfDictionary { ["Filter"] = new PdfName("FlateDecode") }, new byte[] { 1 });
        var decoded = new PdfStream(new byte[] { 1, 2, 3 });
        decoded.IsDecoded.Should().BeTrue("fixture");

        scope.NoteImageSampleRead(undecoded);
        scope.NoteImageSampleRead(decoded);
        scope.NoteImageSampleRead(undecoded);

        sink.Should().BeEmpty("the sink is filled once, when the render ends");
        scope.Dispose();
        sink.Should().HaveCount(2).And.Contain(undecoded).And.Contain(decoded);

        scope.Dispose();
        sink.Should().HaveCount(2, "a second dispose adds nothing");
    }

    [Fact]
    public void WithoutASink_NothingIsRecorded()
    {
        using var scope = new RenderResourceScope();
        var act = () => scope.NoteImageSampleRead(new PdfStream(new byte[] { 1 }));
        act.Should().NotThrow();
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
