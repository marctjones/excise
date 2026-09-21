using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Primitives;
using Excise.Core.Tests.Fixtures;
using Xunit;

namespace Excise.Core.Tests.Graphics;

/// <summary>
/// #1671: <see cref="PdfFont.EncodeString"/> writes '?' for any character a
/// WinAnsi font cannot represent, and its authoring callers never said so — a
/// form field or a drawn string holding Polish, Czech, Cyrillic or CJK text
/// saved as <c>?</c> with no warning. The authoring paths now refuse instead,
/// before they change anything.
///
/// <para>WinAnsi covers Latin-1 0xA0-0xFF, so <c>José</c> and <c>Müller</c> are
/// NOT affected and must keep working — the issue's own examples were wrong on
/// that point, and the last tests pin it. EncodeString itself keeps its '?'
/// (<c>WinAnsiEncodingTests</c>): it is the right output for a WinAnsi font;
/// what was wrong was that nobody was told.</para>
/// </summary>
public class UnencodableTextRefusalTests
{
    private const string Polish = "Łukasz";               // Ł U+0141 is outside WinAnsi
    private const string Cyrillic = "Дмитрий";
    private static readonly PdfRectangle Box = new(72, 700, 300, 720);

    private static PdfFont Embedded() => PdfFont.FromTrueType(TestFontFixtures.LoadDejaVuSansBytes(), 11);

    private static string AppearanceContent(PdfDocument doc, PdfField field)
    {
        var ap = (PdfDictionary)doc.Resolve(field.RawDictionary.GetOptional("AP")!);
        var stream = (PdfStream)doc.Resolve(ap.GetOptional("N")!);
        return System.Text.Encoding.Latin1.GetString(stream.DecodedData);
    }

    // ---- form fields ----------------------------------------------------

    [Theory]
    [InlineData(Polish, "Ł", "U+0141")]
    [InlineData(Cyrillic, "Д", "U+0414")]
    public void AddTextField_DefaultValueOutsideWinAnsi_IsRefused_AndAddsNothing(string value, string character, string codePoint)
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank(612, 792);

        var act = () => doc.AddTextField(1, Box, "name", defaultValue: value);

        var thrown = act.Should().Throw<ArgumentException>().Which;
        thrown.Message.Should().Contain(character).And.Contain(codePoint).And.Contain("Helvetica")
            .And.Contain("'?'", "the message says what would have been written instead");
        (doc.GetAcroForm()?.Fields.Count ?? 0).Should().Be(0, "a refused field must not be half-attached");
    }

    [Fact]
    public void SetValue_OutsideWinAnsi_OnAnAuthoredField_IsRefused_AndLeavesTheFieldAsItWas()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank(612, 792);
        doc.AddTextField(1, Box, "name", defaultValue: "Ada Lovelace");
        var field = doc.GetAcroForm()!.FindField("name")!;

        var act = () => field.SetValue(Polish);

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("U+0141").And.Contain("'name'");
        field.Value.Should().Be("Ada Lovelace", "/V must not change when the appearance cannot be drawn");
        var content = AppearanceContent(doc, field);
        content.Should().Contain("Ada Lovelace").And.NotContain("?", "no '?' may reach the appearance stream");
    }

    [Fact]
    public void SetValue_Latin1AccentedText_StillWorks_AsRealWinAnsiBytes()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank(612, 792);
        doc.AddTextField(1, Box, "name");
        var field = doc.GetAcroForm()!.FindField("name")!;

        field.SetValue("José Müller");

        field.Value.Should().Be("José Müller");
        var content = AppearanceContent(doc, field);
        content.Should().Contain("(Jos\\351 M\\374ller)", "é and ü are WinAnsi bytes 0xE9 and 0xFC, written as octal escapes");
        content.Should().NotContain("?");
    }

    [Fact]
    public void AnEmbeddedAppearanceFont_DrawsCharactersWinAnsiCannot()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank(612, 792);
        doc.AddTextField(1, Box, "name", defaultValue: Polish, appearanceFont: Embedded());
        var field = doc.GetAcroForm()!.FindField("name")!;

        var act = () => field.SetValue(Cyrillic);

        act.Should().NotThrow("a Type0 font with a cmap for Д encodes it as a glyph id");
        AppearanceContent(doc, field).Should().NotContain("?");
    }

    // ---- drawn text / typewriter ---------------------------------------

    [Theory]
    [InlineData(Polish, "U+0141")]
    [InlineData(Cyrillic, "U+0414")]
    [InlineData("你好", "U+4F60")]
    public void DrawString_OutsideWinAnsi_IsRefused_AndEmitsNothing(string text, string codePoint)
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(400, 200);
        using var g = page.GetGraphics();

        var act = () => g.DrawString(text, PdfFont.Helvetica(12), PdfBrush.Black, 40, 120);

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain(codePoint);
        g.GetOperators().Should().BeEmpty();
    }

    [Fact]
    public void DrawText_RefusesTheWholeText_NotJustTheLineWithTheBadCharacter()
    {
        // Dispose flushes whatever was emitted, so a per-line refusal would have
        // written the first line into the page and then thrown.
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(400, 200);
        using (var g = page.GetGraphics())
        {
            var act = () => g.DrawText(
                "First line is fine\n" + Polish, PdfFont.Helvetica(12), PdfBrush.Black,
                new PdfRectangle(20, 20, 380, 180));

            act.Should().Throw<ArgumentException>();
            g.GetOperators().Should().BeEmpty("nothing of the text may be written when part of it cannot be");
        }

        System.Text.Encoding.Latin1.GetString(page.GetContentStreamBytes()).Should().NotContain("First line");
    }

    [Fact]
    public void DrawString_Latin1AccentedText_StillWorks()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(400, 200);
        using var g = page.GetGraphics();

        g.DrawString("José Müller", PdfFont.Helvetica(12), PdfBrush.Black, 40, 120);

        g.GetOperators().Should().Contain("(Jos\\351 M\\374ller) Tj").And.NotContain("?");
    }

    [Fact]
    public void DrawString_WithAnEmbeddedFont_DrawsPolishAndCyrillic()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(400, 200);
        using var g = page.GetGraphics();
        var font = Embedded();

        var act = () => g.DrawString(Polish + " " + Cyrillic, font, PdfBrush.Black, 40, 120);

        act.Should().NotThrow();
        font.CanEncodeFully(Polish + " " + Cyrillic).Should().BeTrue(
            "CanEncodeFully asks the font program, not WinAnsi — it used to answer false for every embedded font");
    }

    [Fact]
    public void DrawString_WithAnEmbeddedFont_StillRefusesACharacterTheFontHasNoGlyphFor()
    {
        // .notdef is the embedded font's '?': the same silent substitution.
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(400, 200);
        using var g = page.GetGraphics();
        var font = Embedded();
        font.CanEncodeFully("你").Should().BeFalse("DejaVu Sans carries no CJK ideographs");

        var act = () => g.DrawString("你", font, PdfBrush.Black, 40, 120);

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("U+4F60");
        g.GetOperators().Should().BeEmpty();
    }

    [Fact]
    public void EncodeString_KeepsItsQuestionMark_TheRefusalLivesAtTheAuthoringCallers()
    {
        PdfFont.Helvetica(12).EncodeString(Polish).Should().Be("(?ukasz)");
    }
}
