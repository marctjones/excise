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
/// #1910 render-back: circles and arcs authored through <see cref="PdfGraphics"/> land where
/// the analytic shape is, in <see cref="SkiaRenderer"/> and in MuPDF.
/// </summary>
public class EllipseAuthoringRenderTests
{
    // 72 dpi: one device pixel per point. The page is square, so device y = Size - pdf y.
    private const int Dpi = 72;
    private const int Size = 300;

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
        var path = Path.Combine(Path.GetTempPath(), $"excise-ellipse-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try { return MutoolReferenceRenderer.RenderPage(path, 1, Dpi); }
        finally { File.Delete(path); }
    }

    private static bool Ink(SKBitmap bmp, int x, int y) => bmp.GetPixel(x, y).Red < 128;

    private static void AssertFilledCircle(SKBitmap bmp, string renderer)
    {
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1, area = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (Ink(bmp, x, y))
                {
                    area++;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }

        // Centre (150,150), r = 100: ink covers device pixels 50..249 on both axes.
        minX.Should().BeInRange(49, 51, renderer);
        maxX.Should().BeInRange(248, 250, renderer);
        minY.Should().BeInRange(49, 51, renderer);
        maxY.Should().BeInRange(248, 250, renderer);
        area.Should().BeInRange((int)(Math.PI * 100 * 100 * 0.99), (int)(Math.PI * 100 * 100 * 1.01), renderer);
    }

    [Fact]
    public void FilledCircle_InkMatchesTheAnalyticCircle()
    {
        var pdf = Authored(g => g.DrawCircle(150, 150, 100, PdfBrush.Black, null));

        using (var skia = RenderWithSkia(pdf))
            AssertFilledCircle(skia, "SkiaRenderer");

        Assert.SkipWhen(!MutoolReferenceRenderer.IsAvailable, "mutool is not installed.");
        using var mutool = RenderWithMutool(pdf);
        mutool.Should().NotBeNull();
        AssertFilledCircle(mutool!, "mutool");
    }

    private static void AssertOnlyUpperRightQuarter(SKBitmap bmp, string renderer)
    {
        int upperRight = 0, elsewhere = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                // Skip a band along the axes, where the pen's butt ends sit.
                if (!Ink(bmp, x, y) || Math.Abs(x - 150) < 4 || Math.Abs(y - 150) < 4)
                    continue;
                if (x > 150 && y < 150) upperRight++;
                else elsewhere++;
            }

        upperRight.Should().BeGreaterThan(100, $"{renderer}: 0 to 90 degrees is the upper-right quarter");
        elsewhere.Should().Be(0, renderer);
    }

    [Fact]
    public void Arc_ZeroToNinety_PaintsTheUpperRightQuarter()
    {
        var pdf = Authored(g => g.DrawArc(50, 50, 200, 200, 0, 90, new PdfPen(PdfColor.Black, 3)));

        using (var skia = RenderWithSkia(pdf))
            AssertOnlyUpperRightQuarter(skia, "SkiaRenderer");

        Assert.SkipWhen(!MutoolReferenceRenderer.IsAvailable, "mutool is not installed.");
        using var mutool = RenderWithMutool(pdf);
        mutool.Should().NotBeNull();
        AssertOnlyUpperRightQuarter(mutool!, "mutool");
    }
}
