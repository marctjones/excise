using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Rendering;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Fonts;

/// <summary>
/// Direct, per-font-class rendering coverage for the font model (#512). These
/// tests render ONE font feature in isolation from a minimal generated PDF and
/// assert an OBJECTIVE structural property of the raster (ink present/absent,
/// relative position, size scaling, colour, weight, encoding remap) — never a
/// pixel-exact baseline against excise's own prior output.
///
/// Design notes (learned the hard way):
/// - Shape-sensitive assertions use EMBEDDED fonts so the outline is
///   deterministic. Comparing a NON-embedded font's shape across renderers is
///   invalid because each renderer substitutes a different system font (Apple
///   Helvetica vs URW Nimbus Sans etc.) — see #710.
/// - Assertions are structural (does the glyph paint? is it to the right? is it
///   bigger? is it red? is it heavier?), which is robust to antialiasing/gamma
///   differences and does not make excise its own oracle for glyph shape.
/// </summary>
public class FontRenderingMatrixTests
{
    private const int Dpi = 150;

    // ---- font classes: does each embedded program actually rasterize? --------

    [Fact]
    public void EmbeddedTrueType_RendersGlyphInk()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");
        var pdf = SimpleEmbeddedFontPdf(ttf!, FontFileKind.TrueType, "BT /F1 48 Tf 20 40 Td (A) Tj ET");
        using var bmp = Render(pdf);
        InkFraction(bmp).Should().BeGreaterThan(0.002,
            "an embedded TrueType (/FontFile2) glyph must rasterize visible ink");
    }

    [Fact]
    public void EmbeddedCff_RendersGlyphInk()
    {
        var cff = LoadFixtureFont("Inconsolata.cff");
        Assert.SkipWhen(cff == null, "Inconsolata.cff fixture missing.");
        var pdf = SimpleEmbeddedFontPdf(cff!, FontFileKind.Cff, "BT /F1 48 Tf 20 40 Td (A) Tj ET");
        using var bmp = Render(pdf);
        InkFraction(bmp).Should().BeGreaterThan(0.002,
            "an embedded CFF/Type1C (/FontFile3) glyph must rasterize visible ink");
    }

    [Fact]
    public void EmbeddedOpenType_RendersGlyphInk()
    {
        var otf = LoadFixtureFont("LibertinusSerif-Regular.otf");
        Assert.SkipWhen(otf == null, "LibertinusSerif-Regular.otf fixture missing.");
        var pdf = SimpleEmbeddedFontPdf(otf!, FontFileKind.OpenType, "BT /F1 48 Tf 20 40 Td (A) Tj ET");
        using var bmp = Render(pdf);
        InkFraction(bmp).Should().BeGreaterThan(0.002,
            "an embedded OpenType (/FontFile3 /Subtype /OpenType) glyph must rasterize visible ink");
    }

    [Theory]
    [InlineData("Helvetica")]
    [InlineData("Times-Roman")]
    [InlineData("Courier")]
    [InlineData("Symbol")]
    [InlineData("ZapfDingbats")]
    public void Base14NonEmbedded_RendersGlyphInk(string baseFont)
    {
        // Non-embedded standard-14 fonts: excise must substitute and still paint.
        // Ink-presence only — glyph SHAPE is substitution-dependent (#710), so we
        // deliberately do not compare shape.
        var text = baseFont is "Symbol" or "ZapfDingbats" ? "(ABC)" : "(Ag)";
        var pdf = Base14Pdf(baseFont, $"BT /F1 48 Tf 20 40 Td {text} Tj ET");
        using var bmp = Render(pdf);
        InkFraction(bmp).Should().BeGreaterThan(0.001,
            $"the standard-14 font {baseFont} must be substituted and painted");
    }

    // ---- render modes --------------------------------------------------------

    [Fact]
    public void InvisibleRenderMode_ProducesNoInk()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");
        // Tr 3 = invisible text (used for OCR layers). Must paint nothing.
        var pdf = SimpleEmbeddedFontPdf(ttf!, FontFileKind.TrueType, "BT 3 Tr /F1 48 Tf 20 40 Td (A) Tj ET");
        using var bmp = Render(pdf);
        InkFraction(bmp).Should().BeLessThan(0.0002,
            "text render mode 3 (invisible) must not paint any glyph ink");
    }

    [Fact]
    public void StrokeRenderMode_IsHollowVersusFill()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");
        // A large solid letter: fill (Tr 0) paints the interior; stroke (Tr 1)
        // paints only the outline, so it has strictly less ink.
        using var filled = Render(SimpleEmbeddedFontPdf(ttf!, FontFileKind.TrueType,
            "BT 0 Tr /F1 120 Tf 20 30 Td (B) Tj ET"));
        using var stroked = Render(SimpleEmbeddedFontPdf(ttf!, FontFileKind.TrueType,
            "BT 1 Tr 1 w /F1 120 Tf 20 30 Td (B) Tj ET"));
        var fi = InkFraction(filled);
        var si = InkFraction(stroked);
        fi.Should().BeGreaterThan(0.005, "fill mode must paint the glyph body");
        si.Should().BeGreaterThan(0.0005, "stroke mode must paint the glyph outline");
        si.Should().BeLessThan(fi * 0.85,
            $"a stroked outline must have less ink than a solid fill (fill={fi:P2}, stroke={si:P2})");
    }

    // ---- size / matrix scaling ----------------------------------------------

    [Fact]
    public void LargerFontSize_ScalesInkBoundingBox()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");
        using var small = Render(SimpleEmbeddedFontPdf(ttf!, FontFileKind.TrueType, "BT /F1 24 Tf 20 40 Td (H) Tj ET"));
        using var large = Render(SimpleEmbeddedFontPdf(ttf!, FontFileKind.TrueType, "BT /F1 48 Tf 20 40 Td (H) Tj ET"));
        var hs = InkBounds(small).Height;
        var hl = InkBounds(large).Height;
        hs.Should().BeGreaterThan(0);
        // Doubling Tf must roughly double the painted glyph height (allow AA slack).
        ((double)hl / hs).Should().BeInRange(1.7, 2.3,
            $"doubling the font size must roughly double the glyph height (small={hs}px, large={hl}px)");
    }

    // ---- colour --------------------------------------------------------------

    [Fact]
    public void FillColor_IsAppliedToGlyph()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");
        var pdf = SimpleEmbeddedFontPdf(ttf!, FontFileKind.TrueType, "BT 1 0 0 rg /F1 80 Tf 20 30 Td (H) Tj ET");
        using var bmp = Render(pdf);
        var red = CountPixels(bmp, p => p.Red > 150 && p.Green < 90 && p.Blue < 90);
        red.Should().BeGreaterThan(50, "glyph fill must honour the non-stroking colour (red rg)");
    }

    // ---- positioning ---------------------------------------------------------

    [Fact]
    public void HorizontalAdvance_SecondGlyphIsRightOfFirst()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");
        // Two well-separated glyphs; the ink must occupy two distinct horizontal
        // bands with the second clearly to the right — proves advance-width use.
        var pdf = SimpleEmbeddedFontPdf(ttf!, FontFileKind.TrueType, "BT /F1 60 Tf 20 40 Td (I   I) Tj ET");
        using var bmp = Render(pdf);
        var cols = InkColumns(bmp);
        cols.Count.Should().BeGreaterThanOrEqualTo(2,
            "two glyphs separated by spaces must paint two separated horizontal ink bands");
        cols[^1].Should().BeGreaterThan(cols[0], "the later glyph must advance to the right");
    }

    // ---- weight variation ----------------------------------------------------

    [Fact]
    public void BoldVariant_IsHeavierThanRegular()
    {
        // Standard-14 weight variation: Helvetica-Bold must paint more ink than
        // Helvetica at the same size. Uses ink MASS (not shape), so it is robust
        // to whatever bold face the platform substitutes.
        using var regular = Render(Base14Pdf("Helvetica", "BT /F1 60 Tf 20 40 Td (Weight) Tj ET"));
        using var bold = Render(Base14Pdf("Helvetica-Bold", "BT /F1 60 Tf 20 40 Td (Weight) Tj ET"));
        var ir = InkFraction(regular);
        var ib = InkFraction(bold);
        ir.Should().BeGreaterThan(0.001);
        ib.Should().BeGreaterThan(ir * 1.05,
            $"the bold variant must paint more ink than regular (regular={ir:P2}, bold={ib:P2})");
    }

    // ---- encoding ------------------------------------------------------------

    [Fact]
    public void EncodingDifferences_RemapsCodeToNamedGlyph()
    {
        var cff = LoadFixtureFont("Inconsolata.cff");
        Assert.SkipWhen(cff == null, "Inconsolata.cff fixture missing.");
        // Draw code 65 under WinAnsi ('A') vs a Differences array that remaps 65
        // -> /space (a blank glyph). The remapped render must have strictly less
        // ink, proving the /Encoding /Differences array is applied.
        using var asA = Render(SimpleEmbeddedFontPdf(cff!, FontFileKind.Cff,
            "BT /F1 80 Tf 20 30 Td (A) Tj ET", encoding: "/WinAnsiEncoding"));
        using var asSpace = Render(SimpleEmbeddedFontPdf(cff!, FontFileKind.Cff,
            "BT /F1 80 Tf 20 30 Td (A) Tj ET",
            encoding: "<< /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [65 /space] >>"));
        var inkA = InkFraction(asA);
        var inkSpace = InkFraction(asSpace);
        inkA.Should().BeGreaterThan(0.003, "code 65 under WinAnsi must draw the 'A' glyph");
        inkSpace.Should().BeLessThan(inkA * 0.4,
            $"remapping code 65 -> /space via /Differences must draw (almost) no ink (A={inkA:P2}, space={inkSpace:P2})");
    }

    // ---- composite / CID -----------------------------------------------------

    [Fact]
    public void Type0IdentityH_EmbeddedCidFontType2_RendersInk()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");
        // A composite Type0 font: /Encoding /Identity-H, CIDFontType2 descendant,
        // /CIDToGIDMap /Identity. Two-byte codes are GIDs directly. Draw a run of
        // low GIDs (which in DejaVuSans include real glyphs) and assert ink.
        var pdf = Type0IdentityHPdf(ttf!);
        using var bmp = Render(pdf);
        InkFraction(bmp).Should().BeGreaterThan(0.002,
            "a Type0 Identity-H font with an embedded CIDFontType2 program must rasterize glyph ink");
    }

    // ==== fixtures ============================================================

    private enum FontFileKind { TrueType, Cff, OpenType }

    private static byte[] SimpleEmbeddedFontPdf(
        byte[] program, FontFileKind kind, string content, string encoding = "/WinAnsiEncoding")
    {
        var pdf = new MinimalPdf();
        pdf.Add("<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        pdf.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 120] /Contents 4 0 R "
              + "/Resources << /Font << /F1 5 0 R >> >> >>");
        pdf.Add("<< >>", Encoding.ASCII.GetBytes(content));
        var subtype = kind == FontFileKind.TrueType ? "/TrueType" : "/Type1";
        pdf.Add($"<< /Type /Font /Subtype {subtype} /BaseFont /TestFont /FirstChar 32 /LastChar 126 "
              + $"/Widths [{UniformWidths(95, 600)}] /FontDescriptor 6 0 R /Encoding {encoding} >>");
        var ffKey = kind == FontFileKind.TrueType ? "/FontFile2" : "/FontFile3";
        pdf.Add("<< /Type /FontDescriptor /FontName /TestFont /Flags 32 "
              + "/FontBBox [-1200 -500 2500 1200] /ItalicAngle 0 /Ascent 900 /Descent -250 "
              + $"/CapHeight 700 /StemV 90 /MissingWidth 600 {ffKey} 7 0 R >>");
        var ffDict = kind switch
        {
            FontFileKind.Cff => "<< /Subtype /Type1C >>",
            FontFileKind.OpenType => "<< /Subtype /OpenType >>",
            _ => "<< >>",
        };
        pdf.Add(ffDict, program);
        return pdf.Build(1);
    }

    private static byte[] Base14Pdf(string baseFont, string content)
    {
        var pdf = new MinimalPdf();
        pdf.Add("<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        pdf.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 120] /Contents 4 0 R "
              + "/Resources << /Font << /F1 5 0 R >> >> >>");
        pdf.Add("<< >>", Encoding.ASCII.GetBytes(content));
        pdf.Add($"<< /Type /Font /Subtype /Type1 /BaseFont /{baseFont} >>");
        return pdf.Build(1);
    }

    private static byte[] Type0IdentityHPdf(byte[] ttf)
    {
        // Two-byte codes 0x0003..0x0032 (GIDs, Identity map). DejaVuSans has real
        // glyphs across that low range, so several paint ink.
        var sb = new StringBuilder("BT /F1 40 Tf 20 40 Td <");
        for (int gid = 3; gid <= 0x32; gid++) sb.Append(gid.ToString("X4"));
        sb.Append("> Tj ET");
        var content = sb.ToString();

        var pdf = new MinimalPdf();
        pdf.Add("<< /Type /Catalog /Pages 2 0 R >>");                                       // 1
        pdf.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");                                // 2
        pdf.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 620 120] /Contents 4 0 R "
              + "/Resources << /Font << /F1 5 0 R >> >> >>");                                // 3
        pdf.Add("<< >>", Encoding.ASCII.GetBytes(content));                                  // 4
        pdf.Add("<< /Type /Font /Subtype /Type0 /BaseFont /TestFont-Identity "
              + "/Encoding /Identity-H /DescendantFonts [6 0 R] >>");                        // 5
        pdf.Add("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /TestFont "
              + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
              + "/FontDescriptor 7 0 R /CIDToGIDMap /Identity /DW 1000 >>");                 // 6
        pdf.Add("<< /Type /FontDescriptor /FontName /TestFont /Flags 4 "
              + "/FontBBox [-1200 -500 2500 1200] /ItalicAngle 0 /Ascent 900 /Descent -250 "
              + "/CapHeight 700 /StemV 90 /FontFile2 8 0 R >>");                             // 7
        pdf.Add("<< >>", ttf);                                                               // 8
        return pdf.Build(1);
    }

    private static string UniformWidths(int count, int w) =>
        string.Join(' ', Enumerable.Repeat(w, count));

    // ==== rendering + measurement =============================================

    private static SKBitmap Render(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return new SkiaRenderer().RenderPage(
            doc.GetPage(1), new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White });
    }

    private static bool IsInk(SKColor p) => p.Red < 200 || p.Green < 200 || p.Blue < 200;

    private static double InkFraction(SKBitmap b)
    {
        long ink = 0;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
                if (IsInk(b.GetPixel(x, y))) ink++;
        return (double)ink / (b.Width * (long)b.Height);
    }

    private static int CountPixels(SKBitmap b, Func<SKColor, bool> pred)
    {
        int n = 0;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
                if (pred(b.GetPixel(x, y))) n++;
        return n;
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

    // Count separated horizontal ink bands (columns with any ink, grouped by gaps),
    // returning the center X of each band.
    private static List<int> InkColumns(SKBitmap b)
    {
        var hasInk = new bool[b.Width];
        for (int x = 0; x < b.Width; x++)
            for (int y = 0; y < b.Height; y++)
                if (IsInk(b.GetPixel(x, y))) { hasInk[x] = true; break; }

        var centers = new List<int>();
        int start = -1;
        for (int x = 0; x <= b.Width; x++)
        {
            bool on = x < b.Width && hasInk[x];
            if (on && start < 0) start = x;
            else if (!on && start >= 0) { centers.Add((start + x - 1) / 2); start = -1; }
        }
        return centers;
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

    // Minimal PDF assembler: sequential objects (1-based), auto /Length for
    // stream objects, classic xref + trailer.
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
