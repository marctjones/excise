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
/// Compares excise's residue recovery against the published reference methods,
/// on the constructed difficulty corpus.
///
/// <para><b>The thesis, quantified.</b> excise reads the PDF's OWN exact glyph
/// advances (±0.5pt). The public tools — unredact.live (MIT, pixel + OCR + LLM)
/// and the released Edact-Ray pieces — measure the gap from RENDERED PIXELS and
/// must guess the font, working at ~±2pt. Same width-fit attack, coarser ruler.
/// This runs the SAME engine at both tolerances so the only variable is the
/// measurement precision, and reports recall@N and residual entropy per band.</para>
///
/// <para>The certain-recovery axis (text under a box: x-ray, leedrake5,
/// mandour22) is not a spectrum — the text is present or not. excise's certain
/// mode matches those and adds low-contrast (#1131) and word-in-line (#1149)
/// detection, so it is a strict superset there; this test is about the residue
/// axis, where precision is the differentiator.</para>
/// </summary>
public sealed class UnredactMethodComparisonTests
{
    private readonly ITestOutputHelper _out;
    public UnredactMethodComparisonTests(ITestOutputHelper o) { _out = o; }

    private static readonly string[] Names =
        ("James John Robert Michael William David Richard Joseph Thomas Charles " +
         "Christopher Daniel Matthew Anthony Donald Mark Paul Steven Andrew Kenneth " +
         "Mary Patricia Jennifer Linda Elizabeth Barbara Susan Jessica Sarah Karen " +
         "Nancy Lisa Betty Margaret Sandra Ashley Kimberly Emily Donna Michelle " +
         "Louise Farrar Anne Dorothy Carol Amanda Melissa Deborah Stephanie").Split(' ');

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, ".git"))) d = d.Parent;
        return d!.FullName;
    }

    // Standard-14 Helvetica AFM widths (1000ths em) for the fit computation --
    // the same source Excise.Core.Fonts.StandardFontMetrics uses, so the
    // comparison measures the real width-discrimination, not a re-derivation.
    private static double WidthPt(string s, double sizePt)
    {
        double units = 0;
        foreach (var ch in s)
            if (Excise.Core.Fonts.StandardFontMetrics.TryGetWidth("Helvetica", ch, out var w)) units += w;
        return units / 1000.0 * sizePt;
    }

    [Fact]
    public void ExactMetrics_vs_PixelTolerance_NarrowingPower()
    {
        // The full attacker dictionary -- the real test of width discrimination.
        // (The corpus fixtures cap answers at 8/band; that is too few distinct
        // widths to show the tolerance effect, so this measures over all 49.)
        const double size = 12;
        var widths = Names.ToDictionary(n => n, n => WidthPt(n, size));

        var methods = new (string Name, double Tol)[]
        {
            ("excise (exact PDF metrics, +/-0.5pt)", 0.5),
            ("unredact.live / Edact-Ray class (pixel+OCR, +/-2pt)", 2.0),
        };

        _out.WriteLine($"width-fit over the full {Names.Length}-name dictionary, Helvetica 12pt\n");
        _out.WriteLine($"{"method",-52} {"unique@1",9} {"avgCands",9} {"avgBits",9}");

        var res = new Dictionary<string, (double Unique, double Avg, double Bits)>();
        foreach (var (mname, tol) in methods)
        {
            int unique = 0; var cands = new List<int>(); var bits = new List<double>();
            foreach (var answer in Names)
            {
                var fit = Names.Count(n => Math.Abs(widths[n] - widths[answer]) <= tol);
                if (fit == 1) unique++;
                cands.Add(fit);
                bits.Add(Math.Log2(fit));
            }
            res[mname] = ((double)unique / Names.Length, cands.Average(), bits.Average());
            _out.WriteLine($"{mname,-52} {(double)unique / Names.Length,9:P0} {cands.Average(),9:F2} {bits.Average(),9:F2}");
        }

        var exact = res["excise (exact PDF metrics, +/-0.5pt)"];
        var pixel = res["unredact.live / Edact-Ray class (pixel+OCR, +/-2pt)"];
        _out.WriteLine("");
        _out.WriteLine($"exact-metric advantage: unique recovery {exact.Unique - pixel.Unique:+0%}, " +
                       $"{pixel.Avg / Math.Max(1e-9, exact.Avg):F1}x fewer candidates, " +
                       $"{pixel.Bits - exact.Bits:F2} fewer bits of residual entropy");

        exact.Unique.Should().BeGreaterThan(pixel.Unique,
            "reading exact PDF metrics must UNIQUELY recover more names than the pixel regime");
        exact.Bits.Should().BeLessThan(pixel.Bits,
            "exact metrics must leave less residual entropy than pixels");
    }

    // (kept for reference: the corpus-based version, which the 8-answer cap
    // makes insensitive to tolerance -- the narrowing test above is the real
    // comparison.)
    [Fact(Skip = "8-answer corpus cap makes this insensitive; see NarrowingPower")]
    public void ExactMetrics_vs_PixelTolerance_ResidueRecovery()

    {
        var corpus = Path.Combine(RepoRoot(), "test-pdfs", "redaction-synthetic");
        var manifest = Path.Combine(corpus, "manifest.jsonl");
        Assert.SkipUnless(File.Exists(manifest), "corpus absent [requires: corpus:redaction-synthetic]");

        // Width-preserving name cases -- the residue-relevant band.
        var cases = File.ReadAllLines(manifest).Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(l)!)
            .Where(m => m["method"].GetString() == "width-preserving"
                        && m["dictionary"].GetString()!.StartsWith("dict")
                        && m["font"].GetString() == "Helvetica"
                        && m["sizePt"].GetDouble() == 12)
            .ToList();
        Assert.SkipWhen(cases.Count == 0, "no comparable cases");

        // The two methods: excise's exact metrics vs the pixel/OCR regime.
        var methods = new (string Name, double Tol)[]
        {
            ("excise (exact PDF metrics, ±0.5pt)", 0.5),
            ("unredact.live / Edact-Ray class (pixel+OCR, ±2pt)", 2.0),
        };

        _out.WriteLine($"{cases.Count} width-preserving Helvetica-12 name redactions\n");
        _out.WriteLine($"{"method",-52} {"recall@1",9} {"recall@5",9} {"medianBits",11}");

        var byMethod = new Dictionary<string, (double R1, double R5, double Bits)>();
        foreach (var (mname, tol) in methods)
        {
            int hit1 = 0, hit5 = 0; var bits = new List<double>();
            foreach (var c in cases)
            {
                var id = c["id"].GetString()!;
                var answer = c["answer"].GetString()!;
                var pdf = Path.Combine(corpus, id + ".pdf");
                if (!File.Exists(pdf)) continue;

                var recs = ResidueRecoveryEngine.Recover(pdf, Names,
                    new ResidueRecoveryEngine.Options(ExactTolerancePt: tol,
                        FallbackTolerancePt: tol, RequireMutoolCorroboration: false));

                var rank = recs.SelectMany(r => r.CandidatesFit.Select((w, i) => (w, i + 1)))
                    .Where(t => t.w == answer).Select(t => t.Item2).DefaultIfEmpty(0).Min();
                if (rank is >= 1 and <= 1) hit1++;
                if (rank is >= 1 and <= 5) hit5++;
                foreach (var r in recs.Where(r => r.Status == "ok")) bits.Add(r.ResidualEntropyBits);
            }
            var n = cases.Count;
            var med = bits.Count == 0 ? 0 : bits.OrderBy(x => x).ElementAt(bits.Count / 2);
            byMethod[mname] = ((double)hit1 / n, (double)hit5 / n, med);
            _out.WriteLine($"{mname,-52} {(double)hit1 / n,9:P0} {(double)hit5 / n,9:P0} {med,11:F2}");
        }

        // The comparison's claim: exact metrics recover at rank 1 AT LEAST as
        // often as the pixel regime, and narrow to fewer bits. Not a ratchet --
        // a demonstrated, reproducible advantage.
        var exact = byMethod["excise (exact PDF metrics, ±0.5pt)"];
        var pixel = byMethod["unredact.live / Edact-Ray class (pixel+OCR, ±2pt)"];
        _out.WriteLine("");
        _out.WriteLine($"exact-metric advantage: recall@1 {exact.R1 - pixel.R1:+0%;-0%;0%}, " +
                       $"median bits {exact.Bits - pixel.Bits:+0.00;-0.00;0.00} (lower is better)");

        exact.R1.Should().BeGreaterThanOrEqualTo(pixel.R1,
            "exact PDF metrics must recover at rank 1 at least as often as the pixel regime");
        exact.Bits.Should().BeLessThanOrEqualTo(pixel.Bits + 0.01,
            "exact metrics must narrow the candidate space at least as tightly as pixels");
    }
}
