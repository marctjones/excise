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
        // First group by approximate line — bucket by rounded baseline Y.
        // Use 50 % of font size as the line-merge tolerance so kerned text
        // and superscripts don't get split into multiple "lines".
        var ordered = letters
            .OrderByDescending(l => l.GlyphRectangle.Top)  // PDF Y-up: higher Top = earlier
            .ToList();

        var lines = new List<List<Letter>>();
        foreach (var l in ordered)
        {
            var cy = (l.GlyphRectangle.Bottom + l.GlyphRectangle.Top) * 0.5;
            // Find an existing line whose centre Y is close.
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

        // Sort each line left-to-right.
        foreach (var line in lines)
            line.Sort((a, b) => a.GlyphRectangle.Left.CompareTo(b.GlyphRectangle.Left));

        // Sort lines top-to-bottom (PDF Y-up: descending centre Y).
        lines.Sort((a, b) =>
        {
            var ay = (a[0].GlyphRectangle.Bottom + a[0].GlyphRectangle.Top) * 0.5;
            var by = (b[0].GlyphRectangle.Bottom + b[0].GlyphRectangle.Top) * 0.5;
            return by.CompareTo(ay);
        });

        return lines.SelectMany(l => l).ToList();
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
                var gap = cur.Left - prev.Right;
                if (gap > 0.5 * lineHeight) sb.Append(' ');
            }
            sb.Append(letters[i].Value);
        }
        return sb.ToString();
    }
}
