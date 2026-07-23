using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// #715 / #515 — a <c>/ToUnicode</c> that is the predefined-CMap NAME
/// <c>/Identity-H</c> (or <c>/Identity-V</c>) declares code == UTF-16BE Unicode.
/// It must be decoded directly, not left to the WinAnsi fallback (which is only
/// identity by coincidence and mis-maps 2-byte codes whose value is 128–159).
/// </summary>
public class PredefinedToUnicodeCMapTests
{
    [Fact]
    public void IdentityHToUnicode_DecodesTwoByteCodesAsUtf16BeUnicode()
    {
        // Codes: 0x0041 'A', 0x0091 (a value in the 128–159 band), 0x0042 'B'.
        var pdf = BuildType0IdentityToUnicodePdf("BT /F0 12 Tf 100 700 Td <004100910042> Tj ET");
        using var doc = PdfDocument.Open(pdf);
        var text = new TextExtractor(doc.GetPage(1)).ExtractText();

        // With /ToUnicode /Identity-H the middle code is U+0091, NOT the WinAnsi
        // CP1252 remap U+2019 (’) the old fallback produced.
        text.Should().Be("AB");
        text.Should().NotContain("’", "code 0x91 must decode as U+0091 (Identity), not the WinAnsi ’");
    }

    private static byte[] BuildType0IdentityToUnicodePdf(string content)
    {
        var sb = new StringBuilder();
        var off = new long[8];
        void Mark(int n) => off[n] = Encoding.ASCII.GetByteCount(sb.ToString());

        sb.Append("%PDF-1.7\n");
        Mark(1); sb.Append("1 0 obj <</Type/Catalog/Pages 2 0 R>> endobj\n");
        Mark(2); sb.Append("2 0 obj <</Type/Pages/Count 1/Kids[3 0 R]>> endobj\n");
        Mark(3); sb.Append("3 0 obj <</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]"
                         + "/Resources<</Font<</F0 4 0 R>>>>/Contents 7 0 R>> endobj\n");
        // Type0 with predefined-NAME /ToUnicode /Identity-H (not a stream).
        Mark(4); sb.Append("4 0 obj <</Type/Font/Subtype/Type0/BaseFont/Test"
                         + "/Encoding/Identity-H/ToUnicode/Identity-H/DescendantFonts[5 0 R]>> endobj\n");
        Mark(5); sb.Append("5 0 obj <</Type/Font/Subtype/CIDFontType2/BaseFont/Test"
                         + "/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>"
                         + "/FontDescriptor 6 0 R/CIDToGIDMap/Identity/DW 1000>> endobj\n");
        Mark(6); sb.Append("6 0 obj <</Type/FontDescriptor/FontName/Test/Flags 4"
                         + "/FontBBox[0 0 1000 1000]/ItalicAngle 0/Ascent 900/Descent -200"
                         + "/CapHeight 700/StemV 80>> endobj\n");
        Mark(7); sb.Append($"7 0 obj <</Length {content.Length}>> stream\n{content}\nendstream endobj\n");

        long xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        for (int i = 1; i <= 7; i++) sb.Append($"{off[i]:D10} 00000 n \n");
        sb.Append($"trailer <</Root 1 0 R/Size 8>>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
