using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1180 — the certain channel must surface a VISIBLE failed redaction: readable
/// text drawn on a black box (white-on-black), which is neither occluded nor
/// low-contrast. The safety property is just as important: a legitimate dark
/// BANNER (wide relative to its text) must NOT be flagged, or the detector
/// floods every inverse-video heading with false positives.
/// </summary>
public class HiddenTextVisibleRedactionTests
{
    // A one-page PDF: draw a black rectangle, then WHITE text on top of it.
    private static byte[] WhiteOnBlack(double boxX, double boxW, string text)
    {
        var content =
            $"0 0 0 rg {boxX} 100 {boxW} 22 re f\n" +   // black box (earlier)
            $"1 1 1 rg BT /F1 14 Tf 105 104 Td ({text}) Tj ET\n";   // white text on it (later)
        var body = Encoding.Latin1.GetBytes(content);
        return Encoding.Latin1.GetBytes(
            "%PDF-1.7\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n" +
            $"4 0 obj\n<< /Length {body.Length} >>\nstream\n{content}endstream\nendobj\n" +
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n" +
            "trailer\n<< /Root 1 0 R /Size 6 >>\n%%EOF\n");
    }

    [Fact]
    public void ReadableTextOnATightBlackBox_IsSurfaced()
    {
        // Box 120 pt wide — sized to the word, a redaction shape.
        using var doc = PdfDocument.Open(WhiteOnBlack(boxX: 100, boxW: 120, "SECRET"));
        var hits = HiddenTextDetector.Scan(doc);

        hits.Should().Contain(h => h.Text.Contains("SECRET") && h.HiddenBy.Contains("redaction-shaped"),
            "white text on a box sized to cover it is a redaction that did not take");
    }

    [Fact]
    public void ReadableTextOnAWideDarkBanner_IsNotFlagged()
    {
        // Box spans the full page width — a banner, not a redaction of the word.
        using var doc = PdfDocument.Open(WhiteOnBlack(boxX: 0, boxW: 612, "Heading"));
        var hits = HiddenTextDetector.Scan(doc);

        hits.Should().NotContain(h => h.HiddenBy.Contains("redaction-shaped"),
            "a full-width dark banner is legitimate inverse-video, not a failed redaction");
    }
}
