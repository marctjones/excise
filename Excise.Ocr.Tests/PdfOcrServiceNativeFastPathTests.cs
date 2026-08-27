using System;
using System.Linq;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Graphics;
using Excise.Ocr;
using Excise.Ocr.Native;
using Xunit;

namespace Excise.Ocr.Tests;

/// <summary>
/// The opt-in FFI fast path (#1139) must be a drop-in behind the same
/// <see cref="PdfOcrService"/> surface the subprocess path uses — callers
/// (including <see cref="DifferentialOcrAuditor"/>, the #1137 ocr-differential
/// channel) don't know which backend ran. These tests prove the native path
/// yields the same word set as the subprocess path, so routing the differential
/// auditor through the fast path when available is behaviour-preserving.
/// </summary>
public class PdfOcrServiceNativeFastPathTests
{
    private static readonly string TessdataPrefix =
        Environment.GetEnvironmentVariable("TESSDATA_PREFIX") ?? "";

    private static bool BothBackendsAvailable =>
        new PdfOcrService().IsAvailable() && NativeOcrEngine.IsAvailable();

    private static PdfPage RenderTextPage()
    {
        var doc = PdfDocument.CreateNew();
        var page = doc.Pages.AddBlank(612, 792);
        using (var g = page.GetGraphics())
        {
            g.DrawString("HELLO EXCISE WORLD", PdfFont.Helvetica(32),
                PdfBrush.Black, 100, 600);
            g.Flush();
        }
        return page;
    }

    private static string[] Words(OcrResult r) =>
        r.Words.Select(w => w.Text.Trim('.', ',', ':', ';', '!', '?', '"', '\'').ToUpperInvariant())
               .Where(s => s.Length > 0)
               .OrderBy(s => s, StringComparer.Ordinal)
               .ToArray();

    [Fact]
    public void NativeFastPath_ProducesSameWords_AsSubprocess()
    {
        Assert.SkipUnless(BothBackendsAvailable, "need both tesseract CLI and libtesseract");

        var page = RenderTextPage();

        var subprocess = new PdfOcrService(dpi: 300, tessdataPrefix: TessdataPrefix, useNativeFastPath: false);
        var native = new PdfOcrService(dpi: 300, tessdataPrefix: TessdataPrefix, useNativeFastPath: true);

        native.NativeFastPathActive.Should().BeTrue("libtesseract is available in this environment");

        var subResult = subprocess.RecognizePage(page);
        var natResult = native.RecognizePage(page);

        // Both must read the three words; word bboxes come through the same
        // ParseTsv path, so the sets match exactly.
        Words(natResult).Should().Contain(new[] { "HELLO", "EXCISE", "WORLD" });
        Words(natResult).Should().BeEquivalentTo(Words(subResult));
    }

    [Fact]
    public void NativeFastPath_WordBoxes_AreInPageSpaceLikeSubprocess()
    {
        Assert.SkipUnless(BothBackendsAvailable, "need both tesseract CLI and libtesseract");

        var page = RenderTextPage();
        var native = new PdfOcrService(dpi: 300, tessdataPrefix: TessdataPrefix, useNativeFastPath: true);

        var result = native.RecognizePage(page);

        result.Words.Should().NotBeEmpty();
        // The text was drawn near the top of a 792pt page; in PDF bottom-left
        // points that is a HIGH y. This confirms the native TSV went through the
        // same pixel->points conversion as the subprocess path (no header-skew,
        // no flipped axis).
        var hello = result.Words.First(w =>
            w.Text.Trim('.', ',').Equals("HELLO", StringComparison.OrdinalIgnoreCase));
        hello.BoundingBox.Bottom.Should().BeGreaterThan(400);
    }

    [Fact]
    public void NativeFastPathActive_False_WhenNotOptedIn()
    {
        // Default construction must never silently switch backends.
        new PdfOcrService().NativeFastPathActive.Should().BeFalse();
    }

    private static string? RealTessdataDir() =>
        new[]
        {
            Environment.GetEnvironmentVariable("TESSDATA_PREFIX"),
            "/opt/homebrew/share/tessdata",
            "/usr/local/share/tessdata",
            "/usr/share/tessdata",
            "/usr/share/tesseract-ocr/5/tessdata",
        }.FirstOrDefault(d => !string.IsNullOrEmpty(d) &&
                              System.IO.File.Exists(System.IO.Path.Combine(d!, "eng.traineddata")));

    [Fact]
    public void NativeFastPath_HonoursExplicitTessdataPrefix()
    {
        Assert.SkipUnless(BothBackendsAvailable, "need both tesseract CLI and libtesseract");
        var dir = RealTessdataDir();
        Assert.SkipWhen(dir == null, "no tessdata dir with eng.traineddata found to point at explicitly");

        // A NON-EMPTY, explicit prefix must be threaded to TessBaseAPIInit3 —
        // the defect this pins is the native path ignoring tessdataPrefix and
        // silently using system data instead.
        var native = new PdfOcrService(dpi: 300, tessdataPrefix: dir, useNativeFastPath: true);
        native.NativeFastPathActive.Should().BeTrue();

        var result = native.RecognizePage(RenderTextPage());
        Words(result).Should().Contain(new[] { "HELLO", "EXCISE", "WORLD" });
    }

    [Fact]
    public void NativeFastPathActive_False_WhenTessdataPrefixHasNoModel()
    {
        Assert.SkipUnless(NativeOcrEngine.IsAvailable(), "libtesseract not present");

        // A prefix that exists but holds no traineddata must fail to init, so
        // the instance falls back to the subprocess path rather than pretending
        // the fast path is live. (Confirms the prefix reaches Init3.)
        var bogus = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "excise-empty-tessdata-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(bogus);
        try
        {
            var svc = new PdfOcrService(dpi: 300, tessdataPrefix: bogus, useNativeFastPath: true);
            svc.NativeFastPathActive.Should().BeFalse();
        }
        finally
        {
            try { System.IO.Directory.Delete(bogus, recursive: true); } catch { }
        }
    }
}
