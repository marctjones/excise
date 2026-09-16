using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using Excise.Rendering.Fonts;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1363 on the page that filed it: pdf.js <c>freetext_no_appearance.pdf</c>.
/// Its only content is one FreeText with no <c>/AP</c>, <c>/Border [0 0 0]</c>,
/// no <c>/C</c>, <c>/DA (/Helv 10 Tf 0 g)</c> and 14 lines of Arabic in a
/// UTF-16BE <c>/Contents</c>.
///
/// <para>Measured 2026-09-06 at 150 dpi (inked px): mutool 20,152 clipped to
/// <c>/Rect</c>; Ghostscript 21,318, overflowing <c>/Rect</c> by 133 px;
/// pdfium 4,926 inside <c>/Rect</c>; poppler 63; excise 0. With poppler
/// counted once, 3 of 4 engines draw.</para>
///
/// <para>The bar is ink IN A REGION, never pixels. mutool bundles Noto Naskh
/// Arabic and excise uses whatever Arabic face the system has, so glyph shapes
/// legitimately differ. Two things must hold regardless: the text is drawn,
/// across many lines and at a comparable amount of ink, and none of it escapes
/// <c>/Rect</c>, which is Ghostscript's behaviour and not the model.</para>
/// </summary>
public class FreeTextComplexScriptDifferentialTests
{
    private const int Dpi = 150;
    private const string FixtureName = "freetext_no_appearance.pdf";

    /// <summary>612 x 792 pt at 150 dpi.</summary>
    private const int PageHeightPx = 1650;

    /// <summary>
    /// Excise's ink inside <c>/Rect</c>, as a fraction of mutool's. Wide on
    /// purpose, because the two engines use different Arabic faces. The floor
    /// is what separates "draws the block" from "draws a line or two", and the
    /// ceiling is what separates it from tofu boxes or fill.
    /// </summary>
    private const double MinInkRatio = 0.4;
    private const double MaxInkRatio = 2.5;

    /// <summary><c>/Rect [140 394.12 364.4 560.13]</c> in device pixels (y down).</summary>
    private static readonly SKRectI AnnotRect = ToDevice(140, 394.12, 364.4, 560.13);

    [Fact]
    public void ExciseDrawsTheArabicContents_InsideRect_WithinABandOfMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not on PATH.");
        var path = RequireFixture();
        RequireArabicSystemFont();

        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();
        reference!.Height.Should().Be(PageHeightPx, "the device rect below assumes the fixture's 612 x 792 page");
        var mutoolInside = InkedPixels(reference, Expand(AnnotRect, 2));
        mutoolInside.Should().BeGreaterThan(5_000,
            "mutool draws the Arabic block. If it does not, this fixture no longer tests #1363");

        using var excise = RenderWithExcise(path);
        excise.Height.Should().Be(PageHeightPx);
        var inside = InkedPixels(excise, Expand(AnnotRect, 2));
        var outside = InkedPixels(excise) - inside;

        outside.Should().Be(0,
            "the synthesised appearance is clipped to /Rect. Ghostscript overflows it by 133 px, " +
            "and that is the behaviour not to copy");
        inside.Should().BeInRange((int)(mutoolInside * MinInkRatio), (int)(mutoolInside * MaxInkRatio),
            $"excise must draw the Arabic block at an amount of ink comparable to mutool's " +
            $"{mutoolInside} px, not a single line and not boxes");
    }

    [Fact]
    public void ExciseDrawsEveryLine_NotJustTheFirst()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not on PATH.");
        var path = RequireFixture();
        RequireArabicSystemFont();

        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();
        using var excise = RenderWithExcise(path);

        var mutoolBounds = InkBounds(reference!, AnnotRect);
        var exciseBounds = InkBounds(excise, AnnotRect);
        mutoolBounds.Should().NotBeNull();
        exciseBounds.Should().NotBeNull("excise must draw the block");

        // One 10 pt line is about 21 px at 150 dpi, so an ink box more than
        // three lines tall means many lines were drawn. Before #1363 only the
        // first line was ever considered.
        int oneLinePx = 10 * Dpi / 72;
        exciseBounds!.Value.Height.Should().BeGreaterThan(3 * oneLinePx,
            "the /Contents has 14 lines in mutool's layout");
        exciseBounds.Value.Height.Should().BeGreaterThanOrEqualTo(mutoolBounds!.Value.Height / 2,
            $"mutool's block is {mutoolBounds.Value.Height} px tall; excise's must be of the same order");
    }

    [Fact]
    public void Pdfium_AlsoDrawsTheContentsInsideRect()
    {
        Assert.SkipUnless(PdfiumNativeReferenceRenderer.IsAvailable,
            "pdfium oracle not provisioned (scripts/download-pdfium.sh).");
        var path = RequireFixture();

        using var pdfium = PdfiumNativeReferenceRenderer.RenderPage(
            path, 1, Dpi, userPassword: null, renderAnnotations: true);
        pdfium.Should().NotBeNull();
        var inside = InkedPixels(pdfium!, Expand(AnnotRect, 2));
        inside.Should().BeGreaterThan(0,
            "pdfium's vote (4,926 px inside /Rect) is part of the majority that makes drawing correct");
        (InkedPixels(pdfium!) - inside).Should().Be(0, "pdfium clips to /Rect as mutool does");

        RequireArabicSystemFont();
        using var excise = RenderWithExcise(path);
        InkedPixels(excise, Expand(AnnotRect, 2)).Should().BeGreaterThan(0,
            "two independent engines draw this annotation's text, so excise must too");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static SKBitmap RenderWithExcise(string path)
    {
        using var doc = PdfDocument.Open(path);
        return new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White });
    }

    private static string RequireFixture()
    {
        var path = FindPdfjsFixture();
        Assert.SkipWhen(path == null,
            $"gitignored pdf.js corpus fixture {FixtureName} not present (scripts/download-pdfjs-corpus.sh).");

        using var doc = PdfDocument.Open(path!);
        var annot = doc.GetPage(1).GetAnnotations()
            .Single(a => a.Subtype == PdfAnnotationSubtype.FreeText);
        annot.RawDictionary.GetOptional("AP").Should().BeNull("the fixture is chosen for having no /AP");
        Math.Min(annot.Rect.Left, annot.Rect.Right).Should().BeApproximately(140, 0.01);
        Math.Max(annot.Rect.Bottom, annot.Rect.Top).Should().BeApproximately(560.13, 0.01);
        return path!;
    }

    private static string? FindPdfjsFixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "excise.sln")))
            dir = dir.Parent;
        if (dir == null) return null;

        var corpus = Path.Combine(dir.FullName, "test-pdfs", "pdfjs");
        if (!Directory.Exists(corpus)) return null;
        return Directory.EnumerateFiles(corpus, FixtureName, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static void RequireArabicSystemFont()
    {
        bool covered;
        lock (FontManagerLock.Instance)
        {
            covered = SKFontManager.Default.MatchCharacter(0x0627) != null;
        }

        Assert.SkipWhen(!covered, "No system font covers Arabic (U+0627), so excise has nothing to shape with.");
    }

    private static SKRectI ToDevice(double left, double bottom, double right, double top)
    {
        double s = Dpi / 72.0;
        return new SKRectI(
            (int)Math.Floor(left * s), (int)Math.Floor((792 - top) * s),
            (int)Math.Ceiling(right * s), (int)Math.Ceiling((792 - bottom) * s));
    }

    private static SKRectI Expand(SKRectI r, int by) =>
        new(r.Left - by, r.Top - by, r.Right + by, r.Bottom + by);

    private static bool IsInk(SKColor c) => c.Red < 240 || c.Green < 240 || c.Blue < 240;

    private static int InkedPixels(SKBitmap bmp) => InkedPixels(bmp, new SKRectI(0, 0, bmp.Width, bmp.Height));

    private static int InkedPixels(SKBitmap bmp, SKRectI box)
    {
        int n = 0;
        for (int y = Math.Max(0, box.Top); y < Math.Min(bmp.Height, box.Bottom); y++)
            for (int x = Math.Max(0, box.Left); x < Math.Min(bmp.Width, box.Right); x++)
                if (IsInk(bmp.GetPixel(x, y))) n++;
        return n;
    }

    private static SKRectI? InkBounds(SKBitmap bmp, SKRectI box)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = Math.Max(0, box.Top); y < Math.Min(bmp.Height, box.Bottom); y++)
            for (int x = Math.Max(0, box.Left); x < Math.Min(bmp.Width, box.Right); x++)
                if (IsInk(bmp.GetPixel(x, y)))
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
        return maxX < 0 ? null : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }
}
