using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AwesomeAssertions;
using Pdfe.Avalonia.Controls;
using Pdfe.Avalonia.Imaging;
using SkiaSharp;
using System;
using System.IO;
using System.Text;
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

        using var expected = RenderDirectPage(pdfBytes, pageNumber: 1, dpi: 120);
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

    private async Task<SKBitmap> RenderViaViewerControl(byte[] pdfBytes)
    {
        // [FixedAvaloniaFact] already dispatches this method onto the UI thread, so
        // we can touch Avalonia types directly.

        var doc = Pdfe.Core.Document.PdfDocument.Open(pdfBytes);

        var viewer = new PdfViewerControl();
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

    private async Task<ViewerVisualCapture> RenderViewerVisualSurface(byte[] pdfBytes)
    {
        var doc = Pdfe.Core.Document.PdfDocument.Open(pdfBytes);

        var viewer = new PdfViewerControl();
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
            if (!viewer.IsLoading && pdfImage.Source != null)
                break;
            await Task.Delay(50);
        }

        pdfImage.Source.Should().NotBeNull("viewer should have rendered the first page within 60s");
        viewer.IsLoading.Should().BeFalse();
        viewer.HasError.Should().BeFalse($"viewer reported error: {viewer.ErrorMessage}");
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

    private static SKBitmap RenderDirectPage(byte[] pdfBytes, int pageNumber, int dpi)
    {
        using var doc = Pdfe.Core.Document.PdfDocument.Open(pdfBytes);
        var page = doc.GetPage(pageNumber);
        var renderer = new Pdfe.Rendering.SkiaRenderer();
        return renderer.RenderPage(page, new Pdfe.Rendering.RenderOptions { Dpi = dpi });
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

    private static string FindRepoFile(params string[] pathParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file: {Path.Combine(pathParts)}");
    }

    private sealed record ViewerVisualCapture(SKBitmap Displayed, SKBitmap VisualSurface);

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
