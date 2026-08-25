using AwesomeAssertions;
using Excise.Core.Fonts;
using Xunit;

namespace Excise.Core.Tests.Fonts;

/// <summary>
/// #1106 — <see cref="StandardFontMetrics"/> now resolves advance widths for
/// character codes ABOVE 126 in a non-embedded standard-14 font, where the three
/// base encodings diverge and a code alone is ambiguous. It maps the code through
/// WinAnsiEncoding to a glyph name, then the name to a width from the Adobe AFM
/// metrics — killing the last silently-guessed range (previously a flat 600 / 556)
/// in the standard-14 path.
///
/// <para><b>Oracle.</b> Every expected value is the glyph's <c>WX</c> in the Adobe
/// Core-14 AFM (StartFontMetrics 4.1), the same provenance as the 32-126 tables.
/// The samples named in issue #1106 (bullet 350, eacute 556, quoteright 222,
/// en/em dash) are pinned directly.</para>
/// </summary>
public class StandardFontUpperWidthTests
{
    private static double Width(string baseFont, int code)
    {
        StandardFontMetrics.TryGetWidth(baseFont, code, out var w).Should().BeTrue(
            $"'{baseFont}' code {code} should resolve to a standard-14 width");
        return w;
    }

    // --- The wired code>126 path (this flows through TextExtractor.GetStandardFontWidth) ---

    [Theory]
    // Helvetica (WinAnsi code -> glyph -> Adobe AFM WX). 0x92 quoteright=222,
    // 0x95 bullet=350, 0x96 endash=556, 0x97 emdash=1000, 0xE9 eacute=556 — the
    // exact glyphs #1106 calls out, plus the Windows 128-159 and Latin-1 ranges.
    [InlineData(0x92, 222)]   // quoteright
    [InlineData(0x95, 350)]   // bullet
    [InlineData(0x96, 556)]   // endash
    [InlineData(0x97, 1000)]  // emdash
    [InlineData(0x80, 556)]   // Euro
    [InlineData(0x99, 1000)]  // trademark
    [InlineData(0xA0, 278)]   // WinAnsi 0xA0 -> space
    [InlineData(0xAD, 333)]   // WinAnsi 0xAD -> hyphen
    [InlineData(0xA9, 737)]   // copyright
    [InlineData(0xE9, 556)]   // eacute
    [InlineData(0xE0, 556)]   // agrave
    [InlineData(0xFF, 500)]   // ydieresis
    public void Helvetica_AboveCode126_ResolvesToAfmWidth(int code, int expected)
    {
        Width("Helvetica", code).Should().Be(expected);
    }

    [Theory]
    [InlineData(0x92, 333)]   // quoteright (Times differs from Helvetica's 222)
    [InlineData(0x95, 350)]   // bullet
    [InlineData(0x96, 500)]   // endash (Times 500 vs Helvetica 556)
    [InlineData(0x97, 1000)]  // emdash
    [InlineData(0xE9, 444)]   // eacute (Times 444 vs Helvetica 556)
    public void TimesRoman_AboveCode126_ResolvesToAfmWidth(int code, int expected)
    {
        Width("Times-Roman", code).Should().Be(expected);
    }

    [Fact]
    public void BoldAndItalicFaces_ResolveToTheirOwnMetrics()
    {
        // eacute is 444 across all four Times faces, but quoteright separates
        // the roman/bold-vs-italic columns, so it proves face resolution.
        Width("Times-Bold", 0xE9).Should().Be(444);
        Width("Helvetica-Bold", 0x92).Should().Be(278);       // vs Helvetica 222
        Width("Helvetica-BoldOblique", 0x95).Should().Be(350);
    }

    [Fact]
    public void Courier_AboveCode126_IsMonospaced600()
    {
        Width("Courier", 0xE9).Should().Be(600);      // eacute
        Width("Courier-Bold", 0x95).Should().Be(600); // bullet
    }

    [Fact]
    public void ArialAndTimesNewRoman_AliasesResolve()
    {
        Width("Arial", 0xE9).Should().Be(556);          // -> Helvetica eacute
        Width("Arial-BoldMT", 0x92).Should().Be(278);   // -> Helvetica-Bold quoteright
    }

    [Fact]
    public void SubsetPrefix_IsStripped()
    {
        Width("ABCDEF+Helvetica", 0xE9).Should().Be(556);
        Width("WXYZAB+Times-Bold", 0xE9).Should().Be(444);
    }

    // --- Fail-closed cases: no guess where the encoding/metrics don't apply ---

    [Theory]
    [InlineData(127)]  // DEL — unassigned in WinAnsi
    [InlineData(129)]
    [InlineData(141)]
    [InlineData(143)]
    [InlineData(144)]
    [InlineData(157)]
    public void UnassignedWinAnsiCodes_FailClosed(int code)
    {
        StandardFontMetrics.TryGetWidth("Helvetica", code, out _).Should().BeFalse(
            "an unassigned WinAnsi code has no glyph name to price");
    }

    [Fact]
    public void SymbolAndZapfDingbats_AboveCode126_FailClosed()
    {
        // Their glyphs are not WinAnsi-named; reading eacute's slot through
        // WinAnsi would be a confident lie, so above 126 they decline.
        StandardFontMetrics.TryGetWidth("Symbol", 0xE9, out _).Should().BeFalse();
        StandardFontMetrics.TryGetWidth("ZapfDingbats", 0xE9, out _).Should().BeFalse();
    }

    [Fact]
    public void UnknownFamily_AboveCode126_FailsClosed()
    {
        StandardFontMetrics.TryGetWidth("Wingdings", 0xE9, out _).Should().BeFalse();
    }

    // --- The 32-126 fast path is preserved byte-for-byte (NOT re-routed through WinAnsi) ---

    [Theory]
    [InlineData("Helvetica", 32, 278)]   // space
    [InlineData("Helvetica", 65, 667)]   // A
    [InlineData("Helvetica", 39, 221)]   // code 39: the checked-in Standard value,
                                         // NOT the AFM quoteright 222 (see below)
    [InlineData("Times-Roman", 87, 944)] // W
    [InlineData("Courier", 100, 600)]    // d
    public void Codes32To126_UnchangedFromExistingTables(string baseFont, int code, int expected)
    {
        Width(baseFont, code).Should().Be(expected);
    }

    [Fact]
    public void Code39_KeepsOldValue_WhileByNamePathReturnsAfmValue()
    {
        // The load-bearing "don't re-route ≤126" assertion: WinAnsi code 39 is
        // quotesingle (width 222 in Helvetica), but the 32-126 table encodes the
        // Standard quoteright at code 39 (221). The fast path must keep 221 even
        // though the AFM quoteright is 222 — a glyph-identity choice, not a bug.
        Width("Helvetica", 39).Should().Be(221);
        StandardFontMetrics.TryGetWidthByGlyphName("Helvetica", "quoteright", out var byName)
            .Should().BeTrue();
        byName.Should().Be(222);
    }

    // --- TryGetWidthByGlyphName: the /Differences seam ---

    [Theory]
    [InlineData("Helvetica", "bullet", 350)]
    [InlineData("Helvetica", "eacute", 556)]
    [InlineData("Helvetica", "Euro", 556)]
    [InlineData("Helvetica-Bold", "quoteright", 278)]   // face-sensitive
    [InlineData("Times-Roman", "eacute", 444)]
    [InlineData("Times-Italic", "quotedblleft", 556)]   // vs Times-Roman 444
    [InlineData("Times-Roman", "quotedblleft", 444)]
    [InlineData("Courier", "eacute", 600)]              // monospaced
    [InlineData("Courier", "bullet", 600)]
    public void TryGetWidthByGlyphName_ResolvesLatinAndCourier(string baseFont, string glyph, int expected)
    {
        StandardFontMetrics.TryGetWidthByGlyphName(baseFont, glyph, out var w).Should().BeTrue();
        w.Should().Be(expected);
    }

    [Fact]
    public void TryGetWidthByGlyphName_FailsClosedOffTheLatinFaces()
    {
        StandardFontMetrics.TryGetWidthByGlyphName("Symbol", "bullet", out _).Should().BeFalse();
        StandardFontMetrics.TryGetWidthByGlyphName("ZapfDingbats", "eacute", out _).Should().BeFalse();
        StandardFontMetrics.TryGetWidthByGlyphName("Courier", "notaglyphname", out _).Should().BeFalse();
        StandardFontMetrics.TryGetWidthByGlyphName("Helvetica", "notaglyphname", out _).Should().BeFalse();
        StandardFontMetrics.TryGetWidthByGlyphName(null, "bullet", out _).Should().BeFalse();
        StandardFontMetrics.TryGetWidthByGlyphName("Helvetica", null, out _).Should().BeFalse();
    }
}
