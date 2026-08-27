using System;
using AwesomeAssertions;
using Excise.Ocr.Native;
using Xunit;

namespace Excise.Ocr.Tests;

/// <summary>
/// Crash-ISOLATED smoke tests for the direct tesseract FFI binding (#1139).
///
/// Containment story: these live in the <c>Excise.Ocr.Tests</c> assembly, which
/// runs in its own testhost. A native segfault here takes down only this
/// process — never the serial <c>Excise.App.Tests</c> host that #363/#985 were
/// about. Every test is gated on <see cref="NativeOcrEngine.IsAvailable"/>, so
/// a machine without libtesseract SKIPS rather than fails, and the FFI path is
/// never entered blind.
/// </summary>
public class NativeOcrEngineTests
{
    private static bool NativeAvailable => NativeOcrEngine.IsAvailable();

    /// <summary>
    /// A 220x80 white raster with a crude black "HI" — the same synthetic image
    /// the proof binding used. No font files, no rendering dependency.
    /// </summary>
    private static byte[] BuildHiRaster(out int width, out int height)
    {
        width = 220;
        height = 80;
        int w = width, h = height;
        var buf = new byte[w * h];
        for (int i = 0; i < buf.Length; i++) buf[i] = 255;
        void Px(int x, int y) { if (x >= 0 && x < w && y >= 0 && y < h) buf[y * w + x] = 0; }
        for (int y = 15; y < 65; y++) { Px(30, y); Px(31, y); Px(60, y); Px(61, y); } // H stems
        for (int x = 30; x < 62; x++) { Px(x, 39); Px(x, 40); }                       // H bar
        for (int y = 15; y < 65; y++) { Px(100, y); Px(101, y); }                     // I stem
        for (int x = 88; x < 114; x++) { Px(x, 15); Px(x, 16); Px(x, 63); Px(x, 64); } // I serifs
        return buf;
    }

    [Fact]
    public void OcrRegion_OnSyntheticText_ReturnsTextAndConfidence_NoCrash()
    {
        Assert.SkipUnless(NativeAvailable, "libtesseract / eng.traineddata not present");

        var pixels = BuildHiRaster(out int w, out int h);
        using var engine = NativeOcrEngine.Create("eng", 300);

        var (text, confidence) = engine.OcrRegion(pixels, w, h, NativeOcrEngine.DefaultPageSegMode);

        // Not asserting the exact string — OCR of a hand-plotted glyph is noisy.
        // The contract under test is: it round-trips (text out, confidence in
        // range, handle + result freed) without taking the process down.
        text.Should().NotBeNull();
        confidence.Should().BeInRange(0f, 1f);
        text.Should().ContainAny("H", "I");
    }

    [Fact]
    public void OcrRegionTsv_HasNoHeaderRow_FirstLineIsData()
    {
        Assert.SkipUnless(NativeAvailable, "libtesseract / eng.traineddata not present");

        var pixels = BuildHiRaster(out int w, out int h);
        using var engine = NativeOcrEngine.Create("eng", 300);

        string tsv = engine.OcrRegionTsv(pixels, w, h, 6);
        var firstLine = tsv.Split('\n')[0];

        // Pins the empirical finding that drove PdfOcrService's hasHeader=false:
        // TessBaseAPIGetTsvText emits no `level\t...` header, unlike the CLI TSV
        // renderer. If a future libtesseract starts emitting one, this fails and
        // the parser flag must be revisited.
        firstLine.Should().NotStartWith("level",
            "TessBaseAPIGetTsvText emits data rows directly, no header");
        firstLine.Split('\t')[0].Should().Be("1",
            "the first TSV data row is the page/block level (1)");
    }

    [Fact]
    public void Engine_ReusedAcrossManyRegions_StaysAlive()
    {
        Assert.SkipUnless(NativeAvailable, "libtesseract / eng.traineddata not present");

        var pixels = BuildHiRaster(out int w, out int h);
        using var engine = NativeOcrEngine.Create("eng", 300);

        // The whole point of the in-process path is many small regions on one
        // handle. Exercise the reuse + per-region Clear path.
        for (int i = 0; i < 10; i++)
        {
            var (text, conf) = engine.OcrRegion(pixels, w, h, 6);
            text.Should().NotBeNull();
            conf.Should().BeInRange(0f, 1f);
        }
    }

    [Fact]
    public void OcrRegion_UndersizedBuffer_ThrowsBeforeTouchingNative()
    {
        Assert.SkipUnless(NativeAvailable, "libtesseract / eng.traineddata not present");

        using var engine = NativeOcrEngine.Create("eng", 300);
        var tooSmall = new byte[10];

        // Guard runs in managed code before any pointer reaches libtesseract —
        // this is the boundary that keeps a bad size from becoming a segfault.
        Action act = () => engine.OcrRegion(tooSmall, 100, 100, 3);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IsAvailable_IsStableAndNeverThrows()
    {
        // Must be safe to call on ANY machine (that's how it gates everything
        // else). Memoized, so repeated calls agree.
        var first = NativeOcrEngine.IsAvailable();
        var second = NativeOcrEngine.IsAvailable();
        second.Should().Be(first);
    }
}
