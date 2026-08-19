using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1073 — two defects in annotation border drawing.
///
/// <list type="number">
///   <item><b>The dash pattern was ignored.</b> <c>/BS &lt;&lt; /S /D /D [6 3] &gt;&gt;</c>
///     rendered solid. excise honoured <c>/W</c> and nothing else in the
///     border-style dictionary (§12.5.4).</item>
///   <item><b>The border was centred on <c>/Rect</c> rather than inset.</b>
///     §12.5.6.8 insets a Square/Circle from <c>/Rect</c> by the border width so
///     the stroke's OUTER edge lands on it; Skia strokes centred, so half the
///     border sat outside the annotation rectangle, scaling with width.</item>
/// </list>
///
/// <para>The second is not cosmetic. <c>/Rect</c> is the geometry excise's own
/// redaction scrubber uses to decide which annotations a redaction covers, so
/// ink outside it was ink the removal path did not know about. With the inset
/// the drawn ink lands inside <c>/Rect</c>, which is what makes <c>/Rect</c>-based
/// intersection sufficient rather than approximate.</para>
/// </summary>
public class AnnotationBorderTests : IDisposable
{
    private const int Dpi = 150;
    private const int PageSize = 250;

    private readonly List<string> _temp = new();

    /// <summary>
    /// The inset, measured as a bounding box rather than an ink count, and at
    /// several widths because the error scaled with the width — a single-width
    /// test would have passed at 1pt while a 12pt border overflowed by 6.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(12)]
    public void TheBorderStaysInsideRect(int borderWidth)
    {
        using var bmp = RenderWithExcise(WriteTemp(SquarePdf(borderWidth, dashed: false)));
        var (w, h) = InkBounds(bmp);

        float s = Dpi / 72f;
        // /Rect [40 40 200 200] -> 160 x 160 pt
        var expected = 160 * s;

        w.Should().BeLessThanOrEqualTo((int)expected + 2,
            $"at a {borderWidth}pt border the stroke must not extend past /Rect — " +
            "before #1073 it overflowed by one full border width in each dimension");
        h.Should().BeLessThanOrEqualTo((int)expected + 2);

        // And it must still be drawn at close to full size: an over-eager inset
        // that shrank the shape would satisfy the bound above.
        w.Should().BeGreaterThan((int)expected - (int)(borderWidth * s) - 3,
            "the border must fill /Rect, not sit well inside it");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(12)]
    public void TheBorderBoxMatchesIndependentRenderers(int borderWidth)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(SquarePdf(borderWidth, dashed: false));
        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        var (rw, rh) = InkBounds(reference!);
        var (ew, eh) = InkBounds(RenderWithExcise(path));

        ew.Should().BeCloseTo(rw, 2, "excise's border box must land where mutool's does");
        eh.Should().BeCloseTo(rh, 2);
    }

    /// <summary>
    /// Dashes, asserted as LESS INK over the same box — the only description
    /// that is true of every engine's dash phase. Pinning a count would pin
    /// Skia's phase against mutool's, which is not the property under test.
    /// </summary>
    [Fact]
    public void ADashedBorderDrawsLessInkThanASolidOne()
    {
        var solid = InkPixels(RenderWithExcise(WriteTemp(SquarePdf(3, dashed: false))));
        var dashed = InkPixels(RenderWithExcise(WriteTemp(SquarePdf(3, dashed: true))));

        dashed.Should().BeLessThan((int)(solid * 0.9),
            "a [6 3] dash leaves a third of the perimeter unpainted — before #1073 " +
            "/S /D rendered solid and these two were equal");
        dashed.Should().BeGreaterThan((int)(solid * 0.3),
            "but it is still a border: near-zero ink would mean the dash swallowed it");
    }

    [Fact]
    public void ADashedBorderIsAlsoDashedForAnIndependentRenderer()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var solidPath = WriteTemp(SquarePdf(3, dashed: false));
        var dashedPath = WriteTemp(SquarePdf(3, dashed: true));

        using var refSolid = MutoolReferenceRenderer.RenderPage(solidPath, 1, Dpi);
        using var refDashed = MutoolReferenceRenderer.RenderPage(dashedPath, 1, Dpi);
        refSolid.Should().NotBeNull();
        refDashed.Should().NotBeNull();

        InkPixels(refDashed!).Should().BeLessThan((int)(InkPixels(refSolid!) * 0.9),
            "fixture sanity — mutool must draw this pattern as dashed, or the test " +
            "above is asserting a behaviour nobody else has");
    }

    /// <summary>
    /// The inset is Square/Circle only (§12.5.6.8). A Line draws on its own
    /// declared <c>/L</c> endpoints, and insetting those would move ink the file
    /// positions explicitly.
    /// </summary>
    [Fact]
    public void ALineIsNotInset()
    {
        using var bmp = RenderWithExcise(WriteTemp(LinePdf(borderWidth: 12)));
        var (w, _) = InkBounds(bmp);

        float s = Dpi / 72f;
        // /L [40 120 200 120] -> 160 pt long, plus the cap at each end.
        w.Should().BeGreaterThan((int)(150 * s),
            "a Line is drawn on /L, not inset from /Rect — shrinking it would move " +
            "ink the annotation places explicitly");
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static byte[] SquarePdf(int borderWidth, bool dashed)
    {
        var bs = dashed
            ? $"/BS << /W {borderWidth} /S /D /D [6 3] >>"
            : $"/BS << /W {borderWidth} /S /S >>";
        var annot = $"<< /Type /Annot /Subtype /Square /F 4 /Rect [40 40 200 200] " +
                    $"/C [0 0 0] {bs} >>";
        return Page(annot);
    }

    private static byte[] LinePdf(int borderWidth)
    {
        var annot = $"<< /Type /Annot /Subtype /Line /F 4 /Rect [40 100 200 140] " +
                    $"/L [40 120 200 120] /C [0 0 0] /BS << /W {borderWidth} >> >>";
        return Page(annot);
    }

    private static byte[] Page(string annot) => Assemble(new[]
    {
        "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
        $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {PageSize} {PageSize}] >>\nendobj\n",
        "3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [4 0 R] >>\nendobj\n",
        $"4 0 obj\n{annot}\nendobj\n",
    });

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

    private static (int Width, int Height) InkBounds(SKBitmap bmp)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 200 && c.Green < 200 && c.Blue < 200)
                {
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }
        return maxX < 0 ? (0, 0) : (maxX - minX + 1, maxY - minY + 1);
    }

    private static int InkPixels(SKBitmap bmp)
    {
        int ink = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 200 && c.Green < 200 && c.Blue < 200) ink++;
            }
        return ink;
    }

    private string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-border-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        _temp.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _temp) { try { File.Delete(p); } catch { } }
    }
}
