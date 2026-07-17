using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Xunit;
using Pdfe.Rendering.Differential;

namespace Pdfe.Rendering.Tests.Differential;

/// <summary>
/// Corpus-wide text-extraction parity report (#645) — the measurement #513
/// needs before it touches the shared font resolver. For every page of every
/// PDF in test-pdfs/smoke (downloaded government forms) and
/// test-pdfs/sample-pdfs (checked-in edge-case fixtures, including the CJK
/// case below), extracts with pdfe and with mutool and records:
///   - coverage ratio (pdfe Unicode letter/digit count ÷ mutool's) — catches
///     UNDER-extraction (redaction can't remove what it can't read: #637).
///     Deliberately counted on the RAW extraction via <see cref="char.IsLetterOrDigit(char)"/>,
///     not the ASCII-folded <see cref="Normalize"/> form used for similarity below:
///     folding to ASCII before counting would silently discard every non-Latin
///     character from BOTH sides and hide exactly the failure #645 names —
///     CJK/CID text extracting as empty (see RealWorldSearchTests.CjkFixture_*).
///     A coverage metric that can't see non-Latin loss is not a coverage metric.
///   - content similarity (bigram Jaccard on the ASCII-folded form, same
///     metric as <see cref="TextExtractionDifferentialTests"/>) — catches
///     WRONG content surviving alongside or instead of the real text
///     (marked-content /Artifact leaking into extraction, page 1 of
///     irs-1040-instructions.pdf). Folding is fine here because similarity's
///     job is fuzzy content matching, not counting what exists.
///
/// This test is the GENERATOR, not the gate. It requires mutool and the
/// smoke corpus, both of which CI's PR runners lack (see the Differential
/// exclusion in ci.yml) — exactly the kind of dependency that must never be
/// allowed to define whether the *gate* passes (see the no-self-oracle
/// principle and #619 skip-budget doc). The actual enforcement is
/// scripts/check-extraction-parity.sh, which is required by
/// docs/RELEASE_CHECKLIST.md and fails LOUDLY — not silently skips — when
/// mutool or the corpus is unavailable.
/// </summary>
public sealed class ExtractionParityTests
{
    private readonly ITestOutputHelper _output;

    public ExtractionParityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Pages scoring below this coverage or similarity go into the report's
    /// worklist, ordered worst-first, with the page's font resources attached
    /// so #513-#515 have a blast-radius-ordered starting point.
    /// </summary>
    private const double WorklistThreshold = 0.95;

    [Fact]
    public void GenerateExtractionParityReport()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable,
            "mutool not on PATH — install mupdf-tools to generate the extraction parity report");

        var root = LocateRepoRoot();
        Assert.SkipWhen(root == null, "could not locate repo root (pdfe.sln not found above AppContext.BaseDirectory)");

        // test-pdfs/smoke: downloaded, gitignored, curated real-world government
        // PDFs (scripts/download-smoke-corpus.sh) — the bulk of the measurement.
        //
        // test-pdfs/sample-pdfs: checked into git (see the gitignore exception
        // for this subfolder), purpose-built edge-case fixtures. Critically,
        // multilingual-noto-cjk.pdf is #645's SECOND named blind spot — the
        // Type0/CIDFontType2 "CJK runs extract as empty" case referenced in
        // RealWorldSearchTests.CjkFixture_Search_FindsLatinWord. Without this
        // directory the smoke corpus (all-ASCII government forms) cannot
        // exercise that failure mode at all, and the coverage number would
        // read clean for a reason that has nothing to do with pdfe being fixed.
        var corpusDirs = new[]
        {
            Path.Combine(root!, "test-pdfs", "smoke"),
            Path.Combine(root!, "test-pdfs", "sample-pdfs"),
        };

        var pdfPaths = corpusDirs
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.pdf"))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        Assert.SkipWhen(pdfPaths.Count == 0,
            "no corpus PDFs found — run scripts/download-smoke-corpus.sh for test-pdfs/smoke " +
            "(test-pdfs/sample-pdfs is checked into git and should already be present)");

        var pages = new List<PageEntry>();
        foreach (var pdfPath in pdfPaths)
        {
            var relPath = Path.GetRelativePath(root!, pdfPath).Replace('\\', '/');
            using var doc = PdfDocument.Open(pdfPath);
            for (int pageNumber = 1; pageNumber <= doc.PageCount; pageNumber++)
            {
                pages.Add(ScanPage(relPath, pdfPath, doc, pageNumber));
            }
        }

        long pdfeTotal = pages.Sum(p => (long)p.PdfeChars);
        long mutoolTotal = pages.Sum(p => (long)p.MutoolChars);
        double aggregateCoverage = mutoolTotal == 0 ? 1.0 : (double)pdfeTotal / mutoolTotal;

        var worklist = pages
            .Where(p => p.MutoolChars >= MinReferenceLength
                     && (p.CoverageRatio < WorklistThreshold || p.Similarity < WorklistThreshold))
            .OrderBy(p => Math.Min(p.CoverageRatio, p.Similarity))
            .ToList();

        // Blast-radius rollup for #513-#515: how many *distinct pages* does
        // each (Subtype, Encoding) combination touch across the worklist?
        // A font branch that shows up on 75 pages is a different priority
        // than one that shows up on 1 — this is the ordering #645 asked for,
        // one level above the per-page detail.
        var blastRadius = worklist
            .SelectMany(p => p.Fonts.Select(f => (Key: (f.Subtype, f.Encoding), p.File, p.Page)))
            .GroupBy(x => x.Key)
            .Select(g => new BlastRadiusEntry
            {
                Subtype = g.Key.Subtype,
                Encoding = g.Key.Encoding,
                PageCount = g.Select(x => (x.File, x.Page)).Distinct().Count(),
            })
            .OrderByDescending(b => b.PageCount)
            .ToList();

        _output.WriteLine($"Extraction parity: {pages.Count} pages across {pdfPaths.Count} PDFs (mutool {MutoolVersion()})");
        _output.WriteLine($"Aggregate coverage: pdfe extracts {aggregateCoverage:P1} of mutool's Unicode letter/digit count");
        _output.WriteLine($"Worklist ({worklist.Count} pages below {WorklistThreshold:P0}):");
        foreach (var p in worklist.Take(20))
        {
            _output.WriteLine($"  {p.File} p{p.Page}: coverage={p.CoverageRatio:F3} similarity={p.Similarity:F3} " +
                               $"fonts=[{string.Join(", ", p.Fonts.Select(f => f.Subtype))}]");
        }
        _output.WriteLine("Blast radius (worklist pages touched per font subtype/encoding):");
        foreach (var b in blastRadius)
        {
            _output.WriteLine($"  {b.Subtype} / {b.Encoding}: {b.PageCount} pages");
        }

        var reportDir = Path.Combine(root!, "logs", "extraction-parity");
        Directory.CreateDirectory(reportDir);
        var reportPath = Path.Combine(reportDir, "latest-report.json");
        var report = new ParityReport
        {
            GeneratedUtc = DateTime.UtcNow.ToString("o"),
            MutoolVersion = MutoolVersion(),
            Corpus = "test-pdfs/smoke + test-pdfs/sample-pdfs",
            PageCount = pages.Count,
            PdfCount = pdfPaths.Count,
            AggregateCoverage = aggregateCoverage,
            Pages = pages,
            Worklist = worklist,
            BlastRadius = blastRadius,
        };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        _output.WriteLine($"report: {reportPath}");

        pages.Count.Should().BeGreaterThan(0, "the smoke corpus should contain at least one page");
    }

    /// <summary>
    /// #648's "parser/renderer disagreement" requirement, applied to the
    /// adversarial/malformed corpus (poppler, pdf.js, Isartor — the same
    /// files <c>CorpusConformanceTests</c> scans for crashes/hangs) rather
    /// than the curated smoke corpus above. Deliberately a coarser,
    /// unbaselined pass/fail rather than <see cref="GenerateExtractionParityReport"/>'s
    /// per-page floor ratchet: a malformed-by-design corpus is far noisier
    /// (garbled/partial content is often the CORRECT extraction of a
    /// deliberately-broken file), so per-page regression tracking would be
    /// mostly maintenance noise. What's worth gating on is the catastrophic
    /// case — pdfe silently extracting near-nothing from a file mutool can
    /// still read a meaningful amount of text from, which is exactly the
    /// "redact what pdfe can't see" failure #637/#645 are about, just
    /// discovered via hostile input instead of a real-world form.
    ///
    /// It is expected and CORRECT for one tool to fail to open a file the
    /// other doesn't — that is not a disagreement, it's two different
    /// (both potentially valid) refusals. Only files BOTH tools open
    /// contribute to this check.
    /// </summary>
    /// <summary>
    /// Known adversarial-corpus disagreements by relative PDF path, filed
    /// as #651 on first discovery. Each entry: reason. Removing a line
    /// re-enables the gate for that file. Mirrors
    /// <c>RedactionRoundTripTests.KnownRedactionFailures</c>'s exact
    /// pattern — a new gate that immediately finds real findings should
    /// report them loudly and track them, not silently loosen its own
    /// threshold to pass.
    /// </summary>
    private static readonly Dictionary<string, string> KnownAdversarialDisagreements = new()
    {
        ["test-pdfs/pdfjs/bug854315.pdf"] =
            "#651 — 21% coverage, uncharacterized (has ToUnicode; does not match #659 or #662).",
        ["test-pdfs/pdfjs/issue14497.pdf"] = "#651 — 6% coverage, uncharacterized (no ToUnicode; does not match #662's Differences shape).",
        ["test-pdfs/pdfjs/issue17069.pdf"] = "#651 — 41% coverage, uncharacterized (has ToUnicode; does not match #659 or #662).",
        ["test-pdfs/pdfjs/issue18036.pdf"] = "#651 — 18% coverage, uncharacterized (standard /Identity-H Type0; does not match #659).",
        ["test-pdfs/pdfjs/issue19389.pdf"] = "#651 — 37% coverage, uncharacterized (simple Type1, no ToUnicode; does not match #662's Differences shape).",
    };

    [Fact]
    public void AdversarialCorpus_PdfeDoesNotCatastrophicallyUnderExtractRelativeToMutool()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable,
            "mutool not on PATH — install mupdf-tools to run the adversarial-corpus disagreement check");

        var root = LocateRepoRoot();
        Assert.SkipWhen(root == null, "could not locate repo root (pdfe.sln not found above AppContext.BaseDirectory)");

        var corpusDirs = new[]
        {
            Path.Combine(root!, "test-pdfs", "poppler"),
            Path.Combine(root!, "test-pdfs", "pdfjs"),
            Path.Combine(root!, "test-pdfs", "isartor"),
        };
        var pdfPaths = corpusDirs
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.pdf", SearchOption.AllDirectories))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        Assert.SkipWhen(pdfPaths.Count == 0,
            "no adversarial corpus found — run scripts/download-poppler-corpus.sh / download-pdfjs-corpus.sh / download-test-pdfs.sh");

        int bothOpened = 0, eitherRefused = 0;
        var newDisagreements = new List<string>();
        var knownDisagreements = new List<string>();

        foreach (var pdfPath in pdfPaths)
        {
            string? pdfeText = null;
            try
            {
                using var doc = PdfDocument.Open(pdfPath);
                if (doc.PageCount >= 1) pdfeText = doc.GetPage(1).Text;
            }
            catch { /* pdfe refusing a malformed file is an acceptable outcome — not a disagreement. */ }

            var mutoolText = MutoolTextExtractor.ExtractPage(pdfPath, 1);

            if (pdfeText == null || mutoolText == null)
            {
                eitherRefused++;
                continue;
            }
            bothOpened++;

            int mutoolChars = CountLetterOrDigit(mutoolText);
            if (mutoolChars < MinReferenceLength) continue; // not enough reference text to be meaningful signal.

            int pdfeChars = CountLetterOrDigit(pdfeText);
            double coverage = (double)pdfeChars / mutoolChars;

            // Deliberately coarse (0.5, vs the smoke corpus's 0.95 worklist
            // threshold): this corpus is adversarial by construction, and
            // the goal here is catching a real blind spot, not chasing
            // noise from files designed to confuse extraction generally.
            if (coverage >= 0.5) continue;

            var relPath = Path.GetRelativePath(root!, pdfPath).Replace('\\', '/');
            var finding = $"{relPath}: pdfe={pdfeChars} mutool={mutoolChars} chars (coverage={coverage:P0})";
            if (KnownAdversarialDisagreements.TryGetValue(relPath, out var reason))
                knownDisagreements.Add($"{finding} — ⚑ KNOWN, not gating: {reason}");
            else
                newDisagreements.Add(finding);
        }

        _output.WriteLine($"Adversarial corpus: {pdfPaths.Count} files, {bothOpened} opened by both tools, {eitherRefused} refused by at least one (expected/fine).");
        _output.WriteLine($"Known findings, not gating ({knownDisagreements.Count}), see #651:");
        foreach (var d in knownDisagreements) _output.WriteLine($"  {d}");
        _output.WriteLine($"NEW catastrophic under-extraction findings ({newDisagreements.Count}):");
        foreach (var d in newDisagreements) _output.WriteLine($"  {d}");

        newDisagreements.Should().BeEmpty(
            "pdfe extracting <50% of what mutool reads from a file both tools opened is a redaction-relevant " +
            "blind spot, not an expected adversarial-corpus refusal — see the NEW findings above. If this is a " +
            "genuinely new file/regression, investigate before adding it to KnownAdversarialDisagreements — that " +
            "allowlist is for tracked, not-yet-fixed findings (#651), not a way to silence the gate.");
    }

    /// <summary>
    /// Mirrors <see cref="TextExtractionDifferentialTests.MinReferenceTextLength"/>
    /// — pages with almost no reference text produce noisy ratios (a single
    /// stray character can swing coverage from 0% to 300%) and aren't useful
    /// signal for the worklist.
    /// </summary>
    private const int MinReferenceLength = 32;

    private static PageEntry ScanPage(string relPath, string pdfPath, PdfDocument doc, int pageNumber)
    {
        var entry = new PageEntry { File = relPath, Page = pageNumber };

        string pdfeText;
        try
        {
            pdfeText = doc.GetPage(pageNumber).Text ?? "";
        }
        catch (Exception ex)
        {
            entry.Error = $"{ex.GetType().Name}: {Truncate(ex.Message, 200)}";
            pdfeText = "";
        }

        var mutoolText = MutoolTextExtractor.ExtractPage(pdfPath, pageNumber) ?? "";

        entry.PdfeChars = CountLetterOrDigit(pdfeText);
        entry.MutoolChars = CountLetterOrDigit(mutoolText);
        entry.CoverageRatio = TextSimilarity.CoverageRatio(pdfeText, mutoolText);
        entry.Similarity = TextSimilarity.BigramJaccard(pdfeText, mutoolText);

        try
        {
            entry.Fonts = doc.GetPage(pageNumber).GetFonts()
                .Select(f => new FontInfo
                {
                    Name = f.Name,
                    Subtype = f.Font.GetNameOrNull("Subtype") ?? "?",
                    BaseFont = f.Font.GetNameOrNull("BaseFont") ?? "?",
                    Encoding = DescribeEncoding(f.Font),
                })
                .DistinctBy(f => (f.Subtype, f.BaseFont, f.Encoding))
                .ToList();
        }
        catch
        {
            entry.Fonts = new List<FontInfo>();
        }

        return entry;
    }

    private static string DescribeEncoding(Pdfe.Core.Primitives.PdfDictionary font)
    {
        var encObj = font.GetOptional("Encoding");
        return encObj switch
        {
            Pdfe.Core.Primitives.PdfName n => n.Value,
            Pdfe.Core.Primitives.PdfDictionary => "(dictionary)",
            null => "(none)",
            _ => encObj.GetType().Name,
        };
    }

    /// <summary>
    /// Unicode-aware letter/digit count on the RAW extraction — the coverage
    /// basis. Whitespace/punctuation is excluded (both extractors reflow
    /// those differently and it's not signal), but every script counts, not
    /// just ASCII. See the class doc comment for why this matters.
    /// </summary>
    private static int CountLetterOrDigit(string s) => s.Count(char.IsLetterOrDigit);

    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

    /// <summary>
    /// The baseline's floors are only meaningful against a specific mutool
    /// build — record which one produced them so a maintainer on a different
    /// version can tell "regression" from "the oracle changed."
    /// </summary>
    private static string MutoolVersion()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("mutool", "-v")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return "unknown";
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            var text = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            return text.Trim();
        }
        catch
        {
            return "unknown";
        }
    }

    private static string? LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "pdfe.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    public sealed class ParityReport
    {
        public string? GeneratedUtc { get; set; }
        public string MutoolVersion { get; set; } = "";
        public string Corpus { get; set; } = "";
        public int PageCount { get; set; }
        public int PdfCount { get; set; }
        public double AggregateCoverage { get; set; }
        public List<PageEntry> Pages { get; set; } = new();
        public List<PageEntry> Worklist { get; set; } = new();
        public List<BlastRadiusEntry> BlastRadius { get; set; } = new();
    }

    public sealed class BlastRadiusEntry
    {
        public string Subtype { get; set; } = "";
        public string Encoding { get; set; } = "";
        public int PageCount { get; set; }
    }

    public sealed class PageEntry
    {
        public string File { get; set; } = "";
        public int Page { get; set; }
        public int PdfeChars { get; set; }
        public int MutoolChars { get; set; }
        public double CoverageRatio { get; set; }
        public double Similarity { get; set; }
        public string? Error { get; set; }
        public List<FontInfo> Fonts { get; set; } = new();
    }

    public sealed class FontInfo
    {
        public string Name { get; set; } = "";
        public string Subtype { get; set; } = "";
        public string BaseFont { get; set; } = "";
        public string Encoding { get; set; } = "";
    }
}
