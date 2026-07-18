using System;
using System.IO;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Visual;

/// <summary>
/// Regression test for a SetFont ordering bug: the CFF→OpenType wrapper
/// reads <c>_currentFontWidths</c> / <c>_currentFontFirstChar</c> to
/// build hmtx, but those fields used to be populated *after* the wrapper
/// was called. So every embedded font got wrapped with the previous
/// font's widths (or zero/null on the first font), producing visibly
/// broken layout — overlapping glyphs in some fonts, wide gaps in
/// others — on any document with multiple embedded fonts on a page.
/// The "Business Success with Open Source" book (XEP-produced) exposed
/// this on every page after the cover.
/// </summary>
public class MultiEmbeddedFontLayoutTests
{
    // The fixture lives outside the repo (large file, not redistributable;
    // gitignored under test-pdfs/, populated locally from
    // test-pdfs/manifests/local-real-world-books.json). This used to be a
    // hardcoded single-machine absolute path
    // ("/home/marc/Downloads/business-success-with-open-source_P1.0.pdf")
    // that doesn't exist on any dev machine or CI runner but this repo's
    // original author's — meaning this regression test, written for a real
    // SetFont-ordering bug, silently skipped everywhere else the whole time
    // (the exact #619 "invisible coverage loss" pattern). Resolved via the
    // same FindRepoFile convention every other corpus-dependent test in
    // this project uses.
    private static string? BookPath => FindRepoFile("test-pdfs", "local-real-world",
        "business-success-with-open-source_P1.0.pdf");

    [Fact]
    public void Page3_ParagraphHasNoLargeIntraLineGaps()
    {
        // Tests skip cleanly when the local corpus is missing rather than
        // failing CI.
        if (BookPath == null)
            return;

        using var doc = PdfDocument.Open(File.ReadAllBytes(BookPath));
        var page = doc.GetPage(3);
        using var bmp = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 150 });

        // Sample the first paragraph row ("We all use open source software ...").
        // At 150 DPI on a 540×648pt page, that line lands roughly at y=350.
        // Look for any white gap on that row wider than a typical word
        // space — pre-fix layouts had ~80px gaps from compounded drift.
        int yMid = bmp.Height / 2 - bmp.Height / 8; // approx top-third paragraph band
        int maxGap = MaxWhiteGapInRow(bmp, yMid - 4, yMid + 4);

        // Threshold: ~60px is plenty for a normal word-space at 150 DPI in
        // 9pt Bookman; pre-fix the row had isolated gaps wider than that.
        maxGap.Should().BeLessThan(
            60,
            $"intra-line whitespace runs longer than 60px indicate the per-glyph " +
            $"width table was wrong for this font — the SetFont/CFF-wrap order bug. " +
            $"Largest gap actually observed: {maxGap}px.");
    }

    [Fact]
    public void Page3_BulletColumn_HasInk()
    {
        // The bullet (➤) sits at PDF x=108pt next to the first reviewer
        // ("Johanna Rothman"). For the rendered ZapfDingbats glyph, accept
        // either an embedded CFF outline (when wrapping succeeds) or a
        // system-symbol-font fallback drawn via the GetTypeface path —
        // both produce ink in roughly the same place.
        if (BookPath == null) return;

        using var doc = PdfDocument.Open(File.ReadAllBytes(BookPath));
        var page = doc.GetPage(3);
        using var bmp = new SkiaRenderer().RenderPage(page, new RenderOptions { Dpi = 150 });

        // Bullet baseline ≈ y=412pt → screenY = (648 - 412) * 150/72 ≈ 491px.
        int xCenter = (int)(108 * 150 / 72.0) + 8;
        int yCenter = (int)((648 - 412) * 150 / 72.0);
        int inkCount = 0;
        for (int y = yCenter - 15; y < yCenter + 15; y++)
        {
            if (y < 0 || y >= bmp.Height) continue;
            for (int x = xCenter - 15; x < xCenter + 15; x++)
            {
                if (x < 0 || x >= bmp.Width) continue;
                var c = bmp.GetPixel(x, y);
                if (c.Red < 100 && c.Green < 100 && c.Blue < 100) inkCount++;
            }
        }
        inkCount.Should().BeGreaterThan(20,
            $"the dingbats bullet must produce visible ink near ({xCenter},{yCenter}); " +
            $"got {inkCount} ink pixels — see commit-message context for the wrap-fallback path.");
    }

    private static string? FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// Largest run of all-white pixel columns lying *between* two ink columns
    /// in the given y band. Ignores leading/trailing whitespace.
    /// </summary>
    private static int MaxWhiteGapInRow(SKBitmap bmp, int yStart, int yEnd)
    {
        int? firstInk = null, lastInk = null;
        bool[] inkColumn = new bool[bmp.Width];

        for (int x = 0; x < bmp.Width; x++)
        {
            for (int y = yStart; y < yEnd && y < bmp.Height; y++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 100 && c.Green < 100 && c.Blue < 100)
                {
                    inkColumn[x] = true;
                    firstInk ??= x;
                    lastInk = x;
                    break;
                }
            }
        }

        if (firstInk == null) return 0;

        int maxGap = 0, currentGap = 0;
        for (int x = firstInk.Value; x <= lastInk!.Value; x++)
        {
            if (inkColumn[x]) currentGap = 0;
            else { currentGap++; if (currentGap > maxGap) maxGap = currentGap; }
        }
        return maxGap;
    }
}
