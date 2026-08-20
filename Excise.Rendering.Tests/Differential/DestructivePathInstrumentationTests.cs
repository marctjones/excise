using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Xunit;


namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// A MEASUREMENT, not a gate — does the destructive fallback still fire?
///
/// <para>Three code paths in <c>RedactText</c> delete whole text-showing
/// operators: the stall fallback, <c>RemoveTextShowingOperatorsContaining</c>
/// (default-on) and <c>RemoveTextLinesStillContaining</c> (unconditional).
/// They are #1038's 5–36% collateral mechanism. #1090 proposes making them
/// loud or opt-in.</para>
///
/// <para><b>But there is a better outcome than making dangerous code loud, and
/// this measures for it.</b> If the paths never fire across the collateral
/// corpus, the case is to DELETE them — dead dangerous code is best removed,
/// and #1038 would close by subtraction. If they do fire, the rows they fire on
/// are #1038's missing repro fixtures. Either answer is worth having; the
/// experiment is the same.</para>
///
/// <para>Uses #1089's <c>RedactionReport.UsedDestructiveRemoval</c> rather than
/// new production instrumentation — the signal already exists.</para>
/// </summary>
public class DestructivePathInstrumentationTests
{
    private readonly ITestOutputHelper _out;
    public DestructivePathInstrumentationTests(ITestOutputHelper o) { _out = o; }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    /// <summary>
    /// Replays every (document, term) pair the collateral baseline records and
    /// asserts none reaches a destructive path.
    ///
    /// <para>Also prints per-row collateral, which is how the measurement found
    /// that #1038's collateral is REAL but its stated MECHANISM is not: rows
    /// still lose 400+ characters with the fallback never firing.</para>
    /// </summary>
    [Fact]
    public void HowOftenDoesTheDestructiveFallbackFire()
    {
        var root = RepoRoot();
        var baselinePath = Path.Combine(root, "tests/redaction-collateral/baseline.json");
        Assert.SkipUnless(File.Exists(baselinePath), "collateral baseline not present");

        var baseline = JsonSerializer.Deserialize<Dictionary<string, int>>(
            File.ReadAllText(baselinePath)) ?? new();

        // Baseline keys are "file.pdf|term"; resolve the file across the corpora
        // the harness samples from.
        var corpora = new[] { "test-pdfs/pdfjs", "test-pdfs/pdfium", "test-pdfs/pdf20",
                              "test-pdfs/poppler", "test-pdfs/smoke", "test-pdfs/federal" };

        string? Resolve(string fileName)
        {
            foreach (var c in corpora)
            {
                var p = Path.Combine(root, c, fileName);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        var destructive = new List<string>();
        var clean = 0;
        var unresolved = 0;
        var errored = 0;

        foreach (var key in baseline.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var bar = key.IndexOf('|');
            if (bar <= 0) continue;
            var file = key[..bar];
            var term = key[(bar + 1)..];
            if (term.StartsWith("THREW", StringComparison.Ordinal)) continue;

            var path = Resolve(file);
            if (path == null) { unresolved++; continue; }

            try
            {
                using var doc = PdfDocument.Open(path);

                // Text BEFORE, minus the term's own characters, is what should
                // survive. Anything else lost is collateral.
                var textBefore = string.Concat(
                    Enumerable.Range(1, doc.PageCount).Select(i => doc.GetPage(i).Text));
                var report = doc.RedactText(term, drawBlackRect: false);
                var textAfter = string.Concat(
                    Enumerable.Range(1, doc.PageCount).Select(i => doc.GetPage(i).Text));

                var beforeAlnum = textBefore.Count(char.IsLetterOrDigit);
                var afterAlnum = textAfter.Count(char.IsLetterOrDigit);
                var termCost = report.VerifiedRemovals * term.Count(char.IsLetterOrDigit);
                var collateralNow = Math.Max(0, beforeAlnum - afterAlnum - termCost);
                // Measure CURRENT collateral rather than trusting the recorded
                // floor: a ratchet only fails when things get worse, so a
                // baseline number can be stale-high.
                if (baseline[key] > 0 || collateralNow > 0)
                    _out.WriteLine($"  COLLATERAL-ROW {key}: floor={baseline[key]} NOW={collateralNow} " +
                                   $"destructive={report.UsedDestructiveRemoval}");
                if (report.UsedDestructiveRemoval)
                    destructive.Add($"{key}  (located {report.MatchesLocated}, " +
                                    $"verified {report.VerifiedRemovals}, " +
                                    $"survived {report.Survived}, " +
                                    $"baseline collateral {baseline[key]})");
                else clean++;
            }
            catch (Exception ex) { errored++; _out.WriteLine($"  ERROR {key}: {ex.GetType().Name}"); }
        }

        _out.WriteLine($"replayed        : {baseline.Count} baseline rows");
        _out.WriteLine($"  no destructive: {clean}");
        _out.WriteLine($"  DESTRUCTIVE   : {destructive.Count}");
        _out.WriteLine($"  unresolved    : {unresolved}");
        _out.WriteLine($"  errored       : {errored}");
        foreach (var d in destructive) _out.WriteLine($"    {d}");

        // Anti-vacuity FIRST: a zero destructive count means nothing if nothing
        // ran. Corpus ABSENT (nothing resolved) skips; corpus PARTIAL fails,
        // because a half-provisioned runner silently shrinks what is checked
        // and would report the pin as held on rows it never replayed.
        var replayed = clean + destructive.Count;
        Assert.SkipWhen(replayed == 0,
            "needs the redaction-collateral corpora [requires: corpus:pdfjs]");
        replayed.Should().BeGreaterThan(200,
            "the corpus resolved only partially; that shrinks what this pin covers " +
            "without saying so, which is how a gate goes vacuous");

        // MEASURED 2026-08-20: 0 of 235. The destructive paths are dead code in
        // practice across every document this project redacts in anger.
        //
        // Pinned at zero deliberately. If a change makes one of them fire
        // again, that is a return of the mechanism #1038 was filed about and it
        // should have to be argued for, not discovered later in a collateral
        // ratchet. The failure message names the rows, so a legitimate change
        // can move the pin with evidence.
        destructive.Should().BeEmpty(
            "the whole-operator removal paths must not fire. They delete an entire " +
            "text-showing operator to remove one word, and they fired ZERO times across " +
            "235 real document/term pairs when this was measured -- which is the case for " +
            "DELETING them (#1090), not for making them loud");
    }
}
