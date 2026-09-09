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
        // #1381: a FreeText with /Border [0 0 0] and a UTF-16BE /Contents whose
        // Polish "ł" is above U+00FF. Border suppressed + text suppressed = an
        // annotation that vanished, on a page with no /Contents and empty
        // /Resources, so the whole page was blank.
        { "pdfjs/bug1865341.pdf", 0.60, 1.40 },
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

        // An oracle that rasterized a DIFFERENT PAGE BOX gets no vote — its
        // pixels address a different part of the page, so its ink count is not
        // comparable (#932's rule in the corpus scan). pdftocairo renders the
        // /MediaBox where excise and mutool render the /CropBox, and on
        // bug1865341 that is 1275x1650 against 478x206.
        var oracleInk = new List<(string Name, long Ink)>();
        if (MutoolReferenceRenderer.IsAvailable)
        {
            using var m = MutoolReferenceRenderer.RenderPage(path, 1, dpi);
            if (SameBox(m, ours)) oracleInk.Add(("mutool", InkedPixels(m!)));
        }

        if (PdftocairoReferenceRenderer.IsAvailable)
        {
            using var c = PdftocairoReferenceRenderer.RenderPage(path, 1, dpi);
            if (SameBox(c, ours)) oracleInk.Add(("pdftocairo", InkedPixels(c!)));
        }

        Assert.SkipWhen(oracleInk.Count == 0,
            "no oracle rendered the fixture on the same page box");

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

    /// <summary>
    /// #1382's policy half, measured on a purpose-built fixture rather than on
    /// the two corpus pages the issue wrongly attributed to it.
    ///
    /// A file that shows text with no enclosing <c>BT</c>/<c>ET</c> is
    /// non-conformant (§9.4.1) and ISO 32000-2 does not require rendering it.
    /// excise recovers it because its OWN extractor already did — the walker
    /// never gated on BT — so refusing in the renderer was a divergence inside
    /// excise, not a conformance stance.
    ///
    /// The second case is the one that matters for regressions: a well-formed
    /// stream that sets text state BETWEEN text objects must be completely
    /// unaffected, or the implicit open would fire on ordinary pages.
    /// </summary>
    [Theory]
    [InlineData(false, "20 50 Td\n/F1 24 Tf\n(Hello, world!) Tj\n")]
    [InlineData(true, "/F1 24 Tf\n0 g\nBT\n20 50 Td\n(Hello, world!) Tj\nET\n/F1 24 Tf\n2 Tc\n")]
    public void TextOperatorsOutsideATextObject_DrawWhatTheOraclesDraw(
        bool wellFormed, string contentStream)
    {
        Assert.SkipUnless(
            MutoolReferenceRenderer.IsAvailable || PdftocairoReferenceRenderer.IsAvailable,
            "no independent renderer installed (mutool or pdftocairo)");

        var pdf = BuildHelveticaPage(contentStream);
        var path = Path.Combine(Path.GetTempPath(), $"excise-1382-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            using var doc = PdfDocument.Open(pdf);
            using var ours = new SkiaRenderer().RenderPage(doc.GetPage(1), new RenderOptions { Dpi = 150 });
            var ourInk = InkedPixels(ours);

            var oracles = new List<(string Name, long Ink)>();
            if (MutoolReferenceRenderer.IsAvailable)
            {
                using var m = MutoolReferenceRenderer.RenderPage(path, 1, 150);
                if (m != null) oracles.Add(("mutool", InkedPixels(m)));
            }

            if (PdftocairoReferenceRenderer.IsAvailable)
            {
                using var c = PdftocairoReferenceRenderer.RenderPage(path, 1, 150);
                if (c != null) oracles.Add(("pdftocairo", InkedPixels(c)));
            }

            var inking = oracles.Where(o => o.Ink > 0).ToList();
            Assert.SkipWhen(inking.Count == 0, "no oracle inked the fixture");

            var mean = inking.Average(o => (double)o.Ink);
            var why = wellFormed
                ? "a well-formed BT/ET page with text state set outside the text " +
                  "object must be untouched by the implicit-open lenience"
                : "text shown with no enclosing BT/ET rendered as a BLANK PAGE " +
                  "before #1382, while excise's own text extractor returned the " +
                  "string — the renderer was diverging from the one state machine";

            (ourInk / mean).Should().BeInRange(0.85, 1.15,
                $"{why}. excise inked {ourInk} px against an oracle mean of {mean:F0} " +
                $"({string.Join(", ", inking.Select(o => $"{o.Name}={o.Ink}"))})");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] BuildHelveticaPage(string contentStream)
    {
        var content = System.Text.Encoding.ASCII.GetBytes(contentStream);
        var objects = new List<byte[]>
        {
            System.Text.Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
            System.Text.Encoding.ASCII.GetBytes("<< /Type /Pages /Count 1 /Kids [3 0 R] >>"),
            System.Text.Encoding.ASCII.GetBytes(
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 120] " +
                "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
            System.Text.Encoding.ASCII
                .GetBytes($"<< /Length {content.Length} >>\nstream\n")
                .Concat(content)
                .Concat(System.Text.Encoding.ASCII.GetBytes("endstream"))
                .ToArray(),
            System.Text.Encoding.ASCII.GetBytes(
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        };

        using var ms = new MemoryStream();
        void Write(string s) => ms.Write(System.Text.Encoding.ASCII.GetBytes(s));
        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            Write($"{i + 1} 0 obj ");
            ms.Write(objects[i]);
            Write("\nendobj\n");
        }

        var xref = ms.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets)
            Write($"{o:D10} 00000 n \n");
        Write($"trailer << /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static bool SameBox(SKBitmap? oracle, SKBitmap ours) =>
        oracle != null && oracle.Width == ours.Width && oracle.Height == ours.Height;

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
