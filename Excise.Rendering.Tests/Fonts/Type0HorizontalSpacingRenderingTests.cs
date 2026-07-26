using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Fonts;

/// <summary>
/// Render-path verification for horizontal Type0/CID character/word spacing
/// (#734, PDF 32000-1:2008 §9.3.2/§9.3.3/§9.4.4).
///
/// Before the fix, <c>SkiaRenderer.RenderCidBytes</c> advanced the horizontal
/// text matrix by summed <c>/W</c> glyph widths ONLY — Tc (character
/// spacing) and Tw (word spacing) were never added, unlike the simple-font
/// path (<c>RenderText</c>) and the #515 vertical Type0 path, both of which
/// apply Tc/Tw per spec. On CJK/Type0 pages with a non-zero Tc, glyphs
/// rendered progressively LEFT of where extraction
/// (<c>TextExtractor</c>/<c>ContentStreamParser</c>, fixed for the same bug
/// in PR #759) and reference renderers place them — the renderer's advance
/// was strictly smaller than the correct one.
///
/// Per §9.4.4: <c>tx = ((w0 − Tj/1000)·Tfs + Tc + Tw)·Th</c>. Tw fires only
/// on a SINGLE-byte code 32 (§9.3.3) — a 2-byte &lt;0020&gt; in an
/// Identity-H run must not trigger it. The fix mirrors the #515 vertical
/// path: accumulate Tc (every glyph) and Tw (single-byte code 32 only, using
/// the same <c>DecodeDetailed</c> byte-length info the vertical path uses)
/// into the same per-glyph cursor that positions each glyph, then reuse that
/// cursor for the final text-matrix advance — so drawn glyph positions and
/// the pen advance cannot drift apart.
///
/// Ground truth is the spec's own arithmetic (assertions below are computed
/// by hand) with live pdftocairo/Ghostscript corroboration where installed
/// (no-self-oracle: excise's own extractor is never used as the oracle for
/// what excise's own renderer draws — see CLAUDE.md's "never let excise
/// verify excise").
/// </summary>
public class Type0HorizontalSpacingRenderingTests
{
    private const int Dpi = 150;
    private const float Scale = Dpi / 72f;

    // DejaVuSans.ttf glyph ids from its cmap/post table (standard Macintosh
    // glyph order): 'A' → 36, 'B' → 37, space → 3 (same ground truth
    // VerticalWritingRenderingTests uses for 'A'/'B').
    private const int GidSpace = 3;
    private const int GidA = 36;
    private const int GidB = 37;

    // DejaVuSans advances: 'A' 1401/2048 em ≈ 684, 'B' 1405/2048 em ≈ 686.
    private const int WidthA = 684;
    private const int WidthB = 686;
    private const int WidthSpace = 300;

    [Fact]
    public void HorizontalTc_ControlsInterGlyphGap()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        using var noTc = Render(HorizontalTcPdf(ttf!, tc: 0));
        using var withTc = Render(HorizontalTcPdf(ttf!, tc: 20));

        InkFraction(withTc).Should().BeGreaterThan(0.002,
            "both embedded glyphs must draw — a blank page means the fixture broke");

        var d = InkBounds(noTc);
        var w = InkBounds(withTc);
        float growthPx = w.Width - d.Width;
        var expectedPx = 20 * Scale; // Tc is unscaled text-space units, Th=100%
        growthPx.Should().BeInRange(expectedPx - 12, expectedPx + 12,
            "Tc=20 must widen the ink span by ~20pt (§9.4.4) — before the fix " +
            "the renderer ignored Tc entirely and this span would not grow");
    }

    [Fact]
    public void HorizontalTw_SingleByteCode32_ControlsGap()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        using var noTw = Render(HorizontalTwPdf(ttf!, tw: 0));
        using var withTw = Render(HorizontalTwPdf(ttf!, tw: 30));

        var d = InkBounds(noTw);
        var w = InkBounds(withTw);
        float growthPx = w.Width - d.Width;
        var expectedPx = 30 * Scale; // Tw is unscaled text-space units, Th=100%
        growthPx.Should().BeInRange(expectedPx - 12, expectedPx + 12,
            "Tw=30 on a SINGLE-byte code 32 (§9.3.3) must widen the span between " +
            "the two 'A' glyphs by ~30pt — before the fix Tw was never applied " +
            "in the horizontal renderer");
    }

    [Fact]
    public void HorizontalTc_AppliesInsideHorizontalScaling_NotOutside()
    {
        // The #734 family's core interaction (mirrors the extractor-side
        // Type0SpacingAdvanceTests.Type0_SpacingIsScaledByHorizontalScaling,
        // PR #759): per §9.4.4, tx = (w0/1000·Tfs + Tc + Tw)·Th — Tc sits
        // INSIDE the Th (horizontal-scaling) factor. Every other test in this
        // file uses the default Th=100, where `width *= Th/100` is a no-op
        // and would pass even if a regression put Tc OUTSIDE Th (i.e.
        // `w0·Tfs·Th + Tc` instead of `(w0·Tfs + Tc)·Th`). Tz=50 (Th=0.5)
        // makes the two formulas diverge, so this is the one case that
        // actually discriminates "inside" from "outside".
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        using var noTc = Render(HorizontalTcPdf(ttf!, tc: 0, tz: 50));
        using var withTc = Render(HorizontalTcPdf(ttf!, tc: 20, tz: 50));

        var d = InkBounds(noTc);
        var w = InkBounds(withTc);
        float growthPx = w.Width - d.Width;
        // Correct (Tc inside Th): growth = Tc · Th = 20 · 0.5 = 10pt.
        // Bug (Tc outside Th):    growth = Tc      = 20pt — 2× too large,
        // clearly outside this tolerance band.
        var expectedPx = 20 * 0.5f * Scale;
        growthPx.Should().BeInRange(expectedPx - 8, expectedPx + 8,
            "Tc must be scaled by Th together with the glyph width (§9.4.4), " +
            "not added after Th is applied — growth should be Tc·Th = 10pt, not Tc = 20pt");
    }

    [Fact]
    public void HorizontalTc_UnderHorizontalScaling_MatchesLivePdftocairo()
    {
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed.");
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        AssertMatchesReference(
            HorizontalTcPdf(ttf!, tc: 20, tz: 50),
            path => PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi),
            "pdftocairo");
    }

    [Fact]
    public void HorizontalTc_UnderHorizontalScaling_MatchesLiveGhostscript()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed.");
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        AssertMatchesReference(
            HorizontalTcPdf(ttf!, tc: 20, tz: 50),
            path => GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi),
            "ghostscript");
    }

    [Fact]
    public void IdentityH_TwoByteCode32_DoesNotFireWordSpacing()
    {
        // §9.3.3 control: Identity-H's <0020> is a 2-byte code, so it must
        // NOT fire Tw — mirrors Type0SpacingAdvanceTests'
        // Type0_TwoByteCode32_DoesNotFireWordSpacing but for the renderer.
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        using var noTw = Render(IdentityHTwoByteSpacePdf(ttf!, tw: 0));
        using var withTw = Render(IdentityHTwoByteSpacePdf(ttf!, tw: 30));

        var d = InkBounds(noTw);
        var w = InkBounds(withTw);
        Math.Abs(w.Width - d.Width).Should().BeLessThan(4,
            "a 2-byte <0020> in Identity-H must not fire word spacing (§9.3.3) — " +
            "the ink span must stay put even with Tw=30");
    }

    [Fact]
    public void HorizontalTc_MatchesLivePdftocairo()
    {
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed.");
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        AssertMatchesReference(
            HorizontalTcPdf(ttf!, tc: 20),
            path => PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi),
            "pdftocairo");
    }

    [Fact]
    public void HorizontalTc_MatchesLiveGhostscript()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed.");
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        AssertMatchesReference(
            HorizontalTcPdf(ttf!, tc: 20),
            path => GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi),
            "ghostscript");
    }

    [Fact]
    public void HorizontalTw_MatchesLivePdftocairo()
    {
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed.");
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        AssertMatchesReference(
            HorizontalTwPdf(ttf!, tw: 30),
            path => PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi),
            "pdftocairo");
    }

    [Fact]
    public void HorizontalTw_MatchesLiveGhostscript()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed.");
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        AssertMatchesReference(
            HorizontalTwPdf(ttf!, tw: 30),
            path => GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi),
            "ghostscript");
    }

    private static void AssertMatchesReference(
        byte[] pdf, Func<string, SKBitmap?> renderReference, string referenceName)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-hspacing-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            using var excise = Render(pdf);
            using var reference = renderReference(path);
            Assert.SkipWhen(reference == null, $"{referenceName} declined to render the fixture.");

            using var aligned = DifferentialMetrics.ResizeMatch(excise, reference!.Width, reference.Height);
            var report = DifferentialMetrics.Compare(aligned, reference);
            // Tighter than the corpus-wide 0.10/32 gate, matching #515's
            // vertical-path fixture-specific gate. Measured directly (by
            // temporarily reverting the fix and re-running this exact
            // fixture): PRE-fix differing-pixel-fraction vs. these two
            // references was 1.78%-2.02% (MAE 4.4-4.9) on both the Tc and Tw
            // cases; POST-fix it drops to 0.00%-0.25% (MAE 0.0-0.4) — the
            // residual is antialiasing noise, not mislayout. 1%/MAE 2
            // sits comfortably between "broken" and "fixed" with an order of
            // magnitude of margin on both sides.
            report.DifferingPixelFraction.Should().BeLessThan(0.01,
                $"excise's horizontal Type0 render must place glyphs where {referenceName} " +
                $"places them under non-zero Tc/Tw (differing={report.DifferingPixelFraction:P2}, " +
                $"MAE={report.MeanAbsoluteError:F1})");
            report.MeanAbsoluteError.Should().BeLessThan(2.0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ==== fixtures =============================================================

    /// <summary>
    /// Type0/Identity-H over embedded CIDFontType2 DejaVuSans
    /// (/CIDToGIDMap /Identity), drawing codes &lt;0024&gt;&lt;0025&gt;
    /// (GIDs 36/37 = 'A'/'B') at 72pt from a pen at (30, 150) on a 300×300pt
    /// page, with character spacing <paramref name="tc"/> and horizontal
    /// scaling <paramref name="tz"/> (Tz, default 100 = no-op).
    /// </summary>
    private static byte[] HorizontalTcPdf(byte[] ttf, float tc, float tz = 100)
    {
        var pdf = new MinimalPdf();
        pdf.Add("<< /Type /Catalog /Pages 2 0 R >>");                                        // 1
        pdf.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");                                // 2
        pdf.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R "
              + "/Resources << /Font << /F1 5 0 R >> >> >>");                                // 3
        pdf.Add("<< >>", Encoding.ASCII.GetBytes(
            $"BT /F1 72 Tf {Fmt(tz)} Tz {Fmt(tc)} Tc 30 150 Td <00240025> Tj ET"));           // 4
        pdf.Add("<< /Type /Font /Subtype /Type0 /BaseFont /TestFont-H "
              + "/Encoding /Identity-H /DescendantFonts [6 0 R] >>");                        // 5
        pdf.Add("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /TestFont "
              + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
              + "/FontDescriptor 7 0 R /CIDToGIDMap /Identity "
              + $"/DW 1000 /W [{GidA} [{WidthA} {WidthB}]] >>");                             // 6
        pdf.Add("<< /Type /FontDescriptor /FontName /TestFont /Flags 4 "
              + "/FontBBox [-1200 -500 2500 1200] /ItalicAngle 0 /Ascent 900 /Descent -250 "
              + "/CapHeight 700 /StemV 90 /FontFile2 8 0 R >>");                             // 7
        pdf.Add("<< >>", ttf);                                                               // 8
        return pdf.Build(1);
    }

    /// <summary>
    /// Same fixture as <see cref="HorizontalTcPdf"/> but with a 2-byte
    /// &lt;0020&gt; (GID 3 = space) between two 'A' glyphs, to prove word
    /// spacing does NOT fire on a 2-byte code 32 (§9.3.3).
    /// </summary>
    private static byte[] IdentityHTwoByteSpacePdf(byte[] ttf, float tw)
    {
        var pdf = new MinimalPdf();
        pdf.Add("<< /Type /Catalog /Pages 2 0 R >>");                                        // 1
        pdf.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");                                // 2
        pdf.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R "
              + "/Resources << /Font << /F1 5 0 R >> >> >>");                                // 3
        pdf.Add("<< >>", Encoding.ASCII.GetBytes(
            // <0024 0020 0024> = 'A' <2-byte space> 'A' — hex string
            // whitespace is insignificant, written compact here.
            $"BT /F1 72 Tf {Fmt(tw)} Tw 30 150 Td <002400200024> Tj ET"));                    // 4
        pdf.Add("<< /Type /Font /Subtype /Type0 /BaseFont /TestFont-H2 "
              + "/Encoding /Identity-H /DescendantFonts [6 0 R] >>");                        // 5
        pdf.Add("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /TestFont "
              + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
              + "/FontDescriptor 7 0 R /CIDToGIDMap /Identity "
              + $"/DW 1000 /W [{GidSpace} [{WidthSpace}] {GidA} [{WidthA}]] >>");             // 6
        pdf.Add("<< /Type /FontDescriptor /FontName /TestFont /Flags 4 "
              + "/FontBBox [-1200 -500 2500 1200] /ItalicAngle 0 /Ascent 900 /Descent -250 "
              + "/CapHeight 700 /StemV 90 /FontFile2 8 0 R >>");                             // 7
        pdf.Add("<< >>", ttf);                                                               // 8
        return pdf.Build(1);
    }

    /// <summary>
    /// Type0 font whose /Encoding is an embedded CMap stream with a UNIFORM
    /// 1-byte codespace (mirrors Type0SpacingAdvanceTests.BuildOneByteCodespacePdf)
    /// so a genuinely single-byte code 32 is reachable: code 0x41 → CID 36
    /// ('A'), code 0x20 → CID 3 (space, blank ink). Draws 'A' &lt;space&gt; 'A'
    /// at 72pt from a pen at (30, 150) on a 300×300pt page, with word
    /// spacing <paramref name="tw"/>.
    /// </summary>
    private static byte[] HorizontalTwPdf(byte[] ttf, float tw)
    {
        var encodingCmap =
            "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
            "2 begincidrange\n<41> <41> 36\n<20> <20> 3\nendcidrange\n" +
            "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n";

        var pdf = new MinimalPdf();
        pdf.Add("<< /Type /Catalog /Pages 2 0 R >>");                                        // 1
        pdf.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");                                // 2
        pdf.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R "
              + "/Resources << /Font << /F1 5 0 R >> >> >>");                                // 3
        pdf.Add("<< >>", Encoding.ASCII.GetBytes(
            $"BT /F1 72 Tf {Fmt(tw)} Tw 30 150 Td <412041> Tj ET"));                         // 4
        pdf.Add("<< /Type /Font /Subtype /Type0 /BaseFont /TestFont-1B "
              + "/Encoding 9 0 R /DescendantFonts [6 0 R] >>");                              // 5
        pdf.Add("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /TestFont "
              + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
              + "/FontDescriptor 7 0 R /CIDToGIDMap /Identity "
              + $"/DW 1000 /W [{GidSpace} [{WidthSpace}] {GidA} [{WidthA}]] >>");             // 6
        pdf.Add("<< /Type /FontDescriptor /FontName /TestFont /Flags 4 "
              + "/FontBBox [-1200 -500 2500 1200] /ItalicAngle 0 /Ascent 900 /Descent -250 "
              + "/CapHeight 700 /StemV 90 /FontFile2 8 0 R >>");                             // 7
        pdf.Add("<< >>", ttf);                                                               // 8
        pdf.Add("<< /Type /CMap /CMapName /Test-Encoding >>", Encoding.ASCII.GetBytes(encodingCmap)); // 9
        return pdf.Build(1);
    }

    private static string Fmt(float v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ==== rendering + measurement (same shape as VerticalWritingRenderingTests) ====

    private static SKBitmap Render(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return new SkiaRenderer().RenderPage(
            doc.GetPage(1), new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White });
    }

    private static bool IsInk(SKColor p) => p.Red < 128 && p.Green < 128 && p.Blue < 128;

    private static double InkFraction(SKBitmap b)
    {
        long ink = 0;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
                if (IsInk(b.GetPixel(x, y))) ink++;
        return (double)ink / (b.Width * (long)b.Height);
    }

    private static SKRectI InkBounds(SKBitmap b)
    {
        int minX = b.Width, minY = b.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
                if (IsInk(b.GetPixel(x, y)))
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        return maxX < 0 ? SKRectI.Empty : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }

    private static byte[]? LoadFixtureFont(string name)
    {
        var path = FindRepoFile("Excise.Core.Tests", "Fixtures", "Fonts", name);
        return path == null ? null : File.ReadAllBytes(path);
    }

    private static string? FindRepoFile(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    // Minimal PDF assembler (same shape as VerticalWritingRenderingTests'):
    // sequential objects, auto /Length for streams, classic xref + trailer.
    private sealed class MinimalPdf
    {
        private readonly List<(string Dict, byte[]? Stream)> _objs = new();

        public int Add(string dict, byte[]? stream = null)
        {
            _objs.Add((dict, stream));
            return _objs.Count;
        }

        public byte[] Build(int rootObj)
        {
            using var ms = new MemoryStream();
            void W(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }

            W("%PDF-1.7\n");
            var offsets = new long[_objs.Count + 1];
            for (int i = 0; i < _objs.Count; i++)
            {
                int n = i + 1;
                offsets[n] = ms.Position;
                var (dict, stream) = _objs[i];
                if (stream != null)
                {
                    int close = dict.LastIndexOf(">>", StringComparison.Ordinal);
                    dict = dict.Substring(0, close) + $" /Length {stream.Length} " + dict.Substring(close);
                }
                W($"{n} 0 obj\n{dict}\n");
                if (stream != null)
                {
                    W("stream\n");
                    ms.Write(stream, 0, stream.Length);
                    W("\nendstream\n");
                }
                W("endobj\n");
            }

            long xref = ms.Position;
            W($"xref\n0 {_objs.Count + 1}\n0000000000 65535 f \n");
            for (int n = 1; n <= _objs.Count; n++)
                W($"{offsets[n]:D10} 00000 n \n");
            W($"trailer\n<< /Root {rootObj} 0 R /Size {_objs.Count + 1} >>\nstartxref\n{xref}\n%%EOF");
            return ms.ToArray();
        }
    }
}
