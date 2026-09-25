using AwesomeAssertions;
using Excise.Core.Fonts;
using Excise.Core.Graphics;
using Xunit;

namespace Excise.Core.Tests.Graphics;

/// <summary>
/// <see cref="PdfFont.MeasureWidth"/> for a standard-14 face must be the face's
/// own Adobe AFM advance (#1835), not Helvetica's. Authoring wraps, centres and
/// ellipsises on this number, so a Times line measured with Helvetica widths
/// overflowed or under-filled its box. The literal expectations are published
/// Adobe AFM values, so this does not grade <see cref="StandardFontMetrics"/>
/// against itself; the mutool advance oracle is in Excise.Rendering.Tests.
/// </summary>
public class StandardFontWidthTests
{
    // At size 1000 MeasureWidth returns the advance in AFM units.
    [Theory]
    [InlineData("Times-Roman", "W", 944)]
    [InlineData("Times-Roman", "i", 278)]
    [InlineData("Times-Roman", "m", 778)]
    [InlineData("Times-Roman", " ", 250)]
    [InlineData("Times-Roman", "é", 444)]
    [InlineData("Times-Bold", "W", 1000)]
    [InlineData("Times-Bold", "i", 278)]
    [InlineData("Times-Bold", "m", 833)]
    [InlineData("Times-Italic", "i", 278)]
    [InlineData("Times-BoldItalic", "i", 278)]
    [InlineData("Helvetica", "W", 944)]
    [InlineData("Helvetica", "i", 222)]
    [InlineData("Helvetica", "m", 833)]
    [InlineData("Helvetica", "é", 556)]
    [InlineData("Helvetica-Bold", "i", 278)]
    [InlineData("Helvetica-Bold", "W", 944)]
    [InlineData("Courier", "i", 600)]
    [InlineData("Courier", "W", 600)]
    [InlineData("Courier-Bold", "é", 600)]
    // 39 and 96 are quotesingle and grave under the WinAnsiEncoding a PdfFont declares.
    [InlineData("Times-Roman", "'", 180)]
    [InlineData("Times-Bold", "'", 278)]
    [InlineData("Times-Italic", "'", 214)]
    [InlineData("Helvetica", "'", 191)]
    [InlineData("Helvetica-Bold", "'", 238)]
    [InlineData("Courier", "'", 600)]
    [InlineData("Times-Roman", "`", 333)]
    [InlineData("Helvetica", "`", 333)]
    [InlineData("Symbol", "a", 631)]
    [InlineData("ZapfDingbats", "!", 974)]
    [InlineData("Times-Roman", "—", 1000)]
    public void MeasureWidth_IsTheFacesAfmAdvance(string baseFont, string text, int afmUnits)
        => new PdfFont("F1", baseFont, 1000).MeasureWidth(text).Should().Be(afmUnits);

    [Theory]
    [InlineData("Helvetica")]
    [InlineData("Helvetica-Bold")]
    [InlineData("Helvetica-Oblique")]
    [InlineData("Helvetica-BoldOblique")]
    [InlineData("Times-Roman")]
    [InlineData("Times-Bold")]
    [InlineData("Times-Italic")]
    [InlineData("Times-BoldItalic")]
    [InlineData("Courier")]
    [InlineData("Courier-Bold")]
    [InlineData("Courier-Oblique")]
    [InlineData("Courier-BoldOblique")]
    [InlineData("Symbol")]
    [InlineData("ZapfDingbats")]
    public void MeasureWidth_EqualsStandardFontMetricsForEveryAsciiChar(string baseFont)
    {
        var font = new PdfFont("F1", baseFont, 1000);
        for (var code = 32; code <= 126; code++)
        {
            if (code is 39 or 96) continue; // WinAnsi quotesingle and grave, not the tables' quoteright and quoteleft
            StandardFontMetrics.TryGetWidth(baseFont, code, out var afm).Should().BeTrue();
            font.MeasureWidth(((char)code).ToString()).Should().Be(afm, $"{baseFont} code {code}");
        }
    }

    [Fact]
    public void MeasureWidth_ScalesByFontSize()
        => PdfFont.TimesRoman(12).MeasureWidth("Wi").Should().BeApproximately((944 + 278) * 12 / 1000.0, 1e-9);
}
