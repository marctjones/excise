using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// #1751 — the PAGE-level plan for a width-closing policy: how far every point
/// of every line that lost a run has to move once the removed advance is taken
/// out, so the gap closes on the whole line and not only inside the operator
/// that held the removed glyphs.
/// </summary>
/// <remarks>
/// <para>Real producers split a line into many positioned runs. Omitting the
/// removed advance moves only what follows it in the same pen chain; a run
/// placed by its own <c>Td</c>/<c>Tm</c>, or in a later <c>BT</c>, stayed put
/// and the gap reappeared at that boundary still stating the removed width
/// (#1751 measured it on 10 of 84 real documents). The ledger states the target
/// shift of any point on a line as one function of x; <see cref="GlyphRemover"/>
/// realises it with a numeric-only <c>TJ</c> wherever a run's pen was reset.</para>
/// <para>A line is the maximal row of glyphs on one baseline with no gap wider
/// than <see cref="ColumnGapEms"/>: a wider gap is a column gutter or a tab
/// stop, and moving the text beyond it would itself measure the removed width
/// against every other line of that column.</para>
/// <para>A policy that keeps a gap of its own keeps it ONCE per redaction area,
/// at the area's leftmost removed run: <see cref="WidthPolicy.FixedMarker"/>
/// keeps exactly the marker's width (#1725), so the marker covers nothing that
/// follows it and the gap is the same for every removed string. A term split
/// across two runs is still one area and still one marker.</para>
/// <para>What the ledger cannot move it REPORTS (<see cref="Notes"/>), never
/// skips silently: a run of rotated or skewed text, and glyphs on the line that
/// no operator of this content stream draws.</para>
/// </remarks>
internal sealed class WidthClosureLedger
{
    /// <summary>A gap between glyphs on one baseline wider than this many ems ends the line.</summary>
    private const double ColumnGapEms = 1.2;

    /// <summary>Glyphs within this many ems of a baseline are on that line (a superscript still is).</summary>
    private const double SameLineEms = 0.5;

    private const double Eps = 0.01;

    /// <summary>A text-showing operator with glyphs to remove, in stream order.</summary>
    internal sealed record Removal(int Index, ContentOperator Op, string Text, List<LetterMatch> Matches, List<LetterMatch> ToRemove);

    /// <summary>One contiguous removed run inside one operator.</summary>
    private sealed class Run
    {
        public required Letter First { get; init; }
        public required List<Letter> Letters { get; init; }
        public required double StartX { get; init; }
        /// <summary>Page-space pen advance of the removed glyphs.</summary>
        public required double Width { get; init; }
        /// <summary>Page-space length of one TJ unit in the run's operator.</summary>
        public required double Unit { get; init; }
        /// <summary>Index of the redaction area that removed the run's first glyph.</summary>
        public required int Area { get; init; }
        /// <summary>Page-space gap the policy keeps in place of the run.</summary>
        public double Reserve { get; set; }
        public double Closure => Width - Reserve;
    }

    /// <summary>One line: the glyphs on a baseline between two column-sized gaps.</summary>
    private sealed class Line
    {
        public required double Y { get; init; }
        public required double Em { get; init; }
        public required double Lo { get; init; }
        public required double Hi { get; init; }
        /// <summary>How far right the line may grow: a space short of the next
        /// glyph on its baseline, or the page's edge (#1754).</summary>
        public required double Limit { get; init; }
        public required List<Letter> Letters { get; init; }
        public List<Run> Runs { get; } = new();
    }

    private readonly Dictionary<int, List<Run>> _runsByOp = new();
    private readonly List<Line> _lines = new();
    private readonly HashSet<string> _notes = new(StringComparer.Ordinal);

    /// <summary>What the policy could not do, one row per line (#1751): reported, never skipped.</summary>
    internal IReadOnlyCollection<string> Notes => _notes;

    private WidthClosureLedger() { }

    /// <param name="keep">The gap a policy keeps for one redaction area, given
    /// the area, the glyphs removed in it, where they start and their total
    /// advance (page space); null closes every gap. Kept once per area.</param>
    /// <param name="policy">The policy's name, for the rows it reports.</param>
    /// <param name="page">The page box, the farthest a growing line may reach.</param>
    internal static WidthClosureLedger Build(
        IReadOnlyList<ContentOperator> operations,
        IReadOnlyList<Letter> letters,
        IReadOnlyList<Removal> removals,
        IReadOnlyList<PdfRectangle> areas,
        GlyphRemovalStrategy strategy,
        Func<PdfRectangle, IReadOnlyList<Letter>, double, double, double>? keep,
        string policy,
        PdfRectangle? page)
    {
        var ledger = new WidthClosureLedger();
        foreach (var removal in removals)
            ledger.AddRuns(removal, letters, areas, strategy, page);
        if (keep != null)
        {
            ledger.Keep(areas, keep);
            ledger.FitKeptGaps(policy);
        }
        ledger.NoteForeignGlyphs(operations);
        return ledger;
    }

    /// <summary>
    /// Keep each area's gap at its leftmost removed run, so the text that
    /// follows starts where the kept gap ends: a FixedMarker's width (#1725),
    /// or a QuantizeGap bucket (#1754).
    /// </summary>
    private void Keep(
        IReadOnlyList<PdfRectangle> areas, Func<PdfRectangle, IReadOnlyList<Letter>, double, double, double> keep)
    {
        foreach (var group in _runsByOp.Values.SelectMany(r => r).Where(r => r.Area >= 0).GroupBy(r => r.Area))
        {
            var runs = group.OrderBy(r => r.StartX).ToList();
            var removed = runs.SelectMany(r => r.Letters).ToList();
            runs[0].Reserve = Math.Max(0, keep(areas[group.Key], removed, runs[0].StartX, runs.Sum(r => r.Width)));
        }
    }

    /// <summary>
    /// #1754: a kept gap wider than what it replaced pushes the rest of the
    /// line right. Where the line has no room for that before the next glyph on
    /// its baseline or the page's edge, its gaps close fully instead — the
    /// secure fallback, since a closed gap states nothing — and the line is
    /// reported.
    /// </summary>
    private void FitKeptGaps(string policy)
    {
        foreach (var line in _lines)
        {
            if (line.Runs.Count == 0) continue;
            var closed = line.Runs.Sum(r => r.Closure);
            if (closed >= 0 || line.Hi - closed <= line.Limit + Eps) continue;
            var kept = line.Runs.Sum(r => r.Reserve);
            foreach (var run in line.Runs) run.Reserve = 0;
            _notes.Add($"line at y={line.Y:F1}: no room on the line for the {kept:F1} pt gap {policy} keeps; " +
                       "the gap was closed fully instead" +
                       (policy == nameof(WidthPolicy.FixedMarker) ? ", so the marker covers the text that follows it" : ""));
        }
    }

    private void AddRuns(
        Removal removal, IReadOnlyList<Letter> letters,
        IReadOnlyList<PdfRectangle> areas, GlyphRemovalStrategy strategy, PdfRectangle? page)
    {
        if (UnitOf(removal.Op) is not double unit)
        {
            _notes.Add($"line at y={Baseline(removal.ToRemove):F1}: rotated, skewed, vertical or zero-size " +
                       "text; its gap was closed inside its own text run only, so text placed separately " +
                       "after it on the line did not move");
            return;
        }

        var removed = new HashSet<LetterMatch>(removal.ToRemove, ReferenceEqualityComparer.Instance);
        var runs = new List<Run>();
        _runsByOp[removal.Index] = runs;

        // Contiguous = adjacent in the operator's own glyph order. A ligature
        // is several matches of ONE letter; its advance is counted once.
        var i = 0;
        while (i < removal.Matches.Count)
        {
            if (!removed.Contains(removal.Matches[i])) { i++; continue; }
            var first = removal.Matches[i].Letter;
            double thousandths = 0;
            Letter? previous = null;
            var runLetters = new List<Letter>();
            while (i < removal.Matches.Count && removed.Contains(removal.Matches[i]))
            {
                var letter = removal.Matches[i].Letter;
                if (!ReferenceEquals(letter, previous))
                {
                    thousandths += letter.DisplacementThousandths;
                    runLetters.Add(letter);
                }
                previous = letter;
                i++;
            }

            var run = new Run
            {
                First = first,
                Letters = runLetters,
                StartX = first.StartX,
                Width = thousandths * unit,
                Unit = unit,
                Area = IndexOf(areas, a => strategy.Selects(first.GlyphRectangle, a)),
            };
            runs.Add(run);
            LineAt(first, runLetters, letters, page).Runs.Add(run);
        }
    }

    /// <summary>The line holding <paramref name="first"/>, built from the page's letters once.</summary>
    private Line LineAt(Letter first, List<Letter> runLetters, IReadOnlyList<Letter> letters, PdfRectangle? page)
    {
        foreach (var line in _lines)
            if (Math.Abs(first.StartY - line.Y) <= SameLineEms * line.Em &&
                first.StartX >= line.Lo - Eps && first.StartX <= line.Hi + Eps)
                return line;

        var em = Median(runLetters.Select(l => l.GlyphRectangle.Normalize().Height));
        if (!(em > 0)) em = first.FontSize > 0 ? first.FontSize : 12;
        var y = first.StartY;
        var row = letters.Where(l => Math.Abs(l.StartY - y) <= SameLineEms * em)
                         .OrderBy(l => l.StartX)
                         .ToList();

        // Walk out from the removed glyph in both directions until a gap wider
        // than a column gutter.
        var at = row.FindIndex(l => ReferenceEquals(l, first));
        if (at < 0) { row.Add(first); row.Sort((a, b) => a.StartX.CompareTo(b.StartX)); at = row.IndexOf(first); }
        var lo = at;
        while (lo > 0 && row[lo].StartX - RightOf(row[lo - 1]) <= ColumnGapEms * em) lo--;
        var hi = at;
        var reach = RightOf(row[hi]);
        while (hi + 1 < row.Count && row[hi + 1].StartX - reach <= ColumnGapEms * em)
        {
            hi++;
            reach = Math.Max(reach, RightOf(row[hi]));
        }

        var result = new Line
        {
            Y = y,
            Em = em,
            Lo = row[lo].StartX,
            Hi = reach,
            Limit = hi + 1 < row.Count
                ? row[hi + 1].StartX - 0.25 * em
                : page?.Normalize().Right ?? double.PositiveInfinity,
            Letters = row.GetRange(lo, hi - lo + 1),
        };
        _lines.Add(result);
        return result;
    }

    /// <summary>
    /// Glyphs on an affected line, after a removed run, that no text operator of
    /// this content stream draws — a form XObject's or an annotation's. Nothing
    /// here can move them, so the gap would reopen in front of them.
    /// </summary>
    private void NoteForeignGlyphs(IReadOnlyList<ContentOperator> operations)
    {
        var boxes = operations
            .Where(op => op.Category == OperatorCategory.TextShowing && op.BoundingBox is not null)
            .Select(op => op.BoundingBox!.Value.Normalize())
            .ToList();
        foreach (var line in _lines)
        {
            if (line.Runs.Count == 0) continue;
            var from = line.Runs.Min(r => r.StartX);
            var foreign = line.Letters.Any(l =>
                l.StartX > from + Eps && !string.IsNullOrWhiteSpace(l.Value) &&
                !boxes.Any(b => b.Contains((l.GlyphRectangle.Left + l.GlyphRectangle.Right) / 2,
                                           (l.GlyphRectangle.Bottom + l.GlyphRectangle.Top) / 2)));
            if (foreign)
                _notes.Add($"line at y={line.Y:F1}: text after the removed run is drawn outside this " +
                           "content stream (a form XObject or an annotation) and did not move");
        }
    }

    /// <summary>
    /// Page-space pen advance the removed runs of operator <paramref name="index"/>
    /// no longer contribute, in that operator's TJ units — what its
    /// compensation must NOT replay. Zero when it removed nothing.
    /// </summary>
    internal double ClosedThousandths(int index, ContentOperator op) =>
        _runsByOp.TryGetValue(index, out var runs) && UnitOf(op) is double unit
            ? runs.Sum(r => r.Closure) / unit
            : 0;

    /// <summary>
    /// The operand split's adjustment for each removed run of operator
    /// <paramref name="index"/>: the reserve, in that operator's TJ units, at the
    /// run that holds it, and nothing (the gap closes) everywhere else —
    /// including a run whose operator the ledger could not place.
    /// </summary>
    internal Func<Letter, double, double?> RunAdjustment(int index)
    {
        var runs = _runsByOp.TryGetValue(index, out var r) ? r : null;
        return (first, _) =>
            runs?.FirstOrDefault(run => ReferenceEquals(run.First, first)) is { Reserve: > 0 } reserved
                ? -reserved.Reserve / reserved.Unit
                : null;
    }

    /// <summary>Page-space shift the pen chain of operator <paramref name="index"/> carries past its end.</summary>
    internal double InternalShift(int index) =>
        _runsByOp.TryGetValue(index, out var runs) ? -runs.Sum(r => r.Closure) : 0;

    /// <summary>
    /// Target page-space shift of the text at <paramref name="x"/> on the line
    /// through <paramref name="y"/>: minus every closure on that line to its left.
    /// Zero off every affected line.
    /// </summary>
    internal double ShiftAt(double x, double y)
    {
        foreach (var line in _lines)
        {
            if (line.Runs.Count == 0) continue;
            if (Math.Abs(y - line.Y) > SameLineEms * line.Em) continue;
            if (x < line.Lo - Eps || x > line.Hi + Eps) continue;
            return -line.Runs.Where(r => r.StartX < x - Eps).Sum(r => r.Closure);
        }
        return 0;
    }

    /// <summary>
    /// The target shift of a text-showing operator's glyphs, from where its
    /// first glyph sits. Null when it draws nothing; NaN when a shift cannot be
    /// expressed for it (<see cref="UnitOf"/>).
    /// </summary>
    internal double? TargetOf(ContentOperator op)
    {
        if (op.BoundingBox is not { } box) return null;
        if (UnitOf(op) is null) return double.NaN;
        var m = op.TextTransform!.Value.Multiply(op.GraphicsTransform!.Value);
        return ShiftAt(box.Normalize().Left, m.F);
    }

    /// <summary>
    /// The numeric-only <c>TJ</c> that moves <paramref name="op"/>'s pen by
    /// <paramref name="shift"/> page-space units. A TJ number is scaled by Tfs
    /// and Th but NOT by Tc or Tw (§9.4.4), so this is exact under any Tc/Tw.
    /// </summary>
    internal static ContentOperator ShiftOperator(ContentOperator op, double shift) =>
        new("TJ", new PdfObject[]
        {
            new PdfArray(new PdfObject[] { new PdfReal(-shift / UnitOf(op)!.Value) }),
        });

    /// <summary>
    /// Page-space length of one TJ unit (a thousandth of text space) for
    /// <paramref name="op"/>: Tfs × Th × the horizontal scale of Tm × CTM.
    /// Null for text that is not horizontal, left-to-right and axis-aligned in
    /// page space, or has no expressible size — a shift along x is not a shift
    /// along its baseline.
    /// </summary>
    internal static double? UnitOf(ContentOperator op)
    {
        if (op.TextState is not { IsVerticalWriting: false } state) return null;
        if (op.TextTransform is not { } tm || op.GraphicsTransform is not { } ctm) return null;
        var m = tm.Multiply(ctm);
        if (!(m.A > 0) || Math.Abs(m.B) > 1e-6 * m.A || Math.Abs(m.C) > 1e-6 * Math.Abs(m.D)) return null;
        var unit = state.FontSize * (state.HorizontalScaling / 100.0) * m.A / 1000.0;
        return unit > 1e-9 && double.IsFinite(unit) ? unit : null;
    }

    private static int IndexOf(IReadOnlyList<PdfRectangle> areas, Func<PdfRectangle, bool> match)
    {
        for (var i = 0; i < areas.Count; i++)
            if (match(areas[i])) return i;
        return -1;
    }

    private static double RightOf(Letter l) => Math.Max(l.StartX + l.Width, l.GlyphRectangle.Normalize().Right);

    private static double Baseline(List<LetterMatch> matches) =>
        matches.Count == 0 ? 0 : matches[0].Letter.StartY;

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(v => v > 0).OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }
}
