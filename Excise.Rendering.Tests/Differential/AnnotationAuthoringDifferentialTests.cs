using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// No-self-oracle corroboration for the #626 shape-annotation authoring:
/// excise generating an /AP /N appearance stream and excise rendering it proves
/// only self-consistency. Here the authored file is handed to renderers that
/// are not excise (mutool, pdftocairo) and both must draw the annotation the
/// same way — ink present inside the shape, in the authored color, none
/// outside the rect. If a reference viewer shows nothing where excise shows a
/// shape, the appearance stream is wrong no matter what our renderer says.
/// </summary>
public class AnnotationAuthoringDifferentialTests : IDisposable
{
    private const int Dpi = 72; // 1 PDF point == 1 pixel on a 612x792 page
    private const int PageH = 792;

    private readonly List<string> _temp = new();

    [Fact]
    public void AuthoredFilledSquare_IsDrawnByMutool_WithAuthoredFillColor()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = SaveShapesPdf();
        using var rendered = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull("mutool must be able to open and render the authored file");

        AssertShapePixels(rendered!, "mutool");
    }

    [Fact]
    public void AuthoredFilledSquare_IsDrawnByPdftocairo_WithAuthoredFillColor()
    {
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        var path = SaveShapesPdf();
        using var rendered = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull("pdftocairo must be able to open and render the authored file");

        AssertShapePixels(rendered!, "pdftocairo");
    }

    [Fact]
    public void ExciseAndReferenceRenderer_AgreeOnAuthoredShapeInk()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = SaveShapesPdf();

        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        using var reopened = PdfDocument.Open(path);
        using var excise = new SkiaRenderer().RenderPage(
            reopened.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

        // Both renderers must agree the filled square's interior is inked and
        // the area outside every annotation is blank. Fractions, not pixel
        // equality — anti-aliasing and stroke placement legitimately differ.
        var squareInterior = Box(120, 520, 280, 580);
        var emptyArea = Box(400, 500, 560, 600);

        var exciseFill = InkFraction(excise, squareInterior);
        var referenceFill = InkFraction(reference!, squareInterior);

        exciseFill.Should().BeGreaterThan(0.9, "excise must fill the authored square interior");
        referenceFill.Should().BeGreaterThan(0.9, "mutool must fill the authored square interior");
        Math.Abs(exciseFill - referenceFill).Should().BeLessThan(0.05,
            "the two renderers must honour the same appearance stream the same way");

        InkFraction(excise, emptyArea).Should().Be(0, "excise: no ink outside the annotations");
        InkFraction(reference!, emptyArea).Should().Be(0, "mutool: no ink outside the annotations");
    }

    // ── FreeText (#626, §12.5.6.6) ──────────────────────────────────────────

    [Fact]
    public void AuthoredFreeText_IsDrawnByMutool_WithTextInkInsideTheRect()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = SaveFreeTextPdf();
        using var rendered = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull("mutool must be able to open and render the authored file");

        AssertFreeTextPixels(rendered!, "mutool");
    }

    [Fact]
    public void AuthoredFreeText_IsDrawnByPdftocairo_WithTextInkInsideTheRect()
    {
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        var path = SaveFreeTextPdf();
        using var rendered = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull("pdftocairo must be able to open and render the authored file");

        AssertFreeTextPixels(rendered!, "pdftocairo");
    }

    [Fact]
    public void ExciseAndReferenceRenderer_AgreeOnAuthoredFreeTextInk()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = SaveFreeTextPdf();

        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        using var reopened = PdfDocument.Open(path);
        using var excise = new SkiaRenderer().RenderPage(
            reopened.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

        // Glyph rasterization legitimately differs between engines (hinting,
        // AA, base-14 substitute fonts), so no tight pixel agreement — but
        // both must put real text ink in the first-line band and leave the
        // page outside the annotation blank.
        var textBand = Box(104, 520, 300, 548);
        var emptyArea = Box(420, 480, 560, 560);

        InkFraction(excise, textBand).Should().BeGreaterThan(0.05,
            "excise must draw the authored FreeText glyphs");
        InkFraction(reference!, textBand).Should().BeGreaterThan(0.05,
            "mutool must draw the authored FreeText glyphs from the /AP stream");

        InkFraction(excise, emptyArea).Should().Be(0, "excise: no ink outside the annotation");
        InkFraction(reference!, emptyArea).Should().Be(0, "mutool: no ink outside the annotation");
    }

    // ── Ink (#626, §12.5.6.13) ──────────────────────────────────────────────

    [Fact]
    public void AuthoredInk_IsDrawnByMutool_WithStrokeInkAlongThePolyline()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = SaveInkPdf();
        using var rendered = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull("mutool must be able to open and render the authored file");

        AssertInkPixels(rendered!, "mutool");
    }

    [Fact]
    public void AuthoredInk_IsDrawnByPdftocairo_WithStrokeInkAlongThePolyline()
    {
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        var path = SaveInkPdf();
        using var rendered = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull("pdftocairo must be able to open and render the authored file");

        AssertInkPixels(rendered!, "pdftocairo");
    }

    [Fact]
    public void ExciseAndReferenceRenderer_AgreeOnAuthoredInkStrokes()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = SaveInkPdf();

        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        using var reopened = PdfDocument.Open(path);
        using var excise = new SkiaRenderer().RenderPage(
            reopened.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

        // Stroke rasterization legitimately differs (AA, cap rendering), so no
        // tight pixel agreement — but both must put stroke ink along the
        // horizontal polyline band and leave the page off the path blank.
        var lineBand = Box(120, 695, 280, 705);
        var betweenStrokes = Box(120, 662, 280, 690);
        var emptyArea = Box(350, 300, 560, 500);

        InkFraction(excise, lineBand).Should().BeGreaterThan(0.2,
            "excise must stroke the authored ink polyline");
        InkFraction(reference!, lineBand).Should().BeGreaterThan(0.2,
            "mutool must stroke the authored ink polyline from the /AP stream");

        InkFraction(excise, betweenStrokes).Should().Be(0,
            "excise: no ink between the two strokes");
        InkFraction(reference!, betweenStrokes).Should().Be(0,
            "mutool: no ink between the two strokes");
        InkFraction(excise, emptyArea).Should().Be(0, "excise: no ink outside the annotation");
        InkFraction(reference!, emptyArea).Should().Be(0, "mutool: no ink outside the annotation");
    }

    private void AssertInkPixels(SKBitmap bmp, string tool)
    {
        // Stroke ink along the horizontal polyline (y=700, 4pt pen).
        InkFraction(bmp, Box(120, 695, 280, 705)).Should().BeGreaterThan(0.2,
            $"{tool} must stroke the ink polyline from the authored /AP stream");
        RedFraction(bmp, Box(120, 695, 280, 705)).Should().BeGreaterThan(0.2,
            $"{tool} must use the authored red /C stroke color");

        // Stroke ink at the V-vertex of the second polyline (200,600).
        InkFraction(bmp, Box(190, 594, 210, 610)).Should().BeGreaterThan(0.05,
            $"{tool} must stroke the second polyline through its vertex");

        // No ink between the two strokes (inside the /Rect but off the path)...
        InkFraction(bmp, Box(120, 662, 280, 690)).Should().Be(0,
            $"{tool}: the ink annotation must not fill its /Rect, only stroke the path");

        // ...and nothing anywhere else on the page.
        InkFraction(bmp, Box(350, 300, 560, 500)).Should().Be(0,
            $"{tool}: the authored ink must not spill outside its strokes");
        InkFraction(bmp, Box(60, 60, 560, 180)).Should().Be(0,
            $"{tool}: the rest of the page must stay blank");
    }

    /// <summary>
    /// One blank page carrying an authored Ink annotation with two strokes:
    ///   horizontal (100,700)→(300,700) and a V (100,650)→(200,600)→(300,650),
    ///   red, 4pt pen. Saved, reopened from bytes, and written to a temp file —
    /// the file the reference tools see is a genuine save/reload product, not
    /// in-memory state.
    /// </summary>
    private string SaveInkPdf()
    {
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.AddInkAnnotation(1,
                new[]
                {
                    new[] { (100.0, 700.0), (300.0, 700.0) },
                    new[] { (100.0, 650.0), (200.0, 600.0), (300.0, 650.0) }
                },
                contents: "freehand", red: 1, green: 0, blue: 0, borderWidth: 4);
            saved = doc.SaveToBytes();
        }

        var path = Path.Combine(Path.GetTempPath(), $"excise-annot-ink-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, saved);
        _temp.Add(path);
        return path;
    }

    private void AssertFreeTextPixels(SKBitmap bmp, string tool)
    {
        // Text ink where the first wrapped line ("REVIEW REVIEW", 24pt from
        // x=104, baseline pdfY 536.8) puts its glyphs.
        InkFraction(bmp, Box(104, 520, 300, 548)).Should().BeGreaterThan(0.05,
            $"{tool} must draw the FreeText glyphs from the authored /AP stream");

        // Border stroke on the left edge of the rect.
        InkFraction(bmp, Box(98, 500, 102, 540)).Should().BeGreaterThan(0.3,
            $"{tool} must stroke the FreeText border from the authored /AP stream");

        // Nothing outside the annotation rect.
        InkFraction(bmp, Box(420, 480, 560, 560)).Should().Be(0,
            $"{tool}: the authored FreeText must not spill ink outside its /Rect");
        InkFraction(bmp, Box(60, 60, 560, 180)).Should().Be(0,
            $"{tool}: the rest of the page must stay blank");
    }

    /// <summary>
    /// One blank page carrying an authored FreeText:
    ///   /Rect [100 480 400 560], "REVIEW REVIEW REVIEW" at 24pt black,
    ///   2pt border — wraps to two lines, first baseline at pdfY 536.8.
    /// Saved, reopened from bytes, and written to a temp file — the file the
    /// reference tools see is a genuine save/reload product, not in-memory state.
    /// </summary>
    private string SaveFreeTextPdf()
    {
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.AddFreeTextAnnotation(1, new PdfRectangle(100, 480, 400, 560),
                text: "REVIEW REVIEW REVIEW", fontSize: 24, borderWidth: 2);
            saved = doc.SaveToBytes();
        }

        var path = Path.Combine(Path.GetTempPath(), $"excise-annot-freetext-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, saved);
        _temp.Add(path);
        return path;
    }

    private void AssertShapePixels(SKBitmap bmp, string tool)
    {
        // Filled square: interior carries the red /IC fill.
        InkFraction(bmp, Box(120, 520, 280, 580)).Should().BeGreaterThan(0.9,
            $"{tool} must draw the square's interior fill from the authored /AP stream");
        RedFraction(bmp, Box(120, 520, 280, 580)).Should().BeGreaterThan(0.9,
            $"{tool} must use the authored red /IC fill color");

        // Stroke-only circle: ink on the outline...
        InkFraction(bmp, Box(436, 296, 444, 324)).Should().BeGreaterThan(0.05,
            $"{tool} must stroke the circle outline (rightmost point of the ellipse)");
        // ...but not at the rect corners (outside the inscribed ellipse) and
        // only the faintest anti-aliasing at the very center is acceptable.
        InkFraction(bmp, Box(361, 356, 367, 362)).Should().Be(0,
            $"{tool}: the rect's top-left corner lies outside the inscribed ellipse");

        // Nothing anywhere else on the page.
        InkFraction(bmp, Box(60, 60, 560, 180)).Should().Be(0,
            $"{tool}: the authored annotations must not spill ink elsewhere");
    }

    /// <summary>
    /// One blank page carrying both authored shapes:
    ///   Square  /Rect [100 500 300 600], red interior fill + dark-red border
    ///   Circle  /Rect [360 300 440 360], stroke-only blue outline, width 2
    /// Saved, reopened from bytes, and written to a temp file — the file the
    /// reference tools see is a genuine save/reload product, not in-memory state.
    /// </summary>
    private string SaveShapesPdf()
    {
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.AddSquareAnnotation(1, new PdfRectangle(100, 500, 300, 600),
                contents: "square", red: 0.5, green: 0, blue: 0, borderWidth: 2,
                interiorRed: 1, interiorGreen: 0, interiorBlue: 0);
            doc.AddCircleAnnotation(1, new PdfRectangle(360, 300, 440, 360),
                contents: "circle", red: 0, green: 0, blue: 1, borderWidth: 2);
            saved = doc.SaveToBytes();
        }

        var path = Path.Combine(Path.GetTempPath(), $"excise-annot-author-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, saved);
        _temp.Add(path);
        return path;
    }

    /// <summary>Pixel box from PDF coordinates (Y-up) at 72 dpi.</summary>
    private static SKRectI Box(double left, double bottom, double right, double top) =>
        new((int)left, PageH - (int)top, (int)right, PageH - (int)bottom);

    private static double Fraction(SKBitmap bmp, SKRectI box, Func<SKColor, bool> predicate)
    {
        int hit = 0, total = 0;
        int x0 = Math.Max(0, box.Left), x1 = Math.Min(bmp.Width - 1, box.Right);
        int y0 = Math.Max(0, box.Top), y1 = Math.Min(bmp.Height - 1, box.Bottom);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            total++;
            if (predicate(bmp.GetPixel(x, y))) hit++;
        }
        return total == 0 ? 0 : (double)hit / total;
    }

    private static double InkFraction(SKBitmap bmp, SKRectI box) =>
        Fraction(bmp, box, p => p.Red < 200 || p.Green < 200 || p.Blue < 200);

    private static double RedFraction(SKBitmap bmp, SKRectI box) =>
        Fraction(bmp, box, p => p.Red > 180 && p.Green < 100 && p.Blue < 100);

    public void Dispose()
    {
        foreach (var p in _temp)
        {
            try { File.Delete(p); } catch { /* best effort */ }
        }
    }
}
