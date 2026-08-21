using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// REDACTION MUST REMOVE THE TERM AND ESSENTIALLY NOTHING ELSE — measured
/// corpus-wide, with terms chosen by the corpus rather than by hand (#919).
///
/// The failure this exists for is over-removal: redacting one word takes out
/// the rest of the line, or the whole text operator. It is invisible to every
/// other redaction assertion in this repo, because they all ask "is the term
/// gone?" and none asks "is everything else still there?" — the same
/// one-directional blindness CLAUDE.md records for the corpus gate, where
/// over-draw is computed and never gated (#904, #907).
///
/// That asymmetry let a batching change destroy ~2,200 characters on a
/// six-page form while 4,056 Core tests, 1,326 App tests, 132 CLI tests, t0 and
/// 55 oracle-backed rendering redaction tests all passed.
///
/// THE GENERALISATION. Hand-written cases only catch the instance you already
/// found. The property is computable, so this measures it instead:
///
///     collateral = (alphanumerics removed) - (alphanumerics the term accounts for)
///
/// over every fixture, for terms SAMPLED FROM EACH DOCUMENT (most frequent,
/// mid-frequency, rare) rather than chosen by a human who already knows where
/// the bug is. Results ratchet against a checked-in baseline, exactly like
/// extraction-parity and copy-whitespace-parity.
///
/// ⚠️ The baseline records CURRENT behaviour, not good behaviour. A green run
/// means "no worse than measured", never "the collateral is acceptable".
///
/// This warning used to explain the large numbers by saying "RedactText also
/// drops whole text-showing operators and whole lines that still contain it".
/// #1090 DELETED those paths after measuring them at 0 firings in 235 rows, so
/// that is no longer the explanation for anything — and the five worst rows in
/// the baseline went to exactly 0 once #1038 fixed the real cause (a
/// geometry-only form-field scrub deleting whole field values).
///
/// <para>#1094: this harness also refereeing the reported COUNT against
/// mutool's before/after delta. Every other count assertion in the suite is a
/// .Should().Be(1) on a fixture where matching succeeds — which is exactly
/// where #1043 (reporting attempts as removals) is invisible.</para>
///
/// The oracle is mutool, never excise: page.Text is what a broken redaction
/// would also corrupt, so it cannot referee its own output.
/// </summary>
public class RedactionCollateralHarness
{
    private const string BaselinePath = "tests/redaction-collateral/baseline.json";

    /// <summary>
    /// #1101 — fixtures where excise's own match count disagrees with mutool's
    /// before/after delta. Recorded, not skipped: the collateral half of the
    /// gate still runs on them; only the count half is excused.
    ///
    /// <para>Checked BOTH ways. A listed fixture whose counts start agreeing
    /// fails this gate until its entry is deleted, so a fix cannot leave a
    /// stale exemption behind.</para>
    ///
    /// <para>NOT caused by the standard-14 metrics work (#1100). Measured at
    /// 046799e1, before it: issue1350.pdf reports 36 removals of a term mutool
    /// counts 9 times, identically either way.</para>
    /// </summary>
    private static readonly HashSet<string> CountDisagreesWithOracle = new(StringComparer.Ordinal)
    {
        "issue1350.pdf",      // 'your': excise 36, mutool 9
        "issue14297.pdf",     // 'write': excise 15, mutool 3
        "issue14821.pdf",     // 'text': excise 97, mutool 96
        "ZapfDingbats.pdf",   // 'document': excise 4, mutool 3
    };

    /// <summary>
    /// Baseline value meaning "redaction threw on this document/term". Negative
    /// so it can never be confused with a collateral count, which is clamped at
    /// zero (#1046).
    /// </summary>
    private const int ThrewSentinel = -1;

    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var f in EnumerateFixtures()) data.Add(Path.GetFileName(f));
        if (data.Count == 0) data.Add("(no corpus)");
        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void RedactingASampledTerm_RemovesTheTermAndLittleElse(string fixtureName)
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipWhen(fixtureName == "(no corpus)", "smoke/federal corpus not present");

        var path = EnumerateFixtures().FirstOrDefault(f => Path.GetFileName(f) == fixtureName);
        Assert.SkipWhen(path == null, "fixture not found");

        // #1046: the sampled corpora are renderer REGRESSION suites — a good
        // fraction of them are malformed on purpose. A document excise cannot
        // open has no redaction behaviour to measure, so it is skipped rather
        // than failed; "excise cannot open this at all" is a parser question,
        // not a collateral one, and conflating them would make this gate red
        // for reasons it has no opinion about.
        string before;
        try
        {
            before = ExtractAll(path!);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Assert.Skip($"excise cannot open {fixtureName}: {ex.GetType().Name}");
            return;
        }

        Assert.SkipWhen(before.Length < 200, "fixture has too little text to sample terms from");

        var baseline = LoadBaseline();
        var failures = new List<string>();
        var countMismatches = 0;
        var measured = new SortedDictionary<string, int>();

        foreach (var term in SampleTerms(before))
        {
            var output = Path.Combine(Path.GetTempPath(), $"excise-collateral-{Guid.NewGuid():N}.pdf");
            var reported = 0;
            try
            {
                try
                {
                    using var doc = PdfDocument.Open(File.ReadAllBytes(path!));
                    reported = doc.RedactText(term).VerifiedRemovals;
                    doc.Save(output);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // #1046: the sampled corpora are renderer regression suites
                    // — deliberately full of malformed files — so a throw here
                    // is expected on some of them and must not redden the gate
                    // for every other document.
                    //
                    // It is still RECORDED, and ratcheted like a collateral
                    // number: a document that throws today may keep throwing,
                    // but a NEW throw is a regression and fails. Skipping
                    // silently is what let a defect hide in this corpus in the
                    // first place.
                    measured[term] = ThrewSentinel;
                    var throwKey = $"{fixtureName}|{term}";
                    if (!baseline.TryGetValue(throwKey, out var wasThrowing) || wasThrowing != ThrewSentinel)
                        failures.Add(
                            $"'{term}': redaction threw {ex.GetType().Name} — " +
                            $"{ex.Message}. This document did not throw before.");
                    continue;
                }

                var after = ExtractAll(output);

                // Half one: the term must actually be gone. Already covered
                // elsewhere, asserted here so a "0 collateral" result cannot be
                // achieved by doing nothing at all.
                if (after.Contains(term, StringComparison.OrdinalIgnoreCase))
                    failures.Add($"'{term}': still present after redaction");

                // #1094: the COUNT, refereed by mutool. This is the one number
                // a user reads and acts on, and until now nothing checked it
                // corpus-wide -- every other count assertion in the suite is a
                // .Should().Be(1) on a fixture where matching SUCCEEDS, which
                // is precisely where #1043 (reporting attempts as removals) is
                // invisible.
                var oracleRemoved = CountOccurrences(before, term) - CountOccurrences(after, term);
                //
                // Non-vacuous by construction: terms are SAMPLED FROM the
                // document's own text, so CountOccurrences(before, term) is
                // always > 0 and there is always a real number to disagree
                // with.
                //
                // ⚠️ This comment used to read "Measured at introduction: 0
                // mismatches in 235 rows". That measurement was WRONG — it was
                // taken with a Console.WriteLine diagnostic in a class with no
                // ITestOutputHelper, so it could not have printed a mismatch if
                // one existed. Four fixtures disagree; they are in
                // CountDisagreesWithOracle above. A gate hardened on a
                // measurement that could not fail is the same vacuity this file
                // exists to catch, one level up.
                if (reported != oracleRemoved) countMismatches++;
                if (reported != oracleRemoved && !CountDisagreesWithOracle.Contains(fixtureName))
                    failures.Add(
                        $"'{term}': excise reported {reported} removed, mutool says " +
                        $"{oracleRemoved} ({CountOccurrences(before, term)} before, " +
                        $"{CountOccurrences(after, term)} after). The count is the one " +
                        "number a user reads and acts on (#1043, #1094).");

                var removed = Alnum(before) - Alnum(after);
                var termCost = term.Length * CountOccurrences(before, term);
                var collateral = Math.Max(0, removed - termCost);
                measured[term] = collateral;

                var key = $"{fixtureName}|{term}";
                if (baseline.TryGetValue(key, out var allowed))
                {
                    // Headroom absorbs extractor jitter; anything larger is a
                    // real increase in destroyed text.
                    var ceiling = allowed + Math.Max(50, allowed / 10);
                    if (collateral > ceiling)
                        failures.Add(
                            $"'{term}': collateral {collateral} exceeds baseline {allowed} (ceiling {ceiling}) — " +
                            "redaction destroyed MORE untargeted text than before");
                }
            }
            finally { try { File.Delete(output); } catch { /* best effort */ } }
        }

        if (Environment.GetEnvironmentVariable("REDACTION_COLLATERAL_UPDATE") == "1")
        {
            WriteBaseline(fixtureName, measured);
            return;
        }

        if (CountDisagreesWithOracle.Contains(fixtureName) && countMismatches == 0)
            failures.Add(
                $"{fixtureName} is listed in CountDisagreesWithOracle (#1101) but its counts " +
                "now agree with mutool on every sampled term. Delete its entry.");

        failures.Should().BeEmpty(
            $"{fixtureName}: redaction must remove the term and little else.\n" +
            string.Join("\n", failures) +
            "\n\nIf this is a deliberate behaviour change, re-run with " +
            "REDACTION_COLLATERAL_UPDATE=1 and review the baseline diff — the numbers are " +
            "how much untargeted text redaction destroys, so an increase is a defect until argued otherwise.");
    }

    /// <summary>
    /// Terms chosen BY THE DOCUMENT, not by a human who knows where the bug is:
    /// the most frequent word, one from the middle of the frequency
    /// distribution, and a rare one. Deterministic, so the baseline is stable.
    /// </summary>
    internal static List<string> SampleTerms(string text)
    {
        var freq = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var w = new string(raw.Where(char.IsLetter).ToArray());
            if (w.Length < 4 || w.Length > 12) continue;
            freq[w] = freq.TryGetValue(w, out var n) ? n + 1 : 1;
        }
        var ordered = freq.Where(kv => kv.Value >= 2)
                          .OrderByDescending(kv => kv.Value)
                          .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                          .Select(kv => kv.Key)
                          .ToList();
        if (ordered.Count == 0) return new List<string>();

        var picks = new List<string> { ordered[0] };
        if (ordered.Count > 2) picks.Add(ordered[ordered.Count / 2]);
        if (ordered.Count > 1) picks.Add(ordered[^1]);
        return picks.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Documents that have ALREADY leaked or over-removed. Pinned in by name
    /// and never sampled away (#1046).
    ///
    /// <para>Every redaction defect this project has found lived outside the
    /// smoke/federal set this harness used to cover: #1040's name survived on
    /// a Nitro Pro file in <c>pdfjs</c>, #1039's stall reproduced on a
    /// <c>pdfium</c> file. The gate that would have caught them was pointed
    /// somewhere else, so a one-off sweep found them instead — which is the
    /// job a gate exists to make unnecessary.</para>
    /// </summary>
    private static readonly string[] RegressionFixtures =
    {
        "test-pdfs/pdfjs/issue15629.pdf",              // #1040 — indirect /XObject leak
        "test-pdfs/pdfium/hello_world_split_streams.pdf", // #1039 — unterminated BT
    };

    /// <summary>Corpora sampled from, beyond the always-covered sets.</summary>
    private static readonly string[] SampledCorpora =
    {
        "test-pdfs/pdfjs", "test-pdfs/pdfium", "test-pdfs/pdf20", "test-pdfs/poppler",
    };

    /// <summary>
    /// Documents to draw from each sampled corpus; <b>0 means all of them, and
    /// that is the default</b>. Set <c>REDACTION_COLLATERAL_SAMPLE=N</c> for a
    /// fast slice while iterating locally.
    ///
    /// <para>Sampling was the plan until it was measured. The full sweep is
    /// <b>1,007 documents in 6m31s</b> — cheap, because ~90% of the corpus
    /// skips in about a millisecond for having too little text, and only the
    /// ~99 measurable documents cost real time. Raising a per-corpus sample
    /// from 12 to 45 added <b>four</b> measurable documents and no wall-clock
    /// at all, which makes a sample nearly all cost and no benefit.</para>
    ///
    /// <para>⚠️ A sample BOUNDS COVERAGE, and every redaction defect found so
    /// far was one document in several hundred: #1040 one file in pdfjs, #1039
    /// one in pdfium, and the first full run of this gate turned up #1047 (six
    /// occurrences of a term surviving) and #1048 (a crash) — neither of which
    /// any 12-per-corpus slice contained.</para>
    ///
    /// <para>Costs nothing on a machine without the gitignored corpora: the
    /// directories do not resolve and the rows are never generated.</para>
    /// </summary>
    private static int SampleSize =>
        int.TryParse(Environment.GetEnvironmentVariable("REDACTION_COLLATERAL_SAMPLE"), out var n)
            ? n
            : 0;

    private static IEnumerable<string> EnumerateFixtures()
    {
        // smoke/ and federal/ overlap (both carry irs-w4.pdf etc.) — dedupe by
        // basename or xunit skips the second theory row as a duplicate ID and
        // the "covered" fixture silently isn't.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in new[] { "test-pdfs/smoke", "test-pdfs/federal" })
        {
            var full = Resolve(dir);
            if (full == null) continue;
            foreach (var f in Directory.EnumerateFiles(full, "*.pdf").OrderBy(x => x, StringComparer.Ordinal))
                if (seen.Add(Path.GetFileName(f)))
                    yield return f;
        }

        foreach (var rel in RegressionFixtures)
        {
            var full = Resolve(rel);
            if (full != null && seen.Add(Path.GetFileName(full)))
                yield return full;
        }

        var take = SampleSize;
        foreach (var dir in SampledCorpora)
        {
            var full = Resolve(dir);
            if (full == null) continue;

            // Deterministic and spread across the corpus rather than the first
            // N alphabetically — a prefix of a sorted listing is a biased
            // sample (pdfjs names cluster by bug number, i.e. by era).
            var all = Directory.EnumerateFiles(full, "*.pdf")
                               .OrderBy(x => x, StringComparer.Ordinal)
                               .ToList();
            if (all.Count == 0) continue;

            var step = take <= 0 ? 1 : Math.Max(1, all.Count / take);
            for (var i = 0; i < all.Count; i += step)
                if (seen.Add(Path.GetFileName(all[i])))
                    yield return all[i];
        }
    }

    private static string? Resolve(string rel)
    {
        for (var up = 0; up < 6; up++)
        {
            var p = Path.GetFullPath(Path.Combine(Enumerable.Repeat("..", up).DefaultIfEmpty(".").Aggregate(Path.Combine), rel));
            if (Directory.Exists(p)) return p;
        }
        return null;
    }

    private static string ExtractAll(string pdfPath)
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(pdfPath));
        var pages = MutoolTextExtractor.ExtractAllPages(pdfPath, doc.PageCount);
        return pages == null ? "" : string.Join("\n", pages);
    }

    private static int Alnum(string s) => s.Count(char.IsLetterOrDigit);

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    private static Dictionary<string, int> LoadBaseline()
    {
        var p = Resolve(Path.GetDirectoryName(BaselinePath)!);
        var file = p == null ? null : Path.Combine(p, Path.GetFileName(BaselinePath));
        if (file == null || !File.Exists(file)) return new Dictionary<string, int>();
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetInt32());
    }

    private static void WriteBaseline(string fixtureName, SortedDictionary<string, int> measured)
    {
        var dir = Resolve(Path.GetDirectoryName(BaselinePath)!);
        if (dir == null) return;
        var file = Path.Combine(dir, Path.GetFileName(BaselinePath));
        var all = LoadBaseline();
        foreach (var kv in measured) all[$"{fixtureName}|{kv.Key}"] = kv.Value;
        var sb = new StringBuilder("{\n");
        var keys = all.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        for (var i = 0; i < keys.Count; i++)
            sb.Append("  \"").Append(keys[i]).Append("\": ")
              .Append(all[keys[i]].ToString(CultureInfo.InvariantCulture))
              .Append(i == keys.Count - 1 ? "\n" : ",\n");
        sb.Append("}\n");
        File.WriteAllText(file, sb.ToString());
    }
}
