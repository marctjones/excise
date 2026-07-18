using AwesomeAssertions;
using SkiaSharp;
using System;
using System.IO;

namespace Excise.Rendering.Tests.Visual;

/// <summary>
/// Helper class for visual regression testing.
/// Provides methods to compare rendered images against baseline images.
/// </summary>
public static class VisualAssertions
{
    /// <summary>
    /// Compare two SKBitmaps and return the percentage of different pixels.
    /// </summary>
    /// <param name="actual">The actual rendered image</param>
    /// <param name="expected">The expected baseline image</param>
    /// <returns>Percentage of pixels that differ (0.0 = identical, 1.0 = completely different)</returns>
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
                var actualPixel = actual.GetPixel(x, y);
                var expectedPixel = expected.GetPixel(x, y);

                if (!PixelsMatch(actualPixel, expectedPixel))
                {
                    differentPixels++;
                }
            }
        }

        return (double)differentPixels / totalPixels;
    }

    /// <summary>
    /// Check if two pixels are the same (with small tolerance for rendering variations).
    /// </summary>
    private static bool PixelsMatch(SKColor a, SKColor b, int tolerance = 2)
    {
        return Math.Abs(a.Red - b.Red) <= tolerance &&
               Math.Abs(a.Green - b.Green) <= tolerance &&
               Math.Abs(a.Blue - b.Blue) <= tolerance &&
               Math.Abs(a.Alpha - b.Alpha) <= tolerance;
    }

    /// <summary>
    /// Create a diff image showing where pixels differ between actual and expected.
    /// </summary>
    /// <param name="actual">The actual rendered image</param>
    /// <param name="expected">The expected baseline image</param>
    /// <returns>Diff image where different pixels are highlighted in red</returns>
    public static SKBitmap CreateDiffImage(SKBitmap actual, SKBitmap expected)
    {
        if (actual.Width != expected.Width || actual.Height != expected.Height)
        {
            throw new ArgumentException("Image dimensions must match for diff generation");
        }

        var diff = new SKBitmap(actual.Width, actual.Height);

        for (int y = 0; y < actual.Height; y++)
        {
            for (int x = 0; x < actual.Width; x++)
            {
                var actualPixel = actual.GetPixel(x, y);
                var expectedPixel = expected.GetPixel(x, y);

                if (!PixelsMatch(actualPixel, expectedPixel))
                {
                    // Highlight differences in red
                    diff.SetPixel(x, y, new SKColor(255, 0, 0, 255));
                }
                else
                {
                    // Keep original pixel (slightly faded)
                    var faded = new SKColor(
                        (byte)(actualPixel.Red * 0.5),
                        (byte)(actualPixel.Green * 0.5),
                        (byte)(actualPixel.Blue * 0.5),
                        actualPixel.Alpha);
                    diff.SetPixel(x, y, faded);
                }
            }
        }

        return diff;
    }

    /// <summary>
    /// Save a SKBitmap to a PNG file.
    /// </summary>
    public static void SavePng(SKBitmap bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);
    }

    /// <summary>
    /// Load a SKBitmap from a PNG file.
    /// </summary>
    public static SKBitmap LoadPng(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Baseline image not found: {path}");
        }

        using var stream = File.OpenRead(path);
        return SKBitmap.Decode(stream);
    }
}

/// <summary>
/// FluentAssertions extensions for visual regression testing.
/// </summary>
public static class VisualAssertionExtensions
{
    /// <summary>
    /// Assert that an actual rendered image matches a baseline image within a tolerance.
    /// </summary>
    /// <param name="actual">The actual rendered image</param>
    /// <param name="baselinePath">Path to baseline image</param>
    /// <param name="maxDifferencePercent">Maximum allowed difference (0.01 = 1%)</param>
    /// <param name="saveDiffTo">Optional path to save diff image if test fails</param>
    public static void ShouldVisuallyMatch(
        this SKBitmap actual,
        string baselinePath,
        double maxDifferencePercent = 0.01,
        string? saveDiffTo = null)
    {
        var expected = VisualAssertions.LoadPng(baselinePath);

        try
        {
            // Check dimensions first
            actual.Width.Should().Be(expected.Width, "image width should match baseline");
            actual.Height.Should().Be(expected.Height, "image height should match baseline");

            // Calculate pixel difference
            var difference = VisualAssertions.CalculatePixelDifference(actual, expected);

            // If test fails, save diff image
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
