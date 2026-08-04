using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Fonts;

/// <summary>
/// #892 — two name→GID routes that did not exist, so <c>/Differences</c> names
/// resolved to nothing and the pages rendered blank.
///
/// Distinct from #891: those fonts had a usable built-in cmap the renderer
/// could not read. These have no usable cmap route at all and the glyph must be
/// found BY NAME.
///
///   1. OpenType/CFF ("OTTO") shipped under <c>/FontFile2</c>. <c>isCff</c> is
///      set only for <c>/FontFile3</c> Type1C / CIDFontType0C, so the CFF
///      charset route never ran. Fixed by extracting the <c>CFF </c> table from
///      the container and building the map from its charset — WITHOUT
///      re-wrapping, because an OTTO font is already SFNT and Skia loads it.
///   2. <c>/gNNNN</c> names, a subsetting-tool convention meaning "glyph index
///      NNNN". Added as a LAST-RESORT route (see the ordering test below).
/// </summary>
public class GlyphNameToGidRouteTests
{
    private const int Dpi = 150;

    /// <summary>
    /// OTTO under /FontFile2. Its /Differences names small-cap glyphs (/e.sc),
    /// reachable only through the CFF charset.
    /// </summary>
    [Fact]
    public void OpenTypeCffUnderFontFile2_ResolvesNamesThroughTheCffCharset()
    {
        var path = FindCorpusFile("issue215.pdf");
        Assert.SkipWhen(path == null, "gitignored pdf.js corpus fixture not present (scripts/download-pdfjs-corpus.sh)."); // [requires: corpus:pdfjs]
        Assert.SkipUnless(MutoolReferenceRenderer.IsAvailable, "mutool not installed");

        using var doc = PdfDocument.Open(path!);
        using var excise = Render(doc);
        using var reference = MutoolReferenceRenderer.RenderPage(path!, 1, Dpi);
        reference.Should().NotBeNull();

        Ink(reference!).Should().BeGreaterThan(500,
            "mutool draws this page — otherwise the fixture, not excise, is at fault");
        Ink(excise).Should().BeGreaterThan(500,
            "an OTTO container under /FontFile2 must still reach the CFF charset; " +
            "gating that route on /FontFile3 left every /Differences name unresolved");
    }

    /// <summary>
    /// The /gNNNN route — and the one case in this work where excise is right
    /// and an oracle is WRONG.
    ///
    /// Measured at 150 dpi on issue13316_reduced.pdf:
    ///   excise 1959, pdftocairo 2453   (the CJK glyphs the font names)
    ///   mutool  677, ghostscript 520   (Latin "A C E F")
    ///
    /// All four extract the correct 开票通知单. mutool and Ghostscript then
    /// RENDER Latin, which is why "match the most-inked oracle" is not the goal
    /// here. The assertion is therefore against excise's own extraction being
    /// consistent with heavy CJK ink, corroborated by pdftocairo — not against
    /// mutool.
    /// </summary>
    [Fact]
    public void NumericGlyphNames_DrawTheGlyphsTheFontNames()
    {
        var path = FindCorpusFile("issue13316_reduced.pdf");
        Assert.SkipWhen(path == null, "gitignored pdf.js corpus fixture not present."); // [requires: corpus:pdfjs]

        using var doc = PdfDocument.Open(path!);
        using var excise = Render(doc);

        Ink(excise).Should().BeGreaterThan(1000,
            "/gNNNN names index glyphs directly; without that route every code " +
            "resolved to nothing and the page was blank while extraction was correct");
    }

    /// <summary>
    /// The ordering guard, and the reason this route is safe.
    ///
    /// /gNNNN is a producer CONVENTION, not a spec feature — a font may
    /// legitimately contain a glyph NAMED "g100" that is not index 100. The
    /// numeric route runs dead last, after the CFF charset, the Type 1 route and
    /// the font's own cmap, so a real name always wins.
    ///
    /// Uses a standard font whose names resolve normally: if the numeric route
    /// were consulted first it would map /g100 to glyph 100 and draw something
    /// different from the /A it should draw.
    /// </summary>
    [Fact]
    public void ARealGlyphName_IsNotOverriddenByTheNumericRoute()
    {
        var path = WriteTemp(NamedGlyphPdf("/A"));
        var numeric = WriteTemp(NamedGlyphPdf("/g100"));
        try
        {
            using var docA = PdfDocument.Open(path);
            using var byName = Render(docA);
            using var docN = PdfDocument.Open(numeric);
            using var byNumber = Render(docN);

            Ink(byName).Should().BeGreaterThan(0, "/A is a real glyph name and must draw");
            Ink(byName).Should().NotBe(Ink(byNumber),
                "if the numeric route ran ahead of the name route these would render " +
                "identically — the guard is that a real name is never reinterpreted");
        }
        finally { File.Delete(path); File.Delete(numeric); }
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static byte[] NamedGlyphPdf(string glyphName)
    {
        const string content = "BT /F1 48 Tf 20 100 Td (A) Tj ET";
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica " +
            $"/Encoding << /Type /Encoding /Differences [65 {glyphName}] >> >>\nendobj\n",
        };

        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(sb.Length); sb.Append(o); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static SKBitmap Render(PdfDocument doc) =>
        new SkiaRenderer().RenderPage(doc.GetPage(1),
            new RenderOptions { Dpi = Dpi, AntiAlias = false, BackgroundColor = SKColors.White });

    private static int Ink(SKBitmap b)
    {
        int n = 0;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
            {
                var c = b.GetPixel(x, y);
                if (c.Red < 200 || c.Green < 200 || c.Blue < 200) n++;
            }
        return n;
    }

    private static string? FindCorpusFile(string name)
    {
        var dir = Path.GetFullPath(Path.Combine("..", "..", "..", "..", "test-pdfs", "pdfjs"));
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string WriteTemp(byte[] bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), $"excise-892-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(p, bytes);
        return p;
    }
}
