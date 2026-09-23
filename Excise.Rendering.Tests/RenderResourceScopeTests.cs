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

    /// <summary>
    /// #1678: an early release must also drop the scope's reference to the
    /// stream. A streamed subsampled decode (#1677) has no decoded samples to
    /// release, and the object store has already forgotten the object (#1207
    /// F3), so the scope's record was the only thing keeping its encoded bytes
    /// alive until the render ended — every image on the page at once.
    /// </summary>
    [Fact]
    public void EarlyRelease_DropsTheScopesReference_SoTheStreamCanBeCollectedMidRender()
    {
        using var scope = new RenderResourceScope(releaseDecodedImageSamples: true);
        var weak = NoteAndReleaseAnUndecodedStream(scope);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        weak.IsAlive.Should().BeFalse("the scope is still live, but it is done with this stream");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference NoteAndReleaseAnUndecodedStream(RenderResourceScope scope)
    {
        var stream = new PdfStream(
            new PdfDictionary { ["Filter"] = new PdfName("FlateDecode") },
            new byte[1024]);
        stream.IsDecoded.Should().BeFalse("fixture");
        scope.NoteImageSampleRead(stream);
        scope.ReleaseImageSamplesEarly(stream);
        return new WeakReference(stream);
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
