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

    [Fact]
    public void RepeatedRenders_ReuseTheParsedIccColorSpaceAndItsConverter()
    {
        const int width = 23;
        const int height = 17;
        var samples = VariedSamples(width, height, 4, seed: 42);
        using var document = PdfDocument.Open(BuildIccBasedCmykImagePdf(width, height, samples));
        var page = document.GetPage(1);
        var imageStream = (PdfStream)document.Resolve(
            ((PdfDictionary)document.Resolve(
                ((PdfDictionary)document.Resolve(page.Resources!.GetOptional("XObject")!)).GetOptional("Im0")!)));
        var colorSpaceObject = imageStream.GetOptional("ColorSpace")!;

        using var firstRender = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 72 });
        var afterFirstRender = PdfColorSpace.Parse(colorSpaceObject, document);
        var converterAfterFirstRender = ImageColorConverter.For(afterFirstRender);

        using var secondRender = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 72 });
        var afterSecondRender = PdfColorSpace.Parse(colorSpaceObject, document);

        afterFirstRender.Type.Should().Be(PdfColorSpaceType.ICCBased);
        afterSecondRender.Should().BeSameAs(
            afterFirstRender,
            "a second render must resolve the same ICCBased instance, or the converter lattice is rebuilt");
        ImageColorConverter.For(afterSecondRender).Should().BeSameAs(converterAfterFirstRender);
        PixelBytes(secondRender).Should().Equal(PixelBytes(firstRender));
    }

    private static byte[] BuildIccBasedCmykImagePdf(int width, int height, byte[] samples)
    {
        var icc = IccBasedCmykOverprintTests.BuildLut16CmykProfile();
        var content = System.Text.Encoding.ASCII.GetBytes($"q {width} 0 0 {height} 0 0 cm /Im0 Do Q");
        var buffer = new List<byte>();
        var offsets = new long[7];
        void Append(string s) => buffer.AddRange(System.Text.Encoding.ASCII.GetBytes(s));

        Append("%PDF-1.7\n");
        offsets[1] = buffer.Count;
        Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = buffer.Count;
        Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = buffer.Count;
        Append($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Contents 4 0 R " +
               "/Resources << /XObject << /Im0 6 0 R >> >> >>\nendobj\n");
        offsets[4] = buffer.Count;
        Append($"4 0 obj\n<< /Length {content.Length} >>\nstream\n");
        buffer.AddRange(content);
        Append("\nendstream\nendobj\n");
        offsets[5] = buffer.Count;
        Append($"5 0 obj\n<< /N 4 /Length {icc.Length} >>\nstream\n");
        buffer.AddRange(icc);
        Append("\nendstream\nendobj\n");
        offsets[6] = buffer.Count;
        Append($"6 0 obj\n<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /BitsPerComponent 8 " +
               $"/ColorSpace [/ICCBased 5 0 R] /Length {samples.Length} >>\nstream\n");
        buffer.AddRange(samples);
        Append("\nendstream\nendobj\n");

        var xref = buffer.Count;
        Append("xref\n0 7\n0000000000 65535 f \n");
        for (var i = 1; i <= 6; i++)
            Append($"{offsets[i]:D10} 00000 n \n");
        Append($"trailer\n<< /Root 1 0 R /Size 7 >>\nstartxref\n{xref}\n%%EOF\n");
        return buffer.ToArray();
    }
}
