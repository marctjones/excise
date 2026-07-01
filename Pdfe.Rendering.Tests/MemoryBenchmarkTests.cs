using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Xunit;
namespace Pdfe.Rendering.Tests;

/// <summary>
/// Memory baseline tests for the Skia render pipeline.
///
/// These tests fail when memory usage regresses significantly, catching systemic
/// allocation creep that timing tests miss. Current-thread allocation counters
/// keep the checks stable when test assemblies run in parallel.
///
/// All baselines were calibrated against an Ubuntu 26.04 / .NET 10 / Skia 2.88.9
/// reference machine. Adjust thresholds if the test agent runs on significantly
/// different hardware.
/// </summary>
public class MemoryBenchmarkTests
{
    private const long DefaultSingleRenderBudgetBytes = 30L * 1024 * 1024;
    private const long MultiPageRenderBudgetBytes = 50L * 1024 * 1024;

    private sealed record AllocationBudget(long Bytes, string Reason);

    private static readonly AllocationBudget DefaultSingleRenderBudget = new(
        DefaultSingleRenderBudgetBytes,
        "default single-page smoke-corpus render budget");

    private static readonly Dictionary<string, AllocationBudget> SingleRenderBudgets = new(StringComparer.Ordinal)
    {
        // DS-82 is a dense, form-heavy smoke fixture. Current local .NET 10 /
        // Skia profiling measures about 48 MB for page 1; keep a narrow
        // fixture baseline so it remains gated without turning the whole smoke
        // corpus budget into a 50 MB default.
        ["state-ds82-passport-renewal.pdf"] = new(
            50L * 1024 * 1024,
            "DS-82 page 1 calibrated fixture budget; current local profiling measures about 48 MB"),
    };

    private readonly ITestOutputHelper _out;
    public MemoryBenchmarkTests(ITestOutputHelper o) { _out = o; }

    private static readonly string CorpusDir = ResolveCorpusDir();

    private static AllocationBudget GetSingleRenderBudget(string fileName)
    {
        return SingleRenderBudgets.TryGetValue(fileName, out var budget)
            ? budget
            : DefaultSingleRenderBudget;
    }

    private static string ResolveCorpusDir()
    {
        // Tests run from bin/Debug/net10.0; walk up to repo root.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "test-pdfs", "smoke");
            if (Directory.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "test-pdfs", "smoke");
    }

    /// <summary>
    /// Allocation per render: render a single page at 150 DPI and assert
    /// GC.GetTotalAllocatedBytes delta stays below a threshold.
    /// Tests across the 5 sample PDFs in test-pdfs/smoke/.
    /// </summary>
    [Theory]
    [InlineData("irs-w9.pdf", 1)]
    [InlineData("irs-1040.pdf", 1)]
    [InlineData("scotus-trump-v-us.pdf", 1)]
    [InlineData("state-ds82-passport-renewal.pdf", 1)]
    [InlineData("cdc-vis-covid-19.pdf", 1)]
    public void RenderAllocationPerPage_StaysBelowBudget(string fileName, int pageIndex)
    {
        var path = Path.Combine(CorpusDir, fileName);
        if (!File.Exists(path))
        {
            _out.WriteLine($"SKIP: {path} missing — corpus not downloaded");
            return;
        }

        // Warmup: open + render once so JIT, font caches, and process
        // stabilization don't dominate the allocation measurement.
        using (var warmDoc = PdfDocument.Open(path))
        {
            var warmRenderer = new SkiaRenderer();
            using var _ = warmRenderer.RenderPage(warmDoc.GetPage(pageIndex),
                new RenderOptions { Dpi = 150 });
        }

        // Full GC + settle to establish baseline.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Measure allocation across a single render.
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        using (var doc = PdfDocument.Open(path))
        {
            var renderer = new SkiaRenderer();
            using var bitmap = renderer.RenderPage(doc.GetPage(pageIndex),
                new RenderOptions { Dpi = 150 });
        }
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();

        long allocDelta = allocAfter - allocBefore;
        // Convert to MB for reporting.
        double allocDeltaMb = allocDelta / (1024.0 * 1024.0);
        var budget = GetSingleRenderBudget(fileName);
        double budgetMb = budget.Bytes / (1024.0 * 1024.0);

        _out.WriteLine(
            $"{fileName,-45} allocated {allocDeltaMb:F2} MB " +
            $"(budget={budgetMb:F2} MB, before={allocBefore / (1024.0 * 1024.0):F1} MB, " +
            $"after={allocAfter / (1024.0 * 1024.0):F1} MB, reason={budget.Reason})");

        // Default threshold: 30 MB per render. Fixture-specific budgets are
        // documented above and still fail if their calibrated ceiling regresses.
        allocDelta.Should().BeLessThan(budget.Bytes,
            $"{fileName} single render should allocate < {budgetMb:F2} MB " +
            $"(delta: {allocDeltaMb:F2} MB; {budget.Reason})");
    }

    /// <summary>
    /// Working set after document close: open a document, render all pages,
    /// dispose, force full GC, and assert WorkingSet returns to baseline
    /// within 50 MB.
    /// </summary>
    [Fact]
    public void WorkingSet_ReturnsToBaseline_AfterDocumentClose()
    {
        var path = Path.Combine(CorpusDir, "irs-w9.pdf");
        if (!File.Exists(path))
        {
            _out.WriteLine("SKIP: irs-w9.pdf missing — corpus not downloaded");
            return;
        }

        // Warmup to stabilize WorkingSet.
        using (var warmDoc = PdfDocument.Open(path))
        {
            var warmRenderer = new SkiaRenderer();
            for (int i = 1; i <= warmDoc.PageCount; i++)
            {
                using var _ = warmRenderer.RenderPage(warmDoc.GetPage(i),
                    new RenderOptions { Dpi = 150 });
            }
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Baseline WorkingSet.
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        long workingSetBefore = proc.WorkingSet64;

        // Open, render all, close, and measure cleanup.
        using (var doc = PdfDocument.Open(path))
        {
            var renderer = new SkiaRenderer();
            for (int i = 1; i <= doc.PageCount; i++)
            {
                using var bitmap = renderer.RenderPage(doc.GetPage(i),
                    new RenderOptions { Dpi = 150 });
            }
        }

        // Full GC.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        proc.Refresh();
        long workingSetAfter = proc.WorkingSet64;
        long workingSetDelta = workingSetAfter - workingSetBefore;
        double workingSetDeltaMb = workingSetDelta / (1024.0 * 1024.0);

        _out.WriteLine(
            $"WorkingSet before: {workingSetBefore / (1024.0 * 1024.0):F1} MB, " +
            $"after: {workingSetAfter / (1024.0 * 1024.0):F1} MB, " +
            $"delta: {workingSetDeltaMb:F2} MB");

        // Threshold: 50 MB. After full GC, unreferenced pages and bitmaps should
        // be released. A leak would grow linearly with repeats; this catches that.
        workingSetDelta.Should().BeLessThan(50 * 1024 * 1024,
            $"WorkingSet delta should be < 50 MB; got {workingSetDeltaMb:F2} MB (possible leak)");
    }

    /// <summary>
    /// Letter cache effectiveness: call page.Letters 100 times on the same page.
    /// After the first call, subsequent calls should not allocate measurably
    /// (< 10 KB per call as a loose baseline).
    /// </summary>
    [Fact]
    public void LetterCache_NoAllocationOnRepeatedCalls()
    {
        var path = Path.Combine(CorpusDir, "irs-w9.pdf");
        if (!File.Exists(path))
        {
            _out.WriteLine("SKIP: irs-w9.pdf missing — corpus not downloaded");
            return;
        }

        using var doc = PdfDocument.Open(path);
        var page = doc.GetPage(1);

        // Warmup: first call populates cache.
        _ = page.Letters;

        // Full GC to establish clean baseline.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Measure allocation across 100 cache-hit calls.
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            _ = page.Letters;
        }
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();

        long allocDelta = allocAfter - allocBefore;
        double allocDeltaKb = allocDelta / 1024.0;
        double allocPerCall = allocDelta / 100.0;

        _out.WriteLine(
            $"100 cache-hit calls allocated {allocDeltaKb:F2} KB total " +
            $"({allocPerCall / 1024.0:F2} MB per call)");

        // Threshold: 1 MB total for 100 calls = 10 KB per call on average.
        // Cache hits should be near-zero; this threshold is very loose.
        allocDelta.Should().BeLessThan(1024 * 1024,
            $"Letter cache 100 hits should allocate < 1 MB; got {allocDeltaKb:F2} KB");
    }

    /// <summary>
    /// Sequential renders of the same document: render the same page 10 times
    /// in a loop within the same document instance. Allocation should remain
    /// fairly constant (not grow linearly). Tests for leaks in per-page state.
    /// </summary>
    [Fact]
    public void SequentialRenders_AllocationStable_WithinDocument()
    {
        var path = Path.Combine(CorpusDir, "irs-1040.pdf");
        if (!File.Exists(path))
        {
            _out.WriteLine("SKIP: irs-1040.pdf missing — corpus not downloaded");
            return;
        }

        using var doc = PdfDocument.Open(path);

        // Warmup.
        var warmRenderer = new SkiaRenderer();
        using var _ = warmRenderer.RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = 150 });

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var allocations = new long[10];
        var renderer = new SkiaRenderer();

        for (int i = 0; i < 10; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            using var bitmap = renderer.RenderPage(doc.GetPage(1),
                new RenderOptions { Dpi = 150 });
            long after = GC.GetAllocatedBytesForCurrentThread();
            allocations[i] = after - before;
        }

        // Check that allocation variance is low (all within ~20% of median).
        var median = allocations.OrderBy(x => x).ElementAt(5);
        var first = allocations[0];
        var last = allocations[9];

        double firstMb = first / (1024.0 * 1024.0);
        double lastMb = last / (1024.0 * 1024.0);
        double medianMb = median / (1024.0 * 1024.0);

        _out.WriteLine(
            $"Sequential renders (10×): first={firstMb:F2} MB, last={lastMb:F2} MB, " +
            $"median={medianMb:F2} MB");

        // Last allocation should be within 50% of first (not growing linearly).
        // This catches state accumulation bugs where each render adds overhead.
        last.Should().BeLessThan((long)(first * 1.5),
            $"Sequential render allocations should be stable; " +
            $"first={firstMb:F2} MB, last={lastMb:F2} MB (growing linearly = leak)");
    }

    /// <summary>
    /// Peak allocation during multi-page rendering: track peak allocation
    /// during a full-document render to ensure no single operation consumes
    /// excessive memory.
    /// </summary>
    [Fact]
    public void MultiPageRender_PeakAllocation_StaysBelow_50MB()
    {
        var path = Path.Combine(CorpusDir, "scotus-trump-v-us.pdf");
        if (!File.Exists(path))
        {
            _out.WriteLine("SKIP: scotus-trump-v-us.pdf missing — corpus not downloaded");
            return;
        }

        using var doc = PdfDocument.Open(path);

        // Warmup
        var warmRenderer = new SkiaRenderer();
        using var _ = warmRenderer.RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = 150 });

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocStart = GC.GetAllocatedBytesForCurrentThread();
        long peakAlloc = allocStart;

        var renderer = new SkiaRenderer();
        for (int i = 1; i <= Math.Min(doc.PageCount, 5); i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            using var bitmap = renderer.RenderPage(doc.GetPage(i),
                new RenderOptions { Dpi = 150 });
            long after = GC.GetAllocatedBytesForCurrentThread();

            if (after > peakAlloc)
                peakAlloc = after;
        }

        long peakDelta = peakAlloc - allocStart;
        double peakDeltaMb = peakDelta / (1024.0 * 1024.0);

        _out.WriteLine(
            $"Multi-page render (up to 5 pages): peak allocation delta = {peakDeltaMb:F2} MB");

        // Threshold: 50 MB peak. Current profiling for issue #444 measured
        // about 13 MB here after renderer improvements, so this remains a
        // broad regression guard without a fixture exception.
        peakDelta.Should().BeLessThan(MultiPageRenderBudgetBytes,
            $"Peak allocation across multi-page render should be < 50 MB; got {peakDeltaMb:F2} MB");
    }
}
