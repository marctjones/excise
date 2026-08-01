using System;
using System.IO;
using AwesomeAssertions;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Colour-key masking (#873): /Mask as an ARRAY of integer ranges makes source
/// samples inside those ranges transparent (PDF 32000-1 §8.9.6.4). This is a
/// different mechanism from the stencil form, /Mask as a stream.
///
/// excise implemented only the stencil form. The array form hit a
/// `is not PdfStream` type check and was discarded without a trace, so the
/// image was drawn fully opaque and pixels the document marks transparent were
/// painted.
///
/// WHY THE FAILURE DIRECTION MATTERS
/// ---------------------------------
/// This makes excise show ink that other readers do not. For a redaction tool
/// that is the wrong way round: a reviewer can be looking at a materially
/// different page from the one the recipient sees, and "there is something
/// there" is exactly the judgement they are being asked to make.
///
/// ORACLE, NOT SELF-ASSERTION
/// --------------------------
/// The assertion is against mutool, an independent renderer. excise agreeing
/// with excise about masking would prove only that its bugs are consistent.
/// </summary>
public class ColorKeyMaskDifferentialTests
{
    private const int Dpi = 150;

    /// <summary>
    /// pdfium's bug_343075986.pdf is a 4x4 /Indexed /DeviceRGB image whose
    /// palette is black, red, green, blue, white, carrying:
    ///
    ///     /Mask [0 0 0 0 0 0]   % Mask out black.
    ///
    /// So palette index 0 (black) must be transparent.
    ///
    /// METRIC — measured, and NOT the obvious one. Ink coverage, the metric the
    /// neighbouring differential tests use, is completely blind here:
    ///
    ///     mask honoured : ink 0.8199   near-black 0.000
    ///     mask ignored  : ink 0.8199   near-black 0.188
    ///     mutool        : ink 0.8140   near-black 0.000
    ///
    /// Ink is identical to five decimal places either way, because the masked
    /// black is drawn over content that is inked regardless — masking it
    /// reveals colour beneath rather than bare page. An ink-coverage assertion
    /// here passes whether or not the bug is present, which is exactly what the
    /// first draft of this test did until a mutation run caught it.
    ///
    /// Near-black fraction is the signal: 0.188 vs 0.000, matching the 0.194
    /// per-pixel divergence the corpus scan measured against all five oracles.
    /// </summary>
    [Fact]
    public void ColorKeyMaskedIndexedImage_LeavesNoBlackWhereMutoolLeavesNone()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = FindFixture("bug_343075986.pdf");
        Assert.SkipWhen(path == null,
            "PDFium corpus not present — run scripts/download-pdfium-corpus.sh");

        using var reference = MutoolReferenceRenderer.RenderPage(path!, 1, Dpi);
        reference.Should().NotBeNull("mutool must render this fixture");

        using var mine = RenderWithExcise(path!, 1, Dpi);
        mine.Should().NotBeNull("excise must render a page mutool can render");

        // Vacuity guard, inside the test that depends on it: the image must
        // actually be drawn. A blank page trivially has no black pixels.
        var mineInk = InkCoverage(mine!);
        mineInk.Should().BeGreaterThan(0.5,
            "the image covers most of the page; a near-blank render would make the " +
            $"black-pixel assertion below meaningless (ink={mineInk:F4})");

        var referenceBlack = NearBlackFraction(reference!);
        referenceBlack.Should().BeLessThan(0.01,
            "mutool honours the colour key, so its render carries essentially no black — " +
            "if this ever fails the oracle has changed and the assertion below is not " +
            $"measuring what it claims (mutool near-black={referenceBlack:F4})");

        var mineBlack = NearBlackFraction(mine!);
        mineBlack.Should().BeLessThan(0.01,
            "/Mask [0 0 0 0 0 0] makes palette index 0 (black) transparent. Ignoring the " +
            "colour-key form paints that index and leaves ~19% of the page black where " +
            $"mutool leaves none. excise near-black={mineBlack:F4} " +
            $"mutool near-black={referenceBlack:F4}");
    }

    // ---------------------------------------------------------------- helpers --

    private static SKBitmap? RenderWithExcise(string path, int pageNumber, int dpi)
    {
        using var doc = Excise.Core.Document.PdfDocument.Open(File.ReadAllBytes(path));
        var renderer = new SkiaRenderer();
        return renderer.RenderPage(doc.GetPage(pageNumber), new RenderOptions { Dpi = dpi });
    }

    /// Fraction of non-background pixels — insensitive to where antialiasing
    /// lands, sensitive to whether a region was painted at all.
    private static double InkCoverage(SKBitmap bitmap)
    {
        long inked = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var p = bitmap.GetPixel(x, y);
                if (p.Alpha > 16 && (p.Red < 240 || p.Green < 240 || p.Blue < 240))
                    inked++;
            }
        }

        return (double)inked / Math.Max(1, (long)bitmap.Width * bitmap.Height);
    }

    /// Fraction of near-black pixels. The colour-key mask in this fixture
    /// removes exactly one palette entry — black — so this isolates the masked
    /// region in a way total ink cannot (see the metric note above).
    private static double NearBlackFraction(SKBitmap bitmap)
    {
        long black = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                var p = bitmap.GetPixel(x, y);
                if (p.Alpha > 16 && p.Red < 40 && p.Green < 40 && p.Blue < 40)
                    black++;
            }
        }

        return (double)black / Math.Max(1, (long)bitmap.Width * bitmap.Height);
    }

    private static string? FindFixture(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "test-pdfs", "pdfium", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
