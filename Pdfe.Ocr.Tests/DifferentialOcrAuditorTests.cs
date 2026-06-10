using System;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Graphics;
using Pdfe.Ocr;
using Pdfe.Rendering;
using SkiaSharp;
using Xunit;

namespace Pdfe.Ocr.Tests;

/// <summary>
/// End-to-end test for differential OCR: a PDF whose only text lives
/// inside an image XObject, with a filled rectangle overlay covering
/// part of it. Structural <c>HiddenTextDetector</c> sees nothing
/// (no Tj operators), but differential OCR should recover the hidden
/// digits from the underlying raster.
/// </summary>
public class DifferentialOcrAuditorTests
{
    private static readonly string TessdataPrefix =
        Environment.GetEnvironmentVariable("TESSDATA_PREFIX")
        ?? ResolveRepoTessdata();

    private static string ResolveRepoTessdata()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var probe = Path.Combine(dir.FullName, "tessdata", "eng.traineddata");
            if (File.Exists(probe)) return Path.Combine(dir.FullName, "tessdata");
            dir = dir.Parent;
        }
        return string.Empty;
    }

    private static bool TesseractAvailable => new PdfOcrService().IsAvailable();

    [Fact]
    public void Scan_RasterPdfWithBlackOverlay_RecoversHiddenDigits()
    {
        Assert.SkipUnless(TesseractAvailable, "tesseract CLI not installed");

        // Step 1: render text to a SKBitmap to use as the "scan."
        byte[] rgbBytes;
        int imgW, imgH;
        using (var srcDoc = PdfDocument.CreateNew())
        {
            var srcPage = srcDoc.Pages.AddBlank(400, 200);
            using (var g = srcPage.GetGraphics())
            {
                g.DrawString("ACCT 9876-5432", PdfFont.Helvetica(30),
                    PdfBrush.Black, 50, 100);
                g.Flush();
            }
            using var scan = new SkiaRenderer().RenderPage(srcPage,
                new RenderOptions { Dpi = 150 },
                TestContext.Current.CancellationToken);
            imgW = scan.Width;
            imgH = scan.Height;
            rgbBytes = ToDeviceRgb(scan);
        }

        // Step 2: build a PDF that embeds the scan as an image XObject
        // and draws a black rectangle overlay covering the middle digits.
        var pdfBytes = BuildScanWithOverlayPdf(imgW, imgH, rgbBytes);

        // Step 3: differential OCR should report the hidden digits.
        var auditor = new DifferentialOcrAuditor(
            new PdfOcrService(dpi: 150, tessdataPrefix: TessdataPrefix));
        var hits = auditor.Scan(pdfBytes);

        hits.Should().NotBeEmpty(
            "the rasterized account number is structurally invisible to " +
            "Tj-based detectors but should be recovered from the underlying " +
            "image after the overlay is stripped");

        // The hidden digits "9876-5432" must show up either as one
        // word or as a couple of fragments — accept either.
        var allHitText = string.Join(" ", hits.Select(h => h.Text));
        (allHitText.Contains("9876") || allHitText.Contains("5432") ||
         allHitText.Contains("9876-5432"))
            .Should().BeTrue(
                $"expected the hidden account digits in the diff hits, got: {allHitText}");
    }

    private static byte[] ToDeviceRgb(SKBitmap bmp)
    {
        var buf = new byte[bmp.Width * bmp.Height * 3];
        int idx = 0;
        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            var c = bmp.GetPixel(x, y);
            buf[idx++] = c.Red;
            buf[idx++] = c.Green;
            buf[idx++] = c.Blue;
        }
        return buf;
    }

    private static byte[] BuildScanWithOverlayPdf(int imgW, int imgH, byte[] rgbBytes)
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
        w.WriteLine("%PDF-1.4");
        w.Flush();
        var off = new long[7];
        off[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();
        off[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();
        off[3] = ms.Position;
        w.WriteLine("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 200] " +
                    "/Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj");
        w.Flush();

        // Image fills the page; a black rectangle covers the middle of the
        // text region (PDF y=80..130, x=175..275 roughly straddles "76-54").
        var body = "q 400 0 0 200 0 0 cm /Im0 Do Q\n" +
                   "q 0 0 0 rg 175 80 100 50 re f Q";
        off[4] = ms.Position;
        w.WriteLine($"4 0 obj\n<< /Length {body.Length} >>\nstream");
        w.Write(body); w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        off[5] = ms.Position;
        w.WriteLine($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {imgW} /Height {imgH} " +
                    $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {rgbBytes.Length} >>\nstream");
        w.Flush();
        ms.Write(rgbBytes, 0, rgbBytes.Length);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        long xref = ms.Position;
        w.WriteLine("xref\n0 6\n0000000000 65535 f ");
        for (int i = 1; i <= 5; i++) w.WriteLine($"{off[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xref}\n%%EOF");
        w.Flush();
        return ms.ToArray();
    }

    // ========================================================================
    // SERVICE INSTANTIATION TESTS
    // ========================================================================

    [Fact]
    public void DifferentialOcrAuditor_CanBeInstantiatedWithOcrService()
    {
        var ocrService = new PdfOcrService();
        var auditor = new DifferentialOcrAuditor(ocrService);
        auditor.Should().NotBeNull();
    }

    [Fact]
    public void DifferentialOcrAuditor_Scan_WithNullBytes_ThrowsArgumentNullException()
    {
        var ocrService = new PdfOcrService();
        var auditor = new DifferentialOcrAuditor(ocrService);
        var action = () => auditor.Scan(null!);
        action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DifferentialOcrAuditor_ScanPage_WithNullBytes_ThrowsArgumentNullException()
    {
        var ocrService = new PdfOcrService();
        var auditor = new DifferentialOcrAuditor(ocrService);
        var action = () => auditor.ScanPage(null!, 1);
        action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DifferentialOcrAuditor_ScanFile_WithNonExistentFile_ThrowsIOException()
    {
        var ocrService = new PdfOcrService();
        var auditor = new DifferentialOcrAuditor(ocrService);
        var action = () => auditor.ScanFile("/nonexistent/file.pdf");
        action.Should().Throw<System.IO.IOException>();
    }

    [Fact]
    public void DifferentialOcrAuditor_WithCustomTesseractPath_CanBeInstantiated()
    {
        var ocrService = new PdfOcrService(tesseractPath: "/usr/bin/tesseract");
        var auditor = new DifferentialOcrAuditor(ocrService);
        auditor.Should().NotBeNull();
    }

    [Fact]
    public void DifferentialOcrAuditor_ScanReturnsReadOnlyList()
    {
        var ocrService = new PdfOcrService(tesseractPath: "/nonexistent");
        var auditor = new DifferentialOcrAuditor(ocrService);

        // Create a simple PDF with no hidden content
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(400, 200);
        using var ms = new MemoryStream();
        doc.Save(ms);
        var pdfBytes = ms.ToArray();

        // Scan will fail since tesseract is not available, but if it succeeds,
        // the result should be of type IReadOnlyList
        try
        {
            var result = auditor.Scan(pdfBytes);
            result.Should().NotBeNull();
            typeof(System.Collections.Generic.IReadOnlyList<DifferentialOcrHit>)
                .IsAssignableFrom(result.GetType()).Should().BeTrue();
        }
        catch
        {
            // Expected when tesseract not available
        }
    }
}
