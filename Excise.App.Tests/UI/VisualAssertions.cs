using AwesomeAssertions;
using SkiaSharp;
using System;
using System.IO;

namespace Excise.App.Tests.UI;

// Copy of Excise.Rendering.Tests/Visual/VisualAssertions.cs.
// When a third consumer appears, extract into a shared test-utilities package.
public static class VisualAssertions
{
    public static double CalculatePixelDifference(SKBitmap actual, SKBitmap expected)
    {
        if (actual.Width != expected.Width || actual.Height != expected.Height)
        {
            throw new ArgumentException(
                $"Image dimensions must match. Actual: {actual.Width}x{actual.Height}, Expected: {expected.Width}x{expected.Height}");
        }

        int totalPixels = actual.Width * actual.Height;
        int differentPixels = 0;

        for (int y = 0; y < actual.Height; y++)
        {
            for (int x = 0; x < actual.Width; x++)
            {
                if (!PixelsMatch(actual.GetPixel(x, y), expected.GetPixel(x, y)))
                    differentPixels++;
            }
        }

        return (double)differentPixels / totalPixels;
    }

    private static bool PixelsMatch(SKColor a, SKColor b, int tolerance = 2)
    {
        return Math.Abs(a.Red - b.Red) <= tolerance &&
               Math.Abs(a.Green - b.Green) <= tolerance &&
               Math.Abs(a.Blue - b.Blue) <= tolerance &&
               Math.Abs(a.Alpha - b.Alpha) <= tolerance;
    }

    public static SKBitmap CreateDiffImage(SKBitmap actual, SKBitmap expected)
    {
        if (actual.Width != expected.Width || actual.Height != expected.Height)
            throw new ArgumentException("Image dimensions must match for diff generation");

        var diff = new SKBitmap(actual.Width, actual.Height);
        for (int y = 0; y < actual.Height; y++)
        {
            for (int x = 0; x < actual.Width; x++)
            {
                var a = actual.GetPixel(x, y);
                var e = expected.GetPixel(x, y);
                if (!PixelsMatch(a, e))
                {
                    diff.SetPixel(x, y, new SKColor(255, 0, 0, 255));
                }
                else
                {
                    diff.SetPixel(x, y, new SKColor(
                        (byte)(a.Red * 0.5),
                        (byte)(a.Green * 0.5),
                        (byte)(a.Blue * 0.5),
                        a.Alpha));
                }
            }
        }
        return diff;
    }

    public static void SavePng(SKBitmap bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);
    }

    public static SKBitmap LoadPng(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Baseline image not found: {path}");
        using var stream = File.OpenRead(path);
        return SKBitmap.Decode(stream);
    }
}

public static class VisualAssertionExtensions
{
    public static void ShouldVisuallyMatch(
        this SKBitmap actual,
        string baselinePath,
        double maxDifferencePercent = 0.01,
        string? saveDiffTo = null)
    {
        var expected = VisualAssertions.LoadPng(baselinePath);
        try
        {
            actual.Width.Should().Be(expected.Width, "image width should match baseline");
            actual.Height.Should().Be(expected.Height, "image height should match baseline");

            var difference = VisualAssertions.CalculatePixelDifference(actual, expected);
            if (difference > maxDifferencePercent && saveDiffTo != null)
            {
                var diff = VisualAssertions.CreateDiffImage(actual, expected);
                VisualAssertions.SavePng(diff, saveDiffTo);
                Console.WriteLine($"Visual difference detected. Diff saved to: {saveDiffTo}");
            }

            difference.Should().BeLessThanOrEqualTo(maxDifferencePercent,
                $"image should visually match baseline (actual difference: {difference:P2})");
        }
        finally
        {
            expected.Dispose();
        }
    }
}
