using System;
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using AwesomeAssertions;
using Excise.Avalonia.Controls;
using Excise.Core.Document;
using Excise.App.Tests.Utilities;
using Excise.App.ViewModels;
using Excise.App.Views;

namespace Excise.App.Tests.UI;

/// <summary>
/// Repeatable interaction-latency benchmark for #601. Unlike
/// <see cref="GuiWorkflowPerformanceReportTests"/> (single pass, whole-ms
/// resolution on a 12-page synthetic doc — which floors most interactions at
/// 0 ms), this drives each pure ViewModel interaction MANY times on a LARGE
/// document and reports the AVERAGE in sub-millisecond resolution, so an O(n)
/// per-interaction cost that a single 0-ms sample hides becomes visible.
///
/// The measured phases are the UI-thread work a real interaction triggers
/// (property-change fan-out, highlight/selection recomputation, scroll-offset
/// lookup) — NOT the async render/raster that follows, which belongs to
/// Excise.Rendering (#598/#599) and is out of scope here.
///
/// Budgets are the "measurable budget" restatement requested on #601: each
/// direct interaction must average well under one input frame (16 ms) even on
/// a large document with an active many-match search. They are intentionally
/// generous versus the measured numbers so the gate flags a real regression,
/// not machine jitter.
/// </summary>
[Collection("AvaloniaTests")]
public class GuiLatencyBenchmarkTests
{
    private const int LargePageCount = 400;
    private const int Iterations = 40;

    // Per-interaction average must stay under one 60 Hz input frame.
    private const double DirectInputFrameBudgetMs = 16.0;

    [FixedAvaloniaFact]
    public async Task DirectInteractions_StayWithinInputFrameBudget_OnLargeDocument()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-gui-latency-{Guid.NewGuid():N}.pdf");
        TestPdfGenerator.CreateMultiPagePdf(path, pageCount: LargePageCount);

        try
        {
            var vm = MainWindowViewModelTestFactory.Create();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            await vm.LoadDocumentAsync(path);

            var results = new List<BenchResult>();

            // Page navigation with NO active search (baseline fan-out cost).
            results.Add(BenchToggle("page-navigation.no-search", vm, 10, 20));

            // Populate a many-match search: "e" appears several times on every
            // page, so SearchMatches grows into the thousands — the worst case
            // for the per-navigation highlight recomputation.
            vm.SearchText = "e";
            vm.FindNow();
            await WaitForAsync(() => vm.SearchMatches.Count > 0 && !vm.IsSearching, TimeSpan.FromSeconds(60));
            var matchCount = vm.SearchMatches.Count;

            // Page navigation WITH the active many-match search.
            results.Add(BenchToggle("page-navigation.active-search", vm, 10, 20));

            // Focused A/B of the exact operation the #601 fix changed, free of
            // dispatcher/navigation fixed costs: resolving "which matches are on
            // the current page" via the OLD linear scan over all matches vs the
            // NEW per-page index. This isolates the O(total matches) → O(matches
            // on page) improvement so it reads as signal, not sub-ms jitter.
            // Each sample resolves the matches for ONE page, so the reported
            // avg is the cost of a SINGLE per-navigation lookup (a real page
            // flip does one, not many).
            var matches = vm.SearchMatches;
            var index = vm.MatchesByPageIndexForBenchmark;
            results.Add(BenchLookup("highlight-lookup.old-linear-scan", i =>
            {
                int p = (i * 37) % LargePageCount;
                return matches.Where(m => m.PageIndex == p).ToList().Count;
            }));
            results.Add(BenchLookup("highlight-lookup.new-indexed", i =>
            {
                int p = (i * 37) % LargePageCount;
                return index.TryGetValue(p, out var onPage) ? onPage.Count : 0;
            }));

            results.Add(Bench("zoom-in", () => vm.ZoomInCommand.Execute().Subscribe()));
            results.Add(Bench("zoom-fit-width", () => vm.ZoomFitWidthCommand.Execute().Subscribe()));

            // Continuous scroll-offset lookup (cached slot binary search).
            vm.ViewMode = PdfViewMode.Continuous;
            var viewer = window.FindControl<PdfViewerControl>("PdfViewerControl");
            var scroll = viewer?.FindControl<ScrollViewer>("ContinuousScrollViewer");
            if (scroll != null)
            {
                double y = 0;
                results.Add(Bench("continuous-scroll-offset", () =>
                {
                    y = (y + 1_800) % 40_000;
                    scroll.Offset = new Vector(0, y);
                }));
            }
            vm.ViewMode = PdfViewMode.SinglePage;

            results.Add(Bench("redaction-preview-state", () =>
            {
                vm.IsRedactionMode = true;
                vm.CurrentRedactionArea = new Rect(20, 40, 160, 48);
                vm.CurrentRedactionPageArea = PdfPageRect.ViewerDips(
                    vm.CurrentPage, 20, 40, 160, 48, MainWindowViewModel.DefaultViewerRenderDpi);
            }));

            WriteReport(results, matchCount);

            foreach (var r in results)
            {
                // The highlight-lookup.* probes are diagnostic A/B measurements
                // of the changed operation, not direct interactions — they are
                // reported but not gated.
                if (r.Name.StartsWith("highlight-lookup.", StringComparison.Ordinal))
                    continue;

                r.AvgMs.Should().BeLessThan(DirectInputFrameBudgetMs,
                    $"interaction '{r.Name}' averaged {r.AvgMs:F3} ms/iter over {Iterations} iterations " +
                    $"on a {LargePageCount}-page document ({matchCount} active search matches); " +
                    "direct interactions must stay under one 60 Hz input frame");
            }
        }
        finally
        {
            TestPdfGenerator.CleanupTestFile(path);
        }
    }

    private static BenchResult BenchToggle(string name, MainWindowViewModel vm, int a, int b)
    {
        var toggle = false;
        return Bench(name, () =>
        {
            vm.CurrentPageIndex = toggle ? a : b;
            toggle = !toggle;
        });
    }

    private static BenchResult Bench(string name, Action action)
    {
        // Warm up so JIT / first-touch allocation isn't charged to the average.
        for (int i = 0; i < 5; i++) action();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < Iterations; i++) action();
        sw.Stop();

        return new BenchResult(name, sw.Elapsed.TotalMilliseconds / Iterations);
    }

    // High-sample-count measurement of a single-page match lookup. Each call
    // does exactly one lookup, so the reported avg is per-navigation cost.
    // The sample count is large because the indexed path is sub-microsecond.
    private const int LookupSamples = 20_000;

    private static BenchResult BenchLookup(string name, Func<int, int> lookup)
    {
        for (int i = 0; i < 200; i++) _ = lookup(i); // warm up

        long sink = 0;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < LookupSamples; i++) sink += lookup(i);
        sw.Stop();
        GC.KeepAlive(sink);

        return new BenchResult(name, sw.Elapsed.TotalMilliseconds / LookupSamples);
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (!predicate())
        {
            if (deadline.Elapsed > timeout)
                throw new TimeoutException($"Condition was not met within {timeout.TotalSeconds:0.0}s.");
            await Task.Delay(25);
        }
    }

    private static void WriteReport(IReadOnlyList<BenchResult> results, int matchCount)
    {
        var outputDir = Path.Combine(AppContext.BaseDirectory, "UI", "test-output");
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, "gui-latency-benchmark.json");
        var report = new
        {
            schemaVersion = 1,
            generatedUtc = DateTimeOffset.UtcNow,
            suite = "gui-latency-benchmark",
            pageCount = LargePageCount,
            iterations = Iterations,
            searchMatchCount = matchCount,
            budgetMs = DirectInputFrameBudgetMs,
            results = results.Select(r => new { r.Name, avgMs = r.AvgMs }),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record BenchResult(string Name, double AvgMs);
}
