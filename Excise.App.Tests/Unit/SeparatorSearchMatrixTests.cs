using System.Text;
using AwesomeAssertions;
using Excise.App.Models;
using Excise.App.Services;
using Excise.Core.Document;
using Excise.Core.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// The find-in-document separator rule on real pages, through the real
/// <see cref="PdfSearchService"/>: a hyphen and a space in the term match each
/// other by default ("Hyphen = space"), and matching no separator at all is
/// opt-in ("No space too"). Each fixture is one page holding one spelling of
/// "open source"; the expected hit count is fixed by construction.
/// </summary>
public sealed class SeparatorSearchMatrixTests
{
    private static readonly SeparatorMatching Closed = new(HyphenAsSpace: true, AllowNoSeparator: true);

    /// <summary>The page each spelling is drawn on: one or two lines, x 72, 14 pt apart.</summary>
    private static string[] Lines(string spelling) => spelling switch
    {
        "space" => ["an open source tool"],
        "hyphen" => ["an open-source tool"],
        "plain wrap" => ["an open", "source tool"],
        "hyphen wrap" => ["an open-", "source tool"],
        "en dash" => ["an open\u2013source tool"],
        "em dash" => ["an open\u2014source tool"],
        "closed" => ["an opensource tool"],
        _ => throw new ArgumentOutOfRangeException(nameof(spelling)),
    };

    public static TheoryData<string, string, string, int> Matrix()
    {
        var data = new TheoryData<string, string, string, int>();
        foreach (var term in new[] { "open source", "open-source" })
        {
            // Default: every separated spelling, never the closed compound.
            foreach (var spelling in new[] { "space", "hyphen", "plain wrap", "hyphen wrap", "en dash", "em dash" })
                data.Add(spelling, term, "default", 1);
            data.Add("closed", term, "default", 0);

            // No space too: the closed compound as well.
            foreach (var spelling in new[] { "space", "hyphen", "plain wrap", "hyphen wrap", "en dash", "em dash", "closed" })
                data.Add(spelling, term, "closed", 1);
        }

        // Exact: the behaviour before these options, measured 2026-10-11. The
        // space term finds the space and the plain wrap; the hyphen term finds the
        // hyphen and the dashes (the fold reads a dash as a hyphen); NEITHER finds
        // the hyphenated wrap.
        foreach (var (spelling, space, hyphen) in new[]
                 {
                     ("space", 1, 0), ("hyphen", 0, 1), ("plain wrap", 1, 0), ("hyphen wrap", 0, 0),
                     ("en dash", 0, 1), ("em dash", 0, 1), ("closed", 0, 0),
                 })
        {
            data.Add(spelling, "open source", "exact", space);
            data.Add(spelling, "open-source", "exact", hyphen);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void PageSearch_FindsTheSpellingsTheSeparatorRuleNames(string spelling, string term, string mode, int expected)
    {
        var separators = mode switch
        {
            "default" => SeparatorMatching.Default,
            "closed" => Closed,
            _ => SeparatorMatching.Exact,
        };
        using var doc = PdfDocument.Open(Fixture(Lines(spelling)));
        var service = new PdfSearchService(NullLogger<PdfSearchService>.Instance);

        var hits = service.SearchInPage(doc.GetPage(1), term, separators: separators);

        hits.Should().HaveCount(expected, $"'{term}' over the {spelling} spelling, {mode} separators");
        foreach (var hit in hits)
        {
            hit.MatchedText.Should().StartWith("open").And.EndWith("source",
                "the match covers the whole spelling, separator run included");
            hit.Width.Should().BePositive();
        }
    }

    [Fact]
    public void TheDocumentOverload_DefaultsToHyphenAsSpace()
    {
        // A caller that passes no rule gets the default, as every front end does.
        using var doc = PdfDocument.Open(Fixture(Lines("hyphen wrap")));
        var service = new PdfSearchService(NullLogger<PdfSearchService>.Instance);
        service.Search(doc, "open source").Should().ContainSingle();
    }

    [Fact]
    public void RegexSearch_IsUnaffectedByTheSeparatorRule()
    {
        // The options do not reach a regular expression: the pattern says what it means.
        using var doc = PdfDocument.Open(Fixture(Lines("hyphen")));
        var service = new PdfSearchService(NullLogger<PdfSearchService>.Instance);
        service.SearchInPage(doc.GetPage(1), "open source", useRegex: true, separators: Closed)
            .Should().BeEmpty();
        service.SearchInPage(doc.GetPage(1), "open.source", useRegex: true).Should().ContainSingle();
    }

    [Theory]
    [InlineData("default", 1)]
    [InlineData("exact", 0)]
    public void AnnotationSearch_AppliesTheSameRule(string mode, int expected)
    {
        using var doc = PdfDocument.Open(Fixture(["unrelated page text"], annotation: "an open-source tool"));
        var service = new PdfSearchService(NullLogger<PdfSearchService>.Instance);

        var hits = service.SearchInPage(doc.GetPage(1), "open source",
            separators: mode == "default" ? SeparatorMatching.Default : SeparatorMatching.Exact);

        hits.Count(h => h.Context.EndsWith("[in annotation]", StringComparison.Ordinal)).Should().Be(expected);
        if (expected > 0)
            hits.Single().MatchedText.Should().Be("open-source");
    }

    /// <summary>A one-page Helvetica (WinAnsi) PDF; an en dash is code 0x96, an em dash 0x97.</summary>
    private static byte[] Fixture(IReadOnlyList<string> lines, string? annotation = null)
    {
        static string Escape(string s) => s
            .Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")
            .Replace("\u2013", "\\226").Replace("\u2014", "\\227");

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
