using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #1114 — the redaction benchmark runner. For every <c>(document, term)</c>
/// case it redacts with excise and emits ONE machine-readable row.
///
/// <para><b>This is a measurement, not a gate.</b> It asserts only that it
/// actually ran; it does not assert any score. The ratchets
/// (<see cref="RedactionCollateralHarness"/>, the parity gates) are what fail
/// on regression. What this produces is the thing those cannot: a single table
/// you can read to decide WHICH of the open redaction issues to fix first.
/// That ordering is currently guesswork, and #1038 spent months attributed to
/// the wrong mechanism because nothing measured which mechanism fired.</para>
///
/// <para><b>Ground truth is constructed, not collected.</b> There is no public
/// corpus of paired original/redacted documents (checked — see
/// docs/CORPORA.md). None is needed: we supply the input and the term, so the
/// expected output is <i>input minus exactly that term</i>. The unredacted
/// original IS the input.</para>
///
/// <para>excise only, deliberately. Comparing against PyMuPDF and pdfSweep
/// answers "how good do we want to be" (RC16 #1121) — a planning question that
/// does not help rank defects. Adding a tool later is a parameter, not a
/// redesign.</para>
///
/// <example>
/// <code>
/// REDACTION_BENCH=1 dotnet test Excise.Rendering.Tests \
///   --filter FullyQualifiedName~RedactionBenchmarkRunner
/// # → logs/redaction-benchmark/results.jsonl
/// </code>
/// </example>
/// </summary>
public sealed class RedactionBenchmarkRunner
{
    private readonly ITestOutputHelper _out;
    public RedactionBenchmarkRunner(ITestOutputHelper o) { _out = o; }

    /// <summary>One case. Serialised verbatim; add fields, never repurpose them.</summary>
    private sealed record Row
    {
        public string Document { get; init; } = "";
        public string Corpus { get; init; } = "";
        public string Term { get; init; } = "";
        public int Pages { get; init; }

        // What excise says it did.
        public int Reported { get; init; }
        public bool CleanSuccess { get; init; }

        // LEAK — per channel, because "leaked" alone is unactionable.
        public int OracleBefore { get; init; }
        public int OracleAfter { get; init; }
        public bool LeakSavedBytes { get; init; }
        public string LeakContext { get; init; } = "";
        public bool LeakOracleText { get; init; }
        public int LeakBadRedactions { get; init; }   // -1 = x-ray unavailable
        public string[] LeakChannels { get; init; } = Array.Empty<string>();

        // COLLATERAL — untargeted content the redaction took with it.
        public int AlnumBefore { get; init; }
        public int AlnumAfter { get; init; }
        public int Collateral { get; init; }
        public double CollateralFraction { get; init; }

        // FIDELITY.
        public bool QpdfOk { get; init; }

        public string? Error { get; init; }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    /// <summary>
    /// Corpora to sample, most real-world first. Real documents are weighted
    /// deliberately: the renderer-regression corpora are full of deliberately
    /// strange files, and a benchmark dominated by them measures how excise
    /// handles strangeness rather than how it handles the insurance
    /// certificate someone actually needs redacted (#1038's document).
    /// </summary>
    private static readonly (string Name, int Take)[] Corpora =
    {
        ("smoke", 20), ("federal", 20), ("local-real-world", 10),
        ("itext", 15), ("pdfjs", 40), ("pdfium", 25), ("poppler", 15),
    };

    [Fact]
    public void RunBenchmark()
    {
        // Opt-in: this redacts hundreds of documents and is not something a
        // normal test run should pay for.
        Assert.SkipUnless(Environment.GetEnvironmentVariable("REDACTION_BENCH") == "1",
            "set REDACTION_BENCH=1 to run the benchmark [requires: env:REDACTION_BENCH]");

        var root = RepoRoot();
        var rows = new List<Row>();
        var docsSeen = 0;

        foreach (var (corpus, take) in Corpora)
        {
            var dir = Path.Combine(root, "test-pdfs", corpus);
            if (!Directory.Exists(dir)) { _out.WriteLine($"absent: {corpus}"); continue; }

            // Ordered, then taken — the case list must be reproducible, or two
            // runs cannot be compared and the numbers mean nothing.
            var files = Directory.GetFiles(dir, "*.pdf", SearchOption.AllDirectories)
                                 .OrderBy(f => f, StringComparer.Ordinal)
                                 .Take(take);

            foreach (var file in files)
            {
                docsSeen++;
                foreach (var row in MeasureDocument(file, corpus))
                    rows.Add(row);
            }
        }

        WriteReport(root, rows);
        Summarise(rows, docsSeen);

        // Anti-vacuity, and the ONLY assertion. An empty run must not read as
        // a clean one -- that ambiguity is what let #1094 ship a check that
        // could not fail.
        rows.Count.Should().BeGreaterThan(20,
            "the benchmark must actually exercise the corpora; a run that measured " +
            "almost nothing tells you nothing, and looks identical to a good result");
    }

    private IEnumerable<Row> MeasureDocument(string path, string corpus)
    {
        var name = Path.GetFileName(path);

        // C# forbids `yield` inside catch, so the failure is captured first
        // and yielded after the try/catch rather than restructured away.
        string before = "";
        var pageCount = 0;
        string? openError = null;
        try
        {
            using var probe = PdfDocument.Open(path);
            pageCount = probe.PageCount;
            var pages = MutoolTextExtractor.ExtractAllPages(path, pageCount);
            before = pages == null ? "" : string.Join("\n", pages);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            openError = $"open: {ex.GetType().Name}";
        }

        if (openError != null)
        {
            yield return new Row { Document = name, Corpus = corpus, Error = openError };
            yield break;
        }

        // The ORACLE picks the terms, from its own reading of the document.
        // Sampling from excise's extraction would only ever test terms excise
        // can already see, which is the blind spot, not a control for it.
        if (before.Length < 200) yield break;

        foreach (var term in RedactionCollateralHarness.SampleTerms(before))
            yield return MeasureCase(path, corpus, name, term, before, pageCount);
    }

    private Row MeasureCase(string path, string corpus, string name, string term,
                            string before, int pageCount)
    {
        var output = Path.Combine(Path.GetTempPath(), $"excise-bench-{Guid.NewGuid():N}.pdf");
        try
        {
            RedactionReport report;
            byte[] savedBytes;
            try
            {
                using var doc = PdfDocument.Open(path);
                report = doc.RedactText(term);
                doc.Save(output);
                savedBytes = File.ReadAllBytes(output);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A throw is a RESULT. Dropping the documents a tool crashes on
                // is how a benchmark flatters the tool it measures.
                return new Row
                {
                    Document = name, Corpus = corpus, Term = term, Pages = pageCount,
                    Error = $"{ex.GetType().Name}: {ex.Message}",
                };
            }

            var afterPages = MutoolTextExtractor.ExtractAllPages(output, pageCount);
            var after = afterPages == null ? "" : string.Join("\n", afterPages);

            // ⚠️ A raw byte hit is NOT automatically a leak, and the first run
            // of this benchmark proved it: sampling common words from the
            // document made "zero" match /CharSet (/period/slash/zero/one/...)
            // -- the PostScript GLYPH NAME for the digit 0, in a font
            // descriptor. 32% of cases "leaked" and most were that.
            //
            // So classify the context. A hit that only ever appears in font
            // machinery is not the secret surviving; it is the alphabet.
            var (leakBytes, leakContext) = ClassifyByteHit(savedBytes, term);
            var leakText = after.Contains(term, StringComparison.OrdinalIgnoreCase);

            var xray = XRayBadRedactionDetector.Inspect(output);
            // null and empty mean opposite things: -1 records "no oracle", 0
            // records "the oracle ran and found none".
            var badRedactions = xray == null ? -1 : xray.Count;

            var channels = new List<string>();
            if (leakBytes) channels.Add("saved-bytes");
            if (leakText) channels.Add("oracle-text");
            if (badRedactions > 0) channels.Add("bad-redaction");

            var alnumBefore = before.Count(char.IsLetterOrDigit);
            var alnumAfter = after.Count(char.IsLetterOrDigit);
            var termCost = report.VerifiedRemovals * term.Count(char.IsLetterOrDigit);
            var collateral = Math.Max(0, alnumBefore - alnumAfter - termCost);

            var qpdf = QpdfReferenceTool.Check(output);

            return new Row
            {
                Document = name, Corpus = corpus, Term = term, Pages = pageCount,
                Reported = report.VerifiedRemovals,
                CleanSuccess = report.IsCleanSuccess,
                OracleBefore = CountOccurrences(before, term),
                OracleAfter = CountOccurrences(after, term),
                LeakSavedBytes = leakBytes,
                LeakContext = leakContext,
                LeakOracleText = leakText,
                LeakBadRedactions = badRedactions,
                LeakChannels = channels.ToArray(),
                AlnumBefore = alnumBefore,
                AlnumAfter = alnumAfter,
                Collateral = collateral,
                CollateralFraction = alnumBefore == 0 ? 0 : (double)collateral / alnumBefore,
                QpdfOk = qpdf?.Success ?? false,
            };
        }
        finally { try { File.Delete(output); } catch { /* best effort */ } }
    }

    /// <summary>
    /// Contexts where a byte match is font machinery rather than surviving
    /// content. Glyph names in particular are ordinary English words —
    /// "zero", "one", "four", "period", "space" — so any document using those
    /// digits carries those strings by construction.
    /// </summary>
    private static readonly string[] BenignContexts =
    {
        "/CharSet", "/BaseFont", "/FontName", "/Differences", "/FontFile",
        "/Registry", "/Ordering", "/W [", "/CIDSystemInfo",
    };

    /// <summary>
    /// Whether <paramref name="term"/> survives in the saved bytes somewhere
    /// that MATTERS, plus a short label saying where. Returns false only when
    /// EVERY occurrence sits in font machinery — one real hit is a leak even
    /// if a hundred benign ones surround it.
    /// </summary>
    private static (bool Leaked, string Context) ClassifyByteHit(byte[] saved, string term)
    {
        var hits = Excise.Core.Tests.Text.Segmentation.SavedPdfLeakScanner.FindTerm(saved, term);
        if (hits.Count == 0) return (false, "");

        var text = System.Text.Encoding.Latin1.GetString(saved);
        var contexts = new List<string>();
        var realHit = false;

        var i = 0;
        while ((i = text.IndexOf(term, i, StringComparison.Ordinal)) >= 0)
        {
            var before = text[Math.Max(0, i - 300)..i];
            var benign = BenignContexts.FirstOrDefault(b => before.Contains(b, StringComparison.Ordinal));
            if (benign == null)
            {
                realHit = true;
                contexts.Add(
                    before.Contains("<?xpacket", StringComparison.Ordinal) || before.Contains("rdf:", StringComparison.Ordinal) ? "xmp"
                    : before.Contains("/ActualText", StringComparison.Ordinal) || before.Contains("/Alt", StringComparison.Ordinal) ? "structure-tree"
                    : before.Contains("/Title", StringComparison.Ordinal) || before.Contains("/Author", StringComparison.Ordinal)
                      || before.Contains("/Subject", StringComparison.Ordinal) || before.Contains("/Keywords", StringComparison.Ordinal) ? "info"
                    : "raw");
            }
            else contexts.Add("font:" + benign.Trim('/', ' ', '['));
            i += term.Length;
        }

        // Recorded either way. "leaked=false because every hit was a glyph
        // name" is a finding worth being able to read back, not something to
        // silently drop.
        return (realHit, string.Join(",", contexts.Distinct().OrderBy(c => c, StringComparer.Ordinal)));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        { n++; i += needle.Length; }
        return n;
    }

    private void WriteReport(string root, List<Row> rows)
    {
        var dir = Path.Combine(root, "logs", "redaction-benchmark");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "results.jsonl");
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllLines(path, rows.Select(r => JsonSerializer.Serialize(r, opts)));
        _out.WriteLine($"rows → {path}");
    }

    /// <summary>
    /// Per-corpus, because an aggregate hides concentration — and concentration
    /// is the actionable part. "Fine everywhere except X" is a different
    /// instruction from "uniformly mediocre".
    /// </summary>
    private void Summarise(List<Row> rows, int docsSeen)
    {
        var ok = rows.Where(r => r.Error == null).ToList();
        _out.WriteLine($"documents visited : {docsSeen}");
        _out.WriteLine($"cases measured    : {ok.Count}");
        _out.WriteLine($"cases errored     : {rows.Count - ok.Count}");
        _out.WriteLine("");
        _out.WriteLine($"{"corpus",-18} {"cases",6} {"leaks",6} {"badRedact",10} {"countMismatch",14} {"collateral>1%",14} {"qpdfBad",8}");

        foreach (var g in ok.GroupBy(r => r.Corpus).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            _out.WriteLine(
                $"{g.Key,-18} {g.Count(),6} {g.Count(r => r.LeakChannels.Length > 0),6} " +
                $"{g.Count(r => r.LeakBadRedactions > 0),10} " +
                $"{g.Count(r => r.Reported != r.OracleBefore - r.OracleAfter),14} " +
                $"{g.Count(r => r.CollateralFraction > 0.01),14} " +
                $"{g.Count(r => !r.QpdfOk),8}");
        }

        _out.WriteLine("");
        foreach (var r in ok.Where(r => r.LeakChannels.Length > 0).Take(15))
            _out.WriteLine($"  LEAK {r.Corpus}/{r.Document} '{r.Term}' via {string.Join("+", r.LeakChannels)}");
        foreach (var r in ok.Where(r => r.CollateralFraction > 0.05)
                            .OrderByDescending(r => r.CollateralFraction).Take(15))
            _out.WriteLine($"  COLLATERAL {r.Corpus}/{r.Document} '{r.Term}' " +
                           $"{r.Collateral} chars = {r.CollateralFraction:P1}");
        foreach (var e in rows.Where(r => r.Error != null).GroupBy(r => r.Error!.Split(':')[0])
                              .OrderByDescending(g => g.Count()).Take(10))
            _out.WriteLine($"  ERROR {e.Key} × {e.Count()}");
    }
}
