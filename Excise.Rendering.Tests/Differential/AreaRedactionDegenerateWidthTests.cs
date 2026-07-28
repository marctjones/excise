using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #842 — area redaction must not be UNDER-inclusive when the producer uses the
/// unit-Tf idiom that #833 mis-measured.
///
/// Area redaction decides glyph removal by intersecting the user-drawn box
/// against each glyph's <c>GlyphRectangle</c>. Before #833, the ubiquitous
/// "<c>1 Tf … s 0 0 s Tm</c>" idiom (unit font size carried by the text matrix)
/// produced glyph boxes ~0.3×1pt pinned at the baseline corner, so a box drawn
/// over the *visible* ink — anywhere above the baseline — failed to intersect
/// and the glyph was left behind. Text-based <c>RedactText</c> hid this because
/// the target and glyph boxes were equally degenerate and cancelled out; the
/// AREA path had no such self-cancellation, and no test exercised it on a
/// degenerate-width font.
///
/// This is the guard the #833 fix lands behind. The redaction area here covers
/// only the TOP HALF of the glyph ink, entirely ABOVE the baseline. A correct
/// (post-#833) glyph box spans the full cap height and intersects it; a
/// regressed baseline-pinned box does not, the glyph survives, and an
/// INDEPENDENT extractor (mutool) plus an INDEPENDENT renderer (ghostscript)
/// both report the leak — per the no-self-oracle rule.
/// </summary>
public class AreaRedactionDegenerateWidthTests : IDisposable
{
    private const string Secret = "SECRET";
    private const string Keep = "PUBLIC";

    // Fixture geometry, chosen so the assertions are independent of excise's own
    // glyph boxes. Helvetica caps sit on the baseline and rise to the cap height
    // (~0.718 em). At a 24pt effective size the SECRET ink spans y≈700..717.
    private const double Baseline = 700.0;
    private const double EffSize = 24.0;
    private const double CapTop = Baseline + EffSize * 0.718; // ≈ 717.2

    private readonly List<string> _temp = new();

    [Fact]
    public void TopHalfArea_OverUnitTfGlyphs_RemovesThemForAnIndependentExtractor()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var source = UnitTfPdf();

        // Oracle sanity: an independent extractor reads the secret before we act.
        var beforePath = WriteTemp(source);
        MutoolTextExtractor.ExtractPage(beforePath, 1).Should()
            .Contain(Secret, "fixture sanity — the secret must be present before redaction");

        var pdf = PdfDocument.Open(source);
        var page = pdf.GetPage(1);

        // #833 sanity, asserted directly: the unit-Tf glyphs must now measure a
        // real cap height, not the ~1pt baseline sliver that made this leak.
        var secretGlyphs = page.Letters
            .Where(l => l.GlyphRectangle.Bottom < 750 && l.GlyphRectangle.Bottom > 650)
            .ToList();
        secretGlyphs.Should().NotBeEmpty();
        secretGlyphs.Max(l => l.GlyphRectangle.Height).Should().BeGreaterThan(8.0,
            "unit-Tf glyph boxes must carry the matrix scale (#833); a ~1pt baseline box is the " +
            "exact degenerate state in which a top-half area misses the glyph");

        // The area covers ONLY the upper portion of the ink — its bottom edge sits
        // well above the baseline, so a baseline-pinned degenerate box could not
        // intersect it. A correct full-height box does.
        double areaBottom = Baseline + EffSize * 0.30; // ≈ 707.2, safely above baseline
        var area = new PdfRectangle(60, areaBottom, 200, CapTop + 3);
        area.Bottom.Should().BeGreaterThan(Baseline + 4,
            "the guard only means something if the area's bottom is clear of the baseline corner");

        page.RedactArea(area, GlyphRemovalStrategy.AnyOverlap);
        var afterPath = WriteTemp(pdf.SaveToBytes());

        var extracted = MutoolTextExtractor.ExtractPage(afterPath, 1);
        extracted.Should().NotBeNull("mutool must read the redacted file at all");
        extracted!.Should().NotContain(Secret,
            "a top-half area over unit-Tf glyphs must remove them; if mutool still reads the word " +
            "the glyph box was too short to intersect the area — the #833 under-inclusion leak");
        extracted.Should().Contain(Keep,
            "only the targeted line may be removed — the untouched line must survive");
    }

    [Fact]
    public void TopHalfArea_OverUnitTfGlyphs_RemovesTheInk_NotJustCoversIt()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        var pdf = PdfDocument.Open(UnitTfPdf());
        var page = pdf.GetPage(1);

        var secretBox = new PdfRectangle(60, Baseline - 2, 200, CapTop + 3);
        var keepBox = new PdfRectangle(60, Baseline - 102, 200, CapTop - 97);

        var beforePath = WriteTemp(pdf.SaveToBytes());
        using var before = GhostscriptReferenceRenderer.RenderPage(beforePath, 1, dpi: 150);
        before.Should().NotBeNull();
        InkFractionIn(before!, secretBox, page.Height).Should().BeGreaterThan(0.02,
            "fixture sanity — the secret must be inked before redaction");

        double areaBottom = Baseline + EffSize * 0.30;
        page.RedactArea(new PdfRectangle(60, areaBottom, 200, CapTop + 3), GlyphRemovalStrategy.AnyOverlap);
        var afterPath = WriteTemp(pdf.SaveToBytes());

        using var after = GhostscriptReferenceRenderer.RenderPage(afterPath, 1, dpi: 150);
        after.Should().NotBeNull();

        InkFractionIn(after!, secretBox, page.Height).Should().BeLessThan(0.001,
            "an independent renderer still draws ink where the top-half area was applied — the " +
            "glyphs were not removed, only (at best) partially covered");
        InkFractionIn(after!, keepBox, page.Height).Should().BeGreaterThan(0.02,
            "the untargeted line must still be inked — a blanked page would satisfy the removal check");
    }

    /// <summary>
    /// A page that shows two words with the "<c>1 Tf</c> + scaling <c>Tm</c>"
    /// idiom — unit font size, real size carried by the text matrix — the exact
    /// producer shape #833 mis-measured.
    /// </summary>
    private static byte[] UnitTfPdf()
    {
        var content =
            $"BT /F1 1 Tf {EffSize:0} 0 0 {EffSize:0} 72 {Baseline:0} Tm ({Secret}) Tj ET " +
            $"BT /F1 1 Tf {EffSize:0} 0 0 {EffSize:0} 72 {Baseline - 100:0} Tm ({Keep}) Tj ET";

        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string s) { offsets.Add(sb.Length); sb.Append(s); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Fraction of non-white pixels inside <paramref name="box"/> (PDF content
    /// coordinates, bottom-left origin) of a rendered page.
    /// </summary>
    private static double InkFractionIn(SKBitmap bmp, PdfRectangle box, double pageHeight)
    {
        const double scale = 150.0 / 72.0;
        int x0 = Math.Max(0, (int)(box.Left * scale));
        int x1 = Math.Min(bmp.Width - 1, (int)(box.Right * scale));
        int y0 = Math.Max(0, (int)((pageHeight - box.Top) * scale));
        int y1 = Math.Min(bmp.Height - 1, (int)((pageHeight - box.Bottom) * scale));
        if (x1 <= x0 || y1 <= y0) return 0;

        int ink = 0, total = 0;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var p = bmp.GetPixel(x, y);
            total++;
            if (p.Red < 200 || p.Green < 200 || p.Blue < 200) ink++;
        }
        return total == 0 ? 0 : (double)ink / total;
    }

    private string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-area-degen-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        _temp.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var p in _temp)
        {
            try { File.Delete(p); } catch { /* best effort */ }
        }
    }
}
