using AwesomeAssertions;
using Excise.Core.Fonts;
using Excise.Core.Tests.Fixtures;
using Xunit;

namespace Excise.Core.Tests.Fonts;

/// <summary>
/// Direct tests for the sfnt reader behind font embedding (#378), driving its
/// coverage (#351, #603). The DejaVu Sans fixture is embedded in this
/// assembly (Fixtures/Fonts/DejaVuSans.ttf, #603) rather than loaded from a
/// system font path: the previous version of this file hard-coded
/// <c>/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf</c>, a Linux-only
/// path, so these tests silently skipped on every macOS and Windows dev
/// machine — an invisible coverage loss of exactly the kind #619's
/// skip-budget philosophy exists to catch, just not one wired into that
/// gate. Bundling the font removes the environment dependency entirely.
/// </summary>
public class TrueTypeFontFileTests
{
    [Fact]
    public void Parse_DejaVu_ExposesMetricsAndCmap()
    {
        var ttf = TrueTypeFontFile.Parse(TestFontFixtures.LoadDejaVuSansBytes());

        ttf.UnitsPerEm.Should().BeGreaterThan(0);
        ttf.GlyphCount.Should().BeGreaterThan(100);
        ttf.PostScriptName.Should().Contain("DejaVu");
        ttf.Ascent.Should().BeGreaterThan(0);
        ttf.Descent.Should().BeLessThan(0);
        ttf.XMax.Should().BeGreaterThan(ttf.XMin);
        ttf.Cmap.Count.Should().BeGreaterThan(100);
        ttf.IsCff.Should().Be(false, "DejaVuSans is a TrueType (glyf) font");
        ttf.IsBold.Should().BeFalse("DejaVu Sans Regular is not bold");
        ttf.IsItalic.Should().BeFalse("DejaVu Sans Regular is not italic");
        ttf.Data.Should().NotBeEmpty();

        int gidA = ttf.GidForCodepoint('A');
        gidA.Should().BeGreaterThan(0);
        ttf.GidForCodepoint('é').Should().BeGreaterThan(0, "accented Latin should be mapped");
        ttf.GidForCodepoint(0x1FFFFF).Should().Be(0, "an unmapped codepoint returns .notdef");

        ttf.AdvanceWidth(gidA).Should().BeGreaterThan(0);
        ttf.AdvanceWidth(int.MaxValue).Should().BeGreaterThanOrEqualTo(0, "out-of-range gid clamps");
    }

    [Fact]
    public void Parse_AcceptsCffOpenType_ButRejectsBogusData()
    {
        // 'OTTO' sfnt tag is now accepted (CFF-based OpenType), but the minimal
        // OTTO header will fail because it lacks required tables.
        var otto = new byte[] { (byte)'O', (byte)'T', (byte)'T', (byte)'O', 0, 0, 0, 0, 0, 0, 0, 0 };
        FluentActAssert(() => TrueTypeFontFile.Parse(otto),
            "Minimal OTTO lacks required tables");

        // Random bytes → not a font.
        FluentActAssert(() => TrueTypeFontFile.Parse(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }),
            "Random bytes are not a valid sfnt");
    }

    [Fact]
    public void Parse_CffFont_ExposesCffFlag()
    {
        var cff = TrueTypeFontFile.Parse(TestFontFixtures.LoadLibertinusSerifCffBytes());
        cff.IsCff.Should().Be(true, "Libertinus Serif is a CFF-based OpenType ('OTTO') font");
        cff.UnitsPerEm.Should().BeGreaterThan(0);
        cff.GlyphCount.Should().BeGreaterThan(0);
        cff.Cmap.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SymbolAndGlyphNameAccessors_ResolveOnNonSymbolFont()
    {
        // DejaVu Sans is a Unicode (3,1) font with no Microsoft-Symbol (3,0)
        // subtable, exercising the non-symbol branches of the #791 symbolic
        // selection order: HasSymbolCmap is false and GidForSymbolByte falls
        // through the symbol subtable to the Macintosh/none path.
        var ttf = TrueTypeFontFile.Parse(TestFontFixtures.LoadDejaVuSansBytes());

        ttf.HasSymbolCmap.Should().BeFalse("DejaVu Sans carries no (3,0) symbol cmap");
        ttf.GidForSymbolByte('A').Should().BeGreaterThanOrEqualTo(0,
            "a symbol-byte lookup resolves via the Mac subtable or returns .notdef");
        ttf.GidForSymbolByte(0x41).Should().Be(ttf.GidForSymbolByte(0x141),
            "only the low byte is significant in symbolic addressing");

        // The 'post' table exposes PostScript glyph names for real gids; an
        // out-of-range gid returns null.
        int gidA = ttf.GidForCodepoint('A');
        ttf.GlyphName(gidA).Should().NotBeNullOrEmpty("DejaVu carries a version-2 'post' table");
        ttf.GlyphName(int.MaxValue).Should().BeNull("an unknown gid has no name");
    }

    [Fact]
    public void Parse_SymbolCmapFont_ExposesSymbolSubtableAndResolvesF000Codes()
    {
        // A DejaVu-derived font whose cmap is replaced with a single
        // Microsoft-Symbol (3,0) format-4 subtable mapping 0xF041→glyph('R'),
        // 0xF042→glyph('e'), 0xF043→glyph('d'). This drives the #791 symbolic
        // decode path: the (3,0) subtable selection, the raw-key subtable
        // parse (ParseSubtableInto), and the F000-offset lookup order in
        // GidForSymbolByte.
        var mapping = new[] { (0x41, 'R'), (0x42, 'e'), (0x43, 'd') };
        var bytes = SymbolCmapTtfBuilder.BuildSymbolCmapFont(
            TestFontFixtures.LoadDejaVuSansBytes(), mapping);

        var ttf = TrueTypeFontFile.Parse(bytes);

        ttf.HasSymbolCmap.Should().BeTrue("the font now carries a (3,0) symbol cmap");

        // The parsed cmap keys the symbol subtable's raw F000-offset codes.
        int gidR = ttf.GidForCodepoint(0xF041);
        gidR.Should().BeGreaterThan(0, "0xF041 is mapped to the 'R' glyph");
        ttf.GidForCodepoint('R').Should().Be(0,
            "a symbol-only font exposes no bare-Unicode entry for 'R'");

        // Content code 0x41 must resolve through the F000 Private-Use offset to
        // the same glyph 0xF041 maps to.
        ttf.GidForSymbolByte(0x41).Should().Be(gidR,
            "0xF041 addresses the 'R' glyph in the symbol subtable");
        ttf.GidForSymbolByte(0x42).Should().Be(ttf.GidForCodepoint(0xF042));
        ttf.GidForSymbolByte(0x99).Should().Be(0, "an unmapped symbol byte is .notdef");

        // The preserved post (v2) table still names the glyph, so extraction can
        // recover Unicode without a /ToUnicode (the recoverable-symbol premise
        // of #791).
        ttf.GlyphName(gidR).Should().NotBeNullOrEmpty();
    }

    private static void FluentActAssert(System.Action act, string? reason = null) =>
        act.Should().Throw<System.Exception>(reason);

}
