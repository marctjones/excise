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
/// #1908 render-back: a JPEG and RGB pixels with alpha placed with <see cref="PdfGraphics.DrawImage"/>
/// and saved land on their box, in <see cref="SkiaRenderer"/> and in MuPDF, and the pixels'
/// transparent half stays unpainted. The JPEG is encoded by SkiaSharp, not by Excise.
/// </summary>
public class ImageAuthoringRenderTests
{
    // 72 dpi: one device pixel per point. The page is square, so device y = Size - pdf y.
    private const int Dpi = 72;
    private const int Size = 300;

    // The box: pdf x 50..170, y 60..140, so device x 50..170, y 160..240.
    private const double X = 50, Y = 60, W = 120, H = 80;

    private static byte[] Encoded(SKEncodedImageFormat format, Func<int, int, SKColor> pixel)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(40, 20, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 40; x++)
                bitmap.SetPixel(x, y, pixel(x, y));
        using var data = bitmap.Encode(format, 100);
        return data.ToArray();
    }

    private static PdfImage Pixels(Func<int, int, SKColor> pixel)
    {
        var rgb = new byte[40 * 20 * 3];
        var alpha = new byte[40 * 20];
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 40; x++)
            {
                var c = pixel(x, y);
                int i = y * 40 + x;
                (rgb[3 * i], rgb[3 * i + 1], rgb[3 * i + 2], alpha[i]) = (c.Red, c.Green, c.Blue, c.Alpha);
            }
        return PdfImage.FromRgb(40, 20, rgb, alpha);
    }

    private static byte[] Authored(PdfImage image)
    {
        var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(Size, Size);
        using (var g = page.GetGraphics())
            g.DrawImage(image, X, Y, W, H);
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
        var path = Path.Combine(Path.GetTempPath(), $"excise-image-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try { return MutoolReferenceRenderer.RenderPage(path, 1, Dpi); }
        finally { File.Delete(path); }
    }

    private static (int MinX, int MinY, int MaxX, int MaxY) InkBox(SKBitmap bmp)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red > 200 && c.Green > 200 && c.Blue > 200)
                    continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        return (minX, minY, maxX, maxY);
    }

    private static void AssertBox((int MinX, int MinY, int MaxX, int MaxY) box, int right, string renderer)
    {
        box.MinX.Should().BeInRange(49, 51, renderer);
        box.MaxX.Should().BeInRange(right - 2, right, renderer);
        box.MinY.Should().BeInRange(159, 161, renderer);
        box.MaxY.Should().BeInRange(238, 240, renderer);
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
    public void Jpeg_FillsItsBox()
    {
        var pdf = Authored(PdfImage.FromJpeg(Encoded(SKEncodedImageFormat.Jpeg, (_, _) => new SKColor(200, 30, 30))));

        ForBothRenderers(pdf, (bmp, renderer) =>
        {
            AssertBox(InkBox(bmp), 169, renderer);
            var centre = bmp.GetPixel(110, 200);
            ((int)centre.Red).Should().BeInRange(185, 215, renderer);
            ((int)centre.Green).Should().BeLessThan(60, renderer);
        });
    }

    [Fact]
    public void TransparentPixels_PaintOnlyTheirOpaqueHalf()
    {
        // Left half opaque blue, right half fully transparent: without the soft mask the whole box would ink.
        var pdf = Authored(Pixels((x, _) => x < 20 ? new SKColor(20, 40, 220, 255) : new SKColor(0, 0, 0, 0)));

        ForBothRenderers(pdf, (bmp, renderer) =>
        {
            // Half of 120 pt: device x 50..110.
            AssertBox(InkBox(bmp), 109, renderer);
            var opaque = bmp.GetPixel(80, 200);
            ((int)opaque.Blue).Should().BeGreaterThan(200, renderer);
            ((int)opaque.Red).Should().BeLessThan(60, renderer);
        });
    }
}
