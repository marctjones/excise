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

        int gidA = ttf.GidForCodepoint('A');
        gidA.Should().BeGreaterThan(0);
        ttf.GidForCodepoint('é').Should().BeGreaterThan(0, "accented Latin should be mapped");
        ttf.GidForCodepoint(0x1FFFFF).Should().Be(0, "an unmapped codepoint returns .notdef");

        ttf.AdvanceWidth(gidA).Should().BeGreaterThan(0);
        ttf.AdvanceWidth(int.MaxValue).Should().BeGreaterThanOrEqualTo(0, "out-of-range gid clamps");
    }

    [Fact]
    public void Parse_RejectsCffOpenType_AndGarbage()
    {
        // 'OTTO' sfnt tag → CFF OpenType, unsupported here.
        var otto = new byte[] { (byte)'O', (byte)'T', (byte)'T', (byte)'O', 0, 0, 0, 0, 0, 0, 0, 0 };
        FluentActAssert(() => TrueTypeFontFile.Parse(otto));

        // Random bytes → not a font.
        FluentActAssert(() => TrueTypeFontFile.Parse(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }));
    }

    private static void FluentActAssert(System.Action act) =>
        act.Should().Throw<System.Exception>();
}
