using AwesomeAssertions;
using Excise.Core.Document;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Regression cover for the two image-refusal diagnostics (#878, and the
/// codec-path gap closed in 0ee4a044), neither of which had a test.
///
/// WHY A DIAGNOSTIC IS WORTH A TEST AT ALL
///
/// #878 stopped excise painting a fabricated image from an undersized sample
/// buffer, which for a redaction tool is the dangerous failure: a solid black
/// rectangle is indistinguishable from a successful redaction. But it returned
/// null saying NOTHING, trading a visibly-wrong page for an invisibly-
/// incomplete one — and #874's body had predicted exactly that trap before the
/// guard existed ("the failure is invisible").
///
/// The diagnostic is what makes a decoder bug findable. It is the reason #874's
/// root cause is known at all: it reported /JBIG2Decode returning 83 of 103680
/// bytes, which ruled out the polarity hypothesis the issue had been filed on.
/// Silence there would put the next such bug back to square one.
///
/// The assertions check the SHAPE of the message — the filter name and the two
/// byte counts — not its prose. A string-equality test on the sentence would
/// break on any rewording while catching nothing extra.
/// </summary>
public class RefusedImageDiagnosticTests
{
    /// <summary>
    /// The RAW-SAMPLE path: a decoder returns fewer than half the bytes the
    /// image geometry requires.
    ///
    /// ⚠️ THIS TEST MOVED FIXTURES TWICE, BOTH TIMES BECAUSE THE BUG IT
    /// DOCUMENTED WAS FIXED — pinning by root cause is meant to do exactly
    /// this.
    ///
    /// First: pdfium bug_631912.pdf, whose /JBIG2Decode returned 83 of
    /// 103,680 bytes. #874 resolved the always-indirect /JBIG2Globals
    /// reference; that image now decodes (286 inked px, no diagnostic).
    ///
    /// Second: pdf.js bitmap-symbol-context-reuse.pdf, which short-decoded
    /// (454 of 20,000 bytes) because JBIG2 retained symbol-dictionary coding
    /// contexts were unimplemented (#656). #1396 (2026-09-10) gave that
    /// specific unimplemented-feature case its OWN diagnostic ("could not
    /// decode this stream (unimplemented feature): ... (#1396)") instead of
    /// the generic short-buffer message — see
    /// <see cref="PdftocairoOutlierClassificationTests.BitmapSymbolContextReuse_IsAJbig2ShortDecode_NotADisagreement"/>
    /// for that path now. This guard's witness moved again.
    ///
    /// Now: pdf20/huge-image-dimensions.pdf (#1398/#1451) — a deliberately
    /// underspecified fixture (1 byte of data for a declared 10000x10000
    /// 8bpc image, 100,000,000 required bytes) that is NOT regenerable with
    /// real payload data without defeating the resource-limit probe it
    /// exists to be (see the fixture's own rendering-contract note), so it
    /// should stay a stable witness unlike the two JBIG2 fixtures above. This
    /// image carries no compression filter at all — the diagnostic message
    /// says so ("no filter") rather than naming one, so this test checks the
    /// geometry is what locates the defect, not a filter name.
    /// </summary>
    [Fact]
    public void ShortSampleBuffer_ReportsTheShortfallAndTheGeometry()
    {
        var path = FindCorpusFile("pdf20", "huge-image-dimensions.pdf");
        Assert.SkipWhen(path == null, "corpus fixture not present (test-pdfs/pdf20)."); // [requires: corpus:pdf20]

        var diagnostics = new List<string>();
        using var doc = PdfDocument.Open(path!);
        using var _ = new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = 72, AntiAlias = false, BackgroundColor = SKColors.White, Diagnostics = diagnostics });

        var message = diagnostics.FirstOrDefault(d => d.Contains("required bytes"));
        message.Should().NotBeNull(
            "an image refused for supplying too few samples must say so — silence here is " +
            "what made #874's root cause unknown for weeks");

        message.Should().Contain("10000x10000",
            "the GEOMETRY is the actionable part when there's no filter to name: 'an image " +
            "failed' does not locate a bug, '10000x10000 ... 1 of 100000000 required bytes' does");
        message.Should().MatchRegex(@"\d+ of \d+",
            "the shortfall must be quantified, not merely asserted");
    }

    /// <summary>
    /// The CODEC path, which #878's guard never reached: a DCTDecode image
    /// whose stream is four bytes fails inside the JPEG decoder and returns
    /// null earlier. Four pages rendered blank with nothing saying why until
    /// 0ee4a044 covered the common exit.
    /// </summary>
    [Fact]
    public void CodecFailure_ReportsThatNoBitmapWasProduced()
    {
        var path = FindCorpusFile("pdfjs", "issue18042.pdf");
        Assert.SkipWhen(path == null, "gitignored pdf.js corpus fixture not present (scripts/download-pdfjs-corpus.sh)."); // [requires: corpus:pdfjs]

        var diagnostics = new List<string>();
        using var doc = PdfDocument.Open(path!);
        using var _ = new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = 72, AntiAlias = false, BackgroundColor = SKColors.White, Diagnostics = diagnostics });

        var message = diagnostics.FirstOrDefault(d => d.Contains("no bitmap"));
        message.Should().NotBeNull(
            "a codec that fails before the sample-count guard must still report — this is " +
            "the gap that made issue18042's four blank pages look like a vector-fill bug");

        message.Should().Contain("DCTDecode");
        message.Should().Contain("7300x7600",
            "the declared geometry is what shows the stream is absurdly short for it");
    }

    /// <summary>
    /// A healthy image must NOT emit either diagnostic. Without this the tests
    /// above would pass on a build that reported every image as refused.
    /// </summary>
    [Fact]
    public void AHealthyImage_ReportsNothing()
    {
        var path = FindCorpusFile("pdfjs", "issue4573.pdf");
        Assert.SkipWhen(path == null, "gitignored pdf.js corpus fixture not present."); // [requires: corpus:pdfjs]

        var diagnostics = new List<string>();
        using var doc = PdfDocument.Open(path!);
        using var _ = new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = 72, AntiAlias = false, BackgroundColor = SKColors.White, Diagnostics = diagnostics });

        diagnostics.Should().NotContain(d => d.Contains("required bytes") || d.Contains("no bitmap"),
            "a page that renders correctly must stay quiet — a diagnostic that fires on " +
            "everything is as useless as one that never fires");
    }

    private static string? FindCorpusFile(string corpus, string name)
    {
        var dir = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "test-pdfs", corpus));
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
    }
}
