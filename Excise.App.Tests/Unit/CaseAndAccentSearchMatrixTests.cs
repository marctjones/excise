using System.Text;
using AwesomeAssertions;
using Excise.App.Services;
using Excise.Core.Document;
using Excise.Core.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Find-in-document on real pages, through the real <see cref="PdfSearchService"/>,
/// under the case rule redaction shares: Unicode full case folding when Case sensitive
/// is off. Each fixture page holds one spelling; the expected hit count is fixed by
/// construction, and a hit's text is the page's own spelling, never the fold.
/// </summary>
public sealed class CaseAndAccentSearchMatrixTests
{
    [Theory]
    [InlineData("Straße", "strasse", false, 1)]
    [InlineData("Straße", "STRASSE", false, 1)]
    [InlineData("STRASSE", "straße", false, 1)]
    [InlineData("strasse", "STRAẞE", false, 1)]
    [InlineData("Straße", "strasse", true, 0)]
    [InlineData("Straße", "Straße", true, 1)]
    [InlineData("STRASSE", "Straße", true, 0)]
    public void PageSearch_FoldsCaseFully_UnlessCaseSensitive(string spelling, string term, bool caseSensitive, int expected)
    {
        using var doc = PdfDocument.Open(Fixture([$"die {spelling} 5"]));
        var service = new PdfSearchService(NullLogger<PdfSearchService>.Instance);

        var hits = service.SearchInPage(doc.GetPage(1), term, caseSensitive: caseSensitive, wholeWordsOnly: true);

        hits.Should().HaveCount(expected, $"'{term}' over '{spelling}', case sensitive {caseSensitive}");
        foreach (var hit in hits)
        {
            hit.MatchedText.Should().Be(spelling, "the hit shows the page's own spelling, not the fold");
            hit.Context.Should().Contain(spelling);
            hit.Width.Should().BePositive();
        }
    }

    [Fact]
    public void AnnotationSearch_FoldsCaseFully()
    {
        using var doc = PdfDocument.Open(Fixture(["unrelated page text"], annotation: "Hauptstraße 1"));
        var service = new PdfSearchService(NullLogger<PdfSearchService>.Instance);

        var hit = service.SearchInPage(doc.GetPage(1), "STRASSE").Should().ContainSingle().Subject;
        hit.MatchedText.Should().Be("straße");
        hit.Context.Should().EndWith("[in annotation]");
    }

    [Fact]
    public void RegexSearch_KeepsTheRegexEnginesCaseRule()
    {
        using var doc = PdfDocument.Open(Fixture(["die Straße 5"]));
        var service = new PdfSearchService(NullLogger<PdfSearchService>.Instance);
        service.SearchInPage(doc.GetPage(1), "strasse", useRegex: true).Should().BeEmpty(
            "the regular-expression engine does not fold ß to ss");
        service.SearchInPage(doc.GetPage(1), "STRAßE", useRegex: true).Should().ContainSingle();
    }

    /// <summary>A one-page Helvetica (WinAnsi) PDF; Latin-1 letters are written as octal codes.</summary>
    internal static byte[] Fixture(IReadOnlyList<string> lines, string? annotation = null)
    {
        static string Escape(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s)
            {
                if (c is '\\' or '(' or ')') sb.Append('\\').Append(c);
                else if (c >= 0x80 && c <= 0xFF) sb.Append('\\').Append(System.Convert.ToString(c, 8));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        var content = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
            content.Append($"BT /F1 12 Tf 72 {700 - 14 * i} Td ({Escape(lines[i])}) Tj ET\n");
        var body = content.ToString();

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R " + (annotation != null ? "/Annots [6 0 R] " : "") +
                "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {body.Length} >>\nstream\n{body}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        if (annotation != null)
            objects.Add($"<< /Type /Annot /Subtype /Text /Rect [500 100 520 120] /Contents ({Escape(annotation)}) >>");

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
