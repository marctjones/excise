using AwesomeAssertions;
using Excise.Core.ColorSpaces;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

public sealed class RawSampleImageDecoderTests
{
    [Fact]
    public void DeviceRgbSamplesAreCopiedWithoutAContext()
    {
        using var bitmap = RawSampleImageDecoder.Decode(new RawSampleImageDecodeRequest(
            Samples: new byte[] { 255, 0, 0, 0, 255, 0 },
            Width: 2,
            Height: 1,
            BitsPerComponent: 8,
            ColorSpace: PdfColorSpace.DeviceRGB,
            ComponentsPerPixel: 3,
            DecodeArray: null,
            ColorKeyMask: null));

        bitmap.Should().NotBeNull();
        bitmap!.GetPixel(0, 0).Should().Be(SKColors.Red);
        bitmap.GetPixel(1, 0).Should().Be(new SKColor(0, 255, 0));
    }

    [Fact]
    public void DeviceCmykSamplesUseTheColorSpaceLattice()
    {
        using var bitmap = RawSampleImageDecoder.Decode(new RawSampleImageDecodeRequest(
            Samples: new byte[] { 0, 0, 0, 255 },
            Width: 1,
            Height: 1,
            BitsPerComponent: 8,
            ColorSpace: PdfColorSpace.DeviceCMYK,
            ComponentsPerPixel: 4,
            DecodeArray: null,
            ColorKeyMask: null));

        bitmap.Should().NotBeNull();
        var pixel = bitmap!.GetPixel(0, 0);
        var (red, green, blue) = PdfColorSpace.DeviceCMYK.ToRgb([0, 0, 0, 1]);
        pixel.Red.Should().Be((byte)Math.Clamp(red * 255, 0, 255));
        pixel.Green.Should().Be((byte)Math.Clamp(green * 255, 0, 255));
        pixel.Blue.Should().Be((byte)Math.Clamp(blue * 255, 0, 255));
        pixel.Alpha.Should().Be(255);
    }

    [Fact]
    public void DecodeArrayIsAppliedWithoutAContext()
    {
        using var bitmap = RawSampleImageDecoder.Decode(new RawSampleImageDecodeRequest(
            Samples: new byte[] { 0 },
            Width: 1,
            Height: 1,
            BitsPerComponent: 8,
            ColorSpace: PdfColorSpace.DeviceGray,
            ComponentsPerPixel: 1,
            DecodeArray: new[] { 1.0, 0.0 },
            ColorKeyMask: null));

        bitmap.Should().NotBeNull();
        bitmap!.GetPixel(0, 0).Should().Be(SKColors.White);
    }

    [Fact]
    public void ColorKeyMaskUsesRawSamplesBeforeColorConversion()
    {
        using var bitmap = RawSampleImageDecoder.Decode(new RawSampleImageDecodeRequest(
            Samples: new byte[] { 255, 0, 0, 0, 255, 0 },
            Width: 2,
            Height: 1,
            BitsPerComponent: 8,
            ColorSpace: PdfColorSpace.DeviceRGB,
            ComponentsPerPixel: 3,
            DecodeArray: null,
            ColorKeyMask: new[] { 255, 255, 0, 0, 0, 0 }));

        bitmap.Should().NotBeNull();
        bitmap!.GetPixel(0, 0).Alpha.Should().Be(0);
        bitmap.GetPixel(1, 0).Should().Be(new SKColor(0, 255, 0));
    }

    [Fact]
    public void PackedFourBitSamplesAreUnpackedPerRow()
    {
        using var bitmap = RawSampleImageDecoder.Decode(new RawSampleImageDecodeRequest(
            Samples: new byte[] { 0x0f },
            Width: 2,
            Height: 1,
            BitsPerComponent: 4,
            ColorSpace: PdfColorSpace.DeviceGray,
            ComponentsPerPixel: 1,
            DecodeArray: null,
            ColorKeyMask: null));

        bitmap.Should().NotBeNull();
        bitmap!.GetPixel(0, 0).Should().Be(SKColors.Black);
        bitmap.GetPixel(1, 0).Should().Be(SKColors.White);
    }

    [Fact]
    public void OneBitRowsHonorBytePadding()
    {
        using var bitmap = RawSampleImageDecoder.Decode(new RawSampleImageDecodeRequest(
            Samples: new byte[] { 0b0000_0000, 0b1000_0000 },
            Width: 1,
            Height: 2,
            BitsPerComponent: 1,
            ColorSpace: PdfColorSpace.DeviceGray,
            ComponentsPerPixel: 1,
            DecodeArray: null,
            ColorKeyMask: null));

        bitmap.Should().NotBeNull();
        bitmap!.GetPixel(0, 0).Should().Be(SKColors.Black);
        bitmap.GetPixel(0, 1).Should().Be(SKColors.White);
    }

    [Fact]
    public void CancellationIsPropagatedInsteadOfReportedAsMalformedData()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => RawSampleImageDecoder.Decode(new RawSampleImageDecodeRequest(
            Samples: new byte[] { 0 },
            Width: 1,
            Height: 1,
            BitsPerComponent: 8,
            ColorSpace: PdfColorSpace.DeviceGray,
            ComponentsPerPixel: 1,
            DecodeArray: null,
            ColorKeyMask: null,
            CancellationToken: cancellation.Token));

        act.Should().Throw<OperationCanceledException>();
    }
}
