using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1123 — the OUTPUT. Everything else in RC13 produces numbers; this turns them
/// into the answer to "how good is excise, and against whom?" — not a single
/// score (the three axes trade against each other; a raster baseline scores
/// perfect on Leak and terrible on the rest), but a FAILURE TAXONOMY: named
/// classes, each with a stratum and a percentage, that a person can act on.
///
/// <para>Reads the benchmark's <c>results.jsonl</c> (one row per document,
/// target, tool) so it is decoupled from the runner and diffable. The design
/// rules are enforced by construction: never one number, always per-stratum,
/// and <see cref="Coverage"/> records what was NOT run so a scorecard covering
/// 60% of the matrix cannot read as one covering all of it.</para>
/// </summary>
public static class RedactionScorecard
{
    /// <summary>
    /// The subset of a benchmark row the taxonomy AND the #1163 grade score.
    /// Fields beyond what FailureTaxonomy uses (Verdict, ProbeUsable,
    /// LeakSavedBytes, VisualTermReadable, the SurvivingWords* trio,
    /// SurvivingRenderDelta) exist only for <see cref="GradeCase"/> /
    /// <see cref="ComputeScorecards"/> below.
    /// </summary>
    public sealed record Row(
        string Tool, string Corpus, string Document, string Term,
        bool LeakOracleText, IReadOnlyList<string> LeakChannels,
        double CollateralFraction, bool QpdfOk, bool InputQpdfOk,
        string StructuralDropped, string? Error,
        string Verdict = "Unmeasured", bool ProbeUsable = false, bool LeakSavedBytes = false,
        int VisualTermReadable = -1, int SurvivingWordsChecked = 0, int SurvivingWordsDamaged = 0,
        double SurvivingRenderDelta = -1);

    public static IReadOnlyList<Row> Parse(IEnumerable<string> jsonlLines)
    {
        var rows = new List<Row>();
        foreach (var line in jsonlLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var m = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line)!;
            // #1400: the header line WriteReport prepends (commit, timestamp,
            // leakEngines -- the run's actual measurement basis, not an
            // environment probe run later) is not a benchmark row. Without
            // this skip it silently became a bogus Row with every field
            // defaulted to "" / false / 0, counted into Coverage/taxonomy.
            if (m.ContainsKey("_meta")) continue;
            string S(string k) => m.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            bool B(string k) => m.TryGetValue(k, out var v) && (v.ValueKind == JsonValueKind.True);
            double D(string k) => m.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            int I(string k, int fallback) => m.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
            var channels = m.TryGetValue("leakChannels", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                : new List<string>();
            rows.Add(new Row(S("tool"), S("corpus"), S("document"), S("term"),
                B("leakOracleText"), channels, D("collateralFraction"),
                B("qpdfOk"), B("inputQpdfOk"), S("structuralDropped"),
                m.TryGetValue("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null,
                Verdict: m.TryGetValue("verdict", out var vv) && vv.ValueKind == JsonValueKind.String ? vv.GetString()! : "Unmeasured",
                ProbeUsable: B("probeUsable"),
                LeakSavedBytes: B("leakSavedBytes"),
                VisualTermReadable: I("visualTermReadable", -1),
                SurvivingWordsChecked: I("survivingWordsChecked", 0),
                SurvivingWordsDamaged: I("survivingWordsDamaged", 0),
                SurvivingRenderDelta: m.TryGetValue("survivingRenderDelta", out var srd) && srd.ValueKind == JsonValueKind.Number ? srd.GetDouble() : -1));
        }
        return rows;
    }

    /// <summary>What the matrix did NOT cover — the anti-silent-hole record.</summary>
    public sealed record Coverage(int Measured, int Errored, IReadOnlyList<string> ToolsSeen);

    public static Coverage CoverageOf(IReadOnlyList<Row> rows) => new(
        rows.Count(r => r.Error == null),
        rows.Count(r => r.Error != null),
        rows.Select(r => r.Tool).Distinct().OrderBy(t => t).ToList());

    /// <summary>
    /// The taxonomy: one line per named failure class that actually occurred,
    /// each carrying the tool, the stratum, and the percentage. Empty when a
    /// tool has no failures in a class — a clean axis says nothing rather than
    /// "0%", so the list IS the actionable set.
    /// </summary>
    public static IReadOnlyList<string> FailureTaxonomy(
        IReadOnlyList<Row> rows, double collateralThreshold = 0.05)
    {
        var lines = new List<string>();
        var measured = rows.Where(r => r.Error == null).ToList();

        foreach (var tool in measured.Select(r => r.Tool).Distinct().OrderBy(t => t))
        {
            var tr = measured.Where(r => r.Tool == tool).ToList();

            // Leak by CHANNEL × corpus stratum: "leaks via /ActualText on N% of X".
            foreach (var byCorpus in tr.GroupBy(r => r.Corpus))
            {
                var n = byCorpus.Count();
                foreach (var channel in byCorpus.SelectMany(r => r.LeakChannels).Distinct().OrderBy(c => c))
                {
                    var hits = byCorpus.Count(r => r.LeakChannels.Contains(channel));
                    if (hits > 0)
                        lines.Add($"{tool} leaks via {channel} on {Pct(hits, n)} of {byCorpus.Key} " +
                                  $"({hits}/{n})");
                }
            }

            // Collateral above threshold, per corpus stratum.
            foreach (var byCorpus in tr.GroupBy(r => r.Corpus))
            {
                var n = byCorpus.Count();
                var over = byCorpus.Count(r => r.CollateralFraction > collateralThreshold);
                if (over > 0)
                    lines.Add($"{tool} destroys >{collateralThreshold:P0} collateral on " +
                              $"{Pct(over, n)} of {byCorpus.Key} ({over}/{n})");
            }

            // Fidelity: TOOL-caused invalidity only (input already-broken discounted).
            var toolBroke = tr.Count(r => r.InputQpdfOk && !r.QpdfOk);
            if (toolBroke > 0)
                lines.Add($"{tool} produces qpdf-invalid output on {Pct(toolBroke, tr.Count)} of cases " +
                          $"({toolBroke}/{tr.Count})");

            // Structural drops.
            var struc = tr.Count(r => !string.IsNullOrEmpty(r.StructuralDropped));
            if (struc > 0)
                lines.Add($"{tool} drops document structure on {Pct(struc, tr.Count)} of cases " +
                          $"({struc}/{tr.Count})");
        }

        // Head-to-head where tools DISAGREE on the same case — the actionable part.
        foreach (var g in measured.GroupBy(r => $"{r.Corpus}/{r.Document}|{r.Term}")
                                  .Where(g => g.Select(r => r.Tool).Distinct().Count() > 1))
        {
            var excise = g.FirstOrDefault(r => r.Tool == "excise");
            var other = g.FirstOrDefault(r => r.Tool != "excise");
            if (excise == null || other == null) continue;
            if (excise.LeakOracleText && !other.LeakOracleText)
                lines.Add($"excise LOSES to {other.Tool} on {g.Key} (excise leaks text, {other.Tool} does not)");
            else if (!excise.LeakOracleText && other.LeakOracleText)
                lines.Add($"excise BEATS {other.Tool} on {g.Key} ({other.Tool} leaks text, excise does not)");
        }

        return lines;
    }

    private static string Pct(int hit, int total) => total == 0 ? "0%" : $"{(double)hit / total:P0}";

    // =========================================================================
    // #1163 — the GRADE. FailureTaxonomy above answers "what's broken and
    // where"; this answers "how good is this tool, as ONE comparable number
    // per tool (and per tool per corpus, so a tool that's great on simple
    // docs and terrible on one corpus does not average the terrible part
    // away)". Reads the same Row-shaped results.jsonl lines; a pure function
    // over already-recorded per-axis measurements — re-derivable without a
    // fresh (expensive) bench run, and unit-testable against a small
    // synthetic fixture rather than the real corpus.
    // =========================================================================

    /// <summary>
    /// One case's four category scores, each in [0, 1] or null when that
    /// category could not be measured for this case (e.g. Security is null
    /// when <see cref="Row.ProbeUsable"/> is false — the case's leak signal
    /// is a common-word coincidence, not evidence about redaction, so it
    /// must not be averaged in as either a pass or a fail; see #1182).
    /// </summary>
    public sealed record CaseGrade(
        string Tool, string Corpus, string Document, string Term,
        double? Security, double? Fidelity, double? Integrity, double? Robustness);

    /// <summary>
    /// Grades ONE case across the four categories named in #1163:
    /// <list type="bullet">
    /// <item><b>Security</b> — the secret gone across every channel measured:
    /// content/structure (via <see cref="Row.Verdict"/> — Removed=clean,
    /// RemovedWithResidue=half credit for the layout-gap side channel #1116
    /// quantifies, Recoverable=zero), carrier bytes
    /// (<see cref="Row.LeakSavedBytes"/> — the saved-bytes scan CLAUDE.md's
    /// redaction rules require, independent of the content-stream read
    /// LeakOracleText/Verdict already cover), and OCR-on-render
    /// (<see cref="Row.VisualTermReadable"/> — is the secret still legible
    /// in PIXELS). Gated on <see cref="Row.ProbeUsable"/>, the bench's
    /// existing canary/common-word discriminator (#1182): a term that also
    /// lives somewhere redaction was never asked to touch (JS, field names,
    /// viewer boilerplate) cannot indict the tool, so an unusable probe
    /// contributes NO security channels rather than a false pass or fail.</item>
    /// <item><b>Fidelity</b> — surviving CONTENT conserved
    /// (<see cref="Row.SurvivingWordsChecked"/>/<see cref="Row.SurvivingWordsDamaged"/>,
    /// #1157) is the ANCHOR, double-weighted against collateral character
    /// loss (<see cref="Row.CollateralFraction"/>) and whole-page render
    /// stability (<see cref="Row.SurvivingRenderDelta"/>) — per the issue,
    /// render-delta alone under-measures damage concentrated at the masked
    /// term's own region, so it is a secondary signal here, not the
    /// anchor.</item>
    /// <item><b>Integrity</b> — output is a valid PDF the tool itself broke
    /// (<see cref="Row.InputQpdfOk"/> discounts a pre-broken input, matching
    /// FailureTaxonomy's existing rule) and document structures survived
    /// (<see cref="Row.StructuralDropped"/> empty).</item>
    /// <item><b>Robustness</b> — the document was handled at all
    /// (<see cref="Row.Error"/> null). On an errored case the other three
    /// axes have nothing to measure and are null, not zero: a crash is a
    /// Robustness defect, not evidence the (unmeasured) redaction leaked or
    /// was faithful.</item>
    /// </list>
    /// </summary>
    public static CaseGrade GradeCase(Row r)
    {
        if (r.Error != null)
            return new CaseGrade(r.Tool, r.Corpus, r.Document, r.Term, null, null, null, 0.0);

        double? security = null;
        if (r.ProbeUsable)
        {
            var channels = new List<double>();
            var verdictScore = r.Verdict switch
            {
                "Removed" => 1.0,
                "RemovedWithResidue" => 0.5,
                "Recoverable" => 0.0,
                _ => (double?)null, // "Unmeasured" -- excluded, not scored as either
            };
            if (verdictScore is { } vs) channels.Add(vs);
            channels.Add(r.LeakSavedBytes ? 0.0 : 1.0);
            if (r.VisualTermReadable == 0) channels.Add(1.0);
            else if (r.VisualTermReadable == 1) channels.Add(0.0);
            // VisualTermReadable == -1 (not measured) contributes nothing.
            if (channels.Count > 0) security = channels.Average();
        }

        double? fidelity = null;
        {
            var weighted = new List<(double Score, double Weight)>();
            if (r.SurvivingWordsChecked > 0)
            {
                var damageFraction = Math.Clamp((double)r.SurvivingWordsDamaged / r.SurvivingWordsChecked, 0, 1);
                weighted.Add((1.0 - damageFraction, 2.0)); // the Fidelity ANCHOR (#1157)
            }
            weighted.Add((1.0 - Math.Clamp(r.CollateralFraction, 0, 1), 1.0));
            if (r.SurvivingRenderDelta >= 0)
                weighted.Add((1.0 - Math.Clamp(r.SurvivingRenderDelta, 0, 1), 1.0));
            if (weighted.Count > 0)
                fidelity = weighted.Sum(w => w.Score * w.Weight) / weighted.Sum(w => w.Weight);
        }

        var qpdfScore = (r.InputQpdfOk && !r.QpdfOk) ? 0.0 : 1.0;
        var structuralScore = string.IsNullOrEmpty(r.StructuralDropped) ? 1.0 : 0.0;
        var integrity = (qpdfScore + structuralScore) / 2.0;

        return new CaseGrade(r.Tool, r.Corpus, r.Document, r.Term, security, fidelity, integrity, 1.0);
    }

    /// <summary>One tool's grade, either aggregated ("ALL") or for one corpus.</summary>
    public sealed record Scorecard(
        string Tool, string Corpus, int Cases,
        double? SecurityPct, double? FidelityPct, double? IntegrityPct, double? RobustnessPct,
        double? OverallPct, string Grade);

    /// <summary>
    /// The letter-grade cutoffs. No explicit table existed in the codebase
    /// to reuse (checked: <c>UnredactionScorecard.Grade</c> is an unrelated
    /// per-channel-recovery record, not a letter scale; the #1159 copy-quality
    /// companion scorecard this issue's comments reference does not exist
    /// yet). Inferred from the issue's own hand-validated worked example —
    /// the four points below are the ONLY hard constraints; the rest of the
    /// ladder is filled in at even, defensible intervals rather than a
    /// standard school scale, because a standard scale's A- band (usually
    /// 90-92) does not fit the issue's 88.8 -> A- exactly.
    /// excise 88.8 -> A-, pymupdf 82.6 -> B, itext 49.5 -> F, raster 0.0 -> F.
    /// </summary>
    public static string LetterGrade(double overallPct) => overallPct switch
    {
        >= 93 => "A",
        >= 88 => "A-",
        >= 85 => "B+",
        >= 80 => "B",
        >= 77 => "B-",
        >= 73 => "C+",
        >= 70 => "C",
        >= 67 => "C-",
        >= 63 => "D+",
        >= 60 => "D",
        _ => "F",
    };

    /// <summary>
    /// One Scorecard per (tool, corpus) PLUS one per tool aggregated across
    /// every corpus it ran on ("ALL") — per-corpus so a tool that is great on
    /// simple documents and terrible on one corpus cannot average the
    /// terrible part away (the issue's "concentration" requirement); "ALL"
    /// because a single comparable number per tool is still the point.
    /// <para>Overall = Security × Fidelity — a MULTIPLIER, not a weighted
    /// term. This is the load-bearing design choice named in the issue: a
    /// leak cannot be bought back with pretty output (a tool that leaks
    /// scores near zero no matter how clean its output otherwise is), and a
    /// tool that is secure but destroys the document scores near zero too
    /// (Fidelity collapses toward 0 regardless of a perfect Security score).
    /// A naive weighted average would let either failure mode hide behind
    /// the other axis; see <c>RedactionScorecardTests</c> for both pinned.</para>
    /// <para>Integrity and Robustness are reported per category but are
    /// deliberately NOT part of Overall — the issue's formula is exactly
    /// Security × Fidelity, nothing else multiplied in.</para>
    /// </summary>
    public static IReadOnlyList<Scorecard> ComputeScorecards(IReadOnlyList<Row> rows)
    {
        var results = new List<Scorecard>();
        foreach (var toolRows in rows.GroupBy(r => r.Tool).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            foreach (var corpusRows in toolRows.GroupBy(r => r.Corpus).OrderBy(g => g.Key, StringComparer.Ordinal))
                results.Add(BuildScorecard(toolRows.Key, corpusRows.Key, corpusRows.ToList()));
            results.Add(BuildScorecard(toolRows.Key, "ALL", toolRows.ToList()));
        }
        return results;
    }

    private static Scorecard BuildScorecard(string tool, string corpus, IReadOnlyList<Row> rows)
    {
        var grades = rows.Select(GradeCase).ToList();
        var security = Avg(grades.Select(g => g.Security));
        var fidelity = Avg(grades.Select(g => g.Fidelity));
        var integrity = Avg(grades.Select(g => g.Integrity));
        var robustness = Avg(grades.Select(g => g.Robustness));

        double? overallPct = security is { } s && fidelity is { } f ? s * f * 100.0 : null;
        var grade = overallPct is { } o ? LetterGrade(o) : "N/A";

        return new Scorecard(
            tool, corpus, rows.Count,
            security * 100.0, fidelity * 100.0, integrity * 100.0, robustness * 100.0,
            overallPct, grade);
    }

    private static double? Avg(IEnumerable<double?> values)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return present.Count == 0 ? null : present.Average();
    }

    /// <summary>Human-readable table, matching the shape of the issue's worked example.</summary>
    public static string Render(IReadOnlyList<Scorecard> scorecards)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("SCORECARD (#1163) — Security x Fidelity, per tool");
        sb.AppendLine($"{"tool",-16} {"corpus",-14} {"n",4} {"security",9} {"fidelity",9} {"integrity",9} {"robust.",8} {"overall",8} grade");
        foreach (var sc in scorecards)
        {
            string P(double? v) => v is { } d ? $"{d:F1}%" : "n/a";
            sb.AppendLine($"{sc.Tool,-16} {sc.Corpus,-14} {sc.Cases,4} {P(sc.SecurityPct),9} {P(sc.FidelityPct),9} " +
                          $"{P(sc.IntegrityPct),9} {P(sc.RobustnessPct),8} {P(sc.OverallPct),8} {sc.Grade}");
        }
        return sb.ToString();
    }
}
