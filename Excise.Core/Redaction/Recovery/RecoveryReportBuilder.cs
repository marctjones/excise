using System;
using System.Collections.Generic;
using System.Linq;
using Excise.Core.Document;

namespace Excise.Core.Redaction.Recovery;

/// <summary>
/// #1587 — assembles one <see cref="RecoveryReport"/> from the marks a document
/// admits to and the findings every channel produced, and answers the question
/// the report exists for: <i>how much of each redaction came back?</i>
///
/// <para><b>Linking is geometric, and deliberately conservative.</b> A finding
/// belongs to the mark that covers most of it, and only when that mark covers a
/// majority of the finding's own box. A finding that matches no mark is NOT
/// dropped — it goes to <see cref="RecoveryReport.Unlinked"/>. Silently binding
/// a leak to the nearest box would misattribute it; silently discarding it
/// would hide it. The commonest real-world leak (a carrier the redactor never
/// scrubbed, on a page where no box was drawn) is exactly the unlinked
/// case.</para>
///
/// <para><b>Marks can be synthesised, by evidence only.</b> A channel that
/// knows which rectangle it was looking under passes it as a hint — the
/// hidden-text channel knows the obstruction that covered the run, the residue
/// channel knows the gap. When a hint matches no detected mark, a mark is
/// created for it, because the alternative is a report whose denominator
/// silently excludes the redactions the geometry filter was too strict to
/// see.</para>
/// </summary>
public sealed class RecoveryReportBuilder
{
    private readonly List<RedactionMark> _marks = new();
    private readonly List<(RecoveredFinding Finding, PdfRectangle? Hint)> _findings = new();
    private readonly List<string> _channelsRun = new();
    private readonly Dictionary<string, RedactionFitAnalyzer.FitReport> _fits =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _channelsSkipped = new(StringComparer.Ordinal);

    /// <summary>
    /// Fraction of a mark's width that certain findings must cover before the
    /// mark counts as fully recovered rather than partially. Not 1.0: a glyph
    /// box is tight to the ink while a redaction box is padded, so an exact
    /// recovery routinely covers ~85-95% of the mark it replaced.
    /// </summary>
    private const double FullRecoveryWidthFraction = 0.8;

    /// <summary>A finding is this mark's when the mark covers this much of it.</summary>
    private const double LinkOverlapFraction = 0.5;

    public RecoveryReportBuilder AddMarks(IEnumerable<RedactionMark> marks)
    {
        ArgumentNullException.ThrowIfNull(marks);
        _marks.AddRange(marks);
        return this;
    }

    /// <summary>
    /// Record a finding. <paramref name="markHint"/> is the rectangle the
    /// channel itself identified as the redaction it was reading under, when it
    /// knows one — an exact answer beats re-deriving the link from geometry.
    /// </summary>
    public RecoveryReportBuilder AddFinding(RecoveredFinding finding, PdfRectangle? markHint = null)
    {
        ArgumentNullException.ThrowIfNull(finding);
        _findings.Add((finding, markHint));
        return this;
    }

    /// <summary>
    /// #1589 — attach a width-budget analysis to a mark. Only meaningful for a
    /// mark nothing recovered; the builder does not enforce that, because
    /// deciding it needs the findings, which the caller supplies afterwards.
    /// </summary>
    public RecoveryReportBuilder AddFit(string markId, RedactionFitAnalyzer.FitReport fit)
    {
        _fits[markId] = fit;
        return this;
    }

    /// <summary>Declare that a channel ran. Order is preserved for the report.</summary>
    public RecoveryReportBuilder ChannelRan(string channel)
    {
        if (!_channelsRun.Contains(channel, StringComparer.Ordinal)) _channelsRun.Add(channel);
        return this;
    }

    /// <summary>
    /// Declare that a channel did NOT run, and why. A report that omits this is
    /// a report over an unknown number of channels, which reads as coverage it
    /// does not have (same rule as the scorecard's Coverage, #1181).
    /// </summary>
    public RecoveryReportBuilder ChannelSkipped(string channel, string reason)
    {
        _channelsSkipped[channel] = reason;
        return this;
    }

    public RecoveryReport Build()
    {
        var marks = new List<RedactionMark>(_marks);
        SynthesiseMarksFromHints(marks);

        var byMark = marks.ToDictionary(m => m.Id, _ => new List<RecoveredFinding>(), StringComparer.Ordinal);
        var unlinked = new List<RecoveredFinding>();
        var documentLevel = new List<RecoveredFinding>();

        foreach (var (finding, hint) in _findings)
        {
            if (finding.Location is null)
            {
                documentLevel.Add(finding);
                continue;
            }

            var mark = ResolveMark(marks, finding, hint);
            if (mark == null) unlinked.Add(finding);
            else byMark[mark.Id].Add(finding.LinkedTo(mark.Id));
        }

        var summaries = marks
            .Select(m =>
            {
                var findings = byMark[m.Id];
                var outcome = Outcome(m, findings);
                // The constraint is only worth printing where the text did NOT
                // come back: for a recovered mark it is redundant noise.
                var fit = outcome is MarkRecoveryOutcome.Recovered or MarkRecoveryOutcome.PartiallyRecovered
                    ? null
                    : _fits.GetValueOrDefault(m.Id);
                return new MarkSummary(m, outcome, findings, fit);
            })
            .ToList();

        return new RecoveryReport(summaries, unlinked, documentLevel,
            _channelsRun, _channelsSkipped);
    }

    private void SynthesiseMarksFromHints(List<RedactionMark> marks)
    {
        var perPage = marks.GroupBy(m => m.PageNumber)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var (finding, hint) in _findings)
        {
            if (hint is not { } rect || finding.Location is not { } location) continue;
            var normalized = rect.Normalize();
            if (marks.Any(m => m.PageNumber == location.PageNumber && Overlaps(m.Rect, normalized, 0.6)))
                continue;

            perPage.TryGetValue(location.PageNumber, out var n);
            perPage[location.PageNumber] = ++n;
            // The kind follows the evidence: a channel reading under a painted
            // obstruction saw a box; the residue channel saw a hole where a run
            // used to be and nothing was drawn over it.
            var kind = finding.Channel == "residue"
                ? RedactionMarkKind.EmptiedRegion
                : RedactionMarkKind.FilledBox;
            marks.Add(new RedactionMark(
                $"p{location.PageNumber}m{n}", location.PageNumber, normalized, kind,
                kind == RedactionMarkKind.EmptiedRegion
                    ? "emptied region (no mark drawn)"
                    : $"obstruction reported by the {finding.Channel} channel"));
        }

        marks.Sort((a, b) => a.PageNumber != b.PageNumber
            ? a.PageNumber.CompareTo(b.PageNumber)
            : b.Rect.Top.CompareTo(a.Rect.Top));
    }

    private static RedactionMark? ResolveMark(
        List<RedactionMark> marks, RecoveredFinding finding, PdfRectangle? hint)
    {
        var page = finding.Location!.PageNumber;
        var onPage = marks.Where(m => m.PageNumber == page).ToList();
        if (onPage.Count == 0) return null;

        // The channel's own answer first: it knows which rectangle it read under.
        if (hint is { } h)
        {
            var exact = onPage
                .Select(m => (Mark: m, Score: OverlapFraction(m.Rect, h.Normalize())))
                .Where(x => x.Score >= 0.6)
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
            if (exact.Mark != null) return exact.Mark;
        }

        var best = onPage
            .Select(m => (Mark: m, Score: OverlapFraction(finding.Location.Rect, m.Rect)))
            .Where(x => x.Score >= LinkOverlapFraction)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();
        return best.Mark;
    }

    /// <summary>
    /// Grade one mark from the findings linked to it.
    ///
    /// <para><b>The grade is GEOMETRIC, and it under-claims on purpose.</b> A
    /// carrier finding recovers the full string ("HARPER") but its location is
    /// only the stub glyphs the carrier span enclosed, so a 60pt mark grades
    /// <i>partially recovered</i> even though the text came back whole. That
    /// looks like a bug and is not one: nothing establishes that the carrier
    /// restates EVERYTHING the mark removed. More text may have been deleted
    /// that no carrier mentions, and the geometry is the only evidence either
    /// way. Grading it "recovered" would assert coverage the channels did not
    /// measure — the same overstatement, one level up, that makes this tool
    /// report candidate sets instead of answers.</para>
    /// </summary>
    private static MarkRecoveryOutcome Outcome(RedactionMark mark, List<RecoveredFinding> findings)
    {
        if (findings.Count == 0) return MarkRecoveryOutcome.NotRecovered;

        var certain = findings
            .Where(f => f.Confidence == RecoveryConfidence.Certain && f.Location != null)
            .Select(f => f.Location!.Rect.Normalize())
            .ToList();
        if (certain.Count == 0) return MarkRecoveryOutcome.CandidatesOnly;

        return CoveredWidthFraction(mark.Rect, certain) >= FullRecoveryWidthFraction
            ? MarkRecoveryOutcome.Recovered
            : MarkRecoveryOutcome.PartiallyRecovered;
    }

    /// <summary>
    /// Fraction of the mark's width spanned by the union of the given boxes.
    /// Union, not sum: two findings over the same run would otherwise add up to
    /// more coverage than the mark has, and report a partial recovery as full.
    /// </summary>
    internal static double CoveredWidthFraction(PdfRectangle mark, IReadOnlyList<PdfRectangle> boxes)
    {
        var m = mark.Normalize();
        var width = m.Right - m.Left;
        if (width <= 0) return 0;

        var spans = boxes
            .Select(b => (Left: Math.Max(m.Left, b.Left), Right: Math.Min(m.Right, b.Right)))
            .Where(s => s.Right > s.Left)
            .OrderBy(s => s.Left)
            .ToList();
        if (spans.Count == 0) return 0;

        double covered = 0;
        var cursor = spans[0].Left;
        var end = spans[0].Right;
        foreach (var span in spans.Skip(1))
        {
            if (span.Left > end)
            {
                covered += end - cursor;
                cursor = span.Left;
                end = span.Right;
            }
            else if (span.Right > end)
            {
                end = span.Right;
            }
        }
        covered += end - cursor;
        return Math.Min(1.0, covered / width);
    }

    /// <summary>How much of <paramref name="subject"/> lies inside <paramref name="container"/>.</summary>
    internal static double OverlapFraction(PdfRectangle subject, PdfRectangle container)
    {
        var s = subject.Normalize();
        var c = container.Normalize();
        var left = Math.Max(s.Left, c.Left);
        var right = Math.Min(s.Right, c.Right);
        var bottom = Math.Max(s.Bottom, c.Bottom);
        var top = Math.Min(s.Top, c.Top);
        var inter = Math.Max(0, right - left) * Math.Max(0, top - bottom);
        var area = Math.Max(1e-6, (s.Right - s.Left) * (s.Top - s.Bottom));
        return inter / area;
    }

    private static bool Overlaps(PdfRectangle a, PdfRectangle b, double fraction)
        => OverlapFraction(b, a) >= fraction || OverlapFraction(a, b) >= fraction;
}
