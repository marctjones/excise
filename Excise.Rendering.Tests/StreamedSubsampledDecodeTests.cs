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

    private static byte[] RandomSamples(int width, int height, int comps, int bpc, int seed)
    {
        var rowBytes = bpc == 1 ? (width + 7) / 8 : bpc == 8 ? width * comps : ((width * comps * bpc) + 7) / 8;
        var bytes = new byte[rowBytes * height];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
