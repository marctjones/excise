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
/// #515 slice 2 — registered (predefined) CJK CMap support. A Type0 font whose
/// /Encoding is a registered CMap NAME (e.g. /UniGB-UCS2-H, /90ms-RKSJ-H)
/// previously had no code→CID mapping at all, and with no embedded /ToUnicode
/// its CID→Unicode came from the WinAnsi fallback. Two distinct failure
/// classes, both silent redaction failures (CLAUDE.md limitation #1):
///
/// - Non-UCS2 registered encodings (90ms-RKSJ-H): codes are Shift-JIS with
///   MIXED 1/2-byte widths. The fixed 2-byte stride garbled the segmentation
///   and the raw code was decoded as if it were Unicode ("日本語" came out as
///   the raw-code garbage "鏺鱻貪…").
/// - UCS2 registered encodings (UniGB-UCS2-H …): text output happened to be
///   correct pre-fix — the code IS the UCS-2 value and DecodeWinAnsi is
///   identity for codes ≥160 — the same coincidence #715 documents. What WAS
///   broken pre-fix: the /W width table is CID-keyed (§9.7.4.3) and was looked
///   up with the raw code, so every width fell back to /DW.
///
/// Ground-truth values in these tests come from the Adobe cmap-resources /
/// mapping-resources-pdf data (checked against Python's independent shift_jis
/// codec for the RKSJ codes) — not from excise itself.
/// </summary>
public class RegisteredCMapTests
{
    // ---------- provider ----------

    [Fact]
    public void Provider_LoadsUniGbUcs2H_WithKnownMapping()
    {
        var cmap = PredefinedCMapProvider.TryGetEncodingCMap("UniGB-UCS2-H");
        cmap.Should().NotBeNull();
        cmap!.Mapping[0x4E2D].Should().Be(4559, "UniGB-UCS2-H maps U+4E2D 中 to Adobe-GB1 CID 4559");
        cmap.Mapping[0x6587].Should().Be(3795, "UniGB-UCS2-H maps U+6587 文 to Adobe-GB1 CID 3795");
    }

    [Fact]
    public void Provider_UnknownName_ReturnsNull()
    {
        PredefinedCMapProvider.TryGetEncodingCMap("NotACMap-H").Should().BeNull();
        PredefinedCMapProvider.TryGetCidToUnicodeMap("NotAnOrdering").Should().BeNull();
    }

    [Fact] // usecmap: the -V CMaps carry only vertical overrides and inherit the -H base
    public void Provider_VerticalCMap_InheritsBaseMappingsViaUsecmap()
    {
        var v = PredefinedCMapProvider.TryGetEncodingCMap("UniJIS-UCS2-V");
        v.Should().NotBeNull();
        v!.Mapping[0x3042].Should().Be(843,
            "あ has no vertical variant — UniJIS-UCS2-V must inherit it from UniJIS-UCS2-H via usecmap");
        v.Mapping[0x3001].Should().Be(7887,
            "、 HAS a vertical variant — the -V CMap's own entry must override the inherited 634");
        v.CodespaceRanges.Should().NotBeEmpty("codespaces are inherited from the base CMap too");
    }

    [Fact]
    public void Provider_CidToUnicode_Japan1()
    {
        var map = PredefinedCMapProvider.TryGetCidToUnicodeMap("Japan1");
        map.Should().NotBeNull();
        map![843].Should().Be("あ");
        map[3284].Should().Be("日");
    }

    // ---------- mixed-width registered encoding (90ms-RKSJ-H) ----------

    [Fact] // pre-fix: fixed 2-byte stride + raw-code decode → garbage
    public void RksjEncoding_NoToUnicode_ExtractsJapaneseText()
    {
        // Shift-JIS: 93FA=日 967B=本 8CEA=語 8365=テ 8358=ス 8367=ト
        // (independently verifiable: bytes.fromhex(...).decode('shift_jis'))
        var pdf = BuildType0Pdf("90ms-RKSJ-H", "Japan1", "93FA967B8CEA836583588367");

        Extract(pdf).Should().Contain("日本語テスト",
            "a registered /Encoding CMap maps Shift-JIS codes to Adobe-Japan1 CIDs, and the " +
            "ordering's Adobe-Japan1-UCS2 CMap maps those CIDs to Unicode");
    }

    [Fact] // the CMap's codespaces drive segmentation: 1-byte and 2-byte codes mix freely
    public void RksjEncoding_MixedWidthCodes_SegmentPerCodespace()
    {
        // 41='A' (1-byte, CID 264) B1='ｱ' (1-byte, CID 343) 93FA='日' (2-byte, CID 3284)
        var pdf = BuildType0Pdf("90ms-RKSJ-H", "Japan1", "41B193FA");

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var letters = new TextExtractor(doc.GetPage(1)).ExtractLetters();

        letters.Select(l => l.Value).Should().Equal("A", "ｱ", "日");
        // The letter must carry the ORIGINAL source code and its byte length so
        // redaction re-encodes kept glyphs byte-exactly (mixed-width, no fixed stride).
        letters.Select(l => l.CharacterCode).Should().Equal(0x41, 0xB1, 0x93FA);
        letters.Select(l => l.CodeByteLength).Should().Equal(1, 1, 2);
        letters.Should().OnlyContain(l => l.IsCidFont);
    }

    // ---------- UCS2 registered encoding (UniGB-UCS2-H) ----------

    [Fact]
    public void UniGbEncoding_NoToUnicode_ExtractsCjkText()
    {
        // NOTE: this assertion was green pre-fix by the WinAnsi-identity
        // coincidence (code == UCS-2 value); it stands as the regression guard
        // that the principled path decodes identically. The genuinely-red
        // pre-fix assertion for this encoding is the CID-keyed width below.
        var pdf = BuildType0Pdf("UniGB-UCS2-H", "GB1", "4E2D6587");
        Extract(pdf).Should().Contain("中文");
    }

    [Fact] // pre-fix: /W lookup used the raw code, so CID-keyed widths never matched
    public void UniGbEncoding_WidthsAreCidKeyed()
    {
        // /W says CID 4559 (中, code 4E2D) is 500 units wide; /DW is 1000.
        var pdf = BuildType0Pdf("UniGB-UCS2-H", "GB1", "4E2D6587",
            widths: "/W [4559 [500]]");

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var letters = new TextExtractor(doc.GetPage(1)).ExtractLetters();

        letters[0].Width.Should().BeApproximately(0.5 * 24, 0.01,
            "中 maps to CID 4559 whose /W entry is 500/1000 of the 24pt font size");
        letters[1].Width.Should().BeApproximately(1.0 * 24, 0.01,
            "文 (CID 3795) is not in /W and falls back to /DW 1000");
    }

    // ---------- Identity-H + registered /CIDSystemInfo ordering (§9.10.2 (b)) ----------

    [Fact] // pre-fix: CID 843 fell through to WinAnsi identity → U+034B garbage
    public void IdentityH_KnownOrdering_NoToUnicode_DecodesViaOrderingUcs2()
    {
        // Identity-H: code == CID. CIDs 843/845 are あ/い in Adobe-Japan1.
        var pdf = BuildType0Pdf("Identity-H", "Japan1", "034B034D");

        Extract(pdf).Should().Contain("あい",
            "an Identity-H font whose /CIDSystemInfo names a registered ordering must decode " +
            "CIDs through the ordering's Adobe-Japan1-UCS2 CMap, not the WinAnsi fallback");
    }

    [Fact] // #715, CJK half: /ToUnicode as a registered CMap NAME declares the ordering
    public void ToUnicodeRegisteredName_DecodesViaThatOrdering()
    {
        // Ordering (Identity) carries no Unicode information; the registered
        // /ToUnicode NAME /UniJIS-UCS2-H is the only ordering signal. Encoding
        // is Identity-H, so the CID to look up is the code itself.
        var pdf = BuildType0Pdf("Identity-H", "Identity", "034B",
            toUnicodeNameOrRef: "/UniJIS-UCS2-H");

        Extract(pdf).Should().Contain("あ",
            "a registered-CMap-name /ToUnicode identifies the CID ordering (UniJIS-UCS2-H → " +
            "Adobe-Japan1); treating it as a code-keyed map would require the code to be UCS-2, " +
            "which an Identity-H font's codes are not");
    }

    // ---------- guards ----------

    [Fact] // a /ToUnicode STREAM is the producer's declared map and always wins
    public void ToUnicodeStream_OutranksRegisteredOrdering()
    {
        var toUnicode = "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
            "1 beginbfchar\n<034B> <005A>\nendbfchar\nendcmap\nend end";

        var pdf = BuildType0Pdf("Identity-H", "Japan1", "034B", toUnicodeStream: toUnicode);

        var text = Extract(pdf);
        text.Should().Contain("Z", "/ToUnicode is the producer's declared map and has priority");
        text.Should().NotContain("あ", "the registered ordering map must not override /ToUnicode");
    }

    [Fact] // CIDs outside the ordering map fall through — nothing is invented
    public void UnmappedCid_FallsThroughToExistingBehavior()
    {
        // CID 0x5FFF is beyond Adobe-Japan1's CID range; must not throw and the
        // mapped CID must still decode.
        var pdf = BuildType0Pdf("Identity-H", "Japan1", "034B5FFF");
        Extract(pdf).Should().Contain("あ");
    }

    [Fact] // vertical registered CMaps set vertical writing: letters advance downward
    public void VerticalRegisteredCMap_AdvancesDownward()
    {
        var pdf = BuildType0Pdf("UniJIS-UCS2-V", "Japan1", "30423044");

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var letters = new TextExtractor(doc.GetPage(1)).ExtractLetters();

        letters.Select(l => l.Value).Should().Equal("あ", "い");
        // WMode 1: the advance runs along Y, not X. The advance MAGNITUDE and
        // sign still use the horizontal width-as-displacement approximation —
        // real vertical metrics (/W2, /DW2) are #515 slice 5, and this test
        // deliberately does not codify the approximate direction.
        letters[1].StartX.Should().BeApproximately(letters[0].StartX, 0.01,
            "a -V registered CMap means vertical writing mode (WMode 1): no horizontal advance");
        letters[1].StartY.Should().NotBe(letters[0].StartY,
            "vertical writing advances along the Y axis");
    }

    // ---------- redaction round-trip (the point of the slice) ----------

    [Fact] // pre-fix: RedactText couldn't even find the text (extraction was garbage)
    public void RksjEncoding_RedactText_RemovesTargetAndKeepsRest()
    {
        var pdf = BuildType0Pdf("90ms-RKSJ-H", "Japan1", "93FA967B8CEA836583588367");
        var input = Path.Combine(Path.GetTempPath(), $"rcm-rksj-{Guid.NewGuid():N}.pdf");
        var output = Path.Combine(Path.GetTempPath(), $"rcm-rksj-red-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(input, pdf);
            using (var doc = PdfDocument.Open(input))
            {
                doc.RedactText("日本語").Should().Be(1,
                    "glyph-level removal must match and remove exactly the one occurrence — " +
                    "a higher count means the whole-operator fail-safe fired instead");
                doc.Save(output);
            }

            using var redacted = PdfDocument.Open(output);
            var text = new TextExtractor(redacted.GetPage(1)).ExtractText();
            text.Should().NotContain("日本語", "the redacted glyphs must be gone");
            text.Should().NotContain("日").And.NotContain("本").And.NotContain("語");
            text.Should().Contain("テスト",
                "adjacent kept glyphs must survive, re-encoded with their ORIGINAL " +
                "Shift-JIS code bytes (a Unicode re-encode would not decode through the CMap)");

            // Carrier-agnostic saved-bytes check (CLAUDE.md): the secret must
            // not appear in ANY uncompressed carrier, in any of the encodings
            // a PDF can restate text in.
            var saved = File.ReadAllBytes(output);
            var haystack = Encoding.ASCII.GetString(saved)
                + Encoding.BigEndianUnicode.GetString(saved)
                + Encoding.UTF8.GetString(saved);
            haystack.Should().NotContain("日本語");
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    // ---------- corpus ----------

    [Fact] // real-world fixture: /Encoding /90ms-RKSJ-H, no /ToUnicode, non-embedded
    public void Corpus_90msRksjSample_ExtractsJapaneseText()
    {
        var path = FindRepoFile("test-pdfs", "pdfjs", "90ms_rksj_h_sample.pdf");
        Assert.SkipWhen(path == null,
            "gitignored pdf.js corpus fixture 90ms_rksj_h_sample.pdf not present (scripts/download-pdfjs-corpus.sh).");

        using var doc = PdfDocument.Open(path!);
        var text = new TextExtractor(doc.GetPage(1)).ExtractText();

        text.Should().Contain("Hello ASCII");
        text.Should().Contain("日本語テスト",
            "the Shift-JIS bytes 93FA967B8CEA836583588367 decode to 日本語テスト (independently " +
            "verifiable via any Shift-JIS codec); pre-fix this extracted as raw-code garbage. " +
            "Note: mutool 1.27.2 (built without CJK CMap resources) cannot decode this file — " +
            "the oracle here is Adobe's published CMap data plus the Shift-JIS standard, not excise");
    }

    // ---------- helpers ----------

    private static string Extract(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return new TextExtractor(doc.GetPage(1)).ExtractText();
    }

    /// <summary>
    /// Builds a single-page PDF with a non-embedded Type0 font. The content
    /// stream shows <paramref name="codesHex"/> as one hex string.
    /// </summary>
    private static byte[] BuildType0Pdf(
        string encodingName, string ordering, string codesHex,
        string? widths = null, string? toUnicodeStream = null,
        string? toUnicodeNameOrRef = null)
    {
        var sb = new StringBuilder();
        var offsets = new long[7];
        void Obj(int n) => offsets[n] = sb.Length;

        var content = $"BT /F0 24 Tf 72 700 Td <{codesHex}> Tj ET";
        var toUnicodeEntry = toUnicodeStream != null ? "/ToUnicode 6 0 R"
            : toUnicodeNameOrRef != null ? $"/ToUnicode {toUnicodeNameOrRef}"
            : "";

        sb.Append("%PDF-1.7\n");
        Obj(1); sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n");
        Obj(2); sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        Obj(3); sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]" +
                          "/Resources<</Font<</F0 4 0 R>>>>/Contents 5 0 R>> endobj\n");
        Obj(4); sb.Append("4 0 obj <</Type/Font/Subtype/Type0/BaseFont/Test" +
                          $"/Encoding/{encodingName}{toUnicodeEntry}" +
                          "/DescendantFonts[<</Type/Font/Subtype/CIDFontType2/BaseFont/Test" +
                          $"/CIDSystemInfo<</Registry(Adobe)/Ordering({ordering})/Supplement 0>>" +
                          $"/DW 1000 {widths}>>]>> endobj\n");
        Obj(5); sb.Append($"5 0 obj <</Length {content.Length}>>\nstream\n{content}\nendstream endobj\n");
        if (toUnicodeStream != null)
        {
            Obj(6);
            sb.Append($"6 0 obj <</Length {toUnicodeStream.Length}>>\nstream\n{toUnicodeStream}\nendstream endobj\n");
        }

        var xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        for (int i = 1; i <= 6; i++)
        {
            sb.Append(offsets[i] == 0
                ? "0000000000 65535 f \n"
                : offsets[i].ToString("D10") + " 00000 n \n");
        }
        sb.Append($"trailer <</Size 7/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
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
}
