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
/// Tests for TrueType/OpenType font embedding + Unicode text (#378). They embed a
/// real system font, so each skips cleanly when that font isn't installed.
/// </summary>
public class EmbeddedFontTests
{
    private const string DejaVu = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";

    private static string ExtractAll(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Join("\n", doc.GetPages().Select(p => new TextExtractor(p).ExtractText()));
    }

    private static byte[] AuthorWith(string text, out PdfFont font)
    {
        font = PdfFont.FromFile(DejaVu, 18);
        var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(400, 200);
        using (var g = page.GetGraphics())
            g.DrawString(text, font, PdfBrush.Black, 40, 120);
        return doc.SaveToBytes();
    }

    [Fact]
    public void FromFile_ProducesType0EmbeddedFont()
    {
        Assert.SkipUnless(File.Exists(DejaVu), "DejaVuSans not installed");
        var font = PdfFont.FromFile(DejaVu, 12);
        font.Should().BeOfType<PdfTrueTypeFont>();
        // Identity-H encodes 2-byte glyph ids as a hex string.
        font.EncodeString("AB").Should().StartWith("<").And.EndWith(">");
        font.EncodeString("AB").Should().HaveLength(10); // <XXXXXXXX>
    }

    [Fact]
    public void EmbeddedFont_UnicodeText_RoundTripsThroughExtraction()
    {
        Assert.SkipUnless(File.Exists(DejaVu), "DejaVuSans not installed");
        const string text = "Café résumé naïve — Ø ½ € Ελληνικά Кириллица";
        var pdf = AuthorWith(text, out _);

        var extracted = ExtractAll(pdf);
        extracted.Should().Contain("Café résumé naïve");
        extracted.Should().Contain("Ελληνικά");
        extracted.Should().Contain("Кириллица");
        extracted.Should().Contain("€");
    }

    [Fact]
    public void EmbeddedFont_DictionaryIsType0WithFontFile2AndToUnicode()
    {
        Assert.SkipUnless(File.Exists(DejaVu), "DejaVuSans not installed");
        var pdf = AuthorWith("Hello Café", out _);
        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);

        var (_, fontDict) = page.GetFonts().First();
        fontDict.GetNameOrNull("Subtype").Should().Be("Type0");
        fontDict.GetNameOrNull("Encoding").Should().Be("Identity-H");
        doc.Resolve(fontDict.GetOptional("ToUnicode")!).Should().BeOfType<PdfStream>();

        var descendants = doc.Resolve(fontDict.GetOptional("DescendantFonts")!) as PdfArray;
        var cid = doc.Resolve(descendants![0]) as PdfDictionary;
        cid!.GetNameOrNull("Subtype").Should().Be("CIDFontType2");
        var fd = doc.Resolve(cid.GetOptional("FontDescriptor")!) as PdfDictionary;
        doc.Resolve(fd!.GetOptional("FontFile2")!).Should().BeOfType<PdfStream>("the TTF must be embedded");
    }

    [Fact]
    public void EmbeddedFont_MeasureWidth_IsPositiveAndScalesWithText()
    {
        Assert.SkipUnless(File.Exists(DejaVu), "DejaVuSans not installed");
        var font = PdfFont.FromFile(DejaVu, 20);
        double w1 = font.MeasureWidth("i");
        double wLong = font.MeasureWidth("WWWWW");
        w1.Should().BeGreaterThan(0);
        wLong.Should().BeGreaterThan(w1);
    }

    [Fact]
    public void EmbeddedFont_RoundTripsThroughOpen_NoErrors()
    {
        Assert.SkipUnless(File.Exists(DejaVu), "DejaVuSans not installed");
        var pdf = AuthorWith("Round trip Ω", out _);
        var act = () => PdfDocument.Open(pdf).Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void FromTrueType_RejectsNonTrueTypeData()
    {
        var bogus = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E }; // "%PDF-1."
        var act = () => PdfFont.FromTrueType(bogus, 12);
        act.Should().Throw<System.Exception>();
    }
}
