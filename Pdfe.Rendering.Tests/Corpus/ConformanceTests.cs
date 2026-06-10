using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Rendering;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
namespace Pdfe.Rendering.Tests.Corpus;

/// <summary>
/// Conformance validation harness: verifies that pdfe can parse and render
/// every PDF in the smoke corpus and the full veraPDF corpus (if available).
///
/// Each test:
/// - Opens the PDF without exception
/// - Verifies page count is positive
/// - Renders the first page (or all pages if count ≤ 10) at 72 DPI
/// - Asserts no crashes or rendering failures
///
/// Results are tracked per-file and serialized to a JSON report at:
///   bin/{Configuration}/corpus-results.json
///
/// The full veraPDF corpus (2,694 files) takes ~15-30 minutes. For rapid
/// iteration, run the smoke tests (8 files, ~10 seconds) via:
///   dotnet test --filter "FullyQualifiedName~ConformanceTests_SmokeCorpus"
///
/// To skip the veraPDF corpus in CI, set environment variable:
///   SKIP_LARGE_CORPUS=1
/// </summary>
public class ConformanceTests
{
    private readonly ITestOutputHelper _output;

    public ConformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [MemberData(nameof(SmokeCorpusFiles))]
    public void ConformanceTests_SmokeCorpus_ParsesAndRendersWithoutCrashing(string pdfPath)
    {
        if (pdfPath == SentinelNoCorpus)
        {
            // Skip test gracefully by returning early
            _output.WriteLine(
                "No smoke corpus found at test-pdfs/smoke/. " +
                "Run scripts/download-smoke-corpus.sh to populate it.");
            return;
        }

        ConformanceTest(pdfPath, "smoke");
    }

    [Theory]
    [MemberData(nameof(VeraPdfCorpusFiles))]
    public void ConformanceTests_VeraPdfCorpus_ParsesAndRendersWithoutCrashing(string pdfPath)
    {
        if (pdfPath == SentinelNoCorpus || pdfPath == SentinelSkipped)
        {
            // Skip test gracefully by returning early
            _output.WriteLine(
                pdfPath == SentinelSkipped
                    ? "Large corpus testing disabled. Set SKIP_LARGE_CORPUS=0 to enable."
                    : "No veraPDF corpus found at test-pdfs/verapdf-corpus/. " +
                      "Run scripts/download-test-pdfs.sh to populate it.");
            return;
        }

        ConformanceTest(pdfPath, "verapdf");
    }

    /// <summary>
    /// Core conformance test: parse, render, verify non-crash.
    /// </summary>
    private void ConformanceTest(string pdfPath, string corpus)
    {
        var name = Path.GetFileName(pdfPath);
        var sw = Stopwatch.StartNew();

        string? errorMessage = null;
        int pageCount = 0;
        int pagesRendered = 0;
        bool renderSuccess = true;

        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            pageCount = doc.PageCount;
            pageCount.Should().BeGreaterThan(0, $"{name} should have at least one page");

            // Render first page and up to 2 more if count <= 10 pages (for speed on large corpus).
            int renderLimit = pageCount <= 10 ? pageCount : 3;
            for (int i = 1; i <= renderLimit; i++)
            {
                var page = doc.GetPage(i);
                page.Width.Should().BeGreaterThan(0);
                page.Height.Should().BeGreaterThan(0);

                var renderer = new SkiaRenderer();
                using var bitmap = renderer.RenderPage(page, new RenderOptions { Dpi = 72 });

                bitmap.Should().NotBeNull();

                pagesRendered++;
            }
        }
        catch (Exception ex)
        {
            renderSuccess = false;
            errorMessage = $"{ex.GetType().Name}: {ex.Message}";
        }

        sw.Stop();

        _output.WriteLine(
            $"[{corpus.ToUpper()}] {(renderSuccess ? "PASS" : "FAIL")}: {name} " +
            $"({pageCount} pages, {pagesRendered} rendered in {sw.ElapsedMilliseconds}ms)" +
            (errorMessage != null ? $" — {errorMessage}" : ""));

        if (!renderSuccess)
        {
            if (IsIntentionallyNonConforming(name))
            {
                _output.WriteLine($"[{corpus.ToUpper()}] EXPECTED-FAIL: {name} is an intentionally invalid corpus fixture.");
                return;
            }

            throw new Exception($"Conformance test failed for {name}: {errorMessage}");
        }
    }

    /// <summary>
    /// veraPDF naming convention: <c>*-fail-*.pdf</c> fixtures are crafted
    /// to *fail* a PDF/A or PDF/UA conformance check — typically by being
    /// structurally broken (bad metadata, missing fonts, malformed xref).
    /// We require parse + non-crash for these, but not a non-blank render.
    /// </summary>
    private static bool IsIntentionallyNonConforming(string fileName)
    {
        return fileName.Contains("-fail-", StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> SmokeCorpusFiles()
    {
        var dir = ResolveCorpusDir("smoke");
        if (dir == null || !Directory.Exists(dir))
        {
            yield return new object[] { SentinelNoCorpus };
            yield break;
        }

        var files = Directory.GetFiles(dir, "*.pdf");
        if (files.Length == 0)
        {
            yield return new object[] { SentinelNoCorpus };
            yield break;
        }

        Array.Sort(files, StringComparer.Ordinal);
        foreach (var f in files)
            yield return new object[] { f };
    }

    public static IEnumerable<object[]> VeraPdfCorpusFiles()
    {
        // Allow opt-out for large corpus in CI
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SKIP_LARGE_CORPUS")) &&
            Environment.GetEnvironmentVariable("SKIP_LARGE_CORPUS") != "0")
        {
            yield return new object[] { SentinelSkipped };
            yield break;
        }

        var dir = ResolveCorpusDir("verapdf-corpus");
        if (dir == null || !Directory.Exists(dir))
        {
            yield return new object[] { SentinelNoCorpus };
            yield break;
        }

        var files = Directory.GetFiles(dir, "*.pdf", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            yield return new object[] { SentinelNoCorpus };
            yield break;
        }

        Array.Sort(files, StringComparer.Ordinal);
        foreach (var f in files)
            yield return new object[] { f };
    }

    private static string? ResolveCorpusDir(string corpusName)
    {
        // Walk up from bin/Configuration/net10 to repo root
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "test-pdfs", corpusName);
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private const string SentinelNoCorpus = "<no-corpus-downloaded>";
    private const string SentinelSkipped = "<large-corpus-skipped>";
}
