using AwesomeAssertions;
using Pdfe.Core.Graphics;
using Xunit;

namespace Pdfe.Core.Tests.Graphics;

/// <summary>
/// Regression tests for #426: the base-14 (WinAnsi) text encoder used to format
/// the Unicode code point in decimal as a "\ddd" escape — which PDF reads as
/// octal — so accented / punctuation characters came out as mojibake, and code
/// points above 255 (em dash etc.) were never mapped to their WinAnsi byte. The
/// encoder now maps Unicode → WinAnsi and emits correct octal, falling back to
/// '?' for anything genuinely unrepresentable in base-14.
/// </summary>
public class WinAnsiEncodingTests
{
    private static readonly PdfFont Helv = PdfFont.Helvetica(12);

    [Theory]
    [InlineData("plain", "(plain)")]
    [InlineData("café", "(caf\\351)")]    // é U+00E9 -> WinAnsi 0xE9 -> octal 351
    [InlineData("a·b", "(a\\267b)")]      // middot U+00B7 -> 0xB7 -> octal 267
    [InlineData("a–b", "(a\\226b)")]      // en dash U+2013 -> 0x96 -> octal 226
    [InlineData("a—b", "(a\\227b)")]      // em dash U+2014 -> 0x97 -> octal 227
    [InlineData("“q”", "(\\223q\\224)")]  // curly quotes U+201C/U+201D -> 0x93/0x94
    public void EncodeString_WinAnsiChars_UseCorrectOctal(string input, string expected)
        => Helv.EncodeString(input).Should().Be(expected);

    [Fact]
    public void EncodeString_UnmappableChar_FallsBackToQuestionMark()
    {
        // CJK is not representable in base-14 WinAnsi -> '?', never garbage.
        Helv.EncodeString("你好").Should().Be("(??)");
    }

    [Fact]
    public void EncodeString_DoesNotEmitDecimalCodePoint()
    {
        // The exact #426 symptom: "—" must not serialize as its decimal code point.
        Helv.EncodeString("—").Should().NotContain("8212");
    }
}
