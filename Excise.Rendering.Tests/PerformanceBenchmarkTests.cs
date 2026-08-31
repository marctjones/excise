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

    [Fact]
    public void RedactText_CommonTermOnW9_StaysUnderFiveSeconds()
    {
        var path = Path.Combine(CorpusDir, "irs-w9.pdf");
        Assert.SkipUnless(File.Exists(path),
            "irs-w9.pdf is required for the #919 common-term redaction budget");

        // Warm parser, text extraction, and redaction JIT without mutating the
        // measured document. The old per-match path took 7-10 seconds after
        // warmup, so a five-second budget leaves machine-noise headroom while
        // still catching that algorithmic regression.
        using (var warm = PdfDocument.Open(path))
            warm.RedactText("ZzzzNoSuchStringZzzz", drawBlackRect: false);

        using var document = PdfDocument.Open(path);
        var sw = Stopwatch.StartNew();
        var matches = document.RedactText("the").VerifiedRemovals;
        sw.Stop();

        var survivingText = string.Join('\n', Enumerable.Range(1, document.PageCount)
            .Select(pageNumber => document.GetPage(pageNumber).Text));

        _out.WriteLine($"RedactText(irs-w9.pdf, 'the'): {matches} matches in {sw.ElapsedMilliseconds}ms");
        matches.Should().BeGreaterThan(200,
            "the common-term fixture must exercise batching rather than pass vacuously");
        sw.ElapsedMilliseconds.Should().BeLessThan(5_000,
            "#919: common-term redaction must not return to the 7-10 second per-match rewrite path");
        survivingText.Contains("the", StringComparison.OrdinalIgnoreCase).Should().BeFalse(
            "the timed operation must still complete the requested redaction");
        survivingText.Should().Contain("1099-INT").And.Contain("1099-DIV")
            .And.Contain("1099-S").And.Contain("1099-B",
                "the latency fix must not restore the stale-letter collateral regression");
    }
}
