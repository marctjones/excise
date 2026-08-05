using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// #893 — the evidence behind "excise does not read /EndOfBlock, and that is
/// not costing anything".
///
/// §7.4.6 defines <c>/EndOfBlock</c> (default true): an EOFB pattern terminates
/// the data. <c>CcittFaxDecoder</c> never reads it — it decodes to <c>/Rows</c>
/// or to end-of-data. The issue reasoned that both reach the same place on a
/// well-formed stream and differ only on a truncated one, but that was
/// reasoning, not measurement, and the classifier meanwhile reported the
/// parameter as unsupported whenever it appeared.
///
/// Measured: across all 4,159 corpus PDFs (18 carry CCITTFaxDecode) exactly one
/// file sets <c>/EndOfBlock</c> at all — pdfjs/ccitt_EndOfBlock_false.pdf, which
/// carries BOTH true and false streams, i.e. it is a purpose-built A/B fixture
/// for this parameter. It renders at parity with three independent oracles:
///
/// <code>
///   mutool 0.6483   pdftocairo 0.6514   ghostscript 0.6538
/// </code>
///
/// So the reasoning holds where it can be checked, and #893 is a documentation
/// and reporting problem rather than a decoder gap. The classifier was corrected
/// to judge <c>/EndOfBlock</c> by VALUE (true and absent are the default and
/// cost nothing; false carries a truncation caveat) instead of by presence —
/// the same over-reporting bug fixed in the JBIG2 classifier under #656.
///
/// This test is the part that would notice if the reasoning were wrong.
/// </summary>
public class CcittEndOfBlockTests
{
    private const int Dpi = 150;

    [Fact]
    public void TheOnlyCorpusFileSettingEndOfBlock_RendersAtOracleParity()
    {
        var path = FindCorpusFile("ccitt_EndOfBlock_false.pdf");
        Assert.SkipWhen(path == null, "gitignored pdf.js corpus fixture not present (scripts/download-pdfjs-corpus.sh)."); // [requires: corpus:pdfjs]
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        using var reference = MutoolReferenceRenderer.RenderPage(path!, 1, Dpi);
        reference.Should().NotBeNull();
        double oracle = InkFraction(reference!);
        oracle.Should().BeGreaterThan(0.1,
            "the fixture is a dense fax image; if mutool stops producing one this test's " +
            "premise has changed and the comparison below means nothing");

        var diagnostics = new List<string>();
        using var doc = PdfDocument.Open(path!);
        using var excise = Render(doc, diagnostics);

        InkFraction(excise).Should().BeApproximately(oracle, 0.02,
            "decoding to /Rows or end-of-data reaches the same place as obeying an EOFB " +
            "pattern on a well-formed stream. If this diverges, /EndOfBlock has stopped being " +
            "a truncation-only concern and #893 needs a real decoder change");

        diagnostics.Should().NotContain(d => d.Contains("required bytes") || d.Contains("no bitmap"),
            "the CCITT images on this page must decode, not be refused — a refusal would make " +
            "the ink comparison above pass for the wrong reason if the page were mostly blank");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static SKBitmap Render(PdfDocument doc, List<string> diagnostics) =>
        new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions
            {
                Dpi = Dpi,
                AntiAlias = false,
                BackgroundColor = SKColors.White,
                Diagnostics = diagnostics,
            });

    private static double InkFraction(SKBitmap b)
    {
        int n = 0;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
            {
                var c = b.GetPixel(x, y);
                if (c.Red < 200 || c.Green < 200 || c.Blue < 200) n++;
            }
        return (double)n / (b.Width * b.Height);
    }

    private static string? FindCorpusFile(string name)
    {
        var dir = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "test-pdfs", "pdfjs"));
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
    }
}
