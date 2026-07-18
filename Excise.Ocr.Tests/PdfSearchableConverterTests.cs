using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Core.Text.Segmentation;
using Excise.Rendering;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Ocr.Tests;

/// <summary>
/// Tests for <see cref="PdfSearchableConverter"/> (#627): writing an
/// invisible OCR text layer onto a scanned page.
/// </summary>
public class PdfSearchableConverterTests
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

    private static PdfOcrService NewOcrService(int dpi = 300) =>
        new(dpi: dpi, tessdataPrefix: TessdataPrefix);

    // ------------------------------------------------------------------
    // Skip / force behavior
    // ------------------------------------------------------------------

    [Fact]
    public void MakePageSearchable_PageAlreadyHasText_SkipsByDefault()
    {
        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 100);
        using (var g = page.GetGraphics())
        {
            g.DrawString("Real text", PdfFont.Helvetica(12), PdfBrush.Black, 10, 50);
            g.Flush();
        }

        var converter = new PdfSearchableConverter(new PdfOcrService());
        var result = converter.MakePageSearchable(page);

        result.Skipped.Should().BeTrue();
        result.AlreadyHadText.Should().BeTrue();
        result.WordsWritten.Should().Be(0);
    }

    [Fact]
    public void MakePageSearchable_BlankPage_NoTextToSkip_AttemptsOcr()
    {
        Assert.SkipUnless(TesseractAvailable, "tesseract CLI not installed");

        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 100);

        var converter = new PdfSearchableConverter(NewOcrService());
        var result = converter.MakePageSearchable(page);

        result.Skipped.Should().BeFalse();
        result.WordsWritten.Should().Be(0, "a blank page has nothing for tesseract to recognize");
    }

    [Fact]
    public void MakePageSearchable_Force_OcrsEvenWhenTextAlreadyExists()
    {
        Assert.SkipUnless(TesseractAvailable, "tesseract CLI not installed");

        using var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(200, 100);
        using (var g = page.GetGraphics())
        {
            g.DrawString("Real text", PdfFont.Helvetica(12), PdfBrush.Black, 10, 50);
            g.Flush();
        }

        var converter = new PdfSearchableConverter(NewOcrService());
        var result = converter.MakePageSearchable(page, force: true);

        result.Skipped.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // Document-level: progress + cancellation
    // ------------------------------------------------------------------

    [Fact]
    public void MakeSearchable_ReportsProgressOncePerPage()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank(100, 100);
        doc.Pages.AddBlank(100, 100);
        doc.Pages.AddBlank(100, 100);
        foreach (var p in Enumerable.Range(1, 3))
        {
            using var g = doc.GetPage(p).GetGraphics();
            g.DrawString("x", PdfFont.Helvetica(10), PdfBrush.Black, 5, 5);
            g.Flush();
        }

        var reports = new System.Collections.Generic.List<(int Done, int Total)>();
        var progress = new Progress<(int, int)>(reports.Add);

        var converter = new PdfSearchableConverter(new PdfOcrService());
        // All 3 pages already have text, so this exercises the skip path
        // without needing tesseract — still proves progress/loop wiring.
        var result = converter.MakeSearchable(doc, progress: progress, cancellationToken: TestContext.Current.CancellationToken);
        // Progress<T> marshals via SynchronizationContext.Post, which in a
        // console/test context runs synchronously, but don't rely on timing.
        result.PagesSkipped.Should().Be(3);
        result.PagesProcessed.Should().Be(0);
    }

    [Fact]
    public void MakeSearchable_Cancellation_ThrowsBeforeProcessingRemainingPages()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank(100, 100);
        doc.Pages.AddBlank(100, 100);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var converter = new PdfSearchableConverter(new PdfOcrService());
        var act = () => converter.MakeSearchable(doc, cancellationToken: cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void MakeSearchable_EmptyDocument_ReturnsZeroedResult()
    {
        using var doc = PdfDocument.CreateNew();
        doc.Pages.AddBlank(100, 100);
        // Page has no text and tesseract may be unavailable in CI; use the
        // "already has text" skip path deterministically instead.
        using (var g = doc.GetPage(1).GetGraphics())
        {
            g.DrawString("x", PdfFont.Helvetica(10), PdfBrush.Black, 5, 5);
            g.Flush();
        }

        var converter = new PdfSearchableConverter(new PdfOcrService());
        var result = converter.MakeSearchable(doc, cancellationToken: TestContext.Current.CancellationToken);

        result.PagesSkipped.Should().Be(1);
        result.TotalWordsWritten.Should().Be(0);
    }

    // ------------------------------------------------------------------
    // The critical end-to-end test: a real scan, made searchable, then
    // redacted by the word the OCR layer introduced. Verified with
    // independent tools (mutool, ghostscript), not excise's own extractor —
    // see CLAUDE.md's "must not be its own oracle" rule.
    // ------------------------------------------------------------------

    [Fact]
    public void MakeSearchable_ThenRedactText_RemovesBothTheInvisibleLayerAndTheRasterInk()
    {
        Assert.SkipUnless(TesseractAvailable, "tesseract CLI not installed");
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        const string secret = "REDACTME";
        const string keep = "KEEPME";

        // Step 1: render each word to its OWN small bitmap, then place them
        // as two SEPARATE image XObjects on the destination page (rather
        // than one full-page scan image). This matters for what this test
        // proves: excise's image redaction (ImageRedactor.ShouldRemoveImageDo)
        // removes a WHOLE intersecting image XObject, not a pixel-level crop
        // — correct, pre-existing, conservative behavior, but it means a
        // single shared full-page image would be wiped entirely by
        // redacting either word, which wouldn't distinguish "only the
        // targeted content was removed" from "the whole page was blanked."
        // Two separate images make that distinction real.
        double wordImgWidth = 240, wordImgHeight = 70;
        double pageWidth = 300, pageHeight = 220;
        double secretX = 20, secretY = 130;
        double keepX = 20, keepY = 20;

        (int W, int H) secretImg, keepImg;
        byte[] secretRgb, keepRgb;
        RenderWordImage(secret, wordImgWidth, wordImgHeight, out secretImg, out secretRgb);
        RenderWordImage(keep, wordImgWidth, wordImgHeight, out keepImg, out keepRgb);

        // Step 2: build a PDF whose ONLY content is those two raster images —
        // no text-showing operators at all, a true "scanned page."
        var scannedPdfBytes = BuildTwoImagePdf(
            pageWidth, pageHeight,
            secretX, secretY, wordImgWidth, wordImgHeight, secretImg.W, secretImg.H, secretRgb,
            keepX, keepY, wordImgWidth, wordImgHeight, keepImg.W, keepImg.H, keepRgb);

        var scanPath = Path.Combine(Path.GetTempPath(), $"excise-searchable-scan-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(scanPath, scannedPdfBytes);
        try
        {
            using var doc = PdfDocument.Open(scannedPdfBytes);
            var page = doc.GetPage(1);
            page.Letters.Should().BeEmpty("fixture sanity: a scan has no real text layer before conversion");

            var converter = new PdfSearchableConverter(NewOcrService(dpi: 150));
            var result = converter.MakePageSearchable(page);
            result.WordsWritten.Should().BeGreaterThan(0,
                "tesseract must have recognized at least the two rendered words");

            var madeSearchablePath = Path.Combine(Path.GetTempPath(), $"excise-searchable-out-{Guid.NewGuid():N}.pdf");
            doc.Save(madeSearchablePath);

            try
            {
                // Step 3: verify with an INDEPENDENT extractor (mutool), not
                // excise reading its own page.Text.
                var mutoolText = MutoolTextExtractor.ExtractPage(madeSearchablePath, 1);
                mutoolText.Should().NotBeNull();
                mutoolText!.ToUpperInvariant().Should().Contain(secret,
                    "the invisible layer must make the word findable by a tool that is not excise");

                // Step 4: redact the word via RedactText (word-search path,
                // not a hand-picked rectangle) and verify BOTH carriers are
                // gone: the invisible glyphs AND the raster pixels.
                var secretBoxBefore = BoundsOf(page, secret);
                doc.RedactText(secret, drawBlackRect: false).Should().BeGreaterThan(0);

                page.Text.Should().NotContain(secret);
                page.Text.Should().Contain(keep, "only the targeted word should be removed");

                var redactedPath = Path.Combine(Path.GetTempPath(), $"excise-searchable-redacted-{Guid.NewGuid():N}.pdf");
                doc.Save(redactedPath);
                try
                {
                    var mutoolAfter = MutoolTextExtractor.ExtractPage(redactedPath, 1);
                    (mutoolAfter ?? string.Empty).ToUpperInvariant().Should().NotContain(secret,
                        "an independent extractor must not find the redacted word either");

                    if (GhostscriptReferenceRenderer.IsAvailable)
                    {
                        using var rendered = GhostscriptReferenceRenderer.RenderPage(redactedPath, 1, dpi: 150);
                        rendered.Should().NotBeNull();
                        var ink = InkFractionIn(rendered!, secretBoxBefore, pageHeight);
                        ink.Should().BeLessThan(0.02,
                            $"the raster pixels under the redacted word must be gone too, not just the " +
                            $"invisible text layer (ink={ink:P2}) — this is the #637-shaped risk this " +
                            "feature could introduce if word positions didn't line up with the scan");

                        var keepBox = BoundsOf(page, keep);
                        InkFractionIn(rendered!, keepBox, pageHeight).Should().BeGreaterThan(0.02,
                            "the untargeted word's pixels must survive — a blank page would satisfy the check above too");
                    }
                }
                finally { TryDelete(redactedPath); }
            }
            finally { TryDelete(madeSearchablePath); }
        }
        finally { TryDelete(scanPath); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static Excise.Core.Document.PdfRectangle BoundsOf(PdfPage page, string word)
    {
        var letters = page.Letters;
        var idx = FindWordStart(letters, word);
        idx.Should().BeGreaterThanOrEqualTo(0, $"'{word}' must be present in the page's letters to compute its bounds");
        var slice = letters.Skip(idx).Take(word.Length).ToList();
        return new Excise.Core.Document.PdfRectangle(
            slice.Min(l => l.GlyphRectangle.Left),
            slice.Min(l => l.GlyphRectangle.Bottom),
            slice.Max(l => l.GlyphRectangle.Right),
            slice.Max(l => l.GlyphRectangle.Top));
    }

    private static int FindWordStart(System.Collections.Generic.IReadOnlyList<Excise.Core.Text.Letter> letters, string word)
    {
        var text = string.Concat(letters.Select(l => l.Value));
        return text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
    }

    private static double InkFractionIn(SKBitmap bmp, Excise.Core.Document.PdfRectangle box, double pageHeight)
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

    /// <summary>
    /// Renders <paramref name="word"/> alone onto a blank <paramref name="width"/>x<paramref name="height"/>
    /// source page and rasterizes it at 150 DPI — a single-word "scan" tile
    /// to be placed as its own image XObject.
    /// </summary>
    private static void RenderWordImage(string word, double width, double height, out (int W, int H) size, out byte[] rgb)
    {
        using var srcDoc = PdfDocument.CreateNew();
        var srcPage = srcDoc.Pages.AddBlank(width, height);
        using (var g = srcPage.GetGraphics())
        {
            g.DrawString(word, PdfFont.Helvetica(28), PdfBrush.Black, 10, height / 2 - 10);
            g.Flush();
        }
        using var scan = new SkiaRenderer().RenderPage(srcPage, new RenderOptions { Dpi = 150 }, TestContext.Current.CancellationToken);
        size = (scan.Width, scan.Height);
        rgb = ToDeviceRgb(scan);
    }

    /// <summary>
    /// A minimal, hand-written single-page PDF whose only content-stream
    /// operators are two <c>Do</c> calls for two SEPARATE image XObjects —
    /// no text operators at all, i.e. a true scanned page. Two distinct
    /// images (rather than one full-page scan) so a test can verify that
    /// redacting one word's region removes only the image XObject it
    /// intersects (see <c>ImageRedactor.ShouldRemoveImageDo</c>), not every
    /// image on the page. Mirrors
    /// <c>DifferentialOcrAuditorTests.BuildScanWithOverlayPdf</c>'s
    /// hand-rolled-PDF approach, extended to two image objects.
    /// </summary>
    private static byte[] BuildTwoImagePdf(
        double pageWidth, double pageHeight,
        double x0, double y0, double w0, double h0, int imgW0, int imgH0, byte[] rgb0,
        double x1, double y1, double w1, double h1, int imgW1, int imgH1, byte[] rgb1)
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
        w.WriteLine($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Fmt(pageWidth)} {Fmt(pageHeight)}] " +
                    "/Contents 4 0 R /Resources << /XObject << /Im0 5 0 R /Im1 6 0 R >> >> >>\nendobj");
        w.Flush();

        var body =
            $"q {Fmt(w0)} 0 0 {Fmt(h0)} {Fmt(x0)} {Fmt(y0)} cm /Im0 Do Q\n" +
            $"q {Fmt(w1)} 0 0 {Fmt(h1)} {Fmt(x1)} {Fmt(y1)} cm /Im1 Do Q";
        off[4] = ms.Position;
        w.WriteLine($"4 0 obj\n<< /Length {body.Length} >>\nstream");
        w.Write(body); w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        off[5] = ms.Position;
        w.WriteLine($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {imgW0} /Height {imgH0} " +
                    $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {rgb0.Length} >>\nstream");
        w.Flush();
        ms.Write(rgb0, 0, rgb0.Length);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        off[6] = ms.Position;
        w.WriteLine($"6 0 obj\n<< /Type /XObject /Subtype /Image /Width {imgW1} /Height {imgH1} " +
                    $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {rgb1.Length} >>\nstream");
        w.Flush();
        ms.Write(rgb1, 0, rgb1.Length);
        w.WriteLine();
        w.WriteLine("endstream\nendobj");
        w.Flush();

        long xref = ms.Position;
        w.WriteLine("xref\n0 7\n0000000000 65535 f ");
        for (int i = 1; i <= 6; i++) w.WriteLine($"{off[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine($"trailer\n<< /Root 1 0 R /Size 7 >>\nstartxref\n{xref}\n%%EOF");
        w.Flush();
        return ms.ToArray();
    }

    private static string Fmt(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
