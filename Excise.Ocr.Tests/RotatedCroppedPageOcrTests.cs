using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Primitives;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Ocr.Tests;

/// <summary>
/// #1832: the renderer draws the EffectiveCropBox with /Rotate applied, so OCR
/// pixels are visual pixels. Word boxes must come back in content points and the
/// image-only redaction page must have the visual size. The fixture has a
/// non-zero MediaBox origin and a CropBox that is neither the MediaBox nor at
/// its origin, so a height-only flip is wrong at every rotation, including 0.
/// </summary>
public class RotatedCroppedPageOcrTests
{
    // MediaBox [100 50 700 850]; CropBox [200 150 500 650]: l=200, b=150, w=300, h=500.
    private static PdfPage RotatedCroppedPage(PdfDocument doc, int rotation)
    {
        var page = doc.Pages.AddBlank(600, 800);
        page.Dictionary["MediaBox"] = PdfArray.FromRectangle(100, 50, 700, 850);
        page.Dictionary["CropBox"] = PdfArray.FromRectangle(200, 150, 500, 650);
        page.Rotation = rotation;
        return page;
    }

    private static bool TesseractAvailable => new PdfOcrService().IsAvailable();

    /// <summary>
    /// Expected values are derived by hand from rotating the unrotated crop-box
    /// image clockwise by /Rotate (ISO 32000-2 §7.7.3.3), not from the mapper:
    /// visual x∈[10,110], y∈[20,50] (y down) at 72 dpi, so pixels are points.
    /// </summary>
    [Theory]
    [InlineData(0, 210, 600, 310, 630)]
    [InlineData(90, 220, 160, 250, 260)]
    [InlineData(180, 390, 170, 490, 200)]
    [InlineData(270, 450, 540, 480, 640)]
    public void ParseTsv_ReportsWordBoxesInContentPoints(
        int rotation, double left, double bottom, double right, double top)
    {
        using var doc = PdfDocument.CreateNew();
        var page = RotatedCroppedPage(doc, rotation);
        const string tsv =
            "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
            "5\t1\t1\t1\t1\t1\t10\t20\t100\t30\t96\tSECRET\n";

        var box = new PdfOcrService(dpi: 72).ParseTsv(tsv, page).Words.Single().BoundingBox;

        box.Left.Should().BeApproximately(left, 0.001);
        box.Bottom.Should().BeApproximately(bottom, 0.001);
        box.Right.Should().BeApproximately(right, 0.001);
        box.Top.Should().BeApproximately(top, 0.001);
        box.Should().Be(PdfCoordinateMapper.ToContentPoints(
            page, PdfPageRect.VisualPoints(page.PageNumber, 10, 20, 100, 30)).ToPdfRectangle());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RecognizePage_WordBoxLandsOnTheDrawnGlyphs(int rotation)
    {
        Assert.SkipUnless(TesseractAvailable, "tesseract CLI not installed");
        using var doc = PdfDocument.CreateNew();
        var page = RotatedCroppedPage(doc, rotation);
        DrawUpright(page, rotation, ("ANCHOR", 350, 400));

        var anchor = new PdfOcrService().RecognizePage(page).Words.Single(w => w.Text == "ANCHOR").BoundingBox;

        var glyphs = BoundsOf(page, "ANCHOR");
        Center(anchor).x.Should().BeApproximately(Center(glyphs).x, 10, $"rotation {rotation}: box {anchor} vs glyphs {glyphs}");
        Center(anchor).y.Should().BeApproximately(Center(glyphs).y, 10, $"rotation {rotation}: box {anchor} vs glyphs {glyphs}");
    }

    [Fact]
    public void RedactToImageOnly_EmitsTheVisualPageAndBlacksOutTheWordWhereItIsSeen()
    {
        Assert.SkipUnless(TesseractAvailable, "tesseract CLI not installed");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var input = Path.Combine(Path.GetTempPath(), $"excise-1832-in-{Guid.NewGuid():N}.pdf");
        var output = Path.Combine(Path.GetTempPath(), $"excise-1832-out-{Guid.NewGuid():N}.pdf");
        try
        {
            PdfRectangle secret, keep;
            using (var doc = PdfDocument.CreateNew())
            {
                var page = RotatedCroppedPage(doc, 90);
                DrawUpright(page, 90, ("SECRET", 300, 400), ("KEEP", 420, 400));
                secret = BoundsOf(page, "SECRET");
                keep = BoundsOf(page, "KEEP");
                doc.Save(input);
            }

            new PdfRasterRedactionConverter(new PdfOcrService()).RedactToImageOnly(input, output, "SECRET")
                .Should().Be(1);

            using (var redacted = PdfDocument.Open(output))
            {
                var page = redacted.GetPage(1);
                page.Rotation.Should().Be(0);
                page.Width.Should().BeApproximately(500, 0.001, "the visual width of a /Rotate 90 page is its CropBox height");
                page.Height.Should().BeApproximately(300, 0.001, "the visual height of a /Rotate 90 page is its CropBox width");
            }

            // Independent renderer. /Rotate 90 with CropBox origin (200,150): visual x = cy - 150, y = cx - 200.
            const int dpi = 144;
            using var rendered = MutoolReferenceRenderer.RenderPage(output, 1, dpi);
            rendered.Should().NotBeNull();
            var secretInk = InkFractionIn(rendered!, secret, dpi);
            secretInk.Should().BeGreaterThan(0.95, $"the pixels where SECRET is seen must be painted over (ink={secretInk:P1})");
            var keepInk = InkFractionIn(rendered!, keep, dpi);
            keepInk.Should().BeInRange(0.02, 0.6, $"KEEP must survive as text, not blank and not blacked out (ink={keepInk:P1})");
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    // Draw each word so it reads left to right AFTER the page is displayed:
    // /Rotate turns the page clockwise, so the text is turned the same amount
    // anticlockwise about (cx, cy) in content space.
    private static void DrawUpright(PdfPage page, int rotation, params (string Text, double Cx, double Cy)[] words)
    {
        using var g = page.GetGraphics();
        foreach (var (text, cx, cy) in words)
        {
            g.SaveState();
            g.Translate(cx, cy);
            g.Rotate(rotation);
            g.DrawString(text, PdfFont.Helvetica(28), PdfBrush.Black, -55, -10);
            g.RestoreState();
        }
    }

    private static PdfRectangle BoundsOf(PdfPage page, string word)
    {
        var letters = page.Letters;
        var start = string.Concat(letters.Select(l => l.Value)).IndexOf(word, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"'{word}' must be in the page's letters");
        var slice = letters.Skip(start).Take(word.Length).Select(l => l.GlyphRectangle.Normalize()).ToList();
        return new PdfRectangle(
            slice.Min(r => r.Left), slice.Min(r => r.Bottom), slice.Max(r => r.Right), slice.Max(r => r.Top));
    }

    private static (double x, double y) Center(PdfRectangle r) => ((r.Left + r.Right) / 2, (r.Bottom + r.Top) / 2);

    // Ink in the central half of the word's box on the rendered /Rotate 90 output:
    // the glyph box includes ascender and descender space that holds no ink.
    private static double InkFractionIn(SKBitmap bmp, PdfRectangle contentBox, int dpi)
    {
        double s = dpi / 72.0;
        double vx0 = contentBox.Bottom - 150, vx1 = contentBox.Top - 150;
        double vy0 = contentBox.Left - 200, vy1 = contentBox.Right - 200;
        double qx = (vx1 - vx0) / 4, qy = (vy1 - vy0) / 4;
        int x0 = (int)((vx0 + qx) * s), x1 = (int)((vx1 - qx) * s);
        int y0 = (int)((vy0 + qy) * s), y1 = (int)((vy1 - qy) * s);
        int ink = 0, total = 0;
        for (int y = Math.Max(0, y0); y <= Math.Min(bmp.Height - 1, y1); y++)
        for (int x = Math.Max(0, x0); x <= Math.Min(bmp.Width - 1, x1); x++)
        {
            var p = bmp.GetPixel(x, y);
            total++;
            if (p.Red < 128 && p.Green < 128 && p.Blue < 128) ink++;
        }
        return total == 0 ? 0 : (double)ink / total;
    }
}
