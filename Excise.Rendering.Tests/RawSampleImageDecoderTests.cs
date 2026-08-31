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
        using var bitmap = RawSampleImageDecoder.TryDecodeFast(new RawSampleImageDecodeRequest(
            Samples: new byte[] { 255, 0, 0, 0, 255, 0 },
            Width: 2,
            Height: 1,
            BitsPerComponent: 8,
            ColorSpace: PdfColorSpace.DeviceRGB,
            ComponentsPerPixel: 3,
            HasDecodeArray: false));

        bitmap.Should().NotBeNull();
        bitmap!.GetPixel(0, 0).Should().Be(SKColors.Red);
        bitmap.GetPixel(1, 0).Should().Be(new SKColor(0, 255, 0));
    }

    [Fact]
    public void DeviceCmykSamplesUseTheColorSpaceLattice()
    {
        using var bitmap = RawSampleImageDecoder.TryDecodeFast(new RawSampleImageDecodeRequest(
            Samples: new byte[] { 0, 0, 0, 255 },
            Width: 1,
            Height: 1,
            BitsPerComponent: 8,
            ColorSpace: PdfColorSpace.DeviceCMYK,
            ComponentsPerPixel: 4,
            HasDecodeArray: false));

        bitmap.Should().NotBeNull();
        var pixel = bitmap!.GetPixel(0, 0);
        var (red, green, blue) = PdfColorSpace.DeviceCMYK.ToRgb([0, 0, 0, 1]);
        pixel.Red.Should().Be((byte)Math.Clamp(red * 255, 0, 255));
        pixel.Green.Should().Be((byte)Math.Clamp(green * 255, 0, 255));
        pixel.Blue.Should().Be((byte)Math.Clamp(blue * 255, 0, 255));
        pixel.Alpha.Should().Be(255);
    }

    [Fact]
    public void DecoratedOrIncompleteSamplesStayOnTheGeneralPath()
    {
        RawSampleImageDecoder.TryDecodeFast(new RawSampleImageDecodeRequest(
            Samples: new byte[] { 255, 0 },
            Width: 1,
            Height: 1,
            BitsPerComponent: 8,
            ColorSpace: PdfColorSpace.DeviceRGB,
            ComponentsPerPixel: 3,
            HasDecodeArray: false)).Should().BeNull();

        RawSampleImageDecoder.TryDecodeFast(new RawSampleImageDecodeRequest(
            Samples: new byte[] { 255, 0, 0 },
            Width: 1,
            Height: 1,
            BitsPerComponent: 8,
            ColorSpace: PdfColorSpace.DeviceRGB,
            ComponentsPerPixel: 3,
            HasDecodeArray: true)).Should().BeNull();
    }
}
