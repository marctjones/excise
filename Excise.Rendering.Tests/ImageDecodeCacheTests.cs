using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// Guards the decoded-image cache on the render image hot path (#599).
/// SKCodec / raw-image decode + colour conversion is expensive; an image
/// XObject reused on a page must be decoded ONCE and the repeat served from
/// the cache. The cache holds only the decoded pixels — every draw still runs
/// its own CTM/paint, which is what keeps the raster byte-identical (see the
/// pixel-identity Visual/Differential suites, which also exercise the
/// cross-context sharing into transparency groups / tiling patterns / soft
/// masks and would fault on a mis-scoped dispose).
/// </summary>
public class ImageDecodeCacheTests
{
    [Fact]
    public void RepeatedImageXObjectIsDecodedOnceAndReused()
    {
        var pdf = BuildTwoDrawSingleImagePdf();
        using var doc = PdfDocument.Open(pdf);
        var renderer = new SkiaRenderer();

        RenderContext.ImageBitmapCacheHits = 0;
        RenderContext.ImageBitmapCacheMisses = 0;

        using (renderer.RenderPage(doc.GetPage(1), new RenderOptions { Dpi = 96 })) { }

        long hits = RenderContext.ImageBitmapCacheHits;
        long misses = RenderContext.ImageBitmapCacheMisses;

        misses.Should().Be(1,
            "the single image XObject must be decoded exactly once");
        hits.Should().BeGreaterThan(0,
            "the second draw of the same image must be served from the cache, not re-decoded");
    }

    // Minimal PDF: one 2x2 DeviceRGB image XObject, drawn twice at different
    // positions on the page. Binary image samples are written straight to the
    // stream (an ASCII StreamWriter would corrupt them).
    private static byte[] BuildTwoDrawSingleImagePdf()
    {
        // 2x2 RGB: red, green, blue, white.
        byte[] image =
        {
            255, 0, 0,
            0, 255, 0,
            0, 0, 255,
            255, 255, 255,
        };
        const string content =
            "q 100 0 0 100 100 100 cm /Im1 Do Q " +
            "q 100 0 0 100 300 400 cm /Im1 Do Q";

        using var ms = new MemoryStream();
        var offsets = new long[6];

        void Ascii(string s) => Write(ms, Encoding.ASCII.GetBytes(s));

        Ascii("%PDF-1.4\n");

        offsets[1] = ms.Position;
        Ascii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = ms.Position;
        Ascii("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets[3] = ms.Position;
        Ascii("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
              "/Contents 4 0 R /Resources << /XObject << /Im1 5 0 R >> >> >>\nendobj\n");

        offsets[4] = ms.Position;
        Ascii($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        offsets[5] = ms.Position;
        Ascii("5 0 obj\n<< /Type /XObject /Subtype /Image /Width 2 /Height 2 " +
              $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {image.Length} >>\nstream\n");
        Write(ms, image);
        Ascii("\nendstream\nendobj\n");

        long xrefPos = ms.Position;
        Ascii("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++)
            Ascii($"{offsets[i]:D10} 00000 n \n");
        Ascii("trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n" + xrefPos + "\n%%EOF\n");

        return ms.ToArray();
    }

    private static void Write(Stream s, byte[] bytes) => s.Write(bytes, 0, bytes.Length);
}
