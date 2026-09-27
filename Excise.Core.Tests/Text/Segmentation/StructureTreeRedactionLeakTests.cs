using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// Structure-tree text carriers survive glyph removal (issue #636).
///
/// A tagged PDF carries text in more than the content stream. /ActualText on a
/// marked-content span is the *real* text that span represents (used for
/// ligatures, hyphenation, and OCR spans), and /Alt is the alternate
/// description of a figure. Acrobat, screen readers, and any tag-aware
/// extractor read both.
///
/// Glyph-level redaction rewrites the content stream. If it does not also
/// scrub the structure tree, the sensitive string survives in /ActualText or
/// /Alt — and the redaction looks perfect.
///
/// The trap these tests exist to close: the assertion CLAUDE.md mandates,
///     PdfTestHelpers.ExtractAllText(pdf).Should().NotContain(secret)
/// reads the CONTENT STREAM. It cannot see the structure tree, so it PASSES on
/// a leaking document. Every assertion here therefore checks the *saved bytes*
/// — the only carrier-agnostic ground truth. If the secret is anywhere in the
/// file, in any carrier, these fail.
/// </summary>
public class StructureTreeRedactionLeakTests
{
    private const string Secret = "SECRET";
    private const string Keep = "KEEPME";

    [Fact]
    public void RedactArea_RemovesActualTextFromTheStructureTree()
    {
        var pdf = CreateTaggedPdf();
        var page = pdf.GetPage(1);

        RedactWord(page, Secret);

        var saved = Save(pdf);

        // The content-stream assertion — the one the codebase mandates today.
        page.Text.Should().NotContain(Secret, "glyphs must leave the content stream");

        // The assertion that actually matters. /ActualText is a text carrier;
        // if it still holds the secret, the redaction did not happen.
        SavedPdfLeakScanner.FindTerm(saved, Secret).Should().BeEmpty(
            "/ActualText on the redacted span still spells out the secret. Text extraction " +
            "reads the content stream and reports the document clean, while Acrobat and every " +
            "screen reader read the name straight out of the structure tree.");
    }

    [Fact]
    public void RedactArea_RemovesAltTextFromTheStructureTree()
    {
        var pdf = CreateTaggedPdf();
        var page = pdf.GetPage(1);

        RedactWord(page, Secret);

        SavedPdfLeakScanner.FindTerm(Save(pdf), "Photo of " + Secret).Should().BeEmpty(
            "/Alt on a Figure element is an alternate *description* — it routinely restates " +
            "the very content being redacted, and it survives glyph removal untouched.");
    }

    [Fact]
    public void RedactArea_LeavesUnrelatedStructureTreeTextIntact()
    {
        // The inverse guard: scrubbing the structure tree must not turn into
        // deleting it. A redaction that strips every tag would satisfy the
        // leak assertions above while destroying the document's accessibility
        // for whoever we send it to (#631).
        var pdf = CreateTaggedPdf();
        var page = pdf.GetPage(1);

        RedactWord(page, Secret);

        var saved = SavedPdfLeakScanner.AllCarriersText(Save(pdf));
        saved.Should().Contain(Keep,
            "text outside the redaction region must survive in the structure tree");
        saved.Should().Contain("/StructTreeRoot",
            "the structure tree itself must survive — redaction scrubs the sensitive nodes, " +
            "it does not strip the document's tagging and make it inaccessible");
    }

    /// <summary>
    /// #1880: pass 2 matched a carrier against the removed words with plain
    /// ordinal <c>Contains</c>, so an <c>/Alt</c> that restates the word with the
    /// other quote or dash survived, while the page matcher (#1871) reads both
    /// spellings as one. The page draws <paramref name="drawn"/> (WinAnsi), the
    /// Figure's <c>/Alt</c> holds <paramref name="alt"/> (PDFDocEncoding).
    /// </summary>
    [Theory]
    [InlineData(@"O\222Brien", "(Photo of O'Brien)", "Photo of O'Brien")]
    [InlineData("O'Brien", @"(Photo of O\220Brien)", "Photo of O’Brien")]
    [InlineData(@"12\226345", "(Photo of 12-345)", "Photo of 12-345")]
    public void RedactArea_RemovesAnAltThatSpellsTheRemovedWordTypographically(string drawn, string alt, string held)
    {
        var input = CreateTaggedPdfBytes(
            $"BT /F1 24 Tf 100 700 Td ({drawn}) Tj ET " +
            $"/Span <</MCID 1>> BDC BT /F1 24 Tf 100 500 Td ({Keep}) Tj ET EMC",
            alt: alt);
        SavedPdfLeakScanner.FindTerm(input, held).Should().NotBeEmpty("input-side control");
        using var pdf = PdfDocument.Open(input);

        RedactLine(pdf.GetPage(1), 700);

        SavedPdfLeakScanner.FindTerm(Save(pdf), held).Should().BeEmpty(
            "the Figure's /Alt restates the removed word in the spelling the page matcher folds to");
    }

    /// <summary>
    /// #1880, the inline marked-content carrier: a span that does NOT enclose the
    /// redacted glyphs is reached only by the removed-text match, which must fold
    /// as the page matcher does.
    /// </summary>
    [Theory]
    [InlineData(@"O\222Brien", "(Photo of O'Brien)", "Photo of O'Brien")]
    [InlineData("O'Brien", @"(Photo of O\220Brien)", "Photo of O’Brien")]
    public void RedactArea_RemovesAnInlineAltThatSpellsTheRemovedWordTypographically(string drawn, string alt, string held)
    {
        var input = CreateTaggedPdfBytes(
            $"BT /F1 24 Tf 100 700 Td ({drawn}) Tj ET " +
            $"/Span <</MCID 1 /Alt {alt}>> BDC BT /F1 24 Tf 100 500 Td ({Keep}) Tj ET EMC");
        SavedPdfLeakScanner.FindTerm(input, held).Should().NotBeEmpty("input-side control");
        using var pdf = PdfDocument.Open(input);

        RedactLine(pdf.GetPage(1), 700);

        SavedPdfLeakScanner.FindTerm(Save(pdf), held).Should().BeEmpty(
            "the span's /Alt restates the removed word in the spelling the page matcher folds to");
    }

    /// <summary>
    /// #1849 item 1: a span may name its property list (<c>/Span /MC0 BDC</c>,
    /// §14.6.2) and carry its <c>/MCID</c> there. The element owning that MCID
    /// holds an <c>/ActualText</c> that does not restate the painted glyphs, so
    /// only the structural pass can reach it; the element owning the span that
    /// survives keeps its own.
    /// </summary>
    [Fact]
    public void RedactArea_RemovesTheCarrierOfAnElementWhoseSpanNamesItsMcid()
    {
        var input = CreateTaggedPdfBytes(
            $"/Span /MC0 BDC BT /F1 24 Tf 100 700 Td ({Secret}) Tj ET EMC " +
            $"/Span /MC1 BDC BT /F1 24 Tf 100 500 Td ({Keep}) Tj ET EMC",
            secretCarrier: "/ActualText (Quillfeather)",
            keepCarrier: "/ActualText (Wrenfield)",
            resources: "/Properties << /MC0 << /MCID 0 >> /MC1 << /MCID 1 >> >>");
        SavedPdfLeakScanner.FindTerm(input, "Quillfeather").Should().NotBeEmpty("input-side control");
        using var pdf = PdfDocument.Open(input);

        RedactLine(pdf.GetPage(1), 700);

        var saved = Save(pdf);
        SavedPdfLeakScanner.FindTerm(saved, "Quillfeather").Should().BeEmpty(
            "the element owns MCID 0, which the redacted span carries through /Properties");
        SavedPdfLeakScanner.FindTerm(saved, "Wrenfield").Should().NotBeEmpty(
            "MCID 1's span was not redacted, so its element keeps its /ActualText");
    }

    private static void RedactWord(PdfPage page, string word)
    {
        var letters = page.Letters
            .Where(l => word.Contains(l.Value, System.StringComparison.Ordinal))
            .ToList();
        letters.Should().NotBeEmpty($"fixture must render '{word}' as glyphs");
        RedactLine(page, letters[0].GlyphRectangle.Bottom);
    }

    /// <summary>Area-redact the whole line of glyphs whose baseline box sits at <paramref name="bottom"/>.</summary>
    private static void RedactLine(PdfPage page, double bottom)
    {
        var run = page.Letters
            .Where(l => System.Math.Abs(l.GlyphRectangle.Bottom - bottom) < 10.0)
            .ToList();
        run.Should().NotBeEmpty($"fixture must draw a line near y={bottom}");

        var area = new PdfRectangle(
            run.Min(l => l.GlyphRectangle.Left) - 1,
            run.Min(l => l.GlyphRectangle.Bottom) - 1,
            run.Max(l => l.GlyphRectangle.Right) + 1,
            run.Max(l => l.GlyphRectangle.Top) + 1).Normalize();

        page.RedactArea(area, RedactionOptions.Default with { DrawBox = false });
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using var ms = new MemoryStream();
        pdf.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// A tagged PDF whose sensitive string appears in three carriers at once:
    /// the content stream (glyphs), /ActualText on the marked-content span, and
    /// /Alt on a Figure. Only the first is visible to text extraction.
    /// </summary>
    private static PdfDocument CreateTaggedPdf() => PdfDocument.Open(CreateTaggedPdfBytes(
        $"/Span <</MCID 0>> BDC BT /F1 24 Tf 100 700 Td ({Secret}) Tj ET EMC " +
        $"/Span <</MCID 1>> BDC BT /F1 24 Tf 100 500 Td ({Keep}) Tj ET EMC"));

    /// <summary>
    /// The tagged page around <paramref name="content"/>: element 8 owns MCID 0,
    /// element 9 owns MCID 1, and a Figure with no MCID carries <paramref name="alt"/>.
    /// /F1 is WinAnsi, so <c>\222</c> draws U+2019.
    /// </summary>
    private static byte[] CreateTaggedPdfBytes(
        string content,
        string secretCarrier = $"/ActualText ({Secret})",
        string keepCarrier = $"/ActualText ({Keep})",
        string alt = $"(Photo of {Secret})",
        string resources = "")
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");

        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            $"/Resources << /Font << /F1 5 0 R >> {resources} >> /Contents 4 0 R /StructParents 0 >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");

        // StructTreeRoot -> Document -> [ Span(ActualText=SECRET), Span(KEEPME), Figure(Alt=...) ]
        Obj("6 0 obj\n<< /Type /StructTreeRoot /K [7 0 R] >>\nendobj\n");
        Obj("7 0 obj\n<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R 9 0 R 10 0 R] >>\nendobj\n");
        Obj($"8 0 obj\n<< /Type /StructElem /S /Span /P 7 0 R /Pg 3 0 R /K 0 {secretCarrier} >>\nendobj\n");
        Obj($"9 0 obj\n<< /Type /StructElem /S /Span /P 7 0 R /Pg 3 0 R /K 1 {keepCarrier} >>\nendobj\n");
        Obj($"10 0 obj\n<< /Type /StructElem /S /Figure /P 7 0 R /Pg 3 0 R /Alt {alt} >>\nendobj\n");

        int xrefPos = sb.Length;
        sb.Append("xref\n0 11\n0000000000 65535 f \n");
        foreach (var off in offsets)
            sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 11 /Root 1 0 R >>\nstartxref\n").Append(xrefPos).Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
