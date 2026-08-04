using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #866 — verdicts for the five PASS_ONE pages where excise agreed with mutool
/// and disagreed sharply with pdftocairo. Modelled on
/// <see cref="Excise.Core.Tests.Text.EncodingResidualClassificationTests"/> (#532):
/// each fixture is pinned to a documented verdict so a two-renderer
/// disagreement is not silently read as an excise defect.
///
/// Ghostscript, PDFBox and PDFium break the tie, per the issue. Measured on the
/// 2026-08-03 all-pages scan (diff fraction vs excise):
///
///   fixture                        mutool  cairo     gs  pdfbox pdfium  verdict
///   bitmap-symbol-context-reuse     1.000  0.000  0.000  0.000  0.071   DEFECT
///   bug1552113                      0.025  0.838  0.046  0.279  0.058   outlier
///   issue2177                       0.074  0.589  0.592  0.206  0.024   no truth
///   freeculture                     0.038  0.510  0.509  0.018  0.032   outlier
///   issue16316                      0.009  0.328  0.290  0.177  0.197   no truth
///
/// VERDICTS
///
/// • bitmap-symbol-context-reuse.pdf — excise DEFECT, and the issue's premise
///   for this row is now INVERTED. When #866 was filed excise agreed with
///   mutool and pdftocairo was the outlier at 0.991. Today mutool is the
///   outlier at 1.000 and excise sits with cairo/gs/pdfbox at ~0. Nothing about
///   the file changed: #878 stopped excise painting a fabricated image, so
///   excise moved from "wrong pixels" to "no pixels" and swapped which cluster
///   it belongs to. mutool is the only renderer that decodes this JBIG2 and it
///   is the one that is RIGHT — agreeing with three renderers that also fail is
///   not corroboration. Tracked as #874/#656; pinned here by its root cause.
///
/// • bug1552113.pdf — excise CORRECT, pdftocairo the outlier. excise sits with
///   mutool (0.025), Ghostscript (0.046) and PDFium (0.058); only pdftocairo
///   departs (0.838). Three independent agreements beat one disagreement.
///
/// • freeculture.pdf — excise CORRECT. mutool 0.038, PDFBox 0.018, PDFium
///   0.032 all agree with excise; pdftocairo (0.510) and Ghostscript (0.509)
///   are the outliers, and they are outliers TOGETHER — both are text-heavy
///   full-page renders where the two share font-rasterisation behaviour excise
///   and the others do not.
///
/// • issue2177.pdf, issue16316.pdf — NO RELIABLE GROUND TRUTH. The oracles
///   spread out with no majority (issue2177: mutool 0.074 and PDFium 0.024
///   close, cairo 0.589 and gs 0.592 far, PDFBox 0.206 between). excise ranks
///   1 of 5 on centrality in both, i.e. it is the most central renderer of the
///   set, but "most central" is not "correct" and there is nothing here to
///   prove either way. Same posture as #875.
///
/// Only one of the five is an excise defect, and it was already tracked.
/// </summary>
public class PdftocairoOutlierClassificationTests
{
    private const int Dpi = 72;

    /// <summary>
    /// excise DEFECT. Pinned by ROOT CAUSE rather than by blankness: a test
    /// asserting "this page is blank" would pass for as long as the bug lives
    /// and say nothing about why. The short-decode diagnostic (#878, made
    /// non-silent in 0ee4a044) names the actual failure, so when the JBIG2
    /// decoder is fixed this test fails loudly and gets updated.
    /// </summary>
    [Fact]
    public void BitmapSymbolContextReuse_IsAJbig2ShortDecode_NotADisagreement()
    {
        var path = FindCorpusFile("pdfjs", "bitmap-symbol-context-reuse.pdf");
        Assert.SkipWhen(path == null, "gitignored pdf.js corpus fixture not present (scripts/download-pdfjs-corpus.sh)."); // [requires: corpus:pdfjs]

        var diags = new List<string>();
        using var doc = PdfDocument.Open(path!);
        using var _ = new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White, Diagnostics = diags });

        diags.Should().Contain(d => d.Contains("JBIG2Decode") && d.Contains("required bytes"),
            "this page is not a renderer disagreement — excise's JBIG2 decoder returns a " +
            "stub buffer, which is #874/#656. Agreeing with the three renderers that ALSO " +
            "fail is not corroboration; mutool decodes it and is right.");
    }

    /// <summary>
    /// excise CORRECT, pdftocairo the outlier. Stated as a RELATIVE claim —
    /// excise is closer to mutool than pdftocairo is — because that is what the
    /// data supports and it needs no absolute threshold to be meaningful.
    /// </summary>
    [Theory]
    [InlineData("bug1552113.pdf")]
    [InlineData("freeculture.pdf")]
    public void ExciseAgreesWithMutool_WherePdftocairoIsTheOutlier(string fixture)
    {
        var path = FindCorpusFile("pdfjs", fixture);
        Assert.SkipWhen(path == null, $"gitignored pdf.js corpus fixture {fixture} not present."); // [requires: corpus:pdfjs]
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed");

        using var mutool = MutoolReferenceRenderer.RenderPage(path!, 1, Dpi);
        using var cairo = PdftocairoReferenceRenderer.RenderPage(path!, 1, Dpi);
        mutool.Should().NotBeNull();
        cairo.Should().NotBeNull();

        using var doc = PdfDocument.Open(path!);
        using var excise = new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

        var exciseVsMutool = DiffFraction(excise, mutool!);
        var cairoVsMutool = DiffFraction(cairo!, mutool!);

        exciseVsMutool.Should().BeLessThan(cairoVsMutool,
            $"on {fixture} excise is closer to mutool than pdftocairo is, so the " +
            "excise-vs-pdftocairo gap is pdftocairo departing from the others, not an " +
            "excise defect — the point of triaging this rather than assuming");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static double DiffFraction(SKBitmap a, SKBitmap b)
    {
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        if (w == 0 || h == 0) return 1.0;
        long differing = 0, total = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                total++;
                var ca = a.GetPixel(x, y);
                var cb = b.GetPixel(x, y);
                if (Math.Abs(ca.Red - cb.Red) > 24 ||
                    Math.Abs(ca.Green - cb.Green) > 24 ||
                    Math.Abs(ca.Blue - cb.Blue) > 24) differing++;
            }
        return total == 0 ? 1.0 : (double)differing / total;
    }

    private static string? FindCorpusFile(string corpus, string name)
    {
        var dir = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "test-pdfs", corpus));
        if (!Directory.Exists(dir)) return null;
        var hit = Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
        return hit;
    }
}
