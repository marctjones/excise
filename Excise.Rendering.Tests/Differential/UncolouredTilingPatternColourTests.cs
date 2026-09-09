using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1373 — ISO 32000-2 §8.7.3.3: an UNCOLOURED (PaintType 2) tiling pattern's
/// content stream "shall not explicitly specify any colours". The colour comes
/// from the operands of the <c>scn</c> that selected the pattern, interpreted
/// in the Pattern colour space's underlying space.
///
/// <para><b>The defect.</b> Real producers violate that "shall not" anyway, and
/// excise honoured what they emitted — so the cell painted its own colour
/// instead of the tint. On <c>veraPDF test suite 6-2-4-3-t02-pass-d.pdf</c> the
/// cell sets <c>/DeviceCMYK cs 0.6875 0.765625 0.8671875 0 sc</c> while the page
/// selects the pattern with <c>1 0 0 0 scn</c> (100% cyan through the page's own
/// <c>/DefaultCMYK</c> ICC profile). Measured at 150 dpi, dominant non-white
/// colour of page 1:</para>
///
/// <code>
/// renderer      before #1373    after #1373   the page's own ICC profile says
/// ghostscript   (0,158,224)     (0,158,224)   (0,158,224)  &lt;- LittleCMS on obj 12
/// mutool        (0,157,224)     (0,157,224)
/// excise        (107, 77, 55)   (0,157,224)
/// pdftocairo    (109, 78, 55)   (109, 78, 55) &lt;- shares the defect
/// </code>
///
/// <para><b>The issue was filed on the wrong mechanism, and that matters for
/// whoever reads it next.</b> #1373 concluded excise ignores
/// <c>/DefaultCMYK</c> (§8.6.5.6). It does not: the substitution resolves, the
/// 557 KB CMYK→Lab profile parses, and evaluating it directly returns
/// (0,157,224) for <c>[1,0,0,0]</c> — the correct answer, matching LittleCMS.
/// The colour never reached that code because the cell's own <c>sc</c>
/// overwrote the tint after it had been computed. Colour management was a
/// bystander.</para>
///
/// <para><b>Why pdftocairo is excluded from the oracle set.</b> Poppler has the
/// same defect, so it "agreed" with excise's wrong answer — which is exactly
/// how the page came to be pinned <c>ACCEPTED_REFERENCE</c> against pdftocairo
/// with <c>agreeingOracles: 1</c>. A gate that accepted the single agreeing
/// renderer here would pin the bug. mutool (MuPDF) and Ghostscript are the
/// witnesses.</para>
///
/// <para><b>Scope.</b> §8.7.3.3 also says an uncoloured cell "may paint an image
/// mask but no other kind of image". That half is NOT implemented here — no
/// fixture in the corpus exercises it and no oracle comparison was made, so it
/// is left as a known gap rather than guessed at.</para>
/// </summary>
public class UncolouredTilingPatternColourTests : IDisposable
{
    private const int Dpi = 72;

    private readonly List<string> _temp = new();

    /// <summary>
    /// The always-on half: no reference tool required, so the fix keeps
    /// coverage on a machine with no renderers installed.
    ///
    /// <para>Pure blue tint via <c>scn</c>; the cell tries to paint itself pure
    /// red. Blue and red are chosen so the verdict survives any engine's
    /// antialiasing and any colour-conversion rounding — the failure mode is a
    /// whole channel swap, not a shade.</para>
    /// </summary>
    [Fact]
    public void UncolouredPatternCell_ColourOperators_DoNotOverrideTheTint()
    {
        var path = WriteTemp(UncolouredPatternFixture(cellColourOperators: "1 0 0 rg"));

        using var excise = RenderWithExcise(path);
        var (r, g, b) = DominantInk(excise);

        b.Should().BeGreaterThan(200,
            "§8.7.3.3: the cell paints in the tint supplied to scn, which is pure blue");
        r.Should().BeLessThan(60,
            "the cell's own `1 0 0 rg` is a spec violation a conforming reader ignores; "
            + "honouring it painted the cell RED, which is the #1373 defect");
        g.Should().BeLessThan(60);
    }

    /// <summary>
    /// The control. Without it, "the cell is blue" is also satisfied by a
    /// renderer that ignores the cell's colour operators for the wrong reason —
    /// e.g. by ignoring the cell content entirely and filling the clip with the
    /// tint. Here the SAME cell geometry is a PaintType 1 (coloured) pattern,
    /// where the identical <c>1 0 0 rg</c> is legal and MUST be honoured.
    /// </summary>
    [Fact]
    public void ColouredPatternCell_ColourOperators_AreStillHonoured()
    {
        var path = WriteTemp(UncolouredPatternFixture(
            cellColourOperators: "1 0 0 rg",
            paintType: 1));

        using var excise = RenderWithExcise(path);
        var (r, _, b) = DominantInk(excise);

        r.Should().BeGreaterThan(200,
            "a PaintType 1 cell carries its own colour (§8.7.3.2) — suppressing "
            + "colour operators there would be a different bug in the other direction");
        b.Should().BeLessThan(60);
    }

    /// <summary>
    /// The independent witnesses. A differential between excise's parser and
    /// excise's renderer could not see this defect — they shared it — and the
    /// one renderer that agreed with excise (poppler) is the one that has it
    /// too. mutool and Ghostscript are asked the same question on the same
    /// bytes.
    /// </summary>
    [Fact]
    public void UncolouredPatternCell_TintColour_AgreesWithMutoolAndGhostscript()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        var path = WriteTemp(UncolouredPatternFixture(cellColourOperators: "1 0 0 rg"));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var gs = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
        mutool.Should().NotBeNull("mutool renders the fixture");
        gs.Should().NotBeNull("ghostscript renders the fixture");

        var mutoolInk = DominantInk(mutool!);
        var gsInk = DominantInk(gs!);

        // The oracles' own verdict first. If MuPDF and Ghostscript ever start
        // honouring in-cell colour operators this fails HERE, instead of
        // silently becoming a gate that pins whatever excise happens to do.
        mutoolInk.B.Should().BeGreaterThan(200, "MuPDF paints the cell in the scn tint");
        mutoolInk.R.Should().BeLessThan(60, "MuPDF ignores the cell's `1 0 0 rg`");
        gsInk.B.Should().BeGreaterThan(200, "Ghostscript agrees");
        gsInk.R.Should().BeLessThan(60, "Ghostscript agrees");

        using var excise = RenderWithExcise(path);
        var ink = DominantInk(excise);

        ink.R.Should().BeCloseTo(mutoolInk.R, 24, "excise matches MuPDF's red channel");
        ink.G.Should().BeCloseTo(mutoolInk.G, 24);
        ink.B.Should().BeCloseTo(mutoolInk.B, 24);
    }

    /// <summary>
    /// A 200x200 page filled with a tiling pattern whose 20x20 cell paints a
    /// 16x16 square. The pattern is selected through <c>[/Pattern /DeviceRGB]</c>
    /// with a pure-blue tint. DeviceRGB deliberately: a DeviceCMYK tint would
    /// make the comparison depend on each engine's CMYK→RGB model rather than on
    /// the question being asked.
    /// </summary>
    private static byte[] UncolouredPatternFixture(string cellColourOperators, int paintType = 2)
    {
        // For PaintType 1 the cell must carry its own colour, and the scn that
        // selects a coloured pattern takes no tint operands.
        var pageContent = paintType == 2
            ? "/CSP cs 0 0 1 /P0 scn 0 0 200 200 re f"
            : "/CSP cs /P0 scn 0 0 200 200 re f";
        var patternContent = $"{cellColourOperators} 2 2 16 16 re f";

        return Assemble(new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Resources "
                + "<< /ColorSpace << /CSP [/Pattern /DeviceRGB] >> "
                + "/Pattern << /P0 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream\nendobj\n",
            $"5 0 obj\n<< /Type /Pattern /PatternType 1 /PaintType {paintType} /TilingType 1 "
                + $"/BBox [0 0 20 20] /XStep 20 /YStep 20 /Resources << >> "
                + $"/Length {patternContent.Length} >>\nstream\n{patternContent}\nendstream\nendobj\n",
        });
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
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static SKBitmap RenderWithExcise(string path)
    {
        using var doc = PdfDocument.Open(path);
        return new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });
    }

    /// <summary>
    /// Most frequent non-white, non-near-black pixel. Near-white is the page
    /// ground between cells; the cells themselves are the subject.
    /// </summary>
    private static (int R, int G, int B) DominantInk(SKBitmap bmp)
    {
        var counts = new Dictionary<uint, int>();
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red > 240 && c.Green > 240 && c.Blue > 240)
                    continue;
                var key = ((uint)c.Red << 16) | ((uint)c.Green << 8) | c.Blue;
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }

        counts.Should().NotBeEmpty("the pattern must actually paint something");
        var best = counts.OrderByDescending(kv => kv.Value).First().Key;
        return ((int)(best >> 16) & 0xFF, (int)(best >> 8) & 0xFF, (int)best & 0xFF);
    }

    private string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-1373-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        _temp.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _temp) { try { File.Delete(p); } catch { } }
        GC.SuppressFinalize(this);
    }
}
