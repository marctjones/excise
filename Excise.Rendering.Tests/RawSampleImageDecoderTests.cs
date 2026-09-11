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

    // ---- #1470: identity /Decode on Indexed images ----------------------

    private static PdfColorSpace CreateIndexedRgb(Excise.Core.Document.PdfDocument document, int hival)
    {
        // A deliberately non-monotonic palette so a wrong index is visible.
        var lookup = new byte[(hival + 1) * 3];
        for (var i = 0; i <= hival; i++)
        {
            lookup[i * 3] = (byte)((i * 37) % 256);
            lookup[(i * 3) + 1] = (byte)((i * 91 + 13) % 256);
            lookup[(i * 3) + 2] = (byte)(255 - ((i * 53) % 256));
        }

        return PdfColorSpace.Parse(
            new Excise.Core.Primitives.PdfArray(
                new Excise.Core.Primitives.PdfName("Indexed"),
                new Excise.Core.Primitives.PdfName("DeviceRGB"),
                new Excise.Core.Primitives.PdfInteger(hival),
                new Excise.Core.Primitives.PdfString(lookup, isHex: true)),
            document);
    }

    private static byte[] PixelBytes(SKBitmap bitmap)
        => SkiaBitmapPixelBuffer.GetWritableSpan(bitmap).ToArray();

    [Fact]
    public void IdentityDecodeOnIndexed8BitIsPixelIdenticalToNoDecode()
    {
        using var document = Excise.Core.Document.PdfDocument.CreateNew();
        var indexed = CreateIndexedRgb(document, 255);
        var samples = new byte[16 * 16];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (byte)i;

        RawSampleImageDecodeRequest Request(double[]? decode) => new(
            Samples: samples,
            Width: 16,
            Height: 16,
            BitsPerComponent: 8,
            ColorSpace: indexed,
            ComponentsPerPixel: 1,
            DecodeArray: decode,
            ColorKeyMask: null);

        RawSampleImageDecoder.IsIdentityIndexedDecodeArray(indexed, 8, [0, 255])
            .Should().BeTrue("[0 255] is the Indexed 8-bit default and must take the palette-table path");

        using var withoutDecode = RawSampleImageDecoder.Decode(Request(null));
        using var withIdentity = RawSampleImageDecoder.Decode(Request([0, 255]));

        withoutDecode.Should().NotBeNull();
        withIdentity.Should().NotBeNull();
        PixelBytes(withIdentity!).Should().Equal(PixelBytes(withoutDecode!));
    }

    [Fact]
    public void IdentityDecodeOnIndexed4BitIsPixelIdenticalToNoDecode()
    {
        using var document = Excise.Core.Document.PdfDocument.CreateNew();
        var indexed = CreateIndexedRgb(document, 15);
        // 15 pixels cycling through the 4-bit indices, packed two to a byte;
        // an odd width so every row carries a padding nibble.
        const int width = 5;
        const int height = 3;
        var samples = new byte[((width * 4) + 7) / 8 * height];
        var index = 0;
        for (var row = 0; row < height; row++)
        {
            var rowStart = row * (((width * 4) + 7) / 8);
            for (var column = 0; column < width; column++)
            {
                var bit = column * 4;
                samples[rowStart + (bit / 8)] |= (byte)((index++ % 16) << (4 - (bit % 8)));
            }
        }

        RawSampleImageDecodeRequest Request(double[]? decode) => new(
            Samples: samples,
            Width: width,
            Height: height,
            BitsPerComponent: 4,
            ColorSpace: indexed,
            ComponentsPerPixel: 1,
            DecodeArray: decode,
            ColorKeyMask: null);

        RawSampleImageDecoder.IsIdentityIndexedDecodeArray(indexed, 4, [0, 15])
            .Should().BeTrue("[0 15] is the Indexed 4-bit default and must take the palette-table path");

        using var withoutDecode = RawSampleImageDecoder.Decode(Request(null));
        using var withIdentity = RawSampleImageDecoder.Decode(Request([0, 15]));

        withoutDecode.Should().NotBeNull();
        withIdentity.Should().NotBeNull();
        PixelBytes(withIdentity!).Should().Equal(PixelBytes(withoutDecode!));
    }

    [Fact]
    public void NonIdentityOrNonIndexedDecodeKeepsTheGeneralPath()
    {
        using var document = Excise.Core.Document.PdfDocument.CreateNew();
        var indexed = CreateIndexedRgb(document, 255);

        // Non-identity arrays on Indexed.
        RawSampleImageDecoder.IsIdentityIndexedDecodeArray(indexed, 8, [255, 0]).Should().BeFalse();
        RawSampleImageDecoder.IsIdentityIndexedDecodeArray(indexed, 8, [0, 15]).Should().BeFalse(
            "[0 15] is only the default at 4 bpc");
        RawSampleImageDecoder.IsIdentityIndexedDecodeArray(indexed, 4, [0, 255]).Should().BeFalse(
            "[0 255] is not the default at 4 bpc");
        RawSampleImageDecoder.IsIdentityIndexedDecodeArray(indexed, 8, [0, 255, 0, 255]).Should().BeFalse(
            "only an exactly two-element array is recognised");
        RawSampleImageDecoder.IsIdentityIndexedDecodeArray(indexed, 8, [0, 254.99999]).Should().BeFalse(
            "the comparison is exact, not tolerant");
        RawSampleImageDecoder.IsIdentityIndexedDecodeArray(indexed, 16, [0, 65535]).Should().BeFalse(
            "only bpc <= 8 is recognised");
        RawSampleImageDecoder.IsIdentityIndexedDecodeArray(indexed, 8, null).Should().BeFalse();

        // Identity arrays on continuous colour spaces stay on the general path.
        RawSampleImageDecoder.IsIdentityIndexedDecodeArray(PdfColorSpace.DeviceGray, 8, [0, 255]).Should().BeFalse();
        RawSampleImageDecoder.IsIdentityIndexedDecodeArray(PdfColorSpace.DeviceGray, 8, [0, 1]).Should().BeFalse();
        RawSampleImageDecoder.IsIdentityIndexedDecodeArray(PdfColorSpace.DeviceRGB, 8, [0, 1, 0, 1, 0, 1]).Should().BeFalse();

        // And a non-identity Indexed array is still honoured: [255 0] reverses
        // the index, so pixel 0 must read palette entry 255, not entry 0.
        var samples = new byte[] { 0, 255 };
        using var reversed = RawSampleImageDecoder.Decode(new RawSampleImageDecodeRequest(
            Samples: samples,
            Width: 2,
            Height: 1,
            BitsPerComponent: 8,
            ColorSpace: indexed,
            ComponentsPerPixel: 1,
            DecodeArray: [255, 0],
            ColorKeyMask: null));
        using var plain = RawSampleImageDecoder.Decode(new RawSampleImageDecodeRequest(
            Samples: samples,
            Width: 2,
            Height: 1,
            BitsPerComponent: 8,
            ColorSpace: indexed,
            ComponentsPerPixel: 1,
            DecodeArray: null,
            ColorKeyMask: null));

        reversed.Should().NotBeNull();
        plain.Should().NotBeNull();
        reversed!.GetPixel(0, 0).Should().Be(plain!.GetPixel(1, 0));
        reversed.GetPixel(1, 0).Should().Be(plain.GetPixel(0, 0));
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
