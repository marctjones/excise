using System.Linq;
using AwesomeAssertions;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Tests.Fixtures;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Authoring;

/// <summary>
/// Tests for using embedded (Unicode) fonts through the high-level
/// PdfDocumentBuilder / TextStyle facade (#398). Uses the DejaVu Sans
/// fixture embedded in this assembly (#603).
/// </summary>
public class BuilderEmbeddedFontTests
{
    private static string ExtractAll(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Join("\n", doc.GetPages().Select(p => new TextExtractor(p).ExtractText()));
    }

    [Fact]
    public void TextStyle_WithFont_ResolvesToThatFontAtStyleSize()
    {
        var embedded = PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 10);
        var style = TextStyle.Body.WithSize(20).WithFont(embedded);

        var resolved = style.ResolveFont();
        resolved.Should().BeOfType<PdfTrueTypeFont>("the embedded font overrides the base-14 family");
        resolved.Size.Should().Be(20, "the style's size is applied to the embedded font");
    }

    [Fact]
    public void Builder_DefaultFont_RendersUnicodeAndExtracts()
    {
        var pdf = PdfDocumentBuilder.Create()
            .DefaultFont(PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 11))
            .Heading("Отчёт — Café")
            .Paragraph("Ελληνικά, Кириллица, naïve, € ½ Ø")
            .KeyValue("Имя", "Ada")
            .SaveToBytes();

        var text = ExtractAll(pdf);
        text.Should().Contain("Отчёт");
        text.Should().Contain("Ελληνικά");
        text.Should().Contain("Кириллица");
        text.Should().Contain("€");
    }

    [Fact]
    public void Builder_DefaultFont_EmbedsOneSubsetAcrossSizes()
    {
        // Heading(18) + body(11) + bold key-value all use the one default font.
        var pdf = PdfDocumentBuilder.Create()
            .DefaultFont(PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 11))
            .Heading("Большой", 1)
            .Paragraph("маленький")
            .SaveToBytes();

        using var doc = PdfDocument.Open(pdf);
        // Exactly one embedded font object across the document (sizes share it).
        var fontBaseNames = doc.GetPages()
            .SelectMany(p => p.GetFonts())
            .Select(f => f.Font.GetNameOrNull("BaseFont"))
            .Distinct()
            .ToList();
        fontBaseNames.Should().HaveCount(1);
        fontBaseNames[0].Should().MatchRegex("^[A-Z]{6}\\+DejaVuSans$", "one subset-tagged embedded font");
    }

    [Fact]
    public void EmbeddedFont_WithSize_PreservesEmbeddingAndSharesGlyphSet()
    {
        var f11 = PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 11);
        var f22 = f11.WithSize(22);
        f22.Should().BeOfType<PdfTrueTypeFont>("WithSize keeps the embedded type");

        // Draw different glyphs at each size; the subset must contain both sets.
        var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(300, 200);
        using (var g = page.GetGraphics())
        {
            g.DrawString("ABC", f11, PdfBrush.Black, 20, 150);
            g.DrawString("xyz", f22, PdfBrush.Black, 20, 100);
        }
        var bytes = doc.SaveToBytes();

        using var re = PdfDocument.Open(bytes);
        // One embedded font, and both strings extract (proving both glyph sets embedded).
        re.GetPage(1).GetFonts().Should().ContainSingle();
        var text = new TextExtractor(re.GetPage(1)).ExtractText();
        text.Should().Contain("ABC");
        text.Should().Contain("xyz");
    }
}
