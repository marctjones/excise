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
    /// produce a meaningful selection range we re-sort into reading
    /// order: top-to-bottom by line, then left-to-right within line.
    /// Two letters share a line if their vertical centres differ by
    /// less than half the smaller font size.
    /// </summary>
    public static List<Letter> SortReadingOrder(IEnumerable<Letter> letters)
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
    /// Joined text of a letter run. Inserts a single space when the gap
    /// between consecutive letters on the same line exceeds half the
    /// glyph height (typical word boundary), and a newline when crossing
    /// to a different line.
    /// </summary>
    public static string JoinText(IReadOnlyList<Letter> letters)
    {
        if (letters.Count == 0) return string.Empty;
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
            {
                sb.Append('\n');
            }
            else
            {
                // Horizontal gap, measured direction-agnostically so a word
                // break is detected whether the run reads left-to-right (LTR,
                // prev on the left) or right-to-left (RTL logical order, prev
                // on the right). For every existing ascending-X caller this is
                // identical to the old `cur.Left - prev.Right` (#373).
                var gap = Math.Max(cur.Left - prev.Right, prev.Left - cur.Right);
                if (gap > 0.5 * lineHeight) sb.Append(' ');
            }
            sb.Append(letters[i].Value);
        }
        return sb.ToString();
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
        double columnGapThreshold)
    {
        var visualRange = ColumnAwareRange(readingOrdered, anchor, focus, columnGapThreshold);
        var logical = ToLogicalOrder(visualRange, logicalPageLetters);
        return new SelectionResult(visualRange, JoinText(logical));
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
