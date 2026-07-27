using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Document;

/// <summary>
/// Parse + round-trip tests for embedded page thumbnails (/Thumb, ISO 32000-2:2020
/// §12.3.4, issue #331). excise parses and preserves the thumbnail stream on save
/// but deliberately does not decode/render it — that's the renderer's job when a
/// thumbnail strip falls back for pages without one.
/// </summary>
public class PdfPageThumbnailTests
{
    [Fact]
    public void ThumbnailStream_NoThumbEntry_ReturnsNull()
    {
        var pdf = MakePdfWithThumbnail(includeThumb: false);
        using var doc = PdfDocument.Open(pdf);

        doc.GetPage(1).ThumbnailStream.Should().BeNull();
    }

    [Fact]
    public void ThumbnailStream_ThumbPresent_ParsesDictionaryAndBytes()
    {
        var pdf = MakePdfWithThumbnail(includeThumb: true);
        using var doc = PdfDocument.Open(pdf);

        var thumb = doc.GetPage(1).ThumbnailStream;
        thumb.Should().NotBeNull();
        thumb!.GetInt("Width").Should().Be(32);
        thumb.GetInt("Height").Should().Be(24);
        thumb.GetNameOrNull("ColorSpace").Should().Be("DeviceGray");
        thumb.DecodedData.Should().Equal(Encoding.ASCII.GetBytes(ThumbnailPixelData));
    }

    [Fact]
    public void ThumbnailStream_RoundTrip_SurvivesSaveAndReopen()
    {
        var pdf = MakePdfWithThumbnail(includeThumb: true);
        using var doc = PdfDocument.Open(pdf);

        var saved = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(saved);

        var thumb = reopened.GetPage(1).ThumbnailStream;
        thumb.Should().NotBeNull();
        thumb!.GetInt("Width").Should().Be(32);
        thumb.GetInt("Height").Should().Be(24);
        thumb.DecodedData.Should().Equal(Encoding.ASCII.GetBytes(ThumbnailPixelData));
    }

    [Fact]
    public void ThumbnailStream_RoundTrip_DoesNotAppearOnPlainDocument()
    {
        var pdf = MakePdfWithThumbnail(includeThumb: false);
        using var doc = PdfDocument.Open(pdf);

        var saved = doc.SaveToBytes();
        using var reopened = PdfDocument.Open(saved);

        reopened.GetPage(1).ThumbnailStream.Should().BeNull();
    }

    // ─── Helper: PDF builder ───────────────────────────────────────────────

    // 32*24 = 768 one-byte gray pixels; using a short repeating ASCII pattern
    // keeps the fixture readable — content correctness isn't the point, byte
    // preservation across save/reopen is.
    private const string ThumbnailPixelData = "THUMBPIXELDATA-32x24-DEVICEGRAY-PLACEHOLDER-BYTES-0123456789";

    private static byte[] MakePdfWithThumbnail(bool includeThumb)
    {
        var thumbBytes = Encoding.ASCII.GetBytes(ThumbnailPixelData);

        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.7");

        long catalogPos = sb.Length;
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");

        long pagesPos = sb.Length;
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");

        long thumbPos = sb.Length;
        if (includeThumb)
        {
            sb.AppendLine("4 0 obj");
            sb.AppendLine($"<< /Type /XObject /Subtype /Image /Width 32 /Height 24 " +
                           $"/ColorSpace /DeviceGray /BitsPerComponent 8 /Length {thumbBytes.Length} >>");
            sb.AppendLine("stream");
            sb.Append(ThumbnailPixelData);
            sb.AppendLine();
            sb.AppendLine("endstream");
            sb.AppendLine("endobj");
        }

        long pagePos = sb.Length;
        sb.AppendLine("3 0 obj");
        var thumbEntry = includeThumb ? " /Thumb 4 0 R" : "";
        sb.AppendLine($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]{thumbEntry} >>");
        sb.AppendLine("endobj");

        long xrefPos = sb.Length;
        int size = includeThumb ? 5 : 4;
        sb.AppendLine("xref");
        sb.AppendLine($"0 {size}");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine($"{catalogPos:D10} 00000 n ");
        sb.AppendLine($"{pagesPos:D10} 00000 n ");
        sb.AppendLine($"{pagePos:D10} 00000 n ");
        if (includeThumb)
            sb.AppendLine($"{thumbPos:D10} 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine($"<< /Size {size} /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
