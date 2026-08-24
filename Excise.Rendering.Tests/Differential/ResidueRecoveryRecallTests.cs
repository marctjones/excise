using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1133/#1135 — recall@N per difficulty band for the residue engine, scored
/// against the CONSTRUCTED ground truth (#1134's manifest). The manifest is the
/// oracle: we placed the answer, so recovery is checked against what we placed,
/// never against excise's own claim.
///
/// <para>A MEASUREMENT, not a ratchet — it prints recall per band and asserts
/// only (a) it measured something (anti-vacuity) and (b) the NEGATIVE-CONTROL
/// bands stay near zero. A change that lifts real recall AND lifts B8/B6 did
/// not improve the tool; it taught the gap detector to invent gaps.</para>
/// </summary>
public sealed class ResidueRecoveryRecallTests
{
    private readonly ITestOutputHelper _out;
    public ResidueRecoveryRecallTests(ITestOutputHelper o) { _out = o; }

    private sealed record Case(string Id, string Answer, string Band, string Font,
                               double SizePt, string Method, string Colour,
                               string Position, string Dictionary, double GapWidthPt);

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, ".git"))) d = d.Parent;
        return d?.FullName ?? AppContext.BaseDirectory;
    }

    private static readonly string[] Names =
        ("James John Robert Michael William David Richard Joseph Thomas Charles " +
         "Christopher Daniel Matthew Anthony Donald Mark Paul Steven Andrew Kenneth " +
         "Mary Patricia Jennifer Linda Elizabeth Barbara Susan Jessica Sarah Karen " +
         "Nancy Lisa Betty Margaret Sandra Ashley Kimberly Emily Donna Michelle " +
         "Louise Farrar Anne Dorothy Carol Amanda Melissa Deborah Stephanie").Split(' ');

    private static readonly string[] Dates =
        { "01/15/1987","12/03/1992","07/22/1975","09/30/2001","03/11/1968","11/08/1954","06/19/1983","02/27/1990" };
    private static readonly string[] Digits =
        { "4012884012","5555341220","6011000990","3782822463","8842019375","1029384756","9998887776","4444333322" };

    // The candidate set MUST be the one the answer was drawn from (#1134): recall
    // against a different dictionary measures coverage, not width discrimination.
    // Keyed off the manifest's `dictionary` field (#1134), which now records
    // the closed set the answer was actually drawn from.
    private static IReadOnlyList<string> DictionaryFor(string kind) => kind switch
    {
        "dict" or "dict-long" => Names,
        "date" => Dates,
        "digits" => Digits,
        // NOT Random: a proper "answer not in the dictionary" control searches
        // the realistic attacker dictionary (names), where the random secret is
        // structurally absent. Scoring it against its own set made recovery
        // trivially guaranteed and defeated the control (caught on first run).
        "random" => Names,
        _ => Names,
    };

    [Fact]
    public void RecallAtN_PerBand_AgainstConstructedGroundTruth()
    {
        var corpus = Path.Combine(RepoRoot(), "test-pdfs", "redaction-synthetic");
        var manifest = Path.Combine(corpus, "manifest.jsonl");
        Assert.SkipUnless(File.Exists(manifest),
            "run scripts/gen-redaction-corpus.py first [requires: corpus:redaction-synthetic]");

        var cases = File.ReadAllLines(manifest).Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(l)!)
            .Select(m => new Case(
                m["id"].GetString()!, m["answer"].GetString()!, m["band"].GetString()!,
                m["font"].GetString()!, m["sizePt"].GetDouble(), m["method"].GetString()!,
                m["colour"].GetString()!, m["position"].GetString()!,
                m["dictionary"].GetString()!, m["gapWidthPt"].GetDouble()))
            .ToList();

        // Residue recovery only applies to methods that leave a gap. under-box
        // is the certain channel (#1132); width-closing/defended are controls
        // measured below. Score residue on the gap-leaving methods.
        var scored = new List<(Case C, int Rank)>();   // Rank = 1-based position of answer, 0 = not found

        foreach (var c in cases)
        {
            var pdf = Path.Combine(corpus, c.Id + ".pdf");
            if (!File.Exists(pdf)) continue;

            var dict = DictionaryFor(c.Dictionary);

            var recs = ResidueRecoveryEngine.Recover(pdf, dict,
                new ResidueRecoveryEngine.Options(RequireMutoolCorroboration: false));

            // Best rank of the true answer across all gaps the engine found.
            var rank = 0;
            foreach (var r in recs)
            {
                var idx = r.CandidatesFit
                    .Select((w, i) => (w, i))
                    .Where(t => string.Equals(t.w, c.Answer, StringComparison.Ordinal))
                    .Select(t => t.i + 1).DefaultIfEmpty(0).First();
                if (idx > 0 && (rank == 0 || idx < rank)) rank = idx;
            }
            scored.Add((c, rank));
        }

        scored.Should().NotBeEmpty("the corpus must produce cases to score");

        // ── report recall@N per band ──────────────────────────────────────
        _out.WriteLine($"{"band",-6} {"cases",6} {"recall@1",9} {"recall@5",9} {"recall@20",10}  note");
        var byBand = scored.GroupBy(s => s.C.Band).OrderBy(g => g.Key, StringComparer.Ordinal);
        double NegControlWorst = 0;
        double BpRecallAt20 = 0;
        foreach (var g in byBand)
        {
            var n = g.Count();
            double At(int k) => (double)g.Count(s => s.Rank >= 1 && s.Rank <= k) / n;
            var note = g.Key switch
            {
                "B6" => "NEG CONTROL (secret absent from dict) -> ~0",
                "B8" => "NEG CONTROL (width-closed) -> ~0",
                "B9" => "frontier (defended) -> ~0 today",
                "Bc" => "monospace -> should be low unique (all same width)",
                "Bn" => "digit-runs -> hard (few width classes)",
                "Bp" => "single-anchor (line edge) -> box channel (#1140)",
                _ => "",
            };
            _out.WriteLine($"{g.Key,-6} {n,6} {At(1),9:P0} {At(5),9:P0} {At(20),10:P0}  {note}");
            if (g.Key is "B6" or "B8") NegControlWorst = Math.Max(NegControlWorst, At(20));
            if (g.Key == "Bp") BpRecallAt20 = At(20);
        }

        _out.WriteLine("");
        _out.WriteLine($"overall recall@5: {(double)scored.Count(s => s.Rank is >= 1 and <= 5) / scored.Count:P0}");

        // ── the only assertions: anti-vacuity + negative controls ─────────
        // The scorer must not credit recovery on width-closed or random-string
        // cases. If it does, the gap detector is inventing gaps and every other
        // number here is suspect.
        // Tight on purpose: the real engine sits at EXACTLY 0.0 on these bands,
        // so the headroom is for corpus jitter, not for tolerating fabrication.
        // A phantom name-width gap injected into DetectGaps lifts B8 to 12.5%
        // (1 of 8 answers matched) -- which this catches and 0.25 did not.
        NegControlWorst.Should().BeLessThan(0.10,
            "width-closed (B8) and random-string (B6) bands must stay near zero recall; " +
            "a high number means the engine is fabricating candidates, not recovering them");

        // #1140: single-anchor gaps (line-start/line-end) recovered at 0% before
        // the redaction-box channel — the two-glyph-gulf detector cannot see a
        // gap with no glyph on one side. The box width leaks it. This floor is
        // the anti-regression pin: it drops back to 0 the moment box detection
        // is disabled (verified), while the neg-control assertion above proves
        // the box channel did not fabricate its way to that number.
        BpRecallAt20.Should().BeGreaterThan(0.10,
            "single-anchor (Bp) recall must stay above 0 — the #1140 box channel; " +
            "if this is 0 the box gap detector stopped finding line-edge redactions");
    }
}
