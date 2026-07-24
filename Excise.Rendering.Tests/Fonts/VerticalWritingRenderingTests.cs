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
/// Render-path verification for vertical writing mode (#515 — /Identity-V
/// with /W2 and /DW2 metrics, PDF 32000-1:2008 §9.7.4.3/§9.4.4).
///
/// Before the fix the renderer had NO vertical layout at all: Identity-V
/// text was laid out exactly like Identity-H — left to right, advancing by
/// the horizontal /W widths — so a vertical CJK column rendered as a
/// horizontal line in the wrong place.
///
/// The fixture embeds DejaVuSans as CIDFontType2 with /CIDToGIDMap /Identity
/// so CID == GID: codes &lt;0024&gt;&lt;0025&gt; are GIDs 36/37 = 'A'/'B'
/// (from the font's own cmap table — the same ground truth
/// RegisteredCMapRenderingTests uses). At 72pt with the spec-default /DW2
/// [880 −1000], the two glyphs must stack one em (72pt) apart, horizontally
/// centered on the pen (default position vector vx = w0∕2), running DOWN
/// from the pen at (150, 250) on a 300×300pt page.
///
/// Ground truth is the spec's own arithmetic (assertions below are computed
/// by hand) with live pdftocairo/Ghostscript corroboration where installed
/// (no-self-oracle; reference-renderer agreement corroborates, the spec
/// fixture is the primary evidence).
/// </summary>
public class VerticalWritingRenderingTests
{
    private const int Dpi = 150;
    private const float Scale = Dpi / 72f;

    // DejaVuSans.ttf glyph ids from its cmap table: 'A' → 36, 'B' → 37.
    private const int GidA = 36;
    private const int GidB = 37;

    // DejaVuSans advances: 'A' 1401/2048 em ≈ 684, 'B' 1405/2048 em ≈ 686.
    private const int WidthA = 684;
    private const int WidthB = 686;

    [Fact]
    public void IdentityV_StacksGlyphsDownwardCenteredOnPen()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        using var bmp = Render(VerticalPdf(ttf!, "/Identity-V"));

        InkFraction(bmp).Should().BeGreaterThan(0.005,
            "the two embedded glyphs must draw — a blank page means vertical layout collapsed");

        var bounds = InkBounds(bmp);
        // Column, not a line: 'A' over 'B' spans ~2 em vertically but only
        // ~1 glyph width horizontally.
        bounds.Height.Should().BeGreaterThan((int)(bounds.Width * 1.7),
            "Identity-V must stack glyphs vertically — a wide short bbox means " +
            "the glyphs were laid out horizontally like Identity-H");

        // Default position vector vx = w0/2 (§9.7.4.3) centers each glyph on
        // the pen X: 150pt ± ~w0/2 (684/1000 · 72pt ≈ 49.2pt wide glyph cell,
        // 'A' ink slightly narrower). 150pt → 312.5px at 150 DPI.
        var centerX = (bounds.Left + bounds.Right) / 2f;
        centerX.Should().BeInRange(150 * Scale - 15, 150 * Scale + 15,
            "the default vx = w0/2 centers the vertical column on the pen X");

        // Vertical extent: glyph 1's baseline sits vy = 880/1000 em below the
        // pen (250 − 63.4 ≈ 186.6pt); glyph 2's one em lower (114.6pt).
        // 'A'/'B' cap height ≈ 0.73 em (~52pt) above their baselines, so ink
        // runs from ≈ 238.6pt down to ≈ 114.6pt → bitmap y ≈ 128..386px.
        bounds.Top.Should().BeInRange((int)((300 - 245) * Scale), (int)((300 - 230) * Scale),
            "the first glyph must hang below the pen via the position vector (vy = 880)");
        bounds.Bottom.Should().BeInRange((int)((300 - 122) * Scale), (int)((300 - 107) * Scale),
            "the second glyph must sit one em (72pt) below the first (w1y = −1000)");
    }

    [Fact]
    public void IdentityV_Control_DiffersMateriallyFromIdentityH()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        using var vertical = Render(VerticalPdf(ttf!, "/Identity-V"));
        using var horizontal = Render(VerticalPdf(ttf!, "/Identity-H"));

        var hBounds = InkBounds(horizontal);
        hBounds.Width.Should().BeGreaterThan((int)(hBounds.Height * 1.2),
            "the Identity-H control lays the same codes out horizontally");

        var report = DifferentialMetrics.Compare(vertical, horizontal);
        report.DifferingPixelFraction.Should().BeGreaterThan(0.01,
            "vertical and horizontal layout of the same codes must not look alike — " +
            "if these renders match, /Identity-V is not actually changing layout");
    }

    [Fact] // /W2 changes the per-CID vertical displacement (§9.7.4.3)
    public void IdentityV_W2_ControlsInterGlyphGap()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        using var defaultGap = Render(VerticalPdf(ttf!, "/Identity-V"));
        // w1y = −2000 for GID/CID 36 ('A'): the gap below 'A' doubles from
        // one em (72pt) to two (144pt); vx/vy stay at the defaults.
        using var doubleGap = Render(VerticalPdf(ttf!, "/Identity-V",
            cidExtras: $"/W2[{GidA}[-2000 342 880]]"));

        var d = InkBounds(defaultGap);
        var w = InkBounds(doubleGap);
        var growth = (w.Height - d.Height) / Scale;
        growth.Should().BeInRange(60, 84,
            "doubling w1y for the first glyph pushes the second one em (72pt) further down");
    }

    [Fact]
    public void IdentityV_MatchesLivePdftocairo()
    {
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed.");
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        AssertMatchesReference(
            VerticalPdf(ttf!, "/Identity-V"),
            path => PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi),
            "pdftocairo");
    }

    [Fact]
    public void IdentityV_MatchesLiveGhostscript()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed.");
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        AssertMatchesReference(
            VerticalPdf(ttf!, "/Identity-V"),
            path => GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi),
            "ghostscript");
    }

    private static void AssertMatchesReference(
        byte[] pdf, Func<string, SKBitmap?> renderReference, string referenceName)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-vertical-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            using var excise = Render(pdf);
            using var reference = renderReference(path);
            Assert.SkipWhen(reference == null, $"{referenceName} declined to render the fixture.");

            using var aligned = DifferentialMetrics.ResizeMatch(excise, reference!.Width, reference.Height);
            var report = DifferentialMetrics.Compare(aligned, reference);
            // Tighter than the corpus-wide 0.10/32 gate: the pre-fix
            // horizontal mislayout differs from the reference by >2.5% on
            // this sparse fixture, so 2%/MAE 8 separates fixed from broken.
            report.DifferingPixelFraction.Should().BeLessThan(0.02,
                $"excise's Identity-V render must stack the same glyphs {referenceName} stacks " +
                $"(differing={report.DifferingPixelFraction:P2}, MAE={report.MeanAbsoluteError:F1})");
            report.MeanAbsoluteError.Should().BeLessThan(8.0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ==== fixture =============================================================

    /// <summary>
    /// Type0 font over embedded CIDFontType2 DejaVuSans with
    /// /CIDToGIDMap /Identity (CID == GID), drawing codes
    /// &lt;0024&gt;&lt;0025&gt; (GIDs 36/37 = 'A'/'B') at 72pt from a pen at
    /// (150, 250) on a 300×300pt page.
    /// </summary>
    private static byte[] VerticalPdf(byte[] ttf, string encoding, string cidExtras = "")
    {
        var pdf = new MinimalPdf();
        pdf.Add("<< /Type /Catalog /Pages 2 0 R >>");                                        // 1
        pdf.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");                                // 2
        pdf.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R "
              + "/Resources << /Font << /F1 5 0 R >> >> >>");                                // 3
        pdf.Add("<< >>", Encoding.ASCII.GetBytes("BT /F1 72 Tf 150 250 Td <00240025> Tj ET")); // 4
        pdf.Add("<< /Type /Font /Subtype /Type0 /BaseFont /TestFont-V "
              + $"/Encoding {encoding} /DescendantFonts [6 0 R] >>");                        // 5
        pdf.Add("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /TestFont "
              + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
              + "/FontDescriptor 7 0 R /CIDToGIDMap /Identity "
              + $"/DW 1000 /W [{GidA} [{WidthA} {WidthB}]] {cidExtras} >>");                 // 6
        pdf.Add("<< /Type /FontDescriptor /FontName /TestFont /Flags 4 "
              + "/FontBBox [-1200 -500 2500 1200] /ItalicAngle 0 /Ascent 900 /Descent -250 "
              + "/CapHeight 700 /StemV 90 /FontFile2 8 0 R >>");                             // 7
        pdf.Add("<< >>", ttf);                                                               // 8
        return pdf.Build(1);
    }

    // ==== rendering + measurement (same shape as RegisteredCMapRenderingTests) ====

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

    // Minimal PDF assembler (same shape as RegisteredCMapRenderingTests'):
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
