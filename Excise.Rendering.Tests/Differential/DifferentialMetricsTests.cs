using AwesomeAssertions;
using Excise.Rendering.Differential;
using SkiaSharp;

namespace Excise.Rendering.Tests.Differential;

public class DifferentialMetricsTests
{
    [Theory]
    [InlineData(SKColorType.Rgba8888)]
    [InlineData(SKColorType.Bgra8888)]
    public void Compare_ComputesExpectedMetricsForPacked32Bitmaps(SKColorType colorType)
    {
        using var a = CreateBitmap(colorType, SKColors.Black);
        using var b = CreateBitmap(colorType, SKColors.Black);
        b.SetPixel(1, 0, new SKColor(255, 0, 0, 255));
        b.SetPixel(0, 1, new SKColor(0, 80, 0, 255));

        var report = DifferentialMetrics.Compare(a, b);

        report.Width.Should().Be(2);
        report.Height.Should().Be(2);
        report.PixelCount.Should().Be(4);
        report.DifferingPixels.Should().Be(2);
        report.DifferingPixelFraction.Should().Be(0.5);
        report.MeanAbsoluteError.Should().BeApproximately(335.0 / 12.0, 0.0001);
        report.MaxAbsoluteError.Should().Be(255);
    }

    [Fact]
    public void Compare_PremultipliedAlphaMatchesGetPixelSemantics()
    {
        using var a = new SKBitmap(2, 1, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var b = new SKBitmap(2, 1, SKColorType.Rgba8888, SKAlphaType.Premul);
        a.SetPixel(0, 0, new SKColor(100, 20, 0, 128));
        a.SetPixel(1, 0, new SKColor(0, 0, 0, 0));
        b.SetPixel(0, 0, new SKColor(0, 20, 100, 128));
        b.SetPixel(1, 0, new SKColor(80, 0, 0, 0));

        var expected = CompareWithGetPixel(a, b);

        var report = DifferentialMetrics.Compare(a, b);

        report.DifferingPixels.Should().Be(expected.DifferingPixels);
        report.DifferingPixelFraction.Should().Be(expected.DifferingPixelFraction);
        report.MeanAbsoluteError.Should().BeApproximately(expected.MeanAbsoluteError, 0.0001);
        report.MaxAbsoluteError.Should().Be(expected.MaxAbsoluteError);
    }

    private static SKBitmap CreateBitmap(SKColorType colorType, SKColor color)
    {
        var bitmap = new SKBitmap(2, 2, colorType, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        return bitmap;
    }

    private static ExpectedMetrics CompareWithGetPixel(SKBitmap a, SKBitmap b)
    {
        long differingPixels = 0;
        long sumL1 = 0;
        int maxL1 = 0;

        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                var pa = a.GetPixel(x, y);
                var pb = b.GetPixel(x, y);
                int dR = Math.Abs(pa.Red - pb.Red);
                int dG = Math.Abs(pa.Green - pb.Green);
                int dB = Math.Abs(pa.Blue - pb.Blue);
                int l1 = dR + dG + dB;
                int worst = Math.Max(dR, Math.Max(dG, dB));

                sumL1 += l1;
                if (worst > maxL1) maxL1 = worst;
                if (worst > 64) differingPixels++;
            }
        }

        var pixelCount = a.Width * a.Height;
        return new ExpectedMetrics(
            DifferingPixels: (int)differingPixels,
            DifferingPixelFraction: differingPixels / (double)pixelCount,
            MeanAbsoluteError: sumL1 / (pixelCount * 3.0),
            MaxAbsoluteError: maxL1);
    }

    private sealed record ExpectedMetrics(
        int DifferingPixels,
        double DifferingPixelFraction,
        double MeanAbsoluteError,
        double MaxAbsoluteError);
}
