using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1181 — the de-redaction OUTPUT, mirroring <see cref="RedactionScorecard"/>
/// for the recovery side. Turns the four scattered unredaction measurements
/// (certain recall vs x-ray, residue recall@N per band, tool-resistance leak)
/// into the answer to "how good is excise at de-redaction, and against whom?".
///
/// <para>Same design rules as the redaction scorecard: NEVER one number —
/// recovery power trades against false-positive risk, and a channel that
/// recovers everything under a box is useless on a scanned page — so it grades
/// per CHANNEL and per STRATUM, per TOOL. <see cref="Coverage"/> records what was
/// NOT measured, so a scorecard over two channels cannot read as one over all.
/// Pure aggregation over typed rows, so the grading is unit-tested without any
/// engine or external tool.</para>
/// </summary>
public static class UnredactionScorecard
{
    /// <summary>One recovery attempt: did <paramref name="Tool"/> recover the placed answer.</summary>
    public sealed record Row(string Channel, string Stratum, string Tool, bool Recovered, double ResidualBits = 0);

    /// <summary>Per (channel, stratum, tool) recall.</summary>
    public sealed record Grade(
        string Channel, string Stratum, string Tool, int Recovered, int Total, double MedianResidualBits)
    {
        public double RecallPct => Total == 0 ? 0 : 100.0 * Recovered / Total;
    }

    /// <summary>What the scorecard did and did not see — never assume full coverage.</summary>
    public sealed record Coverage(
        IReadOnlyList<string> Channels, IReadOnlyList<string> Tools, IReadOnlyList<string> MissingReferences);

    public static IReadOnlyList<Grade> Score(IEnumerable<Row> rows)
    {
        return rows
            .GroupBy(r => (r.Channel, r.Stratum, r.Tool))
            .Select(g =>
            {
                var bits = g.Select(r => r.ResidualBits).OrderBy(b => b).ToList();
                return new Grade(g.Key.Channel, g.Key.Stratum, g.Key.Tool,
                    g.Count(r => r.Recovered), g.Count(), bits.Count == 0 ? 0 : bits[bits.Count / 2]);
            })
            .OrderBy(g => g.Channel).ThenBy(g => g.Stratum).ThenBy(g => g.Tool)
            .ToList();
    }

    /// <summary>
    /// Excise's advantage per (channel, stratum): its recall minus the BEST
    /// reference tool's recall on the same stratum. Positive means excise leads.
    /// A stratum with no reference tool is reported as such, never as a win.
    /// </summary>
    public static IReadOnlyList<(string Channel, string Stratum, double ExcisePct, double BestRefPct, string BestRef)>
        ExciseVsBestReference(IEnumerable<Grade> grades)
    {
        var result = new List<(string, string, double, double, string)>();
        foreach (var byCS in grades.GroupBy(g => (g.Channel, g.Stratum)))
        {
            var excise = byCS.FirstOrDefault(g => g.Tool == "excise");
            if (excise == null) continue;
            var refs = byCS.Where(g => g.Tool != "excise").ToList();
            if (refs.Count == 0)
                result.Add((byCS.Key.Channel, byCS.Key.Stratum, excise.RecallPct, double.NaN, "(none)"));
            else
            {
                var best = refs.OrderByDescending(g => g.RecallPct).First();
                result.Add((byCS.Key.Channel, byCS.Key.Stratum, excise.RecallPct, best.RecallPct, best.Tool));
            }
        }
        return result;
    }

    public static string Render(IReadOnlyList<Grade> grades, Coverage coverage)
    {
        var sb = new StringBuilder();
        sb.AppendLine("═══ UNREDACTION SCORECARD (#1181) — how good is excise at de-redaction, and against whom ═══");
        sb.AppendLine($"channels: {string.Join(", ", coverage.Channels)}   tools: {string.Join(", ", coverage.Tools)}");
        if (coverage.MissingReferences.Count > 0)
            sb.AppendLine($"⚠ reference NOT measured (recovery uncompared): {string.Join(", ", coverage.MissingReferences)}");
        sb.AppendLine();
        foreach (var g in grades)
            sb.AppendLine($"  {g.Channel,-8} {g.Stratum,-14} {g.Tool,-10} " +
                $"recall {g.Recovered}/{g.Total} ({g.RecallPct,5:F1}%)" +
                (g.MedianResidualBits > 0 ? $"  median {g.MedianResidualBits:F1} bits" : ""));
        sb.AppendLine();
        sb.AppendLine("── excise vs best reference ──");
        foreach (var (ch, st, ex, best, who) in ExciseVsBestReference(grades))
            sb.AppendLine(double.IsNaN(best)
                ? $"  {ch,-8} {st,-14} excise {ex,5:F1}%   (no reference tool available)"
                : $"  {ch,-8} {st,-14} excise {ex,5:F1}%  vs  {who} {best,5:F1}%   → {(ex - best >= 0 ? "+" : "")}{ex - best:F1} pts");
        return sb.ToString();
    }
}
