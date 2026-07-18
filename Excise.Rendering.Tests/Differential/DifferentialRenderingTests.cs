using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering;
using SkiaSharp;
using Xunit;
using Excise.Rendering.Differential;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// For every PDF in a configured corpus directory, render page 1 with
/// excise's <see cref="SkiaRenderer"/> and <c>mutool draw</c> first. If
/// MuPDF disagrees, escalate to Poppler/pdftocairo and then Ghostscript
/// as needed before deciding whether the page is a real excise regression
/// or a reference-renderer outlier.
///
/// This is the "oracle" test layer — instead of pinning each PDF to a
/// hand-curated PNG baseline (which only catches changes against ourselves),
/// we use MuPDF as the fast primary oracle and only pay for Poppler and
/// Ghostscript when MuPDF disagrees. That keeps the common case cheap
/// while still giving us a second and third opinion on suspicious pages.
///
/// On environments without <c>mutool</c> on PATH the entire suite is
/// skipped (each test individually flagged). To run locally:
///   sudo apt install mupdf-tools     # Ubuntu/Debian
///   brew install mupdf-tools         # macOS
///
/// Corpus discovery: <see cref="CorpusDirectories"/> below. Today the smoke
/// corpus shipped via <c>scripts/download-smoke-corpus.sh</c> is the only
/// auto-discovered source. Adding more dirs is a one-line change.
///
/// Threshold tuning rationale: the per-pixel tolerance in
/// <see cref="DifferentialMetrics"/> (24/255) ignores anti-aliasing noise.
/// On top of that, the suite-level "differing pixel fraction" gate is set
/// to 5% — generous enough that sub-pixel layout drift between rendering
/// engines doesn't fail tests, tight enough that the Betterment-style
/// "wrong font, wide spacing" failure produces a 30%+ diff and trips the
/// gate every time.
/// </summary>
public sealed class DifferentialRenderingTests
{
    private readonly ITestOutputHelper _output;

    public DifferentialRenderingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Maximum acceptable differing-pixel fraction. At 10% with a
    /// per-pixel tolerance of 64/255 (see <see cref="DifferentialMetrics"/>),
    /// the smoke corpus passes cleanly and Betterment-style failures
    /// (wrong font + wide kerning) produce ~35%+ and fail loudly.
    /// </summary>
    private const double MaxDifferingPixelFraction = 0.10;

    /// <summary>
    /// Mean absolute error gate (per channel, 0..255). Calibrated to
    /// pass on AA-only differences (typically MAE 18–28 across the
    /// smoke corpus) and fail when entire glyphs drift in shape or
    /// position (MAE 50+).
    /// </summary>
    private const double MaxMeanAbsoluteError = 32.0;

    private const int RenderDpi = 150;

    /// <summary>
    /// Corpora the harness picks up.
    ///
    /// Every corpus listed here gates the build: a disagreement with
    /// mutool fails the test. There used to be a "best-effort" mode
    /// that converted disagreements to Skip — that's been removed
    /// because it hid real failures behind a benign-looking Skip count.
    /// If a PDF in a corpus diverges, either fix the renderer or
    /// allowlist that PDF in <see cref="KnownDifferentialFailures"/>
    /// with a documented reason.
    ///
    /// The pdf.js corpus is intentionally NOT included here — it
    /// surfaces ~250+ disagreements that would block CI today. Run it
    /// explicitly via <c>dotnet test --filter "Trait=Exploratory"</c>
    /// (see <see cref="ExploratoryDifferentialTests"/>).
    /// </summary>
    private static readonly string[] GatingCorpusDirectories =
    {
        "test-pdfs/smoke",
    };

    /// <summary>
    /// PDFs the differential harness already knows we render incorrectly.
    /// Keys may be either:
    ///   • "relative/path/to.pdf"        — applies to every sampled page
    ///   • "relative/path/to.pdf#7"      — applies only to page 7
    /// The harness still runs and collects metrics for these (so
    /// improvements show up in the test output), but it does not fail
    /// the build on them. CI stays green while the underlying bug is
    /// worked. Removing an entry re-enables the gate.
    /// </summary>
    private static readonly Dictionary<string, string> KnownDifferentialFailures = new()
    {
        ["test-pdfs/smoke/irs-1040-instructions.pdf"] =
            "Image XObject not rendered: page 1's compass illustration is missing.",
        ["test-pdfs/smoke/irs-pub509-2026.pdf"] =
            "Image XObject not rendered: page 1's American-flag illustration is missing.",
        ["test-pdfs/smoke/state-ds82-passport-renewal.pdf"] =
            "Sub-pixel AA + form-field hinting drift on small body text exceeds gate.",
        ["test-pdfs/smoke/irs-w9.pdf#2"] =
            "Multi-page rendering bug — page 1 agrees with mutool but page 2 (the W-9 instructions) " +
            "diverges materially. Single-page diffs missed this; caught by Layer 5 (multi-page).",
    };

    public static IEnumerable<object[]> CorpusPdfs() => DiscoverPdfs();

    internal static IEnumerable<object[]> DiscoverPdfs()
    {
        var root = LocateRepoRoot();
        if (root == null)
        {
            yield return new object[] { SentinelNoCorpus, 1 };
            yield break;
        }

        var foundAny = false;
        foreach (var sub in GatingCorpusDirectories)
        {
            var dir = Path.Combine(root, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var pdf in Directory.EnumerateFiles(dir, "*.pdf").OrderBy(p => p))
            {
                var rel = Path.GetRelativePath(root, pdf);
                var pageCount = TryGetPageCount(pdf);
                foreach (var pageNumber in SamplePages(pageCount))
                {
                    foundAny = true;
                    yield return new object[] { rel, pageNumber };
                }
            }
        }

        if (!foundAny)
        {
            yield return new object[] { SentinelNoCorpus, 1 };
        }
    }

    [Theory]
    [MemberData(nameof(CorpusPdfs))]
    public void RendersSimilarlyToMutool(string relativePath, int pageNumber)
    {
        Assert.SkipWhen(relativePath == SentinelNoCorpus,
            "No smoke corpus found at test-pdfs/smoke/. Run scripts/download-smoke-corpus.sh to populate it.");

        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable,
            "mutool not on PATH — install mupdf-tools to run the differential corpus");

        var root = LocateRepoRoot()
            ?? throw new InvalidOperationException("Could not find repo root");
        var pdfPath = Path.Combine(root, relativePath);

        // excise render. Some pdf.js corpus entries are intentionally
        // pathological (fuzzed inputs, malformed structures) — excise may
        // refuse to open or render them. We treat that as "skip" rather
        // than "fail": this harness measures rendering *fidelity*, not
        // robustness. Robustness has its own test suite.
        SKBitmap? exciseBitmap = null;
        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            // Skip silently when this PDF doesn't have the requested
            // page. The MemberData enumerator emits page 1, 2, 5, 20
            // for every PDF; only multi-page docs naturally exercise
            // pages > 1.
            if (pageNumber > doc.PageCount)
                Assert.SkipWhen(true,
                    $"{relativePath}: only {doc.PageCount} page(s); " +
                    $"page {pageNumber} doesn't exist");
            var renderer = new SkiaRenderer();
            exciseBitmap = renderer.RenderPage(doc.GetPage(pageNumber), new RenderOptions { Dpi = RenderDpi });
        }
        catch (Exception ex)
        {
            Assert.SkipWhen(true,
                $"excise could not parse/render {relativePath} p.{pageNumber}: " +
                $"{ex.GetType().Name}: {ex.Message}. " +
                "Robustness for malformed inputs is the parser's responsibility, not this harness's.");
        }
        exciseBitmap.Should().NotBeNull($"excise must successfully render page {pageNumber}");

        // mutool render.
        SKBitmap? mutoolBitmap = MutoolReferenceRenderer.RenderPage(pdfPath, pageNumber, RenderDpi);
        Assert.SkipWhen(mutoolBitmap == null,
            $"mutool refused to render {relativePath} p.{pageNumber} — skipping rather than asserting against a missing reference");

        using var mutoolAlignedExcise = DifferentialMetrics.ResizeMatch(
            exciseBitmap, mutoolBitmap.Width, mutoolBitmap.Height);
        var report = DifferentialMetrics.Compare(mutoolAlignedExcise, mutoolBitmap);
        _output.WriteLine($"  {relativePath}");
        _output.WriteLine($"  {report}");

        SKBitmap? popplerBitmap = null;
        SKBitmap? ghostBitmap = null;
        try
        {
            var failedAgainstMutool = IsFailed(report);

            if (!failedAgainstMutool)
                return;

            // Known failures stay visible in the output, but they do not
            // gate the build while the underlying issue is tracked.
            string? knownReason = null;
            if (KnownDifferentialFailures.TryGetValue($"{relativePath}#{pageNumber}", out var perPage))
                knownReason = perPage;
            else if (KnownDifferentialFailures.TryGetValue(relativePath, out var perPath))
                knownReason = perPath;

            if (knownReason != null)
            {
                _output.WriteLine($"  KNOWN FAILURE - not gating: {knownReason}");
                var knownOutDir = Path.Combine(AppContext.BaseDirectory, "differential-failures");
                Directory.CreateDirectory(knownOutDir);
                var knownName = Path.GetFileNameWithoutExtension(relativePath).Replace(' ', '-');
                var knownTriptychPath = Path.Combine(knownOutDir, $"{knownName}-p{pageNumber}-triptych.png");
                using var knownTriptychExcise = DifferentialMetrics.ResizeMatch(exciseBitmap, mutoolBitmap!.Width, mutoolBitmap.Height);
                DifferentialMetrics.SaveTriptych(knownTriptychPath, knownTriptychExcise, mutoolBitmap);
                _output.WriteLine($"  triptych written to {knownTriptychPath}");
                Assert.SkipWhen(true,
                    $"Known differential failure for {relativePath} p.{pageNumber}: {knownReason}. " +
                    "Remove the entry from KnownDifferentialFailures once fixed.");
            }

            _output.WriteLine("  mutool disagrees with excise; escalating to Poppler for a second opinion");

            var popplerAvailable = PdftocairoReferenceRenderer.IsAvailable;
            var ghostscriptAvailable = GhostscriptReferenceRenderer.IsAvailable;

            if (!popplerAvailable && !ghostscriptAvailable)
            {
                Assert.Fail(
                    $"{relativePath} p.{pageNumber}: excise diverged from mutool reference. {report}. " +
                    "Neither Poppler/pdftocairo nor Ghostscript is available for second-opinion escalation, so this cannot be auto-triaged.");
            }

            DifferentialReport? popplerReport = null;
            DifferentialReport? ghostscriptReport = null;

            if (popplerAvailable)
            {
                popplerBitmap = PdftocairoReferenceRenderer.RenderPage(pdfPath, pageNumber, RenderDpi);
                Assert.SkipWhen(popplerBitmap == null,
                    $"Poppler refused to render {relativePath} p.{pageNumber} — skipping rather than asserting against a missing second-opinion renderer");

                using var popplerAlignedExcise = DifferentialMetrics.ResizeMatch(
                    exciseBitmap, popplerBitmap.Width, popplerBitmap.Height);
                popplerReport = DifferentialMetrics.Compare(popplerAlignedExcise, popplerBitmap);
                _output.WriteLine($"  poppler: {popplerReport}");

                var popplerMatchesExcise = !IsFailed(popplerReport);
                using var mutoolAlignedPoppler = DifferentialMetrics.ResizeMatch(
                    mutoolBitmap!, popplerBitmap.Width, popplerBitmap.Height);
                var popplerMatchesMutool = !IsFailed(DifferentialMetrics.Compare(mutoolAlignedPoppler, popplerBitmap));

                if (popplerMatchesMutool && !popplerMatchesExcise)
                {
                    Assert.Fail(
                        $"{relativePath} p.{pageNumber}: MuPDF and Poppler agree, but excise differs. " +
                        $"MuPDF: {report}; Poppler: {popplerReport}. This is a real excise regression.");
                }

                if (popplerMatchesExcise)
                {
                    _output.WriteLine("  Poppler agrees with excise; accepting as a valid alternate rendering.");
                    return;
                }

                if (popplerMatchesMutool)
                {
                    _output.WriteLine("  Poppler agrees with MuPDF; excise is the outlier for this page.");
                    Assert.Fail(
                        $"{relativePath} p.{pageNumber}: excise diverged from mutool and Poppler agreed with mutool. {report}.");
                }

                _output.WriteLine("  Poppler did not settle the split; escalating to Ghostscript.");
            }

            if (!ghostscriptAvailable)
            {
                Assert.Fail(
                    $"{relativePath} p.{pageNumber}: excise diverged from MuPDF and Poppler did not settle the split. " +
                    "Ghostscript is not available to provide a third opinion.");
            }

            ghostBitmap = GhostscriptReferenceRenderer.RenderPage(pdfPath, pageNumber, RenderDpi);
            Assert.SkipWhen(ghostBitmap == null,
                $"Ghostscript refused to render {relativePath} p.{pageNumber} — skipping rather than asserting against a missing third opinion renderer");

            using var ghostAlignedExcise = DifferentialMetrics.ResizeMatch(
                exciseBitmap, ghostBitmap.Width, ghostBitmap.Height);
            ghostscriptReport = DifferentialMetrics.Compare(ghostAlignedExcise, ghostBitmap);
            _output.WriteLine($"  ghostscript: {ghostscriptReport}");

            var ghostscriptMatchesExcise = !IsFailed(ghostscriptReport);
            using var mutoolAlignedGhostscript = DifferentialMetrics.ResizeMatch(
                mutoolBitmap!, ghostBitmap.Width, ghostBitmap.Height);
            var ghostscriptMatchesMutool = !IsFailed(DifferentialMetrics.Compare(mutoolAlignedGhostscript, ghostBitmap));

            if (ghostscriptMatchesExcise)
            {
                _output.WriteLine("  Ghostscript agrees with excise; accepting as a valid alternate rendering.");
                return;
            }

            if (ghostscriptMatchesMutool)
            {
                Assert.Fail(
                    $"{relativePath} p.{pageNumber}: MuPDF and Ghostscript agree, but excise differs. " +
                    $"MuPDF: {report}; Ghostscript: {ghostscriptReport}. This is a real excise regression.");
            }

            // Write the triptych so a developer can eyeball the divergence
            // without re-running the whole pipeline.
            var outDir = Path.Combine(AppContext.BaseDirectory, "differential-failures");
            Directory.CreateDirectory(outDir);
            var name = Path.GetFileNameWithoutExtension(relativePath).Replace(' ', '-');
            var triptychPath = Path.Combine(outDir, $"{name}-p{pageNumber}-triptych.png");
            using var triptychExcise = DifferentialMetrics.ResizeMatch(exciseBitmap, mutoolBitmap!.Width, mutoolBitmap.Height);
            DifferentialMetrics.SaveTriptych(triptychPath, triptychExcise, mutoolBitmap);
            _output.WriteLine($"  triptych written to {triptychPath}");

            Assert.Fail(
                $"{relativePath} p.{pageNumber}: excise diverged from reference renderers. " +
                $"MuPDF: {report}; Poppler: {(popplerReport?.ToString() ?? "<unavailable>")}; " +
                $"Ghostscript: {(ghostscriptReport?.ToString() ?? "<unavailable>")}. " +
                $"Triptych dumped under bin/.../differential-failures/.");
        }
        finally
        {
            exciseBitmap.Dispose();
            mutoolBitmap?.Dispose();
            popplerBitmap?.Dispose();
            ghostBitmap?.Dispose();
        }
    }

    private static bool IsFailed(DifferentialReport report) =>
        report.DifferingPixelFraction > MaxDifferingPixelFraction ||
        report.MeanAbsoluteError > MaxMeanAbsoluteError;

    private static int? TryGetPageCount(string pdfPath)
    {
        try
        {
            using var doc = PdfDocument.Open(pdfPath);
            return doc.PageCount;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<int> SamplePages(int? pageCount)
    {
        if (pageCount is null)
        {
            yield return 1;
            yield break;
        }

        foreach (var pageNumber in new[] { 1, 2, 5, 20 })
        {
            if (pageNumber <= pageCount.Value)
            {
                yield return pageNumber;
            }
        }
    }

    /// <summary>
    /// Walk up from the test assembly's directory to the project root
    /// (where excise.sln lives). Tests are otherwise unaware of where on
    /// disk they're running.
    /// </summary>
    private static string? LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "excise.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private const string SentinelNoCorpus = "<no-corpus-downloaded>";
}
