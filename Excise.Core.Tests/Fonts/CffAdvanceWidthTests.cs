using AwesomeAssertions;
using Excise.Core.Fonts;
using Excise.Core.Tests.Fixtures;
using Xunit;

namespace Excise.Core.Tests.Fonts;

/// <summary>
/// #1148 — <see cref="CffParser"/> now reads per-glyph advance widths from the
/// Type2 charstrings of an embedded CFF (/FontFile3). A CFF carries no hmtx, so
/// the charstring is the ONLY advance source for a /FontFile3 program with no
/// PDF /Widths array; before this the width cascade fell to the flat-600 guess
/// there (the other half of #1102, whose TrueType /FontFile2 rung already exists).
///
/// <para><b>Oracle.</b> Every expected width here is the same glyph's advance as
/// reported by fontTools' Type2 width extractor
/// (<c>psCharStrings.T2WidthExtractor</c>) — a tool that is not excise, per the
/// no-self-oracle rule. Two checked-in fixtures exercise both encodings of the
/// width:</para>
/// <list type="bullet">
///   <item><b>Inconsolata</b> (raw name-keyed CFF): monospaced, defaultWidthX=500,
///     nominalWidthX=100 — every glyph takes the DEFAULT width, so a
///     default/nominal confusion would show as 100, not 500.</item>
///   <item><b>Libertinus Serif</b> (CFF table lifted from the OTF): proportional,
///     defaultWidthX=0, nominalWidthX=554 — every real width is nominalWidthX plus
///     a signed charstring delta, so a lazy default would read 0 and a sign error
///     would corrupt narrow glyphs like <c>space</c> (250 = 554 − 304).</item>
/// </list>
/// </summary>
public class CffAdvanceWidthTests
{
    private static CffParser.CffFontInfo ParseInconsolata()
    {
        var info = CffParser.Parse(TestFontFixtures.LoadInconsolataCffBytes());
        info.Should().NotBeNull("Inconsolata.cff is a valid raw CFF");
        return info!;
    }

    private static CffParser.CffFontInfo ParseLibertinus()
    {
        var otf = TestFontFixtures.LoadLibertinusSerifCffBytes();
        var cff = SfntTableReader.ExtractTable(otf, "CFF ");
        cff.Should().NotBeNull("the OTF fixture carries a 'CFF ' table");
        var info = CffParser.Parse(cff!);
        info.Should().NotBeNull("the extracted CFF table parses");
        return info!;
    }

    private static int WidthOf(CffParser.CffFontInfo info, string glyphName)
    {
        info.GlyphNameToIndex.TryGetValue(glyphName, out var gid)
            .Should().BeTrue($"the font should contain glyph '{glyphName}'");
        return info.AdvanceWidth(gid);
    }

    [Fact]
    public void Inconsolata_ReportsUnitsPerEm_1000()
    {
        ParseInconsolata().UnitsPerEm.Should().Be(1000);
    }

    [Fact]
    public void Inconsolata_VisibleGlyphs_TakeTheDefaultWidth_500_NotNominal_100()
    {
        var info = ParseInconsolata();
        info.AdvanceWidths.Should().HaveCount(info.NumGlyphs);

        // The visible monospaced glyphs advance by defaultWidthX = 500 (no width
        // operand in the charstring). If the interpreter confused defaultWidthX
        // with nominalWidthX these would read 100; if it never read the Private
        // DICT at all they would read 0.
        foreach (var name in new[] { "space", "A", "a", "period", "zero", "i", "m", "W", ".notdef", "quoteright", "bullet" })
            WidthOf(info, name).Should().Be(500, $"Inconsolata glyph '{name}' is monospaced at 500");
    }

    [Theory]
    // Inconsolata carries 65 construction "NameMe.N" glyphs whose charstrings DO
    // encode an explicit width = nominalWidthX(100) + delta. fontTools reports
    // exactly these; matching them proves the nominal+delta path fires on this
    // fixture too, not only the default path above. (gid 1 => 100 + 391 = 491.)
    [InlineData(1, 491)]
    [InlineData(5, 457)]
    [InlineData(29, 250)]
    [InlineData(31, 233)]
    public void Inconsolata_ConstructionGlyphs_UseNominalPlusDelta(int glyphIndex, int expected)
    {
        ParseInconsolata().AdvanceWidth(glyphIndex).Should().Be(expected);
    }

    [Fact]
    public void Libertinus_ReportsUnitsPerEm_1000()
    {
        ParseLibertinus().UnitsPerEm.Should().Be(1000);
    }

    [Theory]
    // fontTools T2WidthExtractor over LibertinusSerif-Regular's 'CFF ' table.
    [InlineData("space", 250)]      // 554 - 304 : signed delta below nominal
    [InlineData("A", 695)]
    [InlineData("a", 457)]
    [InlineData("period", 220)]
    [InlineData("i", 271)]
    [InlineData("l", 264)]
    [InlineData("m", 790)]
    [InlineData("M", 839)]
    [InlineData("W", 951)]
    [InlineData("quoteright", 268)]
    [InlineData("bullet", 351)]
    [InlineData(".notdef", 500)]
    public void Libertinus_ProportionalAdvances_MatchFontToolsOracle(string glyphName, int expected)
    {
        WidthOf(ParseLibertinus(), glyphName).Should().Be(expected);
    }

    [Fact]
    public void Libertinus_AdvancesAreProportional_NotAllEqual()
    {
        // Guards against a "return defaultWidthX for everything" regression: a
        // proportional face must produce a spread of widths.
        var info = ParseLibertinus();
        var distinct = new System.Collections.Generic.HashSet<int>();
        foreach (var name in new[] { "space", "A", "a", "m", "W", "period", "i" })
            distinct.Add(WidthOf(info, name));
        distinct.Count.Should().BeGreaterThan(3, "proportional widths must differ per glyph");
    }

    [Fact]
    public void AdvanceWidth_OutOfRangeIndex_ReturnsZero()
    {
        var info = ParseInconsolata();
        info.AdvanceWidth(-1).Should().Be(0);
        info.AdvanceWidth(info.NumGlyphs + 10_000).Should().Be(0);
    }
}
