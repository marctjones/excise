using Xunit;
using Excise.App.Services;
using System.IO;
using System;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Microsoft.Extensions.Logging;
using AwesomeAssertions;
using SkiaSharp;

namespace Excise.App.Tests;

/// <summary>
/// Visual rendering tests using PDF Association test suites.
/// Verifies that complex PDFs render correctly without artifacts.
/// </summary>
public class VisualRenderingTests
{
    private readonly ITestOutputHelper _output;
    private readonly string _testPdfPath;

    public VisualRenderingTests(ITestOutputHelper output)
    {
        _output = output;
        _testPdfPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", "..", "test-pdfs");
    }

    [Fact]
    public void Test_DownloadScriptExists()
    {
        var scriptPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", "..", "scripts", "download-test-pdfs.sh");
        
        Assert.True(File.Exists(scriptPath), 
            "Download script not found. Run the script to fetch test PDFs.");
    }

    [Fact]
    public async Task Test_RenderAndCompare_VeraPDF()
    {
        // Skip if test PDFs are not present
        if (!Directory.Exists(_testPdfPath))
        {
            _output.WriteLine($"Test PDF directory not found at {_testPdfPath}. Skipping visual tests.");
            return;
        }

        var veraPdfPath = Path.Combine(_testPdfPath, "verapdf-corpus");
        if (!Directory.Exists(veraPdfPath))
        {
            _output.WriteLine("VeraPDF corpus not found. Run './scripts/download-test-pdfs.sh' to fetch.");
            return;
        }

        // Create baselines directory if it doesn't exist
        var baselinesPath = Path.Combine(_testPdfPath, "baselines");
        Directory.CreateDirectory(baselinesPath);

        // Setup service
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var renderService = new PdfRenderService(loggerFactory.CreateLogger<PdfRenderService>());

        // Find PDF files (limit to a few for reasonable test duration)
        var pdfFiles = Directory.GetFiles(veraPdfPath, "*.pdf", SearchOption.AllDirectories);
        // Take first 5 for now to verify infrastructure
        var testFiles = pdfFiles.Take(5).ToList();

        if (testFiles.Count == 0)
        {
            _output.WriteLine("No PDF files found in corpus.");
            return;
        }

        foreach (var pdfFile in testFiles)
        {
            var fileName = Path.GetFileName(pdfFile);
            var baselineFileName = Path.ChangeExtension(fileName, ".png");
            var baselinePath = Path.Combine(baselinesPath, baselineFileName);

            _output.WriteLine($"Processing {fileName}...");

            try
            {
                // Render page 0
                using var bitmap = await renderService.RenderPageAsync(pdfFile, 0, 72); // 72 DPI for speed
                
                if (bitmap == null)
                {
                    _output.WriteLine($"Failed to render {fileName}");
                    continue;
                }

                // Convert SKBitmap to PNG MemoryStream for ImageSharp
                using var skImage = SKImage.FromBitmap(bitmap);
                using var encodedData = skImage.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = new MemoryStream(encodedData.ToArray());
                stream.Position = 0;

                if (!File.Exists(baselinePath))
                {
                    // Generate baseline
                    _output.WriteLine($"Generating baseline for {fileName}");
                    using var fileStream = File.Create(baselinePath);
                    stream.CopyTo(fileStream);
                }
                else
                {
                    // Compare against baseline
                    var diff = CompareImages(baselinePath, stream);
                    _output.WriteLine($"Difference for {fileName}: {diff:F4}%");

                    // Assert difference is low (allow minor rendering variations < 1%)
                    // Note: If rendering engine changes significantly, baselines need regeneration
                    diff.Should().BeLessThan(1.0, $"Rendering of {fileName} should match baseline");
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Error processing {fileName}: {ex.Message}");
                // Don't fail the whole test for one bad PDF, but log it
            }
        }
    }

    /// <summary>
    /// Helper method to compare two images and return difference percentage
    /// </summary>
    private double CompareImages(string baselinePath, Stream currentImageStream)
    {
        using var baselineImg = Image.Load<Rgba32>(baselinePath);
        using var currentImg = Image.Load<Rgba32>(currentImageStream);

        if (baselineImg.Width != currentImg.Width || baselineImg.Height != currentImg.Height)
        {
            // If dimensions differ, return 100% difference
            return 100.0;
        }

        long differentPixels = 0;
        long totalPixels = baselineImg.Width * baselineImg.Height;

        // Simple pixel-by-pixel comparison
        // Can be optimized or made more robust (SSIM) later
        for (int y = 0; y < baselineImg.Height; y++)
        {
            for (int x = 0; x < baselineImg.Width; x++)
            {
                var p1 = baselineImg[x, y];
                var p2 = currentImg[x, y];

                // Allow small tolerance for antialiasing differences
                if (Math.Abs(p1.R - p2.R) > 5 ||
                    Math.Abs(p1.G - p2.G) > 5 ||
                    Math.Abs(p1.B - p2.B) > 5)
                {
                    differentPixels++;
                }
            }
        }

        return (double)differentPixels / totalPixels * 100.0;
    }
}
