using SkiaSharp;

namespace Pdfe.ImageInspection;

public static class ImageInfoAnalyzer
{
    public static ImageInfoReport Analyze(SKBitmap bitmap, string? path = null)
    {
        long sumR = 0;
        long sumG = 0;
        long sumB = 0;
        long sumA = 0;
        long darkPixels = 0;
        long transparentPixels = 0;
        long nonWhitePixels = 0;
        var pixelCount = checked((long)bitmap.Width * bitmap.Height);

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                sumR += color.Red;
                sumG += color.Green;
                sumB += color.Blue;
                sumA += color.Alpha;
                if (Luminance(color) < 64)
                    darkPixels++;
                if (color.Alpha < 255)
                    transparentPixels++;
                if (color.Red < 250 || color.Green < 250 || color.Blue < 250 || color.Alpha < 255)
                    nonWhitePixels++;
            }
        }

        return new ImageInfoReport
        {
            path = path,
            width = bitmap.Width,
            height = bitmap.Height,
            pixelCount = pixelCount,
            colorType = bitmap.ColorType.ToString(),
            alphaType = bitmap.AlphaType.ToString(),
            meanRed = Mean(sumR),
            meanGreen = Mean(sumG),
            meanBlue = Mean(sumB),
            meanAlpha = Mean(sumA),
            darkPixels = darkPixels,
            transparentPixels = transparentPixels,
            nonWhitePixels = nonWhitePixels,
        };

        double Mean(long sum) => pixelCount == 0 ? 0 : (double)sum / pixelCount;
    }

    private static double Luminance(SKColor color)
        => 0.299 * color.Red + 0.587 * color.Green + 0.114 * color.Blue;
}

public sealed class ImageInfoReport
{
    public string? path { get; set; }
    public int width { get; set; }
    public int height { get; set; }
    public long pixelCount { get; set; }
    public string colorType { get; set; } = "";
    public string alphaType { get; set; } = "";
    public double meanRed { get; set; }
    public double meanGreen { get; set; }
    public double meanBlue { get; set; }
    public double meanAlpha { get; set; }
    public long darkPixels { get; set; }
    public long transparentPixels { get; set; }
    public long nonWhitePixels { get; set; }
}
