using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// #890 — the page group must composite against a TRANSPARENT initial backdrop
/// with the paper colour applied last (§11.4.7).
///
/// excise cleared the bitmap to the background colour first, so every blend
/// mode composited against opaque white. Against Cb = 1 the separable blend
/// functions collapse — B(1, Cs) = 1 for Screen, ColorDodge, Lighten,
/// SoftLight, Overlay, ColorBurn, Hue, Saturation, Color and half of HardLight.
/// NINE OF SIXTEEN modes drew nothing at all, with no error and no diagnostic,
/// because the composite genuinely evaluated to white.
///
/// One corpus page witnessed it (pdfium bug_1302355.pdf, /BM /SoftLight on a
/// form with no /Group — mislabelled an SMask bug), but the mechanism is
/// general: any document using a non-Normal blend mode against the page
/// backdrop was affected, and blend modes are ordinary in design-tool output.
/// The page count understated it badly, which is why this file tests the
/// MODES rather than the page.
/// </summary>
public class PageGroupBackdropTests
{
    private const int Dpi = 72;
    private const int PageSize = 100;

    /// <summary>
    /// The modes that collapse to white against an opaque-white backdrop. Each
    /// must now leave visible ink.
    /// </summary>
    [Theory]
    [InlineData("Screen")]
    [InlineData("ColorDodge")]
    [InlineData("Lighten")]
    [InlineData("SoftLight")]
    [InlineData("Overlay")]
    [InlineData("ColorBurn")]
    [InlineData("Hue")]
    [InlineData("Saturation")]
    [InlineData("Color")]
    public void BlendModesThatCollapseAgainstWhite_NowDrawInk(string blendMode)
    {
        var path = WriteTemp(BlendPdf(blendMode));
        try
        {
            using var doc = PdfDocument.Open(path);
            using var bmp = Render(doc);

            NonWhitePixels(bmp).Should().BeGreaterThan(100,
                $"/BM /{blendMode} against an opaque-white backdrop evaluates to white for " +
                "every source colour — the page group must start transparent and take the " +
                "paper last, or this mode is silently erased");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The control. Normal compositing must be byte-identical, or the change
    /// has altered every page in every document rather than only the blended
    /// ones.
    /// </summary>
    [Fact]
    public void NormalBlend_IsUnaffected()
    {
        var path = WriteTemp(BlendPdf("Normal"));
        try
        {
            using var doc = PdfDocument.Open(path);
            using var bmp = Render(doc);

            NonWhitePixels(bmp).Should().BeGreaterThan(100, "a plain fill still draws");
            // The untouched margin must be exactly the page colour — proving the
            // paper is applied, not merely that something was drawn.
            bmp.GetPixel(2, 2).Should().Be(new SKColor(255, 255, 255, 255),
                "the paper colour must be fully opaque where nothing was drawn — " +
                "a transparent backdrop that is never filled would leave alpha 0");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The paper has to be OPAQUE everywhere, including under drawn content.
    /// A caller writing the bitmap to a format without alpha would otherwise
    /// get a page that renders correctly on screen and wrong on export.
    /// </summary>
    [Fact]
    public void EveryPixel_IsFullyOpaqueAfterRendering()
    {
        var path = WriteTemp(BlendPdf("Multiply"));
        try
        {
            using var doc = PdfDocument.Open(path);
            using var bmp = Render(doc);

            for (int y = 0; y < bmp.Height; y += 7)
                for (int x = 0; x < bmp.Width; x += 7)
                    bmp.GetPixel(x, y).Alpha.Should().Be(255,
                        $"pixel ({x},{y}) must be opaque — the paper is applied under the " +
                        "whole device bitmap, not only where content happens to be absent");
        }
        finally { File.Delete(path); }
    }

    // ── fixture ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A mid-grey filled square under the given blend mode. Grey rather than a
    /// primary so that modes keying off luminosity (Hue/Saturation/Color) have
    /// something to act on.
    /// </summary>
    private static byte[] BlendPdf(string blendMode)
    {
        var content = "/GS1 gs 0.25 0.55 0.35 rg 20 20 60 60 re f";
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {PageSize} {PageSize}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R " +
            "/Resources << /ExtGState << /GS1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            $"5 0 obj\n<< /Type /ExtGState /BM /{blendMode} >>\nendobj\n",
        };

        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static SKBitmap Render(PdfDocument doc) =>
        new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

    private static int NonWhitePixels(SKBitmap bmp)
    {
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 250 || c.Green < 250 || c.Blue < 250) n++;
            }
        return n;
    }

    private static string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-890-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        return p;
    }
}
