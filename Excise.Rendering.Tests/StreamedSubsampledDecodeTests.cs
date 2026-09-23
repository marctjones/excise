using System.IO.Compression;
using AwesomeAssertions;
using Excise.Core.ColorSpaces;
using Excise.Core.Primitives;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// F2 (#1207/#1677): a subsampled raw-sample Flate decode streams source rows into
/// the target grid instead of inflating the whole source array first. The property
/// that matters is PIXEL IDENTITY with the materialised path — same
/// <c>DecodeGeneral</c> reader, fed a grid that holds exactly the samples it would
/// have selected — so the matrix below is the gate, and it is deliberately broad:
/// every depth the reader has a branch for, every component count, odd widths so the
/// byte-aligned row strides carry padding, and targets that shrink one axis, both,
/// or neither-but-one.
/// </summary>
public class StreamedSubsampledDecodeTests
{
    public static IEnumerable<object[]> Matrix()
    {
        foreach (var bpc in new[] { 1, 2, 4, 8, 16 })
        foreach (var comps in new[] { 1, 3, 4 })
        foreach (var srcW in new[] { 7, 13, 64 })
        foreach (var (tw, th) in new[] { (3, 2), (5, 5), (srcW, 1), (1, 9) })
            yield return new object[] { bpc, comps, srcW, 9, Math.Min(tw, srcW), Math.Min(th, 9) };
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void StreamedGrid_DecodesToTheSamePixels_AsTheMaterialisedSubsampledPath(
        int bpc, int comps, int srcW, int srcH, int tgtW, int tgtH)
    {
        var colorSpace = comps switch { 1 => PdfColorSpace.DeviceGray, 3 => PdfColorSpace.DeviceRGB, _ => PdfColorSpace.DeviceCMYK };
        var full = RandomSamples(srcW, srcH, comps, bpc, seed: bpc * 1000 + comps * 100 + srcW);

        using var materialised = RawSampleImageDecoder.Decode(new RawSampleImageDecodeRequest(
            full, srcW, srcH, bpc, colorSpace, comps, DecodeArray: null, ColorKeyMask: null,
            TargetWidth: tgtW, TargetHeight: tgtH));
        materialised.Should().NotBeNull();

        var grid = RawSampleImageDecoder.BuildSubsampledGrid(
            new MemoryStream(full), srcW, srcH, comps, bpc, tgtW, tgtH, CancellationToken.None);
        grid.Should().NotBeNull("a complete source always yields a grid");
        using var streamed = RawSampleImageDecoder.DecodePreSubsampled(new RawSampleImageDecodeRequest(
            grid!, tgtW, tgtH, bpc, colorSpace, comps, DecodeArray: null, ColorKeyMask: null));
        streamed.Should().NotBeNull();

        streamed!.Width.Should().Be(materialised!.Width);
        streamed.Height.Should().Be(materialised.Height);
        streamed.Bytes.Should().Equal(materialised.Bytes,
            $"bpc={bpc} comps={comps} {srcW}x{srcH}->{tgtW}x{tgtH}: the streamed grid must hold exactly the samples the " +
            "materialised subsampled decode reads, in the packing DecodeGeneral expects — including the 1-bpc one-bit-per-pixel stride");
    }

    [Fact]
    public void ATruncatedSource_YieldsNoGrid_SoTheCallerFallsBack()
    {
        var full = RandomSamples(16, 16, 3, 8, seed: 7);
        var truncated = new MemoryStream(full, 0, full.Length / 2);

        RawSampleImageDecoder.BuildSubsampledGrid(truncated, 16, 16, 3, 8, 4, 4, CancellationToken.None)
            .Should().BeNull("a partial grid would draw the missing rows as garbage; the materialised path owns truncation (#878)");
    }

    [Fact]
    public void DecodeArrayAndColorKey_SurviveTheStreamedPath()
    {
        // Colour key on raw samples and a /Decode inversion: both are per-sample and
        // must see the same raw values through the grid as through the full array.
        var full = RandomSamples(12, 6, 3, 8, seed: 3);
        var key = new[] { (int)full[0], full[0], full[1], full[1], full[2], full[2] };
        var decode = new[] { 1.0, 0.0, 1.0, 0.0, 1.0, 0.0 };

        using var materialised = RawSampleImageDecoder.Decode(new RawSampleImageDecodeRequest(
            full, 12, 6, 8, PdfColorSpace.DeviceRGB, 3, decode, key, TargetWidth: 6, TargetHeight: 3));
        var grid = RawSampleImageDecoder.BuildSubsampledGrid(new MemoryStream(full), 12, 6, 3, 8, 6, 3, CancellationToken.None)!;
        using var streamed = RawSampleImageDecoder.DecodePreSubsampled(new RawSampleImageDecodeRequest(
            grid, 6, 3, 8, PdfColorSpace.DeviceRGB, 3, decode, key));

        streamed!.Bytes.Should().Equal(materialised!.Bytes);
    }

    public static IEnumerable<object[]> PredictorMatrix()
    {
        foreach (var predictor in new[] { 2, 10, 12, 14, 15 })
        foreach (var bpc in new[] { 1, 4, 8, 16 })
        foreach (var comps in new[] { 1, 3 })
        foreach (var srcW in new[] { 7, 64 })
        foreach (var (tw, th) in new[] { (3, 2), (srcW, 1), (1, 9) })
            yield return new object[] { predictor, bpc, comps, srcW, 9, Math.Min(tw, srcW), Math.Min(th, 9) };
    }

    /// <summary>
    /// #1677: a /Predictor image streams too, through <c>PdfPredictor.TryOpenDecodeStream</c>.
    /// Oracle: the materialised path's own input, <c>PdfPredictor.ApplyIfNeeded</c> on the
    /// whole array, decoded at the same target. Filter bytes cycle through every PNG type.
    /// </summary>
    [Theory]
    [MemberData(nameof(PredictorMatrix))]
    public void PredictedStreamedGrid_DecodesToTheSamePixels_AsTheMaterialisedPath(
        int predictor, int bpc, int comps, int srcW, int srcH, int tgtW, int tgtH)
    {
        var colorSpace = comps == 1 ? PdfColorSpace.DeviceGray : PdfColorSpace.DeviceRGB;
        var parms = new PdfDictionary();
        parms.SetInt("Predictor", predictor);
        parms.SetInt("Colors", comps);
        parms.SetInt("BitsPerComponent", bpc);
        parms.SetInt("Columns", srcW);
        var encoded = RandomPredictedRows(predictor, comps, bpc, srcW, srcH, seed: predictor * 7 + bpc * 5 + comps * 3 + srcW);

        var full = Excise.Core.Filters.PdfPredictor.ApplyIfNeeded(encoded, parms);
        using var materialised = RawSampleImageDecoder.Decode(new RawSampleImageDecodeRequest(
            full, srcW, srcH, bpc, colorSpace, comps, DecodeArray: null, ColorKeyMask: null,
            TargetWidth: tgtW, TargetHeight: tgtH));

        using var source = Excise.Core.Filters.PdfPredictor.TryOpenDecodeStream(new MemoryStream(encoded), parms)!;
        var grid = RawSampleImageDecoder.BuildSubsampledGrid(source, srcW, srcH, comps, bpc, tgtW, tgtH, CancellationToken.None);
        grid.Should().NotBeNull();
        using var streamed = RawSampleImageDecoder.DecodePreSubsampled(new RawSampleImageDecodeRequest(
            grid!, tgtW, tgtH, bpc, colorSpace, comps, DecodeArray: null, ColorKeyMask: null));

        streamed!.Bytes.Should().Equal(materialised!.Bytes,
            $"predictor={predictor} bpc={bpc} comps={comps} {srcW}x{srcH}->{tgtW}x{tgtH}");
    }

    /// <summary>
    /// #1677 end to end: a page drawing large predicted Flate images small renders the
    /// same pixels whether the renderer streams them (release on, the CLI/thumbnail
    /// default) or materialises them (release off, the viewer). Covers the PdfStream
    /// wiring and the Indexed 4-bpc + PNG shape of pdfium's bug_2034 (#1386).
    /// </summary>
    [Fact]
    public void APageOfPredictedImages_RendersIdentically_StreamedOrMaterialised()
    {
        var bytes = PredictedImagesDocument();

        using var materialisedDoc = Excise.Core.Document.PdfDocument.Open(bytes);
        using var materialised = RenderPage(materialisedDoc, release: false);
        using var streamedDoc = Excise.Core.Document.PdfDocument.Open(bytes);
        using var streamed = RenderPage(streamedDoc, release: true);

        materialised.Bytes.Should().Contain(b => b != 0xFF, "the images actually drew");
        streamed.Bytes.Should().Equal(materialised.Bytes);
    }

    private static SKBitmap RenderPage(Excise.Core.Document.PdfDocument doc, bool release)
        => new SkiaRenderer().RenderPage(doc.GetPage(1), new RenderOptions
        {
            Dpi = 72,
            AntiAlias = false,
            BackgroundColor = SKColors.White,
            ReleaseDecodedImageSamples = release,
        });

    private static byte[] PredictedImagesDocument()
    {
        const int w = 101, h = 67;
        using var doc = Excise.Core.Document.PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 200);
        var xobjects = new PdfDictionary();
        var content = new System.Text.StringBuilder();
        var images = new (string Name, int Predictor, int Bpc, int Comps, PdfObject ColorSpace)[]
        {
            ("Png", 11, 8, 3, new PdfName("DeviceRGB")),
            ("Paeth", 15, 8, 1, new PdfName("DeviceGray")),
            ("Tiff", 2, 8, 3, new PdfName("DeviceRGB")),
            ("Idx", 10, 4, 1, new PdfArray(new PdfName("Indexed"), new PdfName("DeviceRGB"), new PdfInteger(3),
                new PdfString(new byte[] { 0, 0, 0, 255, 0, 0, 0, 255, 0, 255, 255, 255 }))),
        };
        for (var i = 0; i < images.Length; i++)
        {
            var (name, predictor, bpc, comps, cs) = images[i];
            var dict = new PdfDictionary();
            dict.SetName("Type", "XObject");
            dict.SetName("Subtype", "Image");
            dict.SetInt("Width", w);
            dict.SetInt("Height", h);
            dict.SetInt("BitsPerComponent", bpc);
            dict["ColorSpace"] = cs;
            dict.SetName("Filter", "FlateDecode");
            var parms = new PdfDictionary();
            parms.SetInt("Predictor", predictor);
            parms.SetInt("Colors", comps);
            parms.SetInt("BitsPerComponent", bpc);
            parms.SetInt("Columns", w);
            dict["DecodeParms"] = parms;
            xobjects[name] = doc.AddIndirectObject(new PdfStream(dict, Zlib(RandomPredictedRows(predictor, comps, bpc, w, h, seed: i + 1))));
            content.Append($"q 40 0 0 30 {10 + (i % 2) * 100} {10 + (i / 2) * 100} cm /{name} Do Q ");
        }

        var resources = new PdfDictionary();
        resources["XObject"] = xobjects;
        page.Dictionary["Resources"] = resources;
        page.SetContentStreamBytes(System.Text.Encoding.ASCII.GetBytes(content.ToString()));
        return doc.SaveToBytes();
    }

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return output.ToArray();
    }

    /// <summary>Random predicted rows; PNG rows carry filter types cycling 0..4.</summary>
    private static byte[] RandomPredictedRows(int predictor, int comps, int bpc, int width, int height, int seed)
    {
        var rowBytes = (comps * width * bpc + 7) / 8;
        var stride = predictor >= 10 ? rowBytes + 1 : rowBytes;
        var bytes = new byte[stride * height];
        new Random(seed).NextBytes(bytes);
        if (predictor >= 10)
        {
            for (var r = 0; r < height; r++)
                bytes[r * stride] = (byte)(r % 5);
        }

        return bytes;
    }

    private static byte[] RandomSamples(int width, int height, int comps, int bpc, int seed)
    {
        var rowBytes = bpc == 1 ? (width + 7) / 8 : bpc == 8 ? width * comps : ((width * comps * bpc) + 7) / 8;
        var bytes = new byte[rowBytes * height];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
