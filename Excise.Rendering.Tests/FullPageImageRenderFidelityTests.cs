using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// #1437 asked whether <see cref="SkiaRenderer.RenderPage(PdfPage, RenderOptions)"/>
/// is LOSSY for the shape a scanned page actually has -- one full-page image
/// XObject painted by a single <c>Do</c>, nothing else -- on the theory that
/// recompositing through the graphics pipeline (resample, colour convert,
/// anti-alias) degrades the pixels an OCR engine then has to read, and that a
/// caller therefore needs a separate "hand me the decoded scan" API.
///
/// These tests are that measurement. The ground truth is NOT excise's own
/// decoder: the fixture stream is UNCOMPRESSED, unfiltered RGB24 / 1-bpc gray,
/// so the bytes written into the PDF ARE the pixels, and the comparison is
/// against the byte array this file constructed.
///
/// Measured result (see the assertions): at 1:1 -- the image's native pixel
/// grid mapped onto the same number of device pixels, which is what
/// <c>PdfOcrService</c>'s 300 DPI default produces for a 300 DPI scan -- the
/// render is BIT-EXACT. Not "close", not "rounding": every channel of every
/// pixel matches, on a 1-pixel checkerboard, which is the pattern a resampling
/// filter cannot survive.
///
/// The rest of the tests pin the rest of the answer, because "lossless" on its
/// own would be misleading in both directions.
///
/// 1:1 is REACHABLE through existing public API -- the image XObject's native
/// pixel size is readable off the page, so a caller derives the DPI that maps
/// it one-to-one. That is what makes the proposed <c>GetImages()</c> redundant
/// rather than merely unnecessary: it would return bytes the caller can
/// already obtain.
///
/// And the pipeline is lossless AT 1:1 ONLY. Below it, this fixture's pixels
/// are point-sampled -- half the rows and columns discarded, no averaging.
/// ⚠️ Two caveats on that, both measured rather than assumed:
/// it is the DECODER that subsamples, not Skia's bitmap draw (see
/// <see cref="Minification_IsDoneByTheDecoder_NotBySkiasBitmapDraw"/> -- the
/// bare <c>DrawBitmap</c> with no <see cref="SKSamplingOptions"/> looks like
/// the cause and is not); and it is therefore CODEC-SPECIFIC, because the
/// decoders do not agree -- <c>RawSampleImageDecoder</c> (raw/Flate, what this
/// fixture uses) and <c>JpxImageDecoder</c> subsample nearest, deliberately,
/// for the Indexed-palette reason given in #1403, while
/// <c>DctImageDecoder</c> resizes with a linear+mipmap filter. Do not
/// generalise this file's downscale numbers to a JPEG scan.
/// </summary>
public class FullPageImageRenderFidelityTests
{
    private readonly ITestOutputHelper _output;

    public FullPageImageRenderFidelityTests(ITestOutputHelper output) => _output = output;

    private const int ImageWidth = 300;
    private const int ImageHeight = 400;

    // Page points chosen so the image maps 1:1 at 300 DPI (300 px / (300/72) = 72 pt).
    private const int PageWidthPoints = 72;
    private const int PageHeightPoints = 96;

    /// <summary>
    /// A deliberately resampling-hostile pattern: a 1-pixel checkerboard of
    /// saturated colours over most of the field, plus single-pixel black and
    /// white runs. Any filtering wider than one texel shows up as a delta.
    /// </summary>
    private static byte[] BuildRgbPattern()
    {
        var rgb = new byte[ImageWidth * ImageHeight * 3];
        for (var y = 0; y < ImageHeight; y++)
        {
            for (var x = 0; x < ImageWidth; x++)
            {
                var i = ((y * ImageWidth) + x) * 3;
                if (((x + y) & 1) == 0)
                {
                    // Alternating pixel: pure black.
                    rgb[i] = 0;
                    rgb[i + 1] = 0;
                    rgb[i + 2] = 0;
                }
                else
                {
                    // Alternating pixel: a value that varies across the field so
                    // a shifted sample is distinguishable from a filtered one.
                    rgb[i] = (byte)(x * 7 % 256);
                    rgb[i + 1] = (byte)(y * 11 % 256);
                    rgb[i + 2] = (byte)((x + y) * 3 % 256);
                }
            }
        }

        return rgb;
    }

    [Fact]
    public void FullPageRgbImage_RenderedAtItsNativeResolution_IsPixelIdenticalToTheSourceBytes()
    {
        var rgb = BuildRgbPattern();
        var pdf = BuildImagePdf(
            PageWidthPoints,
            PageHeightPoints,
            $"/Width {ImageWidth} /Height {ImageHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8",
            rgb);

        // Default RenderOptions apart from the DPI: anti-aliasing stays ON,
        // which is what a real caller (and PdfOcrService) gets.
        using var bitmap = Render(pdf, dpi: 300);

        bitmap.Width.Should().Be(ImageWidth, "300 DPI over a 72 pt page is exactly the image's pixel width");
        bitmap.Height.Should().Be(ImageHeight);

        var stats = Compare(bitmap, rgb, ImageWidth, ImageHeight);
        _output.WriteLine(
            $"1:1 render, AntiAlias=on: {stats.Identical}/{stats.Total} pixels identical, " +
            $"max per-channel delta {stats.MaxDelta}, non-opaque pixels {stats.NonOpaque}");

        stats.NonOpaque.Should().Be(0, "an opaque image over opaque paper must render fully opaque");
        stats.MaxDelta.Should().Be(0,
            "at 1:1 the graphics pipeline must not alter a single channel -- this is the measurement #1437 asked for");
        stats.Identical.Should().Be(stats.Total);
    }

    [Fact]
    public void FullPageBilevelImage_RenderedAtItsNativeResolution_IsPixelIdenticalToTheSourceBytes()
    {
        // 1 bit per component DeviceGray -- the shape a real bilevel scan has,
        // and the one where any filtering would produce grey where the source
        // has only black and white.
        var rowBytes = (ImageWidth + 7) / 8;
        var packed = new byte[rowBytes * ImageHeight];
        for (var y = 0; y < ImageHeight; y++)
        {
            for (var x = 0; x < ImageWidth; x++)
            {
                // Vertical 1-pixel stripes crossed with horizontal bands.
                var white = ((x & 1) == 0) ^ ((y / 3 & 1) == 0);
                if (white)
                    packed[(y * rowBytes) + (x / 8)] |= (byte)(0x80 >> (x % 8));
            }
        }

        var pdf = BuildImagePdf(
            PageWidthPoints,
            PageHeightPoints,
            $"/Width {ImageWidth} /Height {ImageHeight} /ColorSpace /DeviceGray /BitsPerComponent 1",
            packed);

        using var bitmap = Render(pdf, dpi: 300);
        bitmap.Width.Should().Be(ImageWidth);
        bitmap.Height.Should().Be(ImageHeight);

        var mismatches = 0;
        var greys = 0;
        var pixels = bitmap.Pixels;
        for (var y = 0; y < ImageHeight; y++)
        {
            for (var x = 0; x < ImageWidth; x++)
            {
                var bit = (packed[(y * rowBytes) + (x / 8)] >> (7 - (x % 8))) & 1;
                var expected = bit == 1 ? (byte)255 : (byte)0;
                var actual = pixels[(y * ImageWidth) + x];
                if (actual.Red != expected || actual.Green != expected || actual.Blue != expected)
                    mismatches++;
                if (actual.Red != 0 && actual.Red != 255)
                    greys++;
            }
        }

        _output.WriteLine(
            $"1:1 bilevel render: {mismatches} mismatched pixels of {ImageWidth * ImageHeight}, " +
            $"{greys} pixels that are neither black nor white");

        greys.Should().Be(0, "a bilevel scan rendered 1:1 must stay bilevel -- no filtering-induced grey");
        mismatches.Should().Be(0);
    }

    /// <summary>
    /// The other half of the #1437 answer, and the reason "lossless" alone
    /// would be a misleading close. The pipeline is lossless AT 1:1 ONLY.
    ///
    /// The source here is a 1-pixel black/white checkerboard, so the sampling
    /// kernel is directly readable off the output: an averaging (box/linear)
    /// downsample turns every 2x2 cell into mid-grey, while a point sample
    /// returns one of the two source pixels and produces NO grey at all.
    /// Measured: zero grey pixels and every output pixel lands on the same
    /// checkerboard parity -- point sampling. Half the source rows and columns
    /// are simply discarded.
    ///
    /// This is not an argument for a pixel-extraction API (asking for fewer
    /// pixels than the source has must lose something either way); it is the
    /// caller-facing rule that comes out of the #1437 measurement: render a
    /// scan at its NATIVE resolution. <c>PdfOcrService</c>'s 300 DPI default
    /// is 1:1 for a 300 DPI scan, which is why the OCR path already gets the
    /// bit-exact result above.
    /// </summary>
    [Fact]
    public void FullPageImage_RenderedBelowItsNativeResolution_IsPointSampled_NotAveraged()
    {
        var rgb = new byte[ImageWidth * ImageHeight * 3];
        for (var y = 0; y < ImageHeight; y++)
        {
            for (var x = 0; x < ImageWidth; x++)
            {
                var i = ((y * ImageWidth) + x) * 3;
                var v = ((x + y) & 1) == 0 ? (byte)0 : (byte)255;
                rgb[i] = v;
                rgb[i + 1] = v;
                rgb[i + 2] = v;
            }
        }

        var pdf = BuildImagePdf(
            PageWidthPoints,
            PageHeightPoints,
            $"/Width {ImageWidth} /Height {ImageHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8",
            rgb);

        using var bitmap = Render(pdf, dpi: 150);
        bitmap.Width.Should().Be(ImageWidth / 2);
        bitmap.Height.Should().Be(ImageHeight / 2);

        var blacks = 0;
        var whites = 0;
        var greys = 0;
        foreach (var p in bitmap.Pixels)
        {
            if (p.Red == 0 && p.Green == 0 && p.Blue == 0) blacks++;
            else if (p.Red == 255 && p.Green == 255 && p.Blue == 255) whites++;
            else greys++;
        }

        var total = bitmap.Width * bitmap.Height;
        _output.WriteLine(
            $"2:1 downsample of a 1-px black/white checkerboard: {blacks} black, {whites} white, " +
            $"{greys} intermediate, of {total} output pixels");

        greys.Should().Be(0,
            "an averaging downsample would turn every 2x2 checkerboard cell grey; measured output has none, " +
            "so minification is point-sampled and discards half the source rows and columns");
        (blacks + whites).Should().Be(total);
    }

    /// <summary>
    /// WHICH LAYER drops the pixels, measured rather than read off the code.
    ///
    /// Two candidates produce a point-sampled 2:1 downscale, and the obvious
    /// suspect is the wrong one. <c>SkiaRenderer.Images.cs</c> paints images
    /// with a bare <c>DrawBitmap</c> that passes no <see cref="SKSamplingOptions"/>,
    /// which looks like the culprit -- but since #1403 the raw-sample decoder is
    /// handed the DEVICE target size and subsamples during decode, so the
    /// bitmap reaching <c>DrawBitmap</c> is already device-sized and drawn 1:1.
    ///
    /// The two disagree about WHICH source pixel survives, which is what makes
    /// this measurable. <c>RawSampleImageDecoder.MapTargetToSource</c> is
    /// <c>(t + 0.5) * source / target</c>, so a 2:1 reduction selects source
    /// indices 1, 3, 5... -- ODD. Skia's own nearest sampling of an already
    /// 300-wide bitmap into 150 device pixels would select 2t -- EVEN.
    ///
    /// So: a source whose EVEN columns are white and ODD columns are black
    /// comes out all-black if the decoder subsampled, all-white if Skia did,
    /// and grey if anything averaged. Measured: all black -- the decoder owns
    /// this, and the missing sampling options on <c>DrawBitmap</c> are not what
    /// a caller is feeling. Worth pinning, because "add SKSamplingOptions to
    /// the DrawBitmap call" is the plausible-looking fix that would change
    /// nothing here.
    /// </summary>
    [Fact]
    public void Minification_IsDoneByTheDecoder_NotBySkiasBitmapDraw()
    {
        var rgb = new byte[ImageWidth * ImageHeight * 3];
        for (var y = 0; y < ImageHeight; y++)
        {
            for (var x = 0; x < ImageWidth; x++)
            {
                var i = ((y * ImageWidth) + x) * 3;
                var v = (x & 1) == 0 ? (byte)255 : (byte)0;   // even column white, odd column black
                rgb[i] = v;
                rgb[i + 1] = v;
                rgb[i + 2] = v;
            }
        }

        var pdf = BuildImagePdf(
            PageWidthPoints,
            PageHeightPoints,
            $"/Width {ImageWidth} /Height {ImageHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8",
            rgb);

        using var bitmap = Render(pdf, dpi: 150);
        bitmap.Width.Should().Be(ImageWidth / 2);

        var blacks = 0;
        var whites = 0;
        var greys = 0;
        foreach (var p in bitmap.Pixels)
        {
            if (p.Red == 0 && p.Green == 0 && p.Blue == 0) blacks++;
            else if (p.Red == 255 && p.Green == 255 && p.Blue == 255) whites++;
            else greys++;
        }

        var total = bitmap.Width * bitmap.Height;
        _output.WriteLine(
            $"2:1 downsample of even-white/odd-black columns: {blacks} black, {whites} white, " +
            $"{greys} intermediate, of {total}");

        greys.Should().Be(0, "neither candidate path averages");
        blacks.Should().Be(total,
            "odd source columns survived, which is the decoder's (t+0.5)*source/target mapping -- " +
            "Skia's own nearest sampling would have kept the even (white) columns");
    }

    /// <summary>
    /// "Rendering at 1:1 is lossless" is only a useful answer if a caller can
    /// FIND 1:1. This pins that the whole route is public API: read the image
    /// XObject's native pixel size off the page, derive the DPI that maps it
    /// one-to-one, render, get the source bytes back exactly. Nothing internal,
    /// no new surface -- which is why #1437's proposed <c>GetImages()</c> was
    /// closed as moot rather than implemented.
    /// </summary>
    [Fact]
    public void NativePixelSize_IsDiscoverableThroughPublicApi_AndYieldsTheBitExactRender()
    {
        var rgb = BuildRgbPattern();
        var pdf = BuildImagePdf(
            PageWidthPoints,
            PageHeightPoints,
            $"/Width {ImageWidth} /Height {ImageHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8",
            rgb);

        var path = Path.Combine(Path.GetTempPath(), $"excise-imgfidelity-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            using var doc = PdfDocument.Open(path);
            var page = doc.GetPage(1);

            // Public API only.
            var image = (Excise.Core.Primitives.PdfStream)page.GetXObject("Im0")!;
            var nativeWidth = image.GetInt("Width");
            var nativeHeight = image.GetInt("Height");
            nativeWidth.Should().Be(ImageWidth);
            nativeHeight.Should().Be(ImageHeight);

            var dpi = (int)Math.Round(72.0 * nativeWidth / page.Width);
            _output.WriteLine(
                $"derived 1:1 DPI from public metadata: {nativeWidth}x{nativeHeight} px on " +
                $"{page.Width}x{page.Height} pt -> {dpi} DPI");

            using var bitmap = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = dpi });
            var stats = Compare(bitmap, rgb, ImageWidth, ImageHeight);
            stats.MaxDelta.Should().Be(0);
            stats.Identical.Should().Be(stats.Total);
        }
        finally { File.Delete(path); }
    }

    private static (int Identical, int Total, int MaxDelta, int NonOpaque) Compare(
        SKBitmap bitmap, byte[] rgb, int width, int height)
    {
        var identical = 0;
        var maxDelta = 0;
        var nonOpaque = 0;
        var pixels = bitmap.Pixels;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 3;
                var actual = pixels[(y * width) + x];
                var dr = Math.Abs(actual.Red - rgb[i]);
                var dg = Math.Abs(actual.Green - rgb[i + 1]);
                var db = Math.Abs(actual.Blue - rgb[i + 2]);
                var delta = Math.Max(dr, Math.Max(dg, db));
                if (delta == 0)
                    identical++;
                if (delta > maxDelta)
                    maxDelta = delta;
                if (actual.Alpha != 255)
                    nonOpaque++;
            }
        }

        return (identical, width * height, maxDelta, nonOpaque);
    }

    private static SKBitmap Render(byte[] pdfBytes, int dpi)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-imgfidelity-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdfBytes);
        try
        {
            using var doc = PdfDocument.Open(path);
            return new SkiaRenderer().RenderPage(doc.GetPage(1), new RenderOptions { Dpi = dpi });
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// One page, one image XObject, one <c>Do</c> filling it -- structurally
    /// what a scanned page is. The image stream is unfiltered, so its bytes are
    /// the pixels and no decoder sits between the fixture and the assertion.
    /// </summary>
    private static byte[] BuildImagePdf(int pageW, int pageH, string imageDictExtra, byte[] streamData)
    {
        var content = $"q {pageW} 0 0 {pageH} 0 0 cm /Im0 Do Q";
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {pageW} {pageH}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R " +
            "/Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            $"5 0 obj\n<< /Type /XObject /Subtype /Image {imageDictExtra} /Length {streamData.Length} >>\nstream\n",
        };

        var bytes = new List<byte>(Encoding.ASCII.GetBytes("%PDF-1.7\n"));
        var offsets = new List<int>();
        foreach (var o in objects)
        {
            offsets.Add(bytes.Count);
            bytes.AddRange(Encoding.ASCII.GetBytes(o));
            if (o.Contains("/Subtype /Image"))
            {
                bytes.AddRange(streamData);
                bytes.AddRange(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
            }
        }

        var sb = new StringBuilder();
        var xref = bytes.Count;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        bytes.AddRange(Encoding.ASCII.GetBytes(sb.ToString()));
        return bytes.ToArray();
    }
}
