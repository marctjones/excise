using System.Text.Json;
using AwesomeAssertions;
using SkiaSharp;
using Xunit;

namespace Pdfe.Cli.Tests;

public class VisualDiffCommandTests
{
    [Fact]
    public void AnalyzeVisualDiff_ColorToneDrift_ClassifiesAsLowImpactColor()
    {
        using var reference = CreateSolidBitmap(12, 12, new SKColor(100, 150, 100));
        using var actual = CreateSolidBitmap(12, 12, new SKColor(100, 142, 138));

        var report = Program.AnalyzeVisualDiff(actual, reference, tolerance: 8);

        report.category.Should().Be("color-tone-or-texture");
        report.humanImpact.Should().Be("low");
        report.diffFraction.Should().Be(1.0);
        report.topRegions.Should().ContainSingle();
    }

    [Fact]
    public void AnalyzeVisualDiff_LocalBlackBlock_ClassifiesAsHighImpactGeometry()
    {
        using var reference = CreateSolidBitmap(100, 100, SKColors.White);
        using var actual = CreateSolidBitmap(100, 100, SKColors.White);
        using (var canvas = new SKCanvas(actual))
        using (var paint = new SKPaint { Color = SKColors.Black })
        {
            canvas.DrawRect(new SKRect(30, 30, 70, 70), paint);
        }

        var report = Program.AnalyzeVisualDiff(actual, reference, tolerance: 16);

        report.category.Should().Be("localized-content-or-geometry");
        report.humanImpact.Should().Be("high");
        report.diffBounds.Should().Be(new Program.VisualDiffBounds(30, 30, 40, 40));
        report.topRegions.Should().ContainSingle(r => r.pixelCount == 1600);
    }

    [Fact]
    public void AnalyzeVisualDiff_LocalColorContent_ClassifiesSeparatelyFromGeometry()
    {
        using var reference = CreateSolidBitmap(100, 100, SKColors.White);
        using var actual = CreateSolidBitmap(100, 100, SKColors.White);
        using (var referenceCanvas = new SKCanvas(reference))
        using (var actualCanvas = new SKCanvas(actual))
        using (var referencePaint = new SKPaint { Color = SKColors.Red })
        using (var actualPaint = new SKPaint { Color = SKColors.Blue })
        {
            var rect = new SKRect(30, 30, 70, 70);
            referenceCanvas.DrawRect(rect, referencePaint);
            actualCanvas.DrawRect(rect, actualPaint);
        }

        var report = Program.AnalyzeVisualDiff(actual, reference, tolerance: 16);

        report.category.Should().Be("localized-color-or-image-content");
        report.humanImpact.Should().Be("medium");
        report.topRegions.Should().ContainSingle(r => r.density == 1.0);
    }

    [Fact]
    public async Task VisualDiffCommand_WritesJsonAndTriptych()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdfe-visual-diff-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var actualPath = Path.Combine(root, "actual.png");
            var referencePath = Path.Combine(root, "reference.png");
            var jsonPath = Path.Combine(root, "report.json");
            var outputPath = Path.Combine(root, "triptych.png");

            using (var reference = CreateSolidBitmap(20, 20, SKColors.White))
            using (var actual = CreateSolidBitmap(20, 20, SKColors.White))
            {
                using (var canvas = new SKCanvas(actual))
                using (var paint = new SKPaint { Color = SKColors.Black })
                {
                    canvas.DrawRect(new SKRect(5, 5, 15, 15), paint);
                }

                SavePng(reference, referencePath);
                SavePng(actual, actualPath);
            }

            Environment.ExitCode = 0;
            var exitCode = await Program.RunAsync(new[]
            {
                "visual-diff",
                actualPath,
                referencePath,
                "--json",
                jsonPath,
                "--output",
                outputPath,
                "--tolerance",
                "16",
            });

            exitCode.Should().Be(0);
            Environment.ExitCode.Should().Be(0);
            File.Exists(jsonPath).Should().BeTrue();
            File.Exists(outputPath).Should().BeTrue();

            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            doc.RootElement.GetProperty("category").GetString()
                .Should().Be("localized-content-or-geometry");
            doc.RootElement.GetProperty("humanImpact").GetString()
                .Should().Be("high");
            doc.RootElement.GetProperty("topRegions").GetArrayLength()
                .Should().BeGreaterThan(0);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static SKBitmap CreateSolidBitmap(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        return bitmap;
    }

    private static void SavePng(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);
    }
}
