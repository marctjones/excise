using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AwesomeAssertions;
using Pdfe.Avalonia.Controls;
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

        // Render completes in ~2s locally, but the first render on a cold CI
        // runner (JIT + xvfb + SkiaSharp native init) can take far longer, so a
        // 15s budget intermittently failed in CI while passing everywhere else.
        // Use a generous 60s budget — we're asserting "it renders", not "it
        // renders fast" (perf is covered by the benchmark suite). (#363)
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (!viewer.IsLoading && pdfImage!.Source != null)
                break;
            await Task.Delay(50);
        }

        pdfImage!.Source.Should().NotBeNull("viewer should have rendered the first page within 60s");
        viewer.IsLoading.Should().BeFalse();
        viewer.HasError.Should().BeFalse($"viewer reported error: {viewer.ErrorMessage}");

        // Avalonia Bitmap → PNG bytes → SKBitmap. Lossless (PNG), so pixel-equivalent
        // to what a user sees on-screen from the control's Image.Source.
        var bitmap = (Bitmap)pdfImage.Source!;
        using var ms = new MemoryStream();
        bitmap.Save(ms);
        ms.Position = 0;
        var result = SKBitmap.Decode(ms)
            ?? throw new InvalidOperationException(
                $"Could not decode captured bitmap ({bitmap.PixelSize}, {ms.Length} bytes). " +
                "If this happens after a working baseline, check that TestAppBuilder has UseHeadlessDrawing=false.");

        window.Close();
        doc.Dispose();

        result.Should().NotBeNull();
        return result;
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
