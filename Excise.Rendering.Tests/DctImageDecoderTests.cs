using AwesomeAssertions;
using Excise.Core.ColorSpaces;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

public sealed class DctImageDecoderTests
{
    [Fact]
    public void DeclaredRgbColorTransformDecodesWithoutAContext()
    {
        using var source = new SKBitmap(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul);
        source.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(source);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, quality: 100);

        using var decoded = DctImageDecoder.Decode(new DctImageDecodeRequest(
            Bytes: encoded.ToArray(),
            SourceWidth: 2,
            SourceHeight: 2,
            TargetWidth: 2,
            TargetHeight: 2,
            ColorSpaceName: "DeviceRGB",
            ColorTransform: 1,
            ResolvedColorSpace: PdfColorSpace.DeviceRGB,
            DecodeArray: null,
            ColorKeyMask: null,
            CancellationToken: default));

        decoded.Should().NotBeNull();
        decoded!.Width.Should().Be(2);
        decoded.Height.Should().Be(2);
        decoded.GetPixel(0, 0).Red.Should().BeGreaterThan((byte)200);
    }

    [Fact]
    public void TransformPolicyUsesDeclaredValueAndCmykFallback()
    {
        DctImageDecoder.ResolveColorTransform(
            new byte[] { 1, 2, 3, 4 },
            "DeviceRGB",
            decodeParametersColorTransform: 1).Should().Be(1);
        DctImageDecoder.ResolveColorTransform(
            new byte[] { 1, 2, 3, 4 },
            "DeviceRGB",
            decodeParametersColorTransform: null).Should().BeNull();
        DctImageDecoder.ResolveColorTransform(
            new byte[] { 1, 2, 3, 4 },
            "DeviceCMYK",
            decodeParametersColorTransform: null).Should().Be(0);
    }

    [Fact]
    public void CancellationIsPropagatedInsteadOfReportedAsMalformedData()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => DctImageDecoder.Decode(new DctImageDecodeRequest(
            Bytes: new byte[] { 1, 2, 3, 4 },
            SourceWidth: 1,
            SourceHeight: 1,
            TargetWidth: 1,
            TargetHeight: 1,
            ColorSpaceName: "DeviceRGB",
            ColorTransform: 1,
            ResolvedColorSpace: PdfColorSpace.DeviceRGB,
            DecodeArray: null,
            ColorKeyMask: null,
            CancellationToken: cancellation.Token));

        act.Should().Throw<OperationCanceledException>();
    }
}
