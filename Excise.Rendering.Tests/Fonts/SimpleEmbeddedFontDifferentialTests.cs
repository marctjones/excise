using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Excise.Rendering;
using Excise.Rendering.Differential;
using SkiaSharp;
using Xunit;

namespace Excise.Rendering.Tests.Fonts;

/// <summary>
/// Independent-oracle coverage for SIMPLE (non-composite) embedded fonts —
/// the font-class-fixture audit for epic #512.
///
/// FontRenderingMatrixTests already renders these same font classes, but it
/// asserts only that excise itself paints ink (a self-oracle for the render)
/// and never checks TEXT EXTRACTION at all. Extraction is the
/// redaction-relevant property: excise cannot redact what excise cannot read
/// (CLAUDE.md, #637/#645). These tests therefore build self-contained
/// fixtures (embedding the checked-in DejaVu Sans TrueType / Inconsolata CFF
/// fonts — no dependency on the un-downloaded corpus) and compare BOTH:
///   1. render ink — excise vs an independent reference renderer
///      (mutool / pdftocairo), and
///   2. extracted text — excise vs mutool (an extractor that is not excise).
///
/// Each finding is classified as extraction-diverges (redaction-relevant) or
/// display-only. Tests SkipWhen the reference tool is absent or declines, and
/// assert the reference actually produced output before comparing, so a
/// missing tool cannot masquerade as a pass (#619).
/// </summary>
public sealed class SimpleEmbeddedFontDifferentialTests
{
    private const int Dpi = 150;
    private readonly ITestOutputHelper _out;

    public SimpleEmbeddedFontDifferentialTests(ITestOutputHelper output) => _out = output;

    // The word drawn by every extraction fixture. ASCII so a WinAnsi simple
    // font maps each code straight to the same Unicode scalar with no
    // /ToUnicode needed — the ordinary real-world simple-font case.
    private const string Word = "Redaction";

    // ---- render: excise vs independent renderer -----------------------------

    [Fact]
    public void SimpleTrueType_FontFile2_BothRenderVisibleInk()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");
        AssertBothRenderInk(SimpleEmbeddedFontPdf(ttf!, FontFileKind.TrueType,
            $"BT /F1 48 Tf 20 40 Td ({Word}) Tj ET"), "simple TrueType /FontFile2");
    }

    [Fact]
    public void SimpleCff_FontFile3_Type1C_BothRenderVisibleInk()
    {
        var cff = LoadFixtureFont("Inconsolata.cff");
        Assert.SkipWhen(cff == null, "Inconsolata.cff fixture missing.");
        AssertBothRenderInk(SimpleEmbeddedFontPdf(cff!, FontFileKind.Cff,
            $"BT /F1 48 Tf 20 40 Td ({Word}) Tj ET"), "simple CFF/Type1C /FontFile3");
    }

    // ---- extraction: excise vs mutool (redaction-relevant) ------------------

    [Fact]
    public void SimpleTrueType_FontFile2_WinAnsi_ExtractionParity()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");
        AssertExtractionParity(SimpleEmbeddedFontPdf(ttf!, FontFileKind.TrueType,
            $"BT /F1 48 Tf 20 40 Td ({Word}) Tj ET"), "simple TrueType /FontFile2 WinAnsi");
    }

    [Fact]
    public void SimpleCff_FontFile3_Type1C_WinAnsi_ExtractionParity()
    {
        var cff = LoadFixtureFont("Inconsolata.cff");
        Assert.SkipWhen(cff == null, "Inconsolata.cff fixture missing.");
        AssertExtractionParity(SimpleEmbeddedFontPdf(cff!, FontFileKind.Cff,
            $"BT /F1 48 Tf 20 40 Td ({Word}) Tj ET"), "simple CFF/Type1C /FontFile3 WinAnsi");
    }

    // ---- DISCRIMINATING extraction probe: /Differences, no /ToUnicode -------
    // The WinAnsi ASCII cases above are necessary but weak: for an ASCII code
    // under WinAnsi the code already EQUALS its Unicode scalar, so an extractor
    // that ignored the encoding entirely and echoed raw content-stream bytes
    // would pass identically. This probe makes extraction depend on the font's
    // encoding: code 65 is remapped via /Differences to /eacute with NO
    // /ToUnicode, so the raw byte is 'A' (0x41) but the CORRECT decode is
    // 'é' (U+00E9). excise and mutool agree ONLY if the /Differences glyph name
    // is resolved to Unicode (code -> glyph name -> AGL). This is the
    // font-driven-decode failure class of #637/#645. Assertions use the RAW
    // extraction (not the ASCII-stripping normalizer, which would erase U+00E9
    // on both sides and produce a vacuous "parity").
    [Fact]
    public void SimpleTrueType_DifferencesNoToUnicode_DecodesToRemappedUnicode()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");
        AssertDifferencesDecode(SimpleEmbeddedFontPdf(ttf!, FontFileKind.TrueType,
            "BT /F1 48 Tf 20 40 Td (A) Tj ET",
            encoding: "<< /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [65 /eacute] >>"),
            "simple TrueType /Differences->/eacute (no ToUnicode)");
    }

    [Fact]
    public void SimpleCff_DifferencesNoToUnicode_DecodesToRemappedUnicode()
    {
        var cff = LoadFixtureFont("Inconsolata.cff");
        Assert.SkipWhen(cff == null, "Inconsolata.cff fixture missing.");
        AssertDifferencesDecode(SimpleEmbeddedFontPdf(cff!, FontFileKind.Cff,
            "BT /F1 48 Tf 20 40 Td (A) Tj ET",
            encoding: "<< /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [65 /eacute] >>"),
            "simple CFF/Type1C /Differences->/eacute (no ToUnicode)");
    }

    // ---- symbolic simple TrueType (/Flags 4, no /Encoding) ------------------
    // Characterization. A symbolic TrueType selects glyphs through the font's
    // own (3,0)/(1,0) cmap with the raw code and legitimately carries no
    // reliable Unicode. This asserts only that excise and the reference agree
    // on whether the glyph PAINTS; it does NOT assert a Unicode extraction,
    // because symbolic fonts without /ToUnicode have none to guarantee. A
    // font that actually ships a (3,0) symbol cmap subtable (DejaVu does not)
    // is a distinct, deeper fixture — see the proposed child issue.
    [Fact]
    public void SymbolicSimpleTrueType_Flags4_BothRenderVisibleInk()
    {
        var ttf = LoadFixtureFont("DejaVuSans.ttf");
        Assert.SkipWhen(ttf == null, "DejaVuSans.ttf fixture missing.");
        // No /Encoding, /Flags 4 (Symbolic) on the descriptor.
        AssertBothRenderInk(SimpleEmbeddedFontPdf(ttf!, FontFileKind.TrueType,
            "BT /F1 48 Tf 20 40 Td (Abc) Tj ET", encoding: null, flags: 4),
            "symbolic simple TrueType (/Flags 4)");
    }

    // ---- MMType1 fallback (acceptance-criterion, non-embedded) --------------
    // MMType1 is named in #512's acceptance criteria and has zero references
    // anywhere in the codebase. Real MMType1 fonts are near-extinct and are
    // almost always non-embedded; the realistic behaviour is graceful
    // substitution. This characterizes that: a non-embedded /MMType1 must
    // still paint via a substituted face, matching a reference renderer.
    [Fact]
    public void MMType1_NonEmbedded_Fallback_BothRenderVisibleInk()
    {
        AssertBothRenderInk(NonEmbeddedMMType1Pdf($"BT /F1 48 Tf 20 40 Td ({Word}) Tj ET"),
            "non-embedded /MMType1 fallback");
    }

    // ==== assertions =========================================================

    private void AssertBothRenderInk(byte[] pdf, string label)
    {
        double exciseInk;
        using (var doc = PdfDocument.Open(pdf))
        using (var bmp = new SkiaRenderer().RenderPage(
                   doc.GetPage(1), new RenderOptions { Dpi = Dpi, BackgroundColor = SKColors.White }))
            exciseInk = InkFraction(bmp);

        _out.WriteLine($"[{label}] excise ink = {exciseInk:P3}");
        exciseInk.Should().BeGreaterThan(0.002, $"excise must paint the {label} glyphs");

        WithTempPdf(pdf, path =>
        {
            var (refBmp, who) = RenderWithAnyReference(path);
            Assert.SkipWhen(refBmp == null, "no independent renderer (mutool/pdftocairo) available or willing.");
            using (refBmp)
            {
                var refInk = InkFraction(refBmp!);
                _out.WriteLine($"[{label}] {who} ink = {refInk:P3}");
                refInk.Should().BeGreaterThan(0.002,
                    $"the independent renderer {who} must also paint the {label} glyphs " +
                    "(if excise paints and it does not, or vice versa, that is the divergence)");
            }
        });
    }

    private void AssertExtractionParity(byte[] pdf, string label)
    {
        string exciseText;
        using (var doc = PdfDocument.Open(pdf))
            exciseText = new TextExtractor(doc.GetPage(1)).ExtractText();
        var exciseNorm = Normalize(exciseText);
        _out.WriteLine($"[{label}] excise extracted: '{exciseText.Trim()}' -> '{exciseNorm}'");

        WithTempPdf(pdf, path =>
        {
            Assert.SkipWhen(!MutoolReferenceRenderer.IsAvailable, "mutool not installed.");
            var mutoolText = MutoolTextExtractor.ExtractPage(path, 1);
            Assert.SkipWhen(mutoolText == null, "mutool declined to extract.");
            var mutoolNorm = Normalize(mutoolText!);
            _out.WriteLine($"[{label}] mutool extracted: '{mutoolText!.Trim()}' -> '{mutoolNorm}'");

            // The reference must actually have read the word — otherwise the
            // fixture is degenerate and proves nothing (#619).
            mutoolNorm.Should().Contain(Word.ToLowerInvariant(),
                $"the fixture must be a valid {label} PDF that the reference extractor can read");

            // Redaction-relevant assertion: excise must recover the same word.
            exciseNorm.Should().Contain(Word.ToLowerInvariant(),
                $"excise must extract the {label} text (extraction bounds redaction — #637/#645). " +
                $"excise='{exciseNorm}' mutool='{mutoolNorm}'");
        });
    }

    private void AssertDifferencesDecode(byte[] pdf, string label)
    {
        const string Accent = "é"; // é — the correct decode of /eacute
        string exciseText;
        using (var doc = PdfDocument.Open(pdf))
            exciseText = new TextExtractor(doc.GetPage(1)).ExtractText();
        _out.WriteLine($"[{label}] excise extracted raw: '{exciseText.Trim()}'");

        WithTempPdf(pdf, path =>
        {
            Assert.SkipWhen(!MutoolReferenceRenderer.IsAvailable, "mutool not installed.");
            var mutoolText = MutoolTextExtractor.ExtractPage(path, 1);
            Assert.SkipWhen(mutoolText == null, "mutool declined to extract.");
            _out.WriteLine($"[{label}] mutool extracted raw: '{mutoolText!.Trim()}'");

            // Reference must actually apply the encoding and yield the accent —
            // otherwise the fixture is degenerate (raw byte 'A' would prove
            // nothing about decoding). Raw contains-check: NO ASCII stripping.
            mutoolText.Should().Contain(Accent,
                $"the reference extractor must decode {label} to U+00E9; if it echoed the raw " +
                "byte it would read 'A' and this probe would be vacuous");

            // Redaction-relevant: excise must resolve the /Differences glyph
            // name to the same Unicode, not echo the raw 'A' byte.
            exciseText.Should().Contain(Accent,
                $"excise must decode {label} through the /Differences array to U+00E9 " +
                $"(code->glyph name->Unicode); got raw '{exciseText.Trim()}'. Echoing the raw " +
                "byte here is the silent mis-decode that bounds redaction (#637/#645)");
            exciseText.Should().NotContain("A",
                "excise must not leave the pre-encoding raw byte 'A' in the extraction");
        });
    }

    private static (SKBitmap? Bitmap, string Who) RenderWithAnyReference(string path)
    {
        if (MutoolReferenceRenderer.IsAvailable)
        {
            var b = MutoolReferenceRenderer.RenderPage(path, 1, Dpi);
            if (b != null) return (b, "mutool");
        }
        if (PdftocairoReferenceRenderer.IsAvailable)
        {
            var b = PdftocairoReferenceRenderer.RenderPage(path, 1, Dpi);
            if (b != null) return (b, "pdftocairo");
        }
        if (GhostscriptReferenceRenderer.IsAvailable)
        {
            var b = GhostscriptReferenceRenderer.RenderPage(path, 1, Dpi);
            if (b != null) return (b, "Ghostscript");
        }
        return (null, "none");
    }

    // ==== fixtures ============================================================

    private enum FontFileKind { TrueType, Cff }

    private static byte[] SimpleEmbeddedFontPdf(
        byte[] program, FontFileKind kind, string content, string? encoding = "/WinAnsiEncoding", int flags = 32)
    {
        var pdf = new MinimalPdf();
        pdf.Add("<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        pdf.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 340 120] /Contents 4 0 R "
              + "/Resources << /Font << /F1 5 0 R >> >> >>");
        pdf.Add("<< >>", Encoding.ASCII.GetBytes(content));
        var subtype = kind == FontFileKind.TrueType ? "/TrueType" : "/Type1";
        var enc = encoding == null ? string.Empty : $"/Encoding {encoding} ";
        pdf.Add($"<< /Type /Font /Subtype {subtype} /BaseFont /TestFont /FirstChar 32 /LastChar 126 "
              + $"/Widths [{UniformWidths(95, 600)}] /FontDescriptor 6 0 R {enc}>>");
        var ffKey = kind == FontFileKind.TrueType ? "/FontFile2" : "/FontFile3";
        pdf.Add($"<< /Type /FontDescriptor /FontName /TestFont /Flags {flags} "
              + "/FontBBox [-1200 -500 2500 1200] /ItalicAngle 0 /Ascent 900 /Descent -250 "
              + $"/CapHeight 700 /StemV 90 /MissingWidth 600 {ffKey} 7 0 R >>");
        var ffDict = kind == FontFileKind.Cff ? "<< /Subtype /Type1C >>" : "<< >>";
        pdf.Add(ffDict, program);
        return pdf.Build(1);
    }

    private static byte[] NonEmbeddedMMType1Pdf(string content)
    {
        var pdf = new MinimalPdf();
        pdf.Add("<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        pdf.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 340 120] /Contents 4 0 R "
              + "/Resources << /Font << /F1 5 0 R >> >> >>");
        pdf.Add("<< >>", Encoding.ASCII.GetBytes(content));
        // No FontFile: a non-embedded multiple-master font that readers
        // substitute. BaseFont carries a real MM family name.
        pdf.Add("<< /Type /Font /Subtype /MMType1 /BaseFont /MinionMM /Encoding /WinAnsiEncoding >>");
        return pdf.Build(1);
    }

    private static string UniformWidths(int count, int w) => string.Join(' ', Enumerable.Repeat(w, count));

    // ==== helpers ============================================================

    private static void WithTempPdf(byte[] pdf, Action<string> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-512-simplefont-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try { body(path); }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (!char.IsWhiteSpace(ch) && ch < 128)
                sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
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
