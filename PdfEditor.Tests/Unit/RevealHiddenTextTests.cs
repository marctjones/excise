using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Graphics;
using Pdfe.Rendering;
using PdfEditor.Models;
using Pdfe.Avalonia.Controls;
using PdfEditor.Tests.Utilities;
using PdfEditor.ViewModels;
using SkiaSharp;
using Xunit;

namespace PdfEditor.Tests.Unit;

/// <summary>
/// Integration test for the "Reveal Hidden Text" audit feature:
/// given a PDF with text occluded by a black rectangle (the classic
/// bad-redaction pattern), verify the ViewModel surfaces it through
/// <see cref="MainWindowViewModel.HiddenTextHighlights"/> when the
/// <see cref="MainWindowViewModel.RevealHiddenText"/> toggle is on.
/// </summary>
[Collection("AvaloniaTests")]
public class RevealHiddenTextTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) try { File.Delete(f); } catch { }
    }

    private static bool TesseractAvailable
        => new Pdfe.Ocr.PdfOcrService().IsAvailable();

    [FixedAvaloniaFact]
    public async Task RevealToggle_FlushesHighlightsForHiddenText()
    {
        // Arrange: build a PDF with "SECRET INFO 12345" covered by a
        // black rectangle drawn on top (classic bad redaction).
        var path = Path.Combine(Path.GetTempPath(), $"reveal-test-{Guid.NewGuid():N}.pdf");
        _tempFiles.Add(path);
        File.WriteAllBytes(path, BuildBadRedactionPdf("SECRET INFO 12345"));

        var vm = new MainWindowViewModel();

        // Act: simulate the user's workflow — load the PDF, flip the toggle.
        await vm.LoadDocumentCommand(path);
        vm.CurrentPageIndex = 0;
        vm.RevealHiddenText.Should().BeFalse("default off");
        vm.HiddenTextHighlights.Should().BeEmpty("nothing populated until revealed");

        vm.RevealHiddenText = true;
        await WaitForHiddenTextScanAsync(vm, () => vm.HiddenTextHighlights.Count == 1);

        // Assert: the hidden text surfaces, with a non-zero on-screen
        // bounding box so the overlay can actually draw it.
        vm.HiddenTextHighlights.Should().HaveCount(1);
        var h = vm.HiddenTextHighlights[0];
        h.Text.Should().Be("SECRET INFO 12345");
        h.HiddenBy.Should().Contain("filled rectangle");
        h.Bounds.Space.Should().Be(PdfCoordinateSpace.ContentPoints,
            "hidden-text detection reports PDF content bounds; the viewer converts to screen coordinates");
        h.Bounds.UnitsPerPoint.Should().Be(1);
        h.Bounds.Width.Should().BeGreaterThan(0);
        h.Bounds.Height.Should().BeGreaterThan(0);

        // Severity classifier — structural, not OCR.
        h.Source.Should().Be(HiddenTextSource.Structural);

        // And: flipping off clears the overlay.
        vm.RevealHiddenText = false;
        vm.HiddenTextHighlights.Should().BeEmpty();
    }

    [FixedAvaloniaFact]
    public async Task RevealRasterizedHidden_FindsTextHiddenByOverlayInsideImage()
    {
        Assert.SkipUnless(TesseractAvailable, "tesseract CLI not installed");

        var path = Path.Combine(Path.GetTempPath(), $"raster-reveal-{Guid.NewGuid():N}.pdf");
        _tempFiles.Add(path);
        File.WriteAllBytes(path, BuildRasterWithOverlayPdf());

        var vm = new MainWindowViewModel();
        await vm.LoadDocumentCommand(path);
        vm.CurrentPageIndex = 0;

        // Structural-only — should find nothing because the text only
        // exists inside the image.
        vm.RevealHiddenText = true;
        await WaitForHiddenTextScanAsync(vm, () => vm.HiddenTextHighlights.Count == 0);
        vm.HiddenTextHighlights.Should().BeEmpty(
            "no Tj operators on the page; only image XObjects + filled rectangle");

        // Differential-OCR — should recover the hidden digits.
        vm.RevealRasterizedHidden = true;
        await WaitForHiddenTextScanAsync(vm, () => vm.HiddenTextHighlights.Count > 0);

        vm.HiddenTextHighlights.Should().NotBeEmpty();
        var hits = string.Join(" ", vm.HiddenTextHighlights.Select(h => h.Text));
        (hits.Contains("9876") || hits.Contains("5432") || hits.Contains("9876-5432"))
            .Should().BeTrue($"expected hidden digits in OCR diff hits, got: {hits}");
        vm.HiddenTextHighlights.Should().Contain(h =>
            h.Source == HiddenTextSource.DifferentialOcr);

        // Toggle off — only the structural pass remains, which finds nothing.
        vm.RevealRasterizedHidden = false;
        vm.HiddenTextHighlights.Should().BeEmpty();
    }

    private static async Task WaitForHiddenTextScanAsync(
        MainWindowViewModel vm,
        Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (!vm.IsHiddenTextScanInProgress && predicate())
                return;
            await Task.Delay(25);
        }

        vm.IsHiddenTextScanInProgress.Should().BeFalse("hidden-text scan should complete");
        predicate().Should().BeTrue("hidden-text scan should publish expected results");
    }

    /// <summary>
    /// Build a PDF whose ONLY text is inside a rasterized image XObject,
    /// with a black rectangle overlay covering the middle of that text.
    /// Mirrors the synthetic from DifferentialOcrAuditorTests.
    /// </summary>
    private static byte[] BuildRasterWithOverlayPdf()
    {
        // Render the secret text onto a SKBitmap.
        byte[] rgb;
        int imgW, imgH;
        using (var src = PdfDocument.CreateNew())
        {
            var page = src.Pages.AddBlank(400, 200);
            using (var g = page.GetGraphics())
            {
                g.DrawString("ACCT 9876-5432", PdfFont.Helvetica(30),
                    PdfBrush.Black, 50, 100);
                g.Flush();
            }
            using var bmp = new SkiaRenderer().RenderPage(page,
                new RenderOptions { Dpi = 150 });
            imgW = bmp.Width;
            imgH = bmp.Height;
            rgb = new byte[imgW * imgH * 3];
            int idx = 0;
            for (int y = 0; y < imgH; y++)
            for (int x = 0; x < imgW; x++)
            {
                var c = bmp.GetPixel(x, y);
                rgb[idx++] = c.Red;
                rgb[idx++] = c.Green;
                rgb[idx++] = c.Blue;
            }
        }

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
        var body = "q 400 0 0 200 0 0 cm /Im0 Do Q\n" +
                   "q 0 0 0 rg 175 80 100 50 re f Q";
        off[4] = ms.Position;
        w.WriteLine($"4 0 obj\n<< /Length {body.Length} >>\nstream");
        w.Write(body); w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();
        off[5] = ms.Position;
        w.WriteLine($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {imgW} /Height {imgH} " +
                    $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {rgb.Length} >>\nstream");
        w.Flush();
        ms.Write(rgb, 0, rgb.Length);
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

    /// <summary>
    /// Minimal PDF with a line of text plus a black rectangle drawn
    /// on top covering the text bbox. Serves as a deterministic bad-
    /// redaction fixture for the reveal test.
    /// </summary>
    private static byte[] BuildBadRedactionPdf(string secretText)
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };

        w.WriteLine("%PDF-1.4");
        w.Flush();

        var offsets = new long[6];
        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        w.Flush();

        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        w.Flush();

        offsets[3] = ms.Position;
        w.WriteLine("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj");
        w.Flush();

        double textX = 100, textY = 700;
        double rectLeft = textX - 4;
        double rectBottom = textY - 4;
        double rectWidth = secretText.Length * 8;
        double rectHeight = 14;

        var body =
            $"BT /F1 14 Tf {textX} {textY} Td ({secretText}) Tj ET\n" +
            $"q 0 0 0 rg {rectLeft} {rectBottom} {rectWidth} {rectHeight} re f Q";

        offsets[4] = ms.Position;
        w.WriteLine($"4 0 obj\n<< /Length {body.Length} >>\nstream");
        w.Write(body);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica " +
                    "/Encoding /WinAnsiEncoding >>\nendobj");
        w.Flush();

        long xref = ms.Position;
        w.WriteLine("xref");
        w.WriteLine("0 6");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 6 >>\nstartxref\n{xref}\n%%EOF");
        w.Flush();

        return ms.ToArray();
    }
}
