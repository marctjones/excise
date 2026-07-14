using AwesomeAssertions;
using Pdfe.Core.Fonts;
using Pdfe.Core.Tests.Fixtures;
using Xunit;

namespace Pdfe.Core.Tests.Fonts;

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

    private static void FluentActAssert(System.Action act, string? reason = null) =>
        act.Should().Throw<System.Exception>(reason);

}
