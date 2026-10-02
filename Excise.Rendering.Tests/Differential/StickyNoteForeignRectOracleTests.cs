using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// A <c>/Text</c> note with no appearance stream and no <c>/Popup</c> is drawn by excise as a post-it card
/// at its own <c>/Rect</c> (#1794). For a note excise authored that is at most one card, but a foreign
/// producer's <c>/Rect</c> is the icon's position and can be anything: veraPDF 6-3-3-t01-fail-a.pdf gives a
/// note the whole page, <c>[0 0 612 792]</c>, and excise painted the entire page yellow where mutool,
/// Ghostscript and pdftocairo draw nothing. That fixture flipped from PASS (0.1 mean pixel difference from
/// every oracle) to DIFF (98.6) when the card was introduced.
///
/// <para>This pins the shape on a synthetic page so it holds without the veraPDF corpus. The oracles are
/// asserted first, so the premise (a note with no /AP draws nothing there) is checked, not assumed.</para>
/// </summary>
public sealed class StickyNoteForeignRectOracleTests
{
    private const int Dpi = 36;

    [Fact]
    public void PageSizedTextNoteWithNoAppearance_DoesNotPaintAPageSizedCard()
    {
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");
        var pdf = BuildPdf();

        var path = Path.Combine(Path.GetTempPath(), $"excise-note-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        SKBitmap? mutool, ghostscript;
        try
        {
            mutool = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
            ghostscript = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
        }
        finally
        {
            File.Delete(path);
        }

        mutool.Should().NotBeNull();
        ghostscript.Should().NotBeNull();
        NonWhiteFraction(mutool!).Should().BeLessThan(0.02, "mutool draws nothing for a /Text with no /AP here");
        NonWhiteFraction(ghostscript!).Should().BeLessThan(0.02, "Ghostscript draws nothing for a /Text with no /AP here");

        using var doc = PdfDocument.Open(pdf);
        using var excise = new SkiaRenderer().RenderPage(
            doc.GetPage(1), new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White });

        // Largest card excise authors is 220x150 pt, 6.8% of a 612x792 page; the bug filled 100%.
        NonWhiteFraction(excise).Should().BeLessThan(0.10,
            "a foreign page-sized /Rect must not become a page-sized card (the pale-yellow page of veraPDF 6-3-3-t01-fail-a)");
        var farCorner = excise.GetPixel(excise.Width - 5, excise.Height - 5);
        farCorner.Should().Be(SKColors.White, "the far corner is outside any card anchored at the Rect's top-left");
    }

    private static double NonWhiteFraction(SKBitmap bitmap)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Red < 250 || c.Green < 250 || c.Blue < 250)
                    count++;
            }
        }

        return (double)count / (bitmap.Width * bitmap.Height);
    }

    /// <summary>One letter-size page with a /Text note, no /AP, no /Popup, /Rect the whole page (the shape of the veraPDF fixture).</summary>
    private static byte[] BuildPdf()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Text /Border [0 0 0] /P 3 0 R /Rect [0 0 612 792] /F 68 >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new long[objects.Length + 1];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= objects.Length; i++)
            sb.Append($"{offsets[i]:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Root 1 0 R /Size {objects.Length + 1} >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
