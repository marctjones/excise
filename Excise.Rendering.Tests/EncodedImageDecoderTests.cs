using AwesomeAssertions;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

public sealed class EncodedImageDecoderTests
{
    [Fact]
    public void EmptyAndMalformedPayloadsAreRefused()
    {
        EncodedImageDecoder.Decode(new EncodedImageDecodeRequest(null)).Should().BeNull();
        EncodedImageDecoder.Decode(new EncodedImageDecodeRequest(Array.Empty<byte>())).Should().BeNull();
        EncodedImageDecoder.Decode(new EncodedImageDecodeRequest(new byte[] { 1, 2, 3, 4 })).Should().BeNull();
    }

    [Fact]
    public void ValidPayloadFallsBackToIntrinsicSizeWhenCodecDoesNotScale()
    {
        using var source = new SKBitmap(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul);
        source.SetPixel(0, 0, SKColors.Red);
        source.SetPixel(1, 0, SKColors.Green);
        source.SetPixel(0, 1, SKColors.Blue);
        source.SetPixel(1, 1, SKColors.White);
        using var image = SKImage.FromBitmap(source);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, quality: 100);

        using var decoded = EncodedImageDecoder.Decode(new EncodedImageDecodeRequest(
            encoded.ToArray(),
            new SKSizeI(1, 1)));

        decoded.Should().NotBeNull();
        decoded!.Width.Should().Be(2,
            "Skia may ignore a preferred decode size for codecs without scaled decode support");
        decoded.Height.Should().Be(2);
    }

    [Fact]
    public void CancellationIsPropagatedInsteadOfReportedAsMalformedData()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => EncodedImageDecoder.Decode(new EncodedImageDecodeRequest(
            new byte[] { 1, 2, 3, 4 },
            CancellationToken: cancellation.Token));

        act.Should().Throw<OperationCanceledException>();
    }
    // ── #1821: a JPEG must decode at a REDUCED size, not fall back to full size ────────────
    //
    // SKBitmap.Decode(bytes, new SKImageInfo(w, h, ...)) returns null for a JPEG unless w x h is one
    // of the codec's own scales (1/2, 1/4, 1/8). The old code treated null as "use the intrinsic
    // size" and decoded the whole image, so a 2480x2630 plate drawn ~200 px wide cost 3.4 s and
    // 376 MB. The size the caller asked for is a HINT; the assertion is on what came back.

    private static byte[] Jpeg(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, 95).ToArray();
    }

    [Theory]
    [InlineData(100, 80, 250, 200)]     // 1/8 still covers the target
    [InlineData(300, 240, 500, 400)]    // 1/8 (250) is too small, 1/4 covers it
    [InlineData(600, 480, 1000, 800)]   // only 1/2 covers it
    public void JpegDecodesAtTheDeepestReductionThatStillCoversTheTarget(
        int targetW, int targetH, int expectedW, int expectedH)
    {
        var jpeg = Jpeg(2000, 1600, new SKColor(200, 30, 60));

        using var decoded = EncodedImageDecoder.Decode(
            new EncodedImageDecodeRequest(jpeg, new SKSizeI(targetW, targetH)));

        decoded.Should().NotBeNull();
        decoded!.Width.Should().Be(expectedW,
            "an arbitrary preferred size used to make Skia return null and decode the whole image");
        decoded.Height.Should().Be(expectedH);
        decoded.Width.Should().BeGreaterThanOrEqualTo(targetW, "a reduction must never fall below the target");
        decoded.Height.Should().BeGreaterThanOrEqualTo(targetH);
    }

    [Fact]
    public void ReducedJpegKeepsItsColour()
    {
        var jpeg = Jpeg(2000, 1600, new SKColor(200, 30, 60));

        using var reduced = EncodedImageDecoder.Decode(
            new EncodedImageDecodeRequest(jpeg, new SKSizeI(100, 80)));
        using var full = SKBitmap.Decode(jpeg);

        reduced.Should().NotBeNull();
        var a = reduced!.GetPixel(reduced.Width / 2, reduced.Height / 2);
        var b = full.GetPixel(full.Width / 2, full.Height / 2);
        ((int)a.Red).Should().BeCloseTo(b.Red, 3);
        ((int)a.Green).Should().BeCloseTo(b.Green, 3);
        ((int)a.Blue).Should().BeCloseTo(b.Blue, 3);
    }

    [Fact]
    public void JpegIsNeverUpscaledOrReducedBelowTheTarget()
    {
        var jpeg = Jpeg(2000, 1600, new SKColor(10, 120, 200));

        // Target as large as the source, and one no supported reduction can cover: the decode
        // must not shrink below what the caller needs, so it keeps the previous behaviour.
        foreach (var target in new[] { new SKSizeI(2000, 1600), new SKSizeI(1500, 1200) })
        {
            using var decoded = EncodedImageDecoder.Decode(new EncodedImageDecodeRequest(jpeg, target));
            decoded.Should().NotBeNull();
            decoded!.Width.Should().BeGreaterThanOrEqualTo(target.Width);
            decoded.Height.Should().BeGreaterThanOrEqualTo(target.Height);
        }
    }

    [Fact]
    public void GrayscaleJpegAlsoReduces()
    {
        using var gray = new SKBitmap(2000, 1600, SKColorType.Gray8, SKAlphaType.Opaque);
        gray.Erase(new SKColor(128, 128, 128));
        using var image = SKImage.FromBitmap(gray);
        var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 95).ToArray();

        using var decoded = EncodedImageDecoder.Decode(
            new EncodedImageDecodeRequest(jpeg, new SKSizeI(100, 80)));

        decoded.Should().NotBeNull();
        decoded!.Width.Should().Be(250);
        var p = decoded.GetPixel(10, 10);
        ((int)p.Red).Should().BeCloseTo(128, 4);
    }
}
