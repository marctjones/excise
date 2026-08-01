using System;
using System.IO;
using AwesomeAssertions;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// An image whose sample data cannot possibly be the declared image must not be
/// painted (#878).
///
/// THE FAILURE THIS PINS
/// ---------------------
/// The raw-sample loop substitutes 0 for any sample past the end of the decoded
/// buffer. In 1-bpc /DeviceGray a sample of 0 is BLACK, so a buffer that stops
/// early does not yield a short image — it yields a full page of fabricated
/// ink, silently.
///
/// It is reachable whenever a filter fails to decode and falls back to handing
/// back its raw ENCODED bytes. On the fixture below the arithmetic is exact:
///
///     /JBIG2Decode stream       189 bytes
///     1152 x 720 at 1 bpc   103,680 bytes required
///     one row                   144 bytes
///
/// So 189 bytes painted row 0 as noise and the remaining 899 rows came out
/// solid black. Measured before the fix: every white pixel excise produced sat
/// on row y=0, and the real content (the word "Test", which the oracles place
/// at y[104..209]) was absent entirely.
///
/// WHY THIS IS WORSE THAN LOOKING WRONG
/// ------------------------------------
/// excise is a redaction tool. A page rendered as a solid black rectangle is
/// visually indistinguishable from a page that was successfully redacted, so
/// this failure mode mimics success for the exact operation the tool exists to
/// perform. A reviewer checking that their redaction landed would have been
/// reassured by a bug.
///
/// Note this does NOT assert that the image decodes correctly — it does not,
/// and that is #874. It asserts that failing to decode produces an honest
/// omission rather than invented ink.
/// </summary>
public class UndecodableImageFabricationTests
{
    private const int Dpi = 150;

    [Fact]
    public void ImageWithFarTooLittleSampleData_IsNotPaintedAsInk()
    {
        var path = FindFixture("bug_631912.pdf");
        Assert.SkipWhen(path == null,
            "PDFium corpus not present — run scripts/download-pdfium-corpus.sh");

        using var mine = RenderWithExcise(path!, 1, Dpi);
        mine.Should().NotBeNull("the page itself is well-formed; only its image is undecodable");

        var black = NearBlackFraction(mine!);
        black.Should().BeLessThan(0.01,
            "the /JBIG2Decode stream is 189 bytes against the 103,680 a 1152x720 1-bpc image " +
            "needs. Painting it fills 899 of 900 rows with fabricated black — a page that looks " +
            $"exactly like a successful redaction. Measured near-black fraction: {black:F4}");
    }

    /// <summary>
    /// The oracle half of the same claim. Asserting only that excise draws no
    /// black would also pass if excise drew nothing at all for a page that
    /// genuinely is black, so pin what the independent renderer says the page
    /// looks like.
    /// </summary>
    [Fact]
    public void MutoolAgrees_ThePageIsNotBlack()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        var path = FindFixture("bug_631912.pdf");
        Assert.SkipWhen(path == null, "PDFium corpus not present");

        using var reference = MutoolReferenceRenderer.RenderPage(path!, 1, Dpi);
        reference.Should().NotBeNull();

        var black = NearBlackFraction(reference!);
        black.Should().BeLessThan(0.01,
            "mutool renders this page as essentially white with a small handwritten mark. If it " +
            "ever renders mostly black, the assertion above is measuring the wrong thing and the " +
            $"fixture must be re-examined. Measured: {black:F4}");
    }

    // ---------------------------------------------------------------- helpers --

    private static SKBitmap? RenderWithExcise(string path, int pageNumber, int dpi)
    {
        using var doc = Excise.Core.Document.PdfDocument.Open(File.ReadAllBytes(path));
        var renderer = new SkiaRenderer();
        return renderer.RenderPage(doc.GetPage(pageNumber), new RenderOptions { Dpi = dpi });
    }

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
