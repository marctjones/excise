using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering;
using SkiaSharp;
using Xunit;
using Excise.Rendering.Differential;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Exploratory mode of the differential harness — runs against the full
/// pdf.js test corpus (~684 PDFs) and emits a JSON report instead of
/// passing/failing the build.
///
/// MEMORY BUDGET: must stay under ~4 GB peak per process. SkiaSharp
/// bitmaps are native-allocated; the .NET GC reclaims them lazily, so
/// running all 684 PDFs in one process accumulates 26+ GB before the
/// kernel intervenes (this is exactly what OOM-killed Claude's session
/// on 2026-05-01). The test is therefore CHUNKED across processes:
///
///   • EXCISE_CHUNK_INDEX (default 0) and EXCISE_CHUNK_TOTAL (default 1)
///     env vars slice the discovered corpus by `i % total == index`.
///   • One process handles ~50 PDFs, then exits — process exit
///     guarantees full native-memory reclamation.
///   • The shell driver scripts/run-exploratory-corpus.sh runs all
///     chunks sequentially and merges the per-chunk JSON outputs into
///     a single exploratory-report.json.
///
/// Within a chunk we also force GC every N iterations so per-bitmap
/// native memory doesn't pile up before exit.
///
/// To run this legacy xUnit entry point for a single chunk locally:
///   EXCISE_RUN_EXPLORATORY_TEST=1 EXCISE_CHUNK_INDEX=0 EXCISE_CHUNK_TOTAL=14 \
///     dotnet test --filter "Category=Exploratory"
///
/// To run the whole corpus chunked:
///   ./scripts/run-exploratory-corpus.sh
///
/// Default `dotnet test` skips this class unless EXCISE_RUN_EXPLORATORY_TEST=1.
/// </summary>
[Trait("Category", "Exploratory")]
public sealed class ExploratoryDifferentialTests
{
    private readonly ITestOutputHelper _output;

    public ExploratoryDifferentialTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Force a full GC + finalizer flush every N iterations to keep
    /// SkiaSharp's native allocations from outpacing the GC's reclaim
    /// rate. Tuned empirically: at 10, the per-process peak stays
    /// under ~600 MB on the smoke corpus; without this the process
    /// grows monotonically.
    /// </summary>
    private const int GcEveryN = 10;

    [Fact]
    [Trait("Category", "Exploratory")]
    public void GenerateCorpusReportChunk()
    {
        Assert.SkipUnless(IsExploratoryTestEnabled(),
            "set EXCISE_RUN_EXPLORATORY_TEST=1 to run the legacy xUnit exploratory corpus path; prefer scripts/run-exploratory-corpus.sh for full runs");

        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable,
            "mutool not on PATH — install mupdf-tools to generate the exploratory report");

        var root = LocateRepoRoot()!;
        var corpus = Path.Combine(root, "test-pdfs", "pdfjs");
        Assert.SkipWhen(!Directory.Exists(corpus),
            "pdf.js corpus not downloaded — run scripts/download-pdfjs-corpus.sh");

        // Chunking. Default is "single chunk = full corpus" so a one-off
        // dev run still works (just budget the memory accordingly).
        int chunkIndex = ParseEnvInt("EXCISE_CHUNK_INDEX", defaultValue: 0);
        int chunkTotal = ParseEnvInt("EXCISE_CHUNK_TOTAL", defaultValue: 1);
        if (chunkTotal < 1) chunkTotal = 1;
        if (chunkIndex < 0 || chunkIndex >= chunkTotal)
            throw new ArgumentOutOfRangeException(
                nameof(chunkIndex),
                $"EXCISE_CHUNK_INDEX={chunkIndex} out of range for EXCISE_CHUNK_TOTAL={chunkTotal}");

        // Keep this order in sync with the CLI corpus scanner and isolated
        // recovery script so chunked reports cover each PDF exactly once.
        var allPdfs = Directory.EnumerateFiles(corpus, "*.pdf")
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        // Stable assignment: PDF i goes into chunk i % chunkTotal. Using
        // index modulo (rather than contiguous slicing) means the chunks
        // are roughly equally weighted across whatever ordering quirks
        // the directory has — no chunk gets all the slow PDFs.
        var myPdfs = allPdfs
            .Select((path, idx) => (path, idx))
            .Where(t => t.idx % chunkTotal == chunkIndex)
            .Select(t => t.path)
            .ToList();

        _output.WriteLine($"chunk {chunkIndex + 1}/{chunkTotal}: " +
                          $"processing {myPdfs.Count} of {allPdfs.Count} PDFs");

        var entries = new List<CorpusEntry>();
        int processed = 0;
        long peakBytes = 0;

        foreach (var pdf in myPdfs)
        {
            var rel = Path.GetRelativePath(root, pdf);
            var entry = ScanOne(rel, pdf);
            entries.Add(entry);
            processed++;

            // Sample RSS every iteration so the chunk's peak measurement
            // is honest even on very small chunks (where the GC interval
            // below would otherwise never fire).
            var rss = Environment.WorkingSet;
            if (rss > peakBytes) peakBytes = rss;

            // Force native finalization every N iterations. SkiaSharp
            // SKBitmap uses unmanaged memory backed by SKObject's
            // dispose chain; the .NET GC sees a small managed wrapper
            // and doesn't urgency-collect it, leaving the native
            // allocation alive until the next gen-2 collection. Forcing
            // the cycle keeps per-process memory bounded.
            if (processed % GcEveryN == 0)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        // Final GC before reporting so the peak is honest.
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Tally + emit summary for this chunk.
        var counts = new Dictionary<string, int>();
        foreach (var e in entries)
        {
            counts.TryGetValue(e.Status, out var c);
            counts[e.Status] = c + 1;
        }

        _output.WriteLine("");
        _output.WriteLine($"=== Chunk {chunkIndex + 1}/{chunkTotal} report ===");
        foreach (var kv in counts.OrderByDescending(kv => kv.Value))
            _output.WriteLine($"  {kv.Value,4}  {kv.Key}");
        _output.WriteLine($"  total processed:  {entries.Count}");
        _output.WriteLine($"  peak RSS observed: {peakBytes / 1024 / 1024} MB");

        // Per-chunk JSON file. The driver script merges all
        // exploratory-chunk-*.json files into the final report.
        var slicePath = Path.Combine(AppContext.BaseDirectory,
            $"exploratory-chunk-{chunkIndex:D3}-of-{chunkTotal:D3}.json");
        var json = JsonSerializer.Serialize(new
        {
            generatedUtc = DateTime.UtcNow.ToString("o"),
            corpus = "test-pdfs/pdfjs",
            chunkIndex,
            chunkTotal,
            counts,
            total = entries.Count,
            peakRssBytes = peakBytes,
            entries,
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(slicePath, json);
        _output.WriteLine($"  slice: {slicePath}");

        // The slice file is the output. We only sanity-assert that the
        // chunk processed at least one PDF (otherwise the slicing math
        // is broken).
        entries.Count.Should().BeGreaterThan(0,
            $"chunk {chunkIndex + 1}/{chunkTotal} should process at least one PDF");
    }

    private static int ParseEnvInt(string name, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var v) ? v : defaultValue;
    }

    private static bool IsExploratoryTestEnabled()
    {
        var raw = Environment.GetEnvironmentVariable("EXCISE_RUN_EXPLORATORY_TEST");
        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private CorpusEntry ScanOne(string rel, string pdfPath)
    {
        var entry = new CorpusEntry { Path = rel };

        // Try to render page 1 with excise.
        SKBitmap? exciseBitmap = null;
        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            entry.PageCount = doc.PageCount;
            if (doc.PageCount == 0)
            {
                entry.Status = "EMPTY_DOC";
                return entry;
            }
            var renderer = new SkiaRenderer();
            exciseBitmap = renderer.RenderPage(doc.GetPage(1), new RenderOptions { Dpi = 150 });
        }
        catch (Exception ex)
        {
            entry.Status = "PARSE_ERROR";
            entry.ErrorType = ex.GetType().Name;
            entry.ErrorMessage = Truncate(ex.Message, 200);
            exciseBitmap?.Dispose();
            return entry;
        }

        if (exciseBitmap == null)
        {
            entry.Status = "RENDER_NULL";
            return entry;
        }

        // Reference render via mutool.
        SKBitmap? mutoolBitmap = null;
        try
        {
            mutoolBitmap = MutoolReferenceRenderer.RenderPage(pdfPath, 1, 150);
            if (mutoolBitmap == null)
            {
                entry.Status = "MUTOOL_REFUSED";
                return entry;
            }

            // Normalize dims if needed.
            if (exciseBitmap.Width != mutoolBitmap.Width || exciseBitmap.Height != mutoolBitmap.Height)
            {
                using var resized = DifferentialMetrics.ResizeMatch(
                    exciseBitmap, mutoolBitmap.Width, mutoolBitmap.Height);
                exciseBitmap.Dispose();
                exciseBitmap = resized.Copy();
            }

            var report = DifferentialMetrics.Compare(exciseBitmap, mutoolBitmap);
            entry.DiffFraction = report.DifferingPixelFraction;
            entry.Mae = report.MeanAbsoluteError;
            entry.Status = (report.DifferingPixelFraction <= 0.10
                         && report.MeanAbsoluteError <= 32.0)
                ? "PASS"
                : "DIFF";
        }
        catch (Exception ex)
        {
            entry.Status = "COMPARE_ERROR";
            entry.ErrorType = ex.GetType().Name;
            entry.ErrorMessage = Truncate(ex.Message, 200);
        }
        finally
        {
            exciseBitmap?.Dispose();
            mutoolBitmap?.Dispose();
        }

        return entry;
    }

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s.Substring(0, n) + "…";

    private static string? LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "excise.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    public sealed class CorpusEntry
    {
        public string Path { get; set; } = "";
        public string Status { get; set; } = "UNKNOWN";
        public int PageCount { get; set; }
        public double DiffFraction { get; set; }
        public double Mae { get; set; }
        public string? ErrorType { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
