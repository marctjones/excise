using System;
using System.Collections.Generic;
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
/// #515 — complete predefined CMap coverage: the full PDF 32000-1 Table 118
/// set (Adobe-GB1 / CNS1 / Japan1 / Korea1) plus ISO 32000-2's Adobe-KR
/// UniAKR-UTF16-H. Before this slice only the Uni*-UCS2 and 90ms-RKSJ CMaps
/// shipped; a Type0 font using any legacy national encoding (GBK-EUC, Big5,
/// EUC-JP, EUC-KR/UHC, ISO-2022 "H"/"V") or a Uni*-UTF16 encoding fell
/// through to the 2-byte identity fallback: bytes misread as CIDs, extraction
/// garbled, and — the reason this is redaction security, not display polish
/// (CLAUDE.md limitation #1) — <c>RedactText</c> could not match the text and
/// reported success while leaving it in the file.
///
/// Ground-truth code→CID values in these tests are computed from the Adobe
/// cmap-resources / mapping-resources-pdf source files directly (see the
/// Adobe-published cidrange/cidchar data), not from excise itself.
/// </summary>
public class PredefinedCMapCoverageTests
{
    // ---------- the shipped set is complete and self-consistent ----------

    /// <summary>Every PDF 32000-1 Table 118 name + UniAKR-UTF16-H, by collection.</summary>
    private static readonly (string Ordering, string[] Names)[] Table118 =
    {
        ("GB1", new[]
        {
            "GB-EUC-H", "GB-EUC-V", "GBpc-EUC-H", "GBpc-EUC-V", "GBK-EUC-H",
            "GBK-EUC-V", "GBKp-EUC-H", "GBKp-EUC-V", "GBK2K-H", "GBK2K-V",
            "UniGB-UCS2-H", "UniGB-UCS2-V", "UniGB-UTF16-H", "UniGB-UTF16-V",
        }),
        ("CNS1", new[]
        {
            "B5pc-H", "B5pc-V", "HKscs-B5-H", "HKscs-B5-V", "ETen-B5-H",
            "ETen-B5-V", "ETenms-B5-H", "ETenms-B5-V", "CNS-EUC-H", "CNS-EUC-V",
            "UniCNS-UCS2-H", "UniCNS-UCS2-V", "UniCNS-UTF16-H", "UniCNS-UTF16-V",
        }),
        ("Japan1", new[]
        {
            "83pv-RKSJ-H", "90ms-RKSJ-H", "90ms-RKSJ-V", "90msp-RKSJ-H",
            "90msp-RKSJ-V", "90pv-RKSJ-H", "Add-RKSJ-H", "Add-RKSJ-V",
            "EUC-H", "EUC-V", "Ext-RKSJ-H", "Ext-RKSJ-V", "H", "V",
            "UniJIS-UCS2-H", "UniJIS-UCS2-V", "UniJIS-UCS2-HW-H",
            "UniJIS-UCS2-HW-V", "UniJIS-UTF16-H", "UniJIS-UTF16-V",
        }),
        ("Korea1", new[]
        {
            "KSC-EUC-H", "KSC-EUC-V", "KSCms-UHC-H", "KSCms-UHC-V",
            "KSCms-UHC-HW-H", "KSCms-UHC-HW-V", "KSCpc-EUC-H",
            "UniKS-UCS2-H", "UniKS-UCS2-V", "UniKS-UTF16-H", "UniKS-UTF16-V",
        }),
        ("KR", new[] { "UniAKR-UTF16-H" }),
    };

    [Fact]
    public void Provider_ShipsEveryTable118CMap_WithOrderingAndCompanionMap()
    {
        foreach (var (ordering, names) in Table118)
        {
            PredefinedCMapProvider.TryGetCidToUnicodeMap(ordering).Should().NotBeNull(
                $"ordering {ordering} needs its Adobe-{ordering}-UCS2 companion for CID→Unicode");

            foreach (var name in names)
            {
                PredefinedCMapProvider.IsKnownEncodingCMap(name).Should().BeTrue(
                    $"{name} is a PDF 32000 Table 118 predefined CMap");
                PredefinedCMapProvider.GetOrderingForEncodingCMap(name).Should().Be(ordering);

                var cmap = PredefinedCMapProvider.TryGetEncodingCMap(name);
                cmap.Should().NotBeNull(
                    $"{name} is registered in the provider, so its embedded resource must load and parse");
                cmap!.CodespaceRanges.Should().NotBeEmpty(
                    $"{name} must declare (or inherit via usecmap) codespace ranges — they drive byte segmentation");
                cmap.Mapping.Should().NotBeEmpty(
                    $"{name} must carry (or inherit) code→CID mappings");
            }
        }
    }

    [Fact] // vertical is a property of the CMap FILE (/WMode 1), not of a "-V" name suffix
    public void Provider_VerticalCMaps_ReportWModeFromFileContent()
    {
        foreach (var (_, names) in Table118)
        {
            foreach (var name in names)
            {
                // Adobe's naming: every vertical CMap in this set is either the
                // one-letter "V" or ends in "-V"; assert IsVertical agrees with
                // the file's own /WMode so the name heuristic can never drift.
                var expectVertical = name == "V" || name.EndsWith("-V", StringComparison.Ordinal);
                PredefinedCMapProvider.TryGetEncodingCMap(name)!.WMode.Should().Be(
                    expectVertical ? 1 : 0, $"{name}'s /WMode must match its writing mode");
                PredefinedCMapProvider.IsVertical(name).Should().Be(expectVertical,
                    $"IsVertical({name}) must answer from the parsed /WMode");
            }
        }

        // The suffix fallback still covers the names the provider doesn't ship.
        PredefinedCMapProvider.IsVertical("Identity-V").Should().BeTrue();
        PredefinedCMapProvider.IsVertical("Identity-H").Should().BeFalse();
    }

    [Fact] // pre-slice: "V" (no "-V" suffix) was reported horizontal
    public void OneLetterV_IsVertical_AndInheritsHBaseViaUsecmap()
    {
        var v = PredefinedCMapProvider.TryGetEncodingCMap("V");
        v.Should().NotBeNull();
        v!.WMode.Should().Be(1, "Adobe-Japan1's V CMap declares /WMode 1");
        v.Mapping[0x2422].Should().Be(843,
            "あ (JIS 0x2422) has no vertical variant — V inherits CID 843 from H via usecmap");
    }

    // ---------- legacy national encodings: code→CID→Unicode end-to-end ----------

    [Fact] // pre-slice: GBK bytes were misread as 2-byte identity CIDs → garbage
    public void GbkEuc_MixedWidthCodes_ExtractSimplifiedChinese()
    {
        // GBK: 41='A' (1-byte codespace <00><80>), BABA=汉 CID 1905, D7D6=字 CID 4659
        // (Adobe GBK-EUC-H cidranges; independently verifiable via any GBK codec).
        var pdf = BuildType0Pdf("GBK-EUC-H", "GB1", "41BABAD7D6");

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var letters = new TextExtractor(doc.GetPage(1)).ExtractLetters();

        letters.Select(l => l.Value).Should().Equal("A", "汉", "字");
        // Redaction re-encodes kept glyphs from these — they must be the
        // ORIGINAL GBK code bytes, mixed 1/2-byte, not Unicode.
        letters.Select(l => l.CharacterCode).Should().Equal(0x41, 0xBABA, 0xD7D6);
        letters.Select(l => l.CodeByteLength).Should().Equal(1, 2, 2);
    }

    [Fact]
    public void EtenB5_ExtractsTraditionalChinese()
    {
        // Big5: A4A4=中 CID 661, A4E5=文 CID 726 (Adobe ETen-B5-H).
        var pdf = BuildType0Pdf("ETen-B5-H", "CNS1", "A4A4A4E5");
        Extract(pdf).Should().Contain("中文",
            "Big5 codes must decode through ETen-B5-H to Adobe-CNS1 CIDs and on to Unicode");
    }

    [Fact]
    public void KscmsUhc_ExtractsHangul()
    {
        // UHC: C7D1=한 CID 3296, B1DB=글 CID 1238 (Adobe KSCms-UHC-H).
        var pdf = BuildType0Pdf("KSCms-UHC-H", "Korea1", "C7D1B1DB");
        Extract(pdf).Should().Contain("한글");
    }

    [Fact]
    public void EucJp_ExtractsKana()
    {
        // EUC-JP: A4A2=あ CID 843, A4A4=い CID 845 (Adobe EUC-H).
        var pdf = BuildType0Pdf("EUC-H", "Japan1", "A4A2A4A4");
        Extract(pdf).Should().Contain("あい");
    }

    [Fact] // the one-letter registered name "H" (ISO-2022-JP) is a valid /Encoding
    public void IsoJisH_OneLetterName_ExtractsKana()
    {
        // JIS X 0208: 2422=あ CID 843, 2424=い CID 845 (Adobe H).
        var pdf = BuildType0Pdf("H", "Japan1", "24222424");
        Extract(pdf).Should().Contain("あい");
    }

    [Fact] // legacy vertical CMaps set vertical writing like the Uni*-V ones do
    public void GbkEucV_AdvancesDownward()
    {
        var pdf = BuildType0Pdf("GBK-EUC-V", "GB1", "BABAD7D6");

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var letters = new TextExtractor(doc.GetPage(1)).ExtractLetters();

        letters.Select(l => l.Value).Should().Equal("汉", "字");
        letters[1].StartX.Should().BeApproximately(letters[0].StartX, 0.01,
            "GBK-EUC-V declares /WMode 1: no horizontal advance");
        letters[1].StartY.Should().NotBe(letters[0].StartY,
            "vertical writing advances along the Y axis");
    }

    // ---------- UTF-16 encodings (the PDF 1.5+ replacements for UCS-2) ----------

    [Fact]
    public void UniGbUtf16_Bmp_DecodesLikeUcs2()
    {
        // UTF-16BE BMP codes coincide with UCS-2: 4E2D=中 CID 4559.
        var pdf = BuildType0Pdf("UniGB-UTF16-H", "GB1", "4E2D6C49");
        Extract(pdf).Should().Contain("中汉");
    }

    [Fact] // 4-byte surrogate-pair codes: the codespace <D800DC00><DBFFDFFF>
    public void UniGbUtf16_SurrogatePairCode_DecodesToPlane2Cid()
    {
        var cmap = PredefinedCMapProvider.TryGetEncodingCMap("UniGB-UTF16-H")!;

        // U+20087 𠂇 encodes as the surrogate pair D840 DC87; Adobe's
        // UniGB-UTF16-H maps that 4-byte code to CID 22048.
        var decoded = cmap.DecodeDetailed(new byte[] { 0xD8, 0x40, 0xDC, 0x87, 0x4E, 0x2D });

        decoded.Should().HaveCount(2, "a surrogate pair is ONE 4-byte code, not two 2-byte codes");
        decoded[0].Cid.Should().Be(22048, "Adobe UniGB-UTF16-H maps <D840DC87> to CID 22048");
        decoded[0].ByteLength.Should().Be(4,
            "redaction must re-encode the kept glyph with all four original bytes");
        decoded[1].Cid.Should().Be(4559, "the following BMP code 4E2D still decodes normally");
        decoded[1].ByteLength.Should().Be(2);
    }

    [Fact]
    public void UniGbUtf16_SurrogatePairCode_ExtractsPlane2Character()
    {
        // End-to-end: CID 22048 → U+20087 𠂇 via Adobe-GB1-UCS2 (which carries
        // surrogate-pair destinations for plane-2 CIDs).
        var pdf = BuildType0Pdf("UniGB-UTF16-H", "GB1", "D840DC874E2D");
        var text = Extract(pdf);
        text.Should().Contain("\U00020087");
        text.Should().Contain("中");
    }

    [Fact] // Adobe-KR (ISO 32000-2) — distinct from the deprecated Adobe-Korea1
    public void UniAkrUtf16_ExtractsHangul_ViaKrOrdering()
    {
        // UTF-16: D55C=한 → Adobe-KR CID 2835, AE00=글 → CID 392; the KR
        // ordering's Adobe-KR-UCS2 companion maps them back to Unicode.
        var pdf = BuildType0Pdf("UniAKR-UTF16-H", "KR", "D55CAE00");
        Extract(pdf).Should().Contain("한글");
    }

    // ---------- redaction round-trip (why this slice exists) ----------

    [Fact] // pre-slice: RedactText couldn't find GBK text at all — silent failure
    public void GbkEuc_RedactText_RemovesTargetAndKeepsRest()
    {
        // 'A' 汉 字 'B' — redact 汉字, keep the ASCII neighbours.
        var pdf = BuildType0Pdf("GBK-EUC-H", "GB1", "41BABAD7D642");
        var input = Path.Combine(Path.GetTempPath(), $"cmap-gbk-{Guid.NewGuid():N}.pdf");
        var output = Path.Combine(Path.GetTempPath(), $"cmap-gbk-red-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(input, pdf);
            using (var doc = PdfDocument.Open(input))
            {
                doc.RedactText("汉字").Should().Be(1,
                    "glyph-level removal must match and remove exactly the one occurrence — " +
                    "a higher count means the whole-operator fail-safe fired instead");
                doc.Save(output);
            }

            using var redacted = PdfDocument.Open(output);
            var text = new TextExtractor(redacted.GetPage(1)).ExtractText();
            text.Should().NotContain("汉").And.NotContain("字");
            text.Should().Contain("A").And.Contain("B",
                "adjacent kept glyphs must survive, re-encoded with their ORIGINAL GBK code " +
                "bytes (a Unicode re-encode would not decode through the CMap)");

            // Carrier-agnostic saved-bytes check (CLAUDE.md): the secret must
            // not appear in ANY uncompressed carrier, in any of the encodings
            // a PDF can restate text in.
            var saved = File.ReadAllBytes(output);
            var haystack = Encoding.ASCII.GetString(saved)
                + Encoding.BigEndianUnicode.GetString(saved)
                + Encoding.UTF8.GetString(saved);
            haystack.Should().NotContain("汉字");
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    // ---------- helpers (same fixture builder as RegisteredCMapTests) ----------

    private static string Extract(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return new TextExtractor(doc.GetPage(1)).ExtractText();
    }

    /// <summary>
    /// Builds a single-page PDF with a non-embedded Type0 font. The content
    /// stream shows <paramref name="codesHex"/> as one hex string.
    /// </summary>
    private static byte[] BuildType0Pdf(string encodingName, string ordering, string codesHex)
    {
        var sb = new StringBuilder();
        var offsets = new long[6];
        void Obj(int n) => offsets[n] = sb.Length;

        var content = $"BT /F0 24 Tf 72 700 Td <{codesHex}> Tj ET";

        sb.Append("%PDF-1.7\n");
        Obj(1); sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n");
        Obj(2); sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        Obj(3); sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]" +
                          "/Resources<</Font<</F0 4 0 R>>>>/Contents 5 0 R>> endobj\n");
        Obj(4); sb.Append("4 0 obj <</Type/Font/Subtype/Type0/BaseFont/Test" +
                          $"/Encoding/{encodingName}" +
                          "/DescendantFonts[<</Type/Font/Subtype/CIDFontType2/BaseFont/Test" +
                          $"/CIDSystemInfo<</Registry(Adobe)/Ordering({ordering})/Supplement 0>>" +
                          "/DW 1000>>]>> endobj\n");
        Obj(5); sb.Append($"5 0 obj <</Length {content.Length}>>\nstream\n{content}\nendstream endobj\n");

        var xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++)
            sb.Append(offsets[i].ToString("D10") + " 00000 n \n");
        sb.Append($"trailer <</Size 6/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
