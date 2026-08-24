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
    /// <summary>The subset of a benchmark row the taxonomy scores.</summary>
    public sealed record Row(
        string Tool, string Corpus, string Document, string Term,
        bool LeakOracleText, IReadOnlyList<string> LeakChannels,
        double CollateralFraction, bool QpdfOk, bool InputQpdfOk,
        string StructuralDropped, string? Error);

    public static IReadOnlyList<Row> Parse(IEnumerable<string> jsonlLines)
    {
        var rows = new List<Row>();
        foreach (var line in jsonlLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var m = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line)!;
            string S(string k) => m.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            bool B(string k) => m.TryGetValue(k, out var v) && (v.ValueKind == JsonValueKind.True);
            double D(string k) => m.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            var channels = m.TryGetValue("leakChannels", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                : new List<string>();
            rows.Add(new Row(S("tool"), S("corpus"), S("document"), S("term"),
                B("leakOracleText"), channels, D("collateralFraction"),
                B("qpdfOk"), B("inputQpdfOk"), S("structuralDropped"),
                m.TryGetValue("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null));
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
}
