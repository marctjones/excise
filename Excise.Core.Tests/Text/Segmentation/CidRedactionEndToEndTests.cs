using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// End-to-end CID/Type0 (CJK) redaction on a real Identity-H PDF (issue #353):
/// redacting one CJK glyph must remove its original 2-byte code from the rebuilt
/// content stream while the surviving glyph keeps its ORIGINAL code (not a
/// Unicode/UTF-8 re-encoding a CID font can't render). This exercises the whole
/// pipeline through the real extractor + content-stream parser, not synthetic
/// Letter objects.
/// </summary>
public class CidRedactionEndToEndTests
{
    // 中 = U+4E2D (CID 0x4E2D), 文 = U+6587 (CID 0x6587), drawn via <4E2D6587> Tj.
    private static readonly byte[] ZhongCode = { 0x4E, 0x2D };
    private static readonly byte[] WenCode = { 0x65, 0x87 };

    [Fact]
    public void RedactArea_OverSecondCjkGlyph_RemovesItsCode_KeepsFirstWithOriginalCode()
    {
        var pdf = BuildIdentityHPdf();
        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);

        // Sanity: the extractor reads both CJK letters with their CID codes.
        var letters = page.Letters;
        string.Concat(letters.Select(l => l.Value)).Should().Contain("中文",
            "the Identity-H + ToUnicode font must extract as 中文");

        // Locate 文 (the 2nd glyph) and redact a band over just it.
        var wen = letters.First(l => l.Value == "文");
        var area = new PdfRectangle(wen.GlyphRectangle.Left + 0.5, wen.GlyphRectangle.Bottom - 2,
                                    wen.GlyphRectangle.Right + 2, wen.GlyphRectangle.Top + 2);
        page.RedactArea(area, GlyphRemovalStrategy.AnyOverlap);

        var saved = doc.SaveToBytes();
        var content = Encoding.Latin1.GetString(
            PdfDocument.Open(saved).GetPage(1).GetContentStreamBytes());

        content.Should().NotContain(Encoding.Latin1.GetString(WenCode),
            "the redacted glyph's original 2-byte CID code must be gone from the rebuilt stream");
        content.Should().Contain(Encoding.Latin1.GetString(ZhongCode),
            "the surviving glyph keeps its ORIGINAL 2-byte CID code");
        // And NOT re-encoded as UTF-8 (a CID font can't render Unicode bytes).
        content.Should().NotContain(Encoding.Latin1.GetString(Encoding.UTF8.GetBytes("中")),
            "the kept CJK glyph must not be emitted as UTF-8");
    }

    [Fact]
    public void RedactText_Type0FontWithSingleByteCodespaceEncoding_RemovesOnlyTargetedByte()
    {
        // #659: a Type0 font whose /Encoding is an embedded CMap declaring a
        // 1-byte codespace (not Identity-H) must be re-decoded with the
        // SAME stride by the redaction path (ContentStreamParser/GlyphRemover)
        // as TextExtractor uses to find matches — otherwise a "found" match
        // could be rebuilt with the wrong byte stride, corrupting the
        // surviving text or failing to remove the right byte.
        //
        // CID codes 0x99/0x98 (not 0x41/0x42 = ASCII 'A'/'B') deliberately
        // don't collide with PDF operator keywords (e.g. "BT") in a raw
        // substring search, and their ToUnicode targets ('Z'/'Y') are
        // deliberately NOT the same bytes as the CIDs — so a content-stream
        // check can tell "original CID byte preserved" apart from "wrongly
        // re-encoded via its Unicode value" (a CID font can't render
        // arbitrary Unicode bytes — same concern the CJK test above guards).
        var pdf = BuildSingleByteCodespacePdf();
        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);

        page.Text.Should().Be("ZY", "the 1-byte codespace must decode 2 letters from 2 bytes");

        doc.RedactText("Y", drawBlackRect: false).Should().Be(1);

        var saved = doc.SaveToBytes();
        var reopened = PdfDocument.Open(saved);
        reopened.GetPage(1).Text.Should().Be("Z", "only the targeted byte/glyph should survive");

        // Non-printable bytes are written octal-escaped in a literal PDF
        // string (0x98 -> \230, 0x99 -> \231) — check for the escape
        // sequence as written, not the raw byte value.
        var content = Encoding.Latin1.GetString(reopened.GetPage(1).GetContentStreamBytes());
        content.Should().NotContain(@"\230", "the redacted glyph's original CID byte (0x98) must be gone from the rebuilt stream");
        content.Should().Contain(@"\231", "the surviving glyph keeps its ORIGINAL CID byte (0x99)");
        content.Should().NotContain("Z", "the kept glyph must not be re-emitted via its Unicode value — a CID font can't render arbitrary Unicode bytes");
        content.Should().NotContain("Y", "the redacted glyph's Unicode value must not appear either");
    }

    /// <summary>
    /// Minimal one-page PDF: a Type0 font whose /Encoding is an embedded
    /// CMap stream declaring a 1-byte codespace (`&lt;00&gt; &lt;FF&gt;`), the
    /// shape #659 fixes. Content stream draws two 1-byte codes 0x99 0x98,
    /// mapped via ToUnicode to 'Z' and 'Y' respectively — deliberately not
    /// matching their own byte values or PDF keyword bytes.
    /// </summary>
    private static byte[] BuildSingleByteCodespacePdf()
    {
        var encodingCmap =
            "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
            "1 begincidrange\n<00> <FF> 0\nendcidrange\n" +
            "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n";
        var toUnicodeCmap =
            "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
            "2 beginbfchar\n<99> <005A>\n<98> <0059>\nendbfchar\n" +
            "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n";

        var content = "BT /F1 24 Tf 100 700 Td <9998> Tj ET";

        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> >>",
            StreamBody("", content),
            "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding 9 0 R " +
                "/DescendantFonts [6 0 R] /ToUnicode 7 0 R >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Test " +
                "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                "/FontDescriptor 8 0 R /CIDToGIDMap /Identity /DW 1000 " +
                "/W [ 152 [500] 153 [500] ] >>",
            StreamBody("", toUnicodeCmap),
            "<< /Type /FontDescriptor /FontName /Test /Flags 4 /FontBBox [0 0 1000 1000] " +
                "/ItalicAngle 0 /Ascent 1000 /Descent 0 /CapHeight 1000 /StemV 80 >>",
            StreamBody("/Type /CMap /CMapName /Test-Encoding", encodingCmap),
        };

        using var ms = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        W("%PDF-1.5\n");
        var off = new long[bodies.Length + 1];
        for (int i = 0; i < bodies.Length; i++)
        {
            off[i + 1] = ms.Position;
            W($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        long xref = ms.Position;
        W($"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++) W($"{off[i]:D10} 00000 n \n");
        W($"trailer\n<< /Root 1 0 R /Size {bodies.Length + 1} >>\nstartxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }

    /// <summary>
    /// Minimal one-page PDF whose only text is two CJK glyphs in a Type0
    /// Identity-H font with a ToUnicode CMap and /W widths (no embedded font
    /// file needed — the extractor uses widths + ToUnicode).
    /// </summary>
    private static byte[] BuildIdentityHPdf()
    {
        // ToUnicode CMap: CID code -> Unicode (identity here, since these CIDs
        // equal their code points).
        var cmap =
            "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
            "2 beginbfchar\n<4E2D> <4E2D>\n<6587> <6587>\nendbfchar\n" +
            "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n";

        var content = "BT /F1 24 Tf 100 700 Td <4E2D6587> Tj ET";

        // Object bodies (1..8).
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> >>",
            StreamBody("", content),
            "<< /Type /Font /Subtype /Type0 /BaseFont /STSong /Encoding /Identity-H " +
                "/DescendantFonts [6 0 R] /ToUnicode 7 0 R >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /STSong " +
                "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                "/FontDescriptor 8 0 R /CIDToGIDMap /Identity /DW 1000 " +
                "/W [ 20013 [1000] 25991 [1000] ] >>",
            StreamBody("", cmap),
            "<< /Type /FontDescriptor /FontName /STSong /Flags 4 /FontBBox [0 0 1000 1000] " +
                "/ItalicAngle 0 /Ascent 1000 /Descent 0 /CapHeight 1000 /StemV 80 >>",
        };

        using var ms = new MemoryStream();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        W("%PDF-1.5\n");
        var off = new long[bodies.Length + 1];
        for (int i = 0; i < bodies.Length; i++)
        {
            off[i + 1] = ms.Position;
            W($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        long xref = ms.Position;
        W($"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++) W($"{off[i]:D10} 00000 n \n");
        W($"trailer\n<< /Root 1 0 R /Size {bodies.Length + 1} >>\nstartxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }

    private static string StreamBody(string dictExtra, string content)
    {
        var data = Encoding.Latin1.GetBytes(content);
        return $"<< {dictExtra} /Length {data.Length} >>\nstream\n{content}\nendstream";
    }
}
