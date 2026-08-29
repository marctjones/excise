using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.Avalonia.Tests;

/// <summary>
/// Reliability harness for the copy/whitespace path (paragraph + list fidelity).
/// Runs the REAL selection path — <see cref="TextSelectionEngine.SortReadingOrder(System.Collections.Generic.IEnumerable{Letter}, ReadingOrderStrategy)"/>
/// then <see cref="TextSelectionEngine.JoinText(System.Collections.Generic.IReadOnlyList{Letter}, WhitespaceMode)"/>
/// — over real corpus PDFs and compares to an INDEPENDENT oracle (poppler
/// <c>pdftotext</c>). It runs whenever those independent prerequisites are
/// available, and skips explicitly when <c>pdftotext</c> or the corpus is
/// absent. Reproduce with <c>scripts/copy-whitespace-parity.sh</c>.
///
/// <para>
/// The oracle split is deliberate (see docs/copy-whitespace-reliability.md):
/// pdftotext is a valid oracle ONLY for word-spacing and line-breaks — the two
/// dimensions where excise does not intend to diverge. It is NOT the oracle for
/// paragraph blank-lines or list indentation (pdftotext emits neither); those
/// are graded by the construction-known synthetic fixtures in
/// <c>Excise.App.Tests/Unit/CopyWhitespaceModeTests.cs</c>. Here we measure only
/// word-token and line-segmentation agreement, order-insensitively (Jaccard) so
/// reading-order differences don't masquerade as spacing errors.
/// </para>
/// </summary>
public class CopyWhitespaceParityHarness
{
    private readonly ITestOutputHelper _output;
    public CopyWhitespaceParityHarness(ITestOutputHelper output) => _output = output;

    // Corpus files (relative to test-pdfs/) and the 1-based page range sampled.
    private static readonly (string Path, int First, int Last, string Kind)[] Corpus =
    {
        ("local-real-world/producingoss.pdf", 20, 27, "multi-paragraph prose"),
        ("local-real-world/foss-primer.pdf", 5, 12, "prose + headings"),
        ("federal/scotus-trump-v-us.pdf", 3, 10, "legal prose"),
        ("federal/irs-pub509-2026.pdf", 3, 8, "instructions + lists"),
        ("federal/cdc-vis-covid-19.pdf", 1, 2, "bulleted health notice"),
        ("federal/state-ds82-passport-renewal.pdf", 1, 6, "passport application form"),
    };

    [Fact]
    public void MeasureParity_AgainstPdftotext()
    {
        if (!PdftotextAvailable())
        {
            Assert.Skip("pdftotext (poppler) not found on PATH.");
            return;
        }
        var testPdfs = FindTestPdfsDir();
        if (testPdfs == null)
        {
            Assert.Skip("test-pdfs/ corpus not found; run scripts/download-test-pdfs.sh.");
            return;
        }

        var rows = new List<(string File, string Kind, int Pages, double WordJaccard, double LineJaccard, double SeqAgreement)>();
        double wSum = 0, lSum = 0, sSum = 0; int pageTotal = 0;

        foreach (var (rel, first, last, kind) in Corpus)
        {
            var path = Path.Combine(testPdfs, rel);
            if (!File.Exists(path)) { _output.WriteLine($"SKIP missing {rel}"); continue; }

            double wAcc = 0, lAcc = 0, sAcc = 0; int n = 0;
            using var doc = PdfDocument.Open(path);
            for (int p = first; p <= Math.Min(last, doc.PageCount); p++)
            {
                List<Letter> letters;
                try { letters = doc.GetPage(p).Letters?.ToList() ?? new List<Letter>(); }
                catch { continue; }
                if (letters.Count == 0) continue;

                var reading = TextSelectionEngine.SortReadingOrder(letters, ReadingOrderStrategy.ColumnAware);
                var excise = TextSelectionEngine.JoinText(reading, WhitespaceMode.Smart);
                var oracle = RunPdftotext(path, p);
                if (string.IsNullOrWhiteSpace(oracle)) continue;

                if (Environment.GetEnvironmentVariable("COPY_WHITESPACE_DEBUG") == "1" && p == first)
                {
                    _output.WriteLine($"===== {rel} p{p} EXCISE =====");
                    _output.WriteLine(excise.Length > 400 ? excise.Substring(0, 400) : excise);
                    _output.WriteLine($"===== {rel} p{p} ORACLE =====");
                    _output.WriteLine(oracle.Length > 400 ? oracle.Substring(0, 400) : oracle);
                }
                wAcc += WordJaccard(excise, oracle);
                lAcc += LineJaccard(excise, oracle);
                sAcc += SeqAgreement(excise, oracle);
                n++;
            }
            if (n == 0) continue;
            rows.Add((Path.GetFileName(rel), kind, n, wAcc / n, lAcc / n, sAcc / n));
            wSum += wAcc; lSum += lAcc; sSum += sAcc; pageTotal += n;
        }

        var sb = new StringBuilder();
        sb.AppendLine("| File | Kind | Pages | Word-token agreement | Line-break agreement | Sequence-order agreement |");
        sb.AppendLine("|------|------|------:|---------------------:|---------------------:|-------------------------:|");
        foreach (var r in rows)
            sb.AppendLine($"| {r.File} | {r.Kind} | {r.Pages} | {r.WordJaccard:P1} | {r.LineJaccard:P1} | {r.SeqAgreement:P1} |");
        if (pageTotal > 0)
            sb.AppendLine($"| **AGGREGATE** | — | {pageTotal} | **{wSum / pageTotal:P1}** | **{lSum / pageTotal:P1}** | **{sSum / pageTotal:P1}** |");

        var report = sb.ToString();
        _output.WriteLine(report);

        // #837: ratcheting floor gate — fail when a document's word/line
        // agreement drops below its checked-in floor. Regenerate the floors with
        // COPY_WHITESPACE_PARITY_UPDATE=1 (an intentional improvement elsewhere).
        CheckOrUpdateFloors(rows);

        // Persist a machine-generated fragment so the checked-in doc's numbers
        // are reproducible rather than hand-copied.
        //
        // #1013: this used to write into the TRACKED tree on every run, and the
        // timestamp line changes even when the numbers do not — so the working
        // tree was dirty the moment the full suite finished. run-full-suite.sh
        // --assert-green then refuses ("evidence must be a full-suite run at a
        // clean, committed HEAD"), and committing the file moves HEAD, which
        // invalidates the sha-keyed markers and forces the whole ~60-90 minute
        // suite to re-run — which dirties the tree again. An evidence run must
        // not mutate the tree it is evidence for.
        //
        // So the tracked copy is only rewritten under the same
        // COPY_WHITESPACE_PARITY_UPDATE=1 flag that already regenerates the
        // floors; otherwise the report goes beside the test binary, where it is
        // still available for reading and diffing.
        var body =
            "<!-- generated by scripts/copy-whitespace-parity.sh — do not edit by hand -->\n" +
            $"<!-- {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC, poppler oracle -->\n\n" + report;

        if (Environment.GetEnvironmentVariable("COPY_WHITESPACE_PARITY_UPDATE") == "1")
        {
            var outDir = Path.Combine(FindRepoRoot(), "tests", "copy-whitespace");
            Directory.CreateDirectory(outDir);
            var tracked = Path.Combine(outDir, "parity-results.md");
            File.WriteAllText(tracked, body);
            _output.WriteLine($"Updated tracked report: {tracked}");
        }
        else
        {
            var scratch = Path.Combine(AppContext.BaseDirectory, "parity-results.md");
            File.WriteAllText(scratch, body);
            _output.WriteLine($"Report written to {scratch} (tracked copy unchanged; " +
                              "re-run with COPY_WHITESPACE_PARITY_UPDATE=1 to refresh it)");
        }

        Assert.True(pageTotal > 0, "no corpus pages were measured");
    }

    /// <summary>
    /// Ratcheting floor gate (#837). Reads per-document word/line agreement
    /// floors from <c>tests/copy-whitespace/floors.json</c> and fails if any
    /// measured score falls below its floor. With
    /// <c>COPY_WHITESPACE_PARITY_UPDATE=1</c> it rewrites the floors from the
    /// current measurement (each floor = measured − 3-point noise margin), the
    /// same posture as <c>scripts/check-extraction-parity.sh --update</c>.
    /// </summary>
    private void CheckOrUpdateFloors(
        System.Collections.Generic.List<(string File, string Kind, int Pages, double WordJaccard, double LineJaccard, double SeqAgreement)> rows)
    {
        const double margin = 0.03;
        var floorsPath = Path.Combine(FindRepoRoot(), "tests", "copy-whitespace", "floors.json");

        if (Environment.GetEnvironmentVariable("COPY_WHITESPACE_PARITY_UPDATE") == "1")
        {
            var entries = rows.Select(r =>
                $"  \"{r.File}\": {{ \"word\": {System.Math.Max(0, r.WordJaccard - margin):F3}, \"line\": {System.Math.Max(0, r.LineJaccard - margin):F3}, \"seq\": {System.Math.Max(0, r.SeqAgreement - margin):F3} }}");
            File.WriteAllText(floorsPath,
                "// generated by COPY_WHITESPACE_PARITY_UPDATE=1 — per-doc parity floors (#837)\n{\n"
                + string.Join(",\n", entries) + "\n}\n");
            _output.WriteLine($"Updated parity floors: {floorsPath}");
            return;
        }

        if (!File.Exists(floorsPath))
        {
            _output.WriteLine("No floors.json yet — run COPY_WHITESPACE_PARITY_UPDATE=1 to create it.");
            return;
        }

        // Tolerant of the leading `//` comment line the updater writes.
        var raw = File.ReadAllText(floorsPath);
        var jsonStart = raw.IndexOf('{');
        using var doc = System.Text.Json.JsonDocument.Parse(raw.Substring(jsonStart));
        var failures = new System.Collections.Generic.List<string>();
        foreach (var r in rows)
        {
            if (!doc.RootElement.TryGetProperty(r.File, out var f)) continue;
            var wFloor = f.GetProperty("word").GetDouble();
            var lFloor = f.GetProperty("line").GetDouble();
            if (r.WordJaccard < wFloor - 1e-9) failures.Add($"{r.File} word {r.WordJaccard:P1} < floor {wFloor:P1}");
            if (r.LineJaccard < lFloor - 1e-9) failures.Add($"{r.File} line {r.LineJaccard:P1} < floor {lFloor:P1}");
            // #838: sequence-order floor. Absent on floors written before the
            // seq metric existed, so gate only when present (a re-run with
            // COPY_WHITESPACE_PARITY_UPDATE=1 adds it).
            if (f.TryGetProperty("seq", out var sEl))
            {
                var sFloor = sEl.GetDouble();
                if (r.SeqAgreement < sFloor - 1e-9) failures.Add($"{r.File} seq {r.SeqAgreement:P1} < floor {sFloor:P1}");
            }
        }

        Assert.True(failures.Count == 0,
            "copy-whitespace parity regressed below tests/copy-whitespace/floors.json. If this is an "
            + "intentional trade-off, re-run with COPY_WHITESPACE_PARITY_UPDATE=1. Regressions: "
            + string.Join("; ", failures));
    }

    // ── metric self-tests (#838) ───────────────────────────────────────────────
    // Construction-known, corpus-free: prove the order-SENSITIVE metric catches
    // exactly the reading-order regression the order-INSENSITIVE Jaccard is blind
    // to. These run in the normal suite (no COPY_WHITESPACE_PARITY gate).

    [Fact]
    public void SeqAgreement_FallsWhenReadingOrderIsScrambled_WhileJaccardDoesNot()
    {
        var oracle = "the quick brown fox jumps over the lazy dog";
        // Same MULTISET of tokens, reading order badly scrambled (as a
        // two-column interleave would produce).
        var scrambled = "quick the fox brown over jumps lazy the dog";

        WordJaccard(scrambled, oracle).Should().Be(1.0,
            "the token multiset is identical — Jaccard cannot see the reordering (the #838 blind spot)");

        SeqAgreement(scrambled, oracle).Should().BeLessThan(0.75,
            "the order-sensitive metric must drop when the same tokens are reordered");
        SeqAgreement(oracle, oracle).Should().Be(1.0, "identical order scores 1");
    }

    [Fact]
    public void SeqAgreement_IgnoresIntentionalParagraphBlankLines()
    {
        var oracle = "alpha beta gamma delta";
        var exciseWithParaBreaks = "alpha beta\n\ngamma delta"; // Smart-mode paragraph break
        SeqAgreement(exciseWithParaBreaks, oracle).Should().Be(1.0,
            "newlines fold to spaces before scoring — an intentional paragraph break is not a reorder");
    }

    // ── metrics ──────────────────────────────────────────────────────────────

    /// <summary>Order-insensitive word-token overlap. Newlines fold to spaces
    /// (excise's paragraph blank lines are intentional divergence, not error),
    /// so a DROPPED word space — which would fuse two tokens into one and shrink
    /// the overlap — is what this penalises. Oracle = pdftotext.</summary>
    private static double WordJaccard(string excise, string oracle)
        => MultisetJaccard(Tokens(excise), Tokens(oracle));

    /// <summary>Order-insensitive line agreement. excise's <c>\n\n</c> collapses
    /// to <c>\n</c> first so an added paragraph break is not counted against
    /// line fidelity; what remains measures whether excise segments lines the
    /// way pdftotext does.</summary>
    private static double LineJaccard(string excise, string oracle)
    {
        var e = excise.Replace("\n\n", "\n");
        return MultisetJaccard(Lines(e), Lines(oracle));
    }

    /// <summary>
    /// Order-SENSITIVE token agreement (#838). The Jaccard metrics above are
    /// multiset overlap: reordering the very same tokens leaves them unchanged,
    /// so a reading-ORDER regression (multi-column scramble, header/footer
    /// interleave) is invisible to them — proven by #774/#824 not moving the
    /// Jaccard numbers at all. This measures the longest common SUBSEQUENCE of
    /// excise's token stream against pdftotext's, normalised by the longer
    /// stream: <c>LCS / max(|a|,|b|)</c>. Scrambling order shortens the common
    /// subsequence and drops the score; getting order closer to the oracle
    /// raises it. Newlines fold to spaces first (like WordJaccard) so excise's
    /// intentional paragraph blank lines are not penalised.
    /// </summary>
    private static double SeqAgreement(string excise, string oracle)
    {
        var a = Tokens(excise);
        var b = Tokens(oracle);
        if (a.Count == 0 && b.Count == 0) return 1.0;
        if (a.Count == 0 || b.Count == 0) return 0.0;
        var lcs = Lcs(a, b);
        return (double)lcs / Math.Max(a.Count, b.Count);
    }

    /// <summary>Longest common subsequence length, rolling-array DP (O(|a|·|b|)
    /// time, O(min) space). Corpus pages are a few hundred tokens, well within
    /// budget.</summary>
    private static int Lcs(List<string> a, List<string> b)
    {
        // Keep the inner (rolling) dimension the smaller one.
        if (b.Count > a.Count) (a, b) = (b, a);
        var prev = new int[b.Count + 1];
        var cur = new int[b.Count + 1];
        for (int i = 1; i <= a.Count; i++)
        {
            for (int j = 1; j <= b.Count; j++)
                cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
            (prev, cur) = (cur, prev);
            Array.Clear(cur, 0, cur.Length);
        }
        return prev[b.Count];
    }

    private static List<string> Tokens(string s)
        => s.Split(new[] { ' ', '\t', '\n', '\r', '\f' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize).Where(t => t.Length > 0).ToList();

    private static List<string> Lines(string s)
        => s.Split('\n').Select(l => Normalize(l.Replace(" ", ""))).Where(l => l.Length > 0).ToList();

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static double MultisetJaccard(List<string> a, List<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1.0;
        var ca = a.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var cb = b.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        int inter = 0, union = 0;
        foreach (var k in ca.Keys.Union(cb.Keys))
        {
            int x = ca.TryGetValue(k, out var vx) ? vx : 0;
            int y = cb.TryGetValue(k, out var vy) ? vy : 0;
            inter += Math.Min(x, y);
            union += Math.Max(x, y);
        }
        return union == 0 ? 1.0 : (double)inter / union;
    }

    // ── external oracle ──────────────────────────────────────────────────────

    private static bool PdftotextAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("pdftotext", "-v")
            { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            p!.WaitForExit(5000);
            return true;
        }
        catch { return false; }
    }

    private static string RunPdftotext(string pdfPath, int page)
    {
        var psi = new ProcessStartInfo("pdftotext",
            $"-f {page} -l {page} \"{pdfPath}\" -")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        // #925: drain both redirected pipes concurrently.
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(20000))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* gone */ }
        }
        _ = stderrTask.GetAwaiter().GetResult();
        return stdoutTask.GetAwaiter().GetResult();
    }

    // ── locate corpus / repo ─────────────────────────────────────────────────

    private static string? FindTestPdfsDir()
    {
        // Explicit override wins (test-pdfs is gitignored and may live outside a
        // worktree checkout).
        var env = Environment.GetEnvironmentVariable("EXCISE_TEST_PDFS");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;

        var candidate = Path.Combine(FindRepoRoot(), "test-pdfs");
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? AppContext.BaseDirectory;
    }
}
