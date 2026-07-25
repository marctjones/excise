using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// No-self-oracle corroboration for the #626 "remaining annotation subtypes"
/// work (Underline/StrikeOut/Squiggly markup, Line/Arrow, Polygon/PolyLine,
/// standard-name and image Stamp): excise generating an /AP /N appearance
/// stream and excise rendering it proves only self-consistency. Here the
/// authored files are handed to renderers that are not excise (mutool,
/// pdftocairo) and both must draw each annotation the same way. Mirrors the
/// pattern established in <see cref="AnnotationAuthoringDifferentialTests"/>
/// for the earlier #626 subtypes (Square/Circle/FreeText/Ink).
/// </summary>
public class RemainingAnnotationSubtypesDifferentialTests : IDisposable
{
    private const int Dpi = 72; // 1 PDF point == 1 pixel on a 612x792 page
    private const int PageH = 792;

    private readonly List<string> _temp = new();

    // ── Underline / StrikeOut / Squiggly (§12.5.6.10) ────────────────────────

    [Fact]
    public void AuthoredTextMarkup_IsDrawnByMutool_AtDistinctHeights()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = SaveMarkupPdf();
        using var rendered = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull("mutool must be able to open and render the authored file");

        AssertMarkupPixels(rendered!, "mutool");
    }

    [Fact]
    public void AuthoredTextMarkup_IsDrawnByPdftocairo_AtDistinctHeights()
    {
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        var path = SaveMarkupPdf();
        using var rendered = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull("pdftocairo must be able to open and render the authored file");

        AssertMarkupPixels(rendered!, "pdftocairo");
    }

    [Fact]
    public void ExciseAndReferenceRenderer_AgreeOnAuthoredTextMarkupInk()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = SaveMarkupPdf();
        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        using var reopened = PdfDocument.Open(path);
        using var excise = new SkiaRenderer().RenderPage(
            reopened.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

        var underlineBand = Box(105, 702, 395, 706);
        InkFraction(excise, underlineBand).Should().BeGreaterThan(0.1,
            "excise must stroke the authored underline");
        InkFraction(reference!, underlineBand).Should().BeGreaterThan(0.1,
            "mutool must stroke the authored underline from the /AP stream");

        var emptyArea = Box(420, 500, 560, 600);
        InkFraction(excise, emptyArea).Should().Be(0, "excise: no ink outside the markup annotations");
        InkFraction(reference!, emptyArea).Should().Be(0, "mutool: no ink outside the markup annotations");
    }

    private void AssertMarkupPixels(SKBitmap bmp, string tool)
    {
        var underlineBand = Box(105, 702, 395, 706);
        var strikeOutBand = Box(105, 642, 395, 646);
        var squigglyBand = Box(105, 560.5, 395, 566);
        var gapBand = Box(105, 660, 395, 695);
        var emptyArea = Box(420, 500, 560, 600);

        InkFraction(bmp, underlineBand).Should().BeGreaterThan(0.1,
            $"{tool} must stroke the authored underline from the /AP stream");
        RedFraction(bmp, underlineBand).Should().BeGreaterThan(0.1,
            $"{tool} must use the authored red /C color for the underline");

        InkFraction(bmp, strikeOutBand).Should().BeGreaterThan(0.1,
            $"{tool} must stroke the authored strikeout from the /AP stream");

        InkFraction(bmp, squigglyBand).Should().BeGreaterThan(0.05,
            $"{tool} must stroke the authored squiggly zig-zag from the /AP stream");

        InkFraction(bmp, gapBand).Should().Be(0,
            $"{tool}: nothing between the underline and strikeout annotation rects");
        InkFraction(bmp, emptyArea).Should().Be(0, $"{tool}: no ink outside the markup annotations");
    }

    /// <summary>
    /// One blank page carrying an authored Underline, StrikeOut and Squiggly,
    /// each in its own 300x30 band, all red. Saved, reopened from bytes, and
    /// written to a temp file — the file the reference tools see is a genuine
    /// save/reload product, not in-memory state.
    /// </summary>
    private string SaveMarkupPdf()
    {
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.AddUnderlineAnnotation(1, new PdfRectangle(100, 700, 400, 730), red: 1, green: 0, blue: 0);
            doc.AddStrikeOutAnnotation(1, new PdfRectangle(100, 630, 400, 660), red: 1, green: 0, blue: 0);
            doc.AddSquigglyAnnotation(1, new PdfRectangle(100, 560, 400, 590), red: 1, green: 0, blue: 0);
            saved = doc.SaveToBytes();
        }

        var path = Path.Combine(Path.GetTempPath(), $"excise-annot-markup-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, saved);
        _temp.Add(path);
        return path;
    }

    // ── Line / Arrow (§12.5.6.7) ──────────────────────────────────────────────

    [Fact]
    public void AuthoredLineAndArrow_IsDrawnByMutool_WithArrowheadOnlyOnTheArrow()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = SaveLineArrowPdf();
        using var rendered = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull("mutool must be able to open and render the authored file");

        AssertLineArrowPixels(rendered!, "mutool");
    }

    [Fact]
    public void AuthoredLineAndArrow_IsDrawnByPdftocairo_WithArrowheadOnlyOnTheArrow()
    {
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        var path = SaveLineArrowPdf();
        using var rendered = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull("pdftocairo must be able to open and render the authored file");

        AssertLineArrowPixels(rendered!, "pdftocairo");
    }

    [Fact]
    public void ExciseAndReferenceRenderer_AgreeOnAuthoredLineAndArrowInk()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = SaveLineArrowPdf();
        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        using var reopened = PdfDocument.Open(path);
        using var excise = new SkiaRenderer().RenderPage(
            reopened.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

        var arrowHeadRegion = Box(285, 602, 299, 608);
        InkFraction(excise, arrowHeadRegion).Should().BeGreaterThan(0.1,
            "excise must draw the arrowhead wing above the shaft");
        InkFraction(reference!, arrowHeadRegion).Should().BeGreaterThan(0.1,
            "mutool must draw the arrowhead wing above the shaft from the /AP stream");

        var emptyArea = Box(400, 300, 560, 500);
        InkFraction(excise, emptyArea).Should().Be(0, "excise: no ink outside the line/arrow");
        InkFraction(reference!, emptyArea).Should().Be(0, "mutool: no ink outside the line/arrow");
    }

    private void AssertLineArrowPixels(SKBitmap bmp, string tool)
    {
        var lineBand = Box(105, 698, 295, 702);
        var arrowShaftBand = Box(105, 598, 295, 602);
        var arrowHeadRegion = Box(285, 602, 299, 608);
        var lineEndNoHeadRegion = Box(285, 704, 299, 710);
        var emptyArea = Box(400, 300, 560, 500);

        InkFraction(bmp, lineBand).Should().BeGreaterThan(0.2,
            $"{tool} must stroke the plain Line");
        InkFraction(bmp, arrowShaftBand).Should().BeGreaterThan(0.2,
            $"{tool} must stroke the Arrow's shaft");
        InkFraction(bmp, arrowHeadRegion).Should().BeGreaterThan(0.1,
            $"{tool} must draw the Arrow's ClosedArrow head above its shaft");
        InkFraction(bmp, lineEndNoHeadRegion).Should().Be(0,
            $"{tool}: the plain Line has /LE [None None] — no arrowhead ink near its end");
        InkFraction(bmp, emptyArea).Should().Be(0, $"{tool}: no ink outside the line/arrow annotations");
    }

    /// <summary>
    /// One blank page carrying a plain Line (100,700)-(300,700) and an Arrow
    /// (100,600)-(300,600) with a ClosedArrow head at its end, both red, 3pt.
    /// Saved, reopened from bytes, and written to a temp file.
    /// </summary>
    private string SaveLineArrowPdf()
    {
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.AddLineAnnotation(1, 100, 700, 300, 700, red: 1, green: 0, blue: 0, lineWidth: 3);
            doc.AddArrowAnnotation(1, 100, 600, 300, 600, red: 1, green: 0, blue: 0, lineWidth: 3,
                endLineEnding: "ClosedArrow");
            saved = doc.SaveToBytes();
        }

        var path = Path.Combine(Path.GetTempPath(), $"excise-annot-line-arrow-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, saved);
        _temp.Add(path);
        return path;
    }

    // ── Polygon / PolyLine (§12.5.6.9) ────────────────────────────────────────

    [Fact]
    public void AuthoredPolygonAndPolyLine_IsDrawnByMutool_WithPolygonFilledAndPolyLineOpen()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = SavePolyPdf();
        using var rendered = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull("mutool must be able to open and render the authored file");

        AssertPolyPixels(rendered!, "mutool");
    }

    [Fact]
    public void AuthoredPolygonAndPolyLine_IsDrawnByPdftocairo_WithPolygonFilledAndPolyLineOpen()
    {
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        var path = SavePolyPdf();
        using var rendered = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull("pdftocairo must be able to open and render the authored file");

        AssertPolyPixels(rendered!, "pdftocairo");
    }

    [Fact]
    public void ExciseAndReferenceRenderer_AgreeOnAuthoredPolygonFill()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = SavePolyPdf();
        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        using var reopened = PdfDocument.Open(path);
        using var excise = new SkiaRenderer().RenderPage(
            reopened.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

        var polygonInterior = Box(180, 140, 220, 160);
        var exciseFill = GreenFraction(excise, polygonInterior);
        var referenceFill = GreenFraction(reference!, polygonInterior);
        exciseFill.Should().BeGreaterThan(0.9, "excise must fill the authored polygon interior");
        referenceFill.Should().BeGreaterThan(0.9, "mutool must fill the authored polygon interior");

        var emptyArea = Box(460, 90, 490, 190);
        InkFraction(excise, emptyArea).Should().Be(0, "excise: no ink outside the polygon/polyline");
        InkFraction(reference!, emptyArea).Should().Be(0, "mutool: no ink outside the polygon/polyline");
    }

    private void AssertPolyPixels(SKBitmap bmp, string tool)
    {
        var polygonInterior = Box(180, 140, 220, 160);
        var polygonOutside = Box(60, 60, 90, 90);
        var polylineVertex = Box(395, 175, 405, 185);
        var polylineClosingGap = Box(395, 99, 405, 101);
        var emptyArea = Box(460, 90, 490, 190);

        GreenFraction(bmp, polygonInterior).Should().BeGreaterThan(0.9,
            $"{tool} must fill the authored Polygon's interior with its /IC color");
        InkFraction(bmp, polygonOutside).Should().Be(0,
            $"{tool}: the polygon fill must not spill outside its border");

        InkFraction(bmp, polylineVertex).Should().BeGreaterThan(0.1,
            $"{tool} must stroke the PolyLine through its middle vertex");
        InkFraction(bmp, polylineClosingGap).Should().Be(0,
            $"{tool}: a PolyLine must stay open — no stroke closing its first and last vertices");

        InkFraction(bmp, emptyArea).Should().Be(0, $"{tool}: no ink outside the polygon/polyline annotations");
    }

    /// <summary>
    /// One blank page carrying a filled-green Polygon triangle
    /// (100,100)-(300,100)-(200,250) and an open red PolyLine zig-zag
    /// (350,100)-(400,180)-(450,100). Saved, reopened from bytes, and
    /// written to a temp file.
    /// </summary>
    private string SavePolyPdf()
    {
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.AddPolygonAnnotation(
                1, new (double X, double Y)[] { (100, 100), (300, 100), (200, 250) },
                red: 0, green: 0, blue: 0, borderWidth: 2,
                interiorRed: 0, interiorGreen: 1, interiorBlue: 0);
            doc.AddPolyLineAnnotation(
                1, new (double X, double Y)[] { (350, 100), (400, 180), (450, 100) },
                red: 1, green: 0, blue: 0, borderWidth: 2);
            saved = doc.SaveToBytes();
        }

        var path = Path.Combine(Path.GetTempPath(), $"excise-annot-poly-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, saved);
        _temp.Add(path);
        return path;
    }

    // ── Stamp (§12.5.6.12) ─────────────────────────────────────────────────────

    [Fact]
    public void AuthoredStamps_AreDrawnByMutool_StandardNameAndImage()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = SaveStampsPdf();
        using var rendered = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull("mutool must be able to open and render the authored file");

        AssertStampPixels(rendered!, "mutool");
    }

    [Fact]
    public void AuthoredStamps_AreDrawnByPdftocairo_StandardNameAndImage()
    {
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        var path = SaveStampsPdf();
        using var rendered = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull("pdftocairo must be able to open and render the authored file");

        AssertStampPixels(rendered!, "pdftocairo");
    }

    [Fact]
    public void ExciseAndReferenceRenderer_AgreeOnAuthoredStampInk()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = SaveStampsPdf();
        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        using var reopened = PdfDocument.Open(path);
        using var excise = new SkiaRenderer().RenderPage(
            reopened.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

        // The image stamp is a full-bleed solid-blue 4x4 image stretched over
        // its /Rect — both renderers must show near-pure blue there.
        var imageStampInterior = Box(354, 704, 446, 746);
        BlueFraction(excise, imageStampInterior).Should().BeGreaterThan(0.9,
            "excise must draw the embedded image XObject full-bleed");
        BlueFraction(reference!, imageStampInterior).Should().BeGreaterThan(0.9,
            "mutool must draw the embedded image XObject from the /AP stream");

        var emptyArea = Box(460, 700, 560, 760);
        InkFraction(excise, emptyArea).Should().Be(0, "excise: no ink outside the stamps");
        InkFraction(reference!, emptyArea).Should().Be(0, "mutool: no ink outside the stamps");
    }

    private void AssertStampPixels(SKBitmap bmp, string tool)
    {
        var standardBorder = Box(100, 701, 104, 749);
        var standardText = Box(110, 712, 290, 726);
        var imageStampInterior = Box(354, 704, 446, 746);
        var gapBetweenStamps = Box(302, 700, 348, 750);
        var emptyArea = Box(460, 700, 560, 760);

        InkFraction(bmp, standardBorder).Should().BeGreaterThan(0.3,
            $"{tool} must stroke the standard-name Stamp's border");
        InkFraction(bmp, standardText).Should().BeGreaterThan(0.02,
            $"{tool} must draw the standard-name Stamp's label text");

        BlueFraction(bmp, imageStampInterior).Should().BeGreaterThan(0.9,
            $"{tool} must draw the embedded image XObject full-bleed from the /AP stream");

        InkFraction(bmp, gapBetweenStamps).Should().Be(0,
            $"{tool}: no ink in the gap between the two stamps");
        InkFraction(bmp, emptyArea).Should().Be(0, $"{tool}: no ink outside the stamp annotations");
    }

    /// <summary>
    /// One blank page carrying a standard-name Stamp ("Approved",
    /// 200x50 @ (100,700)) and a custom image Stamp (a solid-blue 4x4 RGB24
    /// image, 100x50 @ (350,700)). Saved, reopened from bytes, and written to
    /// a temp file.
    /// </summary>
    private string SaveStampsPdf()
    {
        byte[] saved;
        using (var doc = PdfDocument.CreateNew())
        {
            doc.Pages.AddBlank();
            doc.AddStampAnnotation(1, new PdfRectangle(100, 700, 300, 750), "Approved");

            var pixels = new byte[4 * 4 * 3];
            for (int i = 0; i < pixels.Length; i += 3)
            {
                pixels[i] = 0; pixels[i + 1] = 0; pixels[i + 2] = 255;
            }
            doc.AddImageStampAnnotation(1, new PdfRectangle(350, 700, 450, 750), pixels, 4, 4);

            saved = doc.SaveToBytes();
        }

        var path = Path.Combine(Path.GetTempPath(), $"excise-annot-stamp-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, saved);
        _temp.Add(path);
        return path;
    }

    // ── Shared pixel helpers (mirrors AnnotationAuthoringDifferentialTests) ──

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

    private static double GreenFraction(SKBitmap bmp, SKRectI box) =>
        Fraction(bmp, box, p => p.Green > 180 && p.Red < 100 && p.Blue < 100);

    private static double BlueFraction(SKBitmap bmp, SKRectI box) =>
        Fraction(bmp, box, p => p.Blue > 180 && p.Red < 100 && p.Green < 100);

    public void Dispose()
    {
        foreach (var p in _temp)
        {
            try { File.Delete(p); } catch { /* best effort */ }
        }
    }
}
