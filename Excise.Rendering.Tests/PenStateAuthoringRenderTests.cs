using System;
using System.Collections.Generic;
using System.IO;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// #1851 render-back: a dashed, translucent pen authored through <see cref="PdfGraphics"/>
/// paints what its operators say, in <see cref="SkiaRenderer"/> and in MuPDF (so the
/// authoring side is not checked only by the renderer written alongside it).
/// </summary>
public class PenStateAuthoringRenderTests
{
    // 72 dpi: one device pixel per point, so user-space dash lengths are pixel lengths.
    private const int Dpi = 72;
    private const double PageHeight = 60;
    private const double LineY = 30;

    private static byte[] AuthoredLine(PdfPen pen)
    {
        var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(300, PageHeight);
        using (var g = page.GetGraphics())
            g.DrawLine(20, LineY, 280, LineY, pen);
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
        var path = Path.Combine(Path.GetTempPath(), $"excise-pen-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try { return MutoolReferenceRenderer.RenderPage(path, 1, Dpi); }
        finally { File.Delete(path); }
    }

    /// <summary>Lengths of alternating ink/gap runs along the line's centre row, ink first.</summary>
    private static List<int> RunsAlongLine(SKBitmap bmp)
    {
        int row = (int)(PageHeight - LineY);
        var runs = new List<int>();
        bool? inInk = null;
        for (int x = 15; x < 285; x++)
        {
            bool ink = bmp.GetPixel(x, row).Red < 128;
            if (inInk == null && !ink) continue;
            if (ink == inInk) runs[^1]++;
            else { runs.Add(1); inInk = ink; }
        }
        return runs;
    }

    private static void AssertDashPitch(SKBitmap bmp, string renderer)
    {
        var runs = RunsAlongLine(bmp);
        // 260 pt of [10 10] from phase 0: 13 dashes and 12 gaps between them, then the trailing background.
        runs.Count.Should().BeInRange(25, 26, $"{renderer} should paint 13 dashes");
        for (int i = 0; i < 25; i++)
            runs[i].Should().BeInRange(9, 11, $"{renderer} run {i} of {string.Join(',', runs)} should be 10 pt");
    }

    [Fact]
    public void DashedPen_RendersGapsAtTheDashPitch()
    {
        var pdf = AuthoredLine(new PdfPen(PdfColor.Black, 4) { DashArray = [10, 10] });

        using (var skia = RenderWithSkia(pdf))
            AssertDashPitch(skia, "SkiaRenderer");

        Assert.SkipWhen(!MutoolReferenceRenderer.IsAvailable, "mutool is not installed.");
        using var mutool = RenderWithMutool(pdf);
        mutool.Should().NotBeNull();
        AssertDashPitch(mutool!, "mutool");
    }

    [Fact]
    public void TranslucentPen_RendersAtItsOpacity()
    {
        var pdf = AuthoredLine(new PdfPen(PdfColor.Black, 6) { Opacity = 0.35 });
        // Black at 0.35 over white: 255 * (1 - 0.35) ≈ 166.
        const int expected = 166;

        using (var skia = RenderWithSkia(pdf))
            ((int)skia.GetPixel(150, (int)(PageHeight - LineY)).Red).Should().BeInRange(expected - 8, expected + 8);

        Assert.SkipWhen(!MutoolReferenceRenderer.IsAvailable, "mutool is not installed.");
        using var mutool = RenderWithMutool(pdf);
        mutool.Should().NotBeNull();
        ((int)mutool!.GetPixel(150, (int)(PageHeight - LineY)).Red).Should().BeInRange(expected - 8, expected + 8);
    }
}
