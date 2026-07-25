using System;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// #515 slice 4 — CMap codespace edge cases, end-to-end.
///
/// Codespace-range matching must be BYTE-WISE (PDF 32000 §9.7.6.2), not
/// scalar: a codespace &lt;8140&gt; &lt;FEFE&gt; accepts a 2-byte code only when
/// byte 0 ∈ [0x81,0xFE] AND byte 1 ∈ [0x40,0xFE]. The historical scalar
/// compare accepted byte-wise-invalid codes like &lt;81FF&gt; (inside
/// [0x8140,0xFEFE] as a number), stealing bytes that per spec belong to a
/// 1-byte codespace — which mis-segments every following code, garbles
/// extraction, and therefore silently breaks RedactText (CLAUDE.md
/// limitation #1: excise cannot redact what excise cannot read).
///
/// These fixtures use an EMBEDDED /Encoding CMap stream (§9.7.6.2), which as
/// of this slice drives segmentation through the same parsed CMap the
/// renderer already used — extraction, the redaction content-stream parser,
/// and rendering must all segment identically.
///
/// Expected sequences are computed from the spec by hand, not from excise.
/// </summary>
public class CMapCodespaceEdgeCaseTests
{
    // Mixed-width codespaces: every byte is a valid 1-byte code, AND
    // [81..FE][40..FE] pairs are valid 2-byte codes. Per spec the 2-byte
    // space wins where it matches byte-wise; anything else is 1-byte.
    private const string MixedWidthEncodingCMap = @"
/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
2 begincodespacerange
<00> <FF>
<8140> <FEFE>
endcodespacerange
1 begincidrange
<00> <FF> 0
endcidrange
1 begincidchar
<8141> 500
endcidchar
endcmap
CMapName currentdict /CMap defineresource pop
end
end
";

    // Code-keyed ToUnicode: the secret "SECRET" is spelled by 1-byte codes
    // 41 81 FF 42 43 44 — the 81/FF pair is the scalar-vs-byte-wise
    // discriminator: scalar matching folds them into one bogus 2-byte code
    // 0x81FF ("S?RET", secret unfindable); byte-wise matching resolves both
    // through the 1-byte codespace ("SECRET").
    private const string SecretToUnicode = @"
/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
1 begincodespacerange
<00> <FF>
endcodespacerange
9 beginbfchar
<41> <0053>
<81> <0045>
<FF> <0043>
<42> <0052>
<43> <0045>
<44> <0054>
<58> <0058>
<59> <0059>
<5A> <005A>
endbfchar
1 beginbfrange
<20> <20> <0020>
endbfrange
endcmap
end
end
";

    // 41 81 FF 42 43 44 → "SECRET", 20 → " ", 58 59 5A → "XYZ".
    private const string ContentCodesHex = "4181FF4243442058595A";

    [Fact]
    public void Extract_EmbeddedMixedWidthCMap_SegmentsByteWise()
    {
        var pdf = BuildPdf(ContentCodesHex + "8141");

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var letters = new TextExtractor(doc.GetPage(1)).ExtractLetters();

        // 6 secret letters + space + XYZ + the 2-byte code = 11 glyphs.
        letters.Should().HaveCount(11);
        string.Concat(letters.Take(10).Select(l => l.Value)).Should().Be("SECRET XYZ");

        // Source char-code preservation (redaction re-encodes kept glyphs
        // byte-exactly): the original codes and their byte lengths survive.
        letters.Select(l => l.CharacterCode).Should().Equal(
            0x41, 0x81, 0xFF, 0x42, 0x43, 0x44, 0x20, 0x58, 0x59, 0x5A, 0x8141);
        letters.Select(l => l.CodeByteLength).Should().Equal(
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2);

        // The one byte-wise VALID 2-byte code went through its cidchar
        // mapping, proving the 2-byte codespace still claims what is its own.
        letters[10].IsCidFont.Should().BeTrue();
    }

    [Fact]
    public void ContentStreamParser_EmbeddedMixedWidthCMap_StaysInLockstepWithExtractor()
    {
        // The redaction content-stream parser must segment the same bytes
        // the same way the extractor does — otherwise redaction bounds
        // drift from the letters RedactText matched.
        var pdf = BuildPdf(ContentCodesHex);

        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var page = doc.GetPage(1);

        var extracted = new TextExtractor(page).ExtractText();
        var tjOp = page.GetContentStream().Operators.FirstOrDefault(o => o.Name == "Tj");

        tjOp.Should().NotBeNull();
        extracted.Should().Contain("SECRET XYZ");
        tjOp!.TextContent.Should().Be("SECRET XYZ",
            "parser and extractor must decode identical text from identical bytes");
    }

    [Fact]
    public void RedactText_EmbeddedMixedWidthCMap_RemovesSecretThatScalarMatchingCouldNotFind()
    {
        // End-to-end redaction-security proof for the byte-wise fix: under
        // scalar matching the 81/FF pair collapsed into one bogus 2-byte
        // code, extraction read "S?RET", RedactText("SECRET") matched
        // NOTHING and reported success — the definition of a silent leak.
        var pdf = BuildPdf(ContentCodesHex);
        var input = Path.Combine(Path.GetTempPath(), $"cmap-edge-{Guid.NewGuid():N}.pdf");
        var output = Path.Combine(Path.GetTempPath(), $"cmap-edge-red-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(input, pdf);
            using (var doc = PdfDocument.Open(input))
            {
                doc.RedactText("SECRET").Should().Be(1,
                    "byte-wise codespace matching must make the mixed-width-encoded secret findable");
                doc.Save(output);
            }

            using var redacted = PdfDocument.Open(output);
            var text = new TextExtractor(redacted.GetPage(1)).ExtractText();
            text.Should().NotContain("SECRET", "the redacted glyphs must be gone");
            text.Should().Contain("XYZ",
                "adjacent kept glyphs must survive, re-encoded with their original code bytes");

            // Carrier-agnostic saved-bytes check (CLAUDE.md): the secret must
            // not appear in ANY uncompressed carrier, in any of the encodings
            // a PDF can restate text in.
            var saved = File.ReadAllBytes(output);
            var haystack = Encoding.ASCII.GetString(saved)
                + Encoding.BigEndianUnicode.GetString(saved)
                + Encoding.UTF8.GetString(saved);
            haystack.Should().NotContain("SECRET");
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Fact]
    public void Extract_EmbeddedCMapWithUsecmap_InheritsRegisteredBase()
    {
        // An embedded CMap stream may pull in a registered base via
        // `usecmap` — its codespaces and mappings must be honored so the
        // extractor (and redaction) segment exactly like the renderer.
        var embedded = @"
/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
/90ms-RKSJ-H usecmap
endcmap
end
end
";
        // Shift-JIS: 41='A' (1-byte, CID 264), 93FA='日' (2-byte, CID 3284).
        var pdf = BuildPdf("4193FA", encodingCMap: embedded, toUnicode: null, ordering: "Japan1");

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var letters = new TextExtractor(doc.GetPage(1)).ExtractLetters();

        letters.Select(l => l.Value).Should().Equal("A", "日");
        letters.Select(l => l.CodeByteLength).Should().Equal(1, 2);
    }

    [Fact]
    public void Extract_JunkEmbeddedCMap_FallsBackGracefully()
    {
        // A garbage /Encoding stream must not crash or hang — the font
        // keeps the safe 2-byte identity default.
        var pdf = BuildPdf("00410042", encodingCMap: "%% not a cmap at all\n<<<<[[[ (((", toUnicode: null);

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var letters = new TextExtractor(doc.GetPage(1)).ExtractLetters();

        letters.Should().HaveCount(2, "4 bytes decode as two 2-byte identity codes");
        letters.Select(l => l.CharacterCode).Should().Equal(0x41, 0x42);
    }

    // ─── PDF builder ─────────────────────────────────────────────────────────

    private static byte[] BuildPdf(
        string codesHex,
        string? encodingCMap = null,
        string? toUnicode = SecretToUnicode,
        string ordering = "Identity")
    {
        encodingCMap ??= MixedWidthEncodingCMap;

        var sb = new StringBuilder();
        var offsets = new long[8];
        void Obj(int n) => offsets[n] = sb.Length;

        var content = $"BT /F0 24 Tf 72 700 Td <{codesHex}> Tj ET";
        var toUnicodeEntry = toUnicode != null ? "/ToUnicode 7 0 R" : "";

        sb.Append("%PDF-1.7\n");
        Obj(1); sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n");
        Obj(2); sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        Obj(3); sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]" +
                          "/Resources<</Font<</F0 4 0 R>>>>/Contents 5 0 R>> endobj\n");
        Obj(4); sb.Append("4 0 obj <</Type/Font/Subtype/Type0/BaseFont/Test" +
                          $"/Encoding 6 0 R{toUnicodeEntry}" +
                          "/DescendantFonts[<</Type/Font/Subtype/CIDFontType2/BaseFont/Test" +
                          $"/CIDSystemInfo<</Registry(Adobe)/Ordering({ordering})/Supplement 0>>" +
                          "/DW 1000>>]>> endobj\n");
        Obj(5); sb.Append($"5 0 obj <</Length {content.Length}>>\nstream\n{content}\nendstream endobj\n");
        Obj(6); sb.Append($"6 0 obj <</Length {encodingCMap.Length}>>\nstream\n{encodingCMap}\nendstream endobj\n");
        if (toUnicode != null)
        {
            Obj(7);
            sb.Append($"7 0 obj <</Length {toUnicode.Length}>>\nstream\n{toUnicode}\nendstream endobj\n");
        }

        var xref = sb.Length;
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        for (int i = 1; i <= 7; i++)
        {
            sb.Append(offsets[i] == 0
                ? "0000000000 65535 f \n"
                : offsets[i].ToString("D10") + " 00000 n \n");
        }
        sb.Append($"trailer <</Size 8/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
