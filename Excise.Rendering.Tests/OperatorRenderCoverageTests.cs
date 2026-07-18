using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using System.IO;
using System.Text;
using Xunit;

namespace Excise.Rendering.Tests;

/// <summary>
/// Render-OUTPUT coverage for operators whose dispatch existed but whose visual
/// effect was never asserted (#350): the shading operator <c>sh</c> (axial /
/// radial gradients, which were silently no-ops in prior tests because the test
/// PDFs carried no <c>/Shading</c> resource) and the dash operator <c>d</c>
/// (previously parsed but ignored by the renderer). These tests assert actual
/// pixels, not just "renders without error".
/// </summary>
public class OperatorRenderCoverageTests
{
    private const double Scale = 150.0 / 72.0;   // default render DPI is 150

    private static int Px(double userX) => (int)(userX * Scale);
    private static int Py(SKBitmap b, double userY) => b.Height - (int)(userY * Scale);

    // ---- Shading (sh) ---------------------------------------------------

    [Fact]
    public void Sh_AxialShading_PaintsLeftToRightGradient()
    {
        // /Sh1 is an axial (Type 2) gradient: red at the left edge -> blue at the
        // right edge, spanning the page. `sh` with no clip fills the whole page.
        var pdf = CreatePdfWithShadings("/Sh1 sh");
        using var doc = PdfDocument.Open(pdf);
        using var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1));

        var left = bmp.GetPixel(Px(30), Py(bmp, 396));
        var right = bmp.GetPixel(Px(582), Py(bmp, 396));

        left.Red.Should().BeGreaterThan(left.Blue,
            "the left edge of a red->blue axial gradient must be red-dominant");
        right.Blue.Should().BeGreaterThan(right.Red,
            "the right edge of a red->blue axial gradient must be blue-dominant");
    }

    [Fact]
    public void Sh_RadialShading_PaintsCenterToEdgeGradient()
    {
        // /Sh2 is a radial (Type 3) gradient: yellow at the center -> green at the
        // outer radius.
        var pdf = CreatePdfWithShadings("/Sh2 sh");
        using var doc = PdfDocument.Open(pdf);
        using var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1));

        var center = bmp.GetPixel(Px(306), Py(bmp, 396));
        var edge = bmp.GetPixel(Px(306), Py(bmp, 110));   // ~286 user-units below center, near r1=300

        ((int)center.Red).Should().BeGreaterThan(150, "center of a yellow->green radial gradient is yellow (red high)");
        ((int)center.Red).Should().BeGreaterThan(edge.Red + 30,
            "red must fall off from the yellow center toward the green edge");
    }

    [Fact]
    public void Sh_RadialShadingWithoutExtend_DoesNotPaintOutsideEndpointCircles()
    {
        // /Sh3 omits /Extend. PDF radial shadings default to [false false],
        // so the shader must not clamp-fill the whole current clipping area.
        var pdf = CreatePdfWithShadings("/Sh3 sh");
        using var doc = PdfDocument.Open(pdf);
        using var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1));

        var center = bmp.GetPixel(Px(306), Py(bmp, 396));
        var outside = bmp.GetPixel(Px(80), Py(bmp, 396));
        var ring = bmp.GetPixel(Px(306), Py(bmp, 500));

        center.Should().Be(SKColors.White,
            "without start extension, the area inside the start circle must remain unpainted");
        outside.Should().Be(SKColors.White,
            "without end extension, the page outside the end circle must remain unpainted");
        (ring.Red < 245 || ring.Green < 245 || ring.Blue < 245).Should().BeTrue(
            "the annulus between the radial shading circles must still be painted");
    }

    [Fact]
    public void Sh_WithClipping_RestrictsGradientToClipRegion()
    {
        // Clip to a 120x120 box in the lower-left, then paint the axial gradient.
        // Pixels inside the clip are painted; pixels well outside stay white.
        var pdf = CreatePdfWithShadings("q 20 20 120 120 re W n /Sh1 sh Q");
        using var doc = PdfDocument.Open(pdf);
        using var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1));

        var inside = bmp.GetPixel(Px(80), Py(bmp, 80));
        var outside = bmp.GetPixel(Px(400), Py(bmp, 400));

        // Inside the clip the gradient is painted (not white).
        (inside.Red < 245 || inside.Green < 245 || inside.Blue < 245).Should().BeTrue(
            "the gradient must be painted inside the clip region");
        outside.Should().Be(SKColors.White, "the gradient must not paint outside the clip region");
    }

    [Fact]
    public void Sh_AxialShadingWithBBox_DoesNotPaintOutsideShadingBounds()
    {
        var pdf = CreatePdfWithShadings("/Sh4 sh");
        using var doc = PdfDocument.Open(pdf);
        using var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1));

        var inside = bmp.GetPixel(Px(130), Py(bmp, 130));
        var outside = bmp.GetPixel(Px(300), Py(bmp, 130));

        (inside.Red < 245 || inside.Green < 245 || inside.Blue < 245).Should().BeTrue(
            "the axial shading must still paint inside its declared BBox");
        outside.Should().Be(SKColors.White,
            "a direct sh operator must not clamp-fill the page outside the shading BBox");
    }

    [Fact]
    public void Sh_MissingShadingResource_DoesNotThrowAndLeavesPageBlank()
    {
        var pdf = CreatePdfWithShadings("/DoesNotExist sh");
        using var doc = PdfDocument.Open(pdf);
        using var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1));

        bmp.GetPixel(Px(306), Py(bmp, 396)).Should().Be(SKColors.White,
            "an unresolved shading name is a no-op, leaving the page white");
    }

    // ---- Dash pattern (d) ----------------------------------------------

    [Fact]
    public void Dash_BreaksStrokeIntoGaps_ComparedToSolidLine()
    {
        // Same thick horizontal line, once solid and once dashed. The dashed line
        // must leave gaps -> measurably fewer painted pixels along the row.
        const string geom = "0 G 6 w 100 400 m 500 400 l S";
        int solidDark = CountDarkAlongRow("[] 0 d " + geom, 400, 100, 500);
        int dashedDark = CountDarkAlongRow("[12 12] 0 d " + geom, 400, 100, 500);

        solidDark.Should().BeGreaterThan(0, "the solid control line must paint pixels");
        dashedDark.Should().BeGreaterThan(0, "a dashed line still paints its 'on' segments");
        dashedDark.Should().BeLessThan((int)(solidDark * 0.8),
            "a [12 12] dash must leave gaps, painting far fewer pixels than the solid line");
    }

    [Fact]
    public void Dash_EmptyArray_RendersSolidLine()
    {
        // An empty dash array resets to solid: dashing after `[] 0 d` paints a full
        // line (no gaps), matching a never-dashed control.
        const string geom = "0 G 6 w 100 300 m 500 300 l S";
        int control = CountDarkAlongRow(geom, 300, 100, 500);
        int reset = CountDarkAlongRow("[5 5] 0 d [] 0 d " + geom, 300, 100, 500);

        reset.Should().BeGreaterThan((int)(control * 0.9),
            "an empty dash array must produce a solid line again");
    }

    /// <summary>Count dark (stroked) pixels along the device row of a user-space horizontal line.</summary>
    private static int CountDarkAlongRow(string content, double userY, double userX0, double userX1)
    {
        var pdf = CreatePdfWithShadings(content);
        using var doc = PdfDocument.Open(pdf);
        using var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1));
        int row = Py(bmp, userY);
        int dark = 0;
        for (int x = Px(userX0); x <= Px(userX1) && x < bmp.Width; x++)
        {
            // scan a few rows to be robust to the line's device thickness/AA
            for (int dy = -3; dy <= 3; dy++)
            {
                int y = row + dy;
                if (y < 0 || y >= bmp.Height) continue;
                if (bmp.GetPixel(x, y).Red < 128) { dark++; break; }
            }
        }
        return dark;
    }

    // ---- PDF builder with /Shading resources ---------------------------

    /// <summary>
    /// Minimal single-page PDF whose page Resources include two shadings:
    /// /Sh1 = axial red->blue across the page, /Sh2 = radial yellow(center)->green(edge).
    /// </summary>
    private static byte[] CreatePdfWithShadings(string content)
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true) { NewLine = "\n" };
        w.WriteLine("%PDF-1.4");
        w.Flush();

        var offsets = new long[10];

        void Obj(int n, string body)
        {
            offsets[n] = ms.Position;
            w.WriteLine($"{n} 0 obj");
            w.WriteLine(body);
            w.WriteLine("endobj");
            w.Flush();
        }

        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
               "/Resources << /Font << /F1 5 0 R >> /Shading << /Sh1 6 0 R /Sh2 7 0 R /Sh3 8 0 R /Sh4 9 0 R >> >> >>");

        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length {content.Length} >>");
        w.WriteLine("stream");
        w.Write(content);
        w.WriteLine();
        w.WriteLine("endstream");
        w.WriteLine("endobj");
        w.Flush();

        Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        // Axial (Type 2) red -> blue, left to right across the page mid-line.
        Obj(6, "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 396 612 396] /Domain [0 1] " +
               "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> " +
               "/Extend [true true] >>");
        // Radial (Type 3) yellow (center, r0=0) -> green (edge, r1=300).
        Obj(7, "<< /ShadingType 3 /ColorSpace /DeviceRGB /Coords [306 396 0 306 396 300] /Domain [0 1] " +
               "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 1 0] /C1 [0 1 0] /N 1 >> " +
               "/Extend [true true] >>");
        // Radial annulus with default /Extend [false false].
        Obj(8, "<< /ShadingType 3 /ColorSpace /DeviceRGB /Coords [306 396 80 306 396 120] /Domain [0 1] " +
               "/Function << /FunctionType 2 /Domain [0 1] /C0 [0.6 0.8 0.8] /C1 [0 0.8 0.8] /N 1 >> >>");
        // Small axial shading with Extend true and a BBox. Without BBox clipping,
        // the clamped shader would paint far beyond the intended 80x80 area.
        Obj(9, "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [100 120 180 120] /Domain [0 1] " +
               "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> " +
               "/Extend [true true] /BBox [100 100 180 180] >>");

        long xref = ms.Position;
        w.WriteLine("xref");
        w.WriteLine("0 10");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 9; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.WriteLine("trailer");
        w.WriteLine("<< /Root 1 0 R /Size 10 >>");
        w.WriteLine("startxref");
        w.WriteLine(xref.ToString());
        w.WriteLine("%%EOF");
        w.Flush();
        return ms.ToArray();
    }
}
