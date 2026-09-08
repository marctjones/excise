using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// Render-mode evidence for image-requirements.json filters that were
/// decode-verified at the unit level but never painted through SkiaRenderer
/// and checked pixel-by-pixel. Wherever the encoded bytes matter (Flate
/// predictors, LZW+predictor), they were produced by an INDEPENDENT tool
/// (Python's Pillow/libtiff, Python's stdlib base85) rather than by excise's
/// own encoder -- an encoder written by the same hand that decodes it is not
/// independent evidence (see LzwEarlyChangeTests.cs's docstring for why that
/// approach was tried and discarded elsewhere in this codebase).
/// </summary>
public class ImageFilterRenderTests
{
    private static SKBitmap Render(byte[] pdfBytes, int dpi = 72)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-imgfilter-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdfBytes);
        try
        {
            using var doc = PdfDocument.Open(path);
            return new SkiaRenderer().RenderPage(doc.GetPage(1),
                new RenderOptions { Dpi = dpi, AntiAlias = false, BackgroundColor = SKColors.White });
        }
        finally { File.Delete(path); }
    }

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
        int xref = bytes.Count;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        bytes.AddRange(Encoding.ASCII.GetBytes(sb.ToString()));
        return bytes.ToArray();
    }

    /// <summary>
    /// Covers stream-filter:ASCII85Decode render. The stream is the real
    /// output of Python's <c>base64.a85encode</c> on four solid-red RGB
    /// pixels, plus the PDF EOD marker.
    /// </summary>
    [Fact]
    public void Ascii85EncodedRedImage_RendersSolidRed()
    {
        var data = Encoding.ASCII.GetBytes("rr<'!!!*$!!<3$!~>");
        var pdf = BuildImagePdf(2, 2,
            "/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /ASCII85Decode",
            data);

        using var bmp = Render(pdf);
        var c = bmp.GetPixel(1, 1);
        c.Red.Should().BeGreaterThan(240);
        c.Green.Should().BeLessThan(15);
        c.Blue.Should().BeLessThan(15);
    }

    /// <summary>
    /// Covers stream-filter:FlateDecode.predictor-png render. The stream is
    /// the literal IDAT payload of a real PNG written by Pillow/libpng for a
    /// 6x3 grayscale gradient -- genuine PNG-filtered, zlib-compressed
    /// scanline data, which is byte-for-byte what /Predictor 15 expects.
    /// </summary>
    [Fact]
    public void PngPredictorFlateImage_RendersTheGradient()
    {
        var data = Convert.FromHexString("789c6364d00001262e3080520010e40146");
        var pdf = BuildImagePdf(6, 3,
            "/Width 6 /Height 3 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode " +
            "/DecodeParms << /Predictor 15 /Colors 1 /BitsPerComponent 8 /Columns 6 >>",
            data);

        using var bmp = Render(pdf);
        // Expected row 0 (top of image = last PDF image row, since image
        // space is drawn top-down into the page while PDF Y grows upward --
        // pixel (0,0) in the bitmap is the FIRST decoded row): [0,40,80,120,160,200].
        bmp.GetPixel(0, 0).Red.Should().BeLessThan(10, "first sample in row 0 is 0 (black)");
        bmp.GetPixel(5, 0).Red.Should().BeInRange(190, 210, "last sample in row 0 is 200");
        bmp.GetPixel(0, 2).Red.Should().BeInRange(15, 30, "first sample in row 2 is 20");
    }

    /// <summary>
    /// Covers stream-filter:FlateDecode.predictor-tiff render. The stream is
    /// the literal strip payload of a real TIFF written by Pillow/libtiff
    /// (Deflate compression, Predictor=2/horizontal) for a 6x3 grayscale
    /// gradient.
    /// </summary>
    [Fact]
    public void TiffPredictorFlateImage_RendersTheGradient()
    {
        var data = Convert.FromHexString("789c639004017e30290726010ea901a5");
        var pdf = BuildImagePdf(6, 3,
            "/Width 6 /Height 3 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode " +
            "/DecodeParms << /Predictor 2 /Colors 1 /BitsPerComponent 8 /Columns 6 >>",
            data);

        using var bmp = Render(pdf);
        // Expected row 0: [0,25,50,75,100,125].
        bmp.GetPixel(0, 0).Red.Should().BeLessThan(10);
        bmp.GetPixel(5, 0).Red.Should().BeInRange(115, 135, "last sample in row 0 is 125");
        bmp.GetPixel(0, 2).Red.Should().BeInRange(20, 40, "first sample in row 2 is 30");
    }

    /// <summary>
    /// Covers stream-filter:LZWDecode.predictors render. The stream is the
    /// literal strip payload of a real TIFF written by Pillow/libtiff (LZW
    /// compression, Predictor=2/horizontal) for an 8x4 grayscale gradient --
    /// exercises LZWDecode and the TIFF predictor together, in one real,
    /// independently-produced stream.
    /// </summary>
    [Fact]
    public void LzwWithTiffPredictorImage_RendersTheGradient()
    {
        var data = Convert.FromHexString("800003d03820780b058202a110307c2c3d01");
        var pdf = BuildImagePdf(8, 4,
            "/Width 8 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /LZWDecode " +
            "/DecodeParms << /Predictor 2 /Colors 1 /BitsPerComponent 8 /Columns 8 >>",
            data);

        using var bmp = Render(pdf);
        // Expected row 0: [0,30,60,90,120,150,180,210].
        bmp.GetPixel(0, 0).Red.Should().BeLessThan(10);
        bmp.GetPixel(7, 0).Red.Should().BeInRange(200, 220, "last sample in row 0 is 210");
        bmp.GetPixel(0, 3).Red.Should().BeInRange(5, 25, "first sample in row 3 is 15");
    }

    /// <summary>
    /// Covers stream-filter:Crypt render. The /Crypt filter's /Identity
    /// crypt filter is a defined no-op pass-through (ISO 32000-2 §7.4.10):
    /// the "decoded" data is the raw stream bytes unchanged, so a solid-blue
    /// raw image behind /Filter /Crypt must render as solid blue.
    /// </summary>
    [Fact]
    public void CryptIdentityFilteredImage_RendersTheRawPixels()
    {
        var data = new byte[] { 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0, 255 }; // 2x2 solid blue RGB
        var pdf = BuildImagePdf(2, 2,
            "/Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /Crypt " +
            "/DecodeParms << /Name /Identity >>",
            data);

        using var bmp = Render(pdf);
        var c = bmp.GetPixel(1, 1);
        c.Blue.Should().BeGreaterThan(240);
        c.Red.Should().BeLessThan(15);
        c.Green.Should().BeLessThan(15);
    }

    /// <summary>
    /// Covers image-filter:JBIG2Decode.globals render -- the same two pdfium
    /// corpus fixtures whose DECODED byte density is checked in
    /// Jbig2GlobalsResolutionTests.cs, here rendered through the full
    /// SkiaRenderer pipeline and checked against the same mutool-calibrated
    /// ink-fraction bounds.
    /// </summary>
    [Theory]
    [InlineData("bug_631912.pdf", null, 0.0001, 0.01)]
    public void Jbig2WithGlobalsReference_RendersWithinCalibratedInkBounds(
        string fixtureName, string? subdir, double minDark, double maxDark)
    {
        var segments = subdir == null
            ? new[] { "test-pdfs", "pdfium", fixtureName }
            : new[] { "test-pdfs", "pdfium", subdir, fixtureName };
        var path = SkiaRendererTests.FindRepoFile(segments);
        Assert.SkipWhen(path == null, $"No pdfium fixture at {string.Join('/', segments)}.");

        using var doc = PdfDocument.Open(path!);
        using var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        long dark = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).Red < 128) dark++;
        double darkFraction = (double)dark / ((long)bmp.Width * bmp.Height);
        darkFraction.Should().BeInRange(minDark, maxDark);
    }

    /// <summary>
    /// Covers stream-filter:LZWDecode.early-change render -- the same real
    /// Isartor PDF/A conformance-suite fixture used for the parse-level
    /// evidence in LzwEarlyChangeTests.cs (§7.4.4.3's default EarlyChange=1),
    /// rendered through SkiaRenderer this time.
    /// </summary>
    [Fact]
    public void LzwEarlyChangeImage_RendersNotBlank()
    {
        var path = SkiaRendererTests.FindRepoFile(
            "test-pdfs", "isartor", "Isartor testsuite", "PDFA-1b", "6.1 File structure",
            "6.1.10 Filters", "isartor-6-1-10-t01-fail-a.pdf");
        Assert.SkipWhen(path == null, "No Isartor corpus fixture (scripts/download-test-pdfs.sh).");

        using var doc = PdfDocument.Open(path!);
        using var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = 72, BackgroundColor = SKColors.White });

        long nonWhite = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.Red < 250 || p.Green < 250 || p.Blue < 250) nonWhite++;
            }
        nonWhite.Should().BeGreaterThan(100,
            "the LZW-encoded indexed image (default EarlyChange=1) must paint visible content");
    }
}
