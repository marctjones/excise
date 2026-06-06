using System.IO;
using AwesomeAssertions;
using Pdfe.Core.Fonts;
using Xunit;

namespace Pdfe.Core.Tests.Fonts;

/// <summary>
/// Direct tests for the sfnt reader behind font embedding (#378), driving its
/// coverage (#351). Font-dependent cases skip when the system font is absent;
/// CI installs fonts-dejavu-core so they run there too.
/// </summary>
public class TrueTypeFontFileTests
{
    private const string DejaVu = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";

    [Fact]
    public void Parse_DejaVu_ExposesMetricsAndCmap()
    {
        Assert.SkipUnless(File.Exists(DejaVu), "DejaVuSans not installed");
        var ttf = TrueTypeFontFile.Parse(File.ReadAllBytes(DejaVu));

        ttf.UnitsPerEm.Should().BeGreaterThan(0);
        ttf.GlyphCount.Should().BeGreaterThan(100);
        ttf.PostScriptName.Should().Contain("DejaVu");
        ttf.Ascent.Should().BeGreaterThan(0);
        ttf.Descent.Should().BeLessThan(0);
        ttf.XMax.Should().BeGreaterThan(ttf.XMin);
        ttf.Cmap.Count.Should().BeGreaterThan(100);
        ttf.IsCff.Should().Be(false, "DejaVuSans is a TrueType (glyf) font");

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
        const string linLibertineOtf = "/usr/share/fonts/opentype/linux-libertine/LinLibertine_RB.otf";
        const string cantarellOtf = "/usr/share/fonts/opentype/cantarell/Cantarell-VF.otf";

        string? fontPath = File.Exists(linLibertineOtf) ? linLibertineOtf
                         : File.Exists(cantarellOtf) ? cantarellOtf
                         : null;

        Assert.SkipUnless(fontPath != null, "No CFF OpenType font found on system");

        var cff = TrueTypeFontFile.Parse(File.ReadAllBytes(fontPath));
        cff.IsCff.Should().Be(true, "LinLibertine/Cantarell are CFF-based OpenType");
        cff.UnitsPerEm.Should().BeGreaterThan(0);
        cff.GlyphCount.Should().BeGreaterThan(0);
        cff.Cmap.Count.Should().BeGreaterThan(0);
    }

    private static void FluentActAssert(System.Action act, string? reason = null) =>
        act.Should().Throw<System.Exception>(reason);

}
