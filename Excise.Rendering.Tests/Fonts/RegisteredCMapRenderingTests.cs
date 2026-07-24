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
/// Render-path verification for Type0 fonts whose <c>/Encoding</c> is a
/// REGISTERED CMap name (#515) — e.g. <c>/UniGB-UCS2-H</c> — rather than
/// Identity-H/V or an embedded CMap stream.
///
/// Before the fix, <c>TryGetType0EncodingCMap</c> only handled an embedded
/// stream; a registered name fell through to identity decoding, so the
/// 2-byte character codes were misread as CIDs directly. On fonts whose
/// CID space differs from the code space (the whole point of a registered
/// CMap) that selects the wrong — usually .notdef — glyphs and the text
/// renders blank/tofu even though extraction (which already went through
/// PredefinedCMapProvider) reads it fine.
///
/// The fixture makes the failure deterministic and the success provable:
/// codes &lt;0041&gt;&lt;0042&gt; ("AB" in UCS-2) map through the real
/// Adobe UniGB-UCS2-H CMap to Adobe-GB1 CIDs 34/35 (verified against the
/// shipped CMap resource), and the descendant CIDFontType2's /CIDToGIDMap
/// stream maps ONLY CIDs 34/35 to DejaVuSans GIDs 36/37 ('A'/'B'). The
/// identity misreading (CIDs 0x41/0x42) lands on zeroed map entries →
/// GID 0, DejaVuSans's .notdef box. So: two tofu boxes before the fix, a
/// real "AB" after.
///
/// Ground truth is independent (no-self-oracle): pdftocairo, Ghostscript
/// and mutool all render this fixture as "AB" at the same ink bbox; the
/// differential tests below compare excise against pdftocairo/Ghostscript
/// live. (mutool is deliberately NOT used as the oracle here — mutool 1.27
/// builds can lack CJK CMap resources.)
/// </summary>
public class RegisteredCMapRenderingTests
{
    private const int Dpi = 150;

    // Deliberately TIGHTER than the corpus-wide 0.10/32 gate: on this sparse
    // two-glyph fixture the with-fix render matches pdftocairo at 0.0006%
    // differing (MAE 0.1) and Ghostscript at 0.67% (MAE 1.1), while the
    // pre-fix tofu render differs by ~5-8% — the corpus tolerance would have
    // let the bug pass the oracle. 2% / MAE 8 cleanly separates the two.
    private const double MaxDifferingPixelFraction = 0.02;
    private const double MaxMeanAbsoluteError = 8.0;

    // Verified against the shipped Adobe UniGB-UCS2-H CMap resource:
    // U+0041 'A' → GB1 CID 34, U+0042 'B' → GB1 CID 35.
    private const int CidA = 34;
    private const int CidB = 35;

    // DejaVuSans.ttf glyph ids: 'A' → 36, 'B' → 37 (from its cmap table).
    private const ushort GidA = 36;
    private const ushort GidB = 37;

    [Fact]
    public void RegisteredCMap_UniGbUcs2H_SelectsGlyphsViaCidMapping()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        // /CIDToGIDMap has real glyphs ONLY at the CMap-produced CIDs 34/35.
        // The identity misreading (0x41/0x42) hits zeroed entries → .notdef.
        var pdf = RegisteredCMapPdf(ttf!, "/UniGB-UCS2-H",
            cidToGid: new Dictionary<int, ushort> { [CidA] = GidA, [CidB] = GidB });
        using var bmp = Render(pdf);

        InkFraction(bmp).Should().BeGreaterThan(0.02,
            "codes <0041><0042> must map through UniGB-UCS2-H to CIDs 34/35 and draw " +
            "the embedded 'A'/'B' glyphs — a blank page means the registered /Encoding " +
            "CMap was ignored and the codes were misread as identity CIDs (#515)");

        // The glyphs must sit where every reference renderer puts them:
        // 72pt "AB" at Td 20 30 on a 300x120pt page → ink bbox ≈
        // (43,78)-(236,187) at 150 DPI (pdftocairo/gs/mutool all agree).
        var bounds = InkBounds(bmp);
        bounds.Left.Should().BeInRange(30, 60);
        bounds.Top.Should().BeInRange(60, 100);
        bounds.Right.Should().BeInRange(215, 260);
        bounds.Bottom.Should().BeInRange(170, 205);
    }

    [Fact]
    public void RegisteredCMap_IdentityMisreadControl_DrawsNotdefTofuInstead()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        // Control: the SAME descendant font under /Identity-H. Codes 0x41/0x42
        // are then CIDs 0x41/0x42, whose /CIDToGIDMap entries are zero → GID 0,
        // which in DejaVuSans is the visible .notdef tofu box. This is exactly
        // what the registered-CMap fixture rendered like before the #515 fix,
        // and proves the 'AB' in the test above comes from the registered
        // CMap's code→CID remapping, not from luck: the two encodings must
        // produce materially different pixels.
        var correct = RegisteredCMapPdf(ttf!, "/UniGB-UCS2-H",
            cidToGid: new Dictionary<int, ushort> { [CidA] = GidA, [CidB] = GidB });
        var misread = RegisteredCMapPdf(ttf!, "/Identity-H",
            cidToGid: new Dictionary<int, ushort> { [CidA] = GidA, [CidB] = GidB });
        using var correctBmp = Render(correct);
        using var misreadBmp = Render(misread);

        InkFraction(misreadBmp).Should().BeGreaterThan(0.001,
            "the identity misreading selects GID 0 and DejaVuSans draws a visible .notdef box");
        var report = DifferentialMetrics.Compare(correctBmp, misreadBmp);
        report.DifferingPixelFraction.Should().BeGreaterThan(0.02,
            "tofu boxes and the real 'AB' glyphs must not look alike — if these renders " +
            "match, the registered /Encoding CMap is not actually changing glyph selection");
    }

    [Fact]
    public void RegisteredCMap_UnknownName_KeepsIdentityFallback()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        // A CMap name this build does not ship must keep the pre-existing
        // identity fallback: codes 0x41/0x42 are used as CIDs directly, so a
        // map with entries AT 0x41/0x42 must still render.
        var pdf = RegisteredCMapPdf(ttf!, "/Bogus-NotARealCMap-H",
            cidToGid: new Dictionary<int, ushort> { [0x41] = GidA, [0x42] = GidB });
        using var bmp = Render(pdf);

        InkFraction(bmp).Should().BeGreaterThan(0.02,
            "an unknown /Encoding name must fall back to identity CID decoding, not go blank");
    }

    [Fact]
    public void RegisteredCMap_MatchesLivePdftocairo()
    {
        Assert.SkipUnless(PdftocairoReferenceRenderer.IsAvailable, "pdftocairo not installed.");
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        var pdf = RegisteredCMapPdf(ttf!, "/UniGB-UCS2-H",
            cidToGid: new Dictionary<int, ushort> { [CidA] = GidA, [CidB] = GidB });
        var path = WriteTemp(pdf);
        try
        {
            using var excise = Render(pdf);
            using var reference = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
            Assert.SkipWhen(reference == null, "pdftocairo declined to render the fixture.");

            using var aligned = DifferentialMetrics.ResizeMatch(excise, reference!.Width, reference.Height);
            var report = DifferentialMetrics.Compare(aligned, reference);
            report.DifferingPixelFraction.Should().BeLessThan(MaxDifferingPixelFraction,
                "excise's registered-CMap Type0 render must draw the same 'AB' glyphs poppler draws " +
                $"(differing={report.DifferingPixelFraction:P2}, MAE={report.MeanAbsoluteError:F1})");
            report.MeanAbsoluteError.Should().BeLessThan(MaxMeanAbsoluteError);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RegisteredCMap_MatchesLiveGhostscript()
    {
        Assert.SkipUnless(GhostscriptReferenceRenderer.IsAvailable, "ghostscript not installed.");
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");

        var pdf = RegisteredCMapPdf(ttf!, "/UniGB-UCS2-H",
            cidToGid: new Dictionary<int, ushort> { [CidA] = GidA, [CidB] = GidB });
        var path = WriteTemp(pdf);
        try
        {
            using var excise = Render(pdf);
            using var reference = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
            Assert.SkipWhen(reference == null, "ghostscript declined to render the fixture.");

            using var aligned = DifferentialMetrics.ResizeMatch(excise, reference!.Width, reference.Height);
            var report = DifferentialMetrics.Compare(aligned, reference);
            report.DifferingPixelFraction.Should().BeLessThan(MaxDifferingPixelFraction,
                "excise's registered-CMap Type0 render must draw the same 'AB' glyphs Ghostscript draws " +
                $"(differing={report.DifferingPixelFraction:P2}, MAE={report.MeanAbsoluteError:F1})");
            report.MeanAbsoluteError.Should().BeLessThan(MaxMeanAbsoluteError);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ==== fixture =============================================================

    /// <summary>
    /// Type0 font over an embedded CIDFontType2 (DejaVuSans) drawing codes
    /// &lt;0041&gt;&lt;0042&gt; at 72pt. <paramref name="encoding"/> selects the
    /// /Encoding entry; <paramref name="cidToGid"/> populates the /CIDToGIDMap
    /// stream (100 CIDs, all other entries .notdef).
    /// </summary>
    private static byte[] RegisteredCMapPdf(
        byte[] ttf, string encoding, Dictionary<int, ushort> cidToGid)
    {
        var map = new byte[200]; // CIDs 0..99, big-endian uint16 per CID
        foreach (var (cid, gid) in cidToGid)
        {
            map[cid * 2] = (byte)(gid >> 8);
            map[cid * 2 + 1] = (byte)gid;
        }

        // DejaVuSans advances: 'A' 1401/2048 em ≈ 684, 'B' 1405/2048 em ≈ 686.
        var pdf = new MinimalPdf();
        pdf.Add("<< /Type /Catalog /Pages 2 0 R >>");                                        // 1
        pdf.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");                                // 2
        pdf.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 120] /Contents 4 0 R "
              + "/Resources << /Font << /F1 5 0 R >> >> >>");                                // 3
        pdf.Add("<< >>", Encoding.ASCII.GetBytes("BT /F1 72 Tf 20 30 Td <00410042> Tj ET")); // 4
        pdf.Add("<< /Type /Font /Subtype /Type0 /BaseFont /TestFont-GB "
              + $"/Encoding {encoding} /DescendantFonts [6 0 R] >>");                        // 5
        pdf.Add("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /TestFont "
              + "/CIDSystemInfo << /Registry (Adobe) /Ordering (GB1) /Supplement 2 >> "
              + $"/FontDescriptor 7 0 R /CIDToGIDMap 9 0 R /DW 1000 /W [{CidA} [684 686]] >>"); // 6
        pdf.Add("<< /Type /FontDescriptor /FontName /TestFont /Flags 4 "
              + "/FontBBox [-1200 -500 2500 1200] /ItalicAngle 0 /Ascent 900 /Descent -250 "
              + "/CapHeight 700 /StemV 90 /FontFile2 8 0 R >>");                             // 7
        pdf.Add("<< >>", ttf);                                                               // 8
        pdf.Add("<< >>", map);                                                               // 9
        return pdf.Build(1);
    }

    private static string WriteTemp(byte[] pdf)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-registered-cmap-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        return path;
    }

    // ==== rendering + measurement =============================================

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

    // Minimal PDF assembler (same shape as FontRenderingMatrixTests'):
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
