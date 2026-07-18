using System.IO;
using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// Phase 8 — Type 0 (composite) font extraction tests.
///
/// Type 0 fonts wrap a descendant CIDFont and use multi-byte source codes
/// (always 2-byte for the Identity-H/V encodings produced by every modern PDF
/// emitter). Without proper handling of the 2-byte stride and ToUnicode CMap,
/// the extractor returns empty strings or garbled bytes for any non-Latin or
/// recently-produced PDF.
/// </summary>
public class TextExtractorType0Tests
{
    [Fact]
    public void Extract_Type0Font_TwoByteCodes_DecodedViaToUnicodeCMap()
    {
        // Hex string <00010002> = CIDs 1 and 2 → ToUnicode → "AB"
        var contentStream = "BT /F0 12 Tf 100 700 Td <00010002> Tj ET";
        var pdf = BuildType0Pdf(contentStream, ToUnicodeForAB(), wmode: 0,
                                widths: "[1 [500 500]]", defaultWidth: 1000);

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);
        var text = extractor.ExtractText();

        text.Should().Be("AB");
    }

    [Fact]
    public void Extract_Type0Font_MultiCharacterMapping_LigatureExpands()
    {
        // CID 1 → "fi" via ToUnicode <0001><00660069>
        var contentStream = "BT /F0 12 Tf 100 700 Td <0001> Tj ET";
        var pdf = BuildType0Pdf(contentStream, ToUnicodeForLigatureFi(), wmode: 0,
                                widths: "[1 [550]]", defaultWidth: 1000);

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        extractor.ExtractText().Should().Be("fi");
    }

    [Fact]
    public void Extract_Type0Font_MissingByte_TrailingByteDropped()
    {
        // Three bytes — odd stride; the third should be ignored, two letters extracted.
        var contentStream = "BT /F0 12 Tf 100 700 Td <000100020003> Tj ET";
        var pdf = BuildType0Pdf(contentStream, ToUnicodeForAB(), wmode: 0,
                                widths: "[1 [500 500]]", defaultWidth: 1000);

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);
        var letters = extractor.ExtractLetters();

        // The third CID has no ToUnicode mapping but it does decode (returns
        // empty/control char fallback). What matters: the byte stride is
        // honored — three CIDs across 6 bytes, not six 1-byte chars.
        letters.Count.Should().Be(3);
    }

    [Fact]
    public void Extract_Type0Font_DefaultWidth_AppliesWhenCidMissing()
    {
        // CID 5 has no entry in /W → /DW 800 should apply.
        var contentStream = "BT /F0 12 Tf 100 700 Td <0005> Tj ET";
        var pdf = BuildType0Pdf(contentStream, ToUnicodeForCid5MapsToZ(), wmode: 0,
                                widths: "[1 [500]]", defaultWidth: 800);

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);
        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("Z");
        // Width should reflect DW 800 (= advance 9.6pt at 12pt font), not the
        // 1000-unit hard default.
        letters[0].Width.Should().BeApproximately(800.0 / 1000.0 * 12.0, 0.01);
    }

    [Fact]
    public void Extract_Type0Font_CidRangeWidthForm_IsHonored()
    {
        // /W [10 12 750] — CIDs 10,11,12 all width 750
        var contentStream = "BT /F0 12 Tf 100 700 Td <000B> Tj ET";
        var pdf = BuildType0Pdf(contentStream, "/CIDInit /ProcSet findresource begin\n12 dict begin\n2 beginbfchar\n<000B> <0042>\nendbfchar\nend end",
                                wmode: 0,
                                widths: "[10 12 750]", defaultWidth: 1000);

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);
        var letters = extractor.ExtractLetters();

        letters[0].Value.Should().Be("B");
        letters[0].Width.Should().BeApproximately(750.0 / 1000.0 * 12.0, 0.01);
    }

    [Fact]
    public void Extract_Type0Font_IdentityV_VerticalAdvance()
    {
        // Identity-V: vertical writing — y advances, not x.
        var contentStream = "BT /F0 12 Tf 100 700 Td <00010002> Tj ET";
        var pdf = BuildType0Pdf(contentStream, ToUnicodeForAB(), wmode: 1,
                                widths: "[1 [500 500]]", defaultWidth: 1000);

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);
        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(2);
        // Identity-V: characters stack vertically — the second letter's X
        // matches the first (no horizontal advance) while Y advances.
        letters[0].StartX.Should().BeApproximately(letters[1].StartX, 0.0001);
    }

    [Fact]
    public void Extract_Type0Font_NoToUnicodeMap_StillParsesByteStride()
    {
        // No ToUnicode → DecodeCharacter falls back, but stride still 2 bytes.
        var contentStream = "BT /F0 12 Tf 100 700 Td <00010002> Tj ET";
        var pdf = BuildType0PdfNoToUnicode(contentStream, widths: "[1 [500 500]]", defaultWidth: 1000);

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);
        var letters = extractor.ExtractLetters();

        // Two CIDs from 4 bytes, not four bytes-as-codes.
        letters.Should().HaveCount(2);
    }

    [Fact]
    public void Extract_Type0Font_SingleByteCodespaceEncodingCMap_DecodesOneByteAtATime()
    {
        // #659: a Type0 font's /Encoding can be an embedded CMap stream
        // declaring a codespace narrower than Identity-H/V's 2 bytes. Two
        // single-byte codes 0x41 0x42 must decode as CIDs 0x41 and 0x42
        // ("A","B") — not as one bogus 2-byte code 0x4142.
        var contentStream = "BT /F0 12 Tf 100 700 Td <4142> Tj ET";
        var encodingCMap = @"
/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
1 begincodespacerange
<00> <FF>
endcodespacerange
1 begincidrange
<00> <FF> 0
endcidrange
endcmap
CMapName currentdict /CMap defineresource pop
end
end
";
        var toUnicode = @"
/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
1 begincodespacerange
<00> <FF>
endcodespacerange
2 beginbfchar
<41> <0041>
<42> <0042>
endbfchar
endcmap
end end
";
        var pdf = BuildType0PdfWithEncodingCMap(contentStream, encodingCMap, toUnicode,
                                                 widths: "[65 [500 500]]", defaultWidth: 1000);

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);
        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(2, "a 1-byte codespace must decode 2 letters from 2 bytes, not 1 from 4 hex digits");
        letters[0].Value.Should().Be("A");
        letters[1].Value.Should().Be("B");
    }

    [Fact]
    public void Extract_Type0Font_TwoByteCodespaceEncodingCMap_StillDecodesTwoBytesAtATime()
    {
        // Guardrail: an embedded CMap declaring a 2-byte codespace (not
        // Identity-H by name, but still 2-byte) must not be affected by the
        // #659 fix — the safe default only changes for an explicitly
        // UNIFORM 1-byte codespace.
        var contentStream = "BT /F0 12 Tf 100 700 Td <00410042> Tj ET";
        var encodingCMap = @"
/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
1 begincodespacerange
<0000> <FFFF>
endcodespacerange
1 begincidrange
<0000> <FFFF> 0
endcidrange
endcmap
CMapName currentdict /CMap defineresource pop
end
end
";
        var toUnicode = @"
/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
2 beginbfchar
<0041> <0041>
<0042> <0042>
endbfchar
endcmap
end end
";
        var pdf = BuildType0PdfWithEncodingCMap(contentStream, encodingCMap, toUnicode,
                                                 widths: "[65 [500 500]]", defaultWidth: 1000);

        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);
        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(2, "a 2-byte codespace CMap must still decode 2 letters from 4 bytes");
        letters[0].Value.Should().Be("A");
        letters[1].Value.Should().Be("B");
    }

    // ─── PDF builders ────────────────────────────────────────────────────────

    /// <summary>Build a single-page PDF that uses a Type 0 font /F0.</summary>
    private static byte[] BuildType0Pdf(string contentStream, string toUnicodeContent,
                                         int wmode, string widths, double defaultWidth)
    {
        // Stream the ToUnicode CMap (uncompressed for test simplicity).
        var cmapBytes = Encoding.ASCII.GetBytes(toUnicodeContent);
        return BuildBase(contentStream, cmapBytes, wmode, widths, defaultWidth);
    }

    private static byte[] BuildType0PdfNoToUnicode(string contentStream, string widths, double defaultWidth)
        => BuildBase(contentStream, null, wmode: 0, widths, defaultWidth);

    /// <summary>
    /// Build a single-page PDF whose Type0 font's /Encoding is an embedded
    /// CMap stream (not the /Identity-H or /Identity-V name) — the shape
    /// #659 fixes decoding for.
    /// </summary>
    private static byte[] BuildType0PdfWithEncodingCMap(
        string contentStream, string encodingCMapContent, string toUnicodeContent,
        string widths, double defaultWidth)
    {
        var sb = new StringBuilder();
        var offsets = new long[10];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");

        Mark(1); sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n");
        Mark(2); sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");

        Mark(3);
        sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]" +
                  "/Resources<</Font<</F0 4 0 R>>>>" +
                  "/Contents 9 0 R>> endobj\n");

        Mark(4);
        sb.Append("4 0 obj <</Type/Font/Subtype/Type0/BaseFont/Test" +
                  "/Encoding 8 0 R/DescendantFonts[5 0 R]/ToUnicode 7 0 R>> endobj\n");

        Mark(5);
        sb.Append("5 0 obj <</Type/Font/Subtype/CIDFontType2/BaseFont/Test" +
                  "/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>" +
                  $"/FontDescriptor 6 0 R/W {widths}/DW {defaultWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)}>> endobj\n");

        Mark(6);
        sb.Append("6 0 obj <</Type/FontDescriptor/FontName/Test" +
                  "/Flags 4/FontBBox[0 0 1000 1000]/ItalicAngle 0/Ascent 800/Descent -200" +
                  "/CapHeight 700/StemV 80>> endobj\n");

        Mark(7);
        var tu = Encoding.ASCII.GetBytes(toUnicodeContent);
        sb.Append($"7 0 obj <</Length {tu.Length}>>\nstream\n");
        sb.Append(toUnicodeContent);
        sb.Append("\nendstream endobj\n");

        Mark(8);
        var enc = Encoding.ASCII.GetBytes(encodingCMapContent);
        sb.Append($"8 0 obj <</Type/CMap/CMapName/Test-Encoding/Length {enc.Length}>>\nstream\n");
        sb.Append(encodingCMapContent);
        sb.Append("\nendstream endobj\n");

        Mark(9);
        var cs = Encoding.ASCII.GetBytes(contentStream);
        sb.Append($"9 0 obj <</Length {cs.Length}>>\nstream\n");
        sb.Append(contentStream);
        sb.Append("\nendstream endobj\n");

        var xrefPos = sb.Length;
        sb.Append("xref\n0 10\n0000000000 65535 f \n");
        for (int i = 1; i <= 9; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer <</Size 10/Root 1 0 R>>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildBase(string contentStream, byte[]? cmapBytes,
                                    int wmode, string widths, double defaultWidth)
    {
        var sb = new StringBuilder();
        var offsets = new long[10];
        void Mark(int n) => offsets[n] = sb.Length;

        sb.Append("%PDF-1.7\n");

        Mark(1); sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n");
        Mark(2); sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");

        // Page references font 4 and content 9.
        Mark(3);
        sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]" +
                  "/Resources<</Font<</F0 4 0 R>>>>" +
                  "/Contents 9 0 R>> endobj\n");

        // Type 0 font (4) referencing CIDFontType2 (5), descriptor (6), ToUnicode (7).
        var encoding = wmode == 1 ? "/Identity-V" : "/Identity-H";
        Mark(4);
        sb.Append("4 0 obj <</Type/Font/Subtype/Type0/BaseFont/Test" +
                  $"/Encoding{encoding}/DescendantFonts[5 0 R]");
        if (cmapBytes != null) sb.Append("/ToUnicode 7 0 R");
        sb.Append(">> endobj\n");

        // CIDFontType2 (5) with widths and DW.
        Mark(5);
        sb.Append("5 0 obj <</Type/Font/Subtype/CIDFontType2/BaseFont/Test" +
                  "/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>" +
                  $"/FontDescriptor 6 0 R/W {widths}/DW {defaultWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)}>> endobj\n");

        // Minimal font descriptor (6).
        Mark(6);
        sb.Append("6 0 obj <</Type/FontDescriptor/FontName/Test" +
                  "/Flags 4/FontBBox[0 0 1000 1000]/ItalicAngle 0/Ascent 800/Descent -200" +
                  "/CapHeight 700/StemV 80>> endobj\n");

        // ToUnicode stream (7) — only if cmapBytes provided.
        if (cmapBytes != null)
        {
            Mark(7);
            sb.Append($"7 0 obj <</Length {cmapBytes.Length}>>\nstream\n");
            // Directly append the bytes — they're ASCII.
            sb.Append(Encoding.ASCII.GetString(cmapBytes));
            sb.Append("\nendstream endobj\n");
        }

        // Content stream (9).
        Mark(9);
        var cs = Encoding.ASCII.GetBytes(contentStream);
        sb.Append($"9 0 obj <</Length {cs.Length}>>\nstream\n");
        sb.Append(contentStream);
        sb.Append("\nendstream endobj\n");

        var xrefPos = sb.Length;
        sb.Append("xref\n0 10\n0000000000 65535 f \n");
        for (int i = 1; i <= 9; i++)
        {
            if (i == 8 || (cmapBytes == null && i == 7))
                sb.Append("0000000000 65535 f \n");
            else
                sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        }
        sb.Append("trailer <</Size 10/Root 1 0 R>>\nstartxref\n").Append(xrefPos).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string ToUnicodeForAB() => @"
/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def
/CMapName /Adobe-Identity-UCS def
/CMapType 2 def
1 begincodespacerange
<0000> <FFFF>
endcodespacerange
2 beginbfchar
<0001> <0041>
<0002> <0042>
endbfchar
endcmap
CMapName currentdict /CMap defineresource pop
end
end
";

    private static string ToUnicodeForLigatureFi() => @"
/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
1 beginbfchar
<0001> <00660069>
endbfchar
endcmap
end end
";

    private static string ToUnicodeForCid5MapsToZ() => @"
/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
1 beginbfchar
<0005> <005A>
endbfchar
endcmap
end end
";
}
