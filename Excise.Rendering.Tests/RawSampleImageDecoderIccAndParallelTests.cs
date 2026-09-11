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

    // ---- #1208: row-parallel conversion ---------------------------------
    //
    // The serial oracle is the same decoder asked for ONE row at a time: a
    // Height = 1 request is below the parallel row threshold, so it always
    // converts serially. Each row of a large (parallel) decode must equal it.

    private const int LargeWidth = 1100;
    private const int LargeHeight = 1000;

    private static void AssertEveryRowMatchesASingleRowDecode(
        SKBitmap full,
        int width,
        Func<int, RawSampleImageDecodeRequest> singleRowRequest)
    {
        var fullBytes = PixelBytes(full);
        for (var row = 0; row < full.Height; row++)
        {
            using var single = RawSampleImageDecoder.Decode(singleRowRequest(row));
            single.Should().NotBeNull();
            single!.Height.Should().Be(1);
            var expected = PixelBytes(single);
            var actual = fullBytes.AsSpan(row * width * 4, width * 4).ToArray();
            if (!actual.AsSpan().SequenceEqual(expected))
                actual.Should().Equal(expected, $"row {row} of the parallel decode must equal the serial single-row decode");
        }
    }

    [Fact]
    public void ParallelCmykFastPath_MatchesSerialSingleRowDecodes()
    {
        using var document = PdfDocument.CreateNew();
        var iccBased = ParseLut16IccBasedCmyk(document);
        var samples = VariedSamples(LargeWidth, LargeHeight, 4, seed: 7);
        var request = new RawSampleImageDecodeRequest(
            Samples: samples,
            Width: LargeWidth,
            Height: LargeHeight,
            BitsPerComponent: 8,
            ColorSpace: iccBased,
            ComponentsPerPixel: 4,
            DecodeArray: null,
            ColorKeyMask: null);

        using var full = RawSampleImageDecoder.Decode(request);

        full.Should().NotBeNull();
        AssertEveryRowMatchesASingleRowDecode(full!, LargeWidth, row => request with
        {
            Samples = samples.AsSpan(row * LargeWidth * 4, LargeWidth * 4).ToArray(),
            Height = 1,
        });
    }

    [Fact]
    public void ParallelGeneralPath_WithColorKeyMask_MatchesSerialSingleRowDecodes()
    {
        // DeviceRGB through DecodeGeneral (a colour-key mask forces it) with a
        // mask that really does hide some pixels, so the per-band raw-sample
        // scratch array is exercised, not just the conversion.
        var samples = VariedSamples(LargeWidth, LargeHeight, 3, seed: 11);
        int[] mask = [0, 60, 0, 255, 100, 255];
        var request = new RawSampleImageDecodeRequest(
            Samples: samples,
            Width: LargeWidth,
            Height: LargeHeight,
            BitsPerComponent: 8,
            ColorSpace: PdfColorSpace.DeviceRGB,
            ComponentsPerPixel: 3,
            DecodeArray: null,
            ColorKeyMask: mask);

        using var full = RawSampleImageDecoder.Decode(request);

        full.Should().NotBeNull();
        PixelBytes(full!).Where((_, index) => index % 4 == 3).Should().Contain((byte)0, "the mask must hide some pixels");
        AssertEveryRowMatchesASingleRowDecode(full!, LargeWidth, row => request with
        {
            Samples = samples.AsSpan(row * LargeWidth * 3, LargeWidth * 3).ToArray(),
            Height = 1,
        });
    }

    [Fact]
    public void ParallelSubsampledGeneralPath_MatchesSerialSingleRowDecodes()
    {
        // A subsampled decode picks source row sy for target row ty; the serial
        // oracle decodes that one source row to a one-row target of the same
        // width, whose column mapping is identical. The row formula mirrors
        // RawSampleImageDecoder.MapTargetToSource.
        const int sourceWidth = 1700;
        const int sourceHeight = 1500;
        var samples = VariedSamples(sourceWidth, sourceHeight, 1, seed: 13);
        var request = new RawSampleImageDecodeRequest(
            Samples: samples,
            Width: sourceWidth,
            Height: sourceHeight,
            BitsPerComponent: 8,
            ColorSpace: PdfColorSpace.DeviceGray,
            ComponentsPerPixel: 1,
            DecodeArray: null,
            ColorKeyMask: null,
            TargetWidth: LargeWidth,
            TargetHeight: LargeHeight);

        using var full = RawSampleImageDecoder.Decode(request);

        full.Should().NotBeNull();
        full!.Width.Should().Be(LargeWidth);
        full!.Height.Should().Be(LargeHeight);
        AssertEveryRowMatchesASingleRowDecode(full!, LargeWidth, row =>
        {
            var sourceRow = Math.Clamp((int)(((row + 0.5) * sourceHeight) / LargeHeight), 0, sourceHeight - 1);
            return request with
            {
                Samples = samples.AsSpan(sourceRow * sourceWidth, sourceWidth).ToArray(),
                Height = 1,
                TargetHeight = 1,
            };
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParallelConversion_CancelledMidDecode_ThrowsOperationCanceledNeverNullOrAggregate(bool generalPath)
    {
        // A token cancelled before the call never reaches the row bands (Decode
        // checks it first), so cancel while large decodes are running and
        // require every attempt to either finish or surface cancellation as
        // OperationCanceledException — not as null ("malformed image") and not
        // wrapped in AggregateException.
        const int width = 3000;
        const int height = 3000;
        var samples = VariedSamples(width, height, 4, seed: 17);
        var cancellations = 0;
        for (var attempt = 0; attempt < 12 && cancellations == 0; attempt++)
        {
            using var cancellation = new CancellationTokenSource();
            var request = new RawSampleImageDecodeRequest(
                Samples: samples,
                Width: width,
                Height: height,
                BitsPerComponent: 8,
                ColorSpace: PdfColorSpace.DeviceCMYK,
                ComponentsPerPixel: 4,
                DecodeArray: null,
                ColorKeyMask: generalPath ? ImpossibleColorKeyMask(4) : null,
                CancellationToken: cancellation.Token);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(1 + (attempt * 3)));

            try
            {
                using var bitmap = RawSampleImageDecoder.Decode(request);
                bitmap.Should().NotBeNull("an uncancelled decode of valid samples must produce a bitmap");
            }
            catch (OperationCanceledException)
            {
                cancellations++;
            }
        }

        cancellations.Should().BeGreaterThan(0, "at least one attempt should have been cancelled mid-decode");
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
