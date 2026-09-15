using System;
using System.Diagnostics;
using System.IO;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
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

    [Fact]
    public void SaveRoundTrip_StaysUnder_OneSecond_ForSmallForm()
    {
        var path = Path.Combine(CorpusDir, "irs-w9.pdf");
        Assert.SkipUnless(File.Exists(path), "irs-w9.pdf is required for the save performance budget");

        // Warm parsing and writer JIT separately from the measured save.
        using (var warm = PdfDocument.Open(path))
        using (var warmOutput = new MemoryStream())
            warm.Save(warmOutput);

        using var document = PdfDocument.Open(path);
        using var output = new MemoryStream();
        var sw = Stopwatch.StartNew();
        document.Save(output);
        sw.Stop();

        _out.WriteLine($"Save(irs-w9.pdf): {output.Length} bytes in {sw.ElapsedMilliseconds}ms (warmed)");
        output.Length.Should().BeGreaterThan(0, "the timed save must produce a file");
        sw.ElapsedMilliseconds.Should().BeLessThan(1_000,
            "a warmed save of the small office-form fixture should remain interactive");
    }

    [Fact]
    public void AcroFormFillAndSave_StaysUnder_OneSecond_ForSmallForm()
    {
        var path = Path.Combine(CorpusDir, "irs-w9.pdf");
        Assert.SkipUnless(File.Exists(path), "irs-w9.pdf is required for the AcroForm performance budget");

        using (var warm = PdfDocument.Open(path))
        {
            var warmField = warm.GetAcroForm()?.Fields.FirstOrDefault(field => !field.IsReadOnly);
            Assert.SkipUnless(warmField != null, "irs-w9.pdf must expose an editable AcroForm field");
            warmField.SetValue(warmField.Value ?? "Benchmark");
            using var warmOutput = new MemoryStream();
            warm.Save(warmOutput);
        }

        using var document = PdfDocument.Open(path);
        var field = document.GetAcroForm()?.Fields.FirstOrDefault(candidate => !candidate.IsReadOnly);
        Assert.SkipUnless(field != null, "irs-w9.pdf must expose an editable AcroForm field");
        using var output = new MemoryStream();
        var sw = Stopwatch.StartNew();
        field.SetValue(field.Value ?? "Benchmark");
        document.Save(output);
        sw.Stop();

        _out.WriteLine($"AcroFormFillAndSave(irs-w9.pdf): {output.Length} bytes in {sw.ElapsedMilliseconds}ms (warmed)");
        output.Length.Should().BeGreaterThan(0, "the timed form workflow must produce a file");
        sw.ElapsedMilliseconds.Should().BeLessThan(1_000,
            "a warmed ordinary form fill-and-save should remain interactive");
    }

    /// <summary>
    /// #919 guard: a common term on irs-w9.pdf (391 matches) is removed through
    /// one batched content-stream rewrite per pass, not one rewrite per match.
    /// </summary>
    /// <remarks>
    /// <para><b>Why allocation, not wall clock (#1475).</b> This used to assert a
    /// 5 s wall-clock budget. The Debug test host measures 3.7-7.4 s for the
    /// current code on an idle-ish machine and 13.0 s while a full suite runs
    /// alongside, so the budget went red on develop 3 times in 4 and could not
    /// tell the regression from load. The per-match path re-parses and
    /// re-serialises the page's content stream once per match, so what it
    /// multiplies is work per match — and allocation on the calling thread
    /// tracks that work without depending on how busy the machine is.</para>
    ///
    /// <para><b>Measured 2026-09-15</b> (Debug, M5 MacBook, a 3-hour full suite
    /// running in another checkout): current code allocates 865 MiB on this
    /// thread (13.0 s wall); with the batched <c>RedactAreasInternal</c> call
    /// reverted to one call per match — the #919 shape — it allocates
    /// 5,915 MiB (49.3 s wall). The 2 GiB ceiling is 2.4x above the current
    /// code and 2.9x below the regression. The redaction path starts no
    /// threads (no Parallel/Task.Run/ThreadPool in Excise.Core/Redaction or
    /// Text), so this thread sees all of its allocation; the floor fails if
    /// that ever stops being true, because the ceiling would then pass
    /// vacuously.</para>
    ///
    /// <para>Wall time is still printed as a diagnostic, but it is not asserted.</para>
    /// </remarks>
    [Fact]
    public void RedactText_CommonTermOnW9_StaysOnTheBatchedRewritePath()
    {
        var path = Path.Combine(CorpusDir, "irs-w9.pdf");
        Assert.SkipUnless(File.Exists(path),
            "irs-w9.pdf is required for the #919 common-term redaction budget");

        // Warm parser, text extraction, and redaction JIT without mutating the
        // measured document, so one-time JIT and static-table allocation does
        // not count against the budget.
        using (var warm = PdfDocument.Open(path))
            warm.RedactText("ZzzzNoSuchStringZzzz", drawBlackRect: false);

        const double CeilingMiB = 2048;
        const double FloorMiB = 100;

        using var document = PdfDocument.Open(path);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var matches = document.RedactText("the").VerifiedRemovals;
        sw.Stop();
        var allocatedMiB = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / (1024.0 * 1024.0);

        var survivingText = string.Join('\n', Enumerable.Range(1, document.PageCount)
            .Select(pageNumber => document.GetPage(pageNumber).Text));

        _out.WriteLine(
            $"RedactText(irs-w9.pdf, 'the'): {matches} matches, {allocatedMiB:F1} MiB allocated on the calling thread, {sw.ElapsedMilliseconds}ms wall (diagnostic)");
        matches.Should().BeGreaterThan(200,
            "the common-term fixture must exercise batching rather than pass vacuously");
        allocatedMiB.Should().BeLessThan(CeilingMiB,
            "#919: common-term redaction must not return to the per-match rewrite path " +
            "(measured 865 MiB batched vs 5,915 MiB per-match)");
        allocatedMiB.Should().BeGreaterThan(FloorMiB,
            "the redaction must run on the calling thread for the allocation ceiling to mean anything; " +
            "if this fails, the work moved to other threads or got far leaner, so re-measure both numbers in the remarks");
        survivingText.Contains("the", StringComparison.OrdinalIgnoreCase).Should().BeFalse(
            "the timed operation must still complete the requested redaction");
        survivingText.Should().Contain("1099-INT").And.Contain("1099-DIV")
            .And.Contain("1099-S").And.Contain("1099-B",
                "the latency fix must not restore the stale-letter collateral regression");
    }
}
