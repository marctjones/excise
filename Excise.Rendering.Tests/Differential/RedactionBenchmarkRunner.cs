using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using SkiaSharp;
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
        /// <summary>Did the ORIGINAL parse? A reject inherited from a malformed
        /// input is not a redaction fidelity defect (measured: TAMReview.pdf and
        /// Brotli-Prototype-FileA.pdf fail qpdf BEFORE redaction).</summary>
        public bool InputQpdfOk { get; init; }

        // VISUAL (#1141) — ink an INDEPENDENT renderer draws in the redaction
        // region after box-SUPPRESSED removal, so the covering rectangle does
        // not read as 100% ink. A clean removal blanks the region; residual ink
        // is a leak no text carrier covers — a vector path, a raster pixel, or
        // the #1131 white-on-black case. -1 = not measured (a competitor whose
        // box we cannot suppress, a term not on page 1, or no renderer). The
        // text axes cannot see ink; this is the visual axis made first-class.
        public double InkFractionBeforeRegion { get; init; } = -1;
        public double InkFractionInRegion { get; init; } = -1;

        // RENDER FIDELITY — does the SURVIVING content still render correctly?
        // Fraction of pixels OUTSIDE the redacted region that differ between the
        // before and after render (an independent renderer). Low = the rest of
        // the page is untouched; high = the redaction mispositioned text, broke a
        // font, or drew over neighbours (#942/#1100/#167 as a RENDER defect, which
        // qpdf-validity and text-extraction both miss). Per-tool. -1 = not measured.
        public double SurvivingRenderDelta { get; init; } = -1;

        // VISUAL READABILITY — is the secret still READABLE IN PIXELS after
        // redaction? OCR the term's region in the rendered output: 1 = the term
        // is legible (a visual leak the text axes cannot see — vector/raster
        // residue, or a transparent cover), 0 = not legible, -1 = not measured.
        // Per-tool, on the output AS IT SHIPS.
        public int VisualTermReadable { get; init; } = -1;

        // STRUCTURAL (#1117) — structures a text diff cannot see (pages, links,
        // bookmarks, form fields, attachments, PDF/A). "" = conserved; otherwise
        // the specific drops, e.g. "bookmarks 3->0, pdf/a lost". Redaction should
        // remove the term from carriers, not the carriers themselves.
        public string StructuralDropped { get; init; } = "";

        // SURVIVING-CONTENT CONSERVATION (#1157) — the conservation law: a word
        // present in the input that did NOT contain the redacted term must
        // survive unchanged in the output. Checked = untouched word occurrences
        // in the input; Damaged = how many failed to appear (same spelling, same
        // count) in the output — corruption the collateral axis misses because it
        // counts character LOSS and a duplicated ligature (#1156) ADDS characters.
        // Text-based, tool-agnostic, graded against the independent extractor on
        // both sides. Damaged>0 is surviving-content corruption.
        public int SurvivingWordsChecked { get; init; }
        public int SurvivingWordsDamaged { get; init; }
        public string SurvivingWordsDamagedExamples { get; init; } = "";

        public string? Error { get; init; }
    }

    /// <summary>
    /// The tools under comparison. excise is one entry, not the centre — a
    /// benchmark that can only measure the tool that wrote it is a
    /// self-assessment.
    /// </summary>
    // #1121 fairness rules, decided up front (written into the harness before it
    // is run, not after seeing results):
    //   1. Every tool gets the SAME target term, translated only as its API needs.
    //   2. Every tool runs in its DOCUMENTED BEST mode — pymupdf removes touched
    //      images, itext uses pdfSweep autoSweep, raster blacks the region in
    //      pixel space (each adapter's header records which). A tool run in a
    //      weaker mode than its docs recommend measures the harness author, not
    //      the tool.
    //   3. A crash is a RECORDED result (RunAdapter returns null -> a row with an
    //      Error), never an exclusion — dropping the documents a tool fails on is
    //      how a benchmark flatters the tool it measures.
    //   4. Only OPEN-SOURCE tools are wired: PyMuPDF, iText pdfSweep, the raster
    //      baseline (#1121). Proprietary Acrobat is deliberately out.
    // The assertion side stays one-directional (excise <= references on
    // collateral); "excise must match tool X" would elect a redactor (#1015/#932).
    private static IEnumerable<string> Tools()
    {
        yield return "excise";
        if (PyMuPdfPython() != null)
        {
            yield return "pymupdf";
            yield return "raster";   // #1121 — the trade-off anchor (rasterises everything)
        }
        if (ItextRunnable()) yield return "itext";   // #1121 — the dedicated redactor
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
    /// <summary>iText 7 + pdfSweep jars (scripts/download-itext.sh) and a java.</summary>
    private static bool ItextRunnable() =>
        ItextClasspath() != null
        && File.Exists(Path.Combine(RepoRoot(), "scripts", "ItextRedactor.java"))
        && PdfBoxReferenceRedactor.IsAvailable;   // reuses the #1042 java resolution

    private static string? ItextClasspath()
    {
        var dir = Path.Combine(RepoRoot(), "tools", "vendor", "itext");
        if (!Directory.Exists(dir)) return null;
        var jars = Directory.GetFiles(dir, "*.jar");
        return jars.Length > 0 ? string.Join(Path.PathSeparator, jars) : null;
    }

    private static int? RunAdapter(string tool, string src, string dst, string term)
    {
        System.Diagnostics.ProcessStartInfo psi;
        if (tool == "itext")
        {
            // iText is a Java tool (#1121) — dispatch java + the pdfSweep driver,
            // not the python interpreter the others use.
            var java = System.Environment.GetEnvironmentVariable("EXCISE_JAVA_COMMAND")
                       ?? (File.Exists("/opt/homebrew/opt/openjdk/bin/java") ? "/opt/homebrew/opt/openjdk/bin/java" : "java");
            var cp = ItextClasspath();
            var driver = Path.Combine(RepoRoot(), "scripts", "ItextRedactor.java");
            if (cp == null || !File.Exists(driver)) return null;
            psi = new System.Diagnostics.ProcessStartInfo(java)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in new[] { "--class-path", cp, driver, src, dst, term }) psi.ArgumentList.Add(a);
            return RunAndParse(psi);
        }

        var python = PyMuPdfPython();
        if (python == null) return null;
        var script = Path.Combine(RepoRoot(), "scripts", "benchmark-adapters", $"redact-{tool}.py");
        if (!File.Exists(script)) return null;

        try
        {
            psi = new System.Diagnostics.ProcessStartInfo(python)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in new[] { script, src, dst, term }) psi.ArgumentList.Add(a);
            return RunAndParse(psi);
        }
        catch { return null; }
    }

    /// <summary>
    /// Run an adapter subprocess and return the occurrence count it reports as
    /// the LAST integer on stdout (Java tools prefix logging noise), or null on
    /// any failure — which the caller RECORDS, never silently skips.
    /// </summary>
    private static int? RunAndParse(System.Diagnostics.ProcessStartInfo psi)
    {
        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;
            // #1083: drain concurrently, bound the wait -- ReadToEnd before
            // WaitForExit hangs on a child that never closes stdout.
            var outT = proc.StandardOutput.ReadToEndAsync();
            var errT = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(120_000)) { try { proc.Kill(true); } catch { } return null; }
            var stdout = outT.GetAwaiter().GetResult();
            errT.GetAwaiter().GetResult();
            if (proc.ExitCode != 0) return null;
            var m = System.Text.RegularExpressions.Regex.Matches(stdout, @"\b(\d+)\b");
            return m.Count > 0 ? int.Parse(m[m.Count - 1].Groups[1].Value) : 0;
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

        // Optional bounds for a tractable baseline (the render+OCR axes are
        // heavy): REDACTION_BENCH_CORPORA restricts the corpus set,
        // REDACTION_BENCH_TAKE caps files per corpus. Deterministic either way —
        // the file list is still ordered-then-taken, so runs stay comparable.
        var onlyCorpora = Environment.GetEnvironmentVariable("REDACTION_BENCH_CORPORA")
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var takeCap = int.TryParse(Environment.GetEnvironmentVariable("REDACTION_BENCH_TAKE"), out var tc) ? tc : int.MaxValue;

        foreach (var (corpus, take) in Corpora)
        {
            if (onlyCorpora is { Length: > 0 } && !onlyCorpora.Contains(corpus)) continue;
            var dir = Path.Combine(root, "test-pdfs", corpus);
            if (!Directory.Exists(dir)) { _out.WriteLine($"absent: {corpus}"); continue; }

            // Ordered, then taken — the case list must be reproducible, or two
            // runs cannot be compared and the numbers mean nothing.
            var files = Directory.GetFiles(dir, "*.pdf", SearchOption.AllDirectories)
                                 .OrderBy(f => f, StringComparer.Ordinal)
                                 .Take(Math.Min(take, takeCap));

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
                // A redacted output vastly larger than its input is a defect in
                // its own right (a ≤7MB input ballooning past 1GB crashed the
                // baseline twice). Name it in the log so it is diagnosable, not
                // just survivable.
                var inLen = new FileInfo(path).Length;
                if (savedBytes.LongLength > 256L * 1024 * 1024 || savedBytes.LongLength > inLen * 20)
                    _out.WriteLine($"WARN oversized-output {tool} {corpus}/{name} [{term}]: " +
                        $"in={inLen / 1048576.0:F1}MB out={savedBytes.LongLength / 1048576.0:F1}MB");
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
            // #fidelity: only excise-CAUSED invalidity counts. Check the input
            // too, so a reject inherited from an already-malformed source is not
            // charged against the tool.
            var inputQpdf = QpdfReferenceTool.Check(path);

            // ── VISUAL (#1141): is the region blank once the box is off? ──
            var (inkBefore, inkAfter) = MeasureInkAxis(path, term, tool);

            // Per-tool render axes: does the SURVIVING page still render correctly,
            // and is the secret still READABLE in pixels?
            var survivingRenderDelta = MeasureSurvivingRenderDelta(path, output, term);
            var visualReadable = MeasureVisualReadable(path, output, term);

            // ── SURVIVING-CONTENT CONSERVATION (#1157): did untargeted words
            //    survive UNCHANGED? Catches corruption (ligature dup #1156,
            //    substitution) the loss-based collateral axis nets to ~zero.
            var (survChecked, survDamaged, survExamples) =
                MeasureSurvivingWordFidelity(before, after, term);

            // ── STRUCTURAL (#1117): did the redaction drop a structure the
            //    text diff cannot see? Best-effort — a parse failure on either
            //    side is not a structural finding.
            var structuralDropped = "";
            try
            {
                using var sIn = PdfDocument.Open(path);
                using var sOut = PdfDocument.Open(output);
                structuralDropped = StructuralInventory.Of(sIn).DroppedVersus(StructuralInventory.Of(sOut));
            }
            catch { /* leave "" — inventory needs both sides to parse */ }

            // ── RESIDUE: did the layout keep the hole? ────────────────────
            // Page 1 only. This is a sampled signal, not a per-occurrence one:
            // a document whose page 1 reflows and whose page 4 does not will
            // read as "closed up", and the honest fix for that is #1116's
            // per-removal measurement rather than pretending this is exact.
            var glyphsBefore = MutoolGlyphPositions.ExtractPage(path, 1);
            var glyphsAfter = MutoolGlyphPositions.ExtractPage(output, 1);
            var gapPreserved = MutoolGlyphPositions.LayoutGapPreserved(glyphsBefore, glyphsAfter);

            // A term still LEGIBLE in the rendered pixels is recoverable too — the
            // visual leak the text oracles cannot see.
            var recoverable = leakText || badRedactions > 0 || leakBytes || visualReadable == 1;
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
                InputQpdfOk = inputQpdf?.Success ?? false,
                InkFractionBeforeRegion = inkBefore,
                InkFractionInRegion = inkAfter,
                StructuralDropped = structuralDropped,
                SurvivingRenderDelta = survivingRenderDelta,
                VisualTermReadable = visualReadable,
                SurvivingWordsChecked = survChecked,
                SurvivingWordsDamaged = survDamaged,
                SurvivingWordsDamagedExamples = survExamples,
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
        if (SavedPdfLeakScanner.FindTerm(saved, term).Count == 0)
            return (false, "");

        var contexts = new List<string>();
        var realHit = false;

        void ScanSurface(string text, string surface)
        {
            var i = 0;
            while ((i = text.IndexOf(term, i, StringComparison.Ordinal)) >= 0)
            {
                // A term that IS a PDF name token (/Form, /Type — §7.3.5) is
                // STRUCTURE, not surviving content: `/Subtype /Form` matches the
                // term "Form", but scrubbing it would corrupt the XObject. It's a
                // benchmark false positive, not a leak (#1155). Record it so the
                // "unplaced" fallback below doesn't then treat it as one.
                if (i > 0 && text[i - 1] == '/') { contexts.Add("name-token"); i += term.Length; continue; }
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

        // Decoding the whole file to a string overflows past ~1GB (Latin1
        // GetString). FindTerm already proved the leak byte-safely; on an
        // oversized output, skip the raw-surface context walk and let the
        // stream bodies (inflated individually, each far smaller) place it —
        // or fall through to the honest "unplaced" label below.
        const long RawScanCap = 256L * 1024 * 1024;
        if (saved.LongLength <= RawScanCap)
            ScanSurface(System.Text.Encoding.Latin1.GetString(saved), "file");
        else
            contexts.Add("oversized-file");
        foreach (var body in SavedPdfLeakScanner.StreamBodies(saved))
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
        // Count in the ENCODED BYTES of the raw file, not a decoded string of it:
        // Latin1Encoding.GetString overflows on a >1GB corpus file (a real crash
        // the baseline hit). The per-stream bodies are inflated individually and
        // are each far smaller, so they stay string-based.
        var n = CountByteOccurrences(saved, System.Text.Encoding.Latin1.GetBytes(term));
        foreach (var body in SavedPdfLeakScanner.StreamBodies(saved))
            n += CountOccurrences(body, term);
        return n;
    }

    /// <summary>Case-insensitive (ASCII-folded) byte-level occurrence count —
    /// size-safe on multi-hundred-MB haystacks where decoding to a string would
    /// overflow. Non-overlapping, matching <see cref="CountOccurrences"/>.</summary>
    private static int CountByteOccurrences(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0) return 0;
        static byte Fold(byte b) => (byte)(b >= (byte)'A' && b <= (byte)'Z' ? b + 32 : b);
        int n = 0;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
                if (Fold(haystack[i + j]) != Fold(needle[j])) { ok = false; break; }
            if (ok) { n++; i += needle.Length - 1; }
        }
        return n;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        { n++; i += needle.Length; }
        return n;
    }

    /// <summary>
    /// #1141 — ink an independent renderer draws in the redaction region, before
    /// and after a BOX-SUPPRESSED removal. Box-suppressed so the covering
    /// rectangle does not read as 100% ink and hide what leaks around it. Only
    /// excise (we cannot turn off a competitor's box), only page 1, only when a
    /// renderer is installed; otherwise (-1, -1), the axis's "not measured".
    /// </summary>
    internal static (double before, double after) MeasureInkAxis(
        string path, string term, string tool)
    {
        if (tool != "excise") return (-1, -1);
        if (!GhostscriptReferenceRenderer.IsAvailable) return (-1, -1);
        try
        {
            IReadOnlyList<PdfRectangle> regions;
            double pageHeight;
            using (var doc = PdfDocument.Open(path))
            {
                if (doc.PageCount < 1) return (-1, -1);
                var page = doc.GetPage(1);
                pageHeight = page.Height;
                regions = AllMatchBoxesOnPage1(page, term);
                if (regions.Count == 0) return (-1, -1);   // term is not on page 1
            }

            using var before = GhostscriptReferenceRenderer.RenderPage(path, 1, dpi: 150);
            if (before == null) return (-1, -1);

            var tmp = Path.Combine(Path.GetTempPath(), $"excise-ink-{Guid.NewGuid():N}.pdf");
            try
            {
                using (var doc = PdfDocument.Open(path))
                {
                    doc.RedactText(term, drawBlackRect: false);
                    doc.Save(tmp);
                }
                using var after = GhostscriptReferenceRenderer.RenderPage(tmp, 1, dpi: 150);
                // Report the WORST occurrence: the region left with the most ink
                // after removal. Residue on any occurrence is residue — sampling
                // only the first would miss a glyph left standing further down.
                var worstBefore = 0.0; var worstAfter = 0.0;
                foreach (var region in regions)
                {
                    var a = after == null ? -1 : InkFractionIn(after, region, pageHeight);
                    if (a > worstAfter) { worstAfter = a; worstBefore = InkFractionIn(before, region, pageHeight); }
                }
                if (after == null) return (InkFractionIn(before, regions[0], pageHeight), -1);
                // All occurrences cleanly removed ⇒ worstAfter stayed 0; still
                // report a representative before so the pair is meaningful.
                if (worstBefore == 0.0) worstBefore = InkFractionIn(before, regions[0], pageHeight);
                return (worstBefore, worstAfter);
            }
            finally { try { File.Delete(tmp); } catch { /* best effort */ } }
        }
        catch { return (-1, -1); }
    }

    /// <summary>
    /// RENDER-FIDELITY axis — the fraction of pixels OUTSIDE the redacted region
    /// that changed between the before and after render. The redacted region
    /// itself is masked (change there is the point); everything else must be
    /// identical, or the redaction damaged the surviving render. Per-tool, on the
    /// tool's actual output. -1 when not measurable.
    /// </summary>
    internal static double MeasureSurvivingRenderDelta(string inputPath, string outputPath, string term)
    {
        if (!GhostscriptReferenceRenderer.IsAvailable) return -1;
        try
        {
            IReadOnlyList<PdfRectangle> regions; double pageHeight;
            using (var doc = PdfDocument.Open(inputPath))
            {
                if (doc.PageCount < 1) return -1;
                var page = doc.GetPage(1);
                pageHeight = page.Height;
                regions = AllMatchBoxesOnPage1(page, term);
                if (regions.Count == 0) return -1;
            }
            using var before = GhostscriptReferenceRenderer.RenderPage(inputPath, 1, dpi: 150);
            using var after = GhostscriptReferenceRenderer.RenderPage(outputPath, 1, dpi: 150);
            if (before == null || after == null) return -1;
            // A changed page size is itself a surviving-render change (a tool that
            // reflowed or rasterised to a different geometry).
            if (before.Width != after.Width || before.Height != after.Height) return 1.0;

            const double scale = 150.0 / 72.0;
            // Mask EVERY occurrence's covering box, not just the first — the other
            // occurrences are legitimate redactions, not surviving-content change.
            var masks = new List<(int x0, int y0, int x1, int y1)>();
            foreach (var region in regions)
            {
                var m = region.Normalize();
                masks.Add(((int)(m.Left * scale) - 2, (int)((pageHeight - m.Top) * scale) - 2,
                           (int)(m.Right * scale) + 2, (int)((pageHeight - m.Bottom) * scale) + 2));
            }
            bool Masked(int x, int y)
            {
                foreach (var (x0, y0, x1, y1) in masks)
                    if (x >= x0 && x <= x1 && y >= y0 && y <= y1) return true;
                return false;
            }

            long diff = 0, total = 0;
            for (int y = 0; y < before.Height; y++)
            for (int x = 0; x < before.Width; x++)
            {
                if (Masked(x, y)) continue;   // masked redaction region(s)
                total++;
                var a = before.GetPixel(x, y);
                var b = after.GetPixel(x, y);
                if (Math.Abs(a.Red - b.Red) > 24 || Math.Abs(a.Green - b.Green) > 24 || Math.Abs(a.Blue - b.Blue) > 24)
                    diff++;
            }
            return total == 0 ? 0 : (double)diff / total;
        }
        catch { return -1; }
    }

    /// <summary>
    /// SURVIVING-CONTENT CONSERVATION axis (#1157) — every word in the input that
    /// did NOT itself contain the redacted term must survive unchanged in the
    /// output. A word that vanishes or is altered (ligature duplication #1156,
    /// substitution, a dropped glyph) is surviving-content corruption the
    /// collateral axis misses: that axis counts character LOSS, and a duplicated
    /// ligature ADDS characters, netting ~zero.
    ///
    /// <para>Text-based and tool-agnostic — grades any redactor's output text
    /// against the input text, with the independent extractor (mutool) as the
    /// oracle on BOTH sides (no self-oracle). Returns (Checked, Damaged): Checked
    /// = untouched word occurrences in the input, Damaged = how many did not
    /// appear with the same spelling and count in the output. Order-independent
    /// (multiset), so a multi-column reflow in read order does not read as damage.
    /// Damaged &gt; 0 is corruption.</para>
    /// </summary>
    internal static (int Checked, int Damaged, string Examples) MeasureSurvivingWordFidelity(
        string before, string after, string term)
    {
        if (string.IsNullOrEmpty(before) || string.IsNullOrEmpty(term))
            return (0, 0, "");

        // Char ranges the term occupies in the input. A word overlapping one was
        // touched by the redaction and is exempt from conservation — it was
        // legitimately removed or split.
        var touched = new List<(int Start, int End)>();
        var ti = 0;
        while ((ti = before.IndexOf(term, ti, StringComparison.OrdinalIgnoreCase)) >= 0)
        { touched.Add((ti, ti + term.Length)); ti += Math.Max(1, term.Length); }

        bool Overlaps(int s, int e)
        {
            foreach (var (ts, te) in touched)
                if (s < te && ts < e) return true;
            return false;
        }

        // Untouched-word multiset from the input. A word is a maximal run of
        // letters/digits; length >= 2 keeps single-char extraction noise out.
        var untouched = new Dictionary<string, int>();
        foreach (var (word, s, e) in Words(before))
        {
            if (word.Length < 2 || Overlaps(s, e)) continue;
            var k = word.ToLowerInvariant();
            untouched[k] = untouched.GetValueOrDefault(k) + 1;
        }
        if (untouched.Count == 0) return (0, 0, "");

        var present = new Dictionary<string, int>();
        foreach (var (word, _, _) in Words(after))
        {
            if (word.Length < 2) continue;
            var k = word.ToLowerInvariant();
            present[k] = present.GetValueOrDefault(k) + 1;
        }

        int checkedCount = 0, damaged = 0;
        var examples = new List<string>();
        foreach (var (w, cB) in untouched)
        {
            checkedCount += cB;
            var cA = present.GetValueOrDefault(w);
            if (cA < cB)
            {
                damaged += cB - cA;
                if (examples.Count < 8) examples.Add($"{w}x{cB - cA}");
            }
        }
        return (checkedCount, damaged, string.Join(",", examples));
    }

    private static IEnumerable<(string Word, int Start, int End)> Words(string text)
    {
        int i = 0, n = text.Length;
        while (i < n)
        {
            if (char.IsLetterOrDigit(text[i]))
            {
                int s = i;
                while (i < n && char.IsLetterOrDigit(text[i])) i++;
                yield return (text[s..i], s, i);
            }
            else i++;
        }
    }

    /// <summary>
    /// VISUAL-READABILITY axis — OCR the term's region in the RENDERED output. If
    /// tesseract can read the term back, the secret survives visually even when
    /// no text carrier holds it (vector/raster residue, or a see-through cover).
    /// Per-tool, on the output as it ships. Returns 1 legible, 0 not, -1 not
    /// measurable.
    /// </summary>
    internal static int MeasureVisualReadable(string inputPath, string outputPath, string term)
    {
        if (!GhostscriptReferenceRenderer.IsAvailable) return -1;
        var tess = ResolveTesseract();
        if (tess == null) return -1;
        try
        {
            IReadOnlyList<PdfRectangle> regions; double pageHeight;
            using (var doc = PdfDocument.Open(inputPath))
            {
                if (doc.PageCount < 1) return -1;
                var page = doc.GetPage(1);
                pageHeight = page.Height;
                regions = AllMatchBoxesOnPage1(page, term);
                if (regions.Count == 0) return -1;
            }
            using var after = GhostscriptReferenceRenderer.RenderPage(outputPath, 1, dpi: 200);
            if (after == null) return -1;

            const double scale = 200.0 / 72.0;
            // OCR EVERY occurrence's region, not just the first: a secret removed
            // in one place but left legible in another is still a visual leak.
            // Capped so a term appearing dozens of times on page 1 doesn't spawn
            // dozens of tesseract calls; a legible secret is almost always caught
            // in the first few, and the cap is logged as a limitation, not hidden.
            const int MaxRegions = 12;
            var measuredAny = false;
            foreach (var region in regions.Take(MaxRegions))
            {
                var m = region.Normalize();
                // Pad generously — OCR needs whitespace around the glyphs to segment.
                int x0 = Math.Max(0, (int)(m.Left * scale) - 6);
                int x1 = Math.Min(after.Width - 1, (int)(m.Right * scale) + 6);
                int y0 = Math.Max(0, (int)((pageHeight - m.Top) * scale) - 6);
                int y1 = Math.Min(after.Height - 1, (int)((pageHeight - m.Bottom) * scale) + 6);
                if (x1 <= x0 || y1 <= y0) continue;

                using var crop = new SKBitmap(x1 - x0 + 1, y1 - y0 + 1);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        crop.SetPixel(x - x0, y - y0, after.GetPixel(x, y));

                var readable = OcrRegionContains(tess, crop, term);
                if (readable == 1) return 1;          // legible anywhere ⇒ leak
                if (readable == 0) measuredAny = true; // ran clean here
            }
            return measuredAny ? 0 : -1;
        }
        catch { return -1; }
    }

    /// <summary>Encode <paramref name="crop"/> to PNG, OCR it, and report whether
    /// <paramref name="term"/> is legible. 1 legible, 0 not, -1 the OCR failed.</summary>
    private static int OcrRegionContains(string tess, SKBitmap crop, string term)
    {
        var png = Path.Combine(Path.GetTempPath(), $"vis-{Guid.NewGuid():N}.png");
        try
        {
            using (var img = SKImage.FromBitmap(crop))
            using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
            using (var fs = File.Create(png))
                data.SaveTo(fs);

            var psi = new System.Diagnostics.ProcessStartInfo(tess)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { png, "stdout", "--psm", "6" }) psi.ArgumentList.Add(a);
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return -1;
            var outT = proc.StandardOutput.ReadToEndAsync();
            proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(30_000)) { try { proc.Kill(true); } catch { } return -1; }
            var text = outT.GetAwaiter().GetResult();
            return text.Contains(term, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }
        catch { return -1; }
        finally { try { File.Delete(png); } catch { } }
    }

    private static string? ResolveTesseract()
    {
        foreach (var c in new[] { Environment.GetEnvironmentVariable("EXCISE_TESSERACT"), "tesseract", "/opt/homebrew/bin/tesseract" })
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(c, "--version")
                { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
                if (p == null) continue;
                p.WaitForExit(10_000);
                if (p.ExitCode == 0) return c;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// EVERY occurrence of <paramref name="term"/> on page 1. All three render
    /// axes mask/scan them all: a term redacted in N places leaves N covering
    /// boxes, and masking only one makes the other N-1 read as surviving-content
    /// damage (the false 4.8% on a 41×-"COVID" page). Lines by baseline, ordered
    /// by x — the same grouping the residue engine uses — so a term split across
    /// a TJ array still unions to one box.
    /// </summary>
    private static IReadOnlyList<PdfRectangle> AllMatchBoxesOnPage1(PdfPage page, string term)
    {
        var boxes = new List<PdfRectangle>();
        foreach (var line in page.Letters
                     .GroupBy(l => Math.Round(l.GlyphRectangle.Bottom, 0))
                     .OrderByDescending(g => g.Key))
        {
            var ordered = line.OrderBy(l => l.GlyphRectangle.Left).ToList();
            var text = string.Concat(ordered.Select(l => l.Value));
            var from = 0;
            while (true)
            {
                var idx = text.IndexOf(term, from, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;
                var run = ordered.Skip(idx).Take(term.Length).ToList();
                from = idx + Math.Max(1, term.Length);
                if (run.Count == 0) continue;
                boxes.Add(new PdfRectangle(
                    run.Min(l => l.GlyphRectangle.Left), run.Min(l => l.GlyphRectangle.Bottom),
                    run.Max(l => l.GlyphRectangle.Right), run.Max(l => l.GlyphRectangle.Top)));
            }
        }
        return boxes;
    }

    private static double InkFractionIn(SKBitmap bmp, PdfRectangle box, double pageHeight)
    {
        const double scale = 150.0 / 72.0;
        int x0 = Math.Max(0, (int)(box.Left * scale));
        int x1 = Math.Min(bmp.Width - 1, (int)(box.Right * scale));
        // PDF y is bottom-up; raster y is top-down.
        int y0 = Math.Max(0, (int)((pageHeight - box.Top) * scale));
        int y1 = Math.Min(bmp.Height - 1, (int)((pageHeight - box.Bottom) * scale));
        if (x1 <= x0 || y1 <= y0) return 0;

        int ink = 0, total = 0;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var p = bmp.GetPixel(x, y);
            total++;
            if (p.Red < 200 || p.Green < 200 || p.Blue < 200) ink++;
        }
        return total == 0 ? 0 : (double)ink / total;
    }

    private void WriteReport(string root, List<Row> rows)
    {
        var dir = Path.Combine(root, "logs", "redaction-benchmark");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "results.jsonl");
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllLines(path, rows.Select(r => JsonSerializer.Serialize(r, opts)));
        _out.WriteLine($"rows → {path}");

        // #1123: turn the rows into the FAILURE TAXONOMY — named classes with a
        // stratum and a percentage, read back from the results.jsonl so the
        // scorecard is the same one anyone can re-run over the file.
        var scoreRows = RedactionScorecard.Parse(File.ReadAllLines(path));
        var cov = RedactionScorecard.CoverageOf(scoreRows);
        _out.WriteLine("");
        _out.WriteLine($"SCORECARD (#1123) — measured {cov.Measured}, errored {cov.Errored}, " +
                       $"tools [{string.Join(", ", cov.ToolsSeen)}]");
        var taxonomy = RedactionScorecard.FailureTaxonomy(scoreRows);
        if (taxonomy.Count == 0)
            _out.WriteLine("  no failure classes occurred on this run");
        foreach (var line in taxonomy) _out.WriteLine($"  {line}");
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
                $"{good.Count(r => r.InputQpdfOk && !r.QpdfOk),8}");
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

        // VISUAL AXIS (#1141): residual ink an independent renderer draws in the
        // region after box-suppressed removal. Blank = clean; ink = a leak no
        // text axis can see (vector/raster/white-on-black). Only measured rows
        // (excise, page-1 term, renderer present) count.
        var inkMeasured = ok.Where(r => r.InkFractionInRegion >= 0).ToList();
        if (inkMeasured.Count > 0)
        {
            _out.WriteLine("");
            var visualLeaks = inkMeasured.Count(r => r.InkFractionInRegion > 0.02);
            _out.WriteLine($"VISUAL — region ink after box-suppressed removal " +
                           $"(measured {inkMeasured.Count} rows): {visualLeaks} with residual ink >2%");
            foreach (var r in inkMeasured.Where(r => r.InkFractionInRegion > 0.02)
                                         .OrderByDescending(r => r.InkFractionInRegion).Take(15))
                _out.WriteLine(
                    $"  INK {r.Corpus}/{r.Document}|{r.Term,-16} " +
                    $"{r.InkFractionBeforeRegion:P1} → {r.InkFractionInRegion:P1} " +
                    $"(text axis says leak={r.LeakOracleText})");
        }

        // STRUCTURAL (#1117): redactions that dropped a structure the text diff
        // cannot see — a bookmark tree, a link, a form field, an attachment, a
        // PDF/A claim.
        var structuralDrops = ok.Where(r => !string.IsNullOrEmpty(r.StructuralDropped)).ToList();
        _out.WriteLine("");
        _out.WriteLine($"STRUCTURAL — redactions that dropped an unseen structure: {structuralDrops.Count}");
        foreach (var r in structuralDrops.Take(20))
            _out.WriteLine($"  STRUCT {r.Tool} {r.Corpus}/{r.Document}|{r.Term}: {r.StructuralDropped}");

        // RENDER FIDELITY — did the redaction damage how the SURVIVING page looks?
        _out.WriteLine("");
        _out.WriteLine("RENDER FIDELITY — surviving-content pixel change (masking the redacted region), per tool:");
        foreach (var g in ok.Where(r => r.SurvivingRenderDelta >= 0).GroupBy(r => r.Tool).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var vals = g.Select(r => r.SurvivingRenderDelta).OrderBy(x => x).ToList();
            var median = vals[vals.Count / 2];
            var damaged = g.Count(r => r.SurvivingRenderDelta > 0.02);
            _out.WriteLine($"  {g.Key,-10} median {median:P2}   {damaged}/{vals.Count} with >2% surviving-render change");
        }

        // VISUAL READABILITY — is the secret still legible in the rendered pixels?
        var visLeaks = ok.Where(r => r.VisualTermReadable == 1).ToList();
        _out.WriteLine("");
        _out.WriteLine($"VISUAL READABILITY — the term is still OCR-legible in the output despite redaction: {visLeaks.Count}");
        foreach (var g in visLeaks.GroupBy(r => r.Tool).OrderBy(g => g.Key, StringComparer.Ordinal))
            _out.WriteLine($"  {g.Key}: {g.Count()} case(s) where the secret renders readable");

        // SURVIVING-CONTENT CONSERVATION (#1157) — did untargeted words survive
        // UNCHANGED? Damaged>0 is corruption (ligature dup #1156, substitution, a
        // dropped glyph) the loss-based collateral axis nets to ~zero. Per tool.
        // A tool that produces no extractable text (raster) reads as fully
        // damaged — the honest statement that it destroys the text layer.
        var survMeasured = ok.Where(r => r.SurvivingWordsChecked > 0).ToList();
        _out.WriteLine("");
        _out.WriteLine("SURVIVING-CONTENT CONSERVATION — untargeted words that did NOT survive unchanged, per tool:");
        foreach (var g in survMeasured.GroupBy(r => r.Tool).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var withDamage = g.Where(r => r.SurvivingWordsDamaged > 0).ToList();
            var totalDamaged = g.Sum(r => r.SurvivingWordsDamaged);
            _out.WriteLine($"  {g.Key,-10} {withDamage.Count}/{g.Count()} cases corrupt surviving text " +
                           $"({totalDamaged} word-occurrences total)");
            foreach (var r in withDamage.OrderByDescending(r => r.SurvivingWordsDamaged).Take(8))
                _out.WriteLine($"     CORRUPT {r.Corpus}/{r.Document}|{r.Term,-16} " +
                               $"{r.SurvivingWordsDamaged} lost/altered: {r.SurvivingWordsDamagedExamples}");
        }

        // COUNT ACCURACY (#1101) — does the tool's REPORTED removal count match
        // the independent oracle's occurrence count? excise over-reports (12 for
        // a term mutool counts 9) when a run is OVERPRINTED — page.Letters holds
        // the coincident duplicate glyphs, so the same occurrence is found more
        // than once. The count is the one number `excise redact` prints and a
        // user acts on; an inflated count is a silent trust defect that no leak,
        // collateral, or conservation axis catches. Only rows where the oracle
        // sees the term (OracleBefore > 0) and the tool reports a removal.
        var countRows = ok.Where(r => r.OracleBefore > 0 && r.Reported > 0).ToList();
        _out.WriteLine("");
        _out.WriteLine("COUNT ACCURACY (#1101) — reported removals vs independent occurrence count, per tool:");
        foreach (var g in countRows.GroupBy(r => r.Tool).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var mismatch = g.Where(r => r.Reported != r.OracleBefore).ToList();
            var over = mismatch.Count(r => r.Reported > r.OracleBefore);
            _out.WriteLine($"  {g.Key,-10} {mismatch.Count}/{g.Count()} cases reported != oracle " +
                           $"({over} OVER-reported — the #1101 inflation signature)");
            foreach (var r in mismatch.OrderByDescending(r => Math.Abs(r.Reported - r.OracleBefore)).Take(8))
                _out.WriteLine($"     COUNT {r.Corpus}/{r.Document}|{r.Term,-16} " +
                               $"reported {r.Reported} vs oracle {r.OracleBefore} " +
                               $"(delta {r.Reported - r.OracleBefore:+0;-0})");
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
