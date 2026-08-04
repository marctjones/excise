using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #885 — an annotation that ships NO <c>/AP</c> still has to be visible.
/// §12.5.5 says a conforming reader should generate an appearance from the
/// annotation's own properties.
///
/// WHICH SUBTYPES ARE IMPLEMENTED, AND WHY THE REST ARE NOT
/// ---------------------------------------------------------
/// The issue asked for FreeText, Widget and "icon subtypes" wholesale. Only
/// some of that has an agreed answer. Measured at the corpus scan's own
/// 150 dpi against THREE renderers (an earlier two-oracle reading at 72 dpi
/// was under-informed and got Link wrong — adding Ghostscript flipped it):
///
///   subtype / fixture                    mutool    cairo       gs   verdict
///   Text     vera 6-3-3-t01-pass-a           495      917     1388   ALL 3
///   Widget   annots_action_handling         3737     1138     1608   ALL 3
///   Widget   checkbox_no_appearance          233      229        -   both
///   FreeText freetext_..._without_da        1250     1250        -   both
///   Link     isartor-6-6-1-t01-fail-a          0     5973     5950   2 of 3
///   Stamp    vera 6-3-3-t01-fail-m         81119   808346        0   2 of 3
///   Redact   vera 6-3-3-t01-fail-b         11671        0        0   1 of 3
///   Line     vera 6-3-3-t01-fail-d             0      230        0   1 of 3
///   PolyLine vera 6-3-3-t01-fail-h             0      742        0   1 of 3
///   Ink      vera 6-3-3-t01-fail-o             0      934        0   1 of 3
///   FileAtt  vera 6-3-3-t01-fail-p           196        0        0   1 of 3
///   Sound    vera 6-3-3-t01-fail-q           218        0        0   1 of 3
///
/// IMPLEMENTED: Widget (/FT /Btn), FreeText, Link (bounded — see below) and
/// Text. Each has at least two independent renderers agreeing it should be
/// drawn.
///
/// NOT IMPLEMENTED, deliberately: every 1-of-3 row. On each of those excise
/// already agrees with two renderers, and the scan flags them only because it
/// compares against the MOST-INKED oracle (#883) — the right rule for finding
/// content that was DROPPED, and the wrong one for content a viewer INVENTS.
/// Implementing them means picking one renderer over two with nothing to break
/// the tie: the #875 trap. Tracked in #889.
///
/// STAMP is 2 of 3 and still not implemented, which is a judgement rather than
/// an omission: mutool inks 81k px and pdftocairo 808k for the same annotation,
/// so they agree it is drawn and disagree by an order of magnitude on WHAT.
/// There is no artwork to copy, only an invitation to invent some.
///
/// </summary>
public class SynthesizedAnnotationAppearanceTests : IDisposable
{
    private const int Dpi = 72;
    private const int PageSize = 200;

    private readonly List<string> _temp = new();

    // ── Widget: a checkbox with no /AP and no /MK ────────────────────────────

    /// <summary>
    /// The rule this replaced was "only signature fields get a synthesized
    /// border; everything else is invisible until filled — mutool, Poppler and
    /// Foxit all leave them blank unless the author opted into /MK styling."
    ///
    /// That is true of an empty TEXT field and false of a BUTTON, and pdf.js's
    /// checkbox_no_appearance.pdf (two /FT /Btn widgets, no /MK anywhere) is
    /// the counterexample: mutool 233 inked px, pdftocairo 229, excise 0.
    /// </summary>
    [Fact]
    public void CheckboxWithoutAppearanceOrMk_IsStillDrawn()
    {
        var path = WriteTemp(ButtonWidgetPdf());
        using var bmp = RenderWithExcise(path);

        // A 1pt border round a 50x50 box is ~0.8-1.4% of the 70x70 sample
        // window, so the bar is set just clear of zero rather than at some
        // round-looking number: the discrimination that matters is
        // "drew an outline" vs "drew nothing at all" (excise was exactly 0).
        InkFraction(bmp, new SKRectI(40, 40, 110, 110)).Should().BeGreaterThan(0.004,
            "a checkbox is a control whose state the reader is meant to see, so it " +
            "gets a box even with no /MK to style it from");
    }

    [Fact]
    public void CheckboxWithoutAppearance_MatchesIndependentRenderers()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(ButtonWidgetPdf());
        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        var box = new SKRectI(40, 40, 110, 110);
        InkFraction(reference!, box).Should().BeGreaterThan(0.004,
            "mutool draws the checkbox — this is what makes excise's blank a defect " +
            "rather than a defensible choice");
        InkFraction(RenderWithExcise(path), box).Should().BeGreaterThan(0.004);
    }

    /// <summary>
    /// The half of the old rule that measurement upheld, kept so restoring the
    /// button case does not quietly turn every unfilled text field into a box.
    /// </summary>
    [Fact]
    public void EmptyTextFieldWithoutAppearance_StaysInvisible()
    {
        var path = WriteTemp(EmptyTextWidgetPdf());
        using var bmp = RenderWithExcise(path);

        InkFraction(bmp, new SKRectI(40, 40, 110, 110)).Should().BeLessThan(0.001,
            "an unfilled text field is invisible until filled — unchanged behaviour");
    }

    // ── FreeText ─────────────────────────────────────────────────────────────

    /// <summary>
    /// pdfium freetext_annotation_without_da.pdf pins this exactly: /C present,
    /// /Rect 50x25, and BOTH oracles ink 1250 px — precisely the whole
    /// rectangle. So a FreeText carrying /C is filled, not outlined.
    /// </summary>
    [Fact]
    public void FreeTextWithColor_FillsItsRect()
    {
        var path = WriteTemp(FreeTextPdf(withColor: true));
        using var bmp = RenderWithExcise(path);

        InkFraction(bmp, new SKRectI(50, 50, 150, 100)).Should().BeGreaterThan(0.95,
            "both reference renderers fill a /C-carrying FreeText rect completely");
    }

    [Fact]
    public void FreeTextWithColor_MatchesIndependentRenderers()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(FreeTextPdf(withColor: true));
        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        var box = new SKRectI(50, 50, 150, 100);
        InkFraction(reference!, box).Should().BeGreaterThan(0.95);
        InkFraction(RenderWithExcise(path), box).Should().BeGreaterThan(0.95);
    }

    [Fact]
    public void FreeTextWithoutColor_IsOutlinedNotFilled()
    {
        var path = WriteTemp(FreeTextPdf(withColor: false));
        using var bmp = RenderWithExcise(path);

        var whole = new SKRectI(50, 50, 150, 100);
        var interior = new SKRectI(60, 60, 140, 90);

        InkFraction(bmp, whole).Should().BeGreaterThan(0.02,
            "a FreeText with no /C must still be visible");
        InkFraction(bmp, interior).Should().BeLessThan(0.01,
            "…as an OUTLINE — filling it would hide the page content underneath, " +
            "and both oracles ink only a few hundred px on such a note, not the rect");
    }

    // ── Link: border only when the file explicitly asks for one ──────────────

    /// <summary>
    /// Link had an empty case, justified as "links without /C are intentionally
    /// invisible in print, matching every commercial viewer". Measured at the
    /// scan's own 150 dpi on isartor-6-6-1-t01-fail-a.pdf — a /Link with
    /// /BS &lt;&lt; /W 2 &gt;&gt;, /Border [0 0 2] and NO /C:
    ///
    ///     pdftocairo  5973 inked px (black)
    ///     ghostscript 5950 (black)
    ///     mutool         0
    ///
    /// Two of three is a basis. The earlier reading that filed Link as a plain
    /// renderer split was taken at 72 dpi WITHOUT Ghostscript; the third
    /// opinion flipped it.
    /// </summary>
    [Fact]
    public void LinkWithAnExplicitBorderWidth_IsDrawn()
    {
        var path = WriteTemp(LinkPdf(borderWidth: 2));
        using var bmp = RenderWithExcise(path);

        InkFraction(bmp, new SKRectI(45, 45, 155, 155)).Should().BeGreaterThan(0.004,
            "a /Link that explicitly asks for a 2pt border gets one — pdftocairo and " +
            "Ghostscript both stroke it black");
    }

    /// <summary>
    /// The other half, and the reason the rule keys on the width being STATED
    /// rather than on its value. Most links omit /Border entirely or set
    /// [0 0 0]; only pdftocairo draws those (1 of 3), so excise must not.
    /// Without this the fix would put a box round every link in every document.
    /// </summary>
    [Fact]
    public void LinkWithNoStatedBorderWidth_IsNotDrawn()
    {
        var path = WriteTemp(LinkPdf(borderWidth: null));
        using var bmp = RenderWithExcise(path);

        InkFraction(bmp, new SKRectI(45, 45, 155, 155)).Should().BeLessThan(0.001,
            "with no /Border and no /BS the file has not asked for a visible border, " +
            "and only 1 of 3 reference renderers draws one — measured on " +
            "pdfium bug_821454.pdf: mutool 0, Ghostscript 0, pdftocairo 2830");
    }

    /// <summary>
    /// The width must FIT the rect. pdf.js bug1552113.pdf writes
    /// /Border [0 0 112] on a 150x20 annotation, and that is where the oracles
    /// part company: pdftocairo paints a 45,659-pixel blue slab over the page
    /// while mutool and Ghostscript both draw nothing. Ghostscript draws a sane
    /// border (see above) and refuses an absurd one, which is the whole
    /// difference between the two cases.
    /// </summary>
    [Fact]
    public void LinkWithABorderWiderThanTheAnnotation_IsNotDrawn()
    {
        var path = WriteTemp(LinkPdf(borderWidth: 112));
        using var bmp = RenderWithExcise(path);

        InkFraction(bmp, new SKRectI(20, 20, 180, 180)).Should().BeLessThan(0.001,
            "a 112pt border on a 100pt annotation is not an outline, it is a slab over " +
            "the page — 2 of 3 reference renderers refuse it");
    }

    [Fact]
    public void LinkWithAZeroBorderWidth_IsNotDrawn()
    {
        var path = WriteTemp(LinkPdf(borderWidth: 0));
        using var bmp = RenderWithExcise(path);

        InkFraction(bmp, new SKRectI(45, 45, 155, 155)).Should().BeLessThan(0.001,
            "/Border [0 0 0] is the standard way to say 'no visible border'");
    }

    // ── Text: a fixed-size icon, and /Rect is ignored for sizing ─────────────

    /// <summary>
    /// §12.5.6.4 — a /Text annotation "shall be drawn at a fixed size regardless
    /// of the magnification", so producers legitimately write a DEGENERATE
    /// rect: veraPDF 6-3-3-t01-pass-a.pdf has /Rect [50 110 50 110], zero by
    /// zero. excise's degenerate-rect guard rejected it before anything could
    /// draw, while all three oracles place a ~16pt icon at that anchor
    /// (mutool 495 inked px, pdftocairo 917, Ghostscript 1388).
    ///
    /// All three agree an icon appears and disagree completely on what it looks
    /// like — black strokes vs grey-green fill vs grey+black — so this asserts
    /// only presence and size, never artwork.
    /// </summary>
    [Fact]
    public void TextAnnotationWithADegenerateRect_StillDrawsItsIcon()
    {
        var path = WriteTemp(StickyNotePdf());
        using var bmp = RenderWithExcise(path);

        // /Rect is [50 150 50 150] — zero-size, anchored top-left of the icon.
        // On a 200-high page that is raster y 50..67, x 50..67.
        InkFraction(bmp, new SKRectI(50, 50, 68, 68)).Should().BeGreaterThan(0.5,
            "a zero-size /Rect is NORMAL for /Text — the icon is a fixed size and " +
            "the rect is only its anchor");
    }

    [Fact]
    public void TextAnnotationIcon_MatchesIndependentRenderers()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(StickyNotePdf());
        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        var anchor = new SKRectI(45, 45, 75, 75);
        InkFraction(reference!, anchor).Should().BeGreaterThan(0.05,
            "mutool draws a note icon at the anchor — this is what makes excise's " +
            "blank a defect rather than a stylistic choice");
        InkFraction(RenderWithExcise(path), anchor).Should().BeGreaterThan(0.05);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static byte[] LinkPdf(int? borderWidth)
    {
        var border = borderWidth is { } w
            ? $"/Border [0 0 {w}] /BS << /S /S /W {w} >> "
            : "";
        return AnnotPdf(
            "<< /Type /Annot /Subtype /Link /F 4 /Rect [50 50 150 150] " + border + ">>");
    }

    private static byte[] StickyNotePdf() => AnnotPdf(
        // Degenerate rect on purpose — this is the shape real producers write.
        "<< /Type /Annot /Subtype /Text /F 4 /Rect [50 150 50 150] /Contents (note) >>");


    private static byte[] ButtonWidgetPdf() => AnnotPdf(
        "<< /Type /Annot /Subtype /Widget /FT /Btn /T (cb) /V /Yes /AS /Yes " +
        "/Rect [50 50 100 100] >>");

    private static byte[] EmptyTextWidgetPdf() => AnnotPdf(
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (t) /Rect [50 50 100 100] >>");

    private static byte[] FreeTextPdf(bool withColor) => AnnotPdf(
        "<< /Type /Annot /Subtype /FreeText /Contents (note) /Rect [50 100 150 150] " +
        (withColor ? "/C [0 0 1] " : "") + ">>");

    private static byte[] AnnotPdf(string annotDict) => Assemble(new[]
    {
        "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
        $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {PageSize} {PageSize}] >>\nendobj\n",
        "3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [4 0 R] >>\nendobj\n",
        $"4 0 obj\n{annotDict}\nendobj\n",
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

    private static double InkFraction(SKBitmap bmp, SKRectI box)
    {
        int ink = 0, total = 0;
        for (int y = Math.Max(0, box.Top); y < Math.Min(bmp.Height, box.Bottom); y++)
            for (int x = Math.Max(0, box.Left); x < Math.Min(bmp.Width, box.Right); x++)
            {
                total++;
                var c = bmp.GetPixel(x, y);
                if (c.Red < 240 || c.Green < 240 || c.Blue < 240) ink++;
            }
        return total == 0 ? 0 : (double)ink / total;
    }

    private string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-885-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        _temp.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _temp) { try { File.Delete(p); } catch { } }
    }
}
