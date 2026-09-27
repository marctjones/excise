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
/// <para>#1752: closing the gap moves the text after it, not where the line is
/// anchored, so a line whose EDGES encode its length still states the removed
/// width. A line is classified against the lines above and below it:
/// centred → the whole line moves right by half the closed width; right-aligned
/// → by all of it; justified → the closed width is spread over its word spaces
/// by rewriting the line's own <c>Tw</c> operator (a Tw bump restored after the
/// line would state the difference). Left-aligned and unknown lines keep their
/// start. A justified line whose word spacing is not set by an operator of its
/// own is reported, not guessed at.</para>
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

    /// <summary>Edges within this many ems of each other are aligned.</summary>
    private const double AlignEms = 0.15;

    /// <summary>Lines within this many ems above or below are the references alignment is read from.</summary>
    private const double ReferenceEms = 2.6;

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

    private enum Alignment { Left, Centred, Right, Justified }

    /// <summary>One line: the glyphs on a baseline between two column-sized gaps.</summary>
    private sealed class Line
    {
        public required double Y { get; init; }
        public required double Em { get; init; }
        public required double Lo { get; init; }
        public required double Hi { get; init; }
        /// <summary>How far the line may grow: a space short of the nearest
        /// glyph on its baseline either side, or the page's edge (#1754).</summary>
        public required double LeftLimit { get; init; }
        public required double Limit { get; init; }
        public required List<Letter> Letters { get; init; }
        public List<Run> Runs { get; } = new();

        /// <summary>Page-space shift of the whole line (#1752: centred, right-aligned).</summary>
        public double Base;
        /// <summary>Page-space advance added to each word space in <see cref="Spaces"/> (#1752: justified).</summary>
        public double Stretch;
        public HashSet<Letter> Spaces { get; } = new(ReferenceEqualityComparer.Instance);
        /// <summary>Operator index → how many of <see cref="Spaces"/> it shows.</summary>
        public Dictionary<int, int> SpacesByOp { get; } = new();
        /// <summary>Tw operator index → its rewritten operand; show operator index → the Tw now in effect.</summary>
        public Dictionary<int, double> TwRewrites { get; } = new();
        public Dictionary<int, double> WordSpacing { get; } = new();

        public double Closed => Runs.Sum(r => r.Closure);

        /// <summary>Ink extents: the first and last non-space glyph.</summary>
        public (double Left, double Right) Ink =>
            Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)) is var ink && ink.Any()
                ? (ink.Min(l => l.StartX), ink.Max(RightOf))
                : (Lo, Hi);

        public double ShiftAt(double x) =>
            Base - Runs.Where(r => r.StartX < x - Eps).Sum(r => r.Closure)
                 + (Stretch == 0 ? 0 : Stretch * Spaces.Count(sp => sp.StartX < x - Eps));
    }

    private readonly Dictionary<int, List<Run>> _runsByOp = new();
    private IReadOnlyList<ContentOperator> _operations = Array.Empty<ContentOperator>();
    private IReadOnlyList<Letter> _letters = Array.Empty<Letter>();
    private Func<ContentOperator, IEnumerable<Letter>> _lettersOf = _ => Array.Empty<Letter>();
    private PdfRectangle? _page;
    private WidthPolicy _policy;
    private readonly List<Line> _lines = new();
    private readonly HashSet<string> _notes = new(StringComparer.Ordinal);

    /// <summary>What the policy could not do, one row per line (#1751): reported, never skipped.</summary>
    internal IReadOnlyCollection<string> Notes => _notes;

    private WidthClosureLedger() { }

    /// <param name="keep">The gap a policy keeps for one redaction area, given
    /// the area, the glyphs removed in it, where they start and their total
    /// advance (page space); null closes every gap. Kept once per area.</param>
    /// <param name="policy">The width policy, for the rows it reports and for
    /// FixedMarker, whose lines are not re-anchored (see <see cref="Align"/>).</param>
    /// <param name="page">The page box, the farthest a growing line may reach.</param>
    /// <param name="lettersOf">The page glyphs a text-showing operator draws.</param>
    internal static WidthClosureLedger Build(
        IReadOnlyList<ContentOperator> operations,
        IReadOnlyList<Letter> letters,
        IReadOnlyList<Removal> removals,
        IReadOnlyList<PdfRectangle> areas,
        GlyphRemovalStrategy strategy,
        Func<PdfRectangle, IReadOnlyList<Letter>, double, double, double>? keep,
        WidthPolicy policy,
        PdfRectangle? page,
        Func<ContentOperator, IEnumerable<Letter>> lettersOf)
    {
        var ledger = new WidthClosureLedger
        {
            _policy = policy,
            _operations = operations,
            _letters = letters,
            _lettersOf = lettersOf,
            _page = page?.Normalize(),
        };
        foreach (var removal in removals)
            ledger.AddRuns(removal, letters, areas, strategy, page);
        if (keep != null)
            ledger.Keep(areas, keep);
        foreach (var line in ledger._lines)
            ledger.Align(line);
        ledger.FitKeptGaps();
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
    /// #1754: a kept gap wider than what it replaced grows the line. Where the
    /// line has no room for that before the nearest glyph on its baseline or
    /// the page's edge — or, justified, its word spaces would shrink to nothing
    /// — its gaps close fully instead (the secure fallback: a closed gap states
    /// nothing) and the line is reported.
    /// </summary>
    private void FitKeptGaps()
    {
        foreach (var line in _lines)
        {
            if (line.Runs.Count == 0 || line.Closed >= 0) continue;
            var fits = line.Lo + line.ShiftAt(line.Lo) >= line.LeftLimit - Eps &&
                       line.Hi + line.ShiftAt(line.Hi + 1) <= line.Limit + Eps &&
                       line.Spaces.All(sp => sp.Width + line.Stretch >= 0.1 * line.Em);
            if (fits) continue;
            var kept = line.Runs.Sum(r => r.Reserve);
            foreach (var run in line.Runs) run.Reserve = 0;
            Align(line);
            _notes.Add($"line at y={line.Y:F1}: no room on the line for the {kept:F1} pt gap {_policy} keeps; " +
                       "the gap was closed fully instead" +
                       (_policy == WidthPolicy.FixedMarker ? ", so the marker covers the text that follows it" : ""));
        }
    }

    /// <summary>
    /// #1752: re-anchor the line the way it was aligned, so its edges do not
    /// state the closed width.
    /// </summary>
    private void Align(Line line)
    {
        line.Base = 0;
        line.Stretch = 0;
        line.Spaces.Clear();
        line.SpacesByOp.Clear();
        line.TwRewrites.Clear();
        line.WordSpacing.Clear();
        var closed = line.Closed;
        if (line.Runs.Count == 0 || Math.Abs(closed) < Eps) return;

        var alignment = Classify(line);
        if (_policy == WidthPolicy.FixedMarker)
        {
            // The marker is drawn where the removed text began, so moving the
            // text before it would move the text out from beside the marker;
            // the marker is what shows the redaction. Its line keeps its start,
            // and an aligned line's far edge still moves by the difference.
            if (alignment != Alignment.Left)
                _notes.Add($"line at y={line.Y:F1}: {alignment.ToString().ToLowerInvariant()} line kept in " +
                           $"place beside its marker; its edge moved by {closed:F1} pt, the removed width " +
                           "less the marker's");
            return;
        }

        switch (alignment)
        {
            case Alignment.Centred:
                line.Base = closed / 2;
                break;
            case Alignment.Right:
                line.Base = closed;
                break;
            case Alignment.Justified when !Rejustify(line, closed):
                _notes.Add($"line at y={line.Y:F1}: justified, but its word spacing is not set by an " +
                           "operator of its own, so it was not re-justified and ends " +
                           $"{closed:F1} pt short of its right margin");
                break;
        }
    }

    /// <summary>
    /// Read a line's alignment from the lines around it. Only edges the
    /// references agree on count; a lone line is centred only when it is short
    /// and centred on the page.
    /// </summary>
    private Alignment Classify(Line line)
    {
        var (left, right) = line.Ink;
        var tol = AlignEms * line.Em;
        bool Near(double a, double b) => Math.Abs(a - b) <= tol;
        var refs = References(line);
        if (refs.Count == 0)
            return _page is { } p && right - left < 0.6 * p.Width && Near((left + right) / 2, (p.Left + p.Right) / 2)
                ? Alignment.Centred
                : Alignment.Left;

        bool Most(Func<(double L, double R), bool> agree)
        {
            var n = refs.Count(agree);
            return n > 0 && 2 * n >= refs.Count;
        }
        var leftAligned = refs.Any(r => Near(r.L, left));
        var rightAligned = Most(r => Near(r.R, right));
        var centred = Most(r => Near((r.L + r.R) / 2, (left + right) / 2));
        var refsShareLeft = refs.Count >= 2 && refs.All(r => Near(r.L, refs[0].L));

        if (rightAligned && (leftAligned || refsShareLeft)) return Alignment.Justified;
        if (rightAligned) return Alignment.Right;
        if (centred && !leftAligned) return Alignment.Centred;
        return Alignment.Left;
    }

    /// <summary>
    /// Ink extents of the lines of the same size just above and below
    /// <paramref name="line"/> that overlap it by at least half the shorter.
    /// </summary>
    private List<(double L, double R)> References(Line line)
    {
        var (left, right) = line.Ink;
        var result = new List<(double L, double R)>();
        var near = _letters
            .Where(l => Math.Abs(l.StartY - line.Y) > SameLineEms * line.Em &&
                        Math.Abs(l.StartY - line.Y) <= ReferenceEms * line.Em &&
                        !string.IsNullOrWhiteSpace(l.Value))
            .OrderBy(l => l.StartY)
            .ToList();
        var i = 0;
        while (i < near.Count)
        {
            var y = near[i].StartY;
            var row = new List<Letter>();
            while (i < near.Count && near[i].StartY - y <= 0.25 * line.Em) row.Add(near[i++]);
            if (Math.Abs(Median(row.Select(l => l.GlyphRectangle.Normalize().Height)) - line.Em) > 0.2 * line.Em)
                continue;
            row.Sort((a, b) => a.StartX.CompareTo(b.StartX));
            foreach (var (lo, hi) in Spans(row, line.Em, OperatorOf(y, line.Em)))
            {
                var l = row[lo].StartX;
                var r = row.GetRange(lo, hi - lo + 1).Max(RightOf);
                var overlap = Math.Min(r, right) - Math.Max(l, left);
                if (overlap >= 0.5 * Math.Min(r - l, right - left)) result.Add((l, r));
            }
        }
        return result;
    }

    /// <summary>
    /// #1752: spread the closed width over the line's word spaces by rewriting
    /// the Tw operator that sets their spacing — only when every such operator
    /// governs this line alone, so no other text moves and no operand restates
    /// the old spacing. False when the line cannot be re-justified that way.
    /// </summary>
    private bool Rejustify(Line line, double closed)
    {
        var removed = new HashSet<Letter>(line.Runs.SelectMany(r => r.Letters), ReferenceEqualityComparer.Instance);
        var (left, right) = line.Ink;
        // Tw widens only the single-byte code 32 (§9.3.3).
        var spaces = line.Letters
            .Where(l => l.Value == " " && l.CharacterCode == 32 && l.CodeByteLength == 1 &&
                        !removed.Contains(l) && l.StartX > left && l.StartX < right)
            .ToHashSet<Letter>(ReferenceEqualityComparer.Instance);
        if (spaces.Count == 0) return false;
        var stretch = closed / spaces.Count;

        var counts = new Dictionary<int, int>();
        var rewrites = new Dictionary<int, double>();
        var wordSpacing = new Dictionary<int, double>();
        for (var i = 0; i < _operations.Count; i++)
        {
            var op = _operations[i];
            if (op.Category != OperatorCategory.TextShowing || !OnLine(op, line)) continue;
            var shown = _lettersOf(op).Distinct<Letter>(ReferenceEqualityComparer.Instance).Count(spaces.Contains);
            if (shown == 0) continue;
            if (GoverningWordSpacing(i) is not int tw || !GovernsOnly(tw, line)) return false;
            var m = op.TextTransform!.Value.Multiply(op.GraphicsTransform!.Value);
            var value = op.TextState!.WordSpacing + stretch / (op.TextState.HorizontalScaling / 100.0 * m.A);
            if (rewrites.TryGetValue(tw, out var other) && Math.Abs(other - value) > 1e-6) return false;
            rewrites[tw] = value;
            wordSpacing[i] = value;
            counts[i] = shown;
        }
        if (counts.Values.Sum() != spaces.Count) return false;

        line.Stretch = stretch;
        line.Spaces.UnionWith(spaces);
        foreach (var (k, v) in counts) line.SpacesByOp[k] = v;
        foreach (var (k, v) in rewrites) line.TwRewrites[k] = v;
        foreach (var (k, v) in wordSpacing) line.WordSpacing[k] = v;
        return true;
    }

    /// <summary>The Tw operator whose value is in effect at operator <paramref name="index"/>, if one is.</summary>
    private int? GoverningWordSpacing(int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var name = _operations[i].Name;
            if (name == "Tw") return i;
            // " sets its own; Q may restore one set before a q.
            if (name is "\"" or "Q") return null;
        }
        return null;
    }

    /// <summary>True when every glyph-drawing show the Tw at <paramref name="tw"/> governs is on <paramref name="line"/>.</summary>
    private bool GovernsOnly(int tw, Line line)
    {
        for (var i = tw + 1; i < _operations.Count; i++)
        {
            var op = _operations[i];
            if (op.Name is "Tw" or "\"" or "q" or "Q") return true;
            if (op.Category == OperatorCategory.TextShowing && op.BoundingBox is not null && !OnLine(op, line))
                return false;
        }
        return true;
    }

    private static bool OnLine(ContentOperator op, Line line)
    {
        if (op.BoundingBox is not { } box || UnitOf(op) is null) return false;
        var m = op.TextTransform!.Value.Multiply(op.GraphicsTransform!.Value);
        var x = box.Normalize().Left;
        return Math.Abs(m.F - line.Y) <= SameLineEms * line.Em && x >= line.Lo - Eps && x <= line.Hi + Eps;
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

        var at = row.FindIndex(l => ReferenceEquals(l, first));
        if (at < 0) { row.Add(first); row.Sort((a, b) => a.StartX.CompareTo(b.StartX)); at = row.IndexOf(first); }
        var (lo, hi) = Spans(row, em, OperatorOf(y, em)).First(span => span.Lo <= at && at <= span.Hi);

        var result = new Line
        {
            Y = y,
            Em = em,
            Lo = row[lo].StartX,
            Hi = row.GetRange(lo, hi - lo + 1).Max(RightOf),
            LeftLimit = lo > 0
                ? RightOf(row[lo - 1]) + 0.25 * em
                : page?.Normalize().Left ?? double.NegativeInfinity,
            Limit = hi + 1 < row.Count
                ? row[hi + 1].StartX - 0.25 * em
                : page?.Normalize().Right ?? double.PositiveInfinity,
            Letters = row.GetRange(lo, hi - lo + 1),
        };
        _lines.Add(result);
        return result;
    }

    /// <summary>
    /// A row of glyphs (sorted by x) cut wherever the gap between them is wider
    /// than a column gutter — except between glyphs one operator draws in
    /// turn: word spacing (Tw) or character spacing (Tc) widens those, and a
    /// justified line is still one line. Each span as an inclusive index range.
    /// </summary>
    private static IEnumerable<(int Lo, int Hi)> Spans(List<Letter> row, double em, Dictionary<Letter, int> operatorOf)
    {
        var lo = 0;
        var reach = double.NegativeInfinity;
        for (var i = 0; i < row.Count; i++)
        {
            var sameOperator = i > 0 &&
                               operatorOf.TryGetValue(row[i - 1], out var a) &&
                               operatorOf.TryGetValue(row[i], out var b) && a == b;
            if (i > lo && !sameOperator && row[i].StartX - reach > ColumnGapEms * em)
            {
                yield return (lo, i - 1);
                lo = i;
                reach = double.NegativeInfinity;
            }
            reach = Math.Max(reach, RightOf(row[i]));
        }
        if (row.Count > 0) yield return (lo, row.Count - 1);
    }

    /// <summary>Which text-showing operator (by index) draws each glyph of the row at <paramref name="y"/>.</summary>
    private Dictionary<Letter, int> OperatorOf(double y, double em)
    {
        var result = new Dictionary<Letter, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < _operations.Count; i++)
        {
            var op = _operations[i];
            if (op.Category != OperatorCategory.TextShowing || op.BoundingBox is null || UnitOf(op) is null) continue;
            var m = op.TextTransform!.Value.Multiply(op.GraphicsTransform!.Value);
            if (Math.Abs(m.F - y) > SameLineEms * em) continue;
            foreach (var letter in _lettersOf(op)) result.TryAdd(letter, i);
        }
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

    /// <summary>
    /// Page-space shift the pen chain of operator <paramref name="index"/>
    /// carries past its end: its closed runs, and its widened word spaces.
    /// </summary>
    internal double InternalShift(int index) =>
        (_runsByOp.TryGetValue(index, out var runs) ? -runs.Sum(r => r.Closure) : 0)
        + _lines.Sum(line => line.SpacesByOp.TryGetValue(index, out var n) ? n * line.Stretch : 0);

    /// <summary>#1752: the rewritten operand of the Tw operator at <paramref name="index"/>, if it re-justifies a line.</summary>
    internal double? RewrittenWordSpacing(int index)
    {
        foreach (var line in _lines)
            if (line.TwRewrites.TryGetValue(index, out var value)) return value;
        return null;
    }

    /// <summary>#1752: the word spacing now in effect for the show at <paramref name="index"/>, if its line was re-justified.</summary>
    internal double? WordSpacingFor(int index)
    {
        foreach (var line in _lines)
            if (line.WordSpacing.TryGetValue(index, out var value)) return value;
        return null;
    }

    /// <summary>
    /// Target page-space shift of the text at <paramref name="x"/> on the line
    /// through <paramref name="y"/>: the line's own shift, minus every closure
    /// to its left, plus every widened word space to its left. Zero off every
    /// affected line.
    /// </summary>
    internal double ShiftAt(double x, double y)
    {
        foreach (var line in _lines)
        {
            if (line.Runs.Count == 0) continue;
            if (Math.Abs(y - line.Y) > SameLineEms * line.Em) continue;
            if (x < line.Lo - Eps || x > line.Hi + Eps) continue;
            return line.ShiftAt(x);
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
