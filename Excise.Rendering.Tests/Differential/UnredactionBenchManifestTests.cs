using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.TestSupport;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1591 — the committed half of the unredaction bench's data: ids, URLs,
/// hashes and the VETTING RECORD for every real-world document the bench may
/// touch. The PDFs are gitignored and the recovered values are nowhere.
///
/// <para>This gate exists because the manifest is the only thing standing
/// between "a corpus of redacted documents" and "a repository that republishes
/// what a court tried to withhold". It needs no corpus and no network: it reads
/// the tracked file and checks the properties that make that separation real —
/// the schema is CLOSED (no column can carry a value), every row states what
/// was actually assessed, and a row nobody has looked at cannot be mistaken for
/// one that was cleared.</para>
/// </summary>
public class UnredactionBenchManifestTests
{
    private const int Columns = 9;

    private sealed record Row(
        string Id, string Tier, string Status, string Url, string Sha256,
        string Source, string PublicBasis, string TruthUrl, string Vetting, int LineNumber);

    /// <summary>
    /// The TRACKED checkout this assembly was built from. Deliberately the
    /// local worktree, not the main checkout: everything this gate reads is in
    /// git, so it must be judged at the commit under test (#1527).
    /// </summary>
    private static string Root => TestRepoLayout.LocalCheckoutRoot
        ?? throw new InvalidOperationException("no checkout above " + AppContext.BaseDirectory);

    private static string ManifestPath() =>
        Path.Combine(Root, "tests", "unredaction-bench", "manifest.tsv");

    private static IReadOnlyList<string> AllLines() => File.ReadAllLines(ManifestPath());

    private static IReadOnlyList<Row> Rows()
    {
        var rows = new List<Row>();
        var lines = AllLines();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            var f = line.Split('\t');
            f.Length.Should().Be(Columns,
                $"manifest.tsv line {i + 1} must have exactly {Columns} tab-separated fields");
            rows.Add(new Row(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], i + 1));
        }
        return rows;
    }

    [Fact]
    public void TheManifestExistsAndParses()
    {
        File.Exists(ManifestPath()).Should().BeTrue(
            "the manifest is tracked in git even when no document has been vetted yet — " +
            "an empty vetted set is a finding, not a missing file");

        // The header comment is the contract a later reader gets. Losing it is
        // how the columns quietly acquire a tenth meaning.
        var header = string.Join("\n", AllLines().TakeWhile(l => l.StartsWith("#", StringComparison.Ordinal)));
        header.Should().Contain("#id\ttier\tstatus\turl\tsha256\tsource\tpublic_basis\ttruth_url\tvetting",
            "the column list must be stated in the file itself");
    }

    [Fact]
    public void TheSchemaIsClosed_SoNoColumnCanEverCarryARecoveredValue()
    {
        // #1602: the repository must never hold the text under a redaction. The
        // enforceable version of that rule is not "do not write values" — that
        // is unenforceable — but "there is no place to put one": nine columns,
        // fixed names, none of them a value.
        var header = AllLines().First(l => l.StartsWith("#id", StringComparison.Ordinal));
        var names = header.TrimStart('#').Split('\t');

        names.Should().Equal(new[]
        {
            "id", "tier", "status", "url", "sha256", "source", "public_basis", "truth_url", "vetting",
        }, "adding a column is how a value gets a home; do that deliberately and update this test");

        names.Should().NotContain(n =>
            n.Contains("value", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("answer", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("truth", StringComparison.OrdinalIgnoreCase) && !n.EndsWith("_url", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryRowIsWellFormed()
    {
        var rows = Rows();
        rows.Select(r => r.Id).Should().OnlyHaveUniqueItems("the id is also the local filename stem");

        foreach (var r in rows)
        {
            r.Id.Should().MatchRegex("^[a-z0-9][a-z0-9-]*$",
                $"line {r.LineNumber}: the id becomes a filename");
            r.Tier.Should().BeOneOf(new[] { "B", "C", "D" }, $"line {r.LineNumber}");
            r.Status.Should().BeOneOf(new[] { "vetted", "excluded", "blocked", "unvetted" }, $"line {r.LineNumber}");
            r.Source.Should().BeOneOf(new[] { "court", "agency", "foia", "sec", "press" }, $"line {r.LineNumber}");
            r.PublicBasis.Should().NotBeNullOrWhiteSpace($"line {r.LineNumber}");
        }
    }

    [Fact]
    public void EveryRowCarriesAnAssessment_NotJustAVerdict()
    {
        // The status word says what was decided; it does not say what was
        // checked. A later reader has to be able to re-judge the call, which
        // means the row has to record the reasoning — including, especially,
        // for rows that were REJECTED.
        foreach (var r in Rows())
        {
            r.Vetting.Should().NotBeNullOrWhiteSpace($"line {r.LineNumber}: every row states what was assessed");
            r.Vetting.Trim().Length.Should().BeGreaterThan(40,
                $"line {r.LineNumber} ({r.Id}): '{r.Vetting}' is a verdict, not an assessment");
        }
    }

    [Fact]
    public void AnUnvettedRowIsNeverMistakenForAClearedOne()
    {
        // #1591's comment: "we checked and it is fine" must not read the same as
        // "nobody has looked at this yet". The distinction only survives if an
        // unvetted row cannot carry the things a vetted one does.
        foreach (var r in Rows().Where(r => r.Status != "vetted"))
        {
            r.Sha256.Should().Be("-",
                $"line {r.LineNumber} ({r.Id}): a hash implies someone fetched and accepted these bytes");
            r.Vetting.Should().MatchRegex("(?i)(not yet|unvetted|nobody|pending|excluded|reject|do not|blocked)",
                $"line {r.LineNumber} ({r.Id}): a non-vetted row must say plainly which it is");
        }
    }

    [Fact]
    public void AVettedRowIsFullyGrounded()
    {
        foreach (var r in Rows().Where(r => r.Status == "vetted"))
        {
            r.Sha256.Should().MatchRegex("^[0-9a-f]{64}$",
                $"line {r.LineNumber} ({r.Id}): the download script verifies this and discards a mismatch");
            r.Url.Should().StartWith("https://", $"line {r.LineNumber} ({r.Id})");
            r.Url.Should().NotContain("pacer", $"line {r.LineNumber} ({r.Id}): no paid PACER pulls; RECAP mirrors only");

            if (r.Tier == "B")
            {
                r.TruthUrl.Should().StartWith("https://",
                    $"line {r.LineNumber} ({r.Id}): tier B means the value is ALREADY published — " +
                    "cite where, or it is tier C and cannot be scored");
            }
            else
            {
                r.TruthUrl.Should().Be("-",
                    $"line {r.LineNumber} ({r.Id}): only tier B has published ground truth");
            }
        }
    }

    [Fact]
    public void ABlockedRowNamesTheObstacle()
    {
        // `blocked` only earns its place if it says what is in the way. Without
        // that it is `unvetted` with a friendlier name, and the distinction the
        // file is built on collapses.
        foreach (var r in Rows().Where(r => r.Status == "blocked"))
        {
            r.Vetting.Should().MatchRegex("(?i)(403|user-agent|bot|wall|no durable|no stable|by hand|manual)",
                $"line {r.LineNumber} ({r.Id}): say what blocks the fetch, not merely that something does");
        }
    }

    /// <summary>
    /// The grounds an exclusion may rest on. This list is the taxonomy, and the
    /// manifest header states the same one — a gate that recognised a narrower
    /// set than the grounds that actually arise would push an author to pad the
    /// prose until the regex matched, which is worse than no gate.
    /// </summary>
    private const string ExclusionGrounds =
        "(?i)(victim|private individual|identif|minor" +           // who it would expose
        "|sealed|protective order|SSI|classified|controlled" +      // what it still is
        "|terms|robots" +                                           // how it may be fetched
        "|no verifiable|no stable|no reputable|no durable)";        // whether it can be sourced

    [Fact]
    public void AnExcludedRowSaysWhyAndIsNeverDownloadable()
    {
        foreach (var r in Rows().Where(r => r.Status == "excluded"))
        {
            r.Vetting.Should().MatchRegex(ExclusionGrounds,
                $"line {r.LineNumber} ({r.Id}): an exclusion names the ground it was excluded on");
            r.TruthUrl.Should().Be("-",
                $"line {r.LineNumber} ({r.Id}): an excluded document gets no ground-truth pointer either");
        }
    }

    [Fact]
    public void TheDownloadScriptAndTheRegistryAgreeWithTheManifest()
    {
        var root = Root;

        var script = Path.Combine(root, "scripts", "download-unredaction-bench.sh");
        File.Exists(script).Should().BeTrue();
        var body = File.ReadAllText(script);
        body.Should().Contain("tests/unredaction-bench/manifest.tsv", "the script is manifest-driven, not a URL list");
        body.Should().Contain("ground-truth.local.tsv", "ground truth stays in a gitignored local file");

        // The registry is what makes `corpus.sh verify` — a t0 gate — check that
        // the destination is gitignored. A standalone script would miss that.
        var registry = File.ReadAllLines(Path.Combine(root, "tests", "corpora.tsv"))
            .Where(l => !l.StartsWith("#", StringComparison.Ordinal))
            .Select(l => l.Split('\t'))
            .Where(f => f.Length >= 7)
            .ToList();

        var row = registry.SingleOrDefault(f => f[0] == "unredaction-bench");
        row.Should().NotBeNull("the bench corpus is registered in tests/corpora.tsv, not a parallel mechanism");
        row![2].Should().Be("test-pdfs/unredaction-bench");
        row[3].Should().Be("download-unredaction-bench.sh");
    }

    [Fact]
    public void TheBenchCorpusIsGitignored()
    {
        // corpus.sh verify checks this too, in t0. It is repeated here because
        // this is the one corpus where a committable destination would not be
        // an inconvenience but a disclosure.
        var probe = Path.Combine(Root, "test-pdfs", "unredaction-bench", "probe.pdf");
        GitChecksIgnored(probe).Should().BeTrue(
            "a redacted document fetched into a committable directory is one `git add -A` from being published");
    }

    private static bool GitChecksIgnored(string path)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("check-ignore");
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add(path);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode == 0;
    }
}
