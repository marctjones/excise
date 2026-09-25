using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using System.Text;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// A transparent PNG placed as an image stamp must show the PAGE through its transparent pixels. Read back
/// by mutool (not excise) at 72 dpi over a coloured page, and also by excise's own renderer so the viewer
/// cannot draw a box where every other reader draws none.
/// </summary>
public class ImageStampTransparencyTests : IDisposable
{
    private const int Size = 200;
    private static readonly SKColor Page = new(204, 229, 153);   // light green
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var f in _temp) { try { File.Delete(f); } catch (IOException) { } }
    }

    /// <summary>
    /// 8x8 picture in a 100x100 box at (50,50): a black ring of transparent pixels around a solid red
    /// 6x6 core, and a semi-transparent blue pixel in the ring's top-left.
    /// </summary>
    private string Build(bool withAlpha)
    {
        const int n = 8;
        var rgb = new byte[n * n * 3];
        var alpha = new byte[n * n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var i = y * n + x;
                var core = x is >= 1 and <= 6 && y is >= 1 and <= 6;
                if (core) { rgb[i * 3] = 255; alpha[i] = 255; }          // red, opaque
                else { alpha[i] = 0; }                                    // black RGB, fully transparent
            }
        }
        // Semi-transparent blue at the top-left cell.
        rgb[0] = 0; rgb[1] = 0; rgb[2] = 255; alpha[0] = 128;

        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(Size, Size);
        page.SetContentStreamBytes(Encoding.ASCII.GetBytes("0.8 0.898 0.6 rg 0 0 200 200 re f\n"));
        doc.AddImageStampAnnotation(1, new PdfRectangle(50, 50, 150, 150), rgb, n, n, alphaPixels: withAlpha ? alpha : null);
        var path = Path.Combine(Path.GetTempPath(), $"excise-stamp-alpha-{Guid.NewGuid():N}.pdf");
        doc.Save(path);
        _temp.Add(path);
        return path;
    }

    // Cell (cx,cy) of the 8x8 picture, at its centre, as a 72-dpi pixel (PDF y is up, pixel y is down).
    private static (int X, int Y) Cell(int cx, int cy) => (50 + (int)((cx + 0.5) * 12.5), Size - 150 + (int)((cy + 0.5) * 12.5));

    private static void AssertClose(SKColor actual, SKColor expected, int tolerance, string what)
    {
        Math.Abs(actual.Red - expected.Red).Should().BeLessThanOrEqualTo(tolerance, $"{what}: red {actual}");
        Math.Abs(actual.Green - expected.Green).Should().BeLessThanOrEqualTo(tolerance, $"{what}: green {actual}");
        Math.Abs(actual.Blue - expected.Blue).Should().BeLessThanOrEqualTo(tolerance, $"{what}: blue {actual}");
    }

    private static void AssertTransparencyShowsThePage(SKBitmap bmp, string reader)
    {
        var (rx, ry) = Cell(3, 3);
        AssertClose(bmp.GetPixel(rx, ry), new SKColor(255, 0, 0), 6, $"{reader}: the opaque core is red");

        var (bx, by) = Cell(4, 0);   // a transparent ring cell, RGB black under it
        AssertClose(bmp.GetPixel(bx, by), Page, 6, $"{reader}: a transparent pixel shows the page, not a black box");

        var (hx, hy) = Cell(0, 0);   // blue at 128/255 over the page
        var expected = new SKColor(
            (byte)Math.Round(204 * (127 / 255.0)),
            (byte)Math.Round(229 * (127 / 255.0)),
            (byte)Math.Round(255 * (128 / 255.0) + 153 * (127 / 255.0)));
        AssertClose(bmp.GetPixel(hx, hy), expected, 8, $"{reader}: half-transparent blue blends with the page");
    }

    [Fact]
    public void Mutool_ShowsThePageThroughTransparentPixels()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        using var bmp = MutoolReferenceRenderer.RenderPage(Build(withAlpha: true), 1, 72);
        bmp.Should().NotBeNull();
        AssertTransparencyShowsThePage(bmp!, "mutool");
    }

    [Fact]
    public void Excise_ShowsThePageThroughTransparentPixels()
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(Build(withAlpha: true)));
        using var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1), new RenderOptions { Dpi = 72 });
        AssertTransparencyShowsThePage(bmp, "excise");
    }

    [Fact]
    public void WithoutTheSMask_TheTransparentPixelsAreABlackBox_SoTheTestCanFail()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        using var bmp = MutoolReferenceRenderer.RenderPage(Build(withAlpha: false), 1, 72);
        var (bx, by) = Cell(4, 0);
        var c = bmp!.GetPixel(bx, by);
        (c.Red < 40 && c.Green < 40 && c.Blue < 40).Should().BeTrue(
            $"without a soft mask the ring's black RGB is drawn as a box (got {c}); this is the defect the mask removes");
    }
}
