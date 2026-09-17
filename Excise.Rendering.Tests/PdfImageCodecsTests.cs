using AwesomeAssertions;
using BitMiracle.LibJpeg.Classic;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// #1550 — the JPEG codec Reduce File Size uses. Its output is decoded by
/// SkiaSharp (libjpeg-turbo), not by the codec that wrote it.
/// </summary>
public class PdfImageCodecsTests
{
    private static byte[] Gradient(int width, int height, int components)
    {
        var samples = new byte[width * height * components];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                for (var c = 0; c < components; c++)
                    // Smooth and never wrapping: a wrap is a hard colour edge,
                    // where chroma subsampling legitimately rings.
                    samples[(y * width + x) * components + c] = (byte)(x * 180 / width + c * 20 + y * 30 / height);
        return samples;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Encode_ProducesAJpegAnIndependentDecoderReads(int components)
    {
        var codec = PdfImageCodecs.CreateJpegCodec();
        var samples = Gradient(120, 80, components);

        var jpeg = codec.Encode(samples, 120, 80, components, quality: 90);

        jpeg.Should().NotBeNull();
        using var decoded = SKBitmap.Decode(jpeg);
        decoded.Should().NotBeNull("libjpeg-turbo, via Skia, must accept the codestream");
        decoded!.Width.Should().Be(120);
        decoded.Height.Should().Be(80);
        var pixel = decoded.GetPixel(60, 40);
        var expectedRed = samples[(40 * 120 + 60) * components];
        Math.Abs(pixel.Red - expectedRed).Should().BeLessThan(12, "quality 90 must stay close to the source");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Decode_RoundTripsTheCodecsOwnOutput(int components)
    {
        var codec = PdfImageCodecs.CreateJpegCodec();
        var samples = Gradient(64, 48, components);
        var jpeg = codec.Encode(samples, 64, 48, components, quality: 95)!;

        var decoded = codec.Decode(jpeg, 64, 48, components, colorTransform: null);

        decoded.Should().NotBeNull();
        decoded!.Length.Should().Be(samples.Length);
        var errors = samples.Select((s, i) => Math.Abs(s - decoded[i])).ToArray();
        errors.Average().Should().BeLessThan(2.0, "a colour-transform mistake shows up as a large MEAN error");
        errors.Max().Should().BeLessThan(16);
    }

    [Fact]
    public void Decode_RefusesACodestreamThatDisagreesWithTheDictionary()
    {
        var codec = PdfImageCodecs.CreateJpegCodec();
        var jpeg = codec.Encode(Gradient(64, 48, 3), 64, 48, 3, quality: 80)!;

        codec.Decode(jpeg, 65, 48, 3, null).Should().BeNull("width differs");
        codec.Decode(jpeg, 64, 48, 1, null).Should().BeNull("component count differs");
        codec.Decode([0xFF, 0xD8, 0x00], 64, 48, 3, null).Should().BeNull("truncated");
    }

    [Fact]
    public void Decode_ReadsAnAdobeRgbJpegWithoutAColorTransform()
    {
        // Written by libjpeg itself with RGB (not YCbCr) components — what the
        // Adobe APP14 transform=0 marker means. Decoding it as YCbCr would
        // scramble the colours.
        var samples = Gradient(32, 32, 3);
        var compressor = new jpeg_compress_struct();
        using var output = new MemoryStream();
        compressor.jpeg_stdio_dest(output);
        compressor.Image_width = 32;
        compressor.Image_height = 32;
        compressor.Input_components = 3;
        compressor.In_color_space = J_COLOR_SPACE.JCS_RGB;
        compressor.jpeg_set_defaults();
        compressor.jpeg_set_colorspace(J_COLOR_SPACE.JCS_RGB);
        compressor.jpeg_set_quality(100, true);
        compressor.jpeg_start_compress(true);
        var row = new[] { new byte[96] };
        for (var y = 0; y < 32; y++)
        {
            Array.Copy(samples, y * 96, row[0], 0, 96);
            compressor.jpeg_write_scanlines(row, 1);
        }

        compressor.jpeg_finish_compress();

        var decoded = PdfImageCodecs.CreateJpegCodec().Decode(output.ToArray(), 32, 32, 3, colorTransform: 0);

        decoded.Should().NotBeNull();
        samples.Select((s, i) => Math.Abs(s - decoded![i])).Max().Should().BeLessThan(10);
    }
}
