using System;
using System.IO;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// #1909 render-back: a fill after <see cref="PdfGraphics.Clip"/> is confined to the clip
/// region, and <see cref="PdfGraphics.RestoreState"/> releases it, in <see cref="SkiaRenderer"/>
/// and in MuPDF.
/// </summary>
public class ClipAuthoringRenderTests
{
    // 72 dpi: one device pixel per point. The page is square, so device y = Size - pdf y.
    private const int Dpi = 72;
    private const int Size = 200;

    private static byte[] Authored(Action<PdfGraphics> draw)
    {
        var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(Size, Size);
        using (var g = page.GetGraphics())
            draw(g);
        return doc.SaveToBytes();
    }

    private static SKBitmap RenderWithSkia(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return new SkiaRenderer().RenderPage(
            doc.GetPage(1), new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White });
    }

    private static SKBitmap? RenderWithMutool(byte[] pdf)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-clip-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try { return MutoolReferenceRenderer.RenderPage(path, 1, Dpi); }
        finally { File.Delete(path); }
    }

    private static bool IsRed(SKColor c) => c.Red > 200 && c.Green < 80 && c.Blue < 80;
    private static bool IsBlue(SKColor c) => c.Blue > 200 && c.Red < 80 && c.Green < 80;

    /// <summary>Fraction of device pixels in [x0,x1)×[y0,y1) matching <paramref name="match"/>.</summary>
    private static double Fraction(SKBitmap bmp, Func<SKColor, bool> match, int x0, int y0, int x1, int y1,
        Func<int, int, bool>? exclude = null)
    {
        int hits = 0, total = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                if (exclude?.Invoke(x, y) == true) continue;
                total++;
                if (match(bmp.GetPixel(x, y))) hits++;
            }
        return (double)hits / total;
    }

    private static void ForBothRenderers(byte[] pdf, Action<SKBitmap, string> assert)
    {
        using (var skia = RenderWithSkia(pdf))
            assert(skia, "SkiaRenderer");

        Assert.SkipWhen(!MutoolReferenceRenderer.IsAvailable, "mutool is not installed.");
        using var mutool = RenderWithMutool(pdf);
        mutool.Should().NotBeNull();
        assert(mutool!, "mutool");
    }

    [Fact]
    public void FillInsideClip_IsConfinedToIt_AndRestoreStateReleasesIt()
    {
        var pdf = Authored(g =>
        {
            g.SaveState();
            g.ClipRectangle(50, 50, 100, 100);
            g.DrawRectangle(0, 0, Size, Size, PdfBrush.Red);
            g.RestoreState();
            g.DrawRectangle(0, 0, 40, 40, PdfBrush.Blue);
        });

        ForBothRenderers(pdf, (bmp, renderer) =>
        {
            // Clip box is device 50..150 on both axes; allow a pixel of antialiasing at its edge.
            Fraction(bmp, IsRed, 52, 52, 148, 148).Should().BeGreaterThan(0.999, $"{renderer}: inside the clip");
            Fraction(bmp, IsRed, 0, 0, Size, Size, (x, y) => x is >= 49 and < 151 && y is >= 49 and < 151)
                .Should().BeLessThan(0.001, $"{renderer}: no red outside the clip");
            // pdf (0,0,40,40) is device x 0..40, y 160..200, outside the old clip.
            Fraction(bmp, IsBlue, 1, 161, 39, 199).Should().BeGreaterThan(0.999, $"{renderer}: the clip ends at Q");
        });
    }

    [Fact]
    public void ClipEvenOdd_NestedSubpathCutsAHole()
    {
        // Two squares wound the same way: nonzero would fill the centre, even-odd leaves it out.
        var pdf = Authored(g =>
        {
            g.SaveState();
            g.MoveTo(20, 20); g.LineTo(180, 20); g.LineTo(180, 180); g.LineTo(20, 180); g.ClosePath();
            g.MoveTo(70, 70); g.LineTo(130, 70); g.LineTo(130, 130); g.LineTo(70, 130); g.ClosePath();
            g.ClipEvenOdd();
            g.DrawRectangle(0, 0, Size, Size, PdfBrush.Red);
            g.RestoreState();
        });

        ForBothRenderers(pdf, (bmp, renderer) =>
        {
            Fraction(bmp, IsRed, 72, 72, 128, 128).Should().BeLessThan(0.001, $"{renderer}: the hole");
            Fraction(bmp, IsRed, 22, 22, 68, 178).Should().BeGreaterThan(0.999, $"{renderer}: the ring");
            Fraction(bmp, IsRed, 0, 0, 18, Size).Should().BeLessThan(0.001, $"{renderer}: outside the ring");
        });
    }
}
