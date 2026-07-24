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
/// Search must find Latin text stored with LIGATURE code points
/// (U+FB00–U+FB06) when the user types plain letters (#722). The fixture's
/// /ToUnicode maps character codes to "o ﬃ c e" — where U+FB03 (ﬃ) folds to
/// THREE plain letters — so extraction yields "oﬃce" and only ligature
/// folding can bridge it to the typed needle "office". Latin sibling of
/// ArabicPresentationFormSearchTests (#632).
/// </summary>
public class LatinLigatureSearchTests
{
    /// <summary>What a user types: plain letters.</summary>
    private const string PlainWord = "office";

    /// <summary>What the page stores/extracts: o + ﬃ + c + e.</summary>
    private static readonly string LigatedWord = "o" + (char)0xFB03 + "ce";

    private static PdfSearchService NewService() =>
        new(NullLogger<PdfSearchService>.Instance);

    [Fact]
    public void SearchInPage_PlainLetterNeedle_FindsLigatedWord()
    {
        using var doc = PdfDocument.Open(LigatedPdf());
        var page = doc.GetPage(1);

        // Anti-vacuity: the page really carries the ligature, not plain letters.
        page.Text.Should().Contain(LigatedWord);
        page.Text.Should().NotContain(PlainWord);

        var matches = NewService().SearchInPage(page, PlainWord, pageIndex: 0);

        matches.Should().NotBeEmpty(
            "a plain-letter needle must find the ligated word via ligature folding");
        var match = matches[0];
        match.Width.Should().BeGreaterThan(0, "the match must map back to word bounds");
        match.MatchedText.Should().Be(PlainWord,
            "matched text is reported in the folded (plain-letter) space the search ran in");
    }

    [Fact]
    public void SearchInPage_WholeWord_PlainLetterNeedle_FindsLigatedWord()
    {
        using var doc = PdfDocument.Open(LigatedPdf());
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, PlainWord, wholeWordsOnly: true);

        matches.Should().NotBeEmpty(
            "whole-word comparison must fold both the word and the needle");
    }

    [Fact]
    public void SearchInPage_Regex_PlainLetterPattern_FindsLigatedWord()
    {
        using var doc = PdfDocument.Open(LigatedPdf());
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, "off?ice", useRegex: true);

        matches.Should().NotBeEmpty(
            "the regex path folds the page text so plain-letter patterns match ligated text");
    }

    [Fact]
    public void SearchInPage_LigatedNeedle_AlsoFindsLigatedWord()
    {
        using var doc = PdfDocument.Open(LigatedPdf());
        var page = doc.GetPage(1);

        var matches = NewService().SearchInPage(page, LigatedWord, pageIndex: 0);

        matches.Should().NotBeEmpty("both sides fold, so a ligated needle matches too");
    }

    /// <summary>
    /// Minimal PDF carrying "oﬃce" as codes 'ABCD' with a /ToUnicode CMap
    /// mapping codes 0x41..0x44 to o, U+FB03, c, e. Same shape as
    /// Excise.Core.Tests.Text.RtlPdfFixtures.SingleTj.
    /// </summary>
    private static byte[] LigatedPdf()
    {
        const string content = "BT /F1 24 Tf 100 700 Td (ABCD) Tj ET";
        const string cmap =
            "/CIDInit /ProcSet findresource begin\n" +
            "12 dict begin\n" +
            "begincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
            "/CMapName /Adobe-Identity-UCS def\n" +
            "/CMapType 2 def\n" +
            "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
            "4 beginbfchar\n" +
            "<41> <006F>\n<42> <FB03>\n<43> <0063>\n<44> <0065>\n" +
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
