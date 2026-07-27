using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Tests.Fixtures;
using Excise.Core.Tests.Fonts;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// #796 — <see cref="HiddenTextDetector"/> must FLAG a simple symbolic TrueType
/// whose Microsoft-Symbol <c>(3,0)</c> cmap glyphs spell real text, but which
/// carries an <c>/Encoding</c> so every extractor (excise, mutool, poppler —
/// established by #794/#795) decodes the WinAnsi interpretation instead. The
/// visible text is therefore unrecoverable and <c>RedactText</c> cannot reach
/// it; the audit makes that gap visible rather than silent.
///
/// <para>These tests assert the DETECTOR fires (or stays silent) — they do NOT
/// re-litigate whether extraction is "wrong". The premise that excise matches
/// the reference tools on <c>/Encoding</c> is owned by #795's oracle-anchored
/// tests, so no reference tool is needed here (Core.Tests stays
/// deterministic).</para>
/// </summary>
public sealed class SymbolCmapHiddenTextDetectorTests
{
    // code -> intended letter; codes are non-ASCII so WinAnsi(code) != letter,
    // guaranteeing (3,0)-decode "Redaction" diverges from the WinAnsi echo.
    private static readonly (int Code, char Letter)[] DivergentMapping =
    {
        (0xA1, 'R'), (0xA2, 'e'), (0xA3, 'd'), (0xA4, 'a'), (0xA5, 'c'),
        (0xA6, 't'), (0xA7, 'i'), (0xA8, 'o'), (0xA9, 'n'),
    };

    // ASCII codes whose WinAnsi decode already equals the symbol glyph:
    // WinAnsi(0x52)='R', WinAnsi(0x65)='e', WinAnsi(0x64)='d'. (3,0) decode ==
    // extraction => no divergence => must NOT be flagged.
    private static readonly (int Code, char Letter)[] NonDivergentMapping =
    {
        (0x52, 'R'), (0x65, 'e'), (0x64, 'd'),
    };

    [Fact]
    public void SymbolCmap_WithEncoding_Divergent_IsFlagged()
    {
        var pdf = BuildSymbolFontPdf(DivergentMapping, includeEncoding: true);
        using var doc = PdfDocument.Open(pdf);

        var records = HiddenTextDetector.ScanPage(doc.GetPage(1), 1);

        records.Should().ContainSingle(r =>
            r.HiddenBy.Contains("(3,0) symbol cmap") && r.HiddenBy.Contains("redaction may not reach"),
            "the (3,0) glyphs spell text extraction (honouring /Encoding) does not recover");
        records.Single(r => r.HiddenBy.Contains("(3,0) symbol cmap")).Text
            .Should().Be("Redaction", "the flagged Text is the recoverable visible string");
    }

    [Fact]
    public void NormalWinAnsiFont_IsNotFlagged()
    {
        // (a) A non-symbolic font with the font's own (3,1) Unicode cmap and
        // /Encoding /WinAnsiEncoding — no (3,0) symbol cmap, no gap.
        var pdf = BuildNormalWinAnsiPdf();
        using var doc = PdfDocument.Open(pdf);

        HiddenTextDetector.ScanPage(doc.GetPage(1), 1)
            .Where(r => r.HiddenBy == HiddenTextDetectorSymbolCmapMessage)
            .Should().BeEmpty("a normal WinAnsi font has no (3,0) symbol-cmap gap");
    }

    [Fact]
    public void SymbolCmap_WithoutEncoding_IsNotFlagged()
    {
        // (b) (3,0) symbol cmap but NO /Encoding: #791 already extracts the
        // intended text correctly, so there is no redaction gap to flag.
        var pdf = BuildSymbolFontPdf(DivergentMapping, includeEncoding: false);
        using var doc = PdfDocument.Open(pdf);

        HiddenTextDetector.ScanPage(doc.GetPage(1), 1)
            .Where(r => r.HiddenBy == HiddenTextDetectorSymbolCmapMessage)
            .Should().BeEmpty("without /Encoding, #791 recovers the text — no gap");
    }

    [Fact]
    public void SymbolCmap_WithEncoding_NoDivergence_IsNotFlagged()
    {
        // (c) (3,0)+/Encoding but the (3,0) decode EQUALS the WinAnsi extraction
        // (ASCII codes) => no divergence => no false flag.
        var pdf = BuildSymbolFontPdf(NonDivergentMapping, includeEncoding: true);
        using var doc = PdfDocument.Open(pdf);

        HiddenTextDetector.ScanPage(doc.GetPage(1), 1)
            .Where(r => r.HiddenBy == HiddenTextDetectorSymbolCmapMessage)
            .Should().BeEmpty("(3,0) decode equals extraction — no divergence, no flag");
    }

    // The detector's #796 HiddenBy message (internal const on HiddenTextDetector).
    private const string HiddenTextDetectorSymbolCmapMessage =
        "visible text via (3,0) symbol cmap not recoverable by extraction — redaction may not reach it";

    // ==== fixtures ============================================================

    private static byte[] BuildSymbolFontPdf(
        (int Code, char Letter)[] mapping, bool includeEncoding)
    {
        var dejaVu = TestFontFixtures.LoadDejaVuSansBytes();
        var program = SymbolCmapTtfBuilder.BuildSymbolCmapFont(dejaVu, mapping);
        var contentCodes = mapping.Select(m => (byte)m.Code).ToArray();
        int first = mapping.Min(m => m.Code);
        int last = mapping.Max(m => m.Code);
        var encodingEntry = includeEncoding ? "/Encoding /WinAnsiEncoding " : "";

        return BuildFontPdf(program, first, last, contentCodes, encodingEntry, flags: 4);
    }

    private static byte[] BuildNormalWinAnsiPdf()
    {
        var dejaVu = TestFontFixtures.LoadDejaVuSansBytes();
        // ASCII "Red" via the font's own (3,1) cmap; non-symbolic flags.
        var codes = new byte[] { (byte)'R', (byte)'e', (byte)'d' };
        return BuildFontPdf(dejaVu, 'R', 'z', codes, "/Encoding /WinAnsiEncoding ", flags: 32);
    }

    private static byte[] BuildFontPdf(
        byte[] program, int first, int last, byte[] contentCodes,
        string encodingEntry, int flags)
    {
        var content = new List<byte>();
        content.AddRange(Encoding.ASCII.GetBytes("BT /F1 48 Tf 20 40 Td ("));
        content.AddRange(contentCodes);
        content.AddRange(Encoding.ASCII.GetBytes(") Tj ET"));

        var widths = string.Join(' ', Enumerable.Range(first, last - first + 1).Select(_ => 600));

        var pdf = new MinimalPdf();
        pdf.Add("<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        pdf.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 340 120] /Contents 4 0 R "
              + "/Resources << /Font << /F1 5 0 R >> >> >>");
        pdf.Add("<< >>", content.ToArray());
        pdf.Add($"<< /Type /Font /Subtype /TrueType /BaseFont /SymFont /FirstChar {first} /LastChar {last} "
              + $"/Widths [{widths}] {encodingEntry}/FontDescriptor 6 0 R >>");
        pdf.Add($"<< /Type /FontDescriptor /FontName /SymFont /Flags {flags} "
              + "/FontBBox [-1200 -500 2500 1200] /ItalicAngle 0 /Ascent 900 /Descent -250 "
              + "/CapHeight 700 /StemV 90 /MissingWidth 600 /FontFile2 7 0 R >>");
        pdf.Add("<< >>", program);
        return pdf.Build(1);
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
