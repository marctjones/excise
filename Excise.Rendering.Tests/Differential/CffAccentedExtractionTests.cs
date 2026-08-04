using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// The end-to-end half of the CFF standard-strings fix (the table held 244 of
/// 391 entries and was misaligned from SID 151).
///
/// WHAT IS AND IS NOT PROVEN HERE
///
/// This started life asserting that the fix restored EXTRACTION, on the
/// reasoning that CffParser feeds TextExtractor and so a wrong SID is wrong
/// text — which would make it a redaction-security defect under CLAUDE.md's
/// "redaction completeness is bounded by extraction coverage".
///
/// The mutation check refuted that: with the table reverted, this fixture
/// still extracts "ü" correctly. Its Unicode arrives by another route, so on
/// this page the defect was RENDERING only. Measured, both ways:
///
///     without the table fix   MISSING_CONTENT   (blank page)
///     with the table fix      PASS
///
/// The extraction concern remains structurally real — CffParser.ResolveSid IS
/// on the extraction path — but no fixture demonstrates it, so this test
/// claims only what it can show. The extraction assertion is kept as a
/// non-regression guard and is explicitly NOT the discriminating one.
/// </summary>
public class CffAccentedExtractionTests
{
    [Fact]
    public void AccentedGlyphFromACffSubset_IsRendered_AndAgreesWithMutool()
    {
        var path = FindCorpusFile("pdfjs", "issue4573.pdf");
        Assert.SkipWhen(path == null, "gitignored pdf.js corpus fixture not present (scripts/download-pdfjs-corpus.sh)."); // [requires: corpus:pdfjs]
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        using var doc = PdfDocument.Open(path!);
        using var excise = new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = 72, AntiAlias = false, BackgroundColor = SKColors.White });
        using var reference = MutoolReferenceRenderer.RenderPage(path!, 1, 72);
        reference.Should().NotBeNull();

        // THE DISCRIMINATING ASSERTION. udieresis's CFF standard string sits
        // past the end of the old 244-entry table, so it resolved to null and
        // the only glyph on the page did not rasterise.
        InkPixels(reference!).Should().BeGreaterThan(0,
            "mutool draws the glyph — otherwise the fixture, not excise, is at fault");
        InkPixels(excise).Should().BeGreaterThan(0,
            "the page's only glyph is udieresis; with a truncated standard-strings " +
            "table its name resolved to null and the page rendered blank");

        // Non-regression only: this passed before the fix too (see the class
        // remarks) and must keep passing.
        new TextExtractor(doc.GetPage(1)).ExtractText().Should().Contain("ü");
    }

    private static int InkPixels(SKBitmap bmp)
    {
        int ink = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red < 240 || c.Green < 240 || c.Blue < 240) ink++;
            }
        return ink;
    }

    private static string? FindCorpusFile(string corpus, string name)
    {
        var dir = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "test-pdfs", corpus));
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
    }
}
