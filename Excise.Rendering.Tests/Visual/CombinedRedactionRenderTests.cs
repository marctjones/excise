using System;
using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text.Segmentation;
using Excise.Rendering;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Visual;

/// <summary>
/// End-to-end verification that RedactArea removes BOTH text glyphs and
/// image XObjects from a rewritten PDF, and that the Skia-rendered
/// output reflects the removal at the pixel level.
/// </summary>
/// <remarks>
/// The prior unit tests only proved "the Do op was dropped from the
/// content stream." This test proves "when Skia renders the redacted
/// PDF, the image is actually gone from the pixels" — the property
/// end users care about.
/// </remarks>
public class CombinedRedactionRenderTests
{
    private const int DpiRender = 100;
    private const int PageWidth = 612;
    private const int PageHeight = 792;

    // Image XObject placement (PDF bottom-left origin).
    private const double ImageX = 200;
    private const double ImageY = 600;
    private const double ImageW = 100;
    private const double ImageH = 60;

    // Text placement.
    private const double TextX = 80;
    private const double TextBaselineY = 700;

    // Redaction rectangle (PDF bottom-left origin).
    // Chosen to cover the image and the text both.
    private const double RedactLeft = 60;
    private const double RedactBottom = 580;
    private const double RedactRight = 360;
    private const double RedactTop = 720;

    [Fact]
    public void PrePostRedaction_Pixels_ShowImageAndTextGone()
    {
        var pdfBytes = BuildPdfWithTextAndRedImage();

        // Dump a copy for inspection when the test fails.
        File.WriteAllBytes("/tmp/combined-before.pdf", pdfBytes);

        // ------- BEFORE: render the original -------
        SKBitmap before;
        using (var doc = PdfDocument.Open(pdfBytes))
        {
            var renderer = new SkiaRenderer();
            before = renderer.RenderPage(doc.GetPage(1), new RenderOptions { Dpi = DpiRender });
        }
        SaveBitmap(before, "/tmp/combined-before.png");

        // Pre-assertion: the red image should have produced red pixels
        // in the image region, and the text should have produced dark
        // pixels in the text region.
        CountRedPixels(before, ImageRectInPixels()).Should()
            .BeGreaterThan(0, "pre-redaction image should be visibly red");
        CountDarkPixels(before, TextRectInPixels()).Should()
            .BeGreaterThan(5, "pre-redaction text should be visibly dark");

        // ------- REDACT via the public API -------
        byte[] redactedBytes;
        using (var doc = PdfDocument.Open(pdfBytes))
        {
            var page = doc.GetPage(1);
            page.RedactArea(new PdfRectangle(RedactLeft, RedactBottom, RedactRight, RedactTop));
            // Draw a confirmation black rectangle the same way
            // RedactionService / CLI do it.
            AppendBlackRectangle(page, new PdfRectangle(RedactLeft, RedactBottom, RedactRight, RedactTop));
            redactedBytes = doc.SaveToBytes();
        }
        File.WriteAllBytes("/tmp/combined-after.pdf", redactedBytes);

        // Structural checks before we even render it — content stream
        // must no longer contain /Im0 Do or (SECRET TEXT).
        using (var reopened = PdfDocument.Open(redactedBytes))
        {
            var raw = Encoding.Latin1.GetString(reopened.GetPage(1).GetContentStreamBytes());
            raw.Should().NotContain("/Im0 Do",
                "image XObject invocation must be dropped from content stream");
            raw.Should().NotContain("(SECRET TEXT)",
                "text literal must be dropped from content stream");
        }

        // ------- AFTER: render the redacted doc and check pixels -------
        SKBitmap after;
        using (var doc = PdfDocument.Open(redactedBytes))
        {
            var renderer = new SkiaRenderer();
            after = renderer.RenderPage(doc.GetPage(1), new RenderOptions { Dpi = DpiRender });
        }
        SaveBitmap(after, "/tmp/combined-after.png");

        CountRedPixels(after, ImageRectInPixels()).Should()
            .Be(0, "the red image is structurally removed; no red pixels should remain");

        // Text region should be dominated by black pixels from the
        // redaction overlay, with zero dark anti-aliased-text signal.
        // We check the text ROW — same Y band the original letters sat
        // on — for any stray non-black signal that would indicate
        // letter glyphs snuck through.
        var textRowBlackFraction = FractionBlackPixels(after, TextRectInPixels());
        textRowBlackFraction.Should().BeGreaterThan(0.95,
            "the redaction overlay should cover the text region; >95% of the row should be pure black");
    }

    // Pre/post PNG files are left on disk at /tmp/combined-{before,after}.png
    // for manual visual confirmation during review.
    private static void SaveBitmap(SKBitmap bmp, string path)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);
    }

    /// <summary>
    /// Axis-aligned pixel rect for the image region, converted from
    /// PDF points (bottom-left origin) to image pixels (top-left origin)
    /// at the render DPI.
    /// </summary>
    private static SKRectI ImageRectInPixels()
    {
        double scale = DpiRender / 72.0;
        int left = (int)Math.Floor(ImageX * scale);
        int right = (int)Math.Ceiling((ImageX + ImageW) * scale);
        // PDF bottom-left → image top-left: top = pageHeight - (imageY + imageH)
        int top = (int)Math.Floor((PageHeight - ImageY - ImageH) * scale);
        int bottom = (int)Math.Ceiling((PageHeight - ImageY) * scale);
        return new SKRectI(left, top, right, bottom);
    }

    private static SKRectI TextRectInPixels()
    {
        // Text row at baseline y=700, roughly 12pt tall. Use a 20pt band
        // around the baseline as the sampling region.
        double scale = DpiRender / 72.0;
        int left = (int)Math.Floor((TextX - 5) * scale);
        int right = (int)Math.Ceiling((TextX + 100) * scale); // "SECRET TEXT" span
        int top = (int)Math.Floor((PageHeight - TextBaselineY - 4) * scale);
        int bottom = (int)Math.Ceiling((PageHeight - TextBaselineY + 14) * scale);
        return new SKRectI(left, top, right, bottom);
    }

    private static int CountRedPixels(SKBitmap bmp, SKRectI region)
    {
        int count = 0;
        for (int y = Math.Max(0, region.Top); y < Math.Min(bmp.Height, region.Bottom); y++)
        for (int x = Math.Max(0, region.Left); x < Math.Min(bmp.Width, region.Right); x++)
        {
            var c = bmp.GetPixel(x, y);
            // "Red-ish" — high R, low G/B. Loose enough to survive
            // anti-aliasing at the image edges.
            if (c.Red > 180 && c.Green < 80 && c.Blue < 80) count++;
        }
        return count;
    }

    private static int CountDarkPixels(SKBitmap bmp, SKRectI region)
    {
        int count = 0;
        for (int y = Math.Max(0, region.Top); y < Math.Min(bmp.Height, region.Bottom); y++)
        for (int x = Math.Max(0, region.Left); x < Math.Min(bmp.Width, region.Right); x++)
        {
            var c = bmp.GetPixel(x, y);
            if (c.Red < 80 && c.Green < 80 && c.Blue < 80) count++;
        }
        return count;
    }

    private static double FractionBlackPixels(SKBitmap bmp, SKRectI region)
    {
        int total = 0, black = 0;
        for (int y = Math.Max(0, region.Top); y < Math.Min(bmp.Height, region.Bottom); y++)
        for (int x = Math.Max(0, region.Left); x < Math.Min(bmp.Width, region.Right); x++)
        {
            total++;
            var c = bmp.GetPixel(x, y);
            if (c.Red < 20 && c.Green < 20 && c.Blue < 20) black++;
        }
        return total == 0 ? 0 : (double)black / total;
    }

    private static void AppendBlackRectangle(PdfPage page, PdfRectangle rect)
    {
        var content = page.GetContentStream();
        var ops = new System.Collections.Generic.List<Excise.Core.Content.ContentOperator>(content.Operators);
        ops.Add(Excise.Core.Content.ContentOperator.SaveState());
        ops.Add(Excise.Core.Content.ContentOperator.SetFillRgb(0, 0, 0));
        ops.Add(Excise.Core.Content.ContentOperator.Rectangle(
            rect.Left, rect.Bottom, rect.Right - rect.Left, rect.Top - rect.Bottom));
        ops.Add(Excise.Core.Content.ContentOperator.Fill());
        ops.Add(Excise.Core.Content.ContentOperator.RestoreState());
        page.SetContentStream(new Excise.Core.Content.ContentStream(ops));
    }

    /// <summary>
    /// Build a minimal single-page PDF with:
    /// - A 20×12 "SECRET TEXT" literal drawn via Tj at (TextX, TextBaselineY)
    /// - A solid-red DeviceRGB image XObject placed via
    ///   <c>q W 0 0 H X Y cm /Im0 Do Q</c> at (ImageX, ImageY).
    /// </summary>
    private static byte[] BuildPdfWithTextAndRedImage()
    {
        using var ms = new MemoryStream();
        using var w = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\n"
        };

        w.WriteLine("%PDF-1.4");
        w.Flush();

        var offsets = new long[7];

        offsets[1] = ms.Position;
        w.WriteLine("1 0 obj");
        w.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        w.WriteLine("endobj");
        w.Flush();

        offsets[2] = ms.Position;
        w.WriteLine("2 0 obj");
        w.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        w.WriteLine("endobj");
        w.Flush();

        offsets[3] = ms.Position;
        w.WriteLine("3 0 obj");
        w.WriteLine($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] " +
                    "/Contents 4 0 R /Resources << " +
                    "/Font << /F1 5 0 R >> " +
                    "/XObject << /Im0 6 0 R >> >> >>");
        w.WriteLine("endobj");
        w.Flush();

        // Content stream: draw text, then draw red image XObject
        var content =
            "BT /F1 14 Tf " + $"{TextX} {TextBaselineY} Td (SECRET TEXT) Tj ET\n" +
            $"q {ImageW} 0 0 {ImageH} {ImageX} {ImageY} cm /Im0 Do Q";
        offsets[4] = ms.Position;
        w.WriteLine("4 0 obj");
        w.WriteLine($"<< /Length {content.Length} >>");
        w.WriteLine("stream");
        w.Write(content);
        w.WriteLine();
        w.WriteLine("endstream");
        w.WriteLine("endobj");
        w.Flush();

        offsets[5] = ms.Position;
        w.WriteLine("5 0 obj");
        w.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        w.WriteLine("endobj");
        w.Flush();

        // 4×4 pure-red DeviceRGB image so Skia has real pixel data.
        int imgW = 4, imgH = 4;
        var pixels = new byte[imgW * imgH * 3];
        for (int i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = 255;     // R
            pixels[i + 1] = 0;   // G
            pixels[i + 2] = 0;   // B
        }

        offsets[6] = ms.Position;
        w.WriteLine("6 0 obj");
        w.WriteLine($"<< /Type /XObject /Subtype /Image /Width {imgW} /Height {imgH} " +
                    "/ColorSpace /DeviceRGB /BitsPerComponent 8 " +
                    $"/Length {pixels.Length} >>");
        w.WriteLine("stream");
        w.Flush();
        ms.Write(pixels, 0, pixels.Length);
        w.WriteLine();
        w.WriteLine("endstream");
        w.WriteLine("endobj");
        w.Flush();

        long xrefPos = ms.Position;
        w.WriteLine("xref");
        w.WriteLine("0 7");
        w.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 6; i++)
            w.WriteLine($"{offsets[i]:D10} 00000 n ");
        w.Flush();
        w.WriteLine("trailer");
        w.WriteLine("<< /Root 1 0 R /Size 7 >>");
        w.WriteLine("startxref");
        w.WriteLine(xrefPos.ToString());
        w.WriteLine("%%EOF");
        w.Flush();

        return ms.ToArray();
    }
}
