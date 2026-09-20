using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Excise.Core.Redaction.Recovery;
using Excise.TestSupport;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1645 — DETECTION scored as a confusion matrix, and RECOVERY scored on a
/// ladder, per tool and per failure mode.
///
/// <para><b>What was missing.</b> <see cref="UnredactionScorecard"/> measures
/// recall on leak strata and specificity on control strata, which is all four
/// cells — but split across strata and never combined, so there is no precision
/// and no single matrix per tool. Worse, its <c>Recovered</c> is a <b>bool</b>:
/// a reading with zero residual bits and a 200-candidate guess score
/// identically, when the difference between them is the entire question a
/// de-redaction audit answers.</para>
///
/// <para><b>Why FALSE POSITIVES need their own population.</b> #1624 shipped a
/// defect that reported <b>83.7% of a clean court filing as hidden text</b> —
/// 85 "recovered" marks on a document with no redaction in it — and every
/// existing bench row scored it as nothing at all, because every stratum was a
/// LEAK stratum. A bench with no negatives cannot see a tool that answers yes
/// to everything. That is why <see cref="Case.ContainsLeak"/> is ground truth
/// carried per case, not inferred.</para>
///
/// <para><b>Why scope is DECLARED, not inferred</b> (tests/unredaction-tools.json).
/// x-ray finds one mode by design. Reading its zeros on the other nineteen as
/// false negatives would make excise look better by construction and would hide
/// a real x-ray regression inside the same number. A tool scoring a hit OUTSIDE
/// its declared scope is reported as <see cref="Verdict.BeyondDeclaredScope"/>
/// rather than folded in: that means either the tool does more than it claims
/// or the registry is stale, and both want a human.</para>
/// </summary>
internal static class UnredactionConfusionMatrix
{
    /// <summary>Per (tool, case) outcome against ground truth.</summary>
    internal enum Verdict
    {
        /// <summary>A leak was there and the tool reported it.</summary>
        TruePositive,

        /// <summary>No leak was there and the tool reported one. The #1624 cell.</summary>
        FalsePositive,

        /// <summary>A leak was there and the tool missed it — only counted IN SCOPE.</summary>
        FalseNegative,

        /// <summary>No leak, nothing reported.</summary>
        TrueNegative,

        /// <summary>The mode is outside this tool's declared scope. Not a miss.</summary>
        OutOfScope,

        /// <summary>A hit on a mode the tool does not claim. Report it; never average it in.</summary>
        BeyondDeclaredScope,
    }

    /// <summary>
    /// One bench document with its ground truth.
    /// </summary>
    /// <param name="ContainsLeak">
    /// ⚠️ GROUND TRUTH, and the only field that may not be derived from a tool's
    /// output. A negative case whose truth came from "excise found nothing" would
    /// make the false-positive column unable to fail.
    /// </param>
    internal sealed record Case(string ModeId, string CaseId, bool ContainsLeak);

    /// <summary>What one tool did on one case.</summary>
    /// <param name="Detected">Did it report a leak at all — the detection question.</param>
    /// <param name="Outcome">
    /// The recovery rung. Reuses <see cref="MarkRecoveryOutcome"/> rather than
    /// inventing a parallel ordinal, because the engine already grades on it.
    /// </param>
    /// <param name="CandidateCount">0 for a reading; the admissible set size for an inference.</param>
    internal sealed record ToolResult(
        string Tool,
        string CaseId,
        bool Detected,
        MarkRecoveryOutcome Outcome = MarkRecoveryOutcome.NotRecovered,
        double ResidualBits = 0,
        int CandidateCount = 0,
        string? DominantCarrier = null);

    /// <summary>One (tool, mode) cell.</summary>
    internal sealed record Cell(
        string Tool, string ModeId,
        int TruePositive, int FalsePositive, int FalseNegative, int TrueNegative,
        int OutOfScope, int BeyondDeclaredScope)
    {
        public int Scored => TruePositive + FalsePositive + FalseNegative + TrueNegative;

        /// <summary>Of what it reported, how much was real. Null when it reported nothing.</summary>
        public double? Precision => TruePositive + FalsePositive == 0
            ? null : (double)TruePositive / (TruePositive + FalsePositive);

        /// <summary>Of what was there, how much it found. Null when nothing was there.</summary>
        public double? Recall => TruePositive + FalseNegative == 0
            ? null : (double)TruePositive / (TruePositive + FalseNegative);

        /// <summary>Of what was NOT there, how much it correctly left alone.</summary>
        public double? Specificity => TrueNegative + FalsePositive == 0
            ? null : (double)TrueNegative / (TrueNegative + FalsePositive);

        public double? F1 => Precision is { } p && Recall is { } r && p + r > 0
            ? 2 * p * r / (p + r) : null;
    }

    /// <summary>
    /// How a tool's true positives were actually answered. Two tools with the
    /// same recall are not equally useful if one reads the bytes and the other
    /// returns forty candidates.
    /// </summary>
    internal sealed record RecoveryProfile(
        string Tool, string ModeId,
        int Exact, int Partial, int CandidatesOnly, int DetectedButNotRead,
        double MedianResidualBits, double MedianCandidateSetSize)
    {
        public int Total => Exact + Partial + CandidatesOnly + DetectedButNotRead;
    }

    /// <summary>A tool's declared scope, from tests/unredaction-tools.json.</summary>
    /// <summary>The pseudo-mode real-world negatives are filed under.</summary>
    public const string NegativeMode = "real-world-negative";

    internal sealed record ToolScope(string Id, string Kind, IReadOnlySet<string>? Modes, bool AllModes)
    {
        /// <summary>
        /// ⚠️ <see cref="NegativeMode"/> is in scope for EVERY tool. A negative
        /// has no failure mode to be out of scope for, and a false positive is
        /// meaningful for any tool — letting scope excuse one would mean a
        /// detector could never be measured for over-firing.
        /// </summary>
        public bool Covers(string modeId) =>
            modeId == NegativeMode || AllModes || (Modes?.Contains(modeId) ?? false);
    }

    public static string? RegistryPath =>
        TestRepoLayout.FindFile("tests", "unredaction-tools.json");

    /// <summary>Read the declared scopes. Empty when the registry cannot be found.</summary>
    public static IReadOnlyList<ToolScope> Scopes()
    {
        var path = RegistryPath;
        if (path == null) return Array.Empty<ToolScope>();

        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        var scopes = new List<ToolScope>();
        foreach (var t in doc.RootElement.GetProperty("tools").EnumerateArray())
        {
            var id = t.GetProperty("id").GetString()!;
            var kind = t.TryGetProperty("kind", out var k) ? k.GetString() ?? "unknown" : "unknown";
            var scope = t.GetProperty("scope");
            if (scope.ValueKind == JsonValueKind.String && scope.GetString() == "*")
            {
                scopes.Add(new ToolScope(id, kind, null, AllModes: true));
                continue;
            }
            var modes = scope.ValueKind == JsonValueKind.Array
                ? scope.EnumerateArray().Select(m => m.GetString()!).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            scopes.Add(new ToolScope(id, kind, modes, AllModes: false));
        }
        return scopes;
    }

    /// <summary>Classify one result against its case and the tool's declared scope.</summary>
    internal static Verdict Classify(Case c, ToolResult r, ToolScope scope)
    {
        var inScope = scope.Covers(c.ModeId);

        if (!inScope)
            // A hit outside the declared scope is NOT quietly counted as a win:
            // it means the registry is wrong or the tool changed. Silence outside
            // scope is simply not a miss.
            return r.Detected ? Verdict.BeyondDeclaredScope : Verdict.OutOfScope;

        return (c.ContainsLeak, r.Detected) switch
        {
            (true, true) => Verdict.TruePositive,
            (true, false) => Verdict.FalseNegative,
            (false, true) => Verdict.FalsePositive,
            (false, false) => Verdict.TrueNegative,
        };
    }

    /// <summary>Aggregate into one cell per (tool, mode).</summary>
    public static IReadOnlyList<Cell> Score(
        IReadOnlyList<Case> cases, IReadOnlyList<ToolResult> results, IReadOnlyList<ToolScope> scopes)
    {
        var byId = cases.ToDictionary(c => c.CaseId, StringComparer.Ordinal);
        var scopeById = scopes.ToDictionary(s => s.Id, StringComparer.Ordinal);

        var cells = new List<Cell>();
        foreach (var group in results
                     .Where(r => byId.ContainsKey(r.CaseId) && scopeById.ContainsKey(r.Tool))
                     .GroupBy(r => (r.Tool, byId[r.CaseId].ModeId)))
        {
            var scope = scopeById[group.Key.Tool];
            var verdicts = group.Select(r => Classify(byId[r.CaseId], r, scope)).ToList();
            cells.Add(new Cell(
                group.Key.Tool, group.Key.ModeId,
                verdicts.Count(v => v == Verdict.TruePositive),
                verdicts.Count(v => v == Verdict.FalsePositive),
                verdicts.Count(v => v == Verdict.FalseNegative),
                verdicts.Count(v => v == Verdict.TrueNegative),
                verdicts.Count(v => v == Verdict.OutOfScope),
                verdicts.Count(v => v == Verdict.BeyondDeclaredScope)));
        }
        return cells.OrderBy(c => c.Tool, StringComparer.Ordinal)
                    .ThenBy(c => c.ModeId, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The recovery ladder over TRUE POSITIVES only. A rung on a case with no
    /// leak is meaningless, and a rung on a miss is just the miss again.
    /// </summary>
    public static IReadOnlyList<RecoveryProfile> Profile(
        IReadOnlyList<Case> cases, IReadOnlyList<ToolResult> results, IReadOnlyList<ToolScope> scopes)
    {
        var byId = cases.ToDictionary(c => c.CaseId, StringComparer.Ordinal);
        var scopeById = scopes.ToDictionary(s => s.Id, StringComparer.Ordinal);

        var profiles = new List<RecoveryProfile>();
        foreach (var group in results
                     .Where(r => byId.ContainsKey(r.CaseId) && scopeById.ContainsKey(r.Tool))
                     .Where(r => Classify(byId[r.CaseId], r, scopeById[r.Tool]) == Verdict.TruePositive)
                     .GroupBy(r => (r.Tool, byId[r.CaseId].ModeId)))
        {
            var rows = group.ToList();
            profiles.Add(new RecoveryProfile(
                group.Key.Tool, group.Key.ModeId,
                rows.Count(r => r.Outcome == MarkRecoveryOutcome.Recovered),
                rows.Count(r => r.Outcome == MarkRecoveryOutcome.PartiallyRecovered),
                rows.Count(r => r.Outcome == MarkRecoveryOutcome.CandidatesOnly),
                rows.Count(r => r.Outcome == MarkRecoveryOutcome.NotRecovered),
                Median(rows.Select(r => r.ResidualBits)),
                Median(rows.Where(r => r.CandidateCount > 0).Select(r => (double)r.CandidateCount))));
        }
        return profiles.OrderBy(p => p.Tool, StringComparer.Ordinal)
                       .ThenBy(p => p.ModeId, StringComparer.Ordinal).ToList();
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0) return 0;
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2.0;
    }

    /// <summary>
    /// The report. Names what was NOT scored as prominently as what was —
    /// the same rule the tier-A scorecard follows, for the same reason: a matrix
    /// over two modes must not read like a matrix over twenty.
    /// </summary>
    public static string Render(
        IReadOnlyList<Cell> cells, IReadOnlyList<RecoveryProfile> profiles, IReadOnlyList<string> notMeasured)
        => Render(cells, profiles, notMeasured, Array.Empty<(Case, ToolResult)>());

    /// <summary>
    /// With <paramref name="falsePositives"/>, the report breaks the FP column
    /// down BY CAUSE. One discouraging number invites either despair or a
    /// heuristic that grades around the problem; "28 of 33 are OCR layers"
    /// invites the right conversation.
    /// </summary>
    public static string Render(
        IReadOnlyList<Cell> cells, IReadOnlyList<RecoveryProfile> profiles,
        IReadOnlyList<string> notMeasured, IReadOnlyList<(Case Case, ToolResult Result)> falsePositives)
    {
        var sb = new StringBuilder();
        sb.AppendLine("═══ UNREDACTION CONFUSION MATRIX (#1645) — detection per tool per mode ═══");
        if (notMeasured.Count > 0)
            sb.AppendLine("⚠ NOT measured: " + string.Join("; ", notMeasured));
        if (cells.Count == 0) { sb.AppendLine("  nothing scored"); return sb.ToString(); }

        foreach (var tool in cells.GroupBy(c => c.Tool))
        {
            sb.AppendLine();
            sb.AppendLine($"  {tool.Key}");
            sb.AppendLine("    mode                              TP  FP  FN  TN   prec  recall  spec    F1");
            foreach (var c in tool)
            {
                // #1690 — a deferred mode's row is PRINTED (it is still
                // measured; a row that vanished could not show a regression)
                // and MARKED, so it is legible next to the graded rows and
                // visibly outside the total below them.
                var deferred = UnredactionBenchAxes.IsDeferredMode(c.ModeId) ? "  [deferred #1690]" : "";
                if (c.Scored == 0 && c.OutOfScope > 0)
                {
                    sb.AppendLine($"    {c.ModeId,-32}  —   —   —   —    out of declared scope ({c.OutOfScope} case(s))");
                    continue;
                }
                sb.AppendLine(
                    $"    {c.ModeId,-32} {c.TruePositive,3} {c.FalsePositive,3} {c.FalseNegative,3} {c.TrueNegative,3}" +
                    $"   {Pct(c.Precision)} {Pct(c.Recall)}  {Pct(c.Specificity)} {Pct(c.F1)}{deferred}");
                if (c.BeyondDeclaredScope > 0)
                    sb.AppendLine($"       ⚠ {c.BeyondDeclaredScope} hit(s) BEYOND declared scope — " +
                                  "the tool does more than tests/unredaction-tools.json claims, or that file is stale");
            }

            // ⚠️ #1690 — the TOTAL is the GRADED total: Tier 1 modes only. A
            // deferred channel reports presence, not a value, so folding its
            // cells in would move the headline with findings that recovered no
            // text — in either direction. The deferred cells are totalled
            // separately, never dropped.
            var totals = tool.Where(c => !UnredactionBenchAxes.IsDeferredMode(c.ModeId)).ToList();
            sb.AppendLine($"    {"TOTAL (graded, tier 1)",-32} {totals.Sum(c => c.TruePositive),3} {totals.Sum(c => c.FalsePositive),3} " +
                          $"{totals.Sum(c => c.FalseNegative),3} {totals.Sum(c => c.TrueNegative),3}");
            var deferredCells = tool.Where(c => UnredactionBenchAxes.IsDeferredMode(c.ModeId)).ToList();
            if (deferredCells.Count > 0)
                sb.AppendLine($"    {"deferred (#1690, not graded)",-32} {deferredCells.Sum(c => c.TruePositive),3} " +
                              $"{deferredCells.Sum(c => c.FalsePositive),3} {deferredCells.Sum(c => c.FalseNegative),3} " +
                              $"{deferredCells.Sum(c => c.TrueNegative),3}");
        }

        if (falsePositives.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("── what the FALSE POSITIVES were, by cause ──");
            // Group on the CAUSE, not "cause (n)" — the per-document finding
            // count belongs in the total, not in the key, or every row is unique
            // and the breakdown says nothing.
            static string CauseOf(string? c)
            {
                if (string.IsNullOrEmpty(c)) return "unattributed";
                var i = c.LastIndexOf(" (", StringComparison.Ordinal);
                return i < 0 ? c : c[..i];
            }
            static int CountIn(string? c)
            {
                if (string.IsNullOrEmpty(c)) return 0;
                var i = c.LastIndexOf(" (", StringComparison.Ordinal);
                return i >= 0 && int.TryParse(c[(i + 2)..].TrimEnd(')'), out var n) ? n : 0;
            }

            foreach (var g in falsePositives
                         .GroupBy(f => (f.Result.Tool, Cause: CauseOf(f.Result.DominantCarrier)))
                         .OrderByDescending(g => g.Count()))
                sb.AppendLine(
                    $"  {g.Key.Tool,-10} {g.Count(),3} document(s)  {g.Key.Cause}" +
                    $"   (findings: {g.Sum(x => CountIn(x.Result.DominantCarrier))} total, " +
                    $"worst {g.Max(x => CountIn(x.Result.DominantCarrier))})");
        }

        sb.AppendLine();
        sb.AppendLine("── how the true positives were ANSWERED ──");
        sb.AppendLine("  (exact = read from the bytes; candidates = inferred, with a set and bits left)");
        foreach (var p in profiles)
        {
            if (p.Total == 0) continue;
            sb.AppendLine(
                $"  {p.Tool,-10} {p.ModeId,-32} exact {p.Exact,3}  partial {p.Partial,3}  " +
                $"candidates {p.CandidatesOnly,3}  present-only {p.DetectedButNotRead,3}" +
                (p.CandidatesOnly > 0
                    ? $"   median bits {p.MedianResidualBits:F1}, set {p.MedianCandidateSetSize:F0}"
                    : ""));
        }
        return sb.ToString();
    }

    private static string Pct(double? v) => v is { } x ? $"{x * 100,5:F1}%" : "    —";
}
