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

    [Theory]
    [InlineData("remove", false)]
    [InlineData("replace", false)]
    [InlineData("dictionary", false)]
    [InlineData("program-remove", false)]
    [InlineData("program-replace", false)]
    [InlineData("program-bytes", false)]
    [InlineData("widths", false)]
    [InlineData("unicode", false)]
    [InlineData("program-bytes", true)]
    public void EditedFontResource_IsRebuiltBeforeDrawingAgain(string mutation, bool samePage)
    {
        // #1919: dependent font objects are part of the shared resource identity.
        var font = PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 12);
        using var doc = PdfDocument.CreateNew();
        var first = doc.Pages.AddBlank(400, 200);
        using (var graphics = first.GetGraphics())
            graphics.DrawString("Alpha", font, PdfBrush.Black, 40, 120);
        var oldReference = (PdfReference)first.Resources!.ResolveDictionary(doc, "Font")!.Single().Value;
        var root = (PdfDictionary)doc.Resolve(oldReference);
        var cid = (PdfDictionary)doc.Resolve(root.GetArray("DescendantFonts")[0]);
        var descriptor = (PdfDictionary)doc.Resolve(cid["FontDescriptor"]);
        var programReference = (PdfReference)descriptor["FontFile2"];
        switch (mutation)
        {
            case "remove": doc.RemoveObject(oldReference.ObjectNum); break;
            case "replace": doc.ReplaceIndirectObject(oldReference.ObjectNum, new PdfDictionary()); break;
            case "dictionary": root.SetName("Encoding", "Identity-V"); break;
            case "program-remove": doc.RemoveObject(programReference.ObjectNum); break;
            case "program-replace": doc.ReplaceIndirectObject(programReference.ObjectNum, new PdfStream([0])); break;
            case "program-bytes": ((PdfStream)doc.Resolve(programReference)).EncodedData[0] ^= 0xff; break;
            case "widths": cid.GetArray("W").Get<PdfArray>(1)[0] = new PdfInteger(9999); break;
            case "unicode": ((PdfStream)doc.Resolve(root["ToUnicode"])).DecodedData[0] ^= 0xff; break;
        }

        var second = samePage ? first : doc.Pages.AddBlank(400, 200);
        string newName;
        using (var graphics = second.GetGraphics())
        {
            // Identical text avoids a glyph-map refresh repairing the corruption.
            graphics.DrawString("Alpha", font.WithSize(20), PdfBrush.Black, 40, 80);
            newName = second.Resources!.ResolveDictionary(doc, "Font")!.Last().Key.Value;
        }
        var newReference = (PdfReference)second.Resources!.ResolveDictionary(doc, "Font")![newName];
        newReference.ObjectNum.Should().NotBe(oldReference.ObjectNum);
        var rebuilt = (PdfDictionary)doc.Resolve(newReference);
        rebuilt.GetNameOrNull("Encoding").Should().Be("Identity-H");
        var newCid = (PdfDictionary)doc.Resolve(rebuilt.GetArray("DescendantFonts")[0]);
        var newDescriptor = (PdfDictionary)doc.Resolve(newCid["FontDescriptor"]);
        var newProgram = (PdfStream)doc.Resolve(newDescriptor["FontFile2"]);
        newProgram.EncodedData.Should().NotBeEmpty();
        ((PdfStream)doc.Resolve(rebuilt["ToUnicode"])).DecodedData[0].Should().Be((byte)'/');
    }

    [Fact]
    public void FontAuthoredGlyphMapAndSubsetUpdates_PreserveTheSharedResource()
    {
        var font = PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 12);
        using var doc = PdfDocument.CreateNew();
        var references = new List<int>();
        for (var i = 0; i < PageTexts.Length; i++)
        {
            var page = doc.Pages.AddBlank(400, 200);
            using (var graphics = page.GetGraphics())
                graphics.DrawString(PageTexts[i], font, PdfBrush.Black, 40, 120);
            references.Add(((PdfReference)page.Resources!.ResolveDictionary(doc, "Font")!.Single().Value).ObjectNum);
            doc.TryGetEmbeddedFontObject(font.FontProgramIdentity!, out _).Should().BeTrue("after drawing");
            // Subsetting and new glyphs are authorized authoring updates, not cache corruption.
            doc.SaveToBytes();
            doc.TryGetEmbeddedFontObject(font.FontProgramIdentity!, out _).Should().BeTrue("after saving");
        }
        references.Distinct().Should().ContainSingle();
        FontFile2Count(doc.SaveToBytes()).Should().Be(1);
    }
}
