using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #970 — a NEGATIVE <c>Tf</c> size is legal and mirrors the text.
///
/// §9.3.1 permits it and §9.4.4 puts the size straight into the text rendering
/// matrix's scale, sign included, so <c>-12 Tf</c> reflects each glyph through
/// the text-space origin: the run reads rotated 180° and the pen marches
/// LEFTWARD from the <c>Td</c> point.
///
/// excise drew nothing at all. The size was handed to <c>SKFont</c> unchanged,
/// and a negative SKFont size draws no glyphs; one glyph-width computation also
/// clamped to <c>Math.Max(0f, …)</c>, so even the advance collapsed. On
/// pdfium's <c>text_form_negative_fontsize.pdf</c> the page came out with none
/// of its own text — only a form-field value nobody else draws — which the
/// majority-scored corpus gate (#932) read as 22 tiles missing and 28 invented.
///
/// This is excise-side arithmetic, not a Skia rasterisation difference: Skia
/// was handed a font size no rasteriser can draw. The fix keeps the SKFont size
/// positive and puts the sign in the text rendering matrix, where the spec puts
/// it.
///
/// MEASURED, 300x300 page, <c>BT /F1 -12 Tf 250 150 Td (…) Tj ET</c>, 72 dpi —
/// the corpus fixture, whose only other content is the form field:
///
///   renderer     inked px   bbox
///   mutool            702   (66,147)-(249,158)
///   pdftocairo        713   (66,147)-(249,188)   (+ a field caret)
///   pdftoppm          732   (66,147)-(249,188)   (+ a field caret)
///   ghostscript       461   (67,147)-(249,158)
///   excise BEFORE       0   — (page text absent entirely)
///   excise AFTER     1227   (66,147)-(249,187)
///
/// excise's text now starts at x=249 and runs left to x=66, on the same raster
/// rows as mutool's and Ghostscript's — the same glyph run in the same place.
/// The remaining excess is the <c>/V (Mountain Lion)</c> widget value, which
/// excise synthesizes (#889) and the oracles here do not; that is a separate
/// form-synthesis policy question and does not affect this page's gate status.
/// </summary>
public class NegativeFontSizeTests : IDisposable
{
    private const int Dpi = 72;
    private const int PageSize = 300;

    /// <summary>Where the text is placed: `250 150 Td`.</summary>
    private const int PenX = 250;

    private readonly List<string> _temp = new();

    /// <summary>
    /// The property, stated so it cannot be satisfied by drawing the text the
    /// ordinary way: a mirrored run occupies the space to the LEFT of its pen.
    /// </summary>
    [Fact]
    public void NegativeFontSize_DrawsTheTextMirroredLeftOfThePen()
    {
        var path = WriteTemp(TextPdf(-12));
        using var bmp = RenderWithExcise(path);

        var bbox = InkBounds(bmp);
        bbox.Should().NotBeNull("a negative Tf size is legal text, not text to drop");

        bbox!.Value.Right.Should().BeLessThanOrEqualTo(PenX + 2,
            "the glyphs are mirrored through the text-space origin, so the run " +
            "extends left of the Td point rather than right of it");
        bbox.Value.Left.Should().BeLessThan(PenX - 40,
            "and it is the whole string, not one stray glyph");
    }

    /// <summary>
    /// The control. Same file, same pen, positive size — the run must go the
    /// OTHER way. Without this, "draws left of the pen" could be satisfied by
    /// any regression that shifted all text leftward.
    /// </summary>
    [Fact]
    public void PositiveFontSize_StillDrawsRightOfThePen()
    {
        var path = WriteTemp(TextPdf(12));
        using var bmp = RenderWithExcise(path);

        var bbox = InkBounds(bmp);
        bbox.Should().NotBeNull();
        bbox!.Value.Left.Should().BeGreaterThanOrEqualTo(PenX - 2);
    }

    /// <summary>
    /// A mirrored run is reflected through the pen in BOTH axes, so its glyph
    /// bodies fall on the opposite side of the baseline from an upright run's.
    /// Checking that separates a genuine reflection from a bare horizontal
    /// flip, which the two tests above cannot tell apart.
    /// </summary>
    [Fact]
    public void NegativeFontSize_PutsTheGlyphBodiesOnTheFarSideOfTheBaseline()
    {
        using var upright = RenderWithExcise(WriteTemp(TextPdf(12)));
        using var mirrored = RenderWithExcise(WriteTemp(TextPdf(-12)));

        // Baseline device row: page height minus the Td y. Compared as ink MASS
        // either side of it, not as a bbox edge — a mirrored run's descenders
        // land above the baseline (that is what mirroring does to them), so the
        // extreme edges overlap by a couple of pixels in both directions and an
        // edge test reads as a 1px failure while the reflection is plainly
        // there. mutool's own bbox on the corpus fixture, (66,147)-(249,158)
        // about a baseline at row 150, has the same overlap.
        const int baselineRow = PageSize - 150;
        InkMassBelow(upright, baselineRow).Should().BeLessThan(0.25,
            "an upright run's glyph bodies are above its baseline");
        InkMassBelow(mirrored, baselineRow).Should().BeGreaterThan(0.75,
            "a mirrored one's are below it — the vertical half of the reflection, " +
            "which a purely horizontal flip would not produce");
    }

    /// <summary>
    /// The no-self-oracle half: excise's mirrored run must land where two
    /// independent engines put theirs. Compared as a bbox rather than a pixel
    /// count — the glyph outlines come from whatever typeface each renderer
    /// substituted for Helvetica and will never match exactly, but WHERE the
    /// run sits is the thing this issue is about.
    /// </summary>
    [Fact]
    public void NegativeFontSize_LandsWhereIndependentRenderersPutIt()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        var path = WriteTemp(TextPdf(-12));

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        using var cairo = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
        var mutoolBox = InkBounds(mutool!);
        var cairoBox = InkBounds(cairo!);

        mutoolBox.Should().NotBeNull("mutool draws the mirrored run");
        cairoBox.Should().NotBeNull("so does Poppler — that agreement is the premise");

        using var excise = RenderWithExcise(path);
        var box = InkBounds(excise);
        box.Should().NotBeNull();

        // Horizontal extent is set by the /Widths of Helvetica, which all three
        // read from the same file, so this is tight. Vertical is looser: cap
        // height and descent come from each renderer's substituted typeface.
        // The pen-side edge is position, while the far edge is dependent on
        // the platform substitute for the unembedded Helvetica outline.  On
        // Linux and macOS that substitution has materially different widths.
        // Keep the independent-oracle assertion on the invariant edge and let
        // the two property tests above cover direction and reflection.
        box!.Value.Right.Should().BeCloseTo(mutoolBox!.Value.Right, 6);
        box.Value.Top.Should().BeCloseTo(mutoolBox.Value.Top, 8);
    }

    // ── fixture ──────────────────────────────────────────────────────────────

    /// <summary>
    /// pdfium's text_form_negative_fontsize.pdf reduced to the part this is
    /// about: one Tj at a known pen position, at the given Tf size. The
    /// form field is deliberately left out — excise synthesizes its /V and the
    /// oracles do not, which is #889's territory, not this issue's.
    /// </summary>
    private static byte[] TextPdf(float size)
    {
        var content = "BT\n0 0 0 rg\n/F1 " + size.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " Tf\n" + PenX + " 150 Td\n(Test Form with Negative Font Size) Tj\nET";
        return Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {PageSize} {PageSize}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 5 0 R "
                + "/Resources << /Font << /F1 4 0 R >> >> >>\nendobj\n",
            "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            $"5 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
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

    /// <summary>Fraction of the page's inked pixels at or below a raster row.</summary>
    private static double InkMassBelow(SKBitmap bmp, int row)
    {
        int total = 0, below = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 240 || c.Green < 240 || c.Blue < 240)
                {
                    total++;
                    if (y >= row) below++;
                }
            }
        return total == 0 ? 0 : (double)below / total;
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
        return maxX < 0 ? null : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }

    private string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-970-{Guid.NewGuid():N}.pdf");
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
