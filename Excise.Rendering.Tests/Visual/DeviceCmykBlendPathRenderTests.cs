using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;

namespace Excise.Rendering.Tests.Visual;

/// <summary>
/// Pins the pixel behavior of the DeviceCMYK blend-path fill hot path
/// (RenderContext.TryPaintDeviceCmykBlendPath) after the #599 optimization
/// (bounded, reused coverage mask + bulk raw pixel access). The expected
/// values were captured from the pre-optimization renderer and verified
/// byte-identical against it (A/B PNG comparison over synthetic fixtures at
/// 96/150 DPI and 6 pages each of the DS-11/DS-82 passport forms).
/// </summary>
public sealed class DeviceCmykBlendPathRenderTests
{
    [Fact]
    public void RenderPage_DeviceCmykGroupBlendFills_ProducePinnedColors()
    {
        using var doc = PdfDocument.Open(BuildCmykBlendMixPdf());
        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 96, BackgroundColor = SKColors.White });

        bitmap.Width.Should().Be(400);
        bitmap.Height.Should().Be(400);

        // Interior probes (no antialiasing involvement), pre-change values.
        AssertPixel(bitmap, 67, 333, new SKColor(0, 174, 239), "pure cyan fill");
        AssertPixel(bitmap, 133, 267, new SKColor(236, 0, 140), "magenta painted over cyan");
        AssertPixel(bitmap, 267, 293, new SKColor(255, 242, 0), "half-alpha yellow via CMYK backdrop compositing");
        AssertPixel(bitmap, 200, 133, new SKColor(189, 0, 137), "Multiply blend fill");
        AssertPixel(bitmap, 253, 147, new SKColor(213, 38, 158), "Screen blend triangle interior");
        AssertPixel(bitmap, 67, 67, new SKColor(255, 255, 255), "untouched background");

        // The dashed DeviceCMYK stroke also routes through the blend path
        // (stroke style + dash path effect). Dash segmentation is sensitive to
        // the mask surface cull rect, so assert the dashes actually landed.
        CountDarkPixels(bitmap, yStart: 55, yEnd: 80).Should().BeGreaterThan(1200,
            "the dashed black DeviceCMYK stroke must rasterize through the blend-path mask");
    }

    [Fact]
    public void RenderPage_DeviceCmykKnockoutAndIsolatedGroups_ProducePinnedColors()
    {
        using var doc = PdfDocument.Open(BuildCmykGroupsPdf());
        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 96, BackgroundColor = SKColors.White });

        AssertPixel(bitmap, 80, 320, new SKColor(59, 194, 210), "knockout group fill");
        AssertPixel(bitmap, 160, 200, new SKColor(207, 149, 108), "isolated group Lighten/ColorDodge stack");
        AssertPixel(bitmap, 20, 20, new SKColor(228, 221, 242), "page-level CMYK wash backdrop");
    }

    private static void AssertPixel(SKBitmap bitmap, int x, int y, SKColor expected, string because)
    {
        var actual = bitmap.GetPixel(x, y);
        var delta = Math.Max(
            Math.Abs(actual.Red - expected.Red),
            Math.Max(Math.Abs(actual.Green - expected.Green), Math.Abs(actual.Blue - expected.Blue)));
        delta.Should().BeLessThanOrEqualTo(2,
            $"pixel ({x},{y}) [{because}] expected {expected} but was {actual}");
        actual.Alpha.Should().Be(255, because);
    }

    private static int CountDarkPixels(SKBitmap bitmap, int yStart, int yEnd)
    {
        var count = 0;
        for (var y = yStart; y < yEnd; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Red < 90 && c.Green < 90 && c.Blue < 90)
                    count++;
            }
        }

        return count;
    }

    /// <summary>
    /// One page inside a DeviceCMYK transparency group: opaque and half-alpha
    /// `k` fills, Multiply/Screen blend fills, an antialiased triangle, and a
    /// dashed DeviceCMYK stroke — every entry style of
    /// TryPaintDeviceCmykBlendPath.
    /// </summary>
    private static byte[] BuildCmykBlendMixPdf()
    {
        const string content =
            "1 0 0 0 k 20 20 120 120 re f\n" +
            "0 1 0 0 k 80 80 120 120 re f\n" +
            "/GA gs 0 0 1 0 k 140 20 120 120 re f\n" +
            "/GM gs 0.3 0.6 0 0.1 k 60 150 180 100 re f\n" +
            "/GS gs 150 150 m 280 160 l 200 290 l h f\n" +
            "/GD gs 0 0 0 1 K 4 w [6 3] 0 d 1 J 1 j 30 260 m 270 240 l S\n";
        const string resources =
            "/ExtGState << /GA << /ca 0.5 >> /GZ << /ca 0 >> /GM << /BM /Multiply >> " +
            "/GS << /BM /Screen >> /GD << /BM /Darken >> >>";
        return BuildSinglePagePdf(content, resources, extraObjects: null);
    }

    /// <summary>
    /// Nested DeviceCMYK form groups: a knockout (/K true) group with a
    /// zero-alpha fill and a Multiply fill, and an isolated (/I true) group
    /// with Lighten/ColorDodge fills — the knockout-reset, zero-alpha-shape,
    /// and direct-blend-function branches of the blend loop.
    /// </summary>
    private static byte[] BuildCmykGroupsPdf()
    {
        const string content =
            "0.1 0.1 0 0 k 0 0 300 300 re f\n" +
            "q 1 0 0 1 20 20 cm /F1 Do Q\n" +
            "q 1 0 0 1 90 90 cm /F2 Do Q\n";
        const string knockoutForm =
            "0.8 0 0.2 0 k 10 10 120 120 re f\n" +
            "/GM gs 0 0.7 0 0 k 60 60 120 120 re f\n" +
            "/GZ gs 1 0 0 0 k 30 100 80 80 re f\n";
        const string isolatedForm =
            "0.2 0.4 0.6 0 k 20 20 100 100 re f\n" +
            "/GL gs 0.6 0.1 0 0 k 60 40 100 100 re f\n" +
            "/GC gs 0 0.2 0.9 0 k 40 80 100 100 re f\n";
        var extraObjects = new (int Number, string Dictionary, string StreamContent)[]
        {
            (5,
             "/Type /XObject /Subtype /Form /BBox [0 0 200 200] " +
             "/Group << /S /Transparency /CS /DeviceCMYK /K true >> " +
             "/Resources << /ExtGState << /GZ << /ca 0 >> /GM << /BM /Multiply >> >> >>",
             knockoutForm),
            (6,
             "/Type /XObject /Subtype /Form /BBox [0 0 200 200] " +
             "/Group << /S /Transparency /CS /DeviceCMYK /I true >> " +
             "/Resources << /ExtGState << /GL << /BM /Lighten >> /GC << /BM /ColorDodge >> >> >>",
             isolatedForm),
        };
        return BuildSinglePagePdf(content, "/XObject << /F1 5 0 R /F2 6 0 R >>", extraObjects);
    }

    private static byte[] BuildSinglePagePdf(
        string content,
        string resources,
        (int Number, string Dictionary, string StreamContent)[]? extraObjects)
    {
        var sb = new StringBuilder();
        var objectCount = 4 + (extraObjects?.Length ?? 0);
        var offsets = new long[objectCount + 1];
        sb.Append("%PDF-1.7\n");

        offsets[1] = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets[3] = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R\n");
        sb.Append("   /Group << /S /Transparency /CS /DeviceCMYK >>\n");
        sb.Append($"   /Resources << {resources} >>\n>>\nendobj\n");

        offsets[4] = sb.Length;
        sb.Append($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        if (extraObjects != null)
        {
            foreach (var (number, dictionary, streamContent) in extraObjects)
            {
                offsets[number] = sb.Length;
                sb.Append($"{number} 0 obj\n<< {dictionary} /Length {streamContent.Length} >>\n");
                sb.Append($"stream\n{streamContent}\nendstream\nendobj\n");
            }
        }

        var xref = sb.Length;
        sb.Append($"xref\n0 {objectCount + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= objectCount; i++)
            sb.Append($"{offsets[i]:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Root 1 0 R /Size {objectCount + 1} >>\nstartxref\n{xref}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
