using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #1131 — text hidden by matching its fill colour to the shape behind it,
/// not by a covering box. Missed by both excise audit and x-ray before this,
/// because the covering shape is drawn BEFORE the text (pairing A only looks
/// after) and the text is on top, not under.
///
/// <para>The real case is LOW CONTRAST (black-on-black), not "white-on-black"
/// — white on black is high-contrast and genuinely visible, so it must NOT
/// flag. The negative control pins that distinction.</para>
/// </summary>
public class LowContrastHiddenTextTests
{
    private const string Secret = "Louise Anne Farrar";

    private static byte[] Build(string content)
    {
        var objs = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offs = new int[objs.Length];
        for (var i = 0; i < objs.Length; i++)
        {
            offs[i] = Encoding.Latin1.GetByteCount(sb.ToString());
            sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offs) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string BoxThenText(string textColorOp) =>
        "0 0 0 rg\n137 694 340 26 re f\n" +               // black box
        $"{textColorOp}\nBT /F1 24 Tf 140 700 Td (Name: {Secret}) Tj ET\n";

    [Fact]
    public void BlackTextOnBlackBox_IsFlagged()
    {
        // No colour op -> text inherits the box's black fill. Hidden.
        using var doc = PdfDocument.Open(Build(BoxThenText("")));
        var hits = HiddenTextDetector.Scan(doc);
        hits.Should().NotBeEmpty("black-on-black text is hidden by matching colour");
        hits[0].Text.Should().Contain("Farrar");
    }

    [Fact]
    public void WhiteTextOnBlackBox_IsNotFlagged()
    {
        // NEGATIVE CONTROL. White on black is high-contrast -> genuinely
        // VISIBLE. Flagging it would be a false positive, and "white-on-black"
        // (as #1131 was titled) is exactly the case that must NOT fire.
        using var doc = PdfDocument.Open(Build(BoxThenText("1 1 1 rg")));
        HiddenTextDetector.Scan(doc).Should().BeEmpty(
            "white text on a black box is visible; only low contrast hides text");
    }

    [Fact]
    public void ContrastingTextOnBox_IsNotFlagged()
    {
        // Red text on a black box is readable. Not a leak.
        using var doc = PdfDocument.Open(Build(BoxThenText("1 0 0 rg")));
        HiddenTextDetector.Scan(doc).Should().BeEmpty(
            "contrasting text over a box is visible, not hidden");
    }
}
