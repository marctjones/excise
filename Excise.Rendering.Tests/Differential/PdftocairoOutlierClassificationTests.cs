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
/// • bug1552113.pdf — excise CORRECT, pdftocairo the outlier. Re-verified
///   2026-09-07 with a full pairwise matrix (not just each-vs-excise):
///   excise/mutool/gs form a tight cluster (0.098-0.109 apart from each
///   other) while cairo disagrees with ALL THREE by ~0.85. Still solid.
///
/// • freeculture.pdf — VERDICT CHANGED, 2026-09-07. This entry originally
///   read "excise CORRECT" from the 2026-08-03 scan (mutool 0.038, PDFBox
///   0.018, PDFium 0.032 close to excise; cairo 0.510, gs 0.509 far). A fresh
///   pairwise matrix no longer supports that:
///
///     excise-mutool 0.197   excise-cairo 0.142   excise-gs 0.185
///     mutool-cairo  0.167   mutool-gs    0.271   cairo-gs   0.211
///
///   excise (mean distance 0.175) and cairo (0.174) are now the two most
///   CENTRAL renderers; mutool (0.212) and gs (0.222) are further from the
///   group, and mutool-gs (0.271) is the single largest distance in the
///   whole matrix — the two "reference" renderers disagree with each other
///   more than either disagrees with excise. Visual inspection (pixel-diff
///   heatmap of excise vs mutool) shows the difference is a full-page
///   cascade of thin edge-outlines on every stripe boundary and every glyph
///   in a fine repeating horizontal-line pattern rendered with AntiAlias
///   off — consistent with a benign sub-pixel rasterisation difference, not
///   missing/wrong content. Root cause of WHY excise's numbers moved this
///   much since 2026-08-03 was not chased (would need a git-history bisect
///   across ~5 weeks); what's certain is that "excise agrees with mutool,
///   cairo is the outlier" is no longer what the data says. Reclassified to
///   NO RELIABLE GROUND TRUTH, same posture as the next entry — removed from
///   the pinned theory below rather than asserting a stale direction.
///
/// • issue2177.pdf, issue16316.pdf — NO RELIABLE GROUND TRUTH. The oracles
///   spread out with no majority (issue2177: mutool 0.074 and PDFium 0.024
///   close, cairo 0.589 and gs 0.592 far, PDFBox 0.206 between). excise ranks
///   1 of 5 on centrality in both, i.e. it is the most central renderer of the
///   set, but "most central" is not "correct" and there is nothing here to
///   prove either way. Same posture as #875.
///
/// Only one of the five is a confirmed excise defect, and it was already
/// tracked. freeculture.pdf moved from "confirmed correct" to "unprovable
/// either way" — not a regression finding, a measurement-honesty one.
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
    ///
    /// freeculture.pdf is deliberately NOT here any more (was, until
    /// 2026-09-07) — a fresh pairwise oracle matrix no longer supports the
    /// directional claim this test makes; see the class-level doc comment.
    /// Do not re-add it without re-measuring, and don't add a fixture here
    /// from the class comment's "no reliable ground truth" entries either —
    /// that classification means this assertion is exactly the wrong shape
    /// for them.
    /// </summary>
    [Theory]
    [InlineData("bug1552113.pdf")]
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
