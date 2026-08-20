using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Excise.Rendering;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Fonts;

/// <summary>
/// #791 — simple SYMBOLIC TrueType with a Microsoft-Symbol <c>(3,0)</c> cmap
/// subtable and NO /ToUnicode. This is the one font class the #790 audit could
/// not probe: DejaVu ships no (3,0) subtable, so the fixture is built by
/// <see cref="SymbolCmapTtfBuilder"/> (patches DejaVu's cmap to (3,0), keeps the
/// real glyf outlines and the v2.0 post glyph names).
///
/// The content stream addresses glyphs with NON-ASCII bytes (0xA1..0xA9) whose
/// WinAnsi decode (¡¢£…) is NOT the intended letter — so an extractor that
/// echoes the raw byte through WinAnsi (excise's simple-font fallback) is caught,
/// unlike an ASCII fixture where the byte already equals the letter (#790 lines
/// 88-99). The (3,0) cmap maps 0xF0A1→glyph(R) … 0xF0A9→glyph(n): intended
/// "Redaction".
///
/// Oracle: mutool. It can recover "Redaction" from the post glyph names, so a
/// divergence is a real redaction-relevant mis-decode (extraction bounds
/// redaction — CLAUDE.md, #637/#645), not genuinely-unrecoverable symbol text.
/// The renderer (glyph selection) and the extractor (code→Unicode) are asserted
/// SEPARATELY because they fail independently.
/// </summary>
public sealed class SymbolicTrueTypeSymbolCmapExtractionTests
{
    private const int Dpi = 150;
    private const string Intended = "Redaction";
    private readonly ITestOutputHelper _out;

    public SymbolicTrueTypeSymbolCmapExtractionTests(ITestOutputHelper output) => _out = output;

    // code -> intended letter; codes are non-ASCII so WinAnsi(code) != letter.
    private static readonly (int Code, char Letter)[] Mapping =
    {
        (0xA1, 'R'), (0xA2, 'e'), (0xA3, 'd'), (0xA4, 'a'), (0xA5, 'c'),
        (0xA6, 't'), (0xA7, 'i'), (0xA8, 'o'), (0xA9, 'n'),
    };

    // ---- render: excise paints the right glyphs, and so does the oracle ------

    [Fact]
    public void SymbolCmap_Render_BothPaintInk()
    {
        var pdf = BuildFixture();
        double exciseInk;
        using (var doc = PdfDocument.Open(pdf))
        using (var bmp = new SkiaRenderer().RenderPage(
                   doc.GetPage(1), new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White }))
            exciseInk = InkFraction(bmp);
        _out.WriteLine($"excise ink = {exciseInk:P3}");
        exciseInk.Should().BeGreaterThan(0.002, "excise must paint the (3,0) symbol-cmap glyphs");

        WithTempPdf(pdf, path =>
        {
            Assert.SkipWhen(!MutoolReferenceRenderer.IsAvailable, "mutool not installed.");
            using var refBmp = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
            Assert.SkipWhen(refBmp == null, "mutool declined to render.");
            var refInk = InkFraction(refBmp!);
            _out.WriteLine($"mutool ink = {refInk:P3}");
            refInk.Should().BeGreaterThan(0.002, "the oracle must also paint the symbol-cmap glyphs");
        });
    }

    // ---- extraction: excise vs mutool (the redaction-relevant crux) ----------

    [Fact]
    public void SymbolCmap_Extraction_RecoversIntendedText_NotRawWinAnsi()
    {
        var pdf = BuildFixture();
        string exciseText;
        using (var doc = PdfDocument.Open(pdf))
            exciseText = new TextExtractor(doc.GetPage(1)).ExtractText();
        _out.WriteLine($"excise extracted raw: '{exciseText.Trim()}'");

        WithTempPdf(pdf, path =>
        {
            Assert.SkipWhen(!MutoolReferenceRenderer.IsAvailable, "mutool not installed.");
            var mutoolText = MutoolTextExtractor.ExtractPage(path, 1);
            Assert.SkipWhen(mutoolText == null, "mutool declined to extract.");
            _out.WriteLine($"mutool extracted raw: '{mutoolText!.Trim()}'");

            // The oracle must recover the intended letters — otherwise the fixture
            // is genuinely unrecoverable and proves nothing (#619).
            mutoolText.Should().Contain(Intended,
                "the oracle must recover the intended text from the (3,0) symbol font " +
                "(via post glyph names); if it can't, there is no recoverable Unicode to assert");

            // Redaction-relevant: excise must recover the SAME letters, not echo the
            // raw content byte through WinAnsi (¡¢£…). Echoing the byte is the silent
            // mis-decode that bounds redaction (#637/#645).
            exciseText.Should().Contain(Intended,
                $"excise must decode the (3,0) symbol cmap to '{Intended}', not the raw " +
                $"WinAnsi bytes; got '{exciseText.Trim()}'");
        });
    }

    // ---- redaction: the mis-decode made concrete -----------------------------
    // If excise can read the text it must be able to redact it, and an
    // independent oracle must confirm it is gone.
    [Fact]
    public void SymbolCmap_Redaction_RemovesText_OracleConfirms()
    {
        Assert.SkipWhen(!MutoolReferenceRenderer.IsAvailable, "mutool not installed.");
        var pdf = BuildFixture();

        byte[] redacted;
        using (var doc = PdfDocument.Open(pdf))
        {
            var removed = doc.RedactText(Intended).VerifiedRemovals;
            _out.WriteLine($"redaction removed {removed} occurrence(s)");
            removed.Should().BeGreaterThan(0, "excise must locate and redact the symbol-cmap text");
            using var ms = new MemoryStream();
            doc.Save(ms);
            redacted = ms.ToArray();
        }

        WithTempPdf(redacted, path =>
        {
            var mutoolText = MutoolTextExtractor.ExtractPage(path, 1);
            Assert.SkipWhen(mutoolText == null, "mutool declined to extract.");
            _out.WriteLine($"mutool after redaction: '{mutoolText!.Trim()}'");
            mutoolText.Should().NotContain(Intended,
                "the independent oracle must confirm the redacted text is gone from the file");
        });
    }

    // ==== fixture =============================================================

    private static byte[] BuildFixture()
    {
        var dejaVu = LoadFixtureFont("DejaVuSans.ttf")
            ?? throw new InvalidOperationException("DejaVuSans.ttf fixture missing.");
        var program = SymbolCmapTtfBuilder.BuildSymbolCmapFont(dejaVu, Mapping);

        // Content stream: raw non-ASCII code bytes inside a literal string.
        var content = new List<byte>();
        content.AddRange(Encoding.ASCII.GetBytes("BT /F1 48 Tf 20 40 Td ("));
        foreach (var (code, _) in Mapping) content.Add((byte)code);
        content.AddRange(Encoding.ASCII.GetBytes(") Tj ET"));

        int first = Mapping.Min(m => m.Code);
        int last = Mapping.Max(m => m.Code);
        var widths = string.Join(' ', Enumerable.Range(first, last - first + 1).Select(_ => 600));

        var pdf = new MinimalPdf();
        pdf.Add("<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        pdf.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 340 120] /Contents 4 0 R "
              + "/Resources << /Font << /F1 5 0 R >> >> >>");
        pdf.Add("<< >>", content.ToArray());
        // Symbolic simple TrueType: /Flags 4, NO /Encoding, NO /ToUnicode.
        pdf.Add($"<< /Type /Font /Subtype /TrueType /BaseFont /SymFont /FirstChar {first} /LastChar {last} "
              + $"/Widths [{widths}] /FontDescriptor 6 0 R >>");
        pdf.Add("<< /Type /FontDescriptor /FontName /SymFont /Flags 4 "
              + "/FontBBox [-1200 -500 2500 1200] /ItalicAngle 0 /Ascent 900 /Descent -250 "
              + "/CapHeight 700 /StemV 90 /MissingWidth 600 /FontFile2 7 0 R >>");
        pdf.Add("<< >>", program);
        return pdf.Build(1);
    }

    // ==== helpers ============================================================

    private static void WithTempPdf(byte[] pdf, Action<string> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-791-symcmap-{Guid.NewGuid():N}.pdf");
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

    private static byte[]? LoadFixtureFont(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Excise.Core.Tests", "Fixtures", "Fonts", name);
            if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
            dir = dir.Parent;
        }
        return null;
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
