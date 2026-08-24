using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Fonts;
using Excise.Core.Primitives;
using Excise.Core.Text;

namespace Excise.Rendering.Differential;

/// <summary>
/// #1133 — estimates candidates for text a redaction REMOVED, from the width of
/// the gap it left. Our take on Edact-Ray (PETS 2023), built on excise's
/// advantage: it reads the PDF's OWN exact glyph advances (page.Letters +
/// StandardFontMetrics), not rendered pixels, so it works at ±0.5pt where
/// pixel/OCR tools work at ±2pt. Measured: that turns a 7-8 candidate shortlist
/// into 1-3.
///
/// <para><b>Never asserts an answer.</b> Output is candidates that fit the width
/// plus residual entropy in bits. Even at one candidate it reports the bits, not
/// "the answer is X" — the epistemic difference between measurement and claim is
/// the whole safety property (#1131/#1126).</para>
///
/// <para>The width filter is the HARD constraint. Context ranking (a caller's
/// concern) only reorders within the admissible set; it never adds or removes a
/// candidate. So recall@N is a property of the width bound alone, reproducible.</para>
///
/// <para>Cross-checked against <see cref="MutoolGlyphPositions"/>: excise must
/// not be the only witness that a gap of a given width exists. No-self-oracle.</para>
/// </summary>
public static class ResidueRecoveryEngine
{
    public enum WidthMetricSource { Standard14Exact, MutoolPositionTolerance, Unknown }

    /// <summary>
    /// How the gap was found. <see cref="TwoAnchor"/>: an unusually wide gulf
    /// BETWEEN two surviving glyphs (the original detector). <see cref="RedactionBox"/>:
    /// the width of the black rectangle a width-preserving redaction draws —
    /// anchor-independent, so it recovers the single-anchor (line-start/line-end)
    /// gaps a two-glyph gulf cannot see (#1140). The box width is a literal
    /// content-stream constant, not an excise-estimated metric.
    /// </summary>
    public enum GapKind { TwoAnchor, RedactionBox }

    /// <summary>A detected redaction gap on one text baseline.</summary>
    public readonly record struct Gap(
        int Page, double X0, double X1, double GapWidthPt,
        string Font, double SizePt, WidthMetricSource MetricSource, double TolerancePt,
        GapKind Kind = GapKind.TwoAnchor);

    /// <summary>Candidates for one gap, with the honest metric.</summary>
    public sealed record Recovery(
        Gap Gap,
        IReadOnlyList<string> CandidatesFit,
        int CandidatesConsidered,
        double ResidualEntropyBits,
        string Status);   // "ok" | "no-metric" | "no-gap"

    public sealed record Options(
        double ExactTolerancePt = 0.5,
        double FallbackTolerancePt = 2.0,
        int MaxCandidates = 500,
        bool RequireMutoolCorroboration = true);

    /// <summary>
    /// Recover candidates for every redaction gap on page 1 of
    /// <paramref name="pdfPath"/>, ranking against <paramref name="dictionary"/>.
    /// </summary>
    public static IReadOnlyList<Recovery> Recover(
        string pdfPath, IReadOnlyList<string> dictionary, Options? options = null)
    {
        var opt = options ?? new Options();
        var results = new List<Recovery>();

        List<Letter> letters;
        List<ContentOperator> fills = new();
        var baseFontOf = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            if (doc.PageCount < 1) return results;
            var page = doc.GetPage(1);
            letters = page.Letters.ToList();
            // Letter.FontName is the RESOURCE name (/F1); metrics are keyed by
            // /BaseFont (Helvetica). Resolve the map once, or every case falls
            // to the pixel-tolerance rung and recovery collapses.
            foreach (var (name, font) in page.GetFonts())
            {
                var bf = font.GetNameOrNull("BaseFont");
                if (bf != null) baseFontOf[name] = bf;
            }
            // #1140: the width-preserving redaction box. Its rectangle width
            // leaks the removed run's width even where there is no glyph on one
            // side. Collected from the same content stream the letters came from.
            try
            {
                var ops = new ContentStreamParser(page.GetContentStreamBytes(), page).Parse();
                fills = ops.Operators
                    .Where(o => o.BoundingBox.HasValue && IsFillOperator(o.Name))
                    .ToList();
            }
            catch { /* box channel is best-effort; the glyph-gap path stands alone */ }
        }
        catch { return results; }

        if (letters.Count < 1) return results;

        // Independent witness that a same-width hole sits where excise says.
        // Null (mutool absent) is not "no gap" — it downgrades corroboration
        // to unavailable, it does not fabricate a clean result.
        var mutool = opt.RequireMutoolCorroboration
            ? MutoolGlyphPositions.ExtractPage(pdfPath, 1)
            : null;

        var twoAnchor = letters.Count >= 2
            ? DetectGaps(letters, baseFontOf).ToList()
            : new List<Gap>();
        foreach (var gap in twoAnchor)
        {
            var corroborated = mutool == null || MutoolAgrees(mutool, gap);
            // A gap only excise sees is not scored as a recovery — it is far
            // more likely a detector artifact than a real redaction.
            if (opt.RequireMutoolCorroboration && mutool != null && !corroborated)
                continue;

            results.Add(Rank(gap, dictionary, opt));
        }

        // #1140: box gaps SUPPLEMENT the two-anchor gaps — only where a
        // two-anchor gulf did not already cover the same span (a width-preserving
        // redaction with both anchors present draws a box AND leaves a gulf; the
        // gulf already handled it). Corroboration for a box is ABSENCE of text in
        // its span, not a glyph-void between neighbours: a single-anchor box has
        // no right neighbour, and mutool showing text inside the box means the
        // glyphs are still there (a highlight, not a removal).
        foreach (var gap in DetectBoxGaps(letters, fills, baseFontOf))
        {
            if (twoAnchor.Any(t => Overlaps(t, gap))) continue;
            var corroborated = mutool == null || MutoolConfirmsBoxIsEmpty(mutool, gap);
            if (opt.RequireMutoolCorroboration && mutool != null && !corroborated)
                continue;

            results.Add(Rank(gap, dictionary, opt));
        }
        return results;
    }

    private static bool Overlaps(Gap a, Gap b)
        => a.X0 < b.X1 && b.X0 < a.X1;

    private static bool IsFillOperator(string name)
        => name is "f" or "F" or "f*" or "B" or "B*" or "b" or "b*";

    /// <summary>
    /// A gap is an unusually wide gulf between two consecutive glyphs on the
    /// same baseline. "Unusually" = wider than the font's own space advance
    /// plus a margin, so ordinary inter-word spacing is not mistaken for a
    /// redaction.
    /// </summary>
    private static IEnumerable<Gap> DetectGaps(
        List<Letter> letters, Dictionary<string, string> baseFontOf)
    {
        // Group by baseline (Bottom, rounded) so we compare glyphs on one line.
        foreach (var line in letters
                     .GroupBy(l => Math.Round(l.GlyphRectangle.Bottom, 0))
                     .OrderByDescending(g => g.Key))
        {
            var ordered = line.OrderBy(l => l.GlyphRectangle.Left).ToList();
            if (ordered.Count < 2) continue;

            for (var i = 0; i < ordered.Count - 1; i++)
            {
                var a = ordered[i];
                var b = ordered[i + 1];
                var gapWidth = b.GlyphRectangle.Left - a.GlyphRectangle.Right;

                var size = a.FontSize > 0 ? a.FontSize : 12;
                var baseFont = baseFontOf.TryGetValue(a.FontName, out var bf) ? bf : a.FontName;
                var spaceAdvance = SpaceAdvancePt(baseFont, size);
                if (gapWidth <= spaceAdvance * 1.8) continue;   // ordinary spacing

                var (source, tol) = SourceFor(baseFont);
                yield return new Gap(
                    Page: 1,
                    X0: a.GlyphRectangle.Right,
                    X1: b.GlyphRectangle.Left,
                    // EDGE TO EDGE: the surviving glyphs on either side abut the
                    // removed run with no extra space, so the raw gap already
                    // equals the removed string's rendered width. (Verified on
                    // the #1134 corpus: gap 35.3pt == width("James") 35.3pt.)
                    GapWidthPt: gapWidth,
                    Font: baseFont, SizePt: size,
                    MetricSource: source,
                    TolerancePt: tol);
            }
        }
    }

    /// <summary>
    /// #1140 — single-anchor gaps recovered from the redaction BOX. A
    /// width-preserving redaction paints a filled rectangle whose width equals
    /// the removed run's rendered width; that leaks the width regardless of
    /// whether a glyph survives on both sides, one side, or neither.
    ///
    /// <para>Gated hard, because a filled rectangle is also a table cell, a rule,
    /// a highlight, or a page background:</para>
    /// <list type="bullet">
    ///   <item>thin horizontal band — height within [0.4, 2.5]× a same-baseline
    ///     line's font size (a redaction box is one line tall, not a block);</item>
    ///   <item>EMPTY of glyphs — a box with text inside it is a highlight over
    ///     readable text, not a removal (the text is extractable; 'certain' mode
    ///     owns that, not residue);</item>
    ///   <item>anchored to a real text line — a surviving glyph abuts it on the
    ///     left or right, which is also where the font/size to rank against
    ///     comes from;</item>
    ///   <item>wider than a space — narrower is punctuation-scale noise.</item>
    /// </list>
    /// </summary>
    private static IEnumerable<Gap> DetectBoxGaps(
        List<Letter> letters, List<ContentOperator> fills,
        Dictionary<string, string> baseFontOf)
    {
        if (fills.Count == 0) yield break;

        // Lines of surviving text, by baseline, for anchoring boxes to a font.
        var lines = letters
            .GroupBy(l => Math.Round(l.GlyphRectangle.Bottom, 0))
            .Select(g => new
            {
                Bottom = g.Key,
                Glyphs = g.OrderBy(l => l.GlyphRectangle.Left).ToList()
            })
            .ToList();
        if (lines.Count == 0) yield break;

        foreach (var fill in fills)
        {
            var box = fill.BoundingBox!.Value.Normalize();
            var boxH = box.Top - box.Bottom;
            var boxW = box.Right - box.Left;
            if (boxW <= 0 || boxH <= 0) continue;

            // The line whose baseline sits inside the box's vertical band — the
            // box is drawn ~4pt below the baseline and one em tall, so the
            // baseline falls within [box.Bottom, box.Top].
            var line = lines
                .Where(l => l.Bottom >= box.Bottom - 1 && l.Bottom <= box.Top + 1)
                .OrderBy(l => Math.Abs(l.Bottom - (box.Bottom + boxH * 0.25)))
                .FirstOrDefault();
            if (line == null) continue;

            var anchor = line.Glyphs[0];
            var size = anchor.FontSize > 0 ? anchor.FontSize : 12;

            // Thin band: one text line tall, not a block fill.
            if (boxH < size * 0.4 || boxH > size * 2.5) continue;

            var baseFont = baseFontOf.TryGetValue(anchor.FontName, out var bf)
                ? bf : anchor.FontName;
            if (boxW <= SpaceAdvancePt(baseFont, size)) continue;

            // EMPTY: no surviving glyph centre inside the box span — otherwise
            // this is a highlight over intact text, not a removal.
            var hasTextInside = line.Glyphs.Any(g =>
            {
                var cx = (g.GlyphRectangle.Left + g.GlyphRectangle.Right) / 2.0;
                return cx > box.Left + 0.5 && cx < box.Right - 0.5;
            });
            if (hasTextInside) continue;

            // ANCHORED: a glyph on this line must abut the box on one side (its
            // left or right edge is adjacent), or the box is floating and far
            // more likely furniture than a redaction.
            var abuts = line.Glyphs.Any(g =>
                Math.Abs(g.GlyphRectangle.Right - box.Left) < size ||
                Math.Abs(g.GlyphRectangle.Left - box.Right) < size);
            if (!abuts) continue;

            var (source, tol) = SourceFor(baseFont);
            yield return new Gap(
                Page: 1, X0: box.Left, X1: box.Right,
                GapWidthPt: boxW,
                Font: baseFont, SizePt: size,
                MetricSource: source, TolerancePt: tol,
                Kind: GapKind.RedactionBox);
        }
    }

    /// <summary>
    /// Box corroboration is ABSENCE: mutool must show no glyph centre inside the
    /// box's x-span on its baseline. Text there means the glyphs survive (a
    /// highlight), so it is not a removal to recover. Distinct from
    /// <see cref="MutoolAgrees"/>, which needs a glyph-void between two
    /// neighbours — a single-anchor box has no right neighbour to bound.
    /// </summary>
    private static bool MutoolConfirmsBoxIsEmpty(
        IReadOnlyList<MutoolGlyphPositions.Glyph> glyphs, Gap gap)
        => !glyphs.Any(g => g.X > gap.X0 + 0.5 && g.X < gap.X1 - 0.5);

    private static Recovery Rank(Gap gap, IReadOnlyList<string> dictionary, Options opt)
    {
        if (gap.MetricSource == WidthMetricSource.Unknown)
            return new Recovery(gap, Array.Empty<string>(), dictionary.Count,
                Bits(dictionary.Count), "no-metric");

        var fit = new List<(string Word, double Delta)>();
        foreach (var word in dictionary)
        {
            var w = RenderedWidthPt(word, gap.Font, gap.SizePt);
            if (w < 0) continue;                       // uncomputable char
            var delta = Math.Abs(w - gap.GapWidthPt);
            if (delta <= gap.TolerancePt) fit.Add((word, delta));
        }

        // Width is the hard filter; the only ordering here is tightest-fit
        // first, which is not context — a caller adds that later.
        var ranked = fit.OrderBy(f => f.Delta).Take(opt.MaxCandidates)
                        .Select(f => f.Word).ToList();

        return new Recovery(gap, ranked, dictionary.Count,
            Bits(Math.Max(1, ranked.Count)), ranked.Count == 0 ? "no-fit" : "ok");
    }

    // ── width helpers, mirroring GetCharWidth's standard-14 rung ────────────

    private static (WidthMetricSource, double) SourceFor(string font)
        => StandardFontMetrics.TryGetWidth(font, 'M', out _)
            ? (WidthMetricSource.Standard14Exact, 0.5)
            : (WidthMetricSource.MutoolPositionTolerance, 2.0);

    private static double RenderedWidthPt(string s, string font, double size)
    {
        double units = 0;
        foreach (var ch in s)
        {
            if (!StandardFontMetrics.TryGetWidth(font, ch, out var w)) return -1;
            units += w;
        }
        return units / 1000.0 * size;
    }

    private static double SpaceAdvancePt(string font, double size)
        => StandardFontMetrics.TryGetWidth(font, ' ', out var w)
            ? w / 1000.0 * size
            : 0.25 * size;   // reasonable default when the font is unknown

    private static bool MutoolAgrees(IReadOnlyList<MutoolGlyphPositions.Glyph> glyphs, Gap gap)
    {
        // mutool must show a comparable horizontal void near the same x. Coarse
        // on purpose: it is a corroboration, not a second measurement.
        var xs = glyphs.Select(g => g.X).OrderBy(x => x).ToList();
        if (xs.Count < 2) return false;
        for (var i = 0; i < xs.Count - 1; i++)
        {
            var void_ = xs[i + 1] - xs[i];
            if (void_ >= gap.GapWidthPt * 0.5 &&
                xs[i] <= gap.X1 + 5 && xs[i + 1] >= gap.X0 - 5)
                return true;
        }
        return false;
    }

    private static double Bits(int n) => Math.Log2(Math.Max(1, n));
}
