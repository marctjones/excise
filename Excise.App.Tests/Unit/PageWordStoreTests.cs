using System.Text;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Services;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Live search reads each page's compact words once and keeps them (#1485).
/// </summary>
/// <remarks>
/// Letters are bounded to a few pages per document. Search read words through
/// them, so every search re-walked every evicted page: repeat live search on
/// irs-1040-instructions.pdf went from ~25 ms to ~485 ms. These count walks
/// through the page's own counter rather than timing anything, and pin the
/// other half of keeping data: a rewritten page is walked again and its old
/// words are never served.
/// </remarks>
public sealed class PageWordStoreTests
{
    private static readonly PdfSearchService Service = new(NullLogger<PdfSearchService>.Instance);

    [Fact]
    public void LiveSearch_Repeated_WalksNoPageTwice()
    {
        var pageCount = PdfDocument.PageLetterCacheCapacity + 4;
        using var doc = PdfDocument.Open(MultiPagePdf(pageCount, p => $"Needle{p} haystack words"));

        var first = Service.Search(doc, "haystack");
        first.Should().HaveCount(pageCount);
        var walks = Walks(doc);
        walks.Should().OnlyContain(n => n == 1, "the first search walks each page exactly once");

        var second = Service.Search(doc, "haystack");
        Service.Search(doc, "Needle3", wholeWordsOnly: true).Should().ContainSingle();
        Service.Search(doc, @"Needle\d", useRegex: true).Should().HaveCount(pageCount);

        Walks(doc).Should().Equal(walks, "a page whose words are already known must not be walked again");
        second.Select(Row).Should().Equal(first.Select(Row));
    }

    [Fact]
    public void LiveSearch_AfterContentRewrite_WalksOnlyThatPageAgain_AndServesNoStaleWords()
    {
        var pageCount = PdfDocument.PageLetterCacheCapacity + 2;
        using var doc = PdfDocument.Open(MultiPagePdf(pageCount,
            p => p == 1 ? "Alpha OLDTERM tail" : $"Beta{p} filler"));

        Service.Search(doc, "OLDTERM").Should().ContainSingle();
        doc.GetPage(1).SetContentStreamBytes(
            Encoding.Latin1.GetBytes("BT /F1 12 Tf 72 700 Td (Alpha NEWTERM tail) Tj ET\n"));
        var walks = Walks(doc);

        Service.Search(doc, "OLDTERM").Should().BeEmpty("the rewritten page's old words must not be served");
        Service.Search(doc, "NEWTERM").Should().ContainSingle().Which.PageIndex.Should().Be(0);

        var after = Walks(doc);
        after[0].Should().Be(walks[0] + 1, "the rewritten page is walked once more");
        after.Skip(1).Should().Equal(walks.Skip(1), "untouched pages keep their words");
    }

    [Fact]
    public void LiveSearch_AfterRedaction_DoesNotFindTheRemovedTerm()
    {
        var pageCount = PdfDocument.PageLetterCacheCapacity + 2;
        using var doc = PdfDocument.Open(MultiPagePdf(pageCount,
            p => p is 1 or 5 ? $"Keep{p} SECRETTERM tail" : $"Keep{p} plain"));

        Service.Search(doc, "SECRETTERM").Should().HaveCount(2);

        doc.RedactText("SECRETTERM", drawBlackRect: false).VerifiedRemovals.Should().Be(2);

        Service.Search(doc, "SECRETTERM").Should().BeEmpty("search must reflect the redacted bytes");
        Service.Search(doc, "Keep5").Should().ContainSingle("the redacted page's other words are re-read, not lost");
    }

    private static List<int> Walks(PdfDocument doc) =>
        Enumerable.Range(1, doc.PageCount).Select(p => doc.GetPage(p).TextWalkCount).ToList();

    private static string Row(SearchMatch m) =>
        $"{m.PageIndex}|{m.MatchedText}|{BitConverter.DoubleToInt64Bits(m.X):X}|{BitConverter.DoubleToInt64Bits(m.Y):X}|" +
        $"{BitConverter.DoubleToInt64Bits(m.Width):X}|{BitConverter.DoubleToInt64Bits(m.Height):X}|{m.Context}";

    private static byte[] MultiPagePdf(int pageCount, Func<int, string> pageText)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "__PAGES__",
        };

        int fontObj = 2 + pageCount * 2 + 1;
        var kids = new List<string>();
        for (int i = 0; i < pageCount; i++)
        {
            int pageObj = 3 + i * 2;
            kids.Add($"{pageObj} 0 R");
            bodies.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                       $"/Contents {pageObj + 1} 0 R /Resources << /Font << /F1 {fontObj} 0 R >> >> >>");
            var content = $"BT /F1 12 Tf 72 700 Td ({pageText(i + 1)}) Tj ET\n";
            bodies.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        bodies.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        bodies[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count {pageCount} >>";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        var offsets = new long[bodies.Count + 1];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        long xref = sb.Length;
        sb.Append($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Count; i++) sb.Append($"{offsets[i]:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Root 1 0 R /Size {bodies.Count + 1} >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
