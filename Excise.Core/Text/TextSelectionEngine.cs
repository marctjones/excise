using Excise.Core.Document;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Excise.Core.Text;

/// <summary>
/// Pure-logic letter hit-testing and reading-order range computation
/// for text selection. The control feeds in pointer coordinates (in
/// PDF points), this returns the run of letters between anchor and
/// focus in reading order — the shape of text the user expects when
/// they drag from word A on line N to word B on line M.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class TextSelectionEngine
{
    internal enum PageTextOrderStrategy
    {
        RawStream,
        ColumnAware,
    }

    // Whole-page ordering needs stronger evidence than a drag selection. Six
    // scattered form-grid rows can manufacture apparent gutters while still
    // having no objective reading order (#947). Eight clustered lines is the
    // first bounded shape that requires repeated structure beyond that case.
    private const int MinimumStructuredPageLines = 8;
    private const int MinimumStructuredPageLetters = 64;
    private const int MinimumPairedProseLines = 8;

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
            // Penalise vertical distance heavily so we anchor to the pointer's
            // *line* and only pick X-closest within it. The weight must exceed
            // the widest realistic horizontal gap between a pointer and its
            // line's nearest glyph (dragging past a SHORT line's end can be
            // 100s of pt), or an adjacent line's glyph that happens to be
            // X-closer wins and the selection jumps a line. That mis-pick is
            // also what let sub-pixel rounding at fractional zoom flip the drag
            // focus between lines and over-span the highlight (#845). A generous
            // weight keeps a point on a line's Y-band anchored to that line;
            // dragging ONTO another line (pointer on its band, dy≈0) is
            // unaffected.
            var dy = Math.Abs(pdfY - cy);
            var dx = pdfX < r.Left ? r.Left - pdfX
                   : pdfX > r.Right ? pdfX - r.Right
                   : 0;
            var dist = dy * 40.0 + dx;
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
    /// Whole-page text order for <c>PdfPage.Text</c> (#938/#947). Reorder only
    /// when the page has repeated line structure and a validated column gutter;
    /// scattered form grids retain producer order. <see cref="JoinText(IReadOnlyList{Letter}, WhitespaceMode)"/>
    /// supplies geometric spaces and line breaks in either case.
    /// </summary>
    /// <summary>Shared empty result for the boundaries out-parameter: callers
    /// only read it, and allocating a list per page just to say "none" is the
    /// kind of churn #966 was about.</summary>
    private static readonly List<double> EmptyBoundaries = new();

    internal static List<Letter> SortPageTextOrder(IEnumerable<Letter> letters)
    {
        var all = letters as IReadOnlyList<Letter> ?? letters.ToList();
        // Reuse the boundaries the classifier already computed instead of
        // deriving them a second time (#966). DetectColumnBoundaries is not
        // cheap — it groups every letter into lines and sweeps them — and
        // running it twice per page was pure duplicate work, invisible because
        // both calls return the same answer.
        return DeterminePageTextOrder(all, out var boundaries) == PageTextOrderStrategy.ColumnAware
            ? SortStructuredPageText(all, boundaries)
            : all.ToList();
    }

    /// <summary>
    /// Classify whether whole-page geometry is strong enough to override the
    /// PDF producer's content-stream order. Kept separate so corpus fixtures
    /// can pin the verdict independently of the final joined string.
    /// </summary>
    internal static PageTextOrderStrategy DeterminePageTextOrder(IReadOnlyList<Letter> letters)
        => DeterminePageTextOrder(letters, out _);

    internal static PageTextOrderStrategy DeterminePageTextOrder(
        IReadOnlyList<Letter> letters, out List<double> boundaries)
    {
        boundaries = EmptyBoundaries;
        if (letters.Count < MinimumStructuredPageLetters)
            return PageTextOrderStrategy.RawStream;

        // Core has already restored logical order inside RTL runs. A visual
        // left-to-right page sort would undo that work. Vertical writing has
        // the same axis mismatch, so both stay in logical producer order.
        if (letters.Any(letter => ContainsStrongRtl(letter.Value)) ||
            HasPredominantlyVerticalRuns(letters))
        {
            return PageTextOrderStrategy.RawStream;
        }

        var visualLines = GroupIntoLines(letters);
        if (visualLines.Count < MinimumStructuredPageLines)
            return PageTextOrderStrategy.RawStream;

        // Whole-page reordering is deliberately narrower than selection
        // ordering. One stable gutter plus repeated prose on both sides
        // excludes tables, form grids, and mixed/nested layouts. Repeated
        // right-to-left crossings prove the producer actually interleaved the
        // columns; an already column-major stream needs no intervention.
        boundaries = DetectColumnBoundaries(letters, visualLines);
        if (boundaries.Count != 1)
            return PageTextOrderStrategy.RawStream;

        var boundary = boundaries[0];
        var runs = BuildPageTextRuns(letters);
        var bodyLineCount = 0;
        var pairedProseLineCount = 0;
        foreach (var line in GroupPageRunsIntoLines(runs))
        {
            if (line.Any(run => run.Left < boundary && run.Right > boundary))
                continue;

            var contentRuns = line.Where(run => run.AlphanumericCount > 0).ToList();
            if (contentRuns.Count == 0) continue;
            bodyLineCount++;

            var left = contentRuns.Where(run => run.CentreX < boundary).ToList();
            var right = contentRuns.Where(run => run.CentreX >= boundary).ToList();
            if (left.Count == 1 && right.Count == 1 &&
                IsProseRun(left[0]) && IsProseRun(right[0]))
            {
                pairedProseLineCount++;
            }
        }

        if (pairedProseLineCount < MinimumPairedProseLines ||
            pairedProseLineCount * 2 <= bodyLineCount)
        {
            return PageTextOrderStrategy.RawStream;
        }

        var previousColumn = -1;
        var backtrackCount = 0;
        foreach (var run in runs.Where(run => run.AlphanumericCount > 0))
        {
            var column = run.CentreX < boundary ? 0 : 1;
            if (previousColumn == 1 && column == 0) backtrackCount++;
            previousColumn = column;
        }

        return backtrackCount >= Math.Max(3, pairedProseLineCount / 2)
            ? PageTextOrderStrategy.ColumnAware
            : PageTextOrderStrategy.RawStream;
    }

    private static bool IsProseRun(PageTextRun run) =>
        run.AlphanumericCount >= 12 &&
        run.LetterCount >= 10 &&
        run.LetterCount >= 0.75 * run.AlphanumericCount;

    private static bool HasPredominantlyVerticalRuns(IReadOnlyList<Letter> letters)
    {
        var horizontal = 0;
        var vertical = 0;
        for (var i = 1; i < letters.Count; i++)
        {
            var previous = letters[i - 1];
            var current = letters[i];
            if (IsSpaceGlyph(previous.Value) || IsSpaceGlyph(current.Value)) continue;

            var dx = Math.Abs(current.StartX - previous.StartX);
            var dy = Math.Abs(current.StartY - previous.StartY);
            var scale = Math.Max(1, Math.Max(previous.FontSize, current.FontSize));

            // Ignore jumps between independent runs; only neighbouring glyph
            // advances tell us which writing axis the producer used.
            if (Math.Max(dx, dy) > 2.5 * scale) continue;
            if (dy > dx * 1.5) vertical++;
            else if (dx > dy * 1.5) horizontal++;
        }

        return vertical >= 3 && vertical > horizontal;
    }

    /// <summary>
    /// Column-order whole-page text without geometrically re-sorting individual
    /// glyphs. The copy path needs visual glyph order for highlighting; page
    /// extraction needs the extractor's logical order (ligatures, RTL repair,
    /// overlapping runs) preserved. Reorder coherent runs as units instead.
    /// </summary>
    private static List<Letter> SortStructuredPageText(
        IReadOnlyList<Letter> letters, List<double>? knownBoundaries = null)
    {
        var boundaries = knownBoundaries ?? DetectColumnBoundaries(letters);
        if (boundaries.Count == 0) return letters.ToList();

        var lines = GroupPageRunsIntoLines(BuildPageTextRuns(letters));
        var visualLines = GroupIntoLines(letters);
        var spanning = FindSpanningLineGlyphs(
            visualLines,
            letters.Min(letter => letter.GlyphRectangle.Left),
            letters.Max(letter => letter.GlyphRectangle.Right),
            EstimateColumnGap(letters));
        var result = new List<Letter>(letters.Count);
        var band = new List<PageTextRun>();

        void FlushBand()
        {
            if (band.Count == 0) return;

            var columns = new List<PageTextRun>[boundaries.Count + 1];
            for (var i = 0; i < columns.Length; i++) columns[i] = new List<PageTextRun>();
            foreach (var run in band)
            {
                var column = 0;
                while (column < boundaries.Count && run.CentreX > boundaries[column]) column++;
                columns[column].Add(run);
            }

            foreach (var column in columns)
            {
                foreach (var line in GroupPageRunsIntoLines(column))
                    foreach (var run in line.OrderBy(run => run.Left))
                        result.AddRange(run.Letters);
            }
            band.Clear();
        }

        foreach (var line in lines)
        {
            // A coherent run crossing a gutter is a full-width title/header,
            // not column body. It separates column bands and stays in place.
            if (line.Any(run => run.Letters.Any(spanning.Contains) ||
                    boundaries.Any(boundary => run.Left < boundary && run.Right > boundary)))
            {
                FlushBand();
                foreach (var run in line.OrderBy(run => run.Left))
                    result.AddRange(run.Letters);
            }
            else
            {
                band.AddRange(line);
            }
        }
        FlushBand();
        return result;
    }

    private static List<PageTextRun> BuildPageTextRuns(IReadOnlyList<Letter> letters)
    {
        var runs = new List<PageTextRun>();
        List<Letter>? current = null;
        foreach (var letter in letters)
        {
            if (current == null || !ContinuesPageTextRun(current[^1], letter))
            {
                current = new List<Letter>();
                runs.Add(new PageTextRun(current));
            }
            current.Add(letter);
        }
        foreach (var run in runs) run.Seal();
        return runs;
    }

    private static bool ContinuesPageTextRun(Letter previous, Letter current)
    {
        if (!SameLine(previous, current)) return false;

        var fontSize = Math.Max(1, Math.Max(previous.FontSize, current.FontSize));
        var dx = Math.Abs(current.StartX - previous.StartX);
        return dx <= 3 * fontSize;
    }

    private static List<List<PageTextRun>> GroupPageRunsIntoLines(IEnumerable<PageTextRun> runs)
    {
        var lines = new List<List<PageTextRun>>();
        foreach (var run in runs.OrderByDescending(run => run.CentreY))
        {
            List<PageTextRun>? host = null;
            foreach (var line in lines)
            {
                var tolerance = 0.5 * Math.Min(run.Height, line[0].Height);
                if (tolerance <= 0) tolerance = 4;
                if (Math.Abs(run.CentreY - line[0].CentreY) <= tolerance)
                {
                    host = line;
                    break;
                }
            }

            if (host == null) lines.Add(new List<PageTextRun> { run });
            else host.Add(run);
        }

        lines.Sort((left, right) => right[0].CentreY.CompareTo(left[0].CentreY));
        return lines;
    }

    /// <summary>
    /// A run's geometry is FIXED at construction, so it is computed once here
    /// rather than on every property read (#966).
    ///
    /// Each of these was a LINQ pass over the run's letters, evaluated per
    /// access — and the callers read them inside an O(runs x lines) grouping
    /// loop and an O(n log n) sort comparator. On a dense 6-page form that was
    /// 33 MB of enumerator and closure garbage for ONE page, and it is why
    /// page.Text allocated 18x what page.Letters did. The values are
    /// identical; only the number of times they are computed changes.
    ///
    /// The list is captured, not copied: BuildPageTextRuns appends to it AFTER
    /// constructing the run, so the fields are filled by Seal() once the run is
    /// complete. A run is never read before that.
    /// </summary>
    private sealed class PageTextRun
    {
        internal PageTextRun(List<Letter> letters) => Letters = letters;

        internal List<Letter> Letters { get; }

        internal void Seal()
        {
            double left = double.PositiveInfinity, right = double.NegativeInfinity;
            double bottom = double.PositiveInfinity, top = double.NegativeInfinity;
            int letterCount = 0, alnum = 0;
            foreach (var letter in Letters)
            {
                var r = letter.GlyphRectangle;
                if (r.Left < left) left = r.Left;
                if (r.Right > right) right = r.Right;
                if (r.Bottom < bottom) bottom = r.Bottom;
                if (r.Top > top) top = r.Top;
                foreach (var ch in letter.Value)
                {
                    if (char.IsLetter(ch)) letterCount++;
                    if (char.IsLetterOrDigit(ch)) alnum++;
                }
            }
            Left = left; Right = right; Bottom = bottom; Top = top;
            LetterCount = letterCount; AlphanumericCount = alnum;
        }

        internal double Left { get; private set; }
        internal double Right { get; private set; }
        internal double Bottom { get; private set; }
        internal double Top { get; private set; }
        internal double CentreX => (Left + Right) * 0.5;
        internal double CentreY => (Bottom + Top) * 0.5;
        internal double Height => Top - Bottom;
        internal int LetterCount { get; private set; }
        internal int AlphanumericCount { get; private set; }
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

        // Band-segment (#774/#824): walk visual lines top-to-bottom. A continuous
        // full-width line (running header/footer/title/page number) SEPARATES the
        // column bands — flush the current band column-by-column, then emit the
        // spanning line in place. Without this, a header's own glyphs get bucketed
        // into both columns and the header is torn apart.
        double minX = all.Min(l => l.GlyphRectangle.Left);
        double maxX = all.Max(l => l.GlyphRectangle.Right);
        var gutterThreshold = EstimateColumnGap(all);
        var lines = GroupIntoLines(all).OrderByDescending(LineCentreY).ToList(); // top→bottom (PDF Y-up)
        var spanning = FindSpanningLineGlyphs(lines, minX, maxX, gutterThreshold);

        var result = new List<Letter>(all.Count);
        var band = new List<Letter>();

        void FlushBand()
        {
            if (band.Count == 0) return;
            var buckets = new List<Letter>[boundaries.Count + 1];
            for (int i = 0; i < buckets.Length; i++) buckets[i] = new List<Letter>();
            foreach (var l in band)
            {
                var cx = (l.GlyphRectangle.Left + l.GlyphRectangle.Right) * 0.5;
                int col = 0;
                while (col < boundaries.Count && cx > boundaries[col]) col++;
                buckets[col].Add(l);
            }
            foreach (var bucket in buckets)
                result.AddRange(SortSimple(bucket));
            band.Clear();
        }

        foreach (var line in lines)
        {
            if (line.Count > 0 && spanning.Contains(line[0]))
            {
                FlushBand();
                result.AddRange(SortSimple(line));
            }
            else
            {
                band.AddRange(line);
            }
        }
        FlushBand();
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
        => DetectColumnBoundaries(letters, null);

    /// <summary>
    /// <paramref name="knownLines"/> lets a caller that has ALREADY grouped
    /// these same letters into lines hand that work over instead of paying for
    /// it twice (#966). GroupIntoLines sorts every letter and builds a list per
    /// line; on a 126-page instruction booklet doing it twice per page was
    /// pure duplicate cost, invisible because both calls agree.
    /// </summary>
    internal static List<double> DetectColumnBoundaries(
        IReadOnlyList<Letter> letters, List<List<Letter>>? knownLines)
    {
        var empty = new List<double>();
        if (letters.Count < 8) return empty;

        var lines = knownLines ?? GroupIntoLines(letters);
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

        // Exclude CONTINUOUS full-width lines (running headers/footers/titles/
        // page numbers) from the sweep (#774/#824). Their glyphs cover the gutter
        // X-range, so the cumulative sweep never sees a gap and the whole page
        // falls back to woven row-major order. A two-column BODY line also spans
        // the width but has an internal gap ≥ the gutter threshold, so it is kept
        // — that gap is exactly the gutter we want to find. (On a genuine single-
        // column page every line is continuous-full-width and gets excluded, so
        // no gutter is found and the caller stays on SortSimple — unchanged.)
        var spanning = FindSpanningLineGlyphs(lines, minX: letters.Min(l => l.GlyphRectangle.Left),
            maxX: letters.Max(l => l.GlyphRectangle.Right), gutterThreshold);

        // Candidate interior gaps: sweep glyphs by X and record maximal
        // uncovered X-intervals wider than the gutter threshold. Columns are
        // horizontally disjoint, so their separating gutter shows up as a gap
        // where the running right edge never reaches the next glyph's left.
        var sorted = letters.Where(l => !spanning.Contains(l))
            .OrderBy(l => l.GlyphRectangle.Left).ToList();
        if (sorted.Count == 0) return empty;
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

    /// <summary>
    /// Glyphs of CONTINUOUS full-width lines — a line spanning most of the page
    /// width with NO internal horizontal gap ≥ <paramref name="gutterThreshold"/>
    /// (a running header/footer/title/page number). A two-column BODY line also
    /// spans the width but has the gutter gap, so it is NOT flagged. #774/#824.
    /// </summary>
    private static HashSet<Letter> FindSpanningLineGlyphs(
        List<List<Letter>> lines, double minX, double maxX, double gutterThreshold)
    {
        var result = new HashSet<Letter>();
        double pageW = maxX - minX;
        if (pageW <= 0 || double.IsInfinity(gutterThreshold)) return result;

        foreach (var line in lines)
        {
            double lMin = double.PositiveInfinity, lMax = double.NegativeInfinity;
            foreach (var l in line)
            {
                lMin = Math.Min(lMin, l.GlyphRectangle.Left);
                lMax = Math.Max(lMax, l.GlyphRectangle.Right);
            }
            if (lMax - lMin < 0.6 * pageW) continue; // not full-width

            double maxGap = 0, rr = double.NegativeInfinity;
            foreach (var l in line.OrderBy(l => l.GlyphRectangle.Left))
            {
                if (!double.IsNegativeInfinity(rr))
                    maxGap = Math.Max(maxGap, l.GlyphRectangle.Left - rr);
                rr = Math.Max(rr, l.GlyphRectangle.Right);
            }
            if (maxGap < gutterThreshold) // continuous → header/footer, not columns
                foreach (var l in line) result.Add(l);
        }
        return result;
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
    internal static List<List<Letter>> GroupIntoLines(IEnumerable<Letter> letters)
    {
        var ordered = letters
            .OrderByDescending(l => l.GlyphRectangle.Top)  // PDF Y-up: higher Top = earlier
            .ToList();

        var lines = new List<List<Letter>>();

        // Bucket line indices by quantised centre-Y so a letter examines only
        // the lines that could possibly be within tolerance, instead of every
        // line found so far (#966).
        //
        // THIS PRESERVES THE ORIGINAL SEMANTICS EXACTLY, which matters because
        // this grouping feeds reading order and therefore redaction. The rule
        // was "first line in insertion order whose centre-Y is within
        // 0.5 * min(fontSize) of this letter". Tolerance can never exceed
        // maxTol = 0.5 * (largest font size on the page), and the 4.0 fallback
        // for a zero font size, so a bucket of that width plus its two
        // neighbours is guaranteed to contain every line the linear scan could
        // have matched. Candidates are then examined in ASCENDING LINE INDEX,
        // which is insertion order — so "first match wins" picks the same line
        // the scan did. Anything outside those buckets was unreachable, not
        // merely unlikely.
        double maxTol = 4.0;
        foreach (var l in ordered)
            if (l.FontSize > 0) maxTol = Math.Max(maxTol, 0.5 * l.FontSize);
        var bucketOf = new Dictionary<long, List<int>>();
        var candidates = new List<int>();

        foreach (var l in ordered)
        {
            var cy = (l.GlyphRectangle.Bottom + l.GlyphRectangle.Top) * 0.5;
            var key = (long)Math.Floor(cy / maxTol);

            candidates.Clear();
            for (var k = key - 1; k <= key + 1; k++)
                if (bucketOf.TryGetValue(k, out var bucket))
                    candidates.AddRange(bucket);
            candidates.Sort();

            int hostIndex = -1;
            foreach (var idx in candidates)
            {
                var sample = lines[idx][0];
                var sampleCy = (sample.GlyphRectangle.Bottom + sample.GlyphRectangle.Top) * 0.5;
                var tol = 0.5 * Math.Min(l.FontSize, sample.FontSize);
                if (tol <= 0) tol = 4.0;
                if (Math.Abs(sampleCy - cy) <= tol) { hostIndex = idx; break; }
            }

            if (hostIndex >= 0)
            {
                lines[hostIndex].Add(l);
            }
            else
            {
                lines.Add(new List<Letter> { l });
                var newIndex = lines.Count - 1;
                if (!bucketOf.TryGetValue(key, out var bucket))
                    bucketOf[key] = bucket = new List<int>();
                bucket.Add(newIndex);
            }
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
            while (end < letters.Count && SameLine(letters[end - 1], letters[end]))
            {
                advances.Add(Advance(letters[end - 1], letters[end]));
                widths.Add(GlyphWidth(letters[end]));
                end++;
            }

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
                // A pair where either glyph is already whitespace is skipped — the
                // real space glyph, appended verbatim, does the separating (#833).
                if (IsSpaceGlyph(letters[k - 1].Value) || IsSpaceGlyph(letters[k].Value))
                    continue;

                var pr = letters[k - 1].GlyphRectangle;
                var cr = letters[k].GlyphRectangle;
                if (degenerateWidths && medianAdvance > 0)
                {
                    result[k] = Advance(letters[k - 1], letters[k]) > medianAdvance * WordGapAdvanceFactor;
                }
                else
                {
                    // #835: a word space is a HORIZONTAL quantity, so judge the
                    // gap against a fraction of the font size (~0.25em, poppler's
                    // ~0.1–0.3em band), not against the glyph HEIGHT. The old
                    // `0.5·lineHeight` bar (≈0.5em) sat above a normal word space,
                    // so tight-tracking lines with no real space glyph fused
                    // ("ForewordItisagreat"). Wide letters keep gap≈0 (their real
                    // rect abuts the next glyph), so this does not over-space.
                    var gap = Math.Max(cr.Left - pr.Right, pr.Left - cr.Right);
                    result[k] = fontSize > 0 && gap > 0.25 * fontSize;
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
        return Dehyphenate(sb.ToString());
    }

    /// <summary>
    /// Join words broken across a line by a soft hyphen (#836): a hyphen at the
    /// end of a line immediately followed by a newline and a LOWERCASE-letter
    /// continuation is rejoined (<c>unfamil-\niar</c> → <c>unfamiliar</c>), the
    /// behaviour poppler <c>pdftotext</c> and most readers exhibit. Guarded so
    /// it never eats a real hyphen: the character before the hyphen must be a
    /// letter, the continuation must start LOWERCASE (so ranges, a capitalised
    /// next word, or a hyphen before a paragraph break <c>-\n\n</c> are left
    /// intact). Smart mode only — LineFaithful stays verbatim.
    /// </summary>
    private static string Dehyphenate(string text)
    {
        if (text.IndexOf('-') < 0) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '-'
                && i > 0 && char.IsLetter(text[i - 1])
                && i + 2 < text.Length && text[i + 1] == '\n' && char.IsLower(text[i + 2]))
            {
                // Drop the hyphen AND the newline; resume at the continuation.
                i++; // skip the '\n' (the for-loop i++ then lands on the letter)
                continue;
            }
            sb.Append(text[i]);
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
