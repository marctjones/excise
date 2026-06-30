using System.Text.Json;
using AwesomeAssertions;
using Pdfe.ImageInspection;
using SkiaSharp;
using Xunit;

using RenderProgram = Pdfe.RenderTools.Program;

namespace Pdfe.Cli.Tests;

public class VisualDiffCommandTests
{
    [Fact]
    public void AnalyzeVisualDiff_ColorToneDrift_ClassifiesAsLowImpactColor()
    {
        using var reference = CreateSolidBitmap(12, 12, new SKColor(100, 150, 100));
        using var actual = CreateSolidBitmap(12, 12, new SKColor(100, 142, 138));

        var report = VisualDiffAnalyzer.Analyze(actual, reference, tolerance: 8);

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

        var report = VisualDiffAnalyzer.Analyze(actual, reference, tolerance: 16);

        report.category.Should().Be("localized-content-or-geometry");
        report.humanImpact.Should().Be("high");
        report.diffBounds.Should().Be(new VisualDiffBounds(30, 30, 40, 40));
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

        var report = VisualDiffAnalyzer.Analyze(actual, reference, tolerance: 16);

        report.category.Should().Be("localized-color-or-image-content");
        report.humanImpact.Should().Be("medium");
        report.topRegions.Should().ContainSingle(r => r.density == 1.0);
    }

    [Fact]
    public void ApplyCorpusVisualDiffClassification_PopulatesCorpusEntryFields()
    {
        using var reference = CreateSolidBitmap(50, 50, SKColors.White);
        using var actual = CreateSolidBitmap(50, 50, SKColors.White);
        using (var canvas = new SKCanvas(actual))
        using (var paint = new SKPaint { Color = SKColors.Black })
        {
            canvas.DrawRect(new SKRect(10, 10, 30, 30), paint);
        }

        var entry = new RenderProgram.CorpusScanEntry();

        RenderProgram.ApplyCorpusVisualDiffClassification(entry, actual, reference);

        entry.visualCategory.Should().Be("localized-content-or-geometry");
        entry.visualHumanImpact.Should().Be("high");
        entry.visualDiffBounds.Should().Be(new VisualDiffBounds(10, 10, 20, 20));
        entry.visualTopRegions.Should().ContainSingle(r => r.pixelCount == 400);
    }

    [Fact]
    public void ApplyOracleDisagreementMetrics_RecordsPairwiseOracleSpread()
    {
        using var oracleA = CreateSolidBitmap(30, 30, SKColors.White);
        using var oracleB = CreateSolidBitmap(30, 30, SKColors.White);
        using var oracleC = CreateSolidBitmap(30, 30, SKColors.White);
        FillRect(oracleC, 10, 10, 10, 10, SKColors.Black);
        var entry = new RenderProgram.CorpusScanEntry();

        RenderProgram.ApplyOracleDisagreementMetrics(
            entry,
            new (string Name, SKBitmap? Bitmap)[]
            {
                ("a", oracleA),
                ("b", oracleB),
                ("c", oracleC),
            },
            maxDiffFraction: 0.001,
            maxMae: 0.1);

        entry.oracleComparisonPairs.Should().Be(3);
        entry.oracleDisagreeingPairs.Should().Be(2);
        entry.oracleMaxDiffFraction.Should().BeGreaterThan(0.10);
        entry.oracleMaxMae.Should().BeGreaterThan(20);
        entry.oracleMeanDiffFraction.Should().BeGreaterThan(0);
        entry.oracleMeanMae.Should().BeGreaterThan(0);
    }

    [Fact]
    public void AnalyzeVisualDiff_SmallSparseTextEdges_ClassifiesAsLowImpactAntialiasing()
    {
        using var reference = CreateSolidBitmap(125, 20, SKColors.White);
        using var actual = CreateSolidBitmap(125, 20, SKColors.White);
        for (var offset = 0; offset < 4; offset++)
        {
            DrawSparseConnectedEdge(reference, x: 5 + offset * 28, y: 3);
            DrawSparseConnectedEdge(actual, x: 6 + offset * 28, y: 3);
        }

        var report = VisualDiffAnalyzer.Analyze(actual, reference, tolerance: 16);

        report.category.Should().Be("small-text-antialiasing");
        report.humanImpact.Should().Be("low");
        report.darkPixelBalance.Should().BeGreaterThan(0.95);
    }

    [Fact]
    public void AnalyzeVisualDiff_SparseRepeatedLinework_ClassifiesAsTexture()
    {
        using var reference = CreateSolidBitmap(80, 80, SKColors.White);
        using var actual = CreateSolidBitmap(80, 80, SKColors.White);
        for (var y = 10; y <= 60; y += 10)
        {
            DrawHorizontalLine(reference, 10, 70, y, SKColors.Blue);
            DrawHorizontalLine(actual, 10, 70, y + 1, SKColors.Blue);
        }

        var report = VisualDiffAnalyzer.Analyze(actual, reference, tolerance: 16);

        report.category.Should().Be("localized-linework-or-texture");
        report.humanImpact.Should().Be("medium");
        report.darkPixelBalance.Should().BeGreaterThan(0.75);
    }

    [Fact]
    public void AnalyzeVisualDiff_RepeatedSymbolSaturationShift_ClassifiesAsColorTone()
    {
        using var reference = CreateSolidBitmap(100, 100, SKColors.White);
        using var actual = CreateSolidBitmap(100, 100, SKColors.White);
        for (var y = 10; y <= 70; y += 20)
        {
            for (var x = 10; x <= 70; x += 20)
            {
                FillRect(reference, x, y, 10, 10, new SKColor(95, 150, 105));
                FillRect(actual, x, y, 10, 10, new SKColor(45, 210, 75));
            }
        }

        var report = VisualDiffAnalyzer.Analyze(actual, reference, tolerance: 16);

        report.category.Should().Be("color-tone-or-texture");
        report.humanImpact.Should().Be("low");
        report.meanAbsoluteError.Should().BeLessThan(12);
        report.darkPixelBalance.Should().BeGreaterThan(0.75);
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

    private static void DrawSparseConnectedEdge(SKBitmap bitmap, int x, int y)
    {
        for (var col = 0; col < 10; col++)
            bitmap.SetPixel(x + col, y, SKColors.Black);
        for (var row = 1; row < 6; row++)
            bitmap.SetPixel(x + 9, y + row, SKColors.Black);
        for (var col = 0; col < 10; col++)
            bitmap.SetPixel(x + col, y + 6, SKColors.Black);
    }

    private static void DrawHorizontalLine(SKBitmap bitmap, int left, int right, int y, SKColor color)
    {
        for (var x = left; x < right; x++)
            bitmap.SetPixel(x, y, color);
    }

    private static void FillRect(SKBitmap bitmap, int left, int top, int width, int height, SKColor color)
    {
        for (var y = top; y < top + height; y++)
        {
            for (var x = left; x < left + width; x++)
                bitmap.SetPixel(x, y, color);
        }
    }
}
