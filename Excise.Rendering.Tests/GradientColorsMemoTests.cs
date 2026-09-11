using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// #1404: axial/radial gradient colours are memoised per render, keyed by the
/// shading dictionary's reference identity plus the resource object a
/// /ColorSpace NAME resolved through. These tests pin that the memo changes no
/// pixel, that repeated `sh` of one shading actually hits it, and that the
/// same shading dictionary used under two different resource contexts is NOT
/// served a colour resolved in the other context.
/// </summary>
public sealed class GradientColorsMemoTests
{
    private const int StripCount = 60;

    // ---- scope unit checks ----------------------------------------------

    [Fact]
    public void ScopeMemoIsKeyedByShadingReferenceAndColorSpaceSource()
    {
        var scope = new RenderResourceScope();
        var shading = new PdfDictionary();
        var structuralTwin = new PdfDictionary();
        var source = new PdfArray();
        var colors = new GradientColors(SKColors.Red, SKColors.Blue, null, null);

        scope.TryGetGradientColors(shading, null, out _).Should().BeFalse();
        scope.CacheGradientColors(shading, null, colors);

        scope.TryGetGradientColors(shading, null, out var hit).Should().BeTrue();
        hit.Should().BeSameAs(colors);
        scope.TryGetGradientColors(shading, null, out var again).Should().BeTrue();
        again.Should().BeSameAs(colors);

        scope.TryGetGradientColors(structuralTwin, null, out _).Should().BeFalse(
            "the key is reference identity: an equal-content shading object is resolved on its own");
        scope.TryGetGradientColors(shading, source, out _).Should().BeFalse(
            "a different colour-space source object is a different resolution context");

        scope.Dispose();
        Action useAfterDispose = () => scope.TryGetGradientColors(shading, null, out _);
        useAfterDispose.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void RepeatedShOfOneShadingIsServedFromTheMemo()
    {
        // Seed the scope with a SENTINEL (solid magenta) for one shading
        // object before rendering. Every `sh` that consults the memo paints
        // magenta; any `sh` that re-resolves the shading paints its real
        // gradient, which contains no magenta. Uses only the production memo
        // API, so no test-only counter has to live in the renderer.
        using var shared = PdfDocument.Open(BuildStripsPdf(distinctShadingPerStrip: false));
        using var sharedBitmap = RenderWithSeededSentinel(shared, "ShA");
        for (var strip = 0; strip < StripCount; strip++)
        {
            IsSentinel(StripCentre(sharedBitmap, strip)).Should().Be(strip % 2 == 0,
                $"strip {strip}: every `sh` of the shared axial object (even strips) must be served " +
                "from the memo, and the radial object (odd strips) must not be");
        }

        using var distinct = PdfDocument.Open(BuildStripsPdf(distinctShadingPerStrip: true));
        using var distinctBitmap = RenderWithSeededSentinel(distinct, "S0");
        for (var strip = 0; strip < StripCount; strip++)
        {
            IsSentinel(StripCentre(distinctBitmap, strip)).Should().Be(strip == 0,
                $"strip {strip}: only the seeded object may be served the sentinel; an " +
                "identical-content twin is a different key");
        }
    }

    // ---- render identity -------------------------------------------------

    [Fact]
    public void ManyShOfTheSameShadingRenderIdenticallyToDistinctUncachedShadings()
    {
        // Same content stream shape, same shading CONTENT, rendered once with
        // every `sh` naming ONE shading object (memo hits) and once with every
        // `sh` naming its own identical-content object (memo misses, i.e. the
        // uncached resolution). The two must be pixel-identical.
        using var shared = PdfDocument.Open(BuildStripsPdf(distinctShadingPerStrip: false));
        using var distinct = PdfDocument.Open(BuildStripsPdf(distinctShadingPerStrip: true));

        using var sharedBitmap = new SkiaRenderer().RenderPage(shared.GetPage(1));
        using var distinctBitmap = new SkiaRenderer().RenderPage(distinct.GetPage(1));

        sharedBitmap.Width.Should().Be(distinctBitmap.Width);
        sharedBitmap.Height.Should().Be(distinctBitmap.Height);
        CountNonWhite(sharedBitmap).Should().BeGreaterThan(sharedBitmap.Width * sharedBitmap.Height / 2,
            "the strips must actually paint gradients, or pixel identity proves nothing");
        sharedBitmap.GetPixelSpan().ToArray().Should().Equal(distinctBitmap.GetPixelSpan().ToArray());
    }

    [Fact]
    public void NamedColorSpaceShadingIsNotReusedAcrossResourceContexts()
    {
        // One shading object, /ColorSpace /CS0. The page maps /CS0 to
        // DeviceGray; the form XObject maps /CS0 to a red/blue Indexed space.
        // Drawn in both contexts in one render, each half must equal a render
        // that drew only that half — a memo keyed on the shading alone would
        // paint the second half in the first half's colours.
        using var both = PdfDocument.Open(BuildNamedColorSpacePdf("/Sh1 sh", "/Sh1 sh"));
        using var formFirst = PdfDocument.Open(BuildNamedColorSpacePdf("/Sh1 sh", "/Sh1 sh", formFirst: true));
        using var pageOnly = PdfDocument.Open(BuildNamedColorSpacePdf("/Sh1 sh", null));
        using var formOnly = PdfDocument.Open(BuildNamedColorSpacePdf(null, "/Sh1 sh"));

        using var bothBitmap = new SkiaRenderer().RenderPage(both.GetPage(1));
        using var formFirstBitmap = new SkiaRenderer().RenderPage(formFirst.GetPage(1));
        using var pageOnlyBitmap = new SkiaRenderer().RenderPage(pageOnly.GetPage(1));
        using var formOnlyBitmap = new SkiaRenderer().RenderPage(formOnly.GetPage(1));

        var half = bothBitmap.Height / 2;

        // Non-vacuity: the page half (bottom) is grey, the form half (top) red.
        var bottomLeft = pageOnlyBitmap.GetPixel(2, bothBitmap.Height - 2);
        var topLeft = formOnlyBitmap.GetPixel(2, 2);
        (bottomLeft.Red == bottomLeft.Green && bottomLeft.Green == bottomLeft.Blue).Should().BeTrue(
            $"the page-context shading resolves /CS0 as DeviceGray (got {bottomLeft})");
        (topLeft.Red > 200 && topLeft.Blue < 50).Should().BeTrue(
            $"the form-context shading resolves /CS0 as the red/blue Indexed space (got {topLeft})");

        foreach (var (label, bitmap) in new[] { ("page then form", bothBitmap), ("form then page", formFirstBitmap) })
        {
            Rows(bitmap, 0, half).Should().Equal(Rows(formOnlyBitmap, 0, half),
                $"{label}: the form half must match a form-only render");
            Rows(bitmap, half, bitmap.Height).Should().Equal(Rows(pageOnlyBitmap, half, bitmap.Height),
                $"{label}: the page half must match a page-only render");
        }
    }

    // ---- helpers ---------------------------------------------------------

    private static readonly SKColor Sentinel = new(255, 0, 255);

    private static SKBitmap RenderWithSeededSentinel(PdfDocument document, string shadingName)
    {
        var page = document.GetPage(1);
        var shadings = (PdfDictionary)document.Resolve(page.Resources!.GetOptional("Shading")!);
        var shading = (PdfDictionary)document.Resolve(shadings.GetOptional(shadingName)!);

        var bitmap = new SKBitmap(612, 792, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.SetMatrix(new SKMatrix(1, 0, 0, 0, -1, 792, 0, 0, 1));
        using var scope = new RenderResourceScope();
        // /ColorSpace /DeviceRGB is a built-in name with no /DefaultRGB in
        // these resources, so the colour-space source part of the key is null.
        scope.CacheGradientColors(shading, null, new GradientColors(Sentinel, Sentinel, null, null));
        new RenderContext(canvas, page, new RenderOptions(), scope, CancellationToken.None, bitmap).Render();
        return bitmap;
    }

    private static SKColor StripCentre(SKBitmap bitmap, int strip)
    {
        var x = (int)((strip + 0.5) * (612.0 / StripCount));
        return bitmap.GetPixel(x, 396);
    }

    private static bool IsSentinel(SKColor color)
        => color.Red == Sentinel.Red && color.Green == Sentinel.Green && color.Blue == Sentinel.Blue;

    private static int CountNonWhite(SKBitmap bitmap)
    {
        var count = 0;
        var pixels = bitmap.GetPixelSpan();
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            if (pixels[i] != 255 || pixels[i + 1] != 255 || pixels[i + 2] != 255)
                count++;
        }

        return count;
    }

    private static byte[] Rows(SKBitmap bitmap, int fromRow, int toRow)
    {
        var rowBytes = bitmap.RowBytes;
        return bitmap.GetPixelSpan().Slice(fromRow * rowBytes, (toRow - fromRow) * rowBytes).ToArray();
    }

    /// <summary>
    /// A 612x792 page of <see cref="StripCount"/> vertical clip strips, each
    /// painted with `sh`: even strips an axial shading with a sampled
    /// (N = 2) function, odd strips a radial shading with a sampled function.
    /// </summary>
    private static byte[] BuildStripsPdf(bool distinctShadingPerStrip)
    {
        const string axial =
            "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 612 792] /Domain [0 1] " +
            "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0.2 0] /C1 [0 0.4 1] /N 2 >> " +
            "/Extend [true true] >>";
        const string radial =
            "<< /ShadingType 3 /ColorSpace /DeviceRGB /Coords [306 396 10 306 396 420] /Domain [0 1] " +
            "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 1 0] /C1 [0.1 0.6 0.2] /N 3 >> " +
            "/Extend [true true] >>";

        var content = new StringBuilder();
        var shadingEntries = new StringBuilder();
        var bodies = new List<string>();
        const int firstShadingObject = 5;
        var stripWidth = 612.0 / StripCount;

        if (!distinctShadingPerStrip)
        {
            bodies.Add(axial);
            bodies.Add(radial);
            shadingEntries.Append($"/ShA {firstShadingObject} 0 R /ShB {firstShadingObject + 1} 0 R ");
        }

        for (var strip = 0; strip < StripCount; strip++)
        {
            var isAxial = strip % 2 == 0;
            string name;
            if (distinctShadingPerStrip)
            {
                name = $"S{strip}";
                shadingEntries.Append($"/{name} {firstShadingObject + bodies.Count} 0 R ");
                bodies.Add(isAxial ? axial : radial);
            }
            else
            {
                name = isAxial ? "ShA" : "ShB";
            }

            var x = (strip * stripWidth).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            var w = stripWidth.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            content.Append($"q {x} 0 {w} 792 re W n /{name} sh Q\n");
        }

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            $"/Resources << /Shading << {shadingEntries} >> >> >>",
            StreamBody("", content.ToString()),
        };
        objects.AddRange(bodies);
        return BuildPdf(objects);
    }

    /// <summary>
    /// One shading object (5) with <c>/ColorSpace /CS0</c>. The page maps /CS0
    /// to DeviceGray and paints the bottom half; form XObject 6 maps /CS0 to
    /// <c>[/Indexed /DeviceRGB 1 &lt;FF0000 0000FF&gt;]</c> and paints the top half.
    /// </summary>
    private static byte[] BuildNamedColorSpacePdf(string? pageOp, string? formOp, bool formFirst = false)
    {
        var pagePart = pageOp == null ? "" : $"q 0 0 612 396 re W n {pageOp} Q\n";
        var formPart = formOp == null ? "" : "q 1 0 0 1 0 396 cm /Fm1 Do Q\n";
        var content = formFirst ? formPart + pagePart : pagePart + formPart;

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /ColorSpace << /CS0 /DeviceGray >> /Shading << /Sh1 5 0 R >> " +
            "/XObject << /Fm1 6 0 R >> >> >>",
            StreamBody("", content),
            "<< /ShadingType 2 /ColorSpace /CS0 /Coords [0 0 612 0] /Domain [0 1] " +
            "/Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> /Extend [true true] >>",
            StreamBody(
                "/Type /XObject /Subtype /Form /BBox [0 0 612 396] " +
                "/Resources << /ColorSpace << /CS0 [/Indexed /DeviceRGB 1 <FF00000000FF>] >> " +
                "/Shading << /Sh1 5 0 R >> >>",
                formOp ?? ""),
        };
        return BuildPdf(objects);
    }

    private static string StreamBody(string dictionaryEntries, string content)
        => $"<< {dictionaryEntries} /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    private static byte[] BuildPdf(IReadOnlyList<string> objectBodies)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true) { NewLine = "\n" };
        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[objectBodies.Count + 1];
        for (var i = 0; i < objectBodies.Count; i++)
        {
            offsets[i + 1] = ms.Position;
            writer.WriteLine($"{i + 1} 0 obj");
            writer.WriteLine(objectBodies[i]);
            writer.WriteLine("endobj");
            writer.Flush();
        }

        var xref = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine($"0 {objectBodies.Count + 1}");
        writer.WriteLine("0000000000 65535 f ");
        for (var i = 1; i <= objectBodies.Count; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Root 1 0 R /Size {objectBodies.Count + 1} >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xref.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteLine("%%EOF");
        writer.Flush();
        return ms.ToArray();
    }
}
