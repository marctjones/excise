using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Redaction.Recovery;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Xunit;
using Row = Excise.Rendering.Tests.Differential.UnredactionScorecard.Row;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1181 — the consolidated unredaction scorecard. A unit test pins the grading
/// logic (never one number, per-stratum, excise-vs-best-reference); an
/// integration driver populates the CERTAIN channel from the real x-ray
/// reference and prints the scorecard. Residue and tool-resistance are measured
/// by their own tests (ResidueRecoveryRecallTests, ToolResistanceComparisonTests)
/// and recorded in Coverage as not-yet-aggregated here, so the scorecard cannot
/// read as covering more than it does.
/// </summary>
public class UnredactionScorecardTests
{
    private readonly Xunit.ITestOutputHelper _out;
    public UnredactionScorecardTests(Xunit.ITestOutputHelper o) => _out = o;

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, ".git")) && !File.Exists(Path.Combine(d.FullName, ".git"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException(
            "repository root not found: no .git directory or worktree .git file above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Score_GradesPerChannelStratumTool_AndComputesExciseAdvantage()
    {
        var rows = new[]
        {
            new Row("certain", "occluded", "excise", true),
            new Row("certain", "occluded", "excise", true),
            new Row("certain", "occluded", "xray", true),
            new Row("certain", "occluded", "xray", false),
            new Row("certain", "inverted-box", "excise", true),
            new Row("certain", "inverted-box", "xray", false),
        };

        var grades = UnredactionScorecard.Score(rows);

        grades.Single(g => g.Stratum == "occluded" && g.Tool == "excise").RecallPct.Should().Be(100);
        grades.Single(g => g.Stratum == "occluded" && g.Tool == "xray").RecallPct.Should().Be(50);

        var adv = UnredactionScorecard.ExciseVsBestReference(grades);
        adv.Single(a => a.Stratum == "occluded").Should()
            .Match<(string, string, double, double, string)>(a => a.Item3 == 100 && a.Item4 == 50);
        adv.Single(a => a.Stratum == "inverted-box").Should()
            .Match<(string, string, double, double, string)>(a => a.Item3 == 100 && a.Item4 == 0,
                "#1180: excise surfaces the inverted-box class the x-ray reference misses");
    }

    [Fact]
    public void ANegativeControlStratum_ScoresSpecificity_SoSilenceIsNotAMiss()
    {
        // #1616: `highlight-readable` is a yellow highlight over text that stays
        // readable — nothing is redacted there. Recovering nothing is the right
        // answer; recall would have scored it 0%, which is how correct behaviour
        // came to be printed as total failure.
        var quiet = UnredactionScorecard.Score(new[]
        {
            new Row("certain", "highlight-readable", "excise", false,
                Polarity: UnredactionScorecard.Polarity.NegativeControl),
            new Row("certain", "highlight-readable", "excise", false,
                Polarity: UnredactionScorecard.Polarity.NegativeControl),
        }).Single();

        quiet.RecallPct.Should().Be(0, "nothing was recovered");
        quiet.ScorePct.Should().Be(100, "and on a negative control that is the correct answer");
        quiet.Metric.Should().Be("specificity");

        // A false positive on the control must move the number DOWN, or the row
        // cannot report a regression either.
        var noisy = UnredactionScorecard.Score(new[]
        {
            new Row("certain", "highlight-readable", "excise", true,
                Polarity: UnredactionScorecard.Polarity.NegativeControl),
            new Row("certain", "highlight-readable", "excise", false,
                Polarity: UnredactionScorecard.Polarity.NegativeControl),
        }).Single();

        noisy.ScorePct.Should().Be(50, "one of the two attempts recovered text that was never hidden");
        UnredactionScorecard.Render(new[] { noisy },
            new UnredactionScorecard.Coverage(new[] { "certain" }, new[] { "excise" }, Array.Empty<string>()))
            .Should().Contain("negative control")
            .And.NotContain("recall 1/2", "a control row must not be rendered as recall");
    }

    [Fact]
    public void NoReferenceForAStratum_IsReportedNotCountedAsAWin()
    {
        var grades = UnredactionScorecard.Score(new[] { new Row("residue", "B1", "excise", true) });
        var adv = UnredactionScorecard.ExciseVsBestReference(grades).Single();
        double.IsNaN(adv.BestRefPct).Should().BeTrue("a stratum with no reference tool must not read as a win");
    }

    [Fact]
    public void ConsolidatedScorecard_CertainAndResidue_ExciseLeadsTheReferences()
    {
        var corpus = Path.Combine(RepoRoot(), "test-pdfs", "redaction-synthetic");
        var manifest = Path.Combine(corpus, "manifest.jsonl");
        Assert.SkipUnless(File.Exists(manifest),
            "constructed corpus absent [requires: corpus:redaction-synthetic]");

        // The generator builds `highlight-readable` as a NEGATIVE CONTROL — a
        // yellow highlight over text that stays readable is not a redaction, so
        // recovering nothing there is correct and recovering something is a
        // false positive. Scored as recall it read 0/8, i.e. correct behaviour
        // printed as total failure (#1616).
        static (string Stratum, UnredactionScorecard.Polarity Polarity) Class(string colour) => colour switch
        {
            "black-on-white" or "low-contrast" => ("occluded", UnredactionScorecard.Polarity.Leak),
            "white-on-black" => ("inverted-box", UnredactionScorecard.Polarity.Leak),
            "highlight-readable" => ("highlight-readable", UnredactionScorecard.Polarity.NegativeControl),
            // #1617: the bar drawn with no colour operator, relying on §8.6.8's
            // initial black. Its own stratum because it is the one band here
            // that was NOT written the way a fixture author writes one, and it
            // is the shape that found total blindness on a real filing.
            "default-colour" => ("default-colour-box", UnredactionScorecard.Polarity.Leak),
            _ => ("highlight", UnredactionScorecard.Polarity.Leak),
        };

        var xrayAvailable = XRayBadRedactionDetector.IsAvailable;
        var rows = new List<Row>();

        var cases = File.ReadAllLines(manifest).Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(l)!)
            .Where(m => m["method"].GetString() == "under-box")
            .Select(m => (Id: m["id"].GetString()!, Answer: m["answer"].GetString()!, Colour: m["colour"].GetString()!))
            .Where(c => File.Exists(Path.Combine(corpus, c.Id + ".pdf")))
            .ToList();

        foreach (var c in cases)
        {
            var path = Path.Combine(corpus, c.Id + ".pdf");
            var (stratum, polarity) = Class(c.Colour);

            // Score what `excise unredact` ACTUALLY does — the whole recovery
            // scan — not one of its channels. Probing HiddenTextDetector alone
            // scored inverted-box at 0/8 while the shipped command recovers it
            // as Certain (#1616): the tool understating itself, which buries a
            // future regression under a number that was already zero.
            bool exciseGot;
            using (var doc = PdfDocument.Open(File.ReadAllBytes(path)))
                exciseGot = RecoveryScanner.Scan(doc, TestContext.Current.CancellationToken).AllFindings
                    .Any(f => f.Text != null && f.Text.Contains(c.Answer, StringComparison.OrdinalIgnoreCase));
            rows.Add(new Row("certain", stratum, "excise", exciseGot, Polarity: polarity));

            if (!xrayAvailable) continue;
            var xr = XRayBadRedactionDetector.Inspect(path);
            if (xr == null) continue;
            rows.Add(new Row("certain", stratum, "xray",
                xr.Any(b => b.Text.Contains(c.Answer, StringComparison.OrdinalIgnoreCase)),
                Polarity: polarity));
        }

        // #1181: RESIDUE channel — excise's exact PDF metrics (±0.5pt) vs the
        // pixel/OCR reference class (±2pt, the precision unredact.live/Edact-Ray
        // achieve), scored on the width-preserving cases against the SAME closed
        // dictionary the answer was drawn from. Capped per band for runtime; the
        // full sweep is ResidueRecoveryRecallTests.
        var residueCases = File.ReadAllLines(manifest).Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(l)!)
            .Where(m => m["method"].GetString() == "width-preserving")
            .Select(m => (Id: m["id"].GetString()!, Answer: m["answer"].GetString()!,
                          Band: m["band"].GetString()!, Dict: m["dictionary"].GetString()!))
            .Where(c => File.Exists(Path.Combine(corpus, c.Id + ".pdf")))
            .GroupBy(c => c.Band).SelectMany(g => g.Take(4)).ToList();

        foreach (var c in residueCases)
        {
            var pdf = Path.Combine(corpus, c.Id + ".pdf");
            var dict = SyntheticCorpusDictionaries.For(c.Dict);

            // `random` is the residue channel's NEGATIVE CONTROL: the answer is
            // a random string and the dictionary searched is deliberately the
            // names list it is structurally absent from (see
            // SyntheticCorpusDictionaries.For). Listing it would mean the width
            // fit invented a match, so silence is the win and recall is the
            // wrong metric — it read 0/4 for both tools and looked like a gap
            // in the width channel (#1616).
            var polarity = c.Dict == "random"
                ? UnredactionScorecard.Polarity.NegativeControl
                : UnredactionScorecard.Polarity.Leak;

            bool Recovered(double tol) =>
                ResidueRecoveryEngine.Recover(pdf, dict,
                    new ResidueRecoveryEngine.Options(ExactTolerancePt: tol, RequireMutoolCorroboration: false))
                .Any(r => r.CandidatesFit.Contains(c.Answer, System.StringComparer.OrdinalIgnoreCase));
            rows.Add(new Row("residue", c.Band, "excise", Recovered(0.5), Polarity: polarity));
            rows.Add(new Row("residue", c.Band, "pixel-2pt", Recovered(2.0), Polarity: polarity));
        }

        var grades = UnredactionScorecard.Score(rows);
        var missing = new List<string> { "tool-resistance → ToolResistanceComparisonTests" };
        if (!xrayAvailable) missing.Add("x-ray (certain reference) not installed");
        var coverage = new UnredactionScorecard.Coverage(
            Channels: new[] { "certain", "residue" },
            Tools: rows.Select(r => r.Tool).Distinct().OrderBy(t => t).ToList(),
            MissingReferences: missing);

        _out.WriteLine(UnredactionScorecard.Render(grades, coverage));

        // excise must lead (or tie) the real reference on every certain stratum
        // where the reference ran.
        if (xrayAvailable)
            foreach (var (ch, st, ex, best, _) in UnredactionScorecard.ExciseVsBestReference(grades))
                if (!double.IsNaN(best))
                    ex.Should().BeGreaterThanOrEqualTo(best,
                        $"excise must recover at least what x-ray does on {ch}/{st}");
    }
}
