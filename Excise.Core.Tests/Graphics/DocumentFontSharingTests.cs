using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Excise.Core.Authoring;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Primitives;
using Excise.Core.Tests.Fixtures;
using Excise.TestSupport;
using Xunit;

namespace Excise.Core.Tests.Graphics;

/// <summary>
/// #1445: an embedded font program is written once per DOCUMENT, not once per
/// page. <c>PdfPage.AddFont</c> used to de-duplicate only within the page's own
/// <c>/Resources/Font</c>, so every page that drew with the same embedded font
/// built and registered a fresh FontFile2, FontDescriptor, CIDSet, ToUnicode and
/// pre-save subset action.
///
/// <para>What must not change: every page still resolves its font, its text
/// still extracts (checked with mutool, not only excise), and the shared subset
/// keeps the glyphs of EVERY page, including a page drawn at another size.
/// Base-14 fonts stay inline per page.</para>
/// </summary>
public class DocumentFontSharingTests
{
    private static int FontFile2Count(byte[] pdf) =>
        Regex.Matches(Encoding.Latin1.GetString(pdf), @"/FontFile2\b").Count;

    private static readonly string[] PageTexts =
    [
        "Alpha page one",
        "Zebra quartz page two",
        "Wyvern jolt page three",
    ];

    /// <summary>Three pages drawn with one typeface; page two at a second size.</summary>
    private static byte[] ThreePagesOneTypefaceTwoSizes()
    {
        var font = PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 12);
        using var doc = PdfDocument.CreateNew();
        for (var i = 0; i < PageTexts.Length; i++)
        {
            var page = doc.Pages.AddBlank(400, 200);
            using var g = page.GetGraphics();
            g.DrawString(PageTexts[i], i == 1 ? font.WithSize(20) : font, PdfBrush.Black, 40, 120);
        }
        return doc.SaveToBytes();
    }

    /// <summary>The issue's own reproduction.</summary>
    [Fact]
    public void BuilderDocument_TwoPagesWithOneEmbeddedFont_WritesTheFontProgramOnce()
    {
        var pdf = PdfDocumentBuilder.Create()
            .DefaultFont(PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 11))
            .Paragraph("Page one body text")
            .PageBreak()
            .Paragraph("Page two body text")
            .TextField("Name", "name")
            .SaveToBytes();

        FontFile2Count(pdf).Should().Be(1, "both pages draw with the same embedded font program");

        using var doc = PdfDocument.Open(pdf);
        doc.PageCount.Should().Be(2);
        doc.GetPage(1).Text.Should().Contain("Page one body text");
        doc.GetPage(2).Text.Should().Contain("Page two body text");
    }

    [Fact]
    public void ThreePages_OneTypefaceAtTwoSizes_ShareOneFontObject_AndEveryPageKeepsItsText()
    {
        var pdf = ThreePagesOneTypefaceTwoSizes();

        FontFile2Count(pdf).Should().Be(1,
            "WithSize shares the parsed program and glyph set, so all three pages share one embedded font");

        using var doc = PdfDocument.Open(pdf);
        var fontObjects = new HashSet<int>();
        for (var pageNumber = 1; pageNumber <= doc.PageCount; pageNumber++)
        {
            var page = doc.GetPage(pageNumber);
            var fonts = page.Resources!.ResolveDictionary(doc, "Font");
            fonts.Should().NotBeNull($"page {pageNumber} must name its font");
            foreach (var entry in fonts!)
            {
                entry.Value.Should().BeOfType<PdfReference>($"page {pageNumber}'s embedded font is an indirect object");
                fontObjects.Add(((PdfReference)entry.Value).ObjectNum);
            }

            page.Text.Should().Contain(PageTexts[pageNumber - 1],
                $"page {pageNumber} must still extract through the shared font");
        }

        fontObjects.Should().ContainSingle("every page's /Font resource names the same indirect font object");
    }

    /// <summary>
    /// The independent check: the shared subset and ToUnicode were built from
    /// ONE glyph set, so if a page's glyphs were missing from it, mutool could not
    /// read that page's text back.
    /// </summary>
    [Fact]
    public void ThreePages_OneTypefaceAtTwoSizes_EveryPageReadsBackInMutool()
    {
        Assert.SkipUnless(MutoolTextOracle.IsAvailable, "mutool not installed");

        var text = MutoolTextOracle.ExtractAllPages(ThreePagesOneTypefaceTwoSizes());

        foreach (var expected in PageTexts)
            text.Should().Contain(expected, "an independent extractor must read every page's text");
    }

    /// <summary>The control: standard fonts were inline per page and still are.</summary>
    [Fact]
    public void StandardFonts_StayInlineInEachPagesResources()
    {
        using var doc = PdfDocument.CreateNew();
        for (var i = 0; i < 2; i++)
        {
            var page = doc.Pages.AddBlank(400, 200);
            using var g = page.GetGraphics();
            g.DrawString($"Helvetica page {i + 1}", PdfFont.Helvetica(12), PdfBrush.Black, 40, 120);
        }

        for (var pageNumber = 1; pageNumber <= 2; pageNumber++)
        {
            var fonts = doc.GetPage(pageNumber).Resources!.ResolveDictionary(doc, "Font")!;
            fonts.Should().ContainSingle();
            fonts.Single().Value.Should().BeOfType<PdfDictionary>("a base-14 font dictionary stays inline");
        }
    }
}
