using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Visual;

/// <summary>
/// Author → save → reopen → render round-trip for the FreeText annotations
/// added in #626. The authored /AP /N appearance stream — not any synthesized
/// fallback — must be what puts the text ink on the page, at the quadding the
/// /Q entry declares, over the /C background. Rendering here is excise's own
/// renderer; the independent-viewer corroboration lives in
/// Differential/AnnotationAuthoringDifferentialTests.
/// </summary>
public class AuthoredFreeTextAnnotationRenderingTests
{
    // Page is 612x792; render at 72 dpi so 1 PDF point == 1 pixel,
    // raster y = 792 - pdfY.
    private const int PageW = 612;
    private const int PageH = 792;

    [Fact]
    public void AuthoredFreeText_RendersTextInkInsideRect_AfterSaveReload()
    {
        // 24pt text, 2pt border: first baseline at rect.Top - pad - 0.8*24
        // = 560 - 4 - 19.2 = 536.8; "REVIEW" is ~95pt wide from x = 104.
        var rect = new PdfRectangle(100, 480, 400, 560);
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.AddFreeTextAnnotation(1, rect, "REVIEW",
                fontSize: 24, borderWidth: 2);
            saved = doc.SaveToBytes();
        }

        using var reopened = PdfDocument.Open(saved);
        var annot = reopened.GetPage(1).GetAnnotations().Single();
        annot.HasAppearance.Should().BeTrue("the render below must go through /AP /N");

        using var bitmap = Render(reopened);

        // Text ink where the first line's glyphs sit.
        CountInk(bitmap, PdfBox(105, 520, 200, 545)).Should().BeGreaterThan(20,
            "the authored /AP must draw the FreeText glyphs inside the rect");

        // Border stroke on the rect edges.
        CountInk(bitmap, PdfBox(97, 515, 103, 525)).Should().BeGreaterThan(0, "left border edge");
        CountInk(bitmap, PdfBox(397, 515, 403, 525)).Should().BeGreaterThan(0, "right border edge");

        // Nothing outside the rect.
        CountInk(bitmap, PdfBox(420, 480, 560, 560)).Should().Be(0,
            "ink must stay inside the annotation /Rect");
    }

    [Fact]
    public void AuthoredFreeText_HonoursQuaddingAndBackground()
    {
        var leftRect = new PdfRectangle(100, 600, 400, 650);
        var rightRect = new PdfRectangle(100, 300, 400, 350);
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.AddFreeTextAnnotation(1, leftRect, "Hi",
                fontSize: 20, quadding: PdfFreeTextQuadding.LeftJustified);
            doc.AddFreeTextAnnotation(1, rightRect, "Hi",
                fontSize: 20, quadding: PdfFreeTextQuadding.RightJustified,
                backgroundRed: 0, backgroundGreen: 0.8, backgroundBlue: 0);
            saved = doc.SaveToBytes();
        }

        using var reopened = PdfDocument.Open(saved);
        using var bitmap = Render(reopened);

        // Left-justified: glyph ink hugs the left padding edge, none on the right.
        CountInk(bitmap, PdfBox(102, 615, 140, 645)).Should().BeGreaterThan(10,
            "left-justified text starts at the left padding edge");
        CountInk(bitmap, PdfBox(300, 615, 398, 645)).Should().Be(0,
            "left-justified 'Hi' leaves the right side of the box blank");

        // Right-justified: glyph ink hugs the right padding edge; the left side
        // carries only the green background, no black glyphs.
        CountBlackish(bitmap, PdfBox(360, 315, 398, 345)).Should().BeGreaterThan(10,
            "right-justified text ends at the right padding edge");
        CountBlackish(bitmap, PdfBox(102, 315, 200, 345)).Should().Be(0,
            "right-justified 'Hi' leaves the left side of the box glyph-free");

        // The /C background fills the box interior.
        CountGreenish(bitmap, PdfBox(150, 310, 250, 340)).Should().BeGreaterThan(500,
            "the authored background color must fill the FreeText box");
    }

    private static SKBitmap Render(PdfDocument doc) =>
        new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, AntiAlias = false, BackgroundColor = SKColors.White });

    /// <summary>Pixel box from PDF coordinates (Y-up) at 72 dpi.</summary>
    private static SKRectI PdfBox(double left, double bottom, double right, double top) =>
        new(
            Math.Max(0, (int)left), Math.Max(0, PageH - (int)top),
            Math.Min(PageW - 1, (int)right), Math.Min(PageH - 1, PageH - (int)bottom));

    private static int Count(SKBitmap bmp, SKRectI box, Func<SKColor, bool> predicate)
    {
        int count = 0;
        for (int y = box.Top; y <= box.Bottom; y++)
        for (int x = box.Left; x <= box.Right; x++)
        {
            if (predicate(bmp.GetPixel(x, y))) count++;
        }
        return count;
    }

    private static int CountInk(SKBitmap bmp, SKRectI box) =>
        Count(bmp, box, p => p.Red < 200 || p.Green < 200 || p.Blue < 200);

    private static int CountBlackish(SKBitmap bmp, SKRectI box) =>
        Count(bmp, box, p => p.Red < 100 && p.Green < 100 && p.Blue < 100);

    private static int CountGreenish(SKBitmap bmp, SKRectI box) =>
        Count(bmp, box, p => p.Green > 150 && p.Red < 100 && p.Blue < 100);
}
