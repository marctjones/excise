using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// #734 — horizontal advance must follow ISO 32000-1 §9.4.4 exactly:
///
///     tx = ((w0 − Tj/1000)·Tfs + Tc + Tw) · Th
///
/// for BOTH simple and Type0/CID fonts, with Tw firing only on the
/// SINGLE-byte code 32 (§9.3.3) — never on a 2-byte &lt;0020&gt;. The
/// expected positions below are computed BY HAND from the spec formula,
/// not from the extractor's own output, so the extractor is not its own
/// oracle for its advance math.
///
/// The bug fixed here: Tc/Tw were added OUTSIDE the Th (horizontal
/// scaling) factor — `w0·Tfs·Th + Tc + Tw` instead of
/// `(w0·Tfs + Tc + Tw)·Th` — drifting every glyph position on text that
/// combines Tz ≠ 100 with non-zero Tc/Tw. TextExtractor and
/// ContentStreamParser (the redaction bounds source) must apply the same
/// corrected formula; the mirror test at the bottom pins that.
/// </summary>
public class Type0SpacingAdvanceTests
{
    private const double Tolerance = 1e-9;

    [Fact]
    public void Type0_CharSpacing_AppliesPerGlyph()
    {
        // Tfs=12, w0=500/1000, Tc=5, Th=1:
        //   tx = (0.5·12 + 5)·1 = 11 → second glyph at 100 + 11.
        var pdf = BuildIdentityHPdf(
            "BT /F0 12 Tf 5 Tc 100 700 Td <00010002> Tj ET",
            widths: "[1 [500 600]]");

        var letters = ExtractLetters(pdf);

        letters.Should().HaveCount(2);
        letters[0].StartX.Should().BeApproximately(100.0, Tolerance);
        letters[1].StartX.Should().BeApproximately(111.0, Tolerance);
    }

    [Fact]
    public void Type0_TwoByteCode32_DoesNotFireWordSpacing()
    {
        // §9.3.3: word spacing applies only to a SINGLE-byte code 32. The
        // Identity-H space is the 2-byte code <0020>, so Tw must NOT fire:
        //   tx = (250/1000·12 + 0)·1 = 3 → second glyph at 103, not 113.
        var pdf = BuildIdentityHPdf(
            "BT /F0 12 Tf 10 Tw 100 700 Td <00200001> Tj ET",
            widths: "[32 [250] 1 [500]]");

        var letters = ExtractLetters(pdf);

        letters.Should().HaveCount(2);
        letters[1].StartX.Should().BeApproximately(103.0, Tolerance,
            "a 2-byte <0020> must not fire Tw (§9.3.3)");
    }

    [Fact]
    public void Type0_SingleByteCode32_FiresWordSpacing()
    {
        // A Type0 font whose embedded /Encoding CMap declares a 1-byte
        // codespace (#659 shape) DOES produce single-byte code 32, so Tw
        // fires: tx = (250/1000·12 + 10)·1 = 13 → second glyph at 113.
        var pdf = BuildOneByteCodespacePdf(
            "BT /F0 12 Tf 10 Tw 100 700 Td <2041> Tj ET",
            widths: "[32 [250] 65 [500]]");

        var letters = ExtractLetters(pdf);

        letters.Should().HaveCount(2);
        letters[1].StartX.Should().BeApproximately(113.0, Tolerance,
            "a SINGLE-byte code 32 fires Tw even in a Type0 font (§9.3.3)");
    }

    [Fact]
    public void Type0_SpacingIsScaledByHorizontalScaling()
    {
        // THE #734 formula fix. Tz=50 → Th=0.5, Tc=6, w0=500/1000, Tfs=12:
        //   correct: tx = (0.5·12 + 6)·0.5 = 6.0  → second glyph at 106
        //   old bug: tx =  0.5·12·0.5 + 6  = 9.0  → second glyph at 109
        var pdf = BuildIdentityHPdf(
            "BT /F0 12 Tf 50 Tz 6 Tc 100 700 Td <00010001> Tj ET",
            widths: "[1 [500]]");

        var letters = ExtractLetters(pdf);

        letters.Should().HaveCount(2);
        letters[1].StartX.Should().BeApproximately(106.0, Tolerance,
            "Tc sits INSIDE the Th factor: tx = (w0·Tfs + Tc)·Th (§9.4.4)");
    }

    [Fact]
    public void Type0_ZeroSpacing_ControlUnchangedUnderTz()
    {
        // Control: with Tc = Tw = 0 the fix is a no-op even at Tz ≠ 100.
        //   tx = (0.5·12 + 0)·0.5 = 3 → second glyph at 103.
        var pdf = BuildIdentityHPdf(
            "BT /F0 12 Tf 50 Tz 100 700 Td <00010001> Tj ET",
            widths: "[1 [500]]");

        var letters = ExtractLetters(pdf);

        letters.Should().HaveCount(2);
        letters[1].StartX.Should().BeApproximately(103.0, Tolerance);
    }

    [Fact]
    public void SimpleFont_SpacingIsScaledByHorizontalScaling()
    {
        // The advance path is shared, so the same §9.4.4 fix applies to
        // simple fonts: Tz=50, Tc=6, Tw=4, /Widths 500, text "A A":
        //   per 'A':   tx = (0.5·12 + 6)·0.5        = 6.0
        //   per ' ':   tx = (0.25·12 + 6 + 4)·0.5   = 6.5 (1-byte 32 fires Tw)
        // → letters at 100, 106, 112.5.
        var pdf = BuildSimpleFontPdf(
            "BT /F0 12 Tf 50 Tz 6 Tc 4 Tw 100 700 Td (A A) Tj ET");

        var letters = ExtractLetters(pdf);

        letters.Should().HaveCount(3);
        letters[0].StartX.Should().BeApproximately(100.0, Tolerance);
        letters[1].StartX.Should().BeApproximately(106.0, Tolerance);
        letters[2].StartX.Should().BeApproximately(112.5, Tolerance);
    }

    [Fact]
    public void ContentStreamParser_MirrorsExtractorBounds_UnderTcTwTz()
    {
        // Redaction bounds come from ContentStreamParser, letters from
        // TextExtractor. Under combined Tz + Tc the two advance formulas
        // must stay in lock-step, or area-redaction drifts off the letters
        // it was aimed at.
        var pdf = BuildIdentityHPdf(
            "BT /F0 12 Tf 50 Tz 6 Tc 100 700 Td <000100020001> Tj ET",
            widths: "[1 [500] 2 [600]]");

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);

        var letters = new TextExtractor(page).ExtractLetters();
        letters.Should().HaveCount(3);

        var tjOp = page.GetContentStream().Operators.First(o => o.Name == "Tj");
        tjOp.BoundingBox.Should().NotBeNull();

        var expectedLeft = letters.Min(l => l.GlyphRectangle.Left);
        var expectedRight = letters.Max(l => l.GlyphRectangle.Right);
        tjOp.BoundingBox!.Value.Left.Should().BeApproximately(expectedLeft, Tolerance);
        tjOp.BoundingBox!.Value.Right.Should().BeApproximately(expectedRight, Tolerance);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static System.Collections.Generic.IReadOnlyList<Letter> ExtractLetters(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        return new TextExtractor(page).ExtractLetters();
    }

    /// <summary>
    /// Minimal one-page Identity-H Type0 PDF. ToUnicode maps CID 1→A, 2→B,
    /// 0x20→space so every test glyph decodes to a printable letter.
    /// </summary>
    private static byte[] BuildIdentityHPdf(string content, string widths)
    {
        var toUnicode =
            "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
            "3 beginbfchar\n<0001> <0041>\n<0002> <0042>\n<0020> <0020>\nendbfchar\n" +
            "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n";

        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F0 5 0 R >> >> >>",
            StreamBody("", content),
            "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding /Identity-H " +
                "/DescendantFonts [6 0 R] /ToUnicode 7 0 R >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Test " +
                "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                $"/FontDescriptor 8 0 R /CIDToGIDMap /Identity /DW 1000 /W {widths} >>",
            StreamBody("", toUnicode),
            "<< /Type /FontDescriptor /FontName /Test /Flags 4 /FontBBox [0 0 1000 1000] " +
                "/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>",
        };
        return AssemblePdf(bodies);
    }

    /// <summary>
    /// Type0 PDF whose /Encoding is an embedded CMap stream with a UNIFORM
    /// 1-byte codespace (#659) so single-byte code 32 is reachable.
    /// </summary>
    private static byte[] BuildOneByteCodespacePdf(string content, string widths)
    {
        var encodingCmap =
            "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
            "1 begincidrange\n<00> <FF> 0\nendcidrange\n" +
            "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n";
        var toUnicode =
            "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
            "2 beginbfchar\n<20> <0020>\n<41> <0041>\nendbfchar\n" +
            "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n";

        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F0 5 0 R >> >> >>",
            StreamBody("", content),
            "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding 9 0 R " +
                "/DescendantFonts [6 0 R] /ToUnicode 7 0 R >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Test " +
                "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                $"/FontDescriptor 8 0 R /CIDToGIDMap /Identity /DW 1000 /W {widths} >>",
            StreamBody("", toUnicode),
            "<< /Type /FontDescriptor /FontName /Test /Flags 4 /FontBBox [0 0 1000 1000] " +
                "/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>",
            StreamBody("/Type /CMap /CMapName /Test-Encoding", encodingCmap),
        };
        return AssemblePdf(bodies);
    }

    /// <summary>
    /// Minimal simple (Type1) font PDF with explicit /Widths: 'A' = 500,
    /// space = 250, so advances are hand-computable.
    /// </summary>
    private static byte[] BuildSimpleFontPdf(string content)
    {
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F0 5 0 R >> >> >>",
            StreamBody("", content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica " +
                "/FirstChar 32 /LastChar 65 " +
                "/Widths [250 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 500] " +
                "/Encoding /WinAnsiEncoding >>",
        };
        return AssemblePdf(bodies);
    }

    private static byte[] AssemblePdf(string[] bodies)
    {
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
