using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// No-self-oracle corroboration for four annotation-subtypes.json subtypes
/// (Caret, Screen, Watermark, Movie) whose only prior "render" evidence was
/// <see cref="AnnotationAppearanceStreamRenderTests.SubtypeWithNormalAppearanceStream_Renders"/>
/// — excise rendering its own generic /AP /N appearance-stream path and
/// excise's own renderer confirming the pixel is red proves only
/// self-consistency (the two-oracle rule this project's CLAUDE.md documents
/// under "no-self-oracle").
///
/// <para>These subtypes have no synthesis case in
/// <c>AnnotationAppearancePolicy.SelectSynthesis</c> (excise cannot AUTHOR
/// them — see <see cref="UnauthoredAnnotationPreservationTests"/>), so unlike
/// <see cref="RemainingAnnotationSubtypesDifferentialTests"/> and
/// <see cref="AnnotationAuthoringDifferentialTests"/> the fixtures here cannot
/// be built with an <c>Add*Annotation</c> call. They are hand-built raw PDF
/// objects carrying a real <c>/AP /N</c> Form XObject, the same shape as
/// <see cref="AnnotationAppearanceStreamRenderTests"/>'s fixture (plus
/// <c>/F 4</c> and, for Movie, the Table 186 <c>/Movie</c> key — see below),
/// so what is being verified is specifically the GENERIC appearance-stream
/// paint path for these subtype tags — not a from-scratch drawing policy,
/// because none exists.</para>
///
/// <para><b>Why Popup, the fifth subtype that file covers, is not here.</b>
/// Probed against mutool, pdftocairo and Ghostscript before writing any
/// assertion: a bare Popup's /AP split 2-1 (mutool AND Ghostscript refuse to
/// paint it — consistent with §12.5.6.2, where a Popup's appearance is
/// conventionally suppressed and only the parent markup annotation paints;
/// pdftocairo painted it anyway). That is not a case where independent
/// renderers corroborate excise's paint decision, so per the "never fabricate
/// a mapping, skip rather than stretch relevance" rule it is NOT claimed as
/// verified here — <c>GroupBAnnotationMeaningTests</c> already excludes Popup
/// from its own oracle-agreement pool for the same reason.</para>
///
/// <para><b>Movie needed a well-formed fixture, not a skip.</b> The first probe
/// used the same bare <c>/Rect + /AP</c> shape as the other four subtypes and
/// got Ghostscript blank on ALL of them (including Caret/Screen/Watermark) and
/// pdftocairo refusing Movie outright ("Bad Annot Movie"). Both looked like
/// paint-path disagreement but neither was: Ghostscript's png16m device only
/// draws annotations carrying the Print flag (<c>/F 4</c> — confirmed by
/// probing a plain Square with and without it, matching the convention
/// <c>GroupBAnnotationMeaningTests.BuildSquare</c> already uses), and
/// pdftocairo's error is Poppler's Table 186 validation rejecting a Movie
/// annotation missing the required <c>/Movie</c> action dictionary, not a
/// judgment about the /AP path. With <c>/F 4</c> added to all fixtures and a
/// minimal well-formed <c>/Movie << /F (dummy.mov) >></c> added to Movie's,
/// mutool, pdftocairo AND Ghostscript unanimously paint all four subtypes
/// below (Ghostscript is not asserted on in the tests — this suite's
/// established two-oracle pattern is mutool + pdftocairo — but its agreement
/// was confirmed manually and is worth recording here).</para>
/// </summary>
public class AnnotationSubtypeVerificationDifferentialTests : IDisposable
{
    private const int Dpi = 72; // 1 PDF point == 1 pixel on a 200x200 page
    private const int PageSize = 200;

    // The annotation /Rect, deliberately smaller than the page so there is a
    // real "outside" region a renderer could wrongly paint into.
    private const int RectLeft = 50, RectBottom = 50, RectRight = 150, RectTop = 150;

    private readonly List<string> _temp = new();

    [Theory]
    [InlineData("Caret")]
    [InlineData("Screen")]
    [InlineData("Watermark")]
    [InlineData("Movie")]
    public void AuthoredSubtypeAppearance_IsDrawnByMutool(string subtype)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteAnnotationFixture(subtype);
        using var rendered = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull("mutool must be able to open and render the fixture");

        AssertPixels(rendered!, "mutool", subtype);
    }

    [Theory]
    [InlineData("Caret")]
    [InlineData("Screen")]
    [InlineData("Watermark")]
    [InlineData("Movie")]
    public void AuthoredSubtypeAppearance_IsDrawnByPdftocairo(string subtype)
    {
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        var path = WriteAnnotationFixture(subtype);
        using var rendered = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
        rendered.Should().NotBeNull("pdftocairo must be able to open and render the fixture");

        AssertPixels(rendered!, "pdftocairo", subtype);
    }

    [Theory]
    [InlineData("Caret")]
    [InlineData("Screen")]
    [InlineData("Watermark")]
    [InlineData("Movie")]
    public void ExciseAndReferenceRenderer_AgreeOnAuthoredSubtypeAppearanceInk(string subtype)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = WriteAnnotationFixture(subtype);
        using var reference = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
        reference.Should().NotBeNull();

        using var doc = PdfDocument.Open(path);
        using var excise = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

        var inside = Box(RectLeft + 10, RectBottom + 10, RectRight - 10, RectTop - 10);
        RedFraction(excise, inside).Should().BeGreaterThan(0.9,
            $"excise must paint the /{subtype} annotation's /AP /N stream through the generic Form XObject path");
        RedFraction(reference!, inside).Should().BeGreaterThan(0.9,
            $"mutool must paint the /{subtype} annotation's /AP /N stream the same way");

        var outside = Box(5, 5, 30, 30);
        InkFraction(excise, outside).Should().Be(0, $"excise: no ink outside the /{subtype} annotation's rect");
        InkFraction(reference!, outside).Should().Be(0, $"mutool: no ink outside the /{subtype} annotation's rect");
    }

    private void AssertPixels(SKBitmap bmp, string tool, string subtype)
    {
        var inside = Box(RectLeft + 10, RectBottom + 10, RectRight - 10, RectTop - 10);
        var outside = Box(5, 5, 30, 30);

        RedFraction(bmp, inside).Should().BeGreaterThan(0.9,
            $"{tool} must paint the /{subtype} annotation's /AP /N stream inside its /Rect");
        InkFraction(bmp, outside).Should().Be(0,
            $"{tool}: no ink outside the /{subtype} annotation's /Rect");
    }

    /// <summary>
    /// One 200x200 page carrying a single annotation of the given subtype,
    /// /Rect [50 50 150 150], whose /AP /N Form XObject fills its BBox solid
    /// red. Hand-built at the object-stream level (no PdfDocument authoring
    /// API exists for these subtypes) — the same shape used by
    /// <see cref="AnnotationAppearanceStreamRenderTests"/>, sized up so an ink
    /// comparison can distinguish "painted the rect" from "painted the page".
    ///
    /// <para>Carries <c>/F 4</c> (Print flag) on every subtype — required for
    /// Ghostscript to draw an annotation appearance at all (see the type
    /// docstring) — and, for Movie only, the Table 186 <c>/Movie</c> key
    /// Poppler's validator requires to accept the annotation as well-formed.
    /// Neither addition changes what excise itself does: excise's own
    /// visibility policy (<c>AnnotationAppearancePolicy.EvaluateVisibility</c>)
    /// never inspects the Print flag or subtype-specific required keys.</para>
    /// </summary>
    private string WriteAnnotationFixture(string subtype)
    {
        const string apContent = "1 0 0 rg 0 0 100 100 re f";
        var extra = subtype == "Movie" ? "/Movie << /F (dummy.mov) >> " : "";
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {PageSize} {PageSize}] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /Annots [6 0 R] >>\nendobj\n",
            "4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n",
            $"5 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] " +
            $"/Length {apContent.Length} >>\nstream\n{apContent}\nendstream\nendobj\n",
            $"6 0 obj\n<< /Type /Annot /Subtype /{subtype} /F 4 {extra}" +
            $"/Rect [{RectLeft} {RectBottom} {RectRight} {RectTop}] /AP << /N 5 0 R >> >>\nendobj\n",
        };

        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");

        var path = Path.Combine(Path.GetTempPath(), $"excise-annot-subtype-{subtype}-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(sb.ToString()));
        _temp.Add(path);
        return path;
    }

    // ── Shared pixel helpers (mirrors RemainingAnnotationSubtypesDifferentialTests) ──

    private static SKRectI Box(double left, double bottom, double right, double top) =>
        new((int)left, PageSize - (int)top, (int)right, PageSize - (int)bottom);

    private static double Fraction(SKBitmap bmp, SKRectI box, Func<SKColor, bool> predicate)
    {
        int hit = 0, total = 0;
        int x0 = Math.Max(0, box.Left), x1 = Math.Min(bmp.Width - 1, box.Right);
        int y0 = Math.Max(0, box.Top), y1 = Math.Min(bmp.Height - 1, box.Bottom);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            total++;
            if (predicate(bmp.GetPixel(x, y))) hit++;
        }
        return total == 0 ? 0 : (double)hit / total;
    }

    private static double InkFraction(SKBitmap bmp, SKRectI box) =>
        Fraction(bmp, box, p => p.Red < 200 || p.Green < 200 || p.Blue < 200);

    private static double RedFraction(SKBitmap bmp, SKRectI box) =>
        Fraction(bmp, box, p => p.Red > 180 && p.Green < 100 && p.Blue < 100);

    public void Dispose()
    {
        foreach (var p in _temp)
        {
            try { File.Delete(p); } catch { /* best effort */ }
        }
    }
}
