using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #888 — an annotation that CARRIES an <c>/AP</c> appearance stream must be
/// drawn. Unlike #885 (synthesising an appearance that isn't there), this has a
/// definite right answer: the document says exactly what to draw.
///
/// Three separate defects sat behind one symptom, each found on a pdfium-corpus
/// fixture and each reproduced here by a fixture authored IN THIS FILE. The
/// corpus is gitignored, so a corpus-dependent test skips on CI — the same hole
/// that made the #872 gate not run on the machine that blocks merges. These
/// build their own input and need no corpus.
///
/// The three:
///
///   1. <b>/BBox is an indirect reference</b> (pdfium <c>bug_1658.pdf</c>). The
///      annotation path tested <c>GetOptional("BBox") is not PdfArray</c>,
///      which inspects the reference object itself and fails, then silently
///      `continue`d. RenderFormXObjectInner — one call away, on the very same
///      stream — already resolved it through ResolveArray. The asymmetry was
///      the bug.
///   2. <b>/BBox is absent</b> (pdfium <c>bug_861842.pdf</c>). Required by
///      §12.5.5, but Poppler and MuPDF fall back to the annotation /Rect
///      rather than discarding the annotation.
///   3. <b>The page content stream leaves an unbalanced CTM</b> (pdfium
///      <c>bug_896366.pdf</c>, whose content is one operator:
///      <c>1 0 0 -1 0 792 cm</c>). Annotation appearances are positioned in
///      DEFAULT user space and do not inherit it. excise drew the widget at
///      raster y80..119 where mutool and pdftocairo both drew y672..711.
///
/// Why this matters beyond fidelity: excise is a redaction tool. An annotation
/// the reviewer never sees is content they cannot decide about, and it still
/// reaches the recipient.
/// </summary>
public class AnnotationAppearanceDrawnTests : IDisposable
{
    private const int Dpi = 72;   // 1 PDF point == 1 pixel
    private const int PageSize = 200;

    private readonly List<string> _temp = new();

    // ── 1. indirect /BBox ────────────────────────────────────────────────────

    [Fact]
    public void IndirectBBox_AppearanceIsStillDrawn()
    {
        var path = WriteTemp(IndirectBBoxPdf());
        using var bmp = RenderWithExcise(path);

        // The appearance fills the annotation rect. Before the fix this was
        // zero: the indirect /BBox failed an `is PdfArray` test and the whole
        // annotation was skipped without a diagnostic.
        InkFraction(bmp, new SKRectI(50, 50, 150, 150)).Should().BeGreaterThan(0.9,
            "an /AP whose /BBox is an indirect reference must still be drawn");
    }

    [Fact]
    public void IndirectBBox_MatchesIndependentRenderer()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(IndirectBBoxPdf());
        using var excise = RenderWithExcise(path);
        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        var box = new SKRectI(50, 50, 150, 150);
        InkFraction(reference!, box).Should().BeGreaterThan(0.9,
            "mutool must draw the appearance — otherwise the fixture, not excise, is wrong");
        InkFraction(excise, box).Should().BeGreaterThan(0.9);
    }

    // ── 2. absent /BBox ──────────────────────────────────────────────────────

    [Fact]
    public void MissingBBox_DrawsTheWidgetChromeInsteadOfDroppingTheAnnotation()
    {
        var path = WriteTemp(MissingBBoxWidgetPdf());
        using var bmp = RenderWithExcise(path);

        InkFraction(bmp, new SKRectI(50, 50, 150, 150)).Should().BeGreaterThan(0.05,
            "a /Widget whose appearance form is invalid must still show its own " +
            "chrome, as other readers do, rather than vanishing from the page");
    }

    /// <summary>
    /// Pins the thing that corrected this fix. The first attempt synthesised a
    /// /BBox from the annotation /Rect and justified it as "what Poppler and
    /// MuPDF do". They don't: on a BBox-less form both draw nothing, and
    /// pdftocairo says so out loud — "Syntax Error: Bad form bounding box".
    /// /BBox is REQUIRED (§8.10.2), so there is no geometry to recover and
    /// inventing one is fabrication.
    ///
    /// This test exists so that claim can never be reintroduced from memory.
    /// </summary>
    [Fact]
    public void MissingBBox_IndependentRenderersRefuseTheFormItself()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(MissingBBoxSquarePdf());
        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        InkFraction(reference!, new SKRectI(50, 50, 150, 150)).Should().BeLessThan(0.01,
            "mutool does NOT repair a form that omits its required /BBox — so excise " +
            "must not claim to be matching it by inventing one from /Rect");
    }

    // ── 3. content stream leaves a transform behind ──────────────────────────

    /// <summary>
    /// The discriminating case, and the reason it is stated as "inked HERE and
    /// blank THERE" rather than just "inked somewhere": a Y-flip moves the
    /// annotation, it does not erase it. An assertion that only counted ink on
    /// the page would have passed with the bug fully present.
    /// </summary>
    [Fact]
    public void UnbalancedCtmInPageContent_DoesNotMoveTheAnnotation()
    {
        var path = WriteTemp(UnbalancedCtmPdf());
        using var bmp = RenderWithExcise(path);

        // /Rect [20 20 80 60] on a 200-high page → raster y 140..180.
        var correct = new SKRectI(20, 140, 80, 180);
        // Where the leftover `1 0 0 -1 0 200 cm` would have put it.
        var mirrored = new SKRectI(20, 20, 80, 60);

        InkFraction(bmp, correct).Should().BeGreaterThan(0.9,
            "the annotation belongs at its /Rect in default user space");
        InkFraction(bmp, mirrored).Should().BeLessThan(0.01,
            "a transform left behind by the page content stream must not relocate " +
            "the annotation — this is the half that fails when the CTM leaks");
    }

    [Fact]
    public void UnbalancedCtmInPageContent_MatchesIndependentRenderer()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteTemp(UnbalancedCtmPdf());
        using var excise = RenderWithExcise(path);
        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        var correct = new SKRectI(20, 140, 80, 180);
        InkFraction(reference!, correct).Should().BeGreaterThan(0.9,
            "mutool places the annotation at its /Rect regardless of the leftover CTM");
        InkFraction(excise, correct).Should().BeGreaterThan(0.9);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    /// <summary>/AP /N form whose /BBox is `7 0 R`, not a direct array.</summary>
    private static byte[] IndirectBBoxPdf()
    {
        const string ap = "0 0 1 rg 50 50 100 100 re f";
        return Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {PageSize} {PageSize}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [4 0 R] >>\nendobj\n",
            "4 0 obj\n<< /Type /Annot /Subtype /Square /F 4 /Rect [50 50 150 150] /AP << /N 5 0 R >> >>\nendobj\n",
            $"5 0 obj\n<< /Type /XObject /Subtype /Form /FormType 1 /BBox 7 0 R /Length {ap.Length} >>\n" +
            $"stream\n{ap}\nendstream\nendobj\n",
            "6 0 obj\n<< >>\nendobj\n",
            "7 0 obj\n[50 50 150 150]\nendobj\n",
        });
    }

    /// <summary>
    /// pdfium bug_861842's shape: a /Widget whose /AP /N form omits the
    /// required /BBox. Other readers fall back to the widget's own chrome.
    /// </summary>
    private static byte[] MissingBBoxWidgetPdf() => MissingBBoxPdf(
        "<< /Type /Annot /Subtype /Widget /FT /Btn /T (b) /F 4 /Rect [50 50 150 150] " +
        "/MK << /BG [1 0 0] /BC [0 0 0] >> /BS << /S /S /W 2 >> /AP << /N 5 0 R >> >>");

    /// <summary>
    /// The same invalid form on a plain /Square, which has no chrome of its
    /// own — used to observe what the reference renderers do with the FORM,
    /// uncontaminated by widget decoration.
    /// </summary>
    private static byte[] MissingBBoxSquarePdf() => MissingBBoxPdf(
        "<< /Type /Annot /Subtype /Square /F 4 /Rect [50 50 150 150] /AP << /N 5 0 R >> >>");

    private static byte[] MissingBBoxPdf(string annotDict)
    {
        const string ap = "0 0 1 rg 0 0 100 100 re f";
        return Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {PageSize} {PageSize}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [4 0 R] >>\nendobj\n",
            $"4 0 obj\n{annotDict}\nendobj\n",
            $"5 0 obj\n<< /Type /XObject /Subtype /Form /FormType 1 /Length {ap.Length} >>\n" +
            $"stream\n{ap}\nendstream\nendobj\n",
        });
    }

    /// <summary>
    /// Page content is a single unbalanced <c>cm</c> — pdfium bug_896366's
    /// shape, reduced. Nothing is drawn by the content itself, so every inked
    /// pixel on the page comes from the annotation.
    /// </summary>
    private static byte[] UnbalancedCtmPdf()
    {
        const string content = "1 0 0 -1 0 200 cm";
        const string ap = "0 0 1 rg 0 0 60 40 re f";
        return Assemble(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {PageSize} {PageSize}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [4 0 R] /Contents 6 0 R >>\nendobj\n",
            "4 0 obj\n<< /Type /Annot /Subtype /Square /F 4 /Rect [20 20 80 60] /AP << /N 5 0 R >> >>\nendobj\n",
            $"5 0 obj\n<< /Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 60 40] /Length {ap.Length} >>\n" +
            $"stream\n{ap}\nendstream\nendobj\n",
            $"6 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
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

    /// <summary>Fraction of non-white pixels inside a RASTER-space box.</summary>
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
        var p = Path.Combine(Path.GetTempPath(), $"excise-888-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        _temp.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _temp) { try { File.Delete(p); } catch { } }
    }
}
