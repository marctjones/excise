using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Redaction of scanned / rasterized content (issue #609).
///
/// A scanned document has no glyphs. The words are PIXELS inside an image
/// XObject. Glyph-level removal has nothing to remove, and text extraction has
/// nothing to find — so every text-based assertion we own passes trivially while
/// the name is still plainly legible to a human looking at the page.
///
/// That is the purest form of the blind spot this track keeps rediscovering: a
/// green suite over a leaking document. Extraction cannot see ink. Only a
/// renderer can.
///
/// Two shapes are covered:
///
///   1. A pure scan — text exists only as image pixels. Redaction must delete
///      the PIXELS. Covering them with a black box would leave the original
///      image data intact in the file, recoverable by anyone who pulls the
///      XObject out with `mutool extract`.
///
///   2. A searchable scan (what OCR produces, and what #627 will make excise
///      produce) — the same text exists TWICE: as raster pixels AND as an
///      invisible text layer (Tr 3) laid over them for selection and search.
///      Redaction must remove BOTH. Removing only the text layer leaves the
///      word legible in the scan; removing only the pixels leaves it
///      extractable, selectable, and searchable.
///
/// Assertions are made with an independent renderer, as an ink differential over
/// the redacted region: ink present before, gone after, and the untargeted
/// content still there.
/// </summary>
public class ScannedRasterRedactionLeakTests : IDisposable
{
    private readonly List<string> _temp = new();

    // The "scanned" ink block: where the secret is burned into the raster.
    private static readonly PdfRectangle SecretBlock = new(72, 680, 272, 730);
    // A second block that must survive, so "delete everything" cannot pass.
    private static readonly PdfRectangle KeepBlock = new(72, 480, 272, 530);

    [Fact]
    public void ScannedInk_IsDeletedFromTheRaster_NotCoveredByABox()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        var pdf = PdfDocument.Open(ScannedPagePdf(withInvisibleTextLayer: false));
        var page = pdf.GetPage(1);

        var beforePath = SaveTemp(pdf);
        using var before = GhostscriptReferenceRenderer.RenderPage(beforePath, 1, dpi: 150);
        before.Should().NotBeNull();

        InkFractionIn(before!, SecretBlock, page.Height).Should().BeGreaterThan(0.5,
            "fixture sanity — the secret must be a solid block of scanned ink before we redact it");

        page.RedactArea(SecretBlock, RedactionOptions.Default with { DrawBox = false });
        var afterPath = SaveTemp(pdf);

        using var after = GhostscriptReferenceRenderer.RenderPage(afterPath, 1, dpi: 150);
        after.Should().NotBeNull();

        InkFractionIn(after!, SecretBlock, page.Height).Should().BeLessThan(0.01,
            "an independent renderer still draws the scanned ink. On a scanned page there are no " +
            "glyphs to remove and no text to extract, so EVERY text-based assertion we own passes " +
            "while the word remains plainly legible. The pixels themselves must go.");

        InkFractionIn(after!, KeepBlock, page.Height).Should().BeGreaterThan(0.5,
            "the untargeted region of the scan must survive — blanking the whole image would " +
            "satisfy the assertion above");
    }

    [Fact]
    public void SearchableScan_LosesBothTheRasterInkAndTheInvisibleTextLayer()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed");

        // The shape OCR produces: the word exists TWICE. Removing one carrier and
        // not the other is a leak in either direction.
        var pdf = PdfDocument.Open(ScannedPagePdf(withInvisibleTextLayer: true));
        var page = pdf.GetPage(1);

        page.Text.Should().Contain("SECRET", "fixture sanity — the invisible text layer is present");

        page.RedactArea(SecretBlock, RedactionOptions.Default with { DrawBox = false });
        var path = SaveTemp(pdf);

        // Carrier 1: the invisible text layer.
        page.Text.Should().NotContain("SECRET",
            "the invisible text layer (Tr 3) is selectable, searchable and extractable even though " +
            "it renders nothing. Removing only the pixels would leave the word fully recoverable.");

        if (MutoolReferenceRenderer.IsAvailable)
        {
            var independent = MutoolTextExtractor.ExtractPage(path, 1);
            independent?.Should().NotContain("SECRET",
                "an independent extractor must not recover the invisible text layer either");
        }

        // Carrier 2: the raster ink.
        using var after = GhostscriptReferenceRenderer.RenderPage(path, 1, dpi: 150);
        after.Should().NotBeNull();

        InkFractionIn(after!, SecretBlock, page.Height).Should().BeLessThan(0.01,
            "removing only the invisible text layer leaves the word legible in the scan — the " +
            "redaction would be invisible to a text search and obvious to a human eye");

        InkFractionIn(after!, KeepBlock, page.Height).Should().BeGreaterThan(0.5,
            "untargeted scanned content must survive");
    }

    /// <summary>Fraction of non-white pixels inside a content-space rect.</summary>
    private static double InkFractionIn(SKBitmap bmp, PdfRectangle box, double pageHeight)
    {
        const double scale = 150.0 / 72.0;
        int x0 = Math.Max(0, (int)(box.Left * scale));
        int x1 = Math.Min(bmp.Width - 1, (int)(box.Right * scale));
        int y0 = Math.Max(0, (int)((pageHeight - box.Top) * scale));
        int y1 = Math.Min(bmp.Height - 1, (int)((pageHeight - box.Bottom) * scale));
        if (x1 <= x0 || y1 <= y0) return 0;

        int ink = 0, total = 0;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var p = bmp.GetPixel(x, y);
            total++;
            if (p.Red < 200 || p.Green < 200 || p.Blue < 200) ink++;
        }
        return total == 0 ? 0 : (double)ink / total;
    }

    private string SaveTemp(PdfDocument pdf)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-scan-{Guid.NewGuid():N}.pdf");
        pdf.Save(path);
        _temp.Add(path);
        return path;
    }

    /// <summary>
    /// A page whose content is a raster "scan": two solid black blocks drawn from
    /// image XObjects, standing in for burned-in scanned words. Optionally overlaid
    /// with an invisible (Tr 3) text layer, which is exactly what an OCR pass adds
    /// to make a scan searchable.
    /// </summary>
    private static byte[] ScannedPagePdf(bool withInvisibleTextLayer)
    {
        // 2x1 pixel all-black RGB images, scaled up by the cm matrix. Small enough
        // to inline, big enough to be unambiguous ink.
        const string blackPixels = "\u0000\u0000\u0000\u0000\u0000\u0000";

        var content = new StringBuilder();
        // Secret block
        content.Append($"q {SecretBlock.Width} 0 0 {SecretBlock.Height} {SecretBlock.Left} {SecretBlock.Bottom} cm /ImSecret Do Q ");
        // Keep block
        content.Append($"q {KeepBlock.Width} 0 0 {KeepBlock.Height} {KeepBlock.Left} {KeepBlock.Bottom} cm /ImKeep Do Q ");

        if (withInvisibleTextLayer)
        {
            // Tr 3 = invisible render mode. Renders nothing; fully extractable.
            content.Append($"BT 3 Tr /F1 24 Tf {SecretBlock.Left} {SecretBlock.Bottom + 10} Td (SECRET) Tj ET ");
            content.Append($"BT 3 Tr /F1 24 Tf {KeepBlock.Left} {KeepBlock.Bottom + 10} Td (KEEPME) Tj ET");
        }

        var cs = content.ToString();

        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string s) { offsets.Add(sb.Length); sb.Append(s); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << " +
            "/XObject << /ImSecret 5 0 R /ImKeep 6 0 R >> /Font << /F1 7 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {cs.Length} >>\nstream\n{cs}\nendstream\nendobj\n");
        Obj($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceRGB " +
            $"/BitsPerComponent 8 /Length {blackPixels.Length} >>\nstream\n{blackPixels}\nendstream\nendobj\n");
        Obj($"6 0 obj\n<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceRGB " +
            $"/BitsPerComponent 8 /Length {blackPixels.Length} >>\nstream\n{blackPixels}\nendstream\nendobj\n");
        Obj("7 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    public void Dispose()
    {
        foreach (var p in _temp)
        {
            try { File.Delete(p); } catch { /* best effort */ }
        }
    }
}
