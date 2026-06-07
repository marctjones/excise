using System.IO;
using System.Linq;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Graphics;
using Pdfe.Core.Primitives;
using Pdfe.Core.Text;
using Xunit;

namespace Pdfe.Core.Tests.Graphics;

/// <summary>
/// Tests for CFF-based OpenType ('OTTO') font embedding + Unicode text (#393).
/// CFF fonts are embedded as a complete OTTO sfnt with a CIDFontType0 descendant
/// and FontFile3 stream (unlike TrueType which uses FontFile2 + CIDFontType2).
/// Tests use real system OpenType fonts, skipping cleanly if unavailable.
/// </summary>
public class CffOpenTypeEmbeddingTests
{
    // Prefer LinLibertine (more common in development), fallback to Cantarell
    private const string LinLibertineOtf = "/usr/share/fonts/opentype/linux-libertine/LinLibertine_RB.otf";
    private const string CantarellOtf = "/usr/share/fonts/opentype/cantarell/Cantarell-VF.otf";

    private static string FindCffFont()
    {
        if (File.Exists(LinLibertineOtf)) return LinLibertineOtf;
        if (File.Exists(CantarellOtf)) return CantarellOtf;
        return null!;
    }

    private static string ExtractAll(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Join("\n", doc.GetPages().Select(p => new TextExtractor(p).ExtractText()));
    }

    private static byte[] AuthorWith(string text, out PdfFont font, string? fontPath = null)
    {
        fontPath ??= FindCffFont();
        font = PdfFont.FromFile(fontPath, 18);
        var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(400, 200);
        using (var g = page.GetGraphics())
            g.DrawString(text, font, PdfBrush.Black, 40, 120);
        return doc.SaveToBytes();
    }

    [Fact]
    public void FromFile_CffFont_ProducesType0EmbeddedFont()
    {
        var fontPath = FindCffFont();
        Assert.SkipUnless(!string.IsNullOrEmpty(fontPath), "No CFF OpenType font found on system");

        var font = PdfFont.FromFile(fontPath!, 12);
        font.Should().BeOfType<PdfTrueTypeFont>();
        // Identity-H encodes 2-byte glyph ids as a hex string.
        font.EncodeString("AB").Should().StartWith("<").And.EndWith(">");
        font.EncodeString("AB").Should().HaveLength(10); // <XXXXXXXX>
    }

    [Fact]
    public void CffFont_DictionaryUsesCIDFontType0AndFontFile3()
    {
        var fontPath = FindCffFont();
        Assert.SkipUnless(!string.IsNullOrEmpty(fontPath), "No CFF OpenType font found on system");

        var pdf = AuthorWith("Hello CFF", out _, fontPath!);
        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);

        var (_, fontDict) = page.GetFonts().First();
        fontDict.GetNameOrNull("Subtype").Should().Be("Type0");
        fontDict.GetNameOrNull("Encoding").Should().Be("Identity-H");
        doc.Resolve(fontDict.GetOptional("ToUnicode")!).Should().BeOfType<PdfStream>();

        var descendants = doc.Resolve(fontDict.GetOptional("DescendantFonts")!) as PdfArray;
        var cid = doc.Resolve(descendants![0]) as PdfDictionary;
        cid!.GetNameOrNull("Subtype").Should().Be("CIDFontType0", "CFF uses CIDFontType0, not CIDFontType2");
        var fd = doc.Resolve(cid.GetOptional("FontDescriptor")!) as PdfDictionary;
        doc.Resolve(fd!.GetOptional("FontFile3")!).Should().BeOfType<PdfStream>("CFF is embedded in FontFile3");
    }

    [Fact]
    public void CffFont_FontFile3HasOpenTypeSubtype()
    {
        var fontPath = FindCffFont();
        Assert.SkipUnless(!string.IsNullOrEmpty(fontPath), "No CFF OpenType font found on system");

        var pdf = AuthorWith("OpenType Test", out _, fontPath!);
        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);

        var (_, fontDict) = page.GetFonts().First();
        var descendants = doc.Resolve(fontDict.GetOptional("DescendantFonts")!) as PdfArray;
        var cid = doc.Resolve(descendants![0]) as PdfDictionary;
        var fd = doc.Resolve(cid.GetOptional("FontDescriptor")!) as PdfDictionary;
        var ff3Stream = doc.Resolve(fd!.GetOptional("FontFile3")!) as PdfStream;

        // PdfStream is a PdfDictionary, so we can call GetNameOrNull directly on it.
        ff3Stream!.GetNameOrNull("Subtype").Should().Be("OpenType",
            "FontFile3 with /Subtype /OpenType signals CFF/OpenType embedding");
    }

    [Fact]
    public void CffFont_UnicodeText_RoundTripsThroughExtraction()
    {
        var fontPath = FindCffFont();
        Assert.SkipUnless(!string.IsNullOrEmpty(fontPath), "No CFF OpenType font found on system");

        const string text = "Café résumé — Ø € ½";
        var pdf = AuthorWith(text, out _, fontPath!);

        var extracted = ExtractAll(pdf);
        extracted.Should().Contain("Café résumé");
        extracted.Should().Contain("Ø");
        extracted.Should().Contain("€");
    }

    [Fact]
    public void CffFont_RoundTripsThroughOpen_NoErrors()
    {
        var fontPath = FindCffFont();
        Assert.SkipUnless(!string.IsNullOrEmpty(fontPath), "No CFF OpenType font found on system");

        var pdf = AuthorWith("CFF round trip Ω", out _, fontPath!);
        var act = () => PdfDocument.Open(pdf).Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void CffFont_MeasureWidth_IsPositiveAndScalesWithText()
    {
        var fontPath = FindCffFont();
        Assert.SkipUnless(!string.IsNullOrEmpty(fontPath), "No CFF OpenType font found on system");

        var font = PdfFont.FromFile(fontPath!, 20);
        double w1 = font.MeasureWidth("i");
        double wLong = font.MeasureWidth("WWWWW");
        w1.Should().BeGreaterThan(0);
        wLong.Should().BeGreaterThan(w1);
    }

    [Fact]
    public void CffFont_MultiplePages_AllEmbedWithCIDFontType0()
    {
        var fontPath = FindCffFont();
        Assert.SkipUnless(!string.IsNullOrEmpty(fontPath), "No CFF OpenType font found on system");

        var font = PdfFont.FromFile(fontPath, 14);
        var doc = PdfDocument.CreateNew();

        for (int i = 0; i < 3; i++)
        {
            var page = doc.Pages.AddBlank(300, 200);
            using (var g = page.GetGraphics())
                g.DrawString($"Page {i + 1} with CFF font", font, PdfBrush.Black, 20, 100);
        }

        var pdf = doc.SaveToBytes();

        using var reopened = PdfDocument.Open(pdf);
        foreach (var page in reopened.GetPages())
        {
            var (_, fontDict) = page.GetFonts().First();
            var descendants = reopened.Resolve(fontDict.GetOptional("DescendantFonts")!) as PdfArray;
            var cid = reopened.Resolve(descendants![0]) as PdfDictionary;
            cid!.GetNameOrNull("Subtype").Should().Be("CIDFontType0");
        }
    }
}
