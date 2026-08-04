using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Regression cover for the Line / Polygon / PolyLine / Ink no-<c>/AP</c>
/// synthesis shipped in v3.6.0 (8c0d3b88) with NO tests at all.
///
/// It works — verified against mutool on authored fixtures — but nothing was
/// guarding it, so any change to the default-appearance switch could have
/// removed four subtypes silently. That is the whole point of this file.
///
/// It also pins the SHAPE of the contract, which is easy to get wrong in the
/// permissive direction: these subtypes draw only when the annotation supplies
/// <c>/C</c>. Without a colour there is nothing to stroke with, and mutool
/// draws nothing either — which is exactly why the veraPDF 6-3-3-t01 Line/Ink/
/// PolyLine fixtures are blank in excise AND in mutool, and why they sit in
/// #889 as renderer splits rather than as defects. Anyone "fixing" those blanks
/// by inventing a default colour would contradict that reasoning, and
/// <see cref="ShapeWithoutColor_DrawsNothing"/> is what stops it happening
/// quietly.
/// </summary>
public class ShapeAnnotationSynthesisTests : IDisposable
{
    private const int Dpi = 72;
    private const int PageSize = 200;

    private readonly List<string> _temp = new();

    /// <summary>
    /// Measured on these exact fixtures (excise vs mutool, 72 dpi, inked px):
    /// Line 704/702, Polygon 1256/1680, PolyLine 844/1122, Ink 602/712.
    /// The assertion is deliberately "substantially inked", not a pixel match —
    /// join and cap rendering differs between engines and pinning a count would
    /// make this brittle without making it stronger.
    /// </summary>
    [Theory]
    [InlineData("Line",     "/L [30 30 170 170]")]
    [InlineData("Polygon",  "/Vertices [30 30 170 30 100 170]")]
    [InlineData("PolyLine", "/Vertices [30 30 170 30 100 170]")]
    [InlineData("Ink",      "/InkList [[30 30 90 90 170 30]]")]
    public void ShapeWithColor_IsDrawn(string subtype, string geometry)
    {
        var path = WriteTemp(ShapePdf(subtype, geometry, withColor: true));
        using var bmp = RenderWithExcise(path);

        InkPixels(bmp).Should().BeGreaterThan(200,
            $"/{subtype} with /C and /BS must be stroked from its own geometry — " +
            "this synthesis shipped with no test and could have been removed unnoticed");
    }

    [Theory]
    [InlineData("Line",     "/L [30 30 170 170]")]
    [InlineData("Polygon",  "/Vertices [30 30 170 30 100 170]")]
    [InlineData("PolyLine", "/Vertices [30 30 170 30 100 170]")]
    [InlineData("Ink",      "/InkList [[30 30 90 90 170 30]]")]
    public void ShapeWithColor_MatchesIndependentRenderer(string subtype, string geometry)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(ShapePdf(subtype, geometry, withColor: true));
        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        InkPixels(reference!).Should().BeGreaterThan(200,
            $"mutool strokes /{subtype} from its geometry — otherwise the fixture is wrong");
        InkPixels(RenderWithExcise(path)).Should().BeGreaterThan(200);
    }

    /// <summary>
    /// The half that pins #889's reasoning. With no <c>/C</c> there is nothing
    /// to stroke with and mutool draws nothing either, so excise drawing
    /// nothing is agreement, not a gap — despite the corpus scan flagging those
    /// pages, because it compares against the most-inked oracle (#883) and
    /// pdftocairo alone invents a colour.
    /// </summary>
    [Theory]
    [InlineData("Line",     "/L [30 30 170 170]")]
    [InlineData("Polygon",  "/Vertices [30 30 170 30 100 170]")]
    [InlineData("PolyLine", "/Vertices [30 30 170 30 100 170]")]
    [InlineData("Ink",      "/InkList [[30 30 90 90 170 30]]")]
    public void ShapeWithoutColor_DrawsNothing(string subtype, string geometry)
    {
        var path = WriteTemp(ShapePdf(subtype, geometry, withColor: false));
        using var bmp = RenderWithExcise(path);

        InkPixels(bmp).Should().Be(0,
            $"/{subtype} with no /C supplies no stroke colour, and mutool draws nothing " +
            "either — inventing one here would silently contradict #889, where these " +
            "very subtypes are classified as renderer splits rather than excise defects");
    }

    [Fact]
    public void ShapeWithoutColor_AgreesWithMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(ShapePdf("Line", "/L [30 30 170 170]", withColor: false));
        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        InkPixels(reference!).Should().Be(0,
            "mutool also declines a /Line with no /C — that agreement is what makes " +
            "excise's blank correct rather than a missing feature");
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static byte[] ShapePdf(string subtype, string geometry, bool withColor)
    {
        var annot = $"<< /Type /Annot /Subtype /{subtype} /F 4 /Rect [20 20 180 180] " +
                    geometry + (withColor ? " /C [1 0 0]" : "") + " /BS << /W 3 >> >>";
        return Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {PageSize} {PageSize}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [4 0 R] >>\nendobj\n",
            $"4 0 obj\n{annot}\nendobj\n",
        });
    }

    private static byte[] Assemble(string[] objects)
    {
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

    private static SKBitmap RenderWithExcise(string path)
    {
        using var doc = PdfDocument.Open(path);
        return new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });
    }

    private static int InkPixels(SKBitmap bmp)
    {
        int ink = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 240 || c.Green < 240 || c.Blue < 240) ink++;
            }
        return ink;
    }

    private string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-shape-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        _temp.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _temp) { try { File.Delete(p); } catch { } }
    }
}
