using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering;

namespace Excise.RenderTools;

// Workflow-level hot-path profiling driver for the #596/#597 performance
// baseline. Measures the representative interactive workflows (first-page
// render, all-page render, page navigation re-render, zoom re-render, text
// extraction, search, redaction save) with elapsed time AND managed
// allocation per step, writing incremental NDJSON progress so long PDFs
// cannot hide status. Excise-owned code only: no reference renderers are
// invoked here (reference timing is captured separately by benchmark-suite).
partial class Program
{
    static Command CreateProfileWorkflowsCommand()
    {
        var corpusOption = new Option<DirectoryInfo>("--corpus")
        {
            Description = "PDF corpus directory (searched recursively).",
            Required = true,
        };
        var outputOption = new Option<DirectoryInfo>("--output-dir", "-o")
        {
            Description = "Directory for workflow-profile.ndjson and workflow-profile.json.",
            DefaultValueFactory = _ => new DirectoryInfo("logs/benchmarks/latest-workflows"),
        };
        var pageLimitOption = new Option<int>("--page-limit")
        {
            Description = "Maximum pages measured per document for all-page workflows.",
            DefaultValueFactory = _ => 8,
        };
        var dpiOption = new Option<int>("--dpi")
        {
            Description = "Base render DPI (display zoom 100%).",
            DefaultValueFactory = _ => 96,
        };
        var zoomDpiOption = new Option<int>("--zoom-dpi")
        {
            Description = "Zoom re-render DPI (display zoom 200% by default).",
            DefaultValueFactory = _ => 192,
        };
        var searchTermOption = new Option<string>("--search-term")
        {
            Description = "Term located during the search workflow step.",
            DefaultValueFactory = _ => "the",
        };
        var stepsOption = new Option<string?>("--steps")
        {
            Description = "Optional comma-separated step filter (e.g. save-roundtrip,redaction-save) so a CPU trace can isolate one workflow.",
        };

        var command = new Command(
            "profile-workflows",
            "Profile representative excise workflows (open, render, navigate, zoom, extract, search, redact-save) with per-step elapsed time and managed allocation")
        {
            corpusOption,
            outputOption,
            pageLimitOption,
            dpiOption,
            zoomDpiOption,
            searchTermOption,
            stepsOption,
        };

        command.SetAction(parseResult =>
        {
            var corpus = parseResult.GetValue(corpusOption)!;
            var output = parseResult.GetValue(outputOption)!;
            var pageLimit = Math.Max(1, parseResult.GetValue(pageLimitOption));
            var dpi = Math.Max(36, parseResult.GetValue(dpiOption));
            var zoomDpi = Math.Max(36, parseResult.GetValue(zoomDpiOption));
            var searchTerm = parseResult.GetValue(searchTermOption) ?? "the";
            var stepsRaw = parseResult.GetValue(stepsOption);
            var steps = string.IsNullOrWhiteSpace(stepsRaw)
                ? null
                : stepsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.Ordinal);

            try
            {
                Environment.ExitCode = RunWorkflowProfile(
                    corpus.FullName, output.FullName, pageLimit, dpi, zoomDpi, searchTerm, steps);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });

        return command;
    }

    private sealed record WorkflowStepResult(
        string pdf,
        string step,
        string workloadId,
        string scope,
        string issueRefs,
        int pageCount,
        int pagesMeasured,
        double elapsedMs,
        long allocatedBytes,
        string status,
        string? detail);

    private static int RunWorkflowProfile(
        string corpusDir,
        string outputDir,
        int pageLimit,
        int dpi,
        int zoomDpi,
        string searchTerm,
        IReadOnlySet<string>? stepFilter = null)
    {
        if (!Directory.Exists(corpusDir))
        {
            Console.Error.WriteLine($"Corpus directory not found: {corpusDir}");
            return 2;
        }

        Directory.CreateDirectory(outputDir);
        var ndjsonPath = Path.Combine(outputDir, "workflow-profile.ndjson");
        var summaryPath = Path.Combine(outputDir, "workflow-profile.json");
        using var ndjson = new StreamWriter(ndjsonPath, append: false);

        var pdfs = Directory.EnumerateFiles(corpusDir, "*.pdf", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        if (pdfs.Length == 0)
        {
            Console.Error.WriteLine($"No PDFs found under {corpusDir}");
            return 2;
        }

        var results = new List<WorkflowStepResult>();
        foreach (var pdfPath in pdfs)
        {
            var rel = Path.GetRelativePath(corpusDir, pdfPath).Replace(Path.DirectorySeparatorChar, '/');
            Console.Out.WriteLine($"Profiling {rel}");
            foreach (var step in ProfilePdfWorkflows(pdfPath, rel, pageLimit, dpi, zoomDpi, searchTerm, stepFilter))
            {
                results.Add(step);
                ndjson.WriteLine(JsonSerializer.Serialize(step));
                ndjson.Flush();
            }
        }

        var summary = BuildWorkflowSummary(results, corpusDir, pageLimit, dpi, zoomDpi, searchTerm);
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, BenchmarkJsonOptions));
        Console.Out.WriteLine($"Workflow profile: {summaryPath}");
        return 0;
    }

    private static IEnumerable<WorkflowStepResult> ProfilePdfWorkflows(
        string pdfPath,
        string rel,
        int pageLimit,
        int dpi,
        int zoomDpi,
        string searchTerm,
        IReadOnlySet<string>? stepFilter = null)
    {
        var results = new List<WorkflowStepResult>();
        PdfDocument? doc = null;
        var pageCount = 0;

        WorkflowStepResult? Measure(
            string step, string workloadId, string issueRefs, int pagesMeasured,
            Func<string?> action, string scope = "excise-owned")
        {
            if (stepFilter is not null && step != "open" && !stepFilter.Contains(step))
                return null;
            var beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            string status;
            string? detail;
            try
            {
                detail = action();
                status = "OK";
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                status = "ERROR";
            }
            sw.Stop();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
            var result = new WorkflowStepResult(
                rel, step, workloadId, scope, issueRefs, pageCount, pagesMeasured,
                sw.Elapsed.TotalMilliseconds, allocated, status, detail);
            results.Add(result);
            return result;
        }

        // 1. Document open/parse.
        Measure("open", "core.document-open", "#597 #600", 0, () =>
        {
            doc = PdfDocument.Open(pdfPath);
            pageCount = doc.PageCount;
            return $"pages={pageCount}";
        });
        if (doc is null)
            return results;

        using (doc)
        {
            var renderer = new SkiaRenderer();
            var measured = Math.Min(pageCount, pageLimit);

            // 2. First-page render (document-open perceived latency).
            Measure("first-page-render", "renderer.page-render", "#598 #599", 1, () =>
            {
                using var bmp = renderer.RenderPage(doc!.GetPage(1), new RenderOptions { Dpi = dpi });
                return $"{bmp.Width}x{bmp.Height}";
            });

            // 3. All-page render (thumbnails / continuous scroll warm-up).
            Measure("all-page-render", "renderer.page-render", "#598 #599", measured, () =>
            {
                long pixels = 0;
                for (var p = 1; p <= measured; p++)
                {
                    using var bmp = renderer.RenderPage(doc!.GetPage(p), new RenderOptions { Dpi = dpi });
                    pixels += (long)bmp.Width * bmp.Height;
                }
                return $"pixels={pixels}";
            });

            // 4. Page-navigation re-render (revisit already-parsed pages, engine-level cost without GUI cache).
            var navPages = Math.Min(2, measured);
            Measure("navigation-rerender", "renderer.page-render", "#598 #601", navPages, () =>
            {
                for (var p = 1; p <= navPages; p++)
                {
                    using var bmp = renderer.RenderPage(doc!.GetPage(p), new RenderOptions { Dpi = dpi });
                }
                return null;
            });

            // 5. Zoom re-render (same page at zoom DPI).
            Measure("zoom-rerender", "renderer.page-render", "#598 #599 #601", 1, () =>
            {
                using var bmp = renderer.RenderPage(doc!.GetPage(1), new RenderOptions { Dpi = zoomDpi });
                return $"{bmp.Width}x{bmp.Height}";
            });

            // 6. Text extraction (cold: first .Text access computes letters).
            Measure("text-extract", "core.text-extract", "#600", measured, () =>
            {
                long chars = 0;
                for (var p = 1; p <= measured; p++)
                    chars += (doc!.GetPage(p).Text ?? "").Length;
                return $"chars={chars}";
            });

            // 7. Search (word segmentation + term scan over cached letters, the GUI search route shape).
            Measure("search", "core.text-extract", "#600", measured, () =>
            {
                var matches = 0;
                for (var p = 1; p <= measured; p++)
                {
                    foreach (var word in doc!.GetPage(p).GetWords())
                    {
                        if (word.Text.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                            matches++;
                    }
                }
                return $"matches={matches}";
            });
        }
        doc = null;

        // 8. Save round-trip without mutation (isolates writer cost from redaction cost).
        Measure("save-roundtrip", "core.document-open", "#597", 0, () =>
        {
            using var saveDoc = PdfDocument.Open(pdfPath);
            var bytes = saveDoc.SaveToBytes();
            return $"bytes={bytes.Length}";
        });

        // 9. Redaction save: glyph-level RedactArea on the first word of page 1, then save.
        Measure("redaction-save", "redaction.synthetic-save", "#597 #602", 1, () =>
        {
            using var redactDoc = PdfDocument.Open(pdfPath);
            var page = redactDoc.GetPage(1);
            var words = page.GetWords();
            if (words.Count == 0)
                return "no-words";
            var word = words[0];
            var letters = word.Letters;
            var area = new PdfRectangle(
                letters.Min(l => l.GlyphRectangle.Left) - 1,
                letters.Min(l => l.GlyphRectangle.Bottom) - 1,
                letters.Max(l => l.GlyphRectangle.Right) + 1,
                letters.Max(l => l.GlyphRectangle.Top) + 1);
            page.RedactArea(area);
            var bytes = redactDoc.SaveToBytes();
            return $"bytes={bytes.Length}";
        }, scope: "excise-owned-security-critical");

        return results;
    }

    private static object BuildWorkflowSummary(
        IReadOnlyList<WorkflowStepResult> results,
        string corpusDir,
        int pageLimit,
        int dpi,
        int zoomDpi,
        string searchTerm)
    {
        var byStep = results
            .GroupBy(r => r.step, StringComparer.Ordinal)
            .Select(g =>
            {
                var elapsed = g.Select(r => r.elapsedMs).OrderBy(v => v).ToArray();
                var first = g.First();
                return new
                {
                    step = g.Key,
                    workloadId = first.workloadId,
                    scope = first.scope,
                    issueRefs = first.issueRefs,
                    count = g.Count(),
                    errorCount = g.Count(r => r.status == "ERROR"),
                    totalMs = Math.Round(elapsed.Sum(), 1),
                    averageMs = Math.Round(elapsed.Average(), 2),
                    p50Ms = Math.Round(Percentile(elapsed, 0.50), 1),
                    p95Ms = Math.Round(Percentile(elapsed, 0.95), 1),
                    maxMs = Math.Round(elapsed[^1], 1),
                    totalAllocatedMB = Math.Round(g.Sum(r => r.allocatedBytes) / (1024.0 * 1024.0), 1),
                    averageAllocatedMB = Math.Round(g.Average(r => r.allocatedBytes) / (1024.0 * 1024.0), 2),
                };
            })
            .OrderByDescending(s => s.totalMs)
            .ToArray();

        return new
        {
            schemaVersion = 1,
            issues = new[] { "#596", "#597" },
            generatedUtc = DateTimeOffset.UtcNow.ToString("O"),
            configuration = new
            {
                corpusDir = Path.GetFullPath(corpusDir),
                pageLimit,
                dpi,
                zoomDpi,
                searchTerm,
                allocationNote = "allocatedBytes is managed (GC) allocation on the driver thread; native SkiaSharp bitmap memory is not included.",
            },
            steps = byStep,
            perPdf = results,
        };
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
        => sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * percentile) - 1, 0, sorted.Count - 1)];
}
