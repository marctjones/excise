using AwesomeAssertions;
using Pdfe.Core.Document;
using SkiaSharp;

namespace Pdfe.Rendering.Tests.Visual;

public sealed class CmykTransparencyRegressionTests
{
    [Fact]
    public void RenderPage_Issue13520_DoesNotPaintDarkSoftMaskLobe()
    {
        var path = ResolveRepoPath("test-pdfs", "pdfjs", "issue13520.pdf");
        if (!File.Exists(path))
            return;

        using var doc = PdfDocument.Open(path);
        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 150, BackgroundColor = SKColors.White });

        CountDarkPurplePixelsInRightLobe(bitmap).Should().BeLessThan(25,
            "the soft-masked non-isolated Screen group should blend against the backdrop instead of producing a dark purple lobe");
    }

    private static int CountDarkPurplePixelsInRightLobe(SKBitmap bitmap)
    {
        var count = 0;
        var left = (int)Math.Floor(bitmap.Width * 0.78);
        var top = (int)Math.Floor(bitmap.Height * 0.30);
        var bottom = (int)Math.Ceiling(bitmap.Height * 0.70);

        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 80 && pixel.Green < 80 && pixel.Blue < 130)
                    count++;
            }
        }

        return count;
    }

    private static string ResolveRepoPath(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(new[] { dir }.Concat(parts).ToArray());
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return candidate;

            var parent = Directory.GetParent(dir);
            if (parent == null)
                break;
            dir = parent.FullName;
        }

        return Path.Combine(new[] { AppContext.BaseDirectory }.Concat(parts).ToArray());
    }
}
