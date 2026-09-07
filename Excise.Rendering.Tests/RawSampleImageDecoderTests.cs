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
    public void SubsamplingUsesNearestNeighborSampleNeverAnAverage()
    {
        // #1403: a 2x2 checkerboard of the two extremes (0, 255) downsampled
        // to 1x1. A box-filter average would produce ~127/128 — a value that
        // never occurred in the source. For an Indexed color space that
        // sample is a PALETTE INDEX, and averaging two indices can land on a
        // completely unrelated palette entry, so nearest-neighbor must
        // instead pick ONE real source sample.
        using var bitmap = RawSampleImageDecoder.Decode(new RawSampleImageDecodeRequest(
            Samples: new byte[] { 0, 255, 255, 0 },
            Width: 2,
            Height: 2,
            BitsPerComponent: 8,
            ColorSpace: PdfColorSpace.DeviceGray,
            ComponentsPerPixel: 1,
            DecodeArray: null,
            ColorKeyMask: null,
            TargetWidth: 1,
            TargetHeight: 1));

        bitmap.Should().NotBeNull();
        bitmap!.Width.Should().Be(1);
        bitmap.Height.Should().Be(1);
        var pixel = bitmap.GetPixel(0, 0);
        (pixel.Red == 0 || pixel.Red == 255).Should().BeTrue(
            $"nearest-neighbor subsampling must reproduce a real source sample, not blend them (got {pixel.Red})");
    }

    [Fact]
    public void TargetAtOrAboveSourceSizeDecodesAtFullSourceResolution()
    {
        // A target equal to (or larger than) the source must never trigger
        // subsampling and must never upscale past the source grid — pins the
        // clamp-to-source behaviour that keeps a full-resolution render
        // (e.g. an image at its own native DPI) byte-identical to omitting
        // TargetWidth/TargetHeight entirely.
        var samples = new byte[] { 255, 0, 0, 0, 255, 0 };
        RawSampleImageDecodeRequest Request(int? targetWidth, int? targetHeight) => new(
            Samples: samples,
            Width: 2,
            Height: 1,
            BitsPerComponent: 8,
            ColorSpace: PdfColorSpace.DeviceRGB,
            ComponentsPerPixel: 3,
            DecodeArray: null,
            ColorKeyMask: null,
            TargetWidth: targetWidth,
            TargetHeight: targetHeight);

        using var withoutTarget = RawSampleImageDecoder.Decode(Request(null, null));
        using var equalTarget = RawSampleImageDecoder.Decode(Request(2, 1));
        using var largerTarget = RawSampleImageDecoder.Decode(Request(500, 500));

        withoutTarget.Should().NotBeNull();
        equalTarget.Should().NotBeNull();
        largerTarget.Should().NotBeNull();

        equalTarget!.Width.Should().Be(2);
        equalTarget.Height.Should().Be(1);
        largerTarget!.Width.Should().Be(2, "a larger target must clamp to the source width, never upscale");
        largerTarget.Height.Should().Be(1, "a larger target must clamp to the source height, never upscale");

        equalTarget.GetPixel(0, 0).Should().Be(withoutTarget!.GetPixel(0, 0));
        equalTarget.GetPixel(1, 0).Should().Be(withoutTarget.GetPixel(1, 0));
        largerTarget.GetPixel(0, 0).Should().Be(withoutTarget.GetPixel(0, 0));
        largerTarget.GetPixel(1, 0).Should().Be(withoutTarget.GetPixel(1, 0));
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
