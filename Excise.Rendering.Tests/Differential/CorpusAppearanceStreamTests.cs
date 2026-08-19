using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// GROUP A on REAL FILES (#1053). Same question as
/// <see cref="AppearanceStreamDifferentialTests"/> — with an <c>/AP</c> present,
/// §12.5.5 says the appearance stream shall be used, so renderer disagreement is
/// a defect — but asked of documents excise did not author.
///
/// <para><b>Why this exists, stated plainly.</b> Every other annotation
/// differential in this repo builds its own fixture: all 45 rows of
/// <c>annotation-synthesis-policy.json</c> are authored inline, and so are the
/// other eight annotation test files. Authoring the input means only ever
/// testing the constructs we thought of. Real annotation appearance streams come
/// out of Acrobat, Nitro, Foxit and scanners, and carry text, images,
/// transparency and nesting that nobody writing a fixture by hand produces.</para>
///
/// <para>Measured: across 4,147 corpus documents, <b>678 carry annotations and
/// 414 carry at least one <c>/AP</c></b> — and before this file, not one of them
/// was used to judge annotation rendering.</para>
///
/// <para>The pdf.js corpus supplies matched pairs — <c>annotation-highlight.pdf</c>
/// against <c>annotation-highlight-without-appearance.pdf</c> — which is exactly
/// the Group A / Group B split, produced by someone else. This file takes the 18
/// that HAVE an <c>/AP</c>. The 11 without belong to Group B and are judged on
/// meaning, not pixels.</para>
///
/// <para><b>What this gate can and cannot see, measured by mutating the
/// renderer rather than assumed:</b></para>
///
/// <list type="bullet">
///   <item>draw no annotations at all → caught by <b>13 of 13</b>. Every
///     fixture here discriminates; four that did not were removed rather than
///     left looking like coverage.</item>
///   <item>ignore <c>/AP</c> and synthesize instead → caught by <b>4 of 13</b>.
///     For the other nine, excise's synthesis approximates the real appearance
///     closely enough to pass — which is worth knowing about the synthesis, not
///     only about the gate.</item>
///   <item>break the <c>/BBox</c>→<c>/Rect</c> mapping (drop the scale, drop the
///     translation) → caught by <b>NONE</b>, and that is not a defect in this
///     file. Every fixture here has <c>/BBox</c> dimensions equal to its
///     <c>/Rect</c> — measured: scale 1.00×1.00 across all of them — so the
///     mapping is a no-op on real files and there is nothing to get wrong.
///     <see cref="AppearanceStreamDifferentialTests"/> exists precisely because
///     real documents do not exercise it; its synthetic fixtures do, and all
///     three mapping mutations are caught there.</item>
/// </list>
///
/// <para>That is the argument for keeping both, and it is evidence rather than
/// preference: the synthetic gate catches what real files never exercise, and
/// this one catches appearance content nobody would think to author.</para>
///
/// <para>⚠️ The corpus is gitignored, so this SKIPS on a machine without it.
/// That is why <see cref="AppearanceStreamDifferentialTests"/> stays: it builds
/// its own inputs and runs everywhere, including CI. This file is the wider net,
/// not the safety net.</para>
/// </summary>
public class CorpusAppearanceStreamTests
{
    private const int Dpi = 72;
    private const int Tile = 10;

    /// <summary>
    /// The pdf.js annotation fixtures that carry an <c>/AP</c>, verified by
    /// scanning for <c>/AP &lt;&lt;</c> through inflated object streams rather
    /// than trusting the file names.
    /// </summary>
    public static TheoryData<string> GroupAFixtures()
    {
        var d = new TheoryData<string>();
        foreach (var n in new[]
                 {
                     "annotation-caret-ink.pdf", "annotation-choice-widget.pdf",
                     "annotation-fileattachment.pdf", "annotation-freetext.pdf",
                     "annotation-line.pdf", "annotation-link-text-popup.pdf",
                     "annotation-polyline-polygon.pdf", "annotation-square-circle.pdf",
                     "annotation-stamp.pdf", "annotation-text-widget.pdf",
                     "annotation-tx2.pdf",
                     "annotation-tx3.pdf", "annotation-underline.pdf",

                     // DELIBERATELY ABSENT — annotation-text-without-popup, whose
                     // page box the engines disagree about: excise and mutool
                     // render one size and place the note icon identically,
                     // pdftocairo renders a much taller page and puts it 1,200px
                     // lower. Once the differing-box oracles are excluded (#932's
                     // rule) too few comparable voters remain for a verdict. That
                     // is a page-box question, not an annotation one, and this
                     // gate has no opinion on it.
                     //
                     // ALSO DELIBERATELY ABSENT — annotation-highlight, -squiggly,
                     // -strikeout and -button-widget. A control run that drew
                     // NO annotations at all left these four green, so they
                     // cannot fail and would only look like coverage.
                     //
                     // The cause is structural, not a tolerance to tune: text
                     // markup sits ON TOP OF TEXT, so its /Rect is already
                     // fully inked by the page content whether or not the
                     // annotation draws. Tile presence cannot see a highlight
                     // over a word that was black already.
                     //
                     // Their /AP-present rendering therefore needs an
                     // instrument this file does not have (colour or ink
                     // quantity, where engines already disagree by blend mode
                     // — see #1004). Group B covers whether they exist, are
                     // contained and carry the right text.
                 })
            d.Add(n);
        return d;
    }

    [Theory]
    [MemberData(nameof(GroupAFixtures))]
    public void ExciseDrawsRealAppearanceStreamsWhereTheOracleMajorityDoes(string fixture)
    {
        var path = Resolve(Path.Combine("test-pdfs", "pdfjs", fixture));
        Assert.SkipWhen(path == null, $"pdf.js corpus not present ({fixture})");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var oracles = new (string Name, SKBitmap? Bmp)[]
        {
            ("mutool",      MutoolReferenceRenderer.RenderPage(path!, 1, Dpi)),
            ("pdftocairo",  PdftocairoReferenceRenderer.IsAvailable
                                ? PdftocairoReferenceRenderer.RenderPage(path!, 1, Dpi) : null),
            ("ghostscript", GhostscriptReferenceRenderer.IsAvailable
                                ? GhostscriptReferenceRenderer.RenderPage(path!, 1, Dpi) : null),
            ("pdfium",      PdfiumNativeReferenceRenderer.IsAvailable
                                ? PdfiumNativeReferenceRenderer.RenderPage(
                                      path!, 1, Dpi, userPassword: null, renderAnnotations: true) : null),
            ("pdfbox",      PdfBoxReferenceRenderer.IsAvailable
                                ? PdfBoxReferenceRenderer.RenderPage(path!, 1, Dpi) : null),
        };

        var voters = oracles.Where(o => o.Bmp != null).ToList();
            // #932's rule, which these gates were missing: AN ORACLE THAT
            // RASTERISED A DIFFERENT PAGE BOX GETS NO VOTE. Its tiles address a
            // different part of the page, so comparing them is meaningless.
            //
            // Found on annotation-text-without-popup.pdf: excise and mutool
            // render the same page and place the note icon identically, while
            // pdftocairo renders a taller page (MediaBox where the others use
            // CropBox) and puts the icon 1,200px lower. Scored naively that read
            // as "no majority inked the /Rect" and the row SKIPPED — hiding a
            // case where excise is right.
            using var mineForSize = RenderWithExcise(path!);
            // A few pixels of tolerance: 200pt at 150 dpi is 416.67, and engines
            // round it differently. Exact equality excluded almost everyone and
            // pushed the voter count below three, turning passing rows into skips.
            // What must be caught is a DIFFERENT PAGE BOX — hundreds of pixels —
            // not a rounding disagreement.
            voters = voters.Where(o => Math.Abs(o.Bmp!.Width - mineForSize.Width) <= 2
                                    && Math.Abs(o.Bmp!.Height - mineForSize.Height) <= 2).ToList();

        Assert.SkipWhen(voters.Count < 3,
            $"only {voters.Count} reference renderer(s) available; a majority needs three (#976)");

        // ⚠️ RESTRICT TO THE ANNOTATION RECTS. Comparing whole pages made this
        // gate almost entirely vacuous, and only a mutation run showed it:
        // making excise ignore /AP completely was caught by 3 of 18 fixtures,
        // and dropping the /BBox->/Rect scale by NONE. Real pages carry body
        // content, so `majority` was dominated by text the annotation had
        // nothing to do with, and a proportional tolerance then swallowed the
        // annotation entirely. The signal is inside /Rect; everything else is
        // noise with a vote.
        var window = AnnotationWindow(path!, out var annotCount);
        Assert.SkipWhen(annotCount == 0, "no annotations excise can see on page 1");

        var tiles = voters.ToDictionary(o => o.Name, o => InkedTiles(o.Bmp!, window));
        var majority = MajorityTiles(tiles.Values.ToList());

        using var mine = RenderWithExcise(path!);
        var ours = InkedTiles(mine, window);

        var report = string.Join("\n", tiles.Select(kv => $"      {kv.Key,-12}{kv.Value.Count,5} tiles"));

        // Unlike the synthetic gate, a real page carries body content too, so
        // "the majority inked nothing" means the fixture is unusable rather
        // than that excise is wrong.
        Assert.SkipWhen(majority.Count == 0, $"no majority ink on {fixture}; nothing to compare");

        var missing = majority.Except(ours).ToList();
        var extra = ours.Except(majority).ToList();

        // Looser than the synthetic gate on purpose. These pages carry real
        // text and vector content, where font rasterisation and hinting differ
        // between engines by more than a hand-built red rectangle does. The
        // failure this must catch is a MISSING or MISPLACED appearance, which
        // is a large fraction of the page, not a fringe.
        var tolerance = Math.Max(2, majority.Count / 5);

        var failures = new List<string>();
        if (missing.Count > tolerance)
            failures.Add($"excise left {missing.Count}/{majority.Count} majority-inked tiles blank " +
                         $"(tolerance {tolerance}) — a missing or misplaced appearance");
        if (extra.Count > tolerance)
            failures.Add($"excise inked {extra.Count} tiles no majority did (tolerance {tolerance})");

        failures.Should().BeEmpty(
            $"{fixture}: excise must draw a real /AP where the oracle majority does.\n  " +
            string.Join("\n  ", failures) + $"\n      excise      {ours.Count,5} tiles\n{report}");
    }

    private static HashSet<(int X, int Y)> MajorityTiles(List<HashSet<(int X, int Y)>> sets)
    {
        var counts = new Dictionary<(int, int), int>();
        foreach (var s in sets)
            foreach (var t in s)
                counts[t] = counts.GetValueOrDefault(t) + 1;
        return counts.Where(kv => kv.Value * 2 > sets.Count).Select(kv => kv.Key).ToHashSet();
    }

    /// <summary>
    /// The tiles overlapping any annotation <c>/Rect</c> on page 1, in device
    /// space, with a one-tile margin so a border stroked just outside the rect
    /// still counts.
    /// </summary>
    private static HashSet<(int X, int Y)> AnnotationWindow(string path, out int annotCount)
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(path));
        var page = doc.GetPage(1);
        var annots = page.GetAnnotations();
        annotCount = annots.Count;

        var pageHeight = page.CropBox.Height;
        var window = new HashSet<(int, int)>();
        foreach (var a in annots)
        {
            var r = a.Rect;
            // PDF y-up to device y-down.
            var x0 = Math.Min(r.Left, r.Right);
            var x1 = Math.Max(r.Left, r.Right);
            var yTop = pageHeight - Math.Max(r.Bottom, r.Top);
            var yBot = pageHeight - Math.Min(r.Bottom, r.Top);

            for (var ty = (int)(yTop / Tile) - 1; ty <= (int)(yBot / Tile) + 1; ty++)
                for (var tx = (int)(x0 / Tile) - 1; tx <= (int)(x1 / Tile) + 1; tx++)
                    if (tx >= 0 && ty >= 0) window.Add((tx, ty));
        }
        return window;
    }

    private static HashSet<(int X, int Y)> InkedTiles(SKBitmap bmp, HashSet<(int X, int Y)> window)
    {
        var tiles = new HashSet<(int, int)>();
        for (var ty = 0; ty * Tile < bmp.Height; ty++)
            for (var tx = 0; tx * Tile < bmp.Width; tx++)
            {
                if (!window.Contains((tx, ty))) continue;
                var inked = 0;
                for (var y = ty * Tile; y < Math.Min((ty + 1) * Tile, bmp.Height); y++)
                    for (var x = tx * Tile; x < Math.Min((tx + 1) * Tile, bmp.Width); x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        if (c.Alpha > 128 && (c.Red < 200 || c.Green < 200 || c.Blue < 200)) inked++;
                    }
                if (inked > Tile * Tile / 20) tiles.Add((tx, ty));
            }
        return tiles;
    }

    private static SKBitmap RenderWithExcise(string path)
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(path));
        var bmp = new SkiaRenderer().RenderPage(doc.GetPage(1), new RenderOptions
        {
            Dpi = Dpi,
            RenderAnnotations = true,
        });
        bmp.Should().NotBeNull("excise must render the page");
        return bmp!;
    }

    private static string? Resolve(string rel)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, rel);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
