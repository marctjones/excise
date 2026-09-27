using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Fonts;
using Xunit;

namespace Excise.Core.Tests.Content;

/// <summary>
/// #1847 — the READ path (redaction/extraction geometry) must resolve codes
/// 39 and 96 by the font's actual <c>/Encoding</c>, not a single fixed table
/// value. No <c>/Encoding</c> at all means the font's built-in encoding —
/// StandardEncoding for a non-symbolic standard-14 face (ISO 32000-2 rule 7)
/// — giving quoteright/quoteleft; an explicit WinAnsiEncoding (bare name or
/// an encoding dictionary's <c>/BaseEncoding</c>) names the other glyph,
/// quotesingle/grave. <see cref="StandardFontUpperWidthTests"/> pins the
/// underlying AFM numbers against <see cref="StandardFontMetrics"/> directly;
/// this pins the same distinction through <see cref="ContentStreamWalker"/>'s
/// GetCharWidth (via TextExtractor, one of the #992 sinks) on a real content
/// stream, which is what redaction geometry and extraction actually consume.
/// </summary>
public class StandardFontEncodingWidthTests
{
    private const string NoEncodingFont =
        "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n";
    private const string WinAnsiNameFont =
        "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n";
    private const string WinAnsiBaseEncodingDictFont =
        "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica "
      + "/Encoding << /BaseEncoding /WinAnsiEncoding >> >>\nendobj\n";

    private static double QuoteWidth(string fontObject, char c)
    {
        var content = $"BT /F1 1000 Tf 1 0 0 1 72 700 Tm ({c}) Tj ET";
        var page = PdfDocument.Open(ContentStreamFixture.Build(content, fontObject: fontObject)).GetPage(1);
        var letters = ContentStreamFixture.ExtractLetters(page);
        letters.Should().HaveCount(1);
        return letters[0].Width;
    }

    [Fact]
    public void NoEncoding_ResolvesToStandardQuoteright()
    {
        StandardFontMetrics.TryGetWidthByGlyphName("Helvetica", "quoteright", out var expected).Should().BeTrue();
        QuoteWidth(NoEncodingFont, '\'').Should().Be(expected);
    }

    [Fact]
    public void NoEncoding_ResolvesToStandardQuoteleft()
    {
        StandardFontMetrics.TryGetWidthByGlyphName("Helvetica", "quoteleft", out var expected).Should().BeTrue();
        QuoteWidth(NoEncodingFont, '`').Should().Be(expected);
    }

    [Fact]
    public void WinAnsiEncoding_AsBareName_ResolvesToQuotesingle()
    {
        StandardFontMetrics.TryGetWidthByGlyphName("Helvetica", "quotesingle", out var expected).Should().BeTrue();
        QuoteWidth(WinAnsiNameFont, '\'').Should().Be(expected);
    }

    [Fact]
    public void WinAnsiEncoding_AsBareName_ResolvesToGrave()
    {
        StandardFontMetrics.TryGetWidthByGlyphName("Helvetica", "grave", out var expected).Should().BeTrue();
        QuoteWidth(WinAnsiNameFont, '`').Should().Be(expected);
    }

    [Fact]
    public void WinAnsiEncoding_ViaBaseEncodingDictionary_AlsoResolvesToQuotesingle()
    {
        StandardFontMetrics.TryGetWidthByGlyphName("Helvetica", "quotesingle", out var expected).Should().BeTrue();
        QuoteWidth(WinAnsiBaseEncodingDictFont, '\'').Should().Be(expected);
    }

    [Fact]
    public void QuotesingleAndQuoteright_AreActuallyDifferentWidths()
    {
        // Sensitivity check: if these matched, the tests above would prove
        // nothing about which glyph identity excise picked.
        QuoteWidth(NoEncodingFont, '\'').Should().NotBe(QuoteWidth(WinAnsiNameFont, '\''));
    }
}
