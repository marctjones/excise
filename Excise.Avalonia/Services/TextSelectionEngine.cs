using Excise.Core.Document;
using Excise.Core.Text;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Excise.Avalonia.Services;

/// <summary>
/// Pure-logic letter hit-testing and reading-order range computation
/// for text selection. The control feeds in pointer coordinates (in
/// PDF points), this returns the run of letters between anchor and
/// focus in reading order — the shape of text the user expects when
/// they drag from word A on line N to word B on line M.
/// </summary>
public static class TextSelectionEngine
{
    /// <summary>
    /// Find the letter at the given PDF-space point, or the closest letter
    /// on the same line if the point isn't directly over any glyph.
    /// Returns null only when the page has no letters at all.
    /// </summary>
    public static Letter? HitTest(IReadOnlyList<Letter> letters, double pdfX, double pdfY)
    {
        if (letters.Count == 0) return null;

        // 1) Direct hit — pointer lies inside a glyph rect.
        Letter? insideHit = null;
        foreach (var l in letters)
        {
            var r = l.GlyphRectangle;
            if (pdfX >= r.Left && pdfX <= r.Right &&
                pdfY >= r.Bottom && pdfY <= r.Top)
            {
                insideHit = l;
                break;
            }
        }
        if (insideHit != null) return insideHit;

        // 2) No direct hit — find the line the pointer is on (Y closest
        // to the glyph's vertical centre) and pick the X-closest letter.
        Letter? best = null;
        double bestDist = double.PositiveInfinity;
        foreach (var l in letters)
        {
            var r = l.GlyphRectangle;
            var cy = (r.Bottom + r.Top) * 0.5;
            // Penalise vertical distance heavily so we anchor to the
            // pointer's *line* and only pick X-closest within it.
            var dy = Math.Abs(pdfY - cy);
            var dx = pdfX < r.Left ? r.Left - pdfX
                   : pdfX > r.Right ? pdfX - r.Right
                   : 0;
            var dist = dy * 4.0 + dx;
            if (dist < bestDist) { bestDist = dist; best = l; }
        }
        return best;
    }

    /// <summary>
    /// Letters are returned by Excise.Core.Text in glyph-emit order. To
    /// produce a meaningful selection range we re-sort into reading order.
    /// Uses the <see cref="ReadingOrderStrategy.ColumnAware"/> strategy — the
    /// highest-quality copy default (#774). Overload kept parameterless so
    /// every existing call site inherits the new default.
    /// </summary>
    public static List<Letter> SortReadingOrder(IEnumerable<Letter> letters)
        => SortReadingOrder(letters, ReadingOrderStrategy.ColumnAware);

    /// <summary>
    /// Re-sort a page's glyphs into the linear sequence used for selection and
    /// copy, per the chosen <paramref name="strategy"/> (#774):
    /// <list type="bullet">
    /// <item><see cref="ReadingOrderStrategy.RawStream"/> — the order Excise.Core
    /// emitted (content-stream / logical order), untouched.</item>
    /// <item><see cref="ReadingOrderStrategy.Simple"/> — geometric: lines
    /// top-to-bottom, glyphs left-to-right within a line. Interleaves columns
    /// that share a vertical band.</item>
    /// <item><see cref="ReadingOrderStrategy.ColumnAware"/> (default) — detect
    /// vertical column gutters and emit each column top-to-bottom before the
    /// next, so a whole-page/cross-column copy reads column-by-column.
    /// Single-column pages fall through to identical <c>Simple</c> output.</item>
    /// </list>
    /// </summary>
    public static List<Letter> SortReadingOrder(IEnumerable<Letter> letters, ReadingOrderStrategy strategy)
    {
        return strategy switch
        {
            ReadingOrderStrategy.RawStream => letters.ToList(),
            ReadingOrderStrategy.Simple => SortSimple(letters),
            _ => SortColumnAware(letters),
        };
    }

    /// <summary>
    /// Geometric reading order: lines top-to-bottom, glyphs left-to-right.
    /// This is the pre-#774 behaviour, preserved verbatim so single-column
    /// output is byte-identical.
    /// </summary>
    private static List<Letter> SortSimple(IEnumerable<Letter> letters)
    {
        var lines = GroupIntoLines(letters);

        // Sort each line left-to-right (visual order — see class remarks).
        foreach (var line in lines)
            line.Sort((a, b) => a.GlyphRectangle.Left.CompareTo(b.GlyphRectangle.Left));

        // Sort lines top-to-bottom (PDF Y-up: descending centre Y).
        lines.Sort((a, b) => LineCentreY(b).CompareTo(LineCentreY(a)));

        return lines.SelectMany(l => l).ToList();
    }

    /// <summary>
    /// Column-aware reading order (#774): partition the page into left-to-right
    /// column bands separated by detected vertical gutters, then emit each band
    /// top-to-bottom/left-to-right (<see cref="SortSimple"/>) before the next.
    /// When no gutter qualifies (single-column, or a full-width line spans the
    /// page) this is exactly <see cref="SortSimple"/> — so single-column copy is
    /// byte-identical and the change is provably scoped to genuine columns.
    /// </summary>
    private static List<Letter> SortColumnAware(IEnumerable<Letter> letters)
    {
        var all = letters as IReadOnlyList<Letter> ?? letters.ToList();
        var boundaries = DetectColumnBoundaries(all);
        if (boundaries.Count == 0)
            return SortSimple(all);

        // Bucket letters by which column their horizontal centre falls in.
        var buckets = new List<Letter>[boundaries.Count + 1];
        for (int i = 0; i < buckets.Length; i++) buckets[i] = new List<Letter>();
        foreach (var l in all)
        {
            var r = l.GlyphRectangle;
            var cx = (r.Left + r.Right) * 0.5;
            int col = 0;
            while (col < boundaries.Count && cx > boundaries[col]) col++;
            buckets[col].Add(l);
        }

        // Each column internally uses the simple geometric order; columns are
        // concatenated left-to-right.
        var result = new List<Letter>(all.Count);
        foreach (var bucket in buckets)
            result.AddRange(SortSimple(bucket));
        return result;
    }

    /// <summary>
    /// Detect vertical column-gutter X positions, ordered left-to-right. A
    /// gutter is an interior X-interval wide enough to be a column separator
    /// (wider than <see cref="EstimateColumnGap"/>) that has a genuine
    /// multi-line text block on <em>both</em> sides, the two blocks running in
    /// parallel down most of the page (see <see cref="IsColumnGutter"/>). That
    /// two-tall-blocks test is what distinguishes a real column boundary from a
    /// wide word space, an indent, or a ragged margin, so <c>hello[ ]world</c>
    /// never splits. Returns an empty list (→ single column) when nothing
    /// qualifies.
    ///
    /// Bounded on purpose (#774): detection is global, so a full-width line
    /// (banner heading, footer) spanning the gutter defeats it and the page
    /// falls back to single-column order. Baseline-aligned wide-gap tables,
    /// nested/uneven columns, and text wrapping around figures are out of
    /// scope — the conservative bar means those degrade to the old geometric
    /// order rather than mis-splitting (a narrow-gap table never produces a
    /// candidate gutter at all).
    /// </summary>
    internal static List<double> DetectColumnBoundaries(IReadOnlyList<Letter> letters)
    {
        var empty = new List<double>();
        if (letters.Count < 8) return empty;

        var lines = GroupIntoLines(letters);
        if (lines.Count < 4) return empty;

        var gutterThreshold = EstimateColumnGap(letters);
        if (double.IsInfinity(gutterThreshold)) return empty;

        // Vertical content extent (by line centre) — a real gutter must run
        // down most of it.
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (var line in lines)
        {
            var cy = LineCentreY(line);
            if (cy < minY) minY = cy;
            if (cy > maxY) maxY = cy;
        }
        var contentHeight = maxY - minY;
        if (contentHeight <= 0) return empty;

        // Candidate interior gaps: sweep glyphs by X and record maximal
        // uncovered X-intervals wider than the gutter threshold. Columns are
        // horizontally disjoint, so their separating gutter shows up as a gap
        // where the running right edge never reaches the next glyph's left.
        var sorted = letters.OrderBy(l => l.GlyphRectangle.Left).ToList();
        double runningRight = double.NegativeInfinity;
        var boundaries = new List<double>();
        foreach (var l in sorted)
        {
            var r = l.GlyphRectangle;
            if (!double.IsNegativeInfinity(runningRight) &&
                r.Left - runningRight > gutterThreshold)
            {
                var gutterCentre = (runningRight + r.Left) * 0.5;
                if (IsColumnGutter(letters, gutterCentre, contentHeight))
                    boundaries.Add(gutterCentre);
            }
            runningRight = Math.Max(runningRight, r.Right);
        }

        boundaries.Sort();
        return boundaries;
    }

    /// <summary>
    /// True when <paramref name="gutterCentre"/> separates two genuine column
    /// blocks: the glyphs whose centre falls left of it and those to its right
    /// each form a block of at least three text lines, and the two blocks'
    /// vertical extents overlap over at least half the page's content height
    /// (they run in parallel down the page). Real columns have <em>staggered</em>
    /// baselines across the gutter — so a per-line "same line straddles the
    /// gap" test fails on exactly the layout we want to split; measuring each
    /// side as its own block sidesteps that. A lone wide gap on one ragged line
    /// (<c>hello[ ]world</c>) has a one-line right block and is rejected.
    /// </summary>
    private static bool IsColumnGutter(
        IReadOnlyList<Letter> letters, double gutterCentre, double contentHeight)
    {
        var leftSide = new List<Letter>();
        var rightSide = new List<Letter>();
        foreach (var l in letters)
        {
            var cx = (l.GlyphRectangle.Left + l.GlyphRectangle.Right) * 0.5;
            if (cx < gutterCentre) leftSide.Add(l);
            else rightSide.Add(l);
        }

        var leftLines = GroupIntoLines(leftSide);
        var rightLines = GroupIntoLines(rightSide);
        if (leftLines.Count < 3 || rightLines.Count < 3) return false;

        var (lMin, lMax) = VerticalExtent(leftLines);
        var (rMin, rMax) = VerticalExtent(rightLines);
        var overlap = Math.Min(lMax, rMax) - Math.Max(lMin, rMin);
        return overlap >= 0.5 * contentHeight;
    }

    /// <summary>Min/max line-centre Y across a set of lines.</summary>
    private static (double Min, double Max) VerticalExtent(List<List<Letter>> lines)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var line in lines)
        {
            var cy = LineCentreY(line);
            if (cy < min) min = cy;
            if (cy > max) max = cy;
        }
        return (min, max);
    }

    /// <summary>
    /// Bucket letters into approximate lines by baseline/centre Y. Two letters
    /// share a line when their vertical centres differ by less than half the
    /// smaller font size (kerned text and superscripts stay on one line).
    /// Within a returned line the letters keep their input relative order;
    /// callers sort as they need (visual L-R for selection ranges, logical
    /// page order for copied text). Lines themselves are unsorted.
    /// </summary>
    private static List<List<Letter>> GroupIntoLines(IEnumerable<Letter> letters)
    {
        var ordered = letters
            .OrderByDescending(l => l.GlyphRectangle.Top)  // PDF Y-up: higher Top = earlier
            .ToList();

        var lines = new List<List<Letter>>();
        foreach (var l in ordered)
        {
            var cy = (l.GlyphRectangle.Bottom + l.GlyphRectangle.Top) * 0.5;
            List<Letter>? hostLine = null;
            foreach (var line in lines)
            {
                var sample = line[0];
                var sampleCy = (sample.GlyphRectangle.Bottom + sample.GlyphRectangle.Top) * 0.5;
                var tol = 0.5 * Math.Min(l.FontSize, sample.FontSize);
                if (tol <= 0) tol = 4.0;
                if (Math.Abs(sampleCy - cy) <= tol) { hostLine = line; break; }
            }
            if (hostLine != null) hostLine.Add(l);
            else lines.Add(new List<Letter> { l });
        }

        return lines;
    }

    private static double LineCentreY(List<Letter> line)
    {
        var r = line[0].GlyphRectangle;
        return (r.Bottom + r.Top) * 0.5;
    }

    /// <summary>
    /// Range of letters between <paramref name="anchor"/> and
    /// <paramref name="focus"/> in reading order. <paramref name="ordered"/>
    /// must be the output of <see cref="SortReadingOrder"/>. Inclusive of
    /// both endpoints. Returns empty list if either endpoint isn't found
    /// in the ordered set.
    /// </summary>
    public static List<Letter> RangeBetween(IReadOnlyList<Letter> ordered, Letter anchor, Letter focus)
    {
        var aIdx = -1; var fIdx = -1;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ReferenceEquals(ordered[i], anchor)) aIdx = i;
            if (ReferenceEquals(ordered[i], focus)) fIdx = i;
            if (aIdx >= 0 && fIdx >= 0) break;
        }
        if (aIdx < 0 || fIdx < 0) return new List<Letter>();

        var lo = Math.Min(aIdx, fIdx);
        var hi = Math.Max(aIdx, fIdx);
        var result = new List<Letter>(hi - lo + 1);
        for (int i = lo; i <= hi; i++) result.Add(ordered[i]);
        return result;
    }

    /// <summary>
    /// Joined text of a letter run in the pre-existing line-faithful mode
    /// (<see cref="WhitespaceMode.LineFaithful"/>). Kept as the parameterless
    /// entry point so every existing call site and test is byte-identical;
    /// callers that want paragraph/list-aware output pass a
    /// <see cref="WhitespaceMode"/>.
    /// </summary>
    public static string JoinText(IReadOnlyList<Letter> letters)
        => JoinText(letters, WhitespaceMode.LineFaithful);

    /// <summary>
    /// Joined text of a letter run per the chosen <paramref name="mode"/>.
    /// Word spacing is identical in both modes: a single space is inserted when
    /// the same-line gap between consecutive glyphs exceeds half the glyph
    /// height (a word boundary, measured direction-agnostically for RTL, #373).
    /// The modes differ only in the vertical dimension —
    /// <see cref="WhitespaceMode.LineFaithful"/> emits one <c>\n</c> per visual
    /// line change; <see cref="WhitespaceMode.Smart"/> adds heuristic paragraph
    /// blank lines and bullet/number list indentation. See
    /// <c>docs/copy-whitespace-reliability.md</c> for measured reliability.
    /// </summary>
    public static string JoinText(IReadOnlyList<Letter> letters, WhitespaceMode mode)
    {
        if (letters.Count == 0) return string.Empty;
        return mode == WhitespaceMode.Smart
            ? JoinSmart(letters)
            : JoinLineFaithful(letters);
    }

    /// <summary>
    /// Pre-existing behaviour, preserved verbatim: single space on a same-line
    /// word gap, single <c>\n</c> on every visual line change.
    /// </summary>
    private static string JoinLineFaithful(IReadOnlyList<Letter> letters)
    {
        var spaceBefore = ComputeWordSpaces(letters);
        var sb = new System.Text.StringBuilder(letters.Count);
        sb.Append(letters[0].Value);
        for (int i = 1; i < letters.Count; i++)
        {
            var prev = letters[i - 1].GlyphRectangle;
            var cur = letters[i].GlyphRectangle;
            var prevCy = (prev.Bottom + prev.Top) * 0.5;
            var curCy = (cur.Bottom + cur.Top) * 0.5;
            var lineHeight = Math.Min(prev.Top - prev.Bottom, cur.Top - cur.Bottom);
            if (Math.Abs(prevCy - curCy) > 0.5 * lineHeight)
                sb.Append('\n');
            else if (spaceBefore[i])
                sb.Append(' ');
            sb.Append(letters[i].Value);
        }
        return sb.ToString();
    }

    /// <summary>Word-space threshold: a same-line origin-to-origin advance this
    /// many times the line's typical advance reads as a word break.</summary>
    private const double WordGapAdvanceFactor = 1.5;

    /// <summary>
    /// Decide, for each letter, whether a same-line word space should precede it.
    ///
    /// This is <b>width-independent</b> on purpose: it compares the glyph's
    /// origin-to-origin ADVANCE (from the content stream, always correct) to the
    /// line's median advance, rather than the width-based gap
    /// <c>cur.Left − prev.Right</c>. Some fonts report a near-zero glyph advance
    /// width (#833), which makes the width-based gap ≈ the full advance and fires
    /// a space between <i>every</i> letter ("w o r r y"). Comparing advances to a
    /// per-line median avoids that entirely: ordinary letters advance by ~the
    /// median (no space), a genuine word gap advances markedly more (space). A
    /// pair where either glyph is already whitespace is skipped — the real space
    /// glyph, appended verbatim, does the separating.
    /// </summary>
    private static bool[] ComputeWordSpaces(IReadOnlyList<Letter> letters)
    {
        var result = new bool[letters.Count];
        var advances = new List<double>();
        var widths = new List<double>();
        int i = 0;
        while (i < letters.Count)
        {
            // Extent of the current visual line (same segmentation as the callers).
            int end = i + 1;
            advances.Clear();
            widths.Clear();
            widths.Add(GlyphWidth(letters[i]));
            bool lineHasWhitespace = IsSpaceGlyph(letters[i].Value);
            while (end < letters.Count && SameLine(letters[end - 1], letters[end]))
            {
                advances.Add(Advance(letters[end - 1], letters[end]));
                widths.Add(GlyphWidth(letters[end]));
                if (IsSpaceGlyph(letters[end].Value)) lineHasWhitespace = true;
                end++;
            }

            // If the line already carries real whitespace glyphs, the PDF is
            // separating words with actual spaces (appended verbatim); the
            // heuristic must stay OUT of the way, or it stacks extra spaces onto
            // width variation ("w orry", "dam ages"). #833.
            if (!lineHasWhitespace)
            {
                double medianWidth = Median(widths);
                double medianAdvance = Median(advances);
                double fontSize = letters[i].FontSize > 0
                    ? letters[i].FontSize
                    : Math.Abs(letters[i].GlyphRectangle.Top - letters[i].GlyphRectangle.Bottom);

                // When the font reports usable glyph widths, keep the original
                // width-based gap rule (unchanged for normal documents). Only
                // when widths are DEGENERATE (~0, #833) fall back to the
                // width-independent advance-vs-median rule.
                bool degenerateWidths = fontSize > 0 && medianWidth < 0.2 * fontSize;
                for (int k = i + 1; k < end; k++)
                {
                    var pr = letters[k - 1].GlyphRectangle;
                    var cr = letters[k].GlyphRectangle;
                    if (degenerateWidths && medianAdvance > 0)
                    {
                        result[k] = Advance(letters[k - 1], letters[k]) > medianAdvance * WordGapAdvanceFactor;
                    }
                    else
                    {
                        var lineHeight = Math.Min(pr.Top - pr.Bottom, cr.Top - cr.Bottom);
                        var gap = Math.Max(cr.Left - pr.Right, pr.Left - cr.Right);
                        result[k] = gap > 0.5 * lineHeight;
                    }
                }
            }
            i = end;
        }
        return result;
    }

    private static double Advance(Letter a, Letter b) =>
        Math.Abs(b.GlyphRectangle.Left - a.GlyphRectangle.Left);

    private static double GlyphWidth(Letter l) =>
        Math.Abs(l.GlyphRectangle.Right - l.GlyphRectangle.Left);

    private static bool SameLine(Letter a, Letter b)
    {
        var ar = a.GlyphRectangle;
        var br = b.GlyphRectangle;
        var aCy = (ar.Bottom + ar.Top) * 0.5;
        var bCy = (br.Bottom + br.Top) * 0.5;
        var lineHeight = Math.Min(ar.Top - ar.Bottom, br.Top - br.Bottom);
        return Math.Abs(aCy - bCy) <= 0.5 * lineHeight;
    }

    private static bool IsSpaceGlyph(string v) =>
        string.IsNullOrEmpty(v) || char.IsWhiteSpace(v[0]);

    /// <summary>
    /// The glyph rectangle to DRAW A SELECTION HIGHLIGHT with — widened to the
    /// glyph's advance when the reported width is degenerate (#833). Some fonts
    /// report a near-zero glyph advance width, which would paint an invisible
    /// ~0-wide sliver; the origin-to-origin advance to the next same-line glyph
    /// (ground-truth position, always correct) is the real visual extent. Only
    /// degenerate widths are overridden — a normal width passes through
    /// unchanged — and the substitute is capped at the font size so a trailing
    /// word gap or line end does not over-paint. Reading direction is preserved.
    /// </summary>
    internal static PdfRectangle EffectiveHighlightRect(IReadOnlyList<Letter> letters, int i)
    {
        var l = letters[i];
        var g = l.GlyphRectangle;
        double width = Math.Abs(g.Right - g.Left);
        double fontSize = l.FontSize > 0 ? l.FontSize : Math.Abs(g.Top - g.Bottom);

        if (fontSize <= 0 || width >= 0.2 * fontSize)
            return g; // width looks real — leave it alone

        double advance = 0;
        if (i + 1 < letters.Count && SameLine(l, letters[i + 1]))
            advance = Math.Abs(letters[i + 1].GlyphRectangle.Left - g.Left);

        double effective = advance > 0 ? Math.Min(advance, fontSize) : Math.Max(fontSize * 0.5, width);
        double right = g.Right >= g.Left ? g.Left + effective : g.Left - effective;
        return new PdfRectangle(g.Left, g.Bottom, right, g.Top);
    }

    // ── Smart whitespace: paragraph + list awareness ─────────────────────────

    /// <summary>Paragraph break when the inter-line gap exceeds this multiple of
    /// the block's typical (median) leading. Conservative on purpose — a value
    /// near 1.0 would split every slightly-loose line.</summary>
    private const double ParagraphGapFactor = 1.6;

    /// <summary>An indent is only recognised (for list nesting) when the line's
    /// left edge sits this many median-glyph-widths right of the block's left
    /// margin. Keeps ordinary ragged left edges from reading as nesting.</summary>
    private const double IndentUnitFactor = 2.0;

    /// <summary>
    /// One visual line distilled from the letter run: its joined text (built
    /// with the same word-spacing rule as line-faithful mode, so word spacing
    /// never regresses), left edge, vertical centre and glyph height.
    /// </summary>
    private readonly record struct JoinLine(string Text, double Left, double CentreY, double Height);

    /// <summary>
    /// <see cref="WhitespaceMode.Smart"/>: line-faithful word spacing, plus a
    /// blank line at detected paragraph breaks and preserved indentation for
    /// bullet/number list items. Wrapped lines are NOT reflowed. The heuristics
    /// and their failure modes are documented and measured in
    /// <c>docs/copy-whitespace-reliability.md</c>.
    /// </summary>
    private static string JoinSmart(IReadOnlyList<Letter> letters)
    {
        var lines = BuildLines(letters);
        if (lines.Count <= 1)
            return lines.Count == 0 ? string.Empty : lines[0].Text;

        // Typical leading = median of consecutive line-centre deltas (PDF Y-up:
        // reading order runs top→bottom, so centres descend and deltas are
        // positive). Robust to the odd large gap.
        var deltas = new List<double>(lines.Count - 1);
        for (int i = 1; i < lines.Count; i++)
        {
            var d = lines[i - 1].CentreY - lines[i].CentreY;
            if (d > 0) deltas.Add(d);
        }
        double medianLeading = Median(deltas);

        // Block left margin = the smallest left edge; indentation is measured
        // relative to it. Indent unit derives from the median glyph height as a
        // stand-in for character width (avoids a second pass over glyphs).
        double leftMargin = double.PositiveInfinity;
        double medianHeight = Median(lines.Select(l => l.Height).ToList());
        foreach (var l in lines)
            if (l.Left < leftMargin) leftMargin = l.Left;
        double indentUnit = Math.Max(medianHeight * 0.5, 1.0);

        var sb = new System.Text.StringBuilder();
        AppendLine(sb, lines[0], leftMargin, indentUnit);
        for (int i = 1; i < lines.Count; i++)
        {
            var prev = lines[i - 1];
            var cur = lines[i];
            bool curIsList = TryGetListMarker(cur.Text, out _);
            var gap = prev.CentreY - cur.CentreY;

            // List items stay tight on their own lines (never blank-separated),
            // so a multi-item list copies as a contiguous list. Otherwise a gap
            // meaningfully larger than the typical leading is a paragraph break.
            if (curIsList)
                sb.Append('\n');
            else if (medianLeading > 0 && gap > medianLeading * ParagraphGapFactor)
                sb.Append("\n\n");
            else
                sb.Append('\n');

            AppendLine(sb, cur, leftMargin, indentUnit);
        }
        return sb.ToString();
    }

    /// <summary>Append one line's text, prefixing preserved indentation (two
    /// spaces per indent level) when the line is a list item sitting right of
    /// the block margin. Non-list lines are emitted flush so ordinary prose is
    /// unindented.</summary>
    private static void AppendLine(
        System.Text.StringBuilder sb, JoinLine line, double leftMargin, double indentUnit)
    {
        if (TryGetListMarker(line.Text, out _))
        {
            int level = (int)Math.Round((line.Left - leftMargin) / (indentUnit * IndentUnitFactor));
            if (level < 0) level = 0;
            if (level > 4) level = 4;
            if (level > 0) sb.Append(' ', level * 2);
        }
        sb.Append(line.Text);
    }

    /// <summary>
    /// Group a reading-ordered letter run into visual lines, joining glyphs
    /// within a line with the line-faithful word-spacing rule. A new line begins
    /// on the same vertical-centre change line-faithful mode breaks on, so the
    /// line segmentation is identical — Smart mode only decides the SEPARATOR
    /// between these lines.
    /// </summary>
    private static List<JoinLine> BuildLines(IReadOnlyList<Letter> letters)
    {
        var result = new List<JoinLine>();
        var sb = new System.Text.StringBuilder();
        double left = double.PositiveInfinity;
        double sumCy = 0, sumH = 0;
        int count = 0;

        void Flush()
        {
            if (count == 0) return;
            result.Add(new JoinLine(sb.ToString(), left, sumCy / count, sumH / count));
            sb.Clear();
            left = double.PositiveInfinity;
            sumCy = 0; sumH = 0; count = 0;
        }

        var spaceBefore = ComputeWordSpaces(letters);
        for (int i = 0; i < letters.Count; i++)
        {
            var cur = letters[i].GlyphRectangle;
            var curCy = (cur.Bottom + cur.Top) * 0.5;
            var curH = cur.Top - cur.Bottom;
            if (i > 0)
            {
                var prev = letters[i - 1].GlyphRectangle;
                var prevCy = (prev.Bottom + prev.Top) * 0.5;
                var lineHeight = Math.Min(prev.Top - prev.Bottom, cur.Top - cur.Bottom);
                if (Math.Abs(prevCy - curCy) > 0.5 * lineHeight)
                    Flush();
                else if (spaceBefore[i])
                    sb.Append(' ');
            }
            sb.Append(letters[i].Value);
            if (cur.Left < left) left = cur.Left;
            sumCy += curCy; sumH += curH; count++;
        }
        Flush();
        return result;
    }

    /// <summary>
    /// True when <paramref name="text"/> begins (after leading spaces) with a
    /// recognised list marker: a bullet (•, ·, ‣, ◦, -, –, —, *) or an ordinal
    /// (<c>N.</c>, <c>N)</c>, <c>a.</c>, <c>a)</c>) followed by whitespace. The
    /// matched marker is returned. This is a lexical guess — see the reliability
    /// doc for where it mis-fires (e.g. a sentence opening with a hyphen, a
    /// table cell that starts with a number).
    /// </summary>
    internal static bool TryGetListMarker(string text, out string marker)
    {
        marker = string.Empty;
        if (string.IsNullOrEmpty(text)) return false;
        int i = 0;
        while (i < text.Length && text[i] == ' ') i++;
        if (i >= text.Length) return false;

        char c = text[i];
        // Single-glyph bullet markers, must be followed by a space/end.
        const string bullets = "•·‣◦*⁃∙-–—";
        if (bullets.IndexOf(c) >= 0)
        {
            if (i + 1 >= text.Length || text[i + 1] == ' ')
            {
                marker = c.ToString();
                return true;
            }
            return false;
        }

        // Ordinal markers: 1-3 digits or a single letter, then '.' or ')' then
        // whitespace/end. "1." / "12)" / "a." / "iv)" (roman falls under letters
        // only as a single char — kept deliberately narrow).
        int start = i;
        if (char.IsDigit(c))
        {
            int digits = 0;
            while (i < text.Length && char.IsDigit(text[i]) && digits < 3) { i++; digits++; }
        }
        else if (char.IsLetter(c) && (i + 1 < text.Length && (text[i + 1] == '.' || text[i + 1] == ')')))
        {
            i++; // single-letter ordinal
        }
        else
        {
            return false;
        }
        if (i < text.Length && (text[i] == '.' || text[i] == ')'))
        {
            i++;
            if (i >= text.Length || text[i] == ' ')
            {
                marker = text.Substring(start, i - start);
                return true;
            }
        }
        return false;
    }

    /// <summary>Median of a value list (0 when empty). Sorts a copy.</summary>
    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    // ── RTL selection + multi-column awareness (#373) ────────────────────────

    /// <summary>
    /// Result of resolving a drag selection: the letters to HIGHLIGHT, in
    /// visual (left-to-right) order so their glyph rectangles are drawn as
    /// the user sees them, and the copied TEXT in logical reading order.
    /// </summary>
    internal readonly record struct SelectionResult(List<Letter> VisualRange, string Text);

    /// <summary>
    /// Resolve a drag from <paramref name="anchor"/> to <paramref name="focus"/>
    /// into the highlight rectangles and the copied text (#373). The highlight
    /// follows visual order (contiguous glyph rects, including within an RTL
    /// run), while the text is re-ordered to logical reading order so Arabic/
    /// Hebrew comes out the way it is read, not the way it is painted.
    /// <paramref name="readingOrdered"/> is <see cref="SortReadingOrder"/>'s
    /// visual output; <paramref name="logicalPageLetters"/> is the page's
    /// letter sequence from Excise.Core (already in logical order via its bidi
    /// reorderer, #632).
    /// </summary>
    internal static SelectionResult BuildSelection(
        IReadOnlyList<Letter> readingOrdered,
        IReadOnlyList<Letter> logicalPageLetters,
        Letter anchor, Letter focus,
        double columnGapThreshold,
        WhitespaceMode whitespaceMode = WhitespaceMode.Smart)
    {
        var visualRange = ColumnAwareRange(readingOrdered, anchor, focus, columnGapThreshold);
        var logical = ToLogicalOrder(visualRange, logicalPageLetters);
        return new SelectionResult(visualRange, JoinText(logical, whitespaceMode));
    }

    /// <summary>
    /// Re-order a visually-selected letter run into logical reading order for
    /// copy/extraction (#373). The selection is built visually so the on-screen
    /// highlight rectangles stay contiguous, but copied RTL text must read in
    /// logical order. This does NOT re-run bidi: it reuses the order Excise.Core
    /// already produced (<see cref="PdfPage"/> letters were reordered by the
    /// extractor's bidi pass, #632) by sorting each line's selected letters on
    /// their index in <paramref name="logicalPageLetters"/>. Selections with no
    /// strong-RTL glyph short-circuit unchanged, so pure-LTR copy is provably
    /// untouched.
    /// </summary>
    internal static List<Letter> ToLogicalOrder(
        IReadOnlyList<Letter> selected, IReadOnlyList<Letter> logicalPageLetters)
    {
        if (selected.Count <= 1) return selected.ToList();

        bool anyRtl = false;
        foreach (var l in selected)
            if (ContainsStrongRtl(l.Value)) { anyRtl = true; break; }
        if (!anyRtl) return selected.ToList();

        var logicalIndex = new Dictionary<Letter, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < logicalPageLetters.Count; i++)
            logicalIndex[logicalPageLetters[i]] = i;

        var lines = GroupIntoLines(selected);
        lines.Sort((a, b) => LineCentreY(b).CompareTo(LineCentreY(a)));

        var result = new List<Letter>(selected.Count);
        foreach (var line in lines)
        {
            line.Sort((a, b) =>
            {
                var ia = logicalIndex.TryGetValue(a, out var va) ? va : int.MaxValue;
                var ib = logicalIndex.TryGetValue(b, out var vb) ? vb : int.MaxValue;
                if (ia != ib) return ia.CompareTo(ib);
                return a.GlyphRectangle.Left.CompareTo(b.GlyphRectangle.Left);
            });
            result.AddRange(line);
        }
        return result;
    }

    /// <summary>
    /// <see cref="RangeBetween"/> with bounded column-gutter awareness (#373):
    /// when both endpoints sit in the same column band, letters that fall in a
    /// DIFFERENT column band (an adjacent column sharing an intervening line's
    /// Y-band) are dropped, so a column-local drag does not vacuum up the
    /// neighbouring column. Cross-column selections (endpoints in different
    /// bands) are left as the plain range — full multi-column reading order is
    /// deferred to #774. Columns are detected as maximal horizontal gaps wider
    /// than <paramref name="columnGapThreshold"/>.
    /// </summary>
    internal static List<Letter> ColumnAwareRange(
        IReadOnlyList<Letter> ordered, Letter anchor, Letter focus, double columnGapThreshold)
    {
        var range = RangeBetween(ordered, anchor, focus);
        if (range.Count == 0 || columnGapThreshold <= 0 || double.IsInfinity(columnGapThreshold))
            return range;

        var bandOf = ComputeColumnBands(ordered, columnGapThreshold);
        if (!bandOf.TryGetValue(anchor, out var anchorBand) ||
            !bandOf.TryGetValue(focus, out var focusBand) ||
            anchorBand != focusBand)
            return range;

        var filtered = new List<Letter>(range.Count);
        foreach (var l in range)
            if (bandOf.TryGetValue(l, out var band) && band == anchorBand)
                filtered.Add(l);
        return filtered;
    }

    /// <summary>
    /// A page-geometry-derived column-gutter width: a horizontal gap wider than
    /// this many times the median glyph width is treated as a column boundary,
    /// not a word space. Returns +∞ when there are no letters (no columns).
    /// </summary>
    internal static double EstimateColumnGap(IReadOnlyList<Letter> letters)
    {
        if (letters.Count == 0) return double.PositiveInfinity;
        var widths = letters
            .Select(l => l.GlyphRectangle.Width)
            .Where(w => w > 0)
            .OrderBy(w => w)
            .ToList();
        if (widths.Count == 0) return double.PositiveInfinity;
        var median = widths[widths.Count / 2];
        return median * ColumnGapFactor;
    }

    /// <summary>Column-gutter width as a multiple of the median glyph width.</summary>
    private const double ColumnGapFactor = 3.0;

    /// <summary>
    /// Assign each letter a left-to-right column-band index by sweeping X and
    /// starting a new band whenever the gap to the running right edge exceeds
    /// <paramref name="gapThreshold"/>. Bands ignore Y (bounded — a full-width
    /// line can merge two columns; full analysis is #774).
    /// </summary>
    private static Dictionary<Letter, int> ComputeColumnBands(
        IReadOnlyList<Letter> letters, double gapThreshold)
    {
        var map = new Dictionary<Letter, int>(ReferenceEqualityComparer.Instance);
        var sorted = letters.OrderBy(l => l.GlyphRectangle.Left).ToList();
        int band = -1;
        double runningRight = double.NegativeInfinity;
        foreach (var l in sorted)
        {
            var r = l.GlyphRectangle;
            if (band < 0 || r.Left - runningRight > gapThreshold)
            {
                band++;
                runningRight = r.Right;
            }
            else
            {
                runningRight = Math.Max(runningRight, r.Right);
            }
            map[l] = band;
        }
        return map;
    }

    /// <summary>
    /// True when <paramref name="value"/> contains a strong right-to-left
    /// scalar (Hebrew/Arabic/Syriac blocks and their presentation forms,
    /// excluding the Arabic-Indic digit ranges which are bidi-weak). This is a
    /// codepoint-range test, not a bidi implementation — it only gates whether
    /// the logical re-order in <see cref="ToLogicalOrder"/> needs to run. Ranges
    /// mirror Excise.Core's reorderer (#632/#373).
    /// </summary>
    internal static bool ContainsStrongRtl(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var c in value)
        {
            if (c >= '\u0660' && c <= '\u0669') continue; // Arabic-Indic digits (weak)
            if (c >= '\u06F0' && c <= '\u06F9') continue; // Extended Arabic-Indic digits (weak)
            if ((c >= '\u0590' && c <= '\u08FF') ||       // Hebrew … Arabic Extended
                (c >= '\uFB1D' && c <= '\uFB4F') ||       // Hebrew presentation forms
                (c >= '\uFB50' && c <= '\uFDFF') ||       // Arabic presentation forms A
                (c >= '\uFE70' && c <= '\uFEFF'))         // Arabic presentation forms B
                return true;
        }
        return false;
    }
}
