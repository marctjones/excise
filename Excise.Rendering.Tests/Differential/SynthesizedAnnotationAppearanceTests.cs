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
/// WHY THIS COVERS ONLY TWO SUBTYPES
/// ---------------------------------
/// The issue asked for FreeText, Widget and "icon subtypes" wholesale. Measured
/// against two independent renderers, only some of that has an agreed answer —
/// on several subtypes mutool and pdftocairo flatly disagree about whether a
/// viewer should invent anything at all:
///
///   subtype / fixture                     mutool   pdftocairo   verdict
///   Widget  checkbox_no_appearance           233         229    both draw
///   FreeText freetext_annotation_without_da 1250        1250    both draw
///   FreeText bug1865341                      212         161    both draw
///   Line    vera 6-3-3-t01-fail-d              0         111    SPLIT
///   Ink     vera 6-3-3-t01-fail-o              0         560    SPLIT
///   PolyLine vera 6-3-3-t01-fail-h             0         454    SPLIT
///   Link    isartor-6-6-1-t01-fail-a           0        1160    SPLIT
///   Redact  vera 6-3-3-t01-fail-b           5600           0    SPLIT
///   Sound   vera 6-3-3-t01-fail-q             60           0    SPLIT
///
/// On every SPLIT row excise currently agrees with one of the two oracles, so
/// "excise draws nothing" is not self-evidently the defect the corpus scan
/// reports it as — the scan compares against the MOST-INKED oracle (#883),
/// which is right for finding dropped content and makes the most permissive
/// renderer the standard for invented content. Implementing those would mean
/// choosing pdftocairo over mutool (or vice versa) with no basis, which is the
/// #875 trap. They are left alone deliberately.
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

    // ── fixtures ─────────────────────────────────────────────────────────────

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
