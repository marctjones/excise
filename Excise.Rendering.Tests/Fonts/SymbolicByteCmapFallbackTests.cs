using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Rendering;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Fonts;

/// <summary>
/// #891 — the renderer's byte→GID cmap reader (<see cref="Fonts.CmapFormat0Table"/>)
/// only understands cmap FORMAT 0. A symbolic TrueType whose built-in cmap is
/// format 4 or 6 (e.g. <c>(3,0)</c> Microsoft-Symbol or <c>(1,0)</c> Macintosh) had
/// no route from PDF content-stream byte to glyph id, so
/// <c>RenderContext.ResolveByteCodeCmap</c> returned null, the draw path fell
/// through to Unicode shaping of a <c>'\0'</c> placeholder (for <c>/Differences</c>
/// names like <c>/gNNN</c> that aren't in the Adobe Glyph List), and the page
/// rendered blank — even though the font's own (3,0)/(1,0) table maps every one
/// of those codes to a real, outline-bearing glyph.
///
/// The fix reuses <c>Excise.Core.Fonts.TrueTypeFontFile.GidForSymbolByte</c> —
/// already implementing ISO 32000-2 §9.6.6.4's symbolic-TrueType lookup order
/// ((3,0) at 0xF000|code, then bare code, then (1,0) bare code) for the
/// extraction path (#791) — as a second byte→GID source in
/// <c>RenderContext.ResolveByteCodeCmap</c>, gated STRICTLY on the
/// <c>/FontDescriptor</c> Symbolic flag (bit 3, value 4).
///
/// The gate matters: <c>issue215.pdf</c> (a DIFFERENT, non-symbolic fixture, #892)
/// has a <c>(1,0)</c> format-6 subtable that is a genuine UNICODE map
/// (<c>0x41 -&gt; gid 28</c>, the small-caps glyph for 'A') — verified directly
/// against that corpus file: its <c>/FontDescriptor</c> carries <c>/Flags 32</c>
/// (Nonsymbolic), NOT bit 3. Treating a (1,0)/(3,0) subtable as byte→GID on a
/// non-symbolic font would silently draw the wrong glyph (regular caps instead of
/// small caps) — worse than the blank #891 fixes, because it looks plausible.
/// </summary>
public sealed class SymbolicByteCmapFallbackTests
{
    private readonly ITestOutputHelper _out;
    public SymbolicByteCmapFallbackTests(ITestOutputHelper output) => _out = output;

    // Real corpus /Differences names from bug1027533.pdf (GRDDWT+Arial-BoldMT):
    // /Differences [ 66 /g0024  89 /g003a  159 /g002c  160 /g0037 ]. None of
    // "g0024"/"g003a"/"g002c"/"g0037" are Adobe Glyph List names, so they cannot
    // decode to a real Unicode char — the failure mode #891 describes.
    private static readonly (int Code, char Letter)[] Mapping =
    {
        (0x42, 'R'), (0x59, 'e'), (0x9F, 'd'), (0xA0, 'a'),
    };

    // ==== unit level: the gate itself =========================================
    // Direct coverage of RenderContext.TryResolveSymbolicByteCmap (made
    // `internal` for this test), independent of Skia typeface loading. This is
    // where the mutation test below actually lives: deleting the Flags check,
    // or deleting the `?? TryResolveSymbolicByteCmap(...)` fallback in
    // ResolveByteCodeCmap, is caught here without any render pass.

    [Fact]
    public void SymbolicFlag_Set_ResolvesRealGidsFromFormat4SymbolCmap()
    {
        var dejaVu = LoadFixtureFont("DejaVuSans.ttf");
        var program = SymbolCmapTtfBuilder.BuildSymbolCmapFont(dejaVu, Mapping);
        var descriptor = DescriptorWithFlags(4); // bit 3: Symbolic

        var map = RenderContext.TryResolveSymbolicByteCmap(program, descriptor);

        map.Should().NotBeNull("a symbolic font's own (3,0) format-4 cmap must resolve byte codes to real glyph ids");
        foreach (var (code, _) in Mapping)
            map![code].Should().NotBe((ushort)0, $"code 0x{code:X2} has a real (3,0) mapping in the fixture font");
    }

    [Fact]
    public void SymbolicFlag_NotSet_ReturnsNull_EvenThoughTheCmapWouldResolve()
    {
        // Same font bytes as the positive case above — only the descriptor's
        // Flags differ (32 = Nonsymbolic, mirroring issue215.pdf's actual
        // /FontDescriptor exactly). This is the design-hazard regression guard:
        // if the Flags gate is ever weakened or removed, this is the test that
        // goes red.
        var dejaVu = LoadFixtureFont("DejaVuSans.ttf");
        var program = SymbolCmapTtfBuilder.BuildSymbolCmapFont(dejaVu, Mapping);
        var descriptor = DescriptorWithFlags(32); // bit 6: Nonsymbolic, bit 3 clear

        var map = RenderContext.TryResolveSymbolicByteCmap(program, descriptor);

        map.Should().BeNull(
            "a non-symbolic font's (1,0)/(3,0) subtable may be a genuine Unicode map " +
            "(issue215.pdf) — using it as byte->GID would silently draw the wrong glyph");
    }

    [Fact]
    public void NullDescriptor_ReturnsNull()
    {
        var dejaVu = LoadFixtureFont("DejaVuSans.ttf");
        var program = SymbolCmapTtfBuilder.BuildSymbolCmapFont(dejaVu, Mapping);

        RenderContext.TryResolveSymbolicByteCmap(program, null).Should().BeNull();
    }

    [Fact]
    public void MissingFlagsKey_DefaultsToNonSymbolic_ReturnsNull()
    {
        var dejaVu = LoadFixtureFont("DejaVuSans.ttf");
        var program = SymbolCmapTtfBuilder.BuildSymbolCmapFont(dejaVu, Mapping);
        var descriptor = new PdfDictionary(); // no /Flags entry at all

        RenderContext.TryResolveSymbolicByteCmap(program, descriptor).Should().BeNull(
            "GetInt(\"Flags\", 0) defaults to 0 when absent, which has bit 3 clear");
    }

    [Fact]
    public void MalformedFontBytes_DoesNotThrow_ReturnsNull()
    {
        var descriptor = DescriptorWithFlags(4);
        var garbage = new byte[] { 1, 2, 3, 4, 5 };

        var act = () => RenderContext.TryResolveSymbolicByteCmap(garbage, descriptor);

        act.Should().NotThrow();
        act().Should().BeNull();
    }

    [Fact]
    public void NullOrEmptyFontBytes_ReturnsNull()
    {
        var descriptor = DescriptorWithFlags(4);
        RenderContext.TryResolveSymbolicByteCmap(null, descriptor).Should().BeNull();
        RenderContext.TryResolveSymbolicByteCmap(Array.Empty<byte>(), descriptor).Should().BeNull();
    }

    // ==== end-to-end: a page that reproduces the actual corpus failure =======
    // Reproduces the mechanism, not just the symptom: /Encoding /Differences
    // names that are NOT in the Adobe Glyph List (matching bug1027533.pdf's own
    // "/g0024" etc.), on a font whose ONLY cmap subtable is (3,0) format 4 (no
    // format-0 fallback CmapFormat0Table could already read). Before the fix,
    // the AGL lookup fails, the draw path receives a NUL placeholder character,
    // and the page renders blank (this is what made bug1027533.pdf/bug1151216.pdf
    // render 196/196 and 272/272 tiles blank respectively).

    [Fact]
    public void NonAglDifferencesNames_OnSymbolicFont_StillPaintsInk()
    {
        var pdf = BuildFixture();

        double exciseInk;
        using (var doc = PdfDocument.Open(pdf))
        using (var bmp = new SkiaRenderer().RenderPage(
                   doc.GetPage(1), new RenderOptions { Dpi = 150, BackgroundColor = SKColors.White }))
            exciseInk = InkFraction(bmp);
        _out.WriteLine($"excise ink = {exciseInk:P3}");

        exciseInk.Should().BeGreaterThan(0.002,
            "excise must resolve /Differences names like /g0024 (not in the AGL) via the " +
            "font's own (3,0) symbol cmap rather than drawing nothing");

        WithTempPdf(pdf, path =>
        {
            Assert.SkipWhen(!MutoolReferenceRenderer.IsAvailable, "mutool not installed.");
            using var refBmp = MutoolReferenceRenderer.RenderPage(path, 1, 150);
            Assert.SkipWhen(refBmp == null, "mutool declined to render.");
            var refInk = InkFraction(refBmp!);
            _out.WriteLine($"mutool ink = {refInk:P3}");
            refInk.Should().BeGreaterThan(0.002, "the independent oracle must also paint ink for this fixture");
        });
    }

    // ==== fixture ==============================================================

    private static byte[] BuildFixture()
    {
        var dejaVu = LoadFixtureFont("DejaVuSans.ttf");
        var program = SymbolCmapTtfBuilder.BuildSymbolCmapFont(dejaVu, Mapping);

        var content = new List<byte>();
        content.AddRange(Encoding.ASCII.GetBytes("BT /F1 48 Tf 20 40 Td ("));
        foreach (var (code, _) in Mapping) content.Add((byte)code);
        content.AddRange(Encoding.ASCII.GetBytes(") Tj ET"));

        int first = Mapping.Min(m => m.Code);
        int last = Mapping.Max(m => m.Code);
        var widths = string.Join(' ', Enumerable.Range(first, last - first + 1).Select(_ => 600));

        // Names deliberately NOT in the Adobe Glyph List, mirroring the exact
        // shape bug1027533.pdf ships (subset-font auto-generated /gNNNN names).
        string DifferenceName(int code) => code switch
        {
            0x42 => "/g0024",
            0x59 => "/g003a",
            0x9F => "/g002c",
            0xA0 => "/g0037",
            _ => throw new InvalidOperationException(),
        };
        var differences = string.Join(' ', Mapping.Select(m => $"{m.Code} {DifferenceName(m.Code)}"));

        var pdf = new MinimalPdf();
        pdf.Add("<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        pdf.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 340 120] /Contents 4 0 R "
              + "/Resources << /Font << /F1 5 0 R >> >> >>");
        pdf.Add("<< >>", content.ToArray());
        // Symbolic simple TrueType, /Encoding with non-AGL /Differences names,
        // NO /ToUnicode — exactly bug1027533.pdf's shape.
        pdf.Add($"<< /Type /Font /Subtype /TrueType /BaseFont /SymFont /FirstChar {first} /LastChar {last} "
              + $"/Widths [{widths}] /Encoding << /BaseEncoding /WinAnsiEncoding /Differences [{differences}] >> "
              + "/FontDescriptor 6 0 R >>");
        pdf.Add("<< /Type /FontDescriptor /FontName /SymFont /Flags 4 "
              + "/FontBBox [-1200 -500 2500 1200] /ItalicAngle 0 /Ascent 900 /Descent -250 "
              + "/CapHeight 700 /StemV 90 /MissingWidth 600 /FontFile2 7 0 R >>");
        pdf.Add("<< >>", program);
        return pdf.Build(1);
    }

    // ==== helpers ==============================================================

    private static PdfDictionary DescriptorWithFlags(int flags)
    {
        var d = new PdfDictionary();
        d[new PdfName("Flags")] = new PdfInteger(flags);
        return d;
    }

    private static void WithTempPdf(byte[] pdf, Action<string> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-891-symcmap-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try { body(path); }
        finally { try { File.Delete(path); } catch (IOException) { } }
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

    private static byte[] LoadFixtureFont(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Excise.Core.Tests", "Fixtures", "Fonts", name);
            if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
            dir = dir.Parent;
        }
        throw new InvalidOperationException($"{name} fixture missing.");
    }

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
