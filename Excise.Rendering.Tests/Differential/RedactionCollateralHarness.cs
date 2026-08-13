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
/// ⚠️ The baseline records CURRENT behaviour, not good behaviour. Redacting
/// "Form" from the W-9 legitimately removes far more than the term today,
/// because RedactText also drops whole text-showing operators and whole lines
/// that still contain it. A green run means "no worse than measured", never
/// "the collateral is acceptable".
///
/// The oracle is mutool, never excise: page.Text is what a broken redaction
/// would also corrupt, so it cannot referee its own output.
/// </summary>
public class RedactionCollateralHarness
{
    private const string BaselinePath = "tests/redaction-collateral/baseline.json";

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

        var before = ExtractAll(path!);
        Assert.SkipWhen(before.Length < 200, "fixture has too little text to sample terms from");

        var baseline = LoadBaseline();
        var failures = new List<string>();
        var measured = new SortedDictionary<string, int>();

        foreach (var term in SampleTerms(before))
        {
            var output = Path.Combine(Path.GetTempPath(), $"excise-collateral-{Guid.NewGuid():N}.pdf");
            try
            {
                using (var doc = PdfDocument.Open(File.ReadAllBytes(path!)))
                {
                    doc.RedactText(term);
                    doc.Save(output);
                }
                var after = ExtractAll(output);

                // Half one: the term must actually be gone. Already covered
                // elsewhere, asserted here so a "0 collateral" result cannot be
                // achieved by doing nothing at all.
                if (after.Contains(term, StringComparison.OrdinalIgnoreCase))
                    failures.Add($"'{term}': still present after redaction");

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
