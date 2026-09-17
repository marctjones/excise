using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Excise.TestSupport;
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

        static string Class(string colour) => colour switch
        {
            "black-on-white" or "low-contrast" => "occluded",
            "white-on-black" => "inverted-box",
            _ => "highlight",
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
            var stratum = Class(c.Colour);

            bool exciseGot;
            using (var doc = PdfDocument.Open(File.ReadAllBytes(path)))
                exciseGot = HiddenTextDetector.Scan(doc)
                    .Any(h => h.Text.Contains(c.Answer, StringComparison.OrdinalIgnoreCase));
            rows.Add(new Row("certain", stratum, "excise", exciseGot));

            if (!xrayAvailable) continue;
            var xr = XRayBadRedactionDetector.Inspect(path);
            if (xr == null) continue;
            rows.Add(new Row("certain", stratum, "xray",
                xr.Any(b => b.Text.Contains(c.Answer, StringComparison.OrdinalIgnoreCase))));
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
            bool Recovered(double tol) =>
                ResidueRecoveryEngine.Recover(pdf, dict,
                    new ResidueRecoveryEngine.Options(ExactTolerancePt: tol, RequireMutoolCorroboration: false))
                .Any(r => r.CandidatesFit.Contains(c.Answer, System.StringComparer.OrdinalIgnoreCase));
            rows.Add(new Row("residue", c.Band, "excise", Recovered(0.5)));
            rows.Add(new Row("residue", c.Band, "pixel-2pt", Recovered(2.0)));
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

    /// <summary>
    /// The CARRIER channel: one stratum per <see cref="CarrierTrapFixtures"/>
    /// trap, excise's certain channel against the strongest generic adversary
    /// that is not excise — every string and decoded stream in qpdf's object
    /// dump. excise must recover at least what the dump does on every stratum
    /// where recovery means text; where it leads (a superseded revision, a PDF
    /// inside an attachment) the dump cannot see the carrier at all.
    /// </summary>
    [Fact]
    public void CarrierTraps_ExciseCertainChannel_LeadsTheQpdfObjectDump()
    {
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf is the carrier-channel reference (brew install qpdf)");

        var dir = Path.Combine(Path.GetTempPath(), $"unredact-scorecard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var rows = new List<Row>();
        var presenceOnly = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var trap in CarrierTrapFixtures.All)
            {
                var bytes = trap.Build(false);
                var path = Path.Combine(dir, trap.Id + ".pdf");
                File.WriteAllBytes(path, bytes);

                bool exciseGot;
                using (var doc = PdfDocument.Open(bytes))
                    exciseGot = CarrierTextRecovery.Scan(doc, TestContext.Current.CancellationToken)
                        .Any(f => f.Kind == CarrierTextRecovery.CarrierFindingKind.Text
                                  && f.Text.Contains(trap.Token, StringComparison.Ordinal));
                var qpdfGot = CarrierTrapIndependentCorroborationTests.QpdfDump(path)
                    .Contains(trap.Token, StringComparison.Ordinal);

                if (trap.Oracle == CarrierTrapFixtures.Oracle.PresenceOnly) presenceOnly.Add(trap.Id);
                rows.Add(new Row("carrier", trap.Id, "excise", exciseGot));
                rows.Add(new Row("carrier", trap.Id, "qpdf-dump", qpdfGot));
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }

        var grades = UnredactionScorecard.Score(rows);
        _out.WriteLine(UnredactionScorecard.Render(grades, new UnredactionScorecard.Coverage(
            Channels: new[] { "carrier" },
            Tools: new[] { "excise", "qpdf-dump" },
            MissingReferences: new[]
            {
                $"presence-only strata ({string.Join(", ", presenceOnly.OrderBy(x => x))}): excise REPORTS the content " +
                "as present and does not decode it, so its text recall there is 0 by design",
            })));

        foreach (var (ch, st, ex, best, who) in UnredactionScorecard.ExciseVsBestReference(grades))
        {
            if (presenceOnly.Contains(st) || double.IsNaN(best)) continue;
            ex.Should().BeGreaterThanOrEqualTo(best, $"excise must recover at least what {who} does on {ch}/{st}");
            ex.Should().Be(100, $"every text trap is recoverable by construction ({ch}/{st})");
        }
    }
}
