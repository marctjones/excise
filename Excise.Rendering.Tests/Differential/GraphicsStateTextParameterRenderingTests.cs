using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #986 — the renderer's <c>q</c>/<c>Q</c> and the §8.4.1 Table 52 text
/// parameters, witnessed by renderers that are not excise.
///
/// <para><b>Why an oracle and not a property test.</b> This is the same defect
/// #983 fixed in the two content parsers, in the third state machine. A
/// differential between excise's parsers and excise's renderer could not see
/// it — they shared it. So every assertion below is against mutool (mupdf) and
/// pdftocairo (poppler), two independent engines, on the same bytes.</para>
///
/// <para><b>MEASURED 2026-08-16, 72 dpi, ink bbox as (left, top, right,
/// bottom) — before and after the fix:</b></para>
///
/// <code>
/// fixture / band              excise BEFORE       excise AFTER      mutool            pdftocairo        ghostscript
/// A post-Q run "After"        (20,153)-(96,181)   (20,171)-(46,181) (20,171)-(46,181) (20,171)-(45,181) (20,171)-(45,180)
/// B q/Q inside BT             (20, 31)-(119,41)   (20, 31)-(119,41) (20, 31)-(118,41) (20, 31)-(118,41) (20, 31)-(86, 40)
/// C post-Q run, other font    (20,186)-(78,200)   (21,182)-(99,200) (21,182)-(99,200) (21,182)-(99,200) (22,183)-(98,200)
/// D post-Do run, form's font  (20,186)-(78,200)   (21,182)-(99,200) (21,182)-(99,200) (21,182)-(99,200) (22,183)-(98,200)
/// </code>
///
/// <para>A is the leak the issue describes: 36 pt and <c>2 Tc</c> set inside
/// <c>q</c>/<c>Q</c> still applied afterwards — a run drawn 76 px wide and
/// 28 px tall where every oracle draws it 26 × 10.</para>
///
/// <para>B is the CONTROL, and it is the reason the fix is a snapshot of the
/// Table 52 parameters rather than of the renderer's whole <c>TextState</c>
/// object. The text matrix is §9.4.1 text-OBJECT state; <c>Q</c> must not
/// rewind the pen. Restoring the whole object would have made the third run
/// overprint the second, and fixture A cannot see that because its q/Q brackets
/// whole <c>BT</c>/<c>ET</c> blocks. Its excise column is unchanged by this
/// fix — the point is that it STAYS unchanged.</para>
///
/// <para><b>The oracles split 2-1 on B, and the row is kept rather than
/// dropped.</b> §8.2 does not permit <c>q</c>/<c>Q</c> inside a text object, so
/// there is no conforming answer: mupdf and poppler leave the pen where the
/// <c>q</c> found it (right edge 118), Ghostscript rewinds it and overprints
/// (86). excise draws 119 — the majority reading, and the same one it drew
/// before #986. Recorded as a measured disagreement between reference
/// implementations, NOT as "everyone agrees"; a fix that adopted Ghostscript's
/// reading would be a deliberate change of majority, not a bug fix.</para>
///
/// <para>C and D are the resolved FONT, which Table 52's <c>Tf</c> parameter
/// carries along with the name and size. C is <c>q</c>/<c>Q</c>; D is the
/// implicit bracket §8.10.1 puts around a form XObject's execution, where the
/// renderer restored the font NAME and not the resolved font — so a post-<c>Do</c>
/// run reported Helvetica@24 while drawing Courier glyphs out of Courier's
/// widths. Both fixtures show the same 58 px vs 78 px "MMMM", which is Courier
/// where the oracles all draw Helvetica.</para>
///
/// <para><b>What this cannot catch.</b> Ink counts are not compared: each
/// engine substitutes its own typeface for non-embedded Helvetica/Courier, so
/// only WHERE the run lands is comparable (the same reasoning as
/// <see cref="NegativeFontSizeTests"/>). <c>Tr</c> (render mode) is restored by
/// the fix but is not oracled here — no fixture below sets it.</para>
/// </summary>
public class GraphicsStateTextParameterRenderingTests : IDisposable
{
    private const int Dpi = 72;

    private readonly List<string> _temp = new();

    /// <summary>
    /// #983's fixture, rendered: a 36 pt / <c>2 Tc</c> run bracketed in
    /// <c>q</c>/<c>Q</c>, then a run that sets no text parameter of its own and
    /// therefore draws with whatever <c>Q</c> restored.
    /// </summary>
    private const string PostQContent =
        "BT /F1 12 Tf 1 0 0 1 20 200 Tm (Base) Tj ET\n"
      + "q\n"
      + "BT /F1 36 Tf 2 Tc 1 0 0 1 20 140 Tm (Big) Tj ET\n"
      + "Q\n"
      + "BT 1 0 0 1 20 60 Tm (After) Tj ET";

    /// <summary>Device rows below the bracketed 36 pt run's descenders (~108).</summary>
    private const int PostQBandTop = 120;

    [Fact]
    public void PostQ_TextParameters_MatchIndependentRenderers()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        var path = WriteTemp(Fixture(PostQContent, 300, 240, ("F1", "Helvetica")));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var cairo = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
        var mutoolBox = InkBounds(mutool!, PostQBandTop);
        var cairoBox = InkBounds(cairo!, PostQBandTop);

        // The oracles' own verdict first. If mupdf and poppler ever stopped
        // restoring the text state at Q this fails HERE, rather than quietly
        // becoming a gate that pins the defect.
        mutoolBox.Should().NotBeNull("mutool draws the post-Q run");
        cairoBox.Should().NotBeNull("so does poppler");
        mutoolBox!.Value.Height.Should().BeLessThan(16,
            "12 pt of restored font size, not the bracketed 36 pt");
        cairoBox!.Value.Height.Should().BeLessThan(16, "poppler agrees");
        mutoolBox.Value.Width.Should().BeCloseTo(cairoBox.Value.Width, 2,
            "two independent engines agree on the run's extent — that agreement "
            + "is the premise of the comparison below");

        using var excise = RenderWithExcise(path);
        var box = InkBounds(excise, PostQBandTop);

        box.Should().NotBeNull();
        box!.Value.Left.Should().BeCloseTo(mutoolBox.Value.Left, 2);
        box.Value.Right.Should().BeCloseTo(mutoolBox.Value.Right, 3,
            "the width carries the restored font size AND the restored Tc — "
            + "excise drew this 76 px wide against mutool's 26 before #986");
        box.Value.Top.Should().BeCloseTo(mutoolBox.Value.Top, 3,
            "and the cap height carries the restored size");
    }

    /// <summary>
    /// The control for the fix's shape. §8.2 does not permit <c>q</c>/<c>Q</c>
    /// inside a text object, but producers emit it, and the text matrix is
    /// §9.4.1 text-object state that <c>Q</c> must leave alone. If the fix had
    /// snapshotted the renderer's whole <c>TextState</c> — matrices included —
    /// the third run here would overprint the second.
    ///
    /// <para>Pinned against mupdf and poppler, which agree; Ghostscript is the
    /// dissenting third and is deliberately NOT asserted against. See the class
    /// docstring for the numbers.</para>
    /// </summary>
    [Fact]
    public void QQ_InsideATextObject_DoesNotRewindThePen()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        // The pen MUST advance between the q and the Q, or the snapshot and the
        // live matrix are equal and restoring the matrix is undetectable. The
        // first draft of this test was `(AAAA) Tj q Q (BBBB) Tj` and passed
        // under a deliberately matrix-restoring mutation for exactly that
        // reason.
        var path = WriteTemp(Fixture(
            "BT /F1 12 Tf 1 0 0 1 20 100 Tm (AAAA) Tj q (BBBB) Tj Q (CCCC) Tj ET",
            300, 140, ("F1", "Helvetica")));
        var halfPath = WriteTemp(Fixture(
            "BT /F1 12 Tf 1 0 0 1 20 100 Tm (AAAA) Tj ET",
            300, 140, ("F1", "Helvetica")));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var cairo = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
        using var exciseHalf = RenderWithExcise(halfPath);
        using var excise = RenderWithExcise(path);

        var mutoolBox = InkBounds(mutool!)!.Value;
        var cairoBox = InkBounds(cairo!)!.Value;
        var halfBox = InkBounds(exciseHalf)!.Value;
        var box = InkBounds(excise)!.Value;

        mutoolBox.Right.Should().BeCloseTo(cairoBox.Right, 2,
            "mupdf and poppler both leave the pen where the q found it");
        mutoolBox.Width.Should().BeGreaterThan((int)(halfBox.Width * 2.6),
            "the oracle's run really is all THREE strings side by side — without "
            + "this the assertion below could be satisfied by drawing nothing");

        box.Right.Should().BeCloseTo(mutoolBox.Right, 3,
            "Q restores the Table 52 parameters, NOT the §9.4.1 text matrix");
    }

    /// <summary>
    /// Table 52's <c>Tf</c> parameter is the FONT, not just its name and size.
    /// Courier's "MMMM" is 58 px where Helvetica's is 78, so the post-<c>Q</c>
    /// run's width says which typeface's widths were in force.
    /// </summary>
    [Fact]
    public void PostQ_ResolvedFont_MatchesIndependentRenderers()
        => AssertLastRunIsHelvetica(WriteTemp(Fixture(
            "BT /F1 24 Tf 1 0 0 1 20 180 Tm (MMMM) Tj ET\n"
          + "q\n"
          + "BT /F2 24 Tf 1 0 0 1 20 110 Tm (MMMM) Tj ET\n"
          + "Q\n"
          + "BT 1 0 0 1 20 40 Tm (MMMM) Tj ET",
            300, 240, ("F1", "Helvetica"), ("F2", "Courier"))));

    /// <summary>
    /// The same parameter at the other implicit bracket: §8.10.1 saves and
    /// restores the graphics state around a form XObject's execution, so the
    /// font a form selects must not survive its <c>Do</c>. No <c>q</c>/<c>Q</c>
    /// appears in this fixture at all.
    /// </summary>
    [Fact]
    public void FormXObject_Font_DoesNotSurviveTheDo()
        => AssertLastRunIsHelvetica(WriteTemp(FormFixture()));

    private void AssertLastRunIsHelvetica(string path)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        // Device rows of the third run (baseline y=40 -> row 200) and of the
        // first (baseline y=180 -> row 60). Both draw the same string with the
        // same Tf size; only the font in force can differ.
        const int lastTop = 170, firstBottom = 80;

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var cairo = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
        var mutoolLast = InkBounds(mutool!, lastTop)!.Value;
        var cairoLast = InkBounds(cairo!, lastTop)!.Value;
        var mutoolFirst = InkBounds(mutool!, 0, firstBottom)!.Value;

        mutoolLast.Width.Should().BeCloseTo(mutoolFirst.Width, 2,
            "mutool draws the last run in the SAME font as the first — the "
            + "bracketed font did not escape its bracket");
        cairoLast.Width.Should().BeCloseTo(mutoolLast.Width, 2, "poppler agrees");

        using var excise = RenderWithExcise(path);
        var box = InkBounds(excise, lastTop)!.Value;

        box.Left.Should().BeCloseTo(mutoolLast.Left, 2);
        box.Right.Should().BeCloseTo(mutoolLast.Right, 3,
            "excise drew this run 58 px wide — Courier's widths, out of Courier's "
            + "typeface — where all three oracles draw it 78 px wide (#986)");
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static byte[] Fixture(string content, int w, int h, params (string Name, string Base)[] fonts)
    {
        var resources = string.Join(" ", fonts.Select((f, i) => $"/{f.Name} {4 + i} 0 R"));
        var objects = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {w} {h}] >>\nendobj\n",
            $"3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents {4 + fonts.Length} 0 R "
                + $"/Resources << /Font << {resources} >> >> >>\nendobj\n",
        };
        foreach (var (_, baseFont) in fonts)
            objects.Add($"{4 + objects.Count - 3} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /{baseFont} >>\nendobj\n");
        objects.Add($"{4 + fonts.Length} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        return Assemble(objects);
    }

    /// <summary>
    /// Fixture D: Helvetica on the page, Courier inside a form XObject, and a
    /// final unstyled run after the <c>Do</c>.
    /// </summary>
    private static byte[] FormFixture()
    {
        const string page = "BT /F1 24 Tf 1 0 0 1 20 180 Tm (MMMM) Tj ET\n"
                          + "/Fm1 Do\n"
                          + "BT 1 0 0 1 20 40 Tm (MMMM) Tj ET";
        const string form = "BT /F2 24 Tf 1 0 0 1 20 110 Tm (MMMM) Tj ET";
        return Assemble(new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 240] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 6 0 R /Resources "
                + "<< /Font << /F1 4 0 R >> /XObject << /Fm1 7 0 R >> >> >>\nendobj\n",
            "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>\nendobj\n",
            $"6 0 obj\n<< /Length {page.Length} >>\nstream\n{page}\nendstream\nendobj\n",
            "7 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 300 240] "
                + $"/Resources << /Font << /F2 5 0 R >> >> /Length {form.Length} >>\n"
                + $"stream\n{form}\nendstream\nendobj\n",
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

    /// <summary>Ink bbox over the raster rows in [<paramref name="top"/>, <paramref name="bottom"/>).</summary>
    private static SKRectI? InkBounds(SKBitmap bmp, int top = 0, int bottom = int.MaxValue)
    {
        int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
        for (int y = Math.Max(0, top); y < Math.Min(bmp.Height, bottom); y++)
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
        return maxX < 0 ? null : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }

    private string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-986-{Guid.NewGuid():N}.pdf");
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
