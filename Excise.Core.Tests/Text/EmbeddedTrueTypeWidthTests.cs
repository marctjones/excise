using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Fonts;
using Excise.Core.Tests.Fixtures;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// #1102 — a simple TrueType font with an embedded program (/FontFile2) but NO
/// /Widths array. The width cascade used to fall through to a flat 600 for
/// every glyph; now it reads the real advance from the embedded program.
/// </summary>
public class EmbeddedTrueTypeWidthTests
{
    /// <summary>
    /// One page, one line of text in a /TrueType font whose /FontDescriptor
    /// embeds DejaVuSans via /FontFile2, with NO /Widths on the font dict.
    /// </summary>
    private static byte[] BuildPdf(byte[] ttf)
    {
        var content = "BT /F1 100 Tf 50 700 Td (HII) Tj ET\n";
        var sb = new StringBuilder();
        var offs = new System.Collections.Generic.List<int>();
        void Obj(string b) { offs.Add(Encoding.Latin1.GetByteCount(sb.ToString())); sb.Append(b); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}endstream\nendobj\n");
        // Font: NO /Widths. WinAnsi so 'H'/'I' map by ASCII.
        Obj("5 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /DejaVuSans " +
            "/FirstChar 32 /LastChar 255 /Encoding /WinAnsiEncoding /FontDescriptor 6 0 R >>\nendobj\n");
        Obj("6 0 obj\n<< /Type /FontDescriptor /FontName /DejaVuSans /Flags 32 " +
            "/FontBBox [-1021 -463 1793 1233] /ItalicAngle 0 /Ascent 928 /Descent -236 " +
            "/CapHeight 928 /StemV 80 /FontFile2 7 0 R >>\nendobj\n");
        // The embedded program (raw ttf).
        var head = $"7 0 obj\n<< /Length {ttf.Length} /Length1 {ttf.Length} >>\nstream\n";
        var pre = Encoding.Latin1.GetByteCount(sb.ToString());
        offs.Add(pre);
        var bytes = new System.Collections.Generic.List<byte>();
        bytes.AddRange(Encoding.Latin1.GetBytes(head));
        bytes.AddRange(ttf);
        bytes.AddRange(Encoding.Latin1.GetBytes("\nendstream\nendobj\n"));
        sb.Append(Encoding.Latin1.GetString(bytes.ToArray()));

        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        foreach (var o in offs) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void SimpleTrueTypeWithoutWidths_UsesTheEmbeddedProgramAdvance_NotFlat600()
    {
        var ttf = TestFontFixtures.LoadDejaVuSansBytes();
        var font = TrueTypeFontFile.Parse(ttf);

        // Ground truth from the font itself: 'H' advance in text-space points
        // at 100pt.
        var gid = font.GidForCodepoint('H');
        gid.Should().BeGreaterThan(0, "DejaVuSans must contain 'H'");
        var expectedH = font.AdvanceWidth(gid) * 100.0 / font.UnitsPerEm;
        // Sanity: DejaVuSans 'H' is much wider than 'I'; both differ from the
        // flat-600 guess (60pt at 100pt), so the test can tell them apart.
        var flat600 = 600.0 / 1000.0 * 100.0;
        expectedH.Should().NotBeApproximately(flat600, 1.0,
            "the fixture only proves something if the real advance differs from 600");

        using var doc = PdfDocument.Open(BuildPdf(ttf));
        var letters = doc.GetPage(1).Letters.Where(l => l.Value == "H").ToList();
        letters.Should().NotBeEmpty("the 'H' must extract");

        letters[0].Width.Should().BeApproximately(expectedH, 1.0,
            "the advance must come from the embedded /FontFile2 program, not the flat-600 " +
            "fallback the cascade used before #1102");
    }
}
