using System.Linq;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Graphics;
using Pdfe.Core.Text;
using Xunit;

namespace Pdfe.Core.Tests.Graphics;

/// <summary>
/// Tests for the high-level text-layout helpers (#379): MeasureText (wrapped)
/// and PdfGraphics.DrawText(rect) with word-wrap + overflow.
/// </summary>
public class TextLayoutTests
{
    private static string ExtractAll(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Join("\n", doc.GetPages().Select(p => new TextExtractor(p).ExtractText()));
    }

    [Fact]
    public void MeasureText_TallerWithMoreWrappedLines()
    {
        var font = PdfFont.Helvetica(12);
        var one = PdfGraphics.MeasureText("short", font, 400);
        var many = PdfGraphics.MeasureText(string.Join(" ", Enumerable.Repeat("word", 200)), font, 100);
        many.Height.Should().BeGreaterThan(one.Height);
        many.Width.Should().BeLessThanOrEqualTo(100 + 0.01);
    }

    [Fact]
    public void DrawText_WrapsAndIsExtractable()
    {
        var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(300, 400);
        var font = PdfFont.Helvetica(12);
        TextLayoutResult result;
        using (var g = page.GetGraphics())
        {
            var bounds = new PdfRectangle(50, 50, 250, 350); // L,B,R,T → 200 wide, 300 tall
            result = g.DrawText(
                "The quick brown fox jumps over the lazy dog several times in a fixed column.",
                font, PdfBrush.Black, bounds);
        }
        result.HasOverflow.Should().BeFalse("the paragraph fits the tall box");
        result.UsedHeight.Should().BeGreaterThan(0);

        var text = ExtractAll(doc.SaveToBytes());
        text.Should().Contain("quick brown fox");
        text.Should().Contain("lazy dog");
    }

    [Fact]
    public void DrawText_ReturnsOverflowWhenBoxTooShort()
    {
        var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(300, 400);
        var font = PdfFont.Helvetica(12);
        using var g = page.GetGraphics();
        // A 1-line-tall box can't hold a long wrapped paragraph.
        var bounds = new PdfRectangle(50, 350, 250, 366);
        var result = g.DrawText(
            string.Join(" ", Enumerable.Repeat("overflow", 80)),
            font, PdfBrush.Black, bounds);

        result.HasOverflow.Should().BeTrue();
        result.Overflow!.Should().Contain("overflow");
    }

    [Fact]
    public void DrawText_OverflowCanFlowToAnotherBox()
    {
        var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(300, 400);
        var font = PdfFont.Helvetica(12);
        var longText = string.Join(" ", Enumerable.Repeat("flow", 40));

        using var g = page.GetGraphics();
        var box1 = new PdfRectangle(20, 330, 140, 380);   // short left column (~3 lines)
        var r1 = g.DrawText(longText, font, PdfBrush.Black, box1);
        r1.HasOverflow.Should().BeTrue();

        var box2 = new PdfRectangle(160, 20, 280, 380);   // tall right column (~25 lines)
        var r2 = g.DrawText(r1.Overflow!, font, PdfBrush.Black, box2);
        // The remainder is short; the tall second column absorbs it.
        r2.HasOverflow.Should().BeFalse();
    }
}
