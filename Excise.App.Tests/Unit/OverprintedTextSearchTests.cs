using System.Globalization;
using System.Text;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.Core.Document;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #1047 in Find: a word whose every letter is stamped three times ~0.3 pt apart
/// (faux bold by stacking) is found by its plain spelling, through the real
/// <see cref="PdfSearchService"/> and the page word store the search index uses.
/// </summary>
public sealed class OverprintedTextSearchTests
{
    [Fact]
    public void Find_MatchesAStackedWord_ByItsPlainSpelling()
    {
        using var doc = PdfDocument.Open(StackedPdf());
        var service = new PdfSearchService(NullLogger<PdfSearchService>.Instance);

        var hit = service.SearchInPage(doc.GetPage(1), "test", wholeWordsOnly: true).Should().ContainSingle().Subject;
        hit.MatchedText.Should().Be("Test");
        hit.Width.Should().BeInRange(30, 60, "the word's own box: four 24 pt letters, not three times as wide");
    }

    [Fact]
    public void Find_StillFindsAGenuineDoubleLetter()
    {
        using var doc = PdfDocument.Open(Pdf("BT /F1 24 Tf 72 700 Td [(let) 60 (ter )] TJ ET\n"));
        var service = new PdfSearchService(NullLogger<PdfSearchService>.Instance);
        service.SearchInPage(doc.GetPage(1), "letter", wholeWordsOnly: true).Should().ContainSingle();
    }

    /// <summary>"A Test word" at 24 pt Helvetica, each letter of "Test" stamped three times.</summary>
    private static byte[] StackedPdf()
    {
        var tj = new StringBuilder("[(A )");
        foreach (var (glyph, width) in new[] { ('T', 611), ('e', 556), ('s', 500), ('t', 278) })
        {
            for (var copy = 0; copy < 3; copy++)
            {
                tj.Append(" (").Append(glyph).Append(')');
                if (copy < 2) tj.Append(' ').Append((width - 12.5).ToString(CultureInfo.InvariantCulture));
            }
        }
        tj.Append(" ( word)]");
        return Pdf($"BT /F1 24 Tf 72 700 Td {tj} TJ ET\n");
    }

    private static byte[] Pdf(string body)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {body.Length} >>\nstream\n{body}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
