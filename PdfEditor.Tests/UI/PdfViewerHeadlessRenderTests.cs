using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AwesomeAssertions;
using Pdfe.Avalonia.Controls;
using Pdfe.Avalonia.Imaging;
using Pdfe.Core.Parsing;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
namespace PdfEditor.Tests.UI;

/// <summary>
/// End-to-end headless render tests: host <see cref="PdfViewerControl"/> in a
/// real Avalonia window, feed it a deterministic in-memory PDF, capture the
/// pixels the user would see, and diff against a committed baseline PNG.
///
/// Baseline workflow:
///   1. First run with PDFE_UPDATE_BASELINES=1 writes the baseline next to the
///      source tree. Eyeball it, then commit.
///   2. Subsequent runs diff against the committed baseline.
/// </summary>
[Collection("AvaloniaTests")]
public class PdfViewerHeadlessRenderTests
{
    private const int ViewerRenderDpi = 120;

    private readonly ITestOutputHelper _output;

    public PdfViewerHeadlessRenderTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [FixedAvaloniaFact]
    public async Task PdfViewer_RendersSimpleText_MatchesBaseline()
    {
        var pdfBytes = CreatePdfWithContent("BT /F1 24 Tf 100 700 Td (Hello, World!) Tj ET");
        var captured = await RenderViaViewerControl(pdfBytes);

        AssertMatchesBaseline(captured, testName: "pdfviewer-simple-text", maxDifference: 0.02);
    }

    /// <summary>
    /// Real-world PDF: a one-page birth-certificate form with standard Type1
    /// fonts and WinAnsi encoding. This exercises the code paths recently
    /// fixed in commits 0709a39 (font encoding detection) and d1357bd
    /// (CodePagesEncodingProvider registration).
    /// </summary>
    [FixedAvaloniaFact]
    public async Task PdfViewer_RendersBirthCertificate_MatchesBaseline()
    {
        var pdfPath = Path.Combine(AppContext.BaseDirectory, "UI", "test-pdfs",
            "birth-certificate-request-scrambled.pdf");
        File.Exists(pdfPath).Should().BeTrue($"expected test PDF at {pdfPath}");
        var pdfBytes = await File.ReadAllBytesAsync(pdfPath);

        var captured = await RenderViaViewerControl(pdfBytes);
        // This real-world form exercises platform font/rasterization paths in
        // the headless Avalonia host. Keep the deterministic synthetic PDF
        // strict, but allow a little more antialiasing drift here.
        AssertMatchesBaseline(captured, testName: "pdfviewer-birth-certificate-page1", maxDifference: 0.06);
    }

    [FixedAvaloniaFact]
    public void SkiaInterop_PreservesOpaqueWhiteBackgroundAndColorChannels()
    {
        using var source = new SKBitmap(4, 1, SKColorType.Rgba8888, SKAlphaType.Premul);
        source.SetPixel(0, 0, SKColors.White);
        source.SetPixel(1, 0, SKColors.Red);
        source.SetPixel(2, 0, SKColors.Green);
        source.SetPixel(3, 0, SKColors.Blue);

        using var avaloniaBitmap = SkiaInterop.ToAvaloniaBitmap(source);
        avaloniaBitmap.Should().NotBeNull();

        using var captured = DecodeAvaloniaBitmap(avaloniaBitmap!);

        captured.GetPixel(0, 0).Should().Be(SKColors.White,
            "an opaque PDF page background must not become transparent or black in the GUI bitmap");
        captured.GetPixel(1, 0).Should().Be(SKColors.Red,
            "red must not be swapped with blue while copying to Avalonia BGRA pixels");
        captured.GetPixel(2, 0).Should().Be(SKColors.Green);
        captured.GetPixel(3, 0).Should().Be(SKColors.Blue);
    }

    [FixedAvaloniaFact]
    public async Task PdfViewer_AccCompensationCover_DisplayedBitmapMatchesRendererAndIsNotBlack()
    {
        var pdfPath = FindRepoFile("test-pdfs", "sample-pdfs", "acc-global-compensation-report.pdf");
        var pdfBytes = await File.ReadAllBytesAsync(pdfPath);

        using var expectedRaw = RenderDirectPage(pdfBytes, pageNumber: 1, dpi: ViewerRenderDpi);
        using var expected = NormalizeSkiaBitmap(expectedRaw);
        using var displayed = await RenderViaViewerControl(pdfBytes);

        displayed.Width.Should().Be(expected.Width);
        displayed.Height.Should().Be(expected.Height);

        var difference = VisualAssertions.CalculatePixelDifference(displayed, expected);
        _output.WriteLine($"ACC cover GUI bitmap vs renderer difference: {difference:P4}");
        difference.Should().BeLessThanOrEqualTo(0.001,
            "the bitmap handed to the GUI Image control should match the renderer output");

        AssertLightOpaquePage(displayed, "ACC cover GUI bitmap");
    }

    [FixedAvaloniaFact]
    public async Task PdfViewer_AccCompensationCover_HeadlessVisualSurfaceMatchesDisplayedBitmap()
    {
        var pdfPath = FindRepoFile("test-pdfs", "sample-pdfs", "acc-global-compensation-report.pdf");
        var pdfBytes = await File.ReadAllBytesAsync(pdfPath);

        var capture = await RenderViewerVisualSurface(pdfBytes);
        using var displayed = capture.Displayed;
        using var visualSurface = capture.VisualSurface;

        displayed.Width.Should().Be(visualSurface.Width);
        displayed.Height.Should().Be(visualSurface.Height);

        var difference = VisualAssertions.CalculatePixelDifference(visualSurface, displayed);
        _output.WriteLine($"ACC cover offscreen GUI surface vs displayed bitmap difference: {difference:P4}");
        difference.Should().BeLessThanOrEqualTo(0.01,
            "the headless Avalonia visual surface should show the same pixels as the rendered page bitmap");

        AssertLightOpaquePage(visualSurface, "ACC cover offscreen GUI surface");
    }

    [FixedAvaloniaFact(Timeout = 900_000)]
    [Trait("Category", "GuiDisplay")]
    public async Task PdfViewer_RenderingQualitySuite_DisplayBitmapsMatchRenderer()
    {
        var repoRoot = FindRepoRoot();
        var includeAllContractPages = Environment.GetEnvironmentVariable("PDFE_GUI_DISPLAY_FULL_CONTRACTS") == "1";
        var includeAllContractGroups = Environment.GetEnvironmentVariable("PDFE_GUI_DISPLAY_ALL_CONTRACTS") == "1";
        var requestedContractGroups = ParseContractGroups(Environment.GetEnvironmentVariable("PDFE_GUI_DISPLAY_CONTRACT_GROUPS"));
        var shardCount = ParsePositiveEnvironmentInt("PDFE_GUI_DISPLAY_SHARD_COUNT", defaultValue: 1);
        var shardIndex = ParseNonNegativeEnvironmentInt("PDFE_GUI_DISPLAY_SHARD_INDEX", defaultValue: 0);
        if (shardIndex >= shardCount)
            throw new InvalidOperationException("PDFE_GUI_DISPLAY_SHARD_INDEX must be less than PDFE_GUI_DISPLAY_SHARD_COUNT.");
        var pageOffset = ParseNonNegativeEnvironmentInt("PDFE_GUI_DISPLAY_PAGE_OFFSET", defaultValue: 0);
        var pageLimit = ParseOptionalPositiveEnvironmentInt("PDFE_GUI_DISPLAY_PAGE_LIMIT");
        var caseFilter = Environment.GetEnvironmentVariable("PDFE_GUI_DISPLAY_CASE_FILTER");
        var cases = DiscoverGuiDisplayCases(
            repoRoot,
            includeAllContractPages,
            includeAllContractGroups,
            requestedContractGroups).ToList();
        if (!string.IsNullOrWhiteSpace(caseFilter))
        {
            cases = cases
                .Where(testCase => testCase.Id.Contains(caseFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        var discoveredCaseCount = cases.Count;
        if (shardCount > 1)
        {
            cases = cases
                .Where((_, index) => index % shardCount == shardIndex)
                .ToList();
        }
        if (pageOffset > 0 || pageLimit.HasValue)
        {
            cases = cases
                .Skip(pageOffset)
                .Take(pageLimit ?? int.MaxValue)
                .ToList();
        }
        cases.Should().NotBeEmpty("the repository should contain GUI display fixtures");

        var outputDir = Path.Combine(AppContext.BaseDirectory, "UI", "test-output");
        Directory.CreateDirectory(outputDir);
        var failureArtifactDir = Path.Combine(outputDir, "gui-display-failures");
        if (Directory.Exists(failureArtifactDir))
            Directory.Delete(failureArtifactDir, recursive: true);
        var reportPath = BuildGuiDisplayReportPath(
            outputDir,
            includeAllContractPages,
            includeAllContractGroups,
            requestedContractGroups,
            shardCount,
            shardIndex,
            pageOffset,
            pageLimit);

        var pdfBytesCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var results = new List<GuiDisplayResult>();
        var failures = new List<string>();
        var suiteSw = Stopwatch.StartNew();

        _output.WriteLine(
            $"GUI display sweep: {cases.Count} page(s), " +
            $"{discoveredCaseCount} discovered before shard/range, " +
            $"{(includeAllContractPages ? "all contracted pages" : "representative contracted pages")}, " +
            $"{(includeAllContractGroups ? "all contract groups" : "renderer contract groups")}" +
            $"{(requestedContractGroups.Count == 0 ? "" : $", groups {string.Join(",", requestedContractGroups)}")}" +
            $"{(shardCount == 1 ? "" : $", shard {shardIndex + 1}/{shardCount}")}" +
            $"{(pageOffset == 0 && !pageLimit.HasValue ? "" : $", offset {pageOffset}, limit {pageLimit?.ToString() ?? "none"}")}" +
            $"{(string.IsNullOrWhiteSpace(caseFilter) ? "" : $", filter '{caseFilter}'")}.");

        for (var i = 0; i < cases.Count; i++)
        {
            var testCase = cases[i];
            var caseSw = Stopwatch.StartNew();
            var currentPage = new GuiDisplayCurrentPage
            {
                ordinal = i + 1,
                total = cases.Count,
                path = testCase.RelativePath,
                page = testCase.PageNumber,
                source = testCase.Source,
                startedAtUtc = DateTimeOffset.UtcNow,
                suiteElapsedMs = suiteSw.ElapsedMilliseconds,
            };
            await WriteGuiDisplayReport(
                reportPath,
                includeAllContractPages,
                includeAllContractGroups,
                requestedContractGroups,
                caseFilter,
                discoveredCaseCount,
                cases.Count,
                shardCount,
                shardIndex,
                pageOffset,
                pageLimit,
                suiteSw.ElapsedMilliseconds,
                results,
                currentPage);
            var result = new GuiDisplayResult
            {
                path = testCase.RelativePath,
                page = testCase.PageNumber,
                source = testCase.Source,
                expectedRawStatus = testCase.Contract?.ExpectedRawStatus,
                qualityStatus = testCase.Contract?.QualityStatus,
            };

            try
            {
                if (!File.Exists(testCase.AbsolutePath))
                    throw new FileNotFoundException("Fixture PDF not found.", testCase.AbsolutePath);

                if (!pdfBytesCache.TryGetValue(testCase.AbsolutePath, out var pdfBytes))
                {
                    pdfBytes = await File.ReadAllBytesAsync(testCase.AbsolutePath);
                    pdfBytesCache[testCase.AbsolutePath] = pdfBytes;
                }

                using var expectedRaw = RenderDirectViewerPage(pdfBytes, testCase.PageNumber, testCase.Password);
                using var expected = NormalizeSkiaBitmap(expectedRaw);
                var capture = await RenderViewerVisualSurface(pdfBytes, testCase.PageNumber, testCase.Password);
                using var displayed = capture.Displayed;
                using var visualSurface = capture.VisualSurface;

                displayed.Width.Should().Be(expected.Width, $"{testCase.Id} GUI image width should match renderer");
                displayed.Height.Should().Be(expected.Height, $"{testCase.Id} GUI image height should match renderer");

                var imageSourceDiff = VisualAssertions.CalculatePixelDifference(displayed, expected);
                result.imageSourcePixelDifference = imageSourceDiff;
                if (imageSourceDiff > 0.001)
                    SaveGuiDisplayFailureArtifacts(outputDir, testCase, expected, displayed, visualSurface);
                imageSourceDiff.Should().BeLessThanOrEqualTo(0.001,
                    $"{testCase.Id} Image.Source should match the PNG-visible renderer output");

                AssertBitmapOpaque(displayed, $"{testCase.Id} Image.Source");
                AssertGuiSurfaceDoesNotObscureDisplayedBitmap(displayed, visualSurface, testCase.Id);

                if (ExpectsNonRenderable(testCase))
                {
                    result.status = "UNEXPECTED_RENDER";
                    result.error = $"Expected non-renderable status {testCase.Contract?.ExpectedRawStatus}, but the page rendered.";
                    failures.Add($"{testCase.Id}: {result.error}");
                }
                else
                {
                    result.status = "PASS";
                    result.visualSurfaceMeanLuminanceDelta =
                        MeasureImage(displayed).MeanLuminance - MeasureImage(visualSurface).MeanLuminance;
                }
            }
            catch (Exception ex)
            {
                result.status = IsExpectedNonRenderable(testCase, ex)
                    ? "NON_RENDERABLE_ACCEPTED"
                    : ex is PdfEncryptionNotSupportedException
                        ? "UNSUPPORTED_TRACKED"
                        : "FAIL";
                result.error = $"{ex.GetType().Name}: {ex.Message}";
                if (result.status == "FAIL")
                    failures.Add($"{testCase.Id}: {result.error}");
            }
            finally
            {
                caseSw.Stop();
                result.elapsedMs = caseSw.ElapsedMilliseconds;
                results.Add(result);
            }

            await WriteGuiDisplayReport(
                reportPath,
                includeAllContractPages,
                includeAllContractGroups,
                requestedContractGroups,
                caseFilter,
                discoveredCaseCount,
                cases.Count,
                shardCount,
                shardIndex,
                pageOffset,
                pageLimit,
                suiteSw.ElapsedMilliseconds,
                results,
                current: null);
            if ((i + 1) % 10 == 0 || i + 1 == cases.Count)
                _output.WriteLine(
                    $"  {i + 1}/{cases.Count} checked, " +
                    $"{failures.Count} failure(s), elapsed {suiteSw.Elapsed:mm\\:ss}");
        }

        suiteSw.Stop();
        await WriteGuiDisplayReport(
            reportPath,
            includeAllContractPages,
            includeAllContractGroups,
            requestedContractGroups,
            caseFilter,
            discoveredCaseCount,
            cases.Count,
            shardCount,
            shardIndex,
            pageOffset,
            pageLimit,
            suiteSw.ElapsedMilliseconds,
            results,
            current: null);
        _output.WriteLine($"GUI display sweep report: {reportPath}");

        failures.Should().BeEmpty("GUI display path should preserve renderer pixels and avoid black/transparent surface failures");
    }

    /// <summary>
    /// Sanity test: render the same PDF directly via SkiaRenderer, bypassing the
    /// PdfViewerControl, to isolate whether a failure is in the renderer or the
    /// UI plumbing.
    /// </summary>
    [Fact]
    public void SkiaRenderer_RendersSimpleText_ProducesExpectedBitmap()
    {
        var pdfBytes = CreatePdfWithContent("BT /F1 24 Tf 100 700 Td (Hello, World!) Tj ET");
        using var doc = Pdfe.Core.Document.PdfDocument.Open(pdfBytes);
        var page = doc.GetPage(1);

        var renderer = new Pdfe.Rendering.SkiaRenderer();
        using var bitmap = renderer.RenderPage(page, new Pdfe.Rendering.RenderOptions { Dpi = 200 });

        _output.WriteLine($"Direct SkiaRenderer output: {bitmap.Width}x{bitmap.Height}");
        bitmap.Width.Should().BeGreaterThan(100, "US Letter @ 200 DPI should be ~1700px wide");
        bitmap.Height.Should().BeGreaterThan(100);
    }

    private async Task<SKBitmap> RenderViaViewerControl(byte[] pdfBytes, int pageNumber = 1, string? password = null)
    {
        // [FixedAvaloniaFact] already dispatches this method onto the UI thread, so
        // we can touch Avalonia types directly.

        var doc = password == null
            ? Pdfe.Core.Document.PdfDocument.Open(pdfBytes)
            : Pdfe.Core.Document.PdfDocument.Open(pdfBytes, password);

        var viewer = new PdfViewerControl { CurrentPage = pageNumber };
        var window = new Window
        {
            Content = viewer,
            Width = 612,
            Height = 792,
            WindowDecorations = WindowDecorations.None
        };
        window.Show();

        // Let initial layout run.
        await Task.Delay(50);

        // Kicks off RenderCurrentPageAsync on the control.
        viewer.Document = doc;

        // Poll for the async render to produce an Image.Source. The renderer
        // dispatches a background SkiaRenderer.RenderPage and marshals the
        // Bitmap back to the UI thread, so we have to yield repeatedly.
        var pdfImage = viewer.FindControl<Image>("PdfImage");
        pdfImage.Should().NotBeNull("PdfViewerControl must expose the PdfImage element");

        await WaitForViewerRender(viewer, pdfImage!);

        var bitmap = (Bitmap)pdfImage.Source!;
        var result = DecodeAvaloniaBitmap(bitmap);

        window.Close();
        doc.Dispose();

        result.Should().NotBeNull();
        return result;
    }

    private async Task<ViewerVisualCapture> RenderViewerVisualSurface(byte[] pdfBytes, int pageNumber = 1, string? password = null)
    {
        var doc = password == null
            ? Pdfe.Core.Document.PdfDocument.Open(pdfBytes)
            : Pdfe.Core.Document.PdfDocument.Open(pdfBytes, password);

        var viewer = new PdfViewerControl { CurrentPage = pageNumber };
        var window = new Window
        {
            Content = viewer,
            Width = 612,
            Height = 792,
            WindowDecorations = WindowDecorations.None
        };
        window.Show();

        await Task.Delay(50);
        viewer.Document = doc;

        var pdfImage = viewer.FindControl<Image>("PdfImage");
        pdfImage.Should().NotBeNull("PdfViewerControl must expose the PdfImage element");

        await WaitForViewerRender(viewer, pdfImage!);

        var imageSource = (Bitmap)pdfImage!.Source!;
        var displayed = DecodeAvaloniaBitmap(imageSource);

        viewer.Width = displayed.Width;
        viewer.Height = displayed.Height;
        window.Width = displayed.Width;
        window.Height = displayed.Height;
        viewer.Measure(new Size(displayed.Width, displayed.Height));
        viewer.Arrange(new Rect(0, 0, displayed.Width, displayed.Height));
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50);

        using var renderTarget = new RenderTargetBitmap(new PixelSize(displayed.Width, displayed.Height));
        renderTarget.Render(viewer);
        var visualSurface = DecodeAvaloniaBitmap(renderTarget);

        window.Close();
        doc.Dispose();

        return new ViewerVisualCapture(displayed, visualSurface);
    }

    private async Task WaitForViewerRender(PdfViewerControl viewer, Image pdfImage)
    {
        // Render completes in ~2s locally, but the first render on a cold CI
        // runner (JIT + xvfb + SkiaSharp native init) can take far longer, so a
        // 15s budget intermittently failed in CI while passing everywhere else.
        // Use a generous 60s budget — we're asserting "it renders", not "it
        // renders fast" (perf is covered by the benchmark suite). (#363)
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (viewer.HasError || (!viewer.IsLoading && pdfImage.Source != null))
                break;
            await Task.Delay(50);
        }

        viewer.HasError.Should().BeFalse($"viewer reported error: {viewer.ErrorMessage}");
        pdfImage.Source.Should().NotBeNull("viewer should have rendered the requested page within 60s");
        viewer.IsLoading.Should().BeFalse();
    }

    private static SKBitmap DecodeAvaloniaBitmap(Bitmap bitmap)
    {
        // Avalonia Bitmap → PNG bytes → SKBitmap. Lossless (PNG), so pixel-equivalent
        // to the pixels the control hands to the Avalonia renderer.
        using var ms = new MemoryStream();
        bitmap.Save(ms);
        ms.Position = 0;
        return SKBitmap.Decode(ms)
            ?? throw new InvalidOperationException(
                $"Could not decode captured bitmap ({bitmap.PixelSize}, {ms.Length} bytes). " +
                "If this happens after a working baseline, check that TestAppBuilder has UseHeadlessDrawing=false.");
    }

    private static SKBitmap NormalizeSkiaBitmap(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = data.AsStream();
        return SKBitmap.Decode(stream)
            ?? throw new InvalidOperationException("Could not decode normalized renderer PNG.");
    }

    private static SKBitmap RenderDirectPage(byte[] pdfBytes, int pageNumber, int dpi, string? password = null)
    {
        using var doc = password == null
            ? Pdfe.Core.Document.PdfDocument.Open(pdfBytes)
            : Pdfe.Core.Document.PdfDocument.Open(pdfBytes, password);
        var page = doc.GetPage(pageNumber);
        var renderer = new Pdfe.Rendering.SkiaRenderer();
        return renderer.RenderPage(page, new Pdfe.Rendering.RenderOptions { Dpi = dpi });
    }

    private static SKBitmap RenderDirectViewerPage(byte[] pdfBytes, int pageNumber, string? password = null)
    {
        using var doc = password == null
            ? Pdfe.Core.Document.PdfDocument.Open(pdfBytes)
            : Pdfe.Core.Document.PdfDocument.Open(pdfBytes, password);
        var page = doc.GetPage(pageNumber);
        var dpi = PdfViewerControl.EffectiveSinglePageRenderDpi(page);
        var renderer = new Pdfe.Rendering.SkiaRenderer();
        return renderer.RenderPage(page, new Pdfe.Rendering.RenderOptions
        {
            Dpi = dpi,
            MaxPixelCount = 64L * 1024L * 1024L
        });
    }

    private static void AssertLightOpaquePage(SKBitmap bitmap, string description)
    {
        var total = (long)bitmap.Width * bitmap.Height;
        long dark = 0;
        long light = 0;
        long transparent = 0;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha < 250)
                    transparent++;

                var alpha = pixel.Alpha / 255.0;
                var red = pixel.Red * alpha + 255 * (1 - alpha);
                var green = pixel.Green * alpha + 255 * (1 - alpha);
                var blue = pixel.Blue * alpha + 255 * (1 - alpha);
                var luminance = 0.2126 * red + 0.7152 * green + 0.0722 * blue;

                if (luminance < 32)
                    dark++;
                if (luminance > 220)
                    light++;
            }
        }

        var darkRatio = (double)dark / total;
        var lightRatio = (double)light / total;
        var transparentRatio = (double)transparent / total;

        transparentRatio.Should().BeLessThan(0.001,
            $"{description} should be an opaque composited page image, not transparent white that can turn black over a dark GUI background");
        darkRatio.Should().BeLessThan(0.35,
            $"{description} should not reproduce the black-background GUI failure");
        lightRatio.Should().BeGreaterThan(0.25,
            $"{description} should preserve the light page background");
    }

    private static void AssertBitmapOpaque(SKBitmap bitmap, string description)
    {
        var stats = MeasureImage(bitmap);
        stats.TransparentRatio.Should().BeLessThan(0.001,
            $"{description} should be an opaque composited page image, not transparent pixels that can turn black over a dark GUI background");
    }

    private static void AssertGuiSurfaceDoesNotObscureDisplayedBitmap(SKBitmap displayed, SKBitmap visualSurface, string description)
    {
        visualSurface.Width.Should().Be(displayed.Width, $"{description} offscreen GUI surface width should match Image.Source");
        visualSurface.Height.Should().Be(displayed.Height, $"{description} offscreen GUI surface height should match Image.Source");

        var displayedStats = MeasureImage(displayed);
        var surfaceStats = MeasureImage(visualSurface);

        surfaceStats.TransparentRatio.Should().BeLessThan(0.001,
            $"{description} offscreen GUI surface should render opaque pixels");

        // The offscreen surface includes the viewer background when a small page is
        // centered inside a larger viewport. That can legitimately lower average
        // luminance even when the displayed page bitmap itself matches the renderer
        // exactly, so guard against black/opaque overlays via opacity and dark-pixel
        // ratio rather than whole-surface brightness.
        var allowedDarkRatio = Math.Min(1.0, displayedStats.DarkRatio + (displayedStats.DarkRatio > 0.90 ? 0.02 : 0.25));
        surfaceStats.DarkRatio.Should().BeLessThanOrEqualTo(allowedDarkRatio,
            $"{description} offscreen GUI surface should not obscure the page with a black background");
    }

    private static ImageStats MeasureImage(SKBitmap bitmap)
    {
        var total = (long)bitmap.Width * bitmap.Height;
        long dark = 0;
        long transparent = 0;
        double luminanceSum = 0;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha < 250)
                    transparent++;

                var alpha = pixel.Alpha / 255.0;
                var red = pixel.Red * alpha + 255 * (1 - alpha);
                var green = pixel.Green * alpha + 255 * (1 - alpha);
                var blue = pixel.Blue * alpha + 255 * (1 - alpha);
                var luminance = 0.2126 * red + 0.7152 * green + 0.0722 * blue;
                luminanceSum += luminance;

                if (luminance < 32)
                    dark++;
            }
        }

        return new ImageStats(
            TransparentRatio: (double)transparent / total,
            DarkRatio: (double)dark / total,
            MeanLuminance: luminanceSum / total);
    }

    private void AssertMatchesBaseline(SKBitmap actual, string testName, double maxDifference)
    {
        var baseDir = AppContext.BaseDirectory;
        var committedBaseline = Path.Combine(baseDir, "UI", "baselines", $"{testName}.png");
        var outputDir = Path.Combine(baseDir, "UI", "test-output");
        var actualPath = Path.Combine(outputDir, $"{testName}-actual.png");
        var diffPath = Path.Combine(outputDir, $"{testName}-diff.png");

        Directory.CreateDirectory(outputDir);
        VisualAssertions.SavePng(actual, actualPath);
        _output.WriteLine($"Captured: {actualPath} ({actual.Width}x{actual.Height})");

        if (Environment.GetEnvironmentVariable("PDFE_UPDATE_BASELINES") == "1")
        {
            // Write into the source tree so the developer can commit it, and also
            // into bin/ so this test run reports pass for sanity.
            var sourceBaseline = FindSourceBaselinePath(testName);
            VisualAssertions.SavePng(actual, sourceBaseline);
            VisualAssertions.SavePng(actual, committedBaseline);
            _output.WriteLine($"Updated baseline: {sourceBaseline}");
            return;
        }

        if (!File.Exists(committedBaseline))
        {
            throw new FileNotFoundException(
                $"Baseline not found: {committedBaseline}. " +
                $"Run with PDFE_UPDATE_BASELINES=1 to generate, verify the PNG, then commit.");
        }

        actual.ShouldVisuallyMatch(committedBaseline, maxDifference, diffPath);
    }

    private static string FindSourceBaselinePath(string testName)
    {
        // Walk up from bin/Debug/net8.0 to the test project root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PdfEditor.Tests.csproj")))
            dir = dir.Parent;

        if (dir == null)
            throw new InvalidOperationException("Could not locate PdfEditor.Tests project root.");

        return Path.Combine(dir.FullName, "UI", "baselines", $"{testName}.png");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "pdfe.sln")) &&
                Directory.Exists(Path.Combine(dir.FullName, "test-pdfs")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static string FindRepoFile(params string[] pathParts)
    {
        var candidate = Path.Combine(new[] { FindRepoRoot() }.Concat(pathParts).ToArray());
        if (File.Exists(candidate))
            return candidate;

        throw new FileNotFoundException($"Could not find repository file: {Path.Combine(pathParts)}");
    }

    private static IEnumerable<GuiDisplayCase> DiscoverGuiDisplayCases(
        string repoRoot,
        bool includeAllContractPages,
        bool includeAllContractGroups,
        IReadOnlyCollection<string> requestedContractGroups)
    {
        var byId = new Dictionary<string, GuiDisplayCase>(StringComparer.Ordinal);

        foreach (var testCase in DiscoverRenderingContractCases(
                     repoRoot,
                     includeAllContractPages,
                     includeAllContractGroups,
                     requestedContractGroups))
            byId.TryAdd(testCase.Id, testCase);

        if (requestedContractGroups.Count == 0 ||
            includeAllContractGroups ||
            requestedContractGroups.Contains("pdf20", StringComparer.Ordinal))
        {
            foreach (var testCase in DiscoverDirectoryFirstPageCases(repoRoot, Path.Combine("test-pdfs", "pdf20"), "pdf20 atomic fixture"))
                byId.TryAdd(testCase.Id, testCase);
        }

        return byId.Values
            .OrderBy(c => SourcePriority(c.Source))
            .ThenBy(c => c.RelativePath, StringComparer.Ordinal)
            .ThenBy(c => c.PageNumber);
    }

    private static int SourcePriority(string source) => source switch
    {
        "pdf20 atomic fixture" => 0,
        "contract:pdf20" => 1,
        "contract:sample-pdfs" => 2,
        "contract:federal" => 3,
        "contract:smoke" => 4,
        "contract:ghent" => 5,
        "contract:altona" => 6,
        "contract:pdfjs" => 7,
        "contract:poppler" => 8,
        "contract:isartor" => 9,
        "contract:verapdf-corpus" => 10,
        _ => 99,
    };

    private static bool ExpectsNonRenderable(GuiDisplayCase testCase)
        => testCase.Contract?.ExpectedRawStatus is { Length: > 0 } status &&
           IsNonRenderableExpectedRawStatus(status);

    private static bool IsNonRenderableExpectedRawStatus(string status)
        => status.Equals("DECODE_ERROR", StringComparison.OrdinalIgnoreCase) ||
           status.Equals("PASSWORD_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
           status.Equals("EMPTY_DOC", StringComparison.OrdinalIgnoreCase) ||
           status.Equals("MALFORMED_PDF", StringComparison.OrdinalIgnoreCase);

    private static bool IsExpectedNonRenderable(GuiDisplayCase testCase, Exception ex)
    {
        if (!ExpectsNonRenderable(testCase))
            return false;

        var expectedError = testCase.Contract?.ExpectedErrorContains;
        return string.IsNullOrWhiteSpace(expectedError) ||
               ex.Message.Contains(expectedError, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ParseContractGroups(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        return value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(group => SourcePriority($"contract:{group}"))
            .ThenBy(group => group, StringComparer.Ordinal)
            .ToArray();
    }

    private static int ParsePositiveEnvironmentInt(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        if (int.TryParse(value, out var parsed) && parsed > 0)
            return parsed;

        throw new InvalidOperationException($"{name} must be a positive integer.");
    }

    private static int ParseNonNegativeEnvironmentInt(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        if (int.TryParse(value, out var parsed) && parsed >= 0)
            return parsed;

        throw new InvalidOperationException($"{name} must be a non-negative integer.");
    }

    private static int? ParseOptionalPositiveEnvironmentInt(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (int.TryParse(value, out var parsed) && parsed > 0)
            return parsed;

        throw new InvalidOperationException($"{name} must be a positive integer.");
    }

    private static IEnumerable<GuiDisplayCase> DiscoverRenderingContractCases(
        string repoRoot,
        bool includeAllContractPages,
        bool includeAllContractGroups,
        IReadOnlyCollection<string> requestedContractGroups)
    {
        var contractsRoot = Path.Combine(repoRoot, "test-pdfs", "rendering-contracts");
        var rendererContractDirs = new[]
        {
            "sample-pdfs",
            "federal",
            "pdf20",
            "altona",
            "ghent",
            "smoke",
        };
        var contractDirs = requestedContractGroups.Count > 0
            ? requestedContractGroups
            : includeAllContractGroups
                ? Directory.EnumerateDirectories(contractsRoot)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Cast<string>()
                    .OrderBy(group => SourcePriority($"contract:{group}"))
                    .ThenBy(group => group, StringComparer.Ordinal)
                    .ToArray()
                : rendererContractDirs;

        foreach (var contractDir in contractDirs)
        {
            var dir = Path.Combine(contractsRoot, contractDir);
            if (!Directory.Exists(dir))
                continue;

            foreach (var contractPath in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories)
                         .Where(path => !Path.GetFileName(path).StartsWith('_'))
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(contractPath));
                var root = json.RootElement;
                if (!root.TryGetProperty("Path", out var pathElement) ||
                    pathElement.GetString() is not { Length: > 0 } relativePdfPath ||
                    !root.TryGetProperty("Pages", out var pagesElement) ||
                    pagesElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var password = root.TryGetProperty("Password", out var passwordElement)
                    ? passwordElement.GetString()
                    : null;
                var pageContracts = new SortedDictionary<int, PageContract>();
                foreach (var pageProperty in pagesElement.EnumerateObject())
                {
                    var pageContract = ParsePageContract(pageProperty.Value);
                    foreach (var page in ExpandPageKey(pageProperty.Name))
                        pageContracts.TryAdd(page, pageContract);
                }

                var allPages = pageContracts.Keys
                    .OrderBy(page => page)
                    .ToList();
                var selectedPages = includeAllContractPages
                    ? allPages
                    : SelectRepresentativePages(allPages);

                var normalizedRelativePdfPath = relativePdfPath.Replace('\\', Path.DirectorySeparatorChar);
                var absolutePath = Path.Combine(repoRoot, "test-pdfs", normalizedRelativePdfPath);
                foreach (var pageNumber in selectedPages)
                {
                    yield return new GuiDisplayCase(
                        RelativePath: Path.Combine("test-pdfs", normalizedRelativePdfPath),
                        AbsolutePath: absolutePath,
                        PageNumber: pageNumber,
                        Source: $"contract:{contractDir}",
                        Password: password,
                        Contract: pageContracts.GetValueOrDefault(pageNumber));
                }
            }
        }
    }

    private static IEnumerable<GuiDisplayCase> DiscoverDirectoryFirstPageCases(string repoRoot, string relativeDirectory, string source)
    {
        var absoluteDirectory = Path.Combine(repoRoot, relativeDirectory);
        if (!Directory.Exists(absoluteDirectory))
            yield break;

        foreach (var pdfPath in Directory.EnumerateFiles(absoluteDirectory, "*.pdf", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            yield return new GuiDisplayCase(
                RelativePath: Path.GetRelativePath(repoRoot, pdfPath),
                AbsolutePath: pdfPath,
                PageNumber: 1,
                Source: source,
                Password: null,
                Contract: null);
        }
    }

    private static PageContract ParsePageContract(JsonElement page)
        => new(
            ExpectedRawStatus: TryGetStringProperty(page, "ExpectedRawStatus"),
            ExpectedErrorContains: TryGetStringProperty(page, "ExpectedErrorContains"),
            QualityStatus: TryGetStringProperty(page, "QualityStatus"));

    private static string? TryGetStringProperty(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static IEnumerable<int> ExpandPageKey(string key)
    {
        var trimmed = key.Trim();
        var dash = trimmed.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            if (int.TryParse(trimmed, out var page) && page > 0)
                yield return page;
            yield break;
        }

        if (!int.TryParse(trimmed[..dash], out var first) ||
            !int.TryParse(trimmed[(dash + 1)..], out var last) ||
            first <= 0 ||
            last < first)
        {
            yield break;
        }

        for (var page = first; page <= last; page++)
            yield return page;
    }

    private static IReadOnlyList<int> SelectRepresentativePages(IReadOnlyList<int> pages)
    {
        if (pages.Count <= 8)
            return pages;

        var selected = new SortedSet<int>
        {
            pages[0],
            pages[1],
            pages[pages.Count / 2],
            pages[^2],
            pages[^1],
        };
        return selected.ToArray();
    }

    private static async Task WriteGuiDisplayReport(
        string reportPath,
        bool includeAllContractPages,
        bool includeAllContractGroups,
        IReadOnlyCollection<string> requestedContractGroups,
        string? caseFilter,
        int discoveredTotal,
        int expectedTotal,
        int shardCount,
        int shardIndex,
        int pageOffset,
        int? pageLimit,
        long elapsedMs,
        IReadOnlyList<GuiDisplayResult> results,
        GuiDisplayCurrentPage? current)
    {
        var report = new GuiDisplaySuiteReport
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            fullContractPages = includeAllContractPages,
            allContractGroups = includeAllContractGroups,
            contractGroups = requestedContractGroups,
            caseFilter = caseFilter,
            discoveredPages = discoveredTotal,
            total = expectedTotal,
            shardCount = shardCount,
            shardIndex = shardIndex,
            pageOffset = pageOffset,
            pageLimit = pageLimit,
            checkedPages = results.Count,
            passed = results.Count(r => r.status == "PASS"),
            unsupportedTracked = results.Count(r => r.status == "UNSUPPORTED_TRACKED"),
            failed = results.Count(r => r.status is "FAIL" or "UNEXPECTED_RENDER"),
            elapsedMs = elapsedMs,
            current = current,
            results = results,
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        var tempPath = $"{reportPath}.{Environment.ProcessId}.tmp";
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, reportPath, overwrite: true);
    }

    private static string BuildGuiDisplayReportPath(
        string outputDir,
        bool includeAllContractPages,
        bool includeAllContractGroups,
        IReadOnlyCollection<string> requestedContractGroups,
        int shardCount,
        int shardIndex,
        int pageOffset,
        int? pageLimit)
    {
        var scope = requestedContractGroups.Count > 0
            ? string.Join("-", requestedContractGroups.Select(SanitizeFileNamePart))
            : includeAllContractGroups
                ? "all-contracts"
                : "renderer-contracts";
        var pages = includeAllContractPages ? "full-pages" : "representative-pages";
        var shard = shardCount > 1 ? $"-shard-{shardIndex + 1}-of-{shardCount}" : "";
        var range = pageOffset > 0 || pageLimit.HasValue
            ? $"-offset-{pageOffset}-limit-{pageLimit?.ToString() ?? "none"}"
            : "";
        return Path.Combine(outputDir, $"gui-display-suite-{scope}-{pages}{shard}{range}.json");
    }

    private static string SanitizeFileNamePart(string value) =>
        string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-'));

    private static void SaveGuiDisplayFailureArtifacts(
        string outputDir,
        GuiDisplayCase testCase,
        SKBitmap expected,
        SKBitmap displayed,
        SKBitmap visualSurface)
    {
        var safeName = string.Concat(testCase.Id.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_'));
        var failureDir = Path.Combine(outputDir, "gui-display-failures");
        Directory.CreateDirectory(failureDir);

        VisualAssertions.SavePng(expected, Path.Combine(failureDir, $"{safeName}-expected-renderer.png"));
        VisualAssertions.SavePng(displayed, Path.Combine(failureDir, $"{safeName}-displayed-image-source.png"));
        VisualAssertions.SavePng(visualSurface, Path.Combine(failureDir, $"{safeName}-visual-surface.png"));

        if (expected.Width == displayed.Width && expected.Height == displayed.Height)
        {
            using var diff = VisualAssertions.CreateDiffImage(displayed, expected);
            VisualAssertions.SavePng(diff, Path.Combine(failureDir, $"{safeName}-image-source-diff.png"));
        }
    }

    private sealed record ViewerVisualCapture(SKBitmap Displayed, SKBitmap VisualSurface);
    private sealed record PageContract(string? ExpectedRawStatus, string? ExpectedErrorContains, string? QualityStatus);

    private sealed record GuiDisplayCase(
        string RelativePath,
        string AbsolutePath,
        int PageNumber,
        string Source,
        string? Password,
        PageContract? Contract)
    {
        public string Id => $"{RelativePath}#{PageNumber}";
    }
    private sealed record ImageStats(double TransparentRatio, double DarkRatio, double MeanLuminance);

    private sealed class GuiDisplaySuiteReport
    {
        public DateTimeOffset generatedAtUtc { get; set; }
        public bool fullContractPages { get; set; }
        public bool allContractGroups { get; set; }
        public IReadOnlyCollection<string> contractGroups { get; set; } = Array.Empty<string>();
        public string? caseFilter { get; set; }
        public int discoveredPages { get; set; }
        public int total { get; set; }
        public int shardCount { get; set; }
        public int shardIndex { get; set; }
        public int pageOffset { get; set; }
        public int? pageLimit { get; set; }
        public int checkedPages { get; set; }
        public int passed { get; set; }
        public int unsupportedTracked { get; set; }
        public int failed { get; set; }
        public long elapsedMs { get; set; }
        public GuiDisplayCurrentPage? current { get; set; }
        public IReadOnlyList<GuiDisplayResult> results { get; set; } = Array.Empty<GuiDisplayResult>();
    }

    private sealed class GuiDisplayCurrentPage
    {
        public int ordinal { get; set; }
        public int total { get; set; }
        public string path { get; set; } = "";
        public int page { get; set; }
        public string source { get; set; } = "";
        public DateTimeOffset startedAtUtc { get; set; }
        public long suiteElapsedMs { get; set; }
    }

    private sealed class GuiDisplayResult
    {
        public string path { get; set; } = "";
        public int page { get; set; }
        public string source { get; set; } = "";
        public string? expectedRawStatus { get; set; }
        public string? qualityStatus { get; set; }
        public string status { get; set; } = "";
        public double imageSourcePixelDifference { get; set; }
        public double visualSurfaceMeanLuminanceDelta { get; set; }
        public long elapsedMs { get; set; }
        public string? error { get; set; }
    }

    /// <summary>
    /// Builds a minimal PDF 1.4 document with the given content stream. Copied
    /// from Pdfe.Rendering.Tests so this test is self-contained and doesn't
    /// depend on a binary test asset.
    /// </summary>
    private static byte[] CreatePdfWithContent(string content)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true) { NewLine = "\n" };

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[6];

        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        writer.WriteLine("endobj");
        writer.Flush();

        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 6");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 6 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }
}
