using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Differential;

/// <summary>
/// Two PDFium corpus pages that rendered COMPLETELY BLANK in excise while
/// mutool and pdftocairo both drew their text (#1382, split out of #1363).
///
/// ⚠️ The issue's stated cause — "text operators outside a BT/ET text object" —
/// is WRONG on both files, and re-checking it is what found the real defects.
/// Both content streams carry a proper <c>BT</c> … <c>ET</c>. The actual causes
/// are unrelated to each other:
///
/// <list type="bullet">
///   <item><b>bug_602650.pdf</b> — an incrementally-updated file whose every
///     section offset is stated 4 bytes early, landing on the <c>obj</c> tail of
///     the preceding <c>endobj</c>. The ROOT section survived by falling through
///     to the file-tail rescan; the <c>/Prev</c> section had no such fallback and
///     the nearby-keyword repair searched BACKWARDS ONLY, so revision 1 was
///     dropped whole and the page's <c>/Contents 3 0 R</c> resolved to null.
///     Fixed by searching both sides of the stated offset, nearest first
///     (<c>XRefParser.TryFindNearbyTraditionalXRef</c>).</item>
///   <item><b>363015187.pdf</b> — a Widget <c>/AP /N</c> stream whose
///     non-embedded TrueType carries <c>/Differences [49 /Alpha]</c>.
///     Excise.Rendering held its OWN second copy of the Adobe Glyph List, a
///     subset stopping at Latin Extended-A, so the Greek name resolved to
///     '\0' and the glyph was silently not drawn. Fixed by falling through to
///     Excise.Core's glyph-name table — the single font-decoding authority
///     CLAUDE.md names.</item>
/// </list>
///
/// Both assertions are against INDEPENDENT renderers (mutool, pdftocairo), not
/// against a checked-in excise baseline: excise confirming excise cannot see a
/// defect excise holds consistently. The band is wide on purpose — the point is
/// "draws the content" versus "draws nothing", not rasteriser parity.
/// </summary>
public class BlankPageRecoveryTests
{
    public static TheoryData<string, double, double> BlankPageCases() => new()
    {
        // fixture (relative to test-pdfs), min ink fraction of the oracle, max
        { "pdfium/bug_602650.pdf", 0.70, 1.30 },
        { "pdfium/363015187.pdf", 0.50, 1.50 },
    };

    [Theory]
    [MemberData(nameof(BlankPageCases))]
    public void PageThatRenderedBlank_NowInksWithinBandOfTheOracles(
        string relativePath, double minRatio, double maxRatio)
    {
        var path = Path.Combine(FindRepoRoot(), "test-pdfs", relativePath);
        Assert.SkipUnless(File.Exists(path), $"corpus fixture not present: {relativePath}");
        Assert.SkipUnless(
            MutoolReferenceRenderer.IsAvailable || PdftocairoReferenceRenderer.IsAvailable,
            "no independent renderer installed (mutool or pdftocairo)");

        const int dpi = 150;

        using var doc = PdfDocument.Open(path);
        using var ours = new SkiaRenderer().RenderPage(doc.GetPage(1), new RenderOptions { Dpi = dpi });
        var ourInk = InkedPixels(ours);

        var oracleInk = new List<(string Name, long Ink)>();
        if (MutoolReferenceRenderer.IsAvailable)
        {
            using var m = MutoolReferenceRenderer.RenderPage(path, 1, dpi);
            if (m != null) oracleInk.Add(("mutool", InkedPixels(m)));
        }

        if (PdftocairoReferenceRenderer.IsAvailable)
        {
            using var c = PdftocairoReferenceRenderer.RenderPage(path, 1, dpi);
            if (c != null) oracleInk.Add(("pdftocairo", InkedPixels(c)));
        }

        Assert.SkipWhen(oracleInk.Count == 0, "no oracle could render the fixture");

        var inking = oracleInk.Where(o => o.Ink > 0).ToList();
        inking.Should().NotBeEmpty(
            "this gate only means something while the references still draw this page; " +
            "if every oracle now inks 0 the fixture changed and the comparison below " +
            "would pass vacuously on a blank excise render");

        ourInk.Should().BeGreaterThan(0,
            $"excise rendered {relativePath} completely blank before #1382 while " +
            string.Join(", ", inking.Select(o => $"{o.Name} inked {o.Ink} px")) +
            ". Zero ink here means the page is blank again.");

        // Compared against the oracle MEAN rather than the most-inked one:
        // scoring against the maximum elects the outlier by construction, which
        // is the mistake #932 fixed in the corpus scan's own missing-content rule.
        var mean = inking.Average(o => (double)o.Ink);
        (ourInk / mean).Should().BeInRange(minRatio, maxRatio,
            $"excise inked {ourInk} px against an oracle mean of {mean:F0} " +
            $"({string.Join(", ", inking.Select(o => $"{o.Name}={o.Ink}"))})");
    }

    /// <summary>
    /// The glyph-name half of the fix, pinned directly rather than only through
    /// a raster: a <c>/Differences</c> name outside the renderer's own AGL
    /// subset must still resolve, or the glyph is silently dropped.
    /// </summary>
    [Theory]
    [InlineData("Alpha", 'Α')]     // Greek — 363015187.pdf's actual name
    [InlineData("Omega", 'Ω')]
    [InlineData("beta", 'β')]
    [InlineData("one", '1')]       // still served by the local subset
    [InlineData("uni0142", 'ł')]   // algorithmic convention, unchanged
    public void GlyphNamesOutsideTheRendererSubset_ResolveThroughCore(
        string glyphName, char expected)
    {
        AdobeGlyphList.TryGet(glyphName, out var unicode).Should().BeTrue(
            $"/Differences [... /{glyphName}] must map to a code point; an " +
            "unresolved name becomes '\\0' and the glyph is not drawn at all");
        unicode.Should().Be(expected);
    }

    private static long InkedPixels(SKBitmap bmp)
    {
        long inked = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if ((c.Red + c.Green + c.Blue) / 3 < 250)
                    inked++;
            }
        }

        return inked;
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "excise.sln")))
            d = d.Parent;
        return d?.FullName ?? AppContext.BaseDirectory;
    }
}
