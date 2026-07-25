using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Visual;

/// <summary>
/// Author → save → reopen → render round-trip for the shape annotations added
/// in #626. The authored /AP /N appearance stream — not any synthesized
/// fallback — must be what puts ink on the page, in the right place, in the
/// right colors. Rendering here is excise's own renderer; the independent-viewer
/// corroboration lives in Differential/AnnotationAuthoringDifferentialTests.
/// </summary>
public class AuthoredShapeAnnotationRenderingTests
{
    // Page is 612x792; render at 72 dpi so 1 PDF point == 1 pixel,
    // raster y = 792 - pdfY.
    private const int PageW = 612;
    private const int PageH = 792;

    [Fact]
    public void AuthoredSquare_RendersStrokeOnRectEdges_AfterSaveReload()
    {
        var rect = new PdfRectangle(100, 500, 300, 600);
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.AddSquareAnnotation(1, rect, red: 1, green: 0, blue: 0, borderWidth: 4);
            saved = doc.SaveToBytes();
        }

        using var reopened = PdfDocument.Open(saved);
        var annot = reopened.GetPage(1).GetAnnotations().Single();
        annot.HasAppearance.Should().BeTrue("the render below must go through /AP /N");

        using var bitmap = Render(reopened);

        // Stroke ink on all four edges (sample a band around each edge midpoint).
        CountReddish(bitmap, PdfBand(100, 550, pad: 4)).Should().BeGreaterThan(0, "left edge");
        CountReddish(bitmap, PdfBand(300, 550, pad: 4)).Should().BeGreaterThan(0, "right edge");
        CountReddish(bitmap, PdfBand(200, 600, pad: 4)).Should().BeGreaterThan(0, "top edge");
        CountReddish(bitmap, PdfBand(200, 500, pad: 4)).Should().BeGreaterThan(0, "bottom edge");

        // Stroke-only shape: the interior stays white.
        CountInk(bitmap, PdfBand(200, 550, pad: 20)).Should().Be(0,
            "a stroke-only square must not fill its interior");

        // And nothing outside the rect.
        CountInk(bitmap, PdfBand(450, 550, pad: 30)).Should().Be(0,
            "ink must stay inside the annotation /Rect");
    }

    [Fact]
    public void AuthoredFilledCircle_RendersInteriorFillInsideAndNotInCorners()
    {
        var rect = new PdfRectangle(200, 200, 360, 320);
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.AddCircleAnnotation(1, rect,
                red: 0, green: 0, blue: 1, borderWidth: 2,
                interiorRed: 0, interiorGreen: 0.8, interiorBlue: 0);
            saved = doc.SaveToBytes();
        }

        using var reopened = PdfDocument.Open(saved);
        using var bitmap = Render(reopened);

        // Interior fill at the ellipse center.
        CountGreenish(bitmap, PdfBand(280, 260, pad: 10)).Should().BeGreaterThan(50,
            "the ellipse center must carry the /IC interior fill");

        // The rect corners lie OUTSIDE the inscribed ellipse — a circle
        // appearance that degenerated into a filled rectangle fails here.
        CountInk(bitmap, PdfBand(203, 317, pad: 2)).Should().Be(0,
            "the top-left corner of /Rect is outside the inscribed ellipse");
        CountInk(bitmap, PdfBand(357, 203, pad: 2)).Should().Be(0,
            "the bottom-right corner of /Rect is outside the inscribed ellipse");

        // Border stroke at the rightmost point of the ellipse.
        CountBluish(bitmap, PdfBand(360, 260, pad: 4)).Should().BeGreaterThan(0,
            "the /C border color must stroke the ellipse outline");
    }

    private static SKBitmap Render(PdfDocument doc) =>
        new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 72, AntiAlias = false, BackgroundColor = SKColors.White });

    /// <summary>Pixel box centred on a PDF-space point (72 dpi ⇒ 1:1, y flipped).</summary>
    private static SKRectI PdfBand(double pdfX, double pdfY, int pad)
    {
        int cx = (int)pdfX;
        int cy = PageH - (int)pdfY;
        return new SKRectI(
            Math.Max(0, cx - pad), Math.Max(0, cy - pad),
            Math.Min(PageW - 1, cx + pad), Math.Min(PageH - 1, cy + pad));
    }

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

    private static int CountReddish(SKBitmap bmp, SKRectI box) =>
        Count(bmp, box, p => p.Red > 180 && p.Green < 100 && p.Blue < 100);

    private static int CountGreenish(SKBitmap bmp, SKRectI box) =>
        Count(bmp, box, p => p.Green > 150 && p.Red < 100 && p.Blue < 100);

    private static int CountBluish(SKBitmap bmp, SKRectI box) =>
        Count(bmp, box, p => p.Blue > 180 && p.Red < 100 && p.Green < 100);
}
