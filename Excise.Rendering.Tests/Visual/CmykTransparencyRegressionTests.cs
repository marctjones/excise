using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;

namespace Excise.Rendering.Tests.Visual;

public sealed class CmykTransparencyRegressionTests
{
    /// <summary>
    /// pdf.js <c>issue13520.pdf</c>: the right-hand lobe carries a soft-masked
    /// non-isolated group (<c>Fm2</c>) whose two nested groups are invoked
    /// <c>/BM /Screen</c>. Screen against the page's inky backdrop composites to
    /// LESS ink than the backdrop alone, so the lobe stays pale; the nested
    /// groups' own shadings are near-black and must never reach the page raw.
    ///
    /// <para>#1505: #1395's child-context composite made those nested groups
    /// child-in-child, where the pre-seed sync wiped the enclosing child's own
    /// seeded backdrop and the Screen blend short-circuited to its raw source —
    /// 439 dark pixels against this 25-pixel gate.
    /// <see cref="Excise.Rendering.Tests.Differential.DeviceCmykNestedNonIsolatedGroupBlendTests"/>
    /// isolates the mechanism; this row is the acceptance on the document that
    /// found it.</para>
    /// </summary>
    [Fact]
    public void RenderPage_Issue13520_DoesNotPaintDarkSoftMaskLobe()
    {
        var path = ResolveRepoPath("test-pdfs", "pdfjs", "issue13520.pdf");
        // #1504 found this as `if (!File.Exists(path)) return;` — a silent
        // vacuous pass on a machine without the pdf.js corpus, and invisible to
        // the #1172 skip gate because a `return` leaves no NotExecuted result.
        Assert.SkipUnless(File.Exists(path), "test-pdfs/pdfjs corpus not downloaded");

        using var doc = PdfDocument.Open(path);
        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 150, BackgroundColor = SKColors.White });

        CountDarkPurplePixelsInRightLobe(bitmap).Should().BeLessThan(25,
            "the soft-masked non-isolated Screen group should blend against the backdrop instead of producing a dark purple lobe");
    }

    /// <summary>
    /// The same region read by two renderers that are not excise. The 25-pixel
    /// gate above is a number someone chose; this says what the page actually
    /// is — and says it with the oracles that AGREE, because pdftocairo does
    /// not: it renders about 103 dark pixels here, sharing the very defect
    /// (#1373/#1394, and #1505's own measurement on a synthetic nested group)
    /// that this test exists to catch. Corroborating against it would have
    /// defended the bug.
    /// </summary>
    [Fact]
    public void RenderPage_Issue13520_RightLobeAgreesWithMutoolAndGhostscript()
    {
        var path = ResolveRepoPath("test-pdfs", "pdfjs", "issue13520.pdf");
        Assert.SkipUnless(File.Exists(path), "test-pdfs/pdfjs corpus not downloaded");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        using var mutool = MutoolReferenceRenderer.RenderPage(path, 1, 150);
        using var gs = GhostscriptReferenceRenderer.RenderPage(path, 1, 150);
        mutool.Should().NotBeNull();
        gs.Should().NotBeNull();

        var mutoolDark = CountDarkPurplePixelsInRightLobe(mutool!);
        var gsDark = CountDarkPurplePixelsInRightLobe(gs!);
        mutoolDark.Should().BeLessThan(25,
            "mutool is the target, so a change in mutool's own answer invalidates the gate, not excise");
        gsDark.Should().BeLessThan(25, "and Ghostscript independently agrees with mutool");

        using var doc = PdfDocument.Open(path);
        using var bitmap = new SkiaRenderer().RenderPage(
            doc.GetPage(1),
            new RenderOptions { Dpi = 150, BackgroundColor = SKColors.White });

        CountDarkPurplePixelsInRightLobe(bitmap).Should().BeLessThan(25,
            $"excise must agree with MuPDF ({mutoolDark}) and Ghostscript ({gsDark}) on a region "
            + "both render with no dark pixels at all (#1505)");
    }

    /// <summary>
    /// Counted in bitmap FRACTIONS, not absolute pixels, so the same region is
    /// read on excise's and mutool's 435x187 CropBox raster and on Ghostscript's
    /// 434x186 one.
    /// </summary>
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
