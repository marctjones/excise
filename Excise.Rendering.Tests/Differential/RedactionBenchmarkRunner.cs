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

    /// <summary>
    /// What actually happened to the term, in the only three grades that
    /// differ in what an attacker can do with the result.
    ///
    /// <para>Collapsing these into "leaked / did not leak" loses the
    /// distinction that matters most: RECOVERABLE means someone reads the
    /// secret, RESIDUE means someone narrows it down. They call for different
    /// fixes and carry different risk, and a single boolean says neither.</para>
    /// </summary>
    private enum Verdict
    {
        /// <summary>Could not be measured — the tool threw, or an oracle refused.</summary>
        Unmeasured,

        /// <summary>
        /// The exact term can be read back out of the output — by an
        /// extractor, by x-ray under a covering box, or from the saved bytes.
        /// No inference required. The worst grade, and the only one where the
        /// secret is definitely disclosed.
        /// </summary>
        Recoverable,

        /// <summary>
        /// The term is gone from every carrier we can search, but the output
        /// still carries measurable information ABOUT it: the layout kept the
        /// gap, so the width of the removed string survives and constrains
        /// what it could have been. Recovery needs guessing and a dictionary
        /// — see #1116, which quantifies it in bits.
        /// </summary>
        RemovedWithResidue,

        /// <summary>
        /// Gone, and the layout closed up behind it, so no width channel
        /// remains. Note this is not free: closing up moves the surviving
        /// text, which the collateral columns will show.
        /// </summary>
        Removed,
    }

    /// <summary>One case. Serialised verbatim; add fields, never repurpose them.</summary>
    private sealed record Row
    {
        public string Tool { get; init; } = "excise";
        /// <summary>Recoverable / RemovedWithResidue / Removed. See <see cref="Verdict"/>.</summary>
        public string Verdict { get; init; } = "Unmeasured";
        /// <summary>Null when the gap could not be judged (nothing removed on page 1, or no oracle).</summary>
        public bool? GapPreserved { get; init; }
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
        /// <summary>Byte occurrences in the ORIGINAL, across raw + inflated streams.</summary>
        public int BytesBefore { get; init; }
        public int BytesAfter { get; init; }
        /// <summary>
        /// False when the term already lived somewhere redaction is not
        /// responsible for. See the comment on the leak assertion.
        /// </summary>
        public bool ProbeUsable { get; init; }
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

    /// <summary>
    /// The tools under comparison. excise is one entry, not the centre — a
    /// benchmark that can only measure the tool that wrote it is a
    /// self-assessment.
    /// </summary>
    private static IEnumerable<string> Tools()
    {
        yield return "excise";
        if (PyMuPdfPython() != null) yield return "pymupdf";
    }

    /// <summary>The x-ray venv python, which also carries PyMuPDF.</summary>
    private static string? PyMuPdfPython()
    {
        var p = Path.Combine(RepoRoot(), "tools", "vendor", "xray-venv", "bin", "python");
        return File.Exists(p) ? p : null;
    }

    /// <summary>
    /// Run a competitor as a subprocess. Returns the occurrence count it
    /// reports, or null when it failed — which is RECORDED, never skipped.
    /// </summary>
    private static int? RunAdapter(string tool, string src, string dst, string term)
    {
        var python = PyMuPdfPython();
        if (python == null) return null;
        var script = Path.Combine(RepoRoot(), "scripts", "benchmark-adapters", $"redact-{tool}.py");
        if (!File.Exists(script)) return null;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(python)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in new[] { script, src, dst, term }) psi.ArgumentList.Add(a);
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(120_000)) { try { proc.Kill(true); } catch { } return null; }
            if (proc.ExitCode != 0) return null;
            return int.TryParse(stdout.Trim(), out var n) ? n : 0;
        }
        catch { return null; }
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
                foreach (var tool in Tools())
                    foreach (var row in MeasureDocument(file, corpus, tool))
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

    private IEnumerable<Row> MeasureDocument(string path, string corpus, string tool)
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
            yield return new Row { Tool = tool, Document = name, Corpus = corpus, Error = openError };
            yield break;
        }

        // The ORACLE picks the terms, from its own reading of the document.
        // Sampling from excise's extraction would only ever test terms excise
        // can already see, which is the blind spot, not a control for it.
        if (before.Length < 200) yield break;

        foreach (var term in RedactionCollateralHarness.SampleTerms(before))
            yield return MeasureCase(path, corpus, name, term, before, pageCount, tool);
    }

    private Row MeasureCase(string path, string corpus, string name, string term,
                            string before, int pageCount, string tool)
    {
        var output = Path.Combine(Path.GetTempPath(), $"excise-bench-{Guid.NewGuid():N}.pdf");
        try
        {
            var reported = 0;
            var cleanSuccess = false;
            byte[] savedBytes;
            try
            {
                if (tool == "excise")
                {
                    using var doc = PdfDocument.Open(path);
                    var report = doc.RedactText(term);
                    reported = report.VerifiedRemovals;
                    cleanSuccess = report.IsCleanSuccess;
                    doc.Save(output);
                }
                else
                {
                    var n = RunAdapter(tool, path, output, term);
                    if (n == null)
                        return new Row
                        {
                            Tool = tool, Document = name, Corpus = corpus, Term = term,
                            Pages = pageCount, Error = $"{tool}: adapter failed",
                        };
                    reported = n.Value;
                    // Competitors report a count, not a verification. Recorded
                    // as false rather than guessed at -- excise's
                    // IsCleanSuccess has no equivalent elsewhere, and inventing
                    // one would flatter or penalise arbitrarily.
                    cleanSuccess = false;
                }
                savedBytes = File.ReadAllBytes(output);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A throw is a RESULT. Dropping the documents a tool crashes on
                // is how a benchmark flatters the tool it measures.
                return new Row
                {
                    Tool = tool, Verdict = nameof(Verdict.Unmeasured),
                    Document = name, Corpus = corpus, Term = term,
                    Pages = pageCount, Error = $"{ex.GetType().Name}: {ex.Message}",
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
            var (rawLeak, leakContext) = ClassifyByteHit(savedBytes, term);

            // ── IS THIS TERM A USABLE LEAK PROBE AT ALL? ──────────────────
            // Sampling by frequency is right for collateral and WRONG here.
            // Measured: leak rate tracked term frequency almost perfectly --
            // 12% on rare terms, 20% mid, 57% frequent -- which is the
            // signature of a common word living elsewhere in the file, not of
            // a redaction defect. irs-w4.pdf 'your' "leaked" inside embedded
            // Acrobat JavaScript: "...see your system administrator."
            //
            // A term is only a usable probe when its byte occurrences in the
            // ORIGINAL are accounted for by the text the oracle can see. If it
            // also lives in JavaScript, field names or viewer boilerplate,
            // redaction was never asked to remove those and its survival there
            // says nothing.
            var bytesBefore = CountByteOccurrences(File.ReadAllBytes(path), term);
            var bytesAfter = CountByteOccurrences(savedBytes, term);
            var textBefore = CountOccurrences(before, term);
            var probeUsable = bytesBefore > 0 && bytesBefore <= textBefore;
            var leakBytes = rawLeak && probeUsable;
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
            var termCost = reported * term.Count(char.IsLetterOrDigit);
            var collateral = Math.Max(0, alnumBefore - alnumAfter - termCost);

            var qpdf = QpdfReferenceTool.Check(output);

            // ── RESIDUE: did the layout keep the hole? ────────────────────
            // Page 1 only. This is a sampled signal, not a per-occurrence one:
            // a document whose page 1 reflows and whose page 4 does not will
            // read as "closed up", and the honest fix for that is #1116's
            // per-removal measurement rather than pretending this is exact.
            var glyphsBefore = MutoolGlyphPositions.ExtractPage(path, 1);
            var glyphsAfter = MutoolGlyphPositions.ExtractPage(output, 1);
            var gapPreserved = MutoolGlyphPositions.LayoutGapPreserved(glyphsBefore, glyphsAfter);

            var recoverable = leakText || badRedactions > 0 || leakBytes;
            var verdict =
                recoverable ? Differential.RedactionBenchmarkRunner.Verdict.Recoverable
                : gapPreserved == true ? Differential.RedactionBenchmarkRunner.Verdict.RemovedWithResidue
                : Differential.RedactionBenchmarkRunner.Verdict.Removed;

            return new Row
            {
                Verdict = verdict.ToString(),
                GapPreserved = gapPreserved,
                Tool = tool, Document = name, Corpus = corpus, Term = term, Pages = pageCount,
                Reported = reported,
                CleanSuccess = cleanSuccess,
                OracleBefore = CountOccurrences(before, term),
                OracleAfter = CountOccurrences(after, term),
                LeakSavedBytes = leakBytes,
                LeakContext = leakContext,
                BytesBefore = bytesBefore,
                BytesAfter = bytesAfter,
                ProbeUsable = probeUsable,
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
    ///
    /// <para>⚠️ Scans the raw bytes AND every inflated stream, because
    /// <c>SavedPdfLeakScanner</c> does. The first version of this classifier
    /// scanned only the raw Latin-1 text, so any hit inside a /FlateDecode
    /// stream found no context, scored as benign, and silently downgraded the
    /// case to "not a leak" — 19 of them. That is the exact blindness
    /// SavedPdfLeakScanner was written to fix (#1049), reintroduced one layer
    /// up in the thing consuming it.</para>
    /// </summary>
    private static (bool Leaked, string Context) ClassifyByteHit(byte[] saved, string term)
    {
        if (Excise.Core.Tests.Text.Segmentation.SavedPdfLeakScanner.FindTerm(saved, term).Count == 0)
            return (false, "");

        var contexts = new List<string>();
        var realHit = false;

        void ScanSurface(string text, string surface)
        {
            var i = 0;
            while ((i = text.IndexOf(term, i, StringComparison.Ordinal)) >= 0)
            {
                var before = text[Math.Max(0, i - 300)..i];
                var benign = BenignContexts.FirstOrDefault(b => before.Contains(b, StringComparison.Ordinal));
                if (benign != null) { contexts.Add("font:" + benign.Trim('/', ' ', '[')); }
                else
                {
                    realHit = true;
                    contexts.Add(
                        before.Contains("<?xpacket", StringComparison.Ordinal) || before.Contains("rdf:", StringComparison.Ordinal) ? "xmp"
                        : before.Contains("/ActualText", StringComparison.Ordinal) || before.Contains("/Alt", StringComparison.Ordinal) ? "structure-tree"
                        : before.Contains("/Title", StringComparison.Ordinal) || before.Contains("/Author", StringComparison.Ordinal)
                          || before.Contains("/Subject", StringComparison.Ordinal) || before.Contains("/Keywords", StringComparison.Ordinal) ? "info"
                        : surface + ":raw");
                }
                i += term.Length;
            }
        }

        ScanSurface(System.Text.Encoding.Latin1.GetString(saved), "file");
        foreach (var body in Excise.Core.Tests.Text.Segmentation.SavedPdfLeakScanner.StreamBodies(saved))
            ScanSurface(body, "stream");

        // FindTerm saw it and no surface here could place it -- UTF-16BE or
        // UTF-8 only, which the Latin-1 walk above cannot see. Report it as a
        // leak with an honest label rather than losing it: an unplaceable hit
        // is still a hit, and calling it clean is the bug this comment block
        // exists because of.
        if (contexts.Count == 0) return (true, "unplaced");

        return (realHit, string.Join(",", contexts.Distinct().OrderBy(c => c, StringComparer.Ordinal)));
    }

    /// <summary>
    /// Occurrences of <paramref name="term"/> across the raw bytes and every
    /// inflated stream — the same surfaces the leak scan looks at, so the two
    /// numbers are comparable.
    /// </summary>
    private static int CountByteOccurrences(byte[] saved, string term)
    {
        var n = CountOccurrences(System.Text.Encoding.Latin1.GetString(saved), term);
        foreach (var body in Excise.Core.Tests.Text.Segmentation.SavedPdfLeakScanner.StreamBodies(saved))
            n += CountOccurrences(body, term);
        return n;
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

        // HEAD TO HEAD. Never one number per tool: the three scores trade
        // against each other, and collapsing them lets a tool that destroys
        // the document win on leak.
        _out.WriteLine("VERDICTS — what actually happened to the term");
        _out.WriteLine($"{"tool",-10} {"cases",6} {"RECOVERABLE",12} {"+residue",9} {"REMOVED",8} " +
                       $"{"unmeasured",11} {"collat>1%",10} {"qpdfBad",8}");
        foreach (var g in rows.GroupBy(r => r.Tool).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var good = g.Where(r => r.Error == null).ToList();
            if (good.Count == 0) { _out.WriteLine($"{g.Key,-10} {0,6} {g.Count(),7}"); continue; }
            var fractions = good.Select(r => r.CollateralFraction).OrderBy(x => x).ToList();
            var median = fractions[fractions.Count / 2];
            _out.WriteLine(
                $"{g.Key,-10} {good.Count,6} " +
                $"{good.Count(r => r.Verdict == nameof(Verdict.Recoverable)),12} " +
                $"{good.Count(r => r.Verdict == nameof(Verdict.RemovedWithResidue)),9} " +
                $"{good.Count(r => r.Verdict == nameof(Verdict.Removed)),8} " +
                $"{g.Count(r => r.Error != null),11} " +
                $"{good.Count(r => r.CollateralFraction > 0.01),10} " +
                $"{good.Count(r => !r.QpdfOk),8}");
            _out.WriteLine($"{"",-10} {"",6} {"median collateral " + median.ToString("P2"),-40}");
        }

        _out.WriteLine("");
        _out.WriteLine("RECOVERABLE, by how the text was read back:");
        foreach (var g in ok.Where(r => r.Verdict == nameof(Verdict.Recoverable))
                            .GroupBy(r => r.Tool).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            _out.WriteLine($"  {g.Key}: extractor={g.Count(r => r.LeakOracleText)} " +
                           $"under-a-box={g.Count(r => r.LeakBadRedactions > 0)} " +
                           $"saved-bytes={g.Count(r => r.LeakSavedBytes)}");
        }

        // WHERE THEY DISAGREE is the actionable part: a case both tools handle
        // identically teaches nothing, and a case only one of them fails is a
        // defect with a reproduction attached.
        _out.WriteLine("");
        var byCase = ok.GroupBy(r => $"{r.Corpus}/{r.Document}|{r.Term}")
                       .Where(g => g.Select(r => r.Tool).Distinct().Count() > 1);
        var disagreements = 0;
        foreach (var g in byCase)
        {
            var e = g.FirstOrDefault(r => r.Tool == "excise");
            var o = g.FirstOrDefault(r => r.Tool != "excise");
            if (e == null || o == null) continue;
            if (e.LeakOracleText == o.LeakOracleText &&
                Math.Abs(e.CollateralFraction - o.CollateralFraction) < 0.01 &&
                e.QpdfOk == o.QpdfOk) continue;
            disagreements++;
            if (disagreements <= 20)
                _out.WriteLine(
                    $"  DIFF {g.Key,-58} excise[leak={e.LeakOracleText} collat={e.CollateralFraction:P1} qpdf={e.QpdfOk}] " +
                    $"{o.Tool}[leak={o.LeakOracleText} collat={o.CollateralFraction:P1} qpdf={o.QpdfOk}]");
        }
        _out.WriteLine($"  cases where the tools differ: {disagreements}");

        _out.WriteLine("");
        foreach (var e in rows.Where(r => r.Error != null)
                              .GroupBy(r => $"{r.Tool}:{r.Error!.Split(':')[0]}")
                              .OrderByDescending(g => g.Count()).Take(12))
            _out.WriteLine($"  ERROR {e.Key} x {e.Count()}");
    }
}
