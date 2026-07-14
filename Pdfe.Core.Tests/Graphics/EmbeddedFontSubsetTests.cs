using System.Linq;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Fonts;
using Pdfe.Core.Graphics;
using Pdfe.Core.Primitives;
using Pdfe.Core.Tests.Fixtures;
using Pdfe.Core.Text;
using Xunit;

namespace Pdfe.Core.Tests.Graphics;

/// <summary>
/// Tests for TrueType font subsetting (#393): only the drawn glyphs are embedded,
/// the font is tagged as a subset, and text still renders/extracts. Uses the
/// DejaVu Sans fixture embedded in this assembly (#603).
/// </summary>
public class EmbeddedFontSubsetTests
{
    private static (PdfDictionary fd, long length1, string baseFont) DescriptorOf(PdfDocument doc)
    {
        var (_, font) = doc.GetPage(1).GetFonts().First();
        var descendants = doc.Resolve(font.GetOptional("DescendantFonts")!) as PdfArray;
        var cid = doc.Resolve(descendants![0]) as PdfDictionary;
        var fd = doc.Resolve(cid!.GetOptional("FontDescriptor")!) as PdfDictionary;
        var ff2 = doc.Resolve(fd!.GetOptional("FontFile2")!) as PdfStream;
        long len1 = ((PdfInteger)ff2!.GetOptional("Length1")!).Value;
        return (fd, len1, cid.GetNameOrNull("BaseFont")!);
    }

    [Fact]
    public void Subset_EmbedsFarLessThanTheFullFont()
    {
        byte[] fullBytes = TestFontFixtures.LoadDejaVuSansBytes();
        long fullSize = fullBytes.Length;

        var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(300, 120);
        using (var g = page.GetGraphics())
            g.DrawString("Hello", PdfFont.FromTrueType(fullBytes, 20), PdfBrush.Black, 30, 70);
        var bytes = doc.SaveToBytes();

        using var re = PdfDocument.Open(bytes);
        var (_, length1, baseFont) = DescriptorOf(re);
        length1.Should().BeLessThan(fullSize / 4, "the embedded subset must be far smaller than the full font");
        bytes.Length.Should().BeLessThan((int)fullSize / 2);
    }

    [Fact]
    public void Subset_TagsTheFontName()
    {
        var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(300, 120);
        using (var g = page.GetGraphics())
            g.DrawString("Hi", PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 20), PdfBrush.Black, 30, 70);

        using var re = PdfDocument.Open(doc.SaveToBytes());
        var (_, _, baseFont) = DescriptorOf(re);
        // Subset tag: six uppercase letters + '+' + the font name.
        baseFont.Should().MatchRegex("^[A-Z]{6}\\+DejaVuSans$");
    }

    [Fact]
    public void Subset_TextStillExtractsAndRoundTrips()
    {
        const string text = "Subset café résumé";
        var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(400, 120);
        using (var g = page.GetGraphics())
            g.DrawString(text, PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 18), PdfBrush.Black, 30, 70);

        var bytes = doc.SaveToBytes();
        var act = () => PdfDocument.Open(bytes).Dispose();
        act.Should().NotThrow();

        using var re = PdfDocument.Open(bytes);
        new TextExtractor(re.GetPage(1)).ExtractText().Should().Contain("Subset café résumé");
    }

    [Fact]
    public void Subsetter_RetainsGidsAndProducesValidSfnt()
    {
        byte[] fullBytes = TestFontFixtures.LoadDejaVuSansBytes();
        var full = TrueTypeFontFile.Parse(fullBytes);
        int gidH = full.GidForCodepoint('H');

        var subset = TrueTypeSubsetter.Subset(fullBytes,
            new System.Collections.Generic.HashSet<int> { 0, gidH });

        subset.Length.Should().BeLessThan(fullBytes.Length);

        // Valid TrueType sfnt header (0x00010000). The subset intentionally drops
        // cmap (a CID font addresses glyphs by GID), so we read maxp directly.
        uint sfnt = (uint)((subset[0] << 24) | (subset[1] << 16) | (subset[2] << 8) | subset[3]);
        sfnt.Should().Be(0x00010000u);

        int numGlyphs = ReadMaxpNumGlyphs(subset);
        numGlyphs.Should().Be(full.GlyphCount, "retain-gid keeps glyph ids stable");
    }

    private static int ReadMaxpNumGlyphs(byte[] sfntData)
    {
        int numTables = (sfntData[4] << 8) | sfntData[5];
        for (int i = 0, p = 12; i < numTables; i++, p += 16)
        {
            if (System.Text.Encoding.ASCII.GetString(sfntData, p, 4) == "maxp")
            {
                int off = (sfntData[p + 8] << 24) | (sfntData[p + 9] << 16) | (sfntData[p + 10] << 8) | sfntData[p + 11];
                return (sfntData[off + 4] << 8) | sfntData[off + 5];
            }
        }
        return -1;
    }

    [Fact]
    public void Subsetter_FallsBackOnNonGlyfData()
    {
        // Garbage that isn't a glyf font → return input unchanged (never throws).
        var input = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        TrueTypeSubsetter.Subset(input, new System.Collections.Generic.HashSet<int> { 0 })
            .Should().BeEquivalentTo(input);
    }
}
