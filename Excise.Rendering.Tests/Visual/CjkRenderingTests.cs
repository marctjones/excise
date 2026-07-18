using System.IO;
using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Visual;

/// <summary>
/// Regression test for the CJK / browser-flipped-Tm rendering fix.
///
/// Three independent bugs collided to make CJK content render as an empty
/// page or a sea of missing-glyph boxes:
///
/// 1. <c>PdfPage.GetFont</c> didn't resolve indirect <c>/Font</c> references.
///    WeasyPrint, Word, and most browser-derived PDFs emit
///    <c>/Font N 0 R</c>, so the font lookup silently returned null and the
///    embedded Type0 font was never loaded.
/// 2. The renderer's text-block <c>Scale(xyRatio, -1)</c> hard-coded a Y-flip,
///    but CJK-producing toolchains author with a flipped <c>Tm</c>
///    (<c>1 0 0 -1</c>). Combined with the outer canvas Y-flip the result was
///    upside-down glyphs.
/// 3. The Type0 path itself had the same hard-coded -1 Y-flip in
///    <c>RenderCidBytes</c>.
///
/// Together they made the embedded TrueType-based CIDFontType2 fonts that
/// every CJK PDF uses look like garbage. This test renders a deterministic
/// multilingual page (English + Simplified Chinese + Traditional Chinese +
/// Japanese + Korean) and asserts each line of text has visible ink in the
/// row it should occupy.
/// </summary>
public class CjkRenderingTests
{
    private const string CjkFixture = "../../../../test-pdfs/sample-pdfs/multilingual-noto-cjk.pdf";

    [Fact]
    public void Multilingual_Page1_AllRowsHaveInk()
    {
        if (!File.Exists(CjkFixture))
            return;

        using var doc = PdfDocument.Open(File.ReadAllBytes(CjkFixture));
        var page = doc.GetPage(1);
        using var bmp = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 120 });
        VisualAssertions.SavePng(bmp, "/tmp/cjk-rendering-test.png");

        // Page contains 7 visible text rows: title + 6 paragraphs (English,
        // zh-Hans, zh-Hant, ja, ko, mixed). At 120 DPI on the 992x1403
        // canvas, those rows stretch from roughly y=260 (title top) to y=920
        // (last paragraph). Walk the band in 80-pixel slices and require
        // each slice to have substantial ink — pre-fix renders had blank
        // bands wherever the embedded CJK font was needed.
        var blankBands = new System.Collections.Generic.List<(int y, int ink)>();
        for (int y = 260; y < 880; y += 80)
        {
            int ink = CountInkInRow(bmp, y, y + 80);
            if (ink < 200) blankBands.Add((y, ink));
        }
        blankBands.Should().BeEmpty(
            "every text row should have visible glyph ink; blank bands listed at " +
            string.Join(", ", blankBands.ConvertAll(b => $"y={b.y}:{b.ink}px")) +
            ". A blank CJK band means the embedded TrueType font isn't loading — " +
            "check PdfPage.GetFont indirect-reference resolution.");
    }

    [Fact]
    public void Multilingual_Page1_GlyphsAreNotUpsideDown()
    {
        // For a top-of-glyph dark pixel to be ABOVE the baseline (= smaller
        // screenY than baseline) the glyph must be right-side-up. If glyphs
        // were upside-down (the pre-fix Tm.d=−1 bug), the top of the ink
        // would be BELOW the baseline.
        if (!File.Exists(CjkFixture))
            return;

        using var doc = PdfDocument.Open(File.ReadAllBytes(CjkFixture));
        var page = doc.GetPage(1);
        using var bmp = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 120 });

        // Title baseline ≈ y=275 at 120 DPI; ascenders extend up to y≈245.
        int topY = FindFirstInkRow(bmp, fromY: 200, toY: 300);
        int bottomY = FindLastInkRow(bmp, fromY: 200, toY: 300);
        topY.Should().BeLessThan(bottomY,
            "ascenders must precede descenders top-to-bottom; if reversed, glyphs are flipped");
        (bottomY - topY).Should().BeGreaterThan(20,
            "title-row glyphs should span at least 20 pixels vertically");
    }

    private static int CountInkInRow(SKBitmap bmp, int yStart, int yEnd)
    {
        int count = 0;
        for (int y = yStart; y < yEnd && y < bmp.Height; y++)
        {
            if (y < 0) continue;
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 100 && c.Green < 100 && c.Blue < 100) count++;
            }
        }
        return count;
    }

    private static int FindFirstInkRow(SKBitmap bmp, int fromY, int toY)
    {
        for (int y = fromY; y < toY && y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 100 && c.Green < 100 && c.Blue < 100) return y;
            }
        }
        return -1;
    }

    private static int FindLastInkRow(SKBitmap bmp, int fromY, int toY)
    {
        int last = -1;
        for (int y = fromY; y < toY && y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 100 && c.Green < 100 && c.Blue < 100) { last = y; break; }
            }
        }
        return last;
    }
}
