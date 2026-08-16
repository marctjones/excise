using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #972 — what a <c>/FT /Btn</c> widget with no <c>/AP</c> is drawn as.
///
/// §12.5.5 lets a reader synthesize an appearance, so renderers MAY differ —
/// that permission is the whole basis of #932's majority scoring. Here they do
/// not differ, and excise was the outlier: it drew two empty blue boxes on
/// pdf.js's checkbox_no_appearance.pdf where mutool, pdftocairo and pdftoppm
/// all draw a black check mark and no box at all.
///
/// RE-MEASURED PER CASE, 72 dpi, one synthesized widget at /Rect
/// [50 50 100 100] on a 200pt page, no /AP anywhere. Inked px, and the bbox —
/// the bbox is in this table on purpose, because its absence is what let the
/// previous round read a check mark as a box (see the class comment on
/// SynthesizedAnnotationAppearanceTests). Ghostscript draws nothing in any of
/// these cases and is omitted:
///
///   case                        mutool                 pdftocairo             pdftoppm
///   /AS on  (V=Yes AS=Yes)   322  (58,105)-(91,140)  320  (58,110)-(91,145)  320
///   /AS off (V=Off AS=Off)     0  -                    0  -                    0
///   /V on, NO /AS              0  -                    0  -                    0
///   pushbutton (Ff bit 17)     0  -                    0  -                    0
///   radio on   (Ff bit 16)   468  (63,113)-(86,136)    0  -                    0
///
/// The widget's device rect is (50,100)-(100,150), so every oracle's mark sits
/// well inside it at roughly two thirds of the box — a glyph, not a border.
///
/// What excise now does, and the majority backing each part:
///   * ON checkbox  -> a check mark, no box                     3 of 3
///   * OFF checkbox -> nothing at all                           3 of 3
///   * state read from /AS ALONE, never from /V                 3 of 3
///   * pushbutton   -> nothing                                  3 of 3
///   * radio        -> NOT IMPLEMENTED (1 of 3 draws a dot)     see below
///
/// Radio is left alone deliberately. One renderer of three draws anything, so
/// implementing it means electing an outlier with nothing to break the tie —
/// the #875 trap that #889 exists to avoid. Filed separately rather than
/// guessed at.
/// </summary>
public class CheckboxSynthesisTests : IDisposable
{
    private const int Dpi = 72;
    private const int PageSize = 200;

    /// <summary>Device-space window over /Rect [50 50 100 100] (PDF Y is up).</summary>
    private static SKRectI WidgetRect => new(50, 100, 100, 150);

    private readonly List<string> _temp = new();

    // ── what excise draws, with no tool installed ────────────────────────────

    [Fact]
    public void CheckboxWithAsOn_DrawsAMarkInsideTheBoxAndNoBorder()
    {
        var path = WriteTemp(ButtonPdf("/V /Yes /AS /Yes"));
        using var bmp = RenderWithExcise(path);

        var bbox = InkBounds(bmp);
        bbox.Should().NotBeNull("an ON checkbox is not blank");

        // A GLYPH, not a border: the mark is inset from the widget rect on all
        // four sides. The old blue box failed exactly this — its bbox WAS the
        // widget rect.
        bbox!.Value.Left.Should().BeGreaterThan(WidgetRect.Left);
        bbox.Value.Top.Should().BeGreaterThan(WidgetRect.Top);
        bbox.Value.Right.Should().BeLessThan(WidgetRect.Right);
        bbox.Value.Bottom.Should().BeLessThan(WidgetRect.Bottom);
    }

    /// <summary>
    /// Orientation, which an ink COUNT cannot see: the first version of this
    /// fix built the polyline in screen sense while the annotation rect is in
    /// PDF space (Y up), and drew a caret. Pixel count and bbox were both
    /// unchanged — only the picture was wrong.
    ///
    /// A tick's corner is its LOWEST point and sits left of centre; a caret's
    /// corner is its highest. Asking where the mark's lowest row is discovers
    /// the difference without needing a reference image.
    /// </summary>
    [Fact]
    public void TheMark_IsATickAndNotACaret()
    {
        var path = WriteTemp(ButtonPdf("/V /Yes /AS /Yes"));
        using var bmp = RenderWithExcise(path);

        var bbox = InkBounds(bmp)!.Value;
        int lowestRow = bbox.Bottom - 1;
        int cornerX = RowInkCentroidX(bmp, lowestRow);

        // Tick: bottom vertex about a third across. Caret: bottom row is the
        // two OUTER ends, whose centroid lands near the middle instead.
        var span = bbox.Right - bbox.Left;
        cornerX.Should().BeLessThan(bbox.Left + (int)(span * 0.45),
            "the low point of a tick is its corner, left of centre — a caret's " +
            "lowest row is its two outer ends and centres instead");
    }

    [Theory]
    [InlineData("/V /Off /AS /Off", "an OFF checkbox draws nothing at all — no box either")]
    [InlineData("/V /Yes", "no /AS means no state; guessing one put a tick in every box of a blank W-9")]
    [InlineData("/Ff 65536", "a pushbutton with no /AP draws nothing in any oracle")]
    [InlineData("/Ff 32768 /V /A /AS /A", "radio is 1-of-3 and deliberately not implemented")]
    public void ButtonsTheOraclesLeaveBlank_AreBlank(string extras, string because)
    {
        var path = WriteTemp(ButtonPdf(extras));
        using var bmp = RenderWithExcise(path);

        InkFraction(bmp, WidgetRect).Should().BeLessThan(0.001, because);
    }

    /// <summary>
    /// /MK styling is untouched by #972 and still draws chrome. mutool honours
    /// /MK here (535 px over the full widget rect for an ON box); Poppler
    /// ignores it and draws only the mark. Keeping the pre-existing behaviour
    /// is a scope decision, not a measurement: this test exists so removing the
    /// unconditional button border did not silently take /MK with it.
    /// </summary>
    [Fact]
    public void CheckboxWithMk_StillDrawsItsChrome()
    {
        var path = WriteTemp(ButtonPdf("/V /Off /AS /Off /MK << /BC [0 0 1] /BG [1 1 0] >>"));
        using var bmp = RenderWithExcise(path);

        InkBounds(bmp).Should().NotBeNull("an explicit /MK is an author asking for chrome");
    }

    // ── no-self-oracle ───────────────────────────────────────────────────────

    [Fact]
    public void OnAndOffCheckboxes_AgreeWithTwoIndependentEngines()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        var on = WriteTemp(ButtonPdf("/V /Yes /AS /Yes"));
        var off = WriteTemp(ButtonPdf("/V /Off /AS /Off"));

        using var mutoolOn = MutoolReferenceRenderer.RenderPage(on, 1, Dpi);
        using var cairoOn = PdftocairoReferenceRenderer.RenderPage(on, 1, Dpi);
        using var mutoolOff = MutoolReferenceRenderer.RenderPage(off, 1, Dpi);
        using var cairoOff = PdftocairoReferenceRenderer.RenderPage(off, 1, Dpi);

        // The oracles first: if these ever stop agreeing, the premise is gone
        // and excise's behaviour is a judgement call again, not a defect.
        InkedPixels(mutoolOn!, WidgetRect).Should().BeGreaterThan(100);
        InkedPixels(cairoOn!, WidgetRect).Should().BeGreaterThan(100);
        InkedPixels(mutoolOff!, WidgetRect).Should().Be(0);
        InkedPixels(cairoOff!, WidgetRect).Should().Be(0);

        using var exciseOn = RenderWithExcise(on);
        using var exciseOff = RenderWithExcise(off);

        // Ink within half of the oracle mean. Deliberately loose: excise draws
        // its own polyline rather than a ZapfDingbats glyph, the two oracles
        // already differ from each other by 5px vertically, and pixel parity
        // with any one renderer is explicitly not the goal.
        var oracleMean = (InkedPixels(mutoolOn!, WidgetRect) + InkedPixels(cairoOn!, WidgetRect)) / 2.0;
        InkedPixels(exciseOn, WidgetRect).Should().BeInRange(
            (uint)(oracleMean * 0.5), (uint)(oracleMean * 1.5),
            "excise draws a mark of the same order as the one both engines draw");

        InkedPixels(exciseOff, WidgetRect).Should().Be(0,
            "and draws nothing where both engines draw nothing");
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static byte[] ButtonPdf(string extras) => Assemble(new[]
    {
        "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>\nendobj\n",
        $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {PageSize} {PageSize}] >>\nendobj\n",
        "3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [4 0 R] >>\nendobj\n",
        "4 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Btn /T (b) "
            + extras + " /Rect [50 50 100 100] >>\nendobj\n",
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

    private static bool IsInk(SKColor c) => c.Red < 240 || c.Green < 240 || c.Blue < 240;

    private static uint InkedPixels(SKBitmap bmp, SKRectI box)
    {
        uint ink = 0;
        for (int y = Math.Max(0, box.Top); y < Math.Min(bmp.Height, box.Bottom); y++)
            for (int x = Math.Max(0, box.Left); x < Math.Min(bmp.Width, box.Right); x++)
                if (IsInk(bmp.GetPixel(x, y))) ink++;
        return ink;
    }

    private static double InkFraction(SKBitmap bmp, SKRectI box)
    {
        int total = Math.Max(0, Math.Min(bmp.Width, box.Right) - Math.Max(0, box.Left))
                  * Math.Max(0, Math.Min(bmp.Height, box.Bottom) - Math.Max(0, box.Top));
        return total == 0 ? 0 : (double)InkedPixels(bmp, box) / total;
    }

    /// <summary>Bounding box of every inked pixel on the page, or null.</summary>
    private static SKRectI? InkBounds(SKBitmap bmp)
    {
        int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (IsInk(bmp.GetPixel(x, y)))
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        return maxX < 0 ? null : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>Mean X of the inked pixels on one raster row.</summary>
    private static int RowInkCentroidX(SKBitmap bmp, int y)
    {
        long sum = 0; int n = 0;
        for (int x = 0; x < bmp.Width; x++)
            if (IsInk(bmp.GetPixel(x, y))) { sum += x; n++; }
        return n == 0 ? -1 : (int)(sum / n);
    }

    private string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-972-{Guid.NewGuid():N}.pdf");
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
