using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// <see cref="PdfFont.MeasureWidth"/> is what authoring wraps, centres and
/// ellipsises on (#1835). It used to return Helvetica's widths for every
/// standard-14 face. mutool shares no code or table with excise, so the
/// advance it places each glyph at is the independent measure of the width the
/// same string is drawn with.
/// </summary>
public class StandardFontAdvanceOracleTests : IDisposable
{
    private const double Size = 20;
    private const double StartX = 72;
    private const double ToleranceOfPrintedPoints = 0.06;

    private readonly List<string> _temp = new();

    [Theory]
    [InlineData("Times-Roman")]
    [InlineData("Times-Bold")]
    [InlineData("Times-Italic")]
    [InlineData("Times-BoldItalic")]
    [InlineData("Helvetica")]
    [InlineData("Helvetica-Bold")]
    [InlineData("Helvetica-Oblique")]
    [InlineData("Helvetica-BoldOblique")]
    [InlineData("Courier")]
    [InlineData("Courier-BoldOblique")]
    public void MeasureWidth_MatchesTheAdvanceMutoolPlacesEachGlyphAt(string baseFont)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        // Letters whose Times, Helvetica and Courier widths all differ, WinAnsi high, an em dash, and
        // codes 39 and 96: quotesingle and grave under WinAnsi, quoteright and quoteleft in the 32-126 AFM tables.
        const string text = "WiTmjl Hello Waé—io don't `x` Wi'i";
        var font = new PdfFont("F1", baseFont, Size);

        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            var page = doc.Pages.AddBlank();
            using (var graphics = page.GetGraphics())
                graphics.DrawString(text, font, PdfBrush.Black, StartX, 700);
            saved = doc.SaveToBytes();
        }

        var glyphs = MutoolGlyphPositions.ExtractPage(SaveTemp(saved), 1);
        glyphs.Should().NotBeNull();
        glyphs!.Should().HaveCount(text.Length, "one stext glyph per character, spaces included");

        for (var i = 1; i < text.Length; i++)
        {
            var mutoolAdvance = glyphs[i].X - glyphs[0].X;
            font.MeasureWidth(text[..i]).Should().BeApproximately(mutoolAdvance, ToleranceOfPrintedPoints,
                $"{baseFont}: advance before '{text[i]}' at index {i}");
        }
    }

    private string SaveTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-font-advance-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        _temp.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
