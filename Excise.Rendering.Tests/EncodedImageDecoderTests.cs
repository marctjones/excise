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
}
