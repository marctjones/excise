using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Excise.Rendering.Differential;
using Xunit;

namespace Excise.Rendering.Tests.Fonts;

/// <summary>
/// #794 — the sibling case of #791, measured and found NOT to reproduce as a
/// excise-specific mis-decode. These are CHARACTERIZATION tests: they pin the
/// measured behavior so a future change can't silently make excise diverge from
/// the ecosystem.
///
/// #791 fixed extraction for a simple SYMBOLIC TrueType with a Microsoft-Symbol
/// <c>(3,0)</c> cmap and <b>no /Encoding</b> (there mutool recovers the intended
/// text from the <c>post</c> glyph names, so excise was made to match). #794
/// asked whether the same font <b>with /Encoding /WinAnsiEncoding</b> (the shape
/// real Word-exported symbol fonts take) still mis-decodes.
///
/// Measured with two independent oracles (fixture built by
/// <see cref="SymbolCmapTtfBuilder"/>, same 0xA1..0xA9 non-ASCII code bytes as
/// #791 so WinAnsi(code) != the intended letter):
///
/// | fixture                                    | excise | mutool | poppler |
/// |--------------------------------------------|--------|--------|---------|
/// | NO /Encoding (#791 shape)                  | Redaction | Redaction | ¡¢£… |
/// | /Encoding /WinAnsiEncoding                 | ¡¢£…   | ¡¢£…   | ¡¢£…    |
/// | /Encoding &lt;&lt;WinAnsi base + Differences&gt;&gt; | ¡¢£… | ¡¢£… | ¡¢£… |
/// | /Encoding /WinAnsiEncoding, WinAnsi-undef codes | ••• | ••• | ••• |
///
/// The ONLY variable that flips mutool off (3,0)/post recovery is the presence
/// of /Encoding: with it, both mutool AND poppler honour WinAnsi and never
/// consult the (3,0) cmap — even for codes WinAnsi leaves undefined (they emit
/// bullets, not the cmap glyph). excise already AGREES with both oracles. Making
/// excise prefer the (3,0) cmap here would make it the SOLE tool emitting
/// "Redaction" — the no-self-oracle violation CLAUDE.md forbids — so no
/// extraction/precedence change is made. (Spec tension noted: ISO 32000-2
/// §9.6.6.4 says a symbolic TrueType ignores /Encoding, so the oracles are
/// arguably non-compliant; that override is a human call, not a unilateral one.)
/// </summary>
public sealed class SymbolicTrueTypeSymbolCmapWithEncodingExtractionTests
{
    private readonly ITestOutputHelper _out;

    public SymbolicTrueTypeSymbolCmapWithEncodingExtractionTests(ITestOutputHelper output) => _out = output;

    // code -> intended letter; codes are non-ASCII so WinAnsi(code) != letter.
    private static readonly (int Code, char Letter)[] Mapping =
    {
        (0xA1, 'R'), (0xA2, 'e'), (0xA3, 'd'), (0xA4, 'a'), (0xA5, 'c'),
        (0xA6, 't'), (0xA7, 'i'), (0xA8, 'o'), (0xA9, 'n'),
    };

    // WinAnsi decode of 0xA1..0xA9 — what both oracles (and excise) emit.
    private const string WinAnsiEcho = "¡¢£¤¥¦§¨©";

    // ---- Characterization: /Encoding /WinAnsiEncoding (bare name) ------------
    // Both oracles honour WinAnsi; excise matches them. The intended "Redaction"
    // is NOT independently recoverable, so it is deliberately NOT asserted.

    [Fact]
    public void SymbolCmap_WinAnsiEncodingName_ExciseMatchesOracle()
    {
        var pdf = BuildFixture("/Encoding /WinAnsiEncoding");
        string exciseText;
        using (var doc = PdfDocument.Open(pdf))
            exciseText = new TextExtractor(doc.GetPage(1)).ExtractText();
        _out.WriteLine($"excise (WinAnsi name) extracted: '{exciseText.Trim()}'");
        exciseText.Should().Contain(WinAnsiEcho,
            "with /Encoding present, excise honours WinAnsi like the oracles do");

        WithTempPdf(pdf, path =>
        {
            Assert.SkipWhen(!MutoolReferenceRenderer.IsAvailable, "mutool not installed.");
            var mutoolText = MutoolTextExtractor.ExtractPage(path, 1);
            Assert.SkipWhen(mutoolText == null, "mutool declined to extract.");
            _out.WriteLine($"mutool (WinAnsi name) extracted: '{mutoolText!.Trim()}'");

            // The crux: excise must AGREE with the independent oracle. mutool does
            // NOT recover "Redaction" once /Encoding is present, so neither should
            // excise — a excise-only "Redaction" would be a self-oracle divergence.
            mutoolText.Should().Contain(WinAnsiEcho,
                "the independent oracle also honours WinAnsi when /Encoding is present");
            exciseText.Trim().Should().Be(mutoolText.Trim(),
                "excise's simple-TrueType /Encoding handling matches the independent oracle");
        });
    }

    // ---- Characterization: /Encoding << /BaseEncoding /WinAnsiEncoding /Differences >>
    // Differences covers OTHER codes (0x41); 0xA1..0xA9 fall to the WinAnsi base.
    // Both oracles still honour WinAnsi.

    [Fact]
    public void SymbolCmap_DifferencesWinAnsiBase_ExciseMatchesOracle()
    {
        var pdf = BuildFixture("/Encoding << /BaseEncoding /WinAnsiEncoding /Differences [ 65 /A ] >>");
        string exciseText;
        using (var doc = PdfDocument.Open(pdf))
            exciseText = new TextExtractor(doc.GetPage(1)).ExtractText();
        _out.WriteLine($"excise (Differences+WinAnsi base) extracted: '{exciseText.Trim()}'");
        exciseText.Should().Contain(WinAnsiEcho);

        WithTempPdf(pdf, path =>
        {
            Assert.SkipWhen(!MutoolReferenceRenderer.IsAvailable, "mutool not installed.");
            var mutoolText = MutoolTextExtractor.ExtractPage(path, 1);
            Assert.SkipWhen(mutoolText == null, "mutool declined to extract.");
            _out.WriteLine($"mutool (Differences+WinAnsi base) extracted: '{mutoolText!.Trim()}'");
            exciseText.Trim().Should().Be(mutoolText!.Trim(),
                "the WinAnsi base-encoding fallback matches the independent oracle");
        });
    }

    // ---- Redaction on what IS extractable: excise removes the text it reads,
    // and the independent oracle confirms it is gone. Anchored to the extracted
    // string (the WinAnsi echo both tools agree on), NOT to the unrecoverable
    // "Redaction" — that keeps the oracle check non-vacuous.

    [Fact]
    public void SymbolCmap_WinAnsiEncodingName_RedactsExtractedText_OracleConfirms()
    {
        Assert.SkipWhen(!MutoolReferenceRenderer.IsAvailable, "mutool not installed.");
        var pdf = BuildFixture("/Encoding /WinAnsiEncoding");

        string extracted;
        byte[] redacted;
        using (var doc = PdfDocument.Open(pdf))
        {
            extracted = new TextExtractor(doc.GetPage(1)).ExtractText().Trim();
            var removed = doc.RedactText(extracted).VerifiedRemovals;
            _out.WriteLine($"redaction of '{extracted}' removed {removed} occurrence(s)");
            removed.Should().BeGreaterThan(0, "excise must redact the text it actually extracts");
            using var ms = new MemoryStream();
            doc.Save(ms);
            redacted = ms.ToArray();
        }

        WithTempPdf(redacted, path =>
        {
            var mutoolText = MutoolTextExtractor.ExtractPage(path, 1);
            Assert.SkipWhen(mutoolText == null, "mutool declined to extract.");
            _out.WriteLine($"mutool after redaction: '{mutoolText!.Trim()}'");
            mutoolText.Should().NotContain(WinAnsiEcho,
                "the independent oracle must confirm the extracted text is gone from the file");
        });
    }

    // ---- Precedence: an explicit /Differences entry is honoured -------------
    // Documents the spec-correct precedence (§9.6.6.2): a per-code /Differences
    // name is the producer's authoritative remapping. Anchored to mutool.

    [Fact]
    public void SymbolCmap_ExplicitDifferencesEntry_HonorsExplicitName()
    {
        // (3,0) cmap maps 0xA1 -> glyph 'R'. /Differences explicitly renames code
        // 0xA1 to glyph "Q". Extraction honours the explicit Differences name.
        var pdf = BuildFixture(
            "/Encoding << /BaseEncoding /WinAnsiEncoding /Differences [ 161 /Q ] >>",
            new[] { (0xA1, 'R') },
            new byte[] { 0xA1 });

        string exciseText;
        using (var doc = PdfDocument.Open(pdf))
            exciseText = new TextExtractor(doc.GetPage(1)).ExtractText();
        _out.WriteLine($"excise (explicit Differences) extracted: '{exciseText.Trim()}'");
        exciseText.Should().Contain("Q",
            "an explicit /Differences entry is the producer's authoritative per-code remapping");

        WithTempPdf(pdf, path =>
        {
            Assert.SkipWhen(!MutoolReferenceRenderer.IsAvailable, "mutool not installed.");
            var mutoolText = MutoolTextExtractor.ExtractPage(path, 1);
            Assert.SkipWhen(mutoolText == null, "mutool declined to extract.");
            _out.WriteLine($"mutool (explicit Differences) extracted: '{mutoolText!.Trim()}'");
            exciseText.Trim().Should().Be(mutoolText!.Trim(),
                "excise honours the explicit /Differences name like the independent oracle");
        });
    }

    // ==== fixture =============================================================

    private static byte[] BuildFixture(string encodingEntry) =>
        BuildFixture(encodingEntry, Mapping, Mapping.Select(m => (byte)m.Code).ToArray());

    private static byte[] BuildFixture(
        string encodingEntry,
        IReadOnlyList<(int Code, char Letter)> mapping,
        byte[] contentCodes)
    {
        var dejaVu = LoadFixtureFont("DejaVuSans.ttf")
            ?? throw new InvalidOperationException("DejaVuSans.ttf fixture missing.");
        var program = SymbolCmapTtfBuilder.BuildSymbolCmapFont(dejaVu, mapping);

        var content = new List<byte>();
        content.AddRange(Encoding.ASCII.GetBytes("BT /F1 48 Tf 20 40 Td ("));
        content.AddRange(contentCodes);
        content.AddRange(Encoding.ASCII.GetBytes(") Tj ET"));

        int first = mapping.Min(m => m.Code);
        int last = mapping.Max(m => m.Code);
        var widths = string.Join(' ', Enumerable.Range(first, last - first + 1).Select(_ => 600));

        var pdf = new MinimalPdf();
        pdf.Add("<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        pdf.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 340 120] /Contents 4 0 R "
              + "/Resources << /Font << /F1 5 0 R >> >> >>");
        pdf.Add("<< >>", content.ToArray());
        // Symbolic simple TrueType: /Flags 4, WITH /Encoding, NO /ToUnicode.
        pdf.Add($"<< /Type /Font /Subtype /TrueType /BaseFont /SymFont /FirstChar {first} /LastChar {last} "
              + $"/Widths [{widths}] {encodingEntry} /FontDescriptor 6 0 R >>");
        pdf.Add("<< /Type /FontDescriptor /FontName /SymFont /Flags 4 "
              + "/FontBBox [-1200 -500 2500 1200] /ItalicAngle 0 /Ascent 900 /Descent -250 "
              + "/CapHeight 700 /StemV 90 /MissingWidth 600 /FontFile2 7 0 R >>");
        pdf.Add("<< >>", program);
        return pdf.Build(1);
    }

    // ==== helpers ============================================================

    private static void WithTempPdf(byte[] pdf, Action<string> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"excise-794-symcmap-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try { body(path); }
        finally { try { File.Delete(path); } catch (IOException) { } }
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
