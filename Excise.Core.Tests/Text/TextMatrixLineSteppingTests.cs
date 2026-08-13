using System;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// §9.4.2: Td/TD/T*/'/" translate the LINE MATRIX in text space —
/// <c>Tlm' = [1 0 0 1 tx ty] × Tlm</c> — so the offset must be composed
/// through the matrix's linear part: <c>e += tx·a + ty·c; f += tx·b + ty·d</c>.
///
/// The extractor applied the raw offset instead (<c>e += tx; f += ty</c>),
/// which is correct only for an unscaled, unrotated text matrix. Under the
/// ubiquitous producer idiom <c>/F1 1 Tf</c> + <c>10 0 0 10 x y Tm</c>, every
/// line advanced 1.2pt instead of 12pt, so successive lines STACKED ONTO EACH
/// OTHER in the letter model while rendering (which uses the correct §9.4.4
/// composition for glyph advances — ShowGlyph already multiplies through the
/// matrix) placed them correctly.
///
/// Consequences of the stacked-letter model, before this was fixed:
///  - #942: redacting a word destroyed the starts of the lines BELOW it —
///    their mispositioned letters genuinely overlapped the match bbox, so
///    GlyphRemover faithfully removed them. 5–36% of a document per term.
///  - #899: under a flipped matrix the offset walks the WRONG DIRECTION,
///    which is how a 1040 page put 1,043 letters up to 216pt above the page.
///
/// Glyph-advance composition was already correct; these tests pin the five
/// line-stepping operators to the same standard.
/// </summary>
public class TextMatrixLineSteppingTests
{
    [Fact]
    public void Td_UnderScaledTextMatrix_StepsInTextSpaceUnits()
    {
        // 0 -1.2 Td under scale-10 Tm = 12pt down, not 1.2pt.
        using var doc = Open(Fixture(
            "BT /F1 1 Tf 10 0 0 10 72 700 Tm (first)Tj 0 -1.2 Td (second)Tj ET"));
        var line2 = FirstLetterOf(doc, "second");

        line2.StartY.Should().BeApproximately(688, 0.5,
            "0 -1.2 Td under [10 0 0 10] must move the baseline 12pt, not 1.2pt — " +
            "the raw-offset bug stacked every line onto the first, and redaction " +
            "then destroyed the 'overlapping' neighbours (#942)");
    }

    [Fact]
    public void Td_UnderFlippedTextMatrix_StepsInTheFlippedDirection()
    {
        // The #899 signature: with d negative, -1.2 in text space moves +12 on
        // the page. The raw-offset bug moved it -1.2 — the wrong direction —
        // which is how letters ended up far off-page on flipped-matrix
        // documents.
        using var doc = Open(Fixture(
            "BT /F1 1 Tf 10 0 0 -10 72 92 Tm (first)Tj 0 -1.2 Td (second)Tj ET"));
        var line2 = FirstLetterOf(doc, "second");

        line2.StartY.Should().BeApproximately(104, 0.5,
            "ty·d = (-1.2)·(-10) = +12: under a flipped matrix the line steps UP the " +
            "page. Applying the raw offset walks the wrong way and marches letters " +
            "off the page (#899's 216pt-high block)");
    }

    [Fact]
    public void Apostrophe_UnderScaledTextMatrix_AppliesLeadingInTextSpace()
    {
        using var doc = Open(Fixture(
            "BT /F1 1 Tf 10 0 0 10 72 700 Tm 1.2 TL (first)' (second)' ET"));
        var line2 = FirstLetterOf(doc, "second");

        // first ' moves to 688, second to 676.
        line2.StartY.Should().BeApproximately(676, 0.5,
            "the ' operator's leading is a text-space distance and must be scaled " +
            "by the matrix like any other line step");
    }

    [Fact]
    public void OperatorBoundingBoxes_FollowTheSameComposition()
    {
        // The twin state machine: ContentStreamParser computes operator bboxes
        // for the fallback removal paths. Same defect, same fix — a text op on
        // line 2 must have its bbox ~12pt below line 1, not overlapping it.
        using var doc = Open(Fixture(
            "BT /F1 1 Tf 10 0 0 10 72 700 Tm (first)Tj 0 -1.2 Td (second)Tj ET"));
        var ops = doc.GetPage(1).GetContentStream().Operators
            .Where(o => o.Name == "Tj" && o.BoundingBox != null)
            .Select(o => o.BoundingBox!.Value.Normalize())
            .ToList();

        ops.Should().HaveCount(2);
        (ops[0].Bottom - ops[1].Bottom).Should().BeApproximately(12, 1.5,
            "ContentStreamParser has its own copy of the line-stepping state " +
            "machine (MoveTextPosition), and RemoveTextLinesStillContaining " +
            "trusts its bboxes — both copies must compose through the matrix");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Excise.Core.Text.Letter FirstLetterOf(PdfDocument doc, string word)
    {
        var letters = doc.GetPage(1).Letters;
        var text = string.Concat(letters.Select(l => l.Value));
        var i = text.IndexOf(word, StringComparison.Ordinal);
        i.Should().BeGreaterThanOrEqualTo(0, $"fixture sanity — '{word}' must extract");
        return letters[i];
    }

    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(pdf);

    /// <summary>One page, Helvetica as /F1, the given content stream.</summary>
    private static byte[] Fixture(string content)
    {
        var c = Encoding.ASCII.GetBytes(content);
        var objs = new (int Num, byte[] Body)[]
        {
            (1, Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>")),
            (2, Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")),
            (3, Encoding.ASCII.GetBytes(
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>")),
            (4, Encoding.ASCII.GetBytes($"<< /Length {c.Length} >>\nstream\n")
                .Concat(c).Concat(Encoding.ASCII.GetBytes("\nendstream")).ToArray()),
            (5, Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")),
        };
        using var ms = new MemoryStream();
        void W(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        W("%PDF-1.7\n");
        var offs = new long[objs.Length + 1];
        foreach (var (num, body) in objs)
        {
            offs[num] = ms.Position;
            W($"{num} 0 obj\n"); ms.Write(body, 0, body.Length); W("\nendobj\n");
        }
        var xref = ms.Position;
        W($"xref\n0 {objs.Length + 1}\n0000000000 65535 f \n");
        for (var n = 1; n <= objs.Length; n++) W($"{offs[n]:D10} 00000 n \n");
        W($"trailer\n<< /Size {objs.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
