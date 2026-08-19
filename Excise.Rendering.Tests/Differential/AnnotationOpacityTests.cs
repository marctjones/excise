using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1072 — <c>/CA</c> (§12.5.2), the annotation's constant opacity, was applied
/// to text markup only. Every other subtype, and every annotation with a baked
/// <c>/AP</c>, drew fully opaque.
///
/// <para>This is the one annotation defect where excise <b>added</b> ink rather
/// than omitting it. A Square or Highlight authored at <c>/CA 0.3</c> to sit
/// lightly over text was painted solid and BURIED the text underneath — content
/// the reader can see everywhere else, hidden here. For a redaction tool,
/// "content the user believes is present but cannot see" is the wrong direction
/// of error.</para>
///
/// <para>Fixed by wrapping the whole per-annotation draw in a transparency
/// layer at the dispatch site, so it covers the <c>/AP</c> path too — a fix
/// inside the synthesis switch would have left most real-world annotations
/// opaque. Group alpha rather than per-paint alpha is MEASURED, not argued:
/// against mutool and pdftocairo on a filled+stroked Square, excise now lands
/// within 1 RGB unit at CA 1.0 / 0.5 / 0.2. Multiplying alpha into each paint
/// composites stroke over fill inside the group and darkens their overlap,
/// which neither oracle does.</para>
/// </summary>
public class AnnotationOpacityTests : IDisposable
{
    private const int Dpi = 150;
    private const int PageSize = 200;

    private readonly List<string> _temp = new();

    /// <summary>
    /// The defect, as an ORDERING rather than as pixel counts. Absolute counts
    /// would be re-broken by any later geometry change (#1073 moves the border
    /// inset); the property that matters is monotonic.
    /// </summary>
    [Fact]
    public void LowerCA_DrawsLighter()
    {
        var full = MeanDarkness(RenderWithExcise(WriteTemp(SquarePdf("1.0"))));
        var half = MeanDarkness(RenderWithExcise(WriteTemp(SquarePdf("0.5"))));
        var faint = MeanDarkness(RenderWithExcise(WriteTemp(SquarePdf("0.2"))));

        full.Should().BeGreaterThan(half + 10,
            "/CA 0.5 must be visibly lighter than /CA 1.0 — before #1072 all three " +
            "rendered identically opaque");
        half.Should().BeGreaterThan(faint + 10, "and 0.2 lighter still");
    }

    /// <summary>
    /// The trap a naive "is it lighter" check falls into: an implementation
    /// that faded only the FILL would pass, while the border stayed solid.
    /// Sampled on the border itself, away from the interior.
    /// </summary>
    [Fact]
    public void LowCA_FadesTheBorderAsWellAsTheInterior()
    {
        using var opaque = RenderWithExcise(WriteTemp(SquarePdf("1.0")));
        using var faint = RenderWithExcise(WriteTemp(SquarePdf("0.2")));

        BorderDarkness(faint).Should().BeLessThan(BorderDarkness(opaque) - 10,
            "§12.5.2 applies /CA to the stroke as well as the fill; a fill-only " +
            "implementation leaves a solid outline around a ghosted interior");
    }

    /// <summary>
    /// The regression guard. /CA is optional and defaults to 1.0, so an
    /// annotation without it must render exactly as it did before this change —
    /// which is also what stops the new transparency layer being opened for
    /// every annotation in every document.
    /// </summary>
    [Fact]
    public void NoCA_RendersIdenticallyToCA1()
    {
        using var absent = RenderWithExcise(WriteTemp(SquarePdf(null)));
        using var one = RenderWithExcise(WriteTemp(SquarePdf("1.0")));

        MeanDarkness(absent).Should().BeApproximately(MeanDarkness(one), 1.0,
            "/CA defaults to 1.0 (§12.5.2 Table 164)");
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("0.5")]
    [InlineData("0.2")]
    public void OpacityMatchesIndependentRenderers(string ca)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(SquarePdf(ca));
        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        // Wide tolerance on purpose: this asserts excise is on the same curve as
        // mutool, not that it matches its rasteriser. Measured agreement on
        // these fixtures is within ~1 RGB unit; 12 leaves room for AA and
        // Skia-origin differences without admitting a missing /CA (which was a
        // gap of 80+).
        MeanDarkness(RenderWithExcise(path)).Should().BeApproximately(
            MeanDarkness(reference!), 12.0,
            $"at /CA {ca} excise must land where an independent renderer lands");
    }

    /// <summary>
    /// The acceptance the issue asked for, and the reason it was ranked above
    /// the other annotation defects: a translucent Highlight must not hide the
    /// text it marks.
    /// </summary>
    [Fact]
    public void TextUnderATranslucentHighlight_StaysReadable()
    {
        using var bmp = RenderWithExcise(WriteTemp(HighlightOverTextPdf("0.3")));

        DarkInkPixels(bmp).Should().BeGreaterThan(40,
            "the glyphs under a /CA 0.3 highlight must still be there — an opaque " +
            "highlight buries the very text it exists to emphasise");
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static byte[] SquarePdf(string? ca)
    {
        var annot = "<< /Type /Annot /Subtype /Square /F 4 /Rect [40 40 160 160] " +
                    "/C [0.8 0.1 0.1] /IC [0.2 0.4 0.9] /BS << /W 4 >>" +
                    (ca == null ? "" : $" /CA {ca}") + " >>";
        return Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {PageSize} {PageSize}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [4 0 R] >>\nendobj\n",
            $"4 0 obj\n{annot}\nendobj\n",
        });
    }

    private static byte[] HighlightOverTextPdf(string ca)
    {
        const string content = "BT /F1 24 Tf 30 90 Td (HHHHHH) Tj ET\n";
        var annot = "<< /Type /Annot /Subtype /Highlight /F 4 /Rect [28 84 170 116] " +
                    "/QuadPoints [28 116 170 116 28 84 170 84] /C [1 1 0] " +
                    $"/CA {ca} >>";
        return Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {PageSize} {PageSize}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [4 0 R] /Contents 5 0 R " +
                "/Resources << /Font << /F1 6 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n{annot}\nendobj\n",
            $"5 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n",
            "6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
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

    /// <summary>Mean distance from white over the annotation's interior.</summary>
    private static double MeanDarkness(SKBitmap bmp) => MeanDarkness(bmp, 60, 140);

    /// <summary>Mean distance from white over the border band only.</summary>
    private static double BorderDarkness(SKBitmap bmp) => MeanDarkness(bmp, 40, 46);

    private static double MeanDarkness(SKBitmap bmp, int fromPt, int toPt)
    {
        float s = Dpi / 72f;
        int a = (int)(fromPt * s), b = (int)(toPt * s);
        double total = 0; int n = 0;
        for (int y = a; y < b && y < bmp.Height; y++)
            for (int x = a; x < b && x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                total += 765 - (c.Red + c.Green + c.Blue);
                n++;
            }
        return n == 0 ? 0 : total / n;
    }

    private static int DarkInkPixels(SKBitmap bmp)
    {
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 110 && c.Green < 110 && c.Blue < 110) n++;
            }
        return n;
    }

    private string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-ca-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        _temp.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _temp) { try { File.Delete(p); } catch { } }
    }
}
