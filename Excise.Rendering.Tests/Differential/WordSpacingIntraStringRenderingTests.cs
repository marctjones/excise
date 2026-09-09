using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1392 — <c>Tw</c> (word spacing) silently dropped from intra-string glyph
/// layout in <c>SkiaRenderer.Text.cs</c>, in two different ways depending on
/// whether the font dictionary carries an explicit <c>/Widths</c> array.
///
/// <para><b>Bug 1 — no <c>/Widths</c> (the common case: base-14 fonts are not
/// required to carry one, ISO 32000-2 §9.6.2.2).</b> The <c>ByteToGlyph</c>
/// fallback branch's <c>needsExplicitSpacing</c> gate required
/// <c>currentFont.Widths != null</c>, so <c>Tc</c>/<c>Tw</c> never
/// repositioned glyphs within a <c>Tj</c> string for a plain
/// <c>&lt;&lt; /Subtype /Type1 /BaseFont /Helvetica &gt;&gt;</c> font — the cursor
/// position for whatever came AFTER the string was still correct (tracked
/// separately), only the glyphs' own drawn positions were wrong. Fixed by
/// having <c>PdfFontResolver</c> populate <c>Widths</c> from
/// <c>StandardFontMetrics</c> (the same authority the shared content walk
/// already uses) when a standard-14 font has no explicit array, per
/// §9.6.2.2's fallback-to-AFM-metrics rule.</para>
///
/// <para><b>Bug 2 — <c>/Widths</c> present (the "authoritative" branch).</b>
/// <c>cursor += (w / 1000f + spacing * sizeSign) * effectiveSize;</c> scaled
/// the ENTIRE sum, including <c>Tc</c>/<c>Tw</c>, by font size. Per ISO
/// 32000-2 §9.4.3, <c>tx = (w0/1000 * Tfs + Tc + Tw) * Th</c> — Tc/Tw are
/// already in unscaled text-space units and must not be scaled by Tfs a
/// second time. At 24 pt with a large Tw this pushed glyphs far off-canvas:
/// measured, a 4-letter "A A A A" string went from 83 px wide (0 Tw, matching
/// mutool) to only 15 px (20 Tw) instead of mutool's 143 px, because the
/// later glyphs landed off the right edge instead of the string growing.
/// This file's sibling code path (<see cref="SkiaRenderer"/>'s ByteToGlyph
/// branch, "needsExplicitSpacing") already had the correct formula and a
/// comment explaining it -- this was a divergence between two per-glyph
/// cursor-advance sites in the same file, not a design gap.</para>
///
/// <para>Excise's OWN extraction/redaction sink (<c>ContentStreamWalker</c>'s
/// <c>_wordSpacing</c> handling) was not affected -- checked directly, it
/// already composes Tc/Tw correctly. This was purely a rendering-visual bug in
/// <c>SkiaRenderer.Text.cs</c>, not a redaction-security one.</para>
///
/// <para>Every assertion here is against mutool, independent of both fixed
/// code paths -- matching this project's no-self-oracle rule.</para>
/// </summary>
public class WordSpacingIntraStringRenderingTests : IDisposable
{
    private const int Dpi = 72;
    private readonly List<string> _tempFiles = new();

    [Theory]
    [InlineData(false)] // Bug 1's path: no /Widths array
    [InlineData(true)]  // Bug 2's path: explicit /Widths array
    public void Tw_WidensAFourWordString_MatchesIndependentRenderer(bool withWidthsArray)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var narrowPath = WriteTemp(Fixture(tw: 0, withWidthsArray));
        var widePath = WriteTemp(Fixture(tw: 20, withWidthsArray));

        using var mutoolNarrow = MutoolReferenceRenderer.RenderPage(narrowPath, 1, Dpi);
        using var mutoolWide = MutoolReferenceRenderer.RenderPage(widePath, 1, Dpi);
        var mutoolNarrowBox = InkBounds(mutoolNarrow!);
        var mutoolWideBox = InkBounds(mutoolWide!);

        // The oracle's own verdict first -- if mutool doesn't widen the run
        // under Tw, this fixture proves nothing about excise either way.
        mutoolNarrowBox.Should().NotBeNull();
        mutoolWideBox.Should().NotBeNull();
        (mutoolWideBox!.Value.Width - mutoolNarrowBox!.Value.Width).Should().BeGreaterThan(40,
            "guard: mutool must visibly widen the run under a large Tw, or this comparison is vacuous");

        using var exciseNarrow = RenderWithExcise(narrowPath);
        using var exciseWide = RenderWithExcise(widePath);
        var exciseNarrowBox = InkBounds(exciseNarrow);
        var exciseWideBox = InkBounds(exciseWide);

        exciseNarrowBox.Should().NotBeNull();
        exciseWideBox.Should().NotBeNull();

        // Bug 1's failure mode: Tw has no effect at all (exciseWideBox ==
        // exciseNarrowBox). Bug 2's failure mode: the run gets NARROWER
        // under Tw (later glyphs pushed off-canvas), the opposite sign of
        // what's asserted here.
        exciseWideBox!.Value.Width.Should().BeCloseTo(mutoolWideBox.Value.Width, 6,
            $"Tw=20 run width must match mutool's independent layout (withWidthsArray={withWidthsArray})");
        exciseNarrowBox!.Value.Width.Should().BeCloseTo(mutoolNarrowBox.Value.Width, 6,
            "Tw=0 control must already match (sanity: base glyph widths/metrics are right)");
    }

    private static byte[] Fixture(int tw, bool withWidthsArray)
    {
        var content = $"BT /F1 24 Tf {tw} Tw 1 0 0 1 20 100 Tm (A A A A) Tj ET";
        var contentBytes = Encoding.ASCII.GetBytes(content);
        // Helvetica AFM widths, thousandths of an em: space(32)=278, A(65)=667.
        // FirstChar 32 .. LastChar 65; everything between is unused (0) --
        // this fixture only draws 'A' and space.
        var widths = new int[65 - 32 + 1];
        widths[0] = 278;
        widths[^1] = 667;
        var widthsEntry = withWidthsArray
            ? $" /FirstChar 32 /LastChar 65 /Widths [{string.Join(" ", widths)}]"
            : "";

        var objects = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 400 150] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 5 0 R /Resources << /Font << /F1 4 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica{widthsEntry} >>\nendobj\n",
            $"5 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n{content}\nendstream\nendobj\n",
        };
        return Assemble(objects);
    }

    private static byte[] Assemble(List<string> objects)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-tw-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        _tempFiles.Add(path);
        return path;
    }

    private static SKBitmap RenderWithExcise(string path)
    {
        using var doc = PdfDocument.Open(path);
        return new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });
    }

    private static SKRectI? InkBounds(SKBitmap bmp)
    {
        int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 240 || c.Green < 240 || c.Blue < 240)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        return maxX >= minX ? SKRectI.Create(minX, minY, maxX - minX + 1, maxY - minY + 1) : null;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { /* best effort */ }
    }
}
