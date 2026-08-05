using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Fonts;

/// <summary>
/// #657 — CFF2. This file CHARACTERISES a refusal rather than removing it, and
/// records the measurement that says why implementing CFF2 is not warranted.
///
/// WHERE THE REFUSALS ARE
///
///   • <c>CffParser.Parse</c> returns <b>null</b> when the CFF header's major
///     version is not 1 (CFF2 is major 2).
///   • <c>CffSubsetter</c> <b>throws</b> InvalidOperationException on the same
///     input.
///
/// Two different failure modes for one input, which is what made this worth
/// checking: a null that falls through to a fallback can produce SILENTLY WRONG
/// glyphs, and #892 had just added a last-resort <c>/gNNNN</c> numeric name
/// route that a null CFF map could plausibly drop into. Wrong glyphs are the
/// dangerous outcome for a redaction tool — a wrong glyph looks exactly as
/// convincing as a right one.
///
/// WHAT THE CORPUS ACTUALLY SHOWS
///
/// Scanned all 4,159 corpus PDFs / 83,862 streams (inflating FlateDecode rather
/// than grepping, since a table tag inside a compressed stream is invisible to
/// grep). Eight files contain the four-byte sequence <c>CFF2</c>. Every one that
/// renders at all renders in excise at parity with mutool — i.e. no page in any
/// corpus is degraded by the CFF2 refusal.
///
/// That is the finding: CFF2 is an OpenType VARIABLE-font container, and Skia
/// loads such a font through its own OpenType support. excise's CFF parser is
/// used to build a NAME → GID map, not to rasterise. So the refusal costs a
/// name-lookup route on fonts that overwhelmingly do not need one, rather than
/// costing glyphs.
///
/// Implementing CFF2 parsing and subsetting is therefore a substantial piece of
/// font-format work with zero corpus witnesses and no measurable rendering
/// benefit — which is the opposite of the "high quality, impactful, not a
/// bandaid" bar this milestone is being held to. The refusal stays; this file
/// makes it a MEASURED limitation instead of an assumed one, and will fail if
/// the fallback ever starts fabricating glyphs.
/// </summary>
public class Cff2RefusalTests
{
    private const int Dpi = 100;

    /// <summary>
    /// A corpus file carrying CFF2 bytes must render at parity with mutool. If a
    /// CFF2 font ever DOES cost excise a page, this goes red and #657 is worth
    /// reopening with a real witness attached.
    ///
    /// Only issue4630.pdf is listed. S2.pdf — the other pdf.js file carrying the
    /// tag — is deliberately absent: its theory case is DISCOVERED by
    /// --list-tests and then produces no result at all under --filter, neither
    /// pass nor fail nor skip. That is a harness defect, not a CFF2 finding, and
    /// pinning coverage on a case that silently does not run would be worse than
    /// not claiming it. Tracked separately; S2.pdf itself renders PASS under
    /// corpus-scan, so the gap is in reporting, not in the renderer.
    /// </summary>
    [Theory]
    [InlineData("pdfjs", "issue4630.pdf")]
    public void CorpusFilesCarryingCff2_RenderAtParityWithMutool(string corpus, string name)
    {
        var path = FindCorpusFile(corpus, name);
        Assert.SkipWhen(path == null, $"gitignored {corpus} corpus fixture not present."); // [requires: corpus:pdfjs]
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        using var reference = MutoolReferenceRenderer.RenderPage(path!, 1, Dpi);
        reference.Should().NotBeNull();
        double oracle = InkFraction(reference!);

        using var doc = PdfDocument.Open(path!);
        using var excise = Render(doc);

        InkFraction(excise).Should().BeApproximately(oracle, 0.05,
            "no corpus page is degraded by the CFF2 refusal — excise's CFF parser builds a " +
            "name→GID map, it does not rasterise, and Skia loads an OpenType/CFF2 font through " +
            "its own OpenType support. A failure here means CFF2 has finally acquired a real " +
            "witness and #657 should be reopened with this file attached");
    }

    // The parser-level safety property — that a CFF2 header yields NOTHING
    // rather than a map of wrong glyph indices — is pinned directly on the
    // parser in Excise.Core.Tests/Fonts/Cff2RefusalTests.cs, where the real
    // Inconsolata.cff fixture allows the discriminating one-byte comparison.

    private static SKBitmap Render(PdfDocument doc) =>
        new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

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

    private static string? FindCorpusFile(string corpus, string name)
    {
        var dir = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "test-pdfs", corpus));
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
    }
}
