using System;
using System.Diagnostics;
using System.IO;
using AwesomeAssertions;
using Excise.Core.Document;
using Xunit;
namespace Excise.Rendering.Tests;

/// <summary>
/// Performance baselines for the Skia render pipeline against real-world PDFs.
///
/// These run in the normal test suite (not a separate benchmark project) so
/// every CI run captures the timing trend. Thresholds are loose enough to
/// avoid CI flakiness (machines are noisy) but tight enough that a 2-3×
/// regression fails the build.
///
/// All baselines were calibrated against an Ubuntu 26.04 / .NET 10 / Skia
/// 2.88.9 reference machine. Adjust thresholds if the test agent runs on
/// significantly slower hardware.
/// </summary>
public class PerformanceBenchmarkTests
{
    private readonly ITestOutputHelper _out;
    public PerformanceBenchmarkTests(ITestOutputHelper o) { _out = o; }

    private static readonly string CorpusDir = ResolveCorpusDir();

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

    [Theory]
    [InlineData("irs-w9.pdf",                          1, 800)]   // small form
    [InlineData("irs-1040.pdf",                        1, 800)]   // small form
    [InlineData("scotus-trump-v-us.pdf",               1, 800)]   // judgment
    [InlineData("state-ds82-passport-renewal.pdf",     1, 1500)]  // CFF-heavy
    [InlineData("cdc-vis-covid-19.pdf",                1, 800)]
    public void RenderSinglePage_StaysUnder_Threshold(string fileName, int page, int maxMs)
    {
        var path = Path.Combine(CorpusDir, fileName);
        if (!File.Exists(path))
        {
            _out.WriteLine($"SKIP: {path} missing — corpus not downloaded");
            return;
        }

        // Warm up: open + render once so JIT and font caches don't dominate.
        using (var warmDoc = PdfDocument.Open(path))
        {
            var warmRenderer = new SkiaRenderer();
            using var _ = warmRenderer.RenderPage(warmDoc.GetPage(page),
                new RenderOptions { Dpi = 150 });
        }

        // Measure: median of 3 runs to absorb GC jitter.
        var times = new long[3];
        for (int i = 0; i < 3; i++)
        {
            using var doc = PdfDocument.Open(path);
            var renderer = new SkiaRenderer();
            var sw = Stopwatch.StartNew();
            using var bitmap = renderer.RenderPage(doc.GetPage(page),
                new RenderOptions { Dpi = 150 });
            sw.Stop();
            times[i] = sw.ElapsedMilliseconds;
        }
        Array.Sort(times);
        var median = times[1];

        _out.WriteLine($"{fileName,-45} median={median}ms  runs=[{times[0]}, {times[1]}, {times[2]}]");
        median.Should().BeLessThan(maxMs,
            $"{fileName} render at 150 DPI should stay under {maxMs}ms");
    }

    [Fact]
    public void OpenDocument_StaysUnder_300ms_ForSmallForm()
    {
        var path = Path.Combine(CorpusDir, "irs-w9.pdf");
        if (!File.Exists(path)) return;

        // Warm
        using (PdfDocument.Open(path)) { }

        var sw = Stopwatch.StartNew();
        using var doc = PdfDocument.Open(path);
        sw.Stop();

        _out.WriteLine($"Open(irs-w9.pdf): {sw.ElapsedMilliseconds}ms (warmed)");
        sw.ElapsedMilliseconds.Should().BeLessThan(300,
            "small form open should be near-instant after warmup");
    }

    [Fact]
    public void TextExtraction_StaysUnder_500ms_PerSmallPage()
    {
        var path = Path.Combine(CorpusDir, "irs-w9.pdf");
        if (!File.Exists(path)) return;

        using var doc = PdfDocument.Open(path);
        var page = doc.GetPage(1);

        // Warm
        _ = page.Letters;

        var sw = Stopwatch.StartNew();
        var letters = page.Letters;
        sw.Stop();

        _out.WriteLine($"Letters(irs-w9.pdf p1): {letters.Count} letters in {sw.ElapsedMilliseconds}ms");
        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            "small-page letter extraction should be sub-half-second");
    }
}
