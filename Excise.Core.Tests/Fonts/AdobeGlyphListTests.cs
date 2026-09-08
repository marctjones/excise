using AwesomeAssertions;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Fonts;

/// <summary>
/// Tests for the AGL "component-joined" underscore convention added for
/// #1423 — a glyph name like "f_i" (a ligature encoded as underscore-joined
/// component names, found in real Type1C subsets such as
/// UZLCYJ+TimesLTStd-Roman) was falling through to a raw byte echo and
/// producing a control character instead of "fi", which both under-covered
/// text extraction AND left real, mutool-visible residue after redaction.
/// </summary>
public class AdobeGlyphListTests
{
    [Fact]
    public void ToUnicode_UnderscoreJoinedLigatureName_ReturnsDecomposedConcatenation()
    {
        // The real-world case: "f_i" must decode to the two ASCII characters
        // "f" + "i" = "fi" (decomposed), matching how mutool reads the same
        // glyph — NOT the precomposed U+FB01 ligature codepoint, which would
        // fix redaction matching but leave the extraction-parity floor short
        // since mutool's own count would still differ.
        AdobeGlyphList.ToUnicode("f_i").Should().Be("fi");
    }

    [Fact]
    public void ToUnicode_ThreeComponentUnderscoreName_ConcatenatesAll()
    {
        AdobeGlyphList.ToUnicode("s_s_t").Should().Be("sst");
    }

    [Fact]
    public void ToUnicode_UnderscoreNameWithUnknownComponent_ReturnsNull()
    {
        // A partial match (silently dropping the unknown component) would be
        // worse than no match at all — this must fail closed.
        AdobeGlyphList.ToUnicode("f_zzznotaglyphname").Should().BeNull();
    }

    [Fact]
    public void ToUnicode_UnderscoreNameWithDotSuffix_StripsSuffixFirstThenSplits()
    {
        // AGL order of operations: strip ".suffix", THEN split on '_'.
        AdobeGlyphList.ToUnicode("f_i.alt").Should().Be("fi");
    }

    [Fact]
    public void ToUnicode_GlyphNameLiterallyCalledUnderscore_StillDirectMapsToUnderscoreChar()
    {
        // "underscore" (the WinAnsi glyph name for '_') must hit the direct
        // table lookup, not be misread as an underscore-joined compound of
        // zero-length parts.
        AdobeGlyphList.ToUnicode("underscore").Should().Be("_");
    }

    [Fact]
    public void ToUnicode_SingleUnderscoreWithNoComponents_ReturnsNull()
    {
        AdobeGlyphList.ToUnicode("_").Should().BeNull();
    }

    [Theory]
    [InlineData("ff", 'ﬀ')]
    [InlineData("fi", 'ﬁ')]
    [InlineData("fl", 'ﬂ')]
    [InlineData("ffi", 'ﬃ')]
    [InlineData("ffl", 'ﬄ')]
    public void ToUnicode_DirectLigatureNames_StillMapToPrecomposedCodepoints(string glyphName, char expected)
    {
        // The direct single-name ligature entries are untouched by #1423 —
        // only the underscore-JOINED convention changed.
        AdobeGlyphList.ToUnicode(glyphName).Should().Be(expected.ToString());
    }
}
