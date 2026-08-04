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
    /// image geometry requires. Pinned on the real JBIG2 fixture, since that is
    /// where the numbers come from.
    /// </summary>
    [Fact]
    public void ShortSampleBuffer_ReportsTheShortfallAndTheFilter()
    {
        var path = FindCorpusFile("pdfium", "bug_631912.pdf");
        Assert.SkipWhen(path == null, "gitignored PDFium corpus fixture not present (scripts/download-pdfium-corpus.sh)."); // [requires: corpus:pdfium]

        var diagnostics = new List<string>();
        using var doc = PdfDocument.Open(path!);
        using var _ = new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = 72, AntiAlias = false, BackgroundColor = SKColors.White, Diagnostics = diagnostics });

        var message = diagnostics.FirstOrDefault(d => d.Contains("required bytes"));
        message.Should().NotBeNull(
            "an image refused for supplying too few samples must say so — silence here is " +
            "what made #874's root cause unknown for weeks");

        message.Should().Contain("JBIG2Decode",
            "the FILTER is the actionable part: 'an image failed' does not locate a bug, " +
            "'/JBIG2Decode returned 83 of 103680 bytes' does");
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
