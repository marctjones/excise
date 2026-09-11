using AwesomeAssertions;
using Excise.Core.ColorSpaces;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Rendering.Tests.Visual;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// #1208: pixel-identical performance changes to raw-sample image decoding.
/// Every test here compares two routes through the production decoder that
/// must agree byte for byte; none of them reads a test-only hook.
/// </summary>
public sealed class RawSampleImageDecoderIccAndParallelTests
{
    // No 8-bit sample can fall inside [256, 256], so this colour-key mask
    // never makes a pixel transparent — but its presence forces
    // RawSampleImageDecoder.Decode off the fast path and into DecodeGeneral.
    private static int[] ImpossibleColorKeyMask(int components)
        => Enumerable.Repeat(256, components * 2).ToArray();

    private static PdfColorSpace ParseLut16IccBasedCmyk(PdfDocument document)
    {
        var iccStream = new PdfStream(
            new PdfDictionary { ["N"] = new PdfInteger(4) },
            IccBasedCmykOverprintTests.BuildLut16CmykProfile());
        var colorSpace = PdfColorSpace.Parse(
            new PdfArray(new PdfName("ICCBased"), document.AddIndirectObject(iccStream)),
            document);
        colorSpace.Type.Should().Be(
            PdfColorSpaceType.ICCBased,
            "the synthetic lut16 profile must parse, or this would test the DeviceCMYK fallback instead");
        colorSpace.Components.Should().Be(4);
        return colorSpace;
    }

    private static byte[] VariedSamples(int width, int height, int components, int seed)
    {
        var random = new Random(seed);
        var samples = new byte[width * height * components];
        random.NextBytes(samples);
        // Include the extremes and every lattice grid value explicitly; they
        // hit LatticeAxis's clamp branch differently from interior values.
        for (var i = 0; i < Math.Min(256, samples.Length); i++)
            samples[i] = (byte)i;
        return samples;
    }

    private static byte[] PixelBytes(SKBitmap bitmap)
        => SkiaBitmapPixelBuffer.GetWritableSpan(bitmap).ToArray();

    [Fact]
    public void IccBasedN4_FastPath_IsPixelIdenticalToTheGeneralPath()
    {
        using var document = PdfDocument.CreateNew();
        var iccBased = ParseLut16IccBasedCmyk(document);
        const int width = 61;
        const int height = 47;
        var samples = VariedSamples(width, height, 4, seed: 1208);

        var fastRequest = new RawSampleImageDecodeRequest(
            Samples: samples,
            Width: width,
            Height: height,
            BitsPerComponent: 8,
            ColorSpace: iccBased,
            ComponentsPerPixel: 4,
            DecodeArray: null,
            ColorKeyMask: null);

        using var fast = RawSampleImageDecoder.Decode(fastRequest);
        using var general = RawSampleImageDecoder.Decode(
            fastRequest with { ColorKeyMask = ImpossibleColorKeyMask(4) });

        fast.Should().NotBeNull();
        general.Should().NotBeNull();
        PixelBytes(fast!).Should().Equal(PixelBytes(general!));

        // And both equal the converter applied to each raw sample quadruple,
        // which is what the fast path is defined to compute.
        var converter = ImageColorConverter.For(iccBased)!;
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var (r, g, b) = converter.ToRgb(
                samples[pixel * 4],
                samples[(pixel * 4) + 1],
                samples[(pixel * 4) + 2],
                samples[(pixel * 4) + 3]);
            var color = fast!.GetPixel(pixel % width, pixel / width);
            (color.Red, color.Green, color.Blue, color.Alpha).Should().Be((r, g, b, (byte)255), $"pixel {pixel}");
        }
    }
}
