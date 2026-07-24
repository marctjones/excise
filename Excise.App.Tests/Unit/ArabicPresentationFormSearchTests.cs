using System;
using System.IO;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.Core.Document;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Search must find Arabic stored as PRESENTATION FORMS (U+FB50–U+FDFF,
/// U+FE70–U+FEFF) when the user types BASE letters (#632). The fixture's
/// /ToUnicode maps character codes to the shaped scalars of "سلام" —
/// seen-initial U+FEB3, lam-alef-final ligature U+FEFC (which folds to TWO
/// base letters), meem-isolated U+FEE1 — so extraction yields shaped text
/// and only presentation-form folding can bridge it to the typed needle.
/// </summary>
public class ArabicPresentationFormSearchTests
{
    /// <summary>What a user types: base letters, logical order.</summary>
    private const string BaseWord = "سلام"; // U+0633 U+0644 U+0627 U+0645

    private static readonly string ShapedWord =
        new(new[] { (char)0xFEB3, (char)0xFEFC, (char)0xFEE1 });

    private static PdfSearchService NewService() =>
        new(NullLogger<PdfSearchService>.Instance);

    [Fact]
    public void SearchInPage_BaseLetterNeedle_FindsPresentationFormWord()
    {
        using var doc = PdfDocument.Open(ShapedArabicPdf());
        var page = doc.GetPage(1);

        // Anti-vacuity: the page really carries shaped text, not base letters.
        page.Text.Should().Contain(ShapedWord);
        page.Text.Should().NotContain(BaseWord);

        var matches = NewService().SearchInPage(page, BaseWord, pageIndex: 0);

        matches.Should().NotBeEmpty(
            "a base-letter needle must find the shaped word via presentation-form folding");
        var match = matches[0];
        match.Width.Should().BeGreaterThan(0, "the match must map back to word bounds");
        match.MatchedText.Should().Be(BaseWord,
            "matched text is reported in the folded (base-letter) space the search ran in");
    }

    [Fact]
    public void SearchInPage_WholeWord_BaseLetterNeedle_FindsPresentationFormWord()
    {
        using var doc = PdfDocument.Open(ShapedArabicPdf());
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, BaseWord, wholeWordsOnly: true);

        matches.Should().NotBeEmpty(
            "whole-word comparison must fold both the word and the needle");
    }

    [Fact]
    public void SearchInPage_ShapedNeedle_AlsoFindsPresentationFormWord()
    {
        using var doc = PdfDocument.Open(ShapedArabicPdf());
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, ShapedWord, pageIndex: 0);

        matches.Should().NotBeEmpty("both sides fold, so a shaped needle matches too");
    }

    /// <summary>
    /// Minimal PDF carrying the shaped word as codes 'CBA' (visual order —
    /// the common producer encoding) with a /ToUnicode CMap mapping codes
    /// 0x41..0x43 to the presentation-form scalars in logical order. Same
    /// shape as Excise.Core.Tests.Text.RtlPdfFixtures.SingleTj.
    /// </summary>
    private static byte[] ShapedArabicPdf()
    {
        const string content = "BT /F1 24 Tf 100 700 Td (CBA) Tj ET";
        const string cmap =
            "/CIDInit /ProcSet findresource begin\n" +
            "12 dict begin\n" +
            "begincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
            "/CMapName /Adobe-Identity-UCS def\n" +
            "/CMapType 2 def\n" +
            "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
            "3 beginbfchar\n" +
            "<41> <FEB3>\n<42> <FEFC>\n<43> <FEE1>\n" +
            "endbfchar\n" +
            "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend";

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.Latin1, leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.7");
        writer.Flush();

        var offsets = new long[7];

        offsets[1] = Flush(writer, ms);
        writer.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");

        offsets[2] = Flush(writer, ms);
        writer.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");

        offsets[3] = Flush(writer, ms);
        writer.WriteLine("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                         "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj");

        offsets[4] = Flush(writer, ms);
        writer.WriteLine($"4 0 obj\n<< /Length {content.Length} >>\nstream");
        writer.WriteLine(content);
        writer.WriteLine("endstream\nendobj");

        offsets[5] = Flush(writer, ms);
        writer.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica " +
                         "/FirstChar 32 /LastChar 127 /ToUnicode 6 0 R >>\nendobj");

        offsets[6] = Flush(writer, ms);
        writer.WriteLine($"6 0 obj\n<< /Length {cmap.Length} >>\nstream");
        writer.WriteLine(cmap);
        writer.WriteLine("endstream\nendobj");

        long xrefPos = Flush(writer, ms);
        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 6; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.WriteLine("trailer\n<< /Root 1 0 R /Size 7 >>\nstartxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static long Flush(StreamWriter writer, MemoryStream ms)
    {
        writer.Flush();
        return ms.Position;
    }
}
