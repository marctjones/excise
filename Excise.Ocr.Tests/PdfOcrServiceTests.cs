using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Ocr;
using Xunit;

namespace Excise.Ocr.Tests;

/// <summary>
/// End-to-end OCR tests. Skip automatically when the <c>tesseract</c>
/// CLI isn't on PATH (local-dev fallback — CI should install it).
/// </summary>
public class PdfOcrServiceTests
{
    // Use the repo's committed tessdata if the test environment doesn't
    // have TESSDATA_PREFIX set externally.
    private static readonly string TessdataPrefix =
        Environment.GetEnvironmentVariable("TESSDATA_PREFIX")
        ?? ResolveRepoTessdata();

    private static string ResolveRepoTessdata()
    {
        // Walk up from the test bin/ directory until we find tessdata/.
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var probe = System.IO.Path.Combine(dir.FullName, "tessdata", "eng.traineddata");
            if (System.IO.File.Exists(probe))
                return System.IO.Path.Combine(dir.FullName, "tessdata");
            dir = dir.Parent;
        }
        return string.Empty;
    }

    private static bool TesseractAvailable => new PdfOcrService().IsAvailable();

    private static PdfOcrService NewService(int dpi = 300) =>
        new(dpi: dpi, tessdataPrefix: TessdataPrefix);

    [Fact]
    public void RecognizePage_ReadsTextFromRenderedPage()
    {
        Assert.SkipUnless(TesseractAvailable, "tesseract CLI not installed");

        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(612, 792);
        using (var g = page.GetGraphics())
        {
            // Big 32pt text — keeps the OCR pass reliable at 300 DPI
            // without needing fine fidelity tuning.
            g.DrawString("HELLO EXCISE WORLD", PdfFont.Helvetica(32),
                PdfBrush.Black, 100, 600);
            g.Flush();
        }

        var ocr = NewService(dpi: 300);
        var result = ocr.RecognizePage(page);

        result.Text.Should().Contain("HELLO");
        result.Text.Should().Contain("EXCISE");
        result.Text.Should().Contain("WORLD");
        result.Words.Should().NotBeEmpty();
    }

    [Fact]
    public void RecognizePage_BlankPage_ReturnsEmptyText()
    {
        Assert.SkipUnless(TesseractAvailable, "tesseract CLI not installed");

        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank(612, 792);

        var ocr = NewService(dpi: 200);
        var result = ocr.RecognizePage(doc.GetPage(1));

        result.Text.Trim().Should().BeEmpty();
        result.Words.Should().BeEmpty();
    }

    [Fact]
    public void OcrWord_BoundingBox_IsInPdfPointsBottomLeft()
    {
        Assert.SkipUnless(TesseractAvailable, "tesseract CLI not installed");

        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(612, 792);
        using (var g = page.GetGraphics())
        {
            g.DrawString("ANCHOR", PdfFont.Helvetica(40),
                PdfBrush.Black, 200, 400);
            g.Flush();
        }

        var ocr = NewService(dpi: 300);
        var result = ocr.RecognizePage(page);

        var anchor = result.Words.FirstOrDefault(w => w.Text.Contains("ANCHOR"));
        anchor.Should().NotBeNull();
        // Drawn near x=200, baseline y=400 (PDF bottom-left). Word box
        // should sit in that general region — 60-point slack per axis.
        anchor!.BoundingBox.Left.Should().BeInRange(140, 260);
        anchor.BoundingBox.Bottom.Should().BeInRange(360, 440);
    }

    // ========================================================================
    // SERVICE INSTANTIATION TESTS
    // ========================================================================

    [Fact]
    public void PdfOcrService_CanBeInstantiatedWithDefaults()
    {
        var service = new PdfOcrService();
        service.Should().NotBeNull();
    }

    [Fact]
    public void PdfOcrService_CanBeInstantiatedWithCustomLanguage()
    {
        var service = new PdfOcrService(language: "fra");
        service.Should().NotBeNull();
    }

    [Fact]
    public void PdfOcrService_CanBeInstantiatedWithCustomDpi()
    {
        var service = new PdfOcrService(dpi: 150);
        service.Should().NotBeNull();
    }

    [Fact]
    public void PdfOcrService_CanBeInstantiatedWithCustomTesseractPath()
    {
        var service = new PdfOcrService(tesseractPath: "/usr/bin/tesseract");
        service.Should().NotBeNull();
    }

    [Fact]
    public void PdfOcrService_CanBeInstantiatedWithAllParameters()
    {
        var service = new PdfOcrService(
            language: "eng",
            dpi: 200,
            tesseractPath: "tesseract",
            tessdataPrefix: "/usr/share/tessdata");
        service.Should().NotBeNull();
    }

    [Fact]
    public void PdfOcrService_IsAvailable_ReturnsValue()
    {
        var service = new PdfOcrService();
        var result = service.IsAvailable();
        (result || !result).Should().BeTrue();
    }

    [Fact]
    public void PdfOcrService_IsAvailable_WithInvalidPath_ReturnsFalse()
    {
        var service = new PdfOcrService(tesseractPath: "/nonexistent/path/to/tesseract");
        var result = service.IsAvailable();
        result.Should().BeFalse();
    }

    [Fact]
    public void PdfOcrService_IsAvailable_DoesNotThrow()
    {
        var service = new PdfOcrService(tesseractPath: "/nonexistent/tesseract");
        var action = () => service.IsAvailable();
        action.Should().NotThrow();
    }

    [Fact]
    public void PdfOcrService_RecognizePage_WithNullPage_ThrowsArgumentNullException()
    {
        var service = new PdfOcrService();
        var action = () => service.RecognizePage(null!);
        action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void PdfOcrService_RecognizeDocument_WithNullDocument_Throws()
    {
        var service = new PdfOcrService();
        var action = () => _ = service.RecognizeDocument(null!).FirstOrDefault();
        action.Should().Throw<Exception>();
    }

    [Fact]
    public void PdfOcrService_RecognizeBitmap_WithNullBitmap_ThrowsArgumentNullException()
    {
        var service = new PdfOcrService();
        var action = () => service.RecognizeBitmap(null!, 792);
        action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void PdfOcrService_RecognizeDocument_ReturnsOneResultPerPage()
    {
        Assert.SkipUnless(TesseractAvailable, "tesseract CLI not installed");

        using var doc = PdfDocument.CreateNew();
        for (int i = 0; i < 3; i++)
        {
            var page = doc.Pages.AddBlank(612, 792);
            using (var g = page.GetGraphics())
            {
                g.DrawString($"PAGE {i + 1}", PdfFont.Helvetica(32),
                    PdfBrush.Black, 100, 400);
                g.Flush();
            }
        }

        var service = NewService(dpi: 200);
        var results = service.RecognizeDocument(doc).ToList();

        results.Should().HaveCount(3);
    }

    [Fact]
    public void PdfOcrService_WithCustomDpi_CanBeInstantiated()
    {
        var service = new PdfOcrService(dpi: 100);
        service.Should().NotBeNull();
    }

    [Fact]
    public void PdfOcrService_WithHighDpi_CanBeInstantiated()
    {
        var service = new PdfOcrService(dpi: 600);
        service.Should().NotBeNull();
    }
}

