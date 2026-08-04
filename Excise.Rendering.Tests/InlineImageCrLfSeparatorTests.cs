using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// #887 — an inline image whose <c>ID</c> is followed by CRLF rather than the
/// single whitespace byte §8.9.7 specifies.
///
/// pdf.js <c>bug1065245.pdf</c> writes all three of its inline images that way:
///
///     ... I D 0d 0a ff d8 ff e0 ...
///
/// Consuming only the <c>\r</c> left the data starting on <c>\n</c>, so the
/// JPEG decoder never saw <c>ffd8</c> at offset 0, returned null, and the page
/// rendered blank — while mutool (12009 inked px) and pdftocairo (10752) both
/// drew it. After the fix excise draws 10597.
///
/// That page was filed under #887 as "vector fill present, nothing drawn — the
/// only cluster with no font, image or annotation". It has no vector fill
/// problem and three images; the classification was wrong because the failure
/// was silent.
///
/// The fixture is built here rather than taken from the gitignored corpus, and
/// the JPEG is encoded at runtime by Skia so the test carries no binary blob.
/// </summary>
public class InlineImageCrLfSeparatorTests
{
    private const int Dpi = 72;

    [Theory]
    [InlineData("\r\n", true)]   // the malformed-but-common form this fixes
    [InlineData("\n", true)]     // the spec form — must keep working
    [InlineData(" ", true)]      // a plain space is also a single whitespace
    public void InlineImage_DecodesRegardlessOfIdSeparator(string separator, bool expectDrawn)
    {
        var path = WriteTemp(InlineImagePdf(separator));
        try
        {
            using var doc = PdfDocument.Open(path);
            using var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1),
                new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

            // PDF y 120..170 on a 200-high page is RASTER y 30..80.
            var drawn = InkFraction(bmp, new SKRectI(25, 35, 115, 75)) > 0.5;
            drawn.Should().Be(expectDrawn,
                $"an inline image separated from ID by {Describe(separator)} must decode — " +
                "the JPEG begins at the first byte AFTER the separator, and mis-counting it " +
                "by one hands the decoder a buffer whose SOI marker is not at offset 0");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The narrow-ness of the fix matters as much as the fix. Only the exact
    /// CRLF pair is treated as one separator — never "skip all whitespace".
    /// For UNFILTERED inline data the first real sample byte can legitimately
    /// be 0x0A or 0x20, and swallowing it would shift every sample by one:
    /// a corrupted image instead of a missing one, which is the worse failure
    /// for a tool whose job is showing a reviewer what is on the page.
    /// </summary>
    [Fact]
    public void UnfilteredInlineImage_KeepsALeadingNewlineSampleByte()
    {
        // 2x1 8-bit gray, no filter. First sample is 0x0A — a byte that is
        // also whitespace. It must survive as DATA.
        var data = new byte[] { 0x0A, 0xFF };
        var content =
            "q 100 0 0 50 20 120 cm\n" +
            "BI /W 2 /H 1 /CS /G /BPC 8 ID \n" + Encoding.Latin1.GetString(data) + "\nEI Q";

        var path = WriteTemp(RawPdf(content));
        try
        {
            using var doc = PdfDocument.Open(path);
            using var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1),
                new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

            // Left half is near-black (0x0A), right half near-white (0xFF).
            // If the 0x0A had been eaten as whitespace the halves would swap
            // or the image would fail entirely.
            var left = InkFraction(bmp, new SKRectI(25, 35, 65, 75));
            left.Should().BeGreaterThan(0.5,
                "the 0x0A first sample is image DATA, not the ID separator — " +
                "a blanket whitespace skip would shift every sample by one byte");
        }
        finally { File.Delete(path); }
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static byte[] InlineImagePdf(string separator)
    {
        var jpeg = EncodeSolidJpeg(8, 8, new SKColor(0x10, 0x10, 0x10));
        var content =
            "q 100 0 0 50 20 120 cm\n" +
            "BI /W 8 /H 8 /CS /RGB /BPC 8 /F [/DCT] ID" + separator +
            Encoding.Latin1.GetString(jpeg) + "\nEI Q";
        return RawPdf(content);
    }

    private static byte[] EncodeSolidJpeg(int w, int h, SKColor color)
    {
        using var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bmp))
            canvas.Clear(color);
        using var image = SKImage.FromBitmap(bmp);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return encoded.ToArray();
    }

    private static byte[] RawPdf(string content)
    {
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
        };

        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Describe(string s) =>
        s == "\r\n" ? "CRLF" : s == "\n" ? "LF" : "a space";

    private static double InkFraction(SKBitmap bmp, SKRectI box)
    {
        int ink = 0, total = 0;
        for (int y = Math.Max(0, box.Top); y < Math.Min(bmp.Height, box.Bottom); y++)
            for (int x = Math.Max(0, box.Left); x < Math.Min(bmp.Width, box.Right); x++)
            {
                total++;
                var c = bmp.GetPixel(x, y);
                if (c.Red < 240 || c.Green < 240 || c.Blue < 240) ink++;
            }
        return total == 0 ? 0 : (double)ink / total;
    }

    private static string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-887-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        return p;
    }
}
