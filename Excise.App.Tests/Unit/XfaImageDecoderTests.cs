using AwesomeAssertions;
using Excise.App.Services;
using Excise.Core.Document;
using Excise.Core.Xfa;
using Excise.Rendering.Differential;
using Excise.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// #1575: the app's XFA layout draws PNG, BMP and GIF images through <see cref="XfaImageDecoder"/>,
/// which Core cannot decode itself. The saved page is rendered by mutool, not excise, and the
/// options come from <see cref="PdfDocumentService.XfaLayoutOptionsForOpen"/>, so the wiring
/// the app opens a form with is what is tested. Page coordinates: 144 dpi puts 2 px on a point.
/// </summary>
public class XfaImageDecoderTests : IDisposable
{
    private const int Dpi = 144;
    private const double Px = Dpi / 72.0;
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static XfaLayoutOptions AppOptions() =>
        new PdfDocumentService(NullLogger<PdfDocumentService>.Instance).XfaLayoutOptionsForOpen();

    private string LayOutAndSave(byte[] pdf, XfaLayoutOptions options, out XfaLayoutResult result)
    {
        using var document = PdfDocument.Open(pdf);
        result = document.ApplyXfaLayout(options, TestContext.Current.CancellationToken);
        result.Status.Should().Be(XfaLayoutStatus.LaidOut, result.FailureReason);
        var path = Path.Combine(Path.GetTempPath(), $"excise-xfa-image-{Guid.NewGuid():N}.pdf");
        document.Save(path);
        _temp.Add(path);
        return path;
    }

    private SKBitmap Render(string path, int page)
    {
        var bitmap = MutoolReferenceRenderer.RenderPage(path, page, Dpi);
        bitmap.Should().NotBeNull("mutool must render the laid-out page");
        return bitmap!;
    }

    /// <summary>A 1in x 1in draw at (1in, 1in) whose image, stretched (aspect none), fills it.</summary>
    private static string ImageDraw(string contentType, byte[] image) =>
        "<draw name=\"Logo\" x=\"1in\" y=\"1in\" w=\"1in\" h=\"1in\"><value>"
        + $"<image aspect=\"none\" contentType=\"{contentType}\">{Convert.ToBase64String(image)}</image></value></draw>";

    private static byte[] RedPng()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A 4 x 3 GIF whose every pixel is palette entry 1, red (made by macOS sips).</summary>
    private static byte[] RedGif() =>
        Convert.FromBase64String("R0lGODdhBAADAJEAAAAAAP8AAP///wAAACH5BAQAAAAALAAAAAAEAAMAAAIDjI9WADs=");

    /// <summary>An 8 x 8 1-bit BMP (palette black, white), all black: the shape of imm5257e's wordmark.</summary>
    private static byte[] BlackOneBitBmp()
    {
        const int w = 8, h = 8, rowBytes = 4;
        var bmp = new byte[14 + 40 + 8 + rowBytes * h];
        void U16(int at, int v) => BitConverter.TryWriteBytes(bmp.AsSpan(at), (ushort)v);
        void U32(int at, int v) => BitConverter.TryWriteBytes(bmp.AsSpan(at), v);
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        U32(2, bmp.Length); U32(10, 14 + 40 + 8);
        U32(14, 40); U32(18, w); U32(22, h); U16(26, 1); U16(28, 1); U32(34, rowBytes * h); U32(46, 2);
        // Palette: entry 0 black, entry 1 white (BGRA); pixel bits all 0 = black.
        bmp[58] = bmp[59] = bmp[60] = 0xFF;
        return bmp;
    }

    private static bool IsRed(SKColor c) => c.Red > 200 && c.Green < 60 && c.Blue < 60;

    private static bool IsBlack(SKColor c) => c.Red < 60 && c.Green < 60 && c.Blue < 60;

    private static SKColor At(SKBitmap bitmap, double xPt, double yPt)
        => bitmap.GetPixel((int)Math.Round(xPt * Px), (int)Math.Round(yPt * Px));

    public static TheoryData<string, string> Formats => new()
    {
        { "PNG", "image/png" },
        { "GIF", "image/gif" },
        { "BMP", "image/bmp" },
    };

    private static byte[] Sample(string format) => format switch
    {
        "PNG" => RedPng(),
        "GIF" => RedGif(),
        _ => BlackOneBitBmp(),
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void NonJpegImage_IsDrawn_FillingItsBox(string format, string contentType)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var pdf = XfaTestForms.BuildPdf(XfaTestForms.Template(ImageDraw(contentType, Sample(format)), layout: "position"));
        var path = LayOutAndSave(pdf, AppOptions(), out var result);
        using var bitmap = Render(path, 1);

        Func<SKColor, bool> drawn = format == "BMP" ? IsBlack : IsRed;
        // The content area starts at (18pt, 18pt): the box covers x 90-162, y 90-162.
        drawn(At(bitmap, 92, 92)).Should().BeTrue($"the {format} image's top-left corner is at the box's");
        drawn(At(bitmap, 160, 160)).Should().BeTrue($"aspect none stretches the {format} image over the whole box");
        drawn(At(bitmap, 170, 170)).Should().BeFalse("nothing is drawn outside the box");
        result.Omissions.Should().NotContain(n => n.StartsWith(format, StringComparison.Ordinal));
    }

    [Fact]
    public void Tiff_IsReported_NotDrawn()
    {
        // SkiaSharp has no TIFF codec: the image stays an omission, and the form still lays out.
        var tiff = new byte[] { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 0, 0 };
        var pdf = XfaTestForms.BuildPdf(XfaTestForms.Template(ImageDraw("image/tiff", tiff), layout: "position"));
        LayOutAndSave(pdf, AppOptions(), out var result);

        result.Omissions.Should().Contain("TIFF images that could not be decoded not drawn");
    }

    [Fact]
    public void RealForm_Imm5257e_DrawsItsBmpWordmark()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        var source = TestRepoLayout.FindFile("test-pdfs", "xfa-real", "imm5257e.pdf");
        Assert.SkipWhen(source == null, TestRepoLayout.AbsenceReason("imm5257e.pdf", "test-pdfs/xfa-real/imm5257e.pdf"));

        // The Government of Canada wordmark is a 1147 x 279 1-bit BMP in a 25.4 x 6.35mm
        // (72 x 18pt) draw on page 1. Laid out with and without the decoder, the pages mutool
        // renders differ by ink in that box. (Page 5's 24-bit hand.bmp adds a little too.)
        var bytes = File.ReadAllBytes(source!);
        var with = LayOutAndSave(bytes, AppOptions(), out var result);
        var without = LayOutAndSave(bytes, new XfaLayoutOptions { TimeLimit = AppOptions().TimeLimit }, out _);

        var regions = new List<(int Page, int Ink, SKRectI Box)>();
        for (int page = 1; page <= result.PageCount; page++)
        {
            using var a = Render(with, page);
            using var b = Render(without, page);
            int ink = 0, left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            for (int y = 0; y < Math.Min(a.Height, b.Height); y++)
            {
                for (int x = 0; x < Math.Min(a.Width, b.Width); x++)
                {
                    if (!IsBlack(a.GetPixel(x, y)) || IsBlack(b.GetPixel(x, y)))
                        continue;
                    ink++;
                    left = Math.Min(left, x); top = Math.Min(top, y);
                    right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                }
            }
            if (ink > 0)
                regions.Add((page, ink, new SKRectI(left, top, right + 1, bottom + 1)));
        }

        // The draw sits at x 182.88mm (518.4pt), y 265.43mm (752.4pt) from the page's top-left.
        regions.Should().Contain(
            r => r.Page == 1 && Math.Abs(r.Box.Left - 518.4 * Px) <= 4 && r.Box.Top >= 752.4 * Px - 4
                && r.Box.Width <= 72 * Px + 4 && r.Box.Height <= 18 * Px + 4
                && r.Box.Width >= 50 * Px && r.Ink > 200,
            $"the wordmark adds ink within one 72 x 18pt box; regions found: {string.Join("; ", regions)}");
        result.Omissions.Should().NotContain(n => n.StartsWith("BMP", StringComparison.Ordinal));
    }
}
