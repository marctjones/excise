using System.Text;
using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Xunit;

namespace Excise.Core.Tests.Content;

/// <summary>
/// Tests for Type0 (CID) font support in ContentStreamParser.
/// Type0 fonts use 2-byte character codes and require descendant CID font dictionaries.
/// </summary>
public class CidFontTests
{
    /// <summary>
    /// Test parsing Type0 font with 2-byte hex string character codes.
    /// </summary>
    [Fact]
    public void Parse_Type0Font_2ByteHexString_ExtractsText()
    {
        var pdf = BuildPdfWithType0Font();

        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var page = doc.GetPage(1);
        var stream = page.GetContentStream();

        var tjOp = stream.Operators.FirstOrDefault(o => o.Name == "Tj");
        tjOp.Should().NotBeNull();
        tjOp!.TextContent.Should().Be("AB");
    }

    /// <summary>
    /// Test parsing Type0 font with TJ array containing 2-byte character codes.
    /// </summary>
    [Fact]
    public void Parse_Type0Font_TJ_Array_ExtractsText()
    {
        var pdf = BuildPdfWithType0FontTJ();

        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var page = doc.GetPage(1);
        var stream = page.GetContentStream();

        var tjOp = stream.Operators.FirstOrDefault(o => o.Name == "TJ");
        tjOp.Should().NotBeNull();
        tjOp!.TextContent.Should().Be("AB");
    }

    /// <summary>
    /// Test that Type1 (non-CID) fonts still use 1-byte character codes.
    /// Verifies that non-Type0 fonts are unaffected by CID support.
    /// </summary>
    [Fact]
    public void Parse_Type1Font_1ByteCharCode_Unaffected()
    {
        var pdf = BuildPdfWithType1Font();

        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var page = doc.GetPage(1);
        var stream = page.GetContentStream();

        var tjOp = stream.Operators.FirstOrDefault(o => o.Name == "Tj");
        tjOp.Should().NotBeNull();
        tjOp!.TextContent.Should().Be("Hello");
    }

    /// <summary>
    /// Test Type0 font with CID width table (W array) parsing.
    /// </summary>
    [Fact]
    public void Parse_Type0Font_WithCidWidthTable_CalculatesBounds()
    {
        var pdf = BuildPdfWithType0FontAndCidWidths();

        using var doc = PdfDocument.Open(new MemoryStream(pdf), false);
        var page = doc.GetPage(1);
        var stream = page.GetContentStream();

        var tjOp = stream.Operators.FirstOrDefault(o => o.Name == "Tj");
        tjOp.Should().NotBeNull();
        tjOp!.TextContent.Should().Be("AB");
        tjOp!.BoundingBox.Should().NotBeNull();
    }

    #region PDF Building Helpers

    /// <summary>
    /// Build a minimal PDF with Type0 font using Tj operator.
    /// Creates:
    /// - A page with /Resources << /Font << /F1 <ref> >> >>
    /// - Type0 font dictionary
    /// - Descendant CIDFont dictionary
    /// - ToUnicode CMap stream mapping 0x0041→"A", 0x0042→"B"
    /// - Content stream with: BT /F1 12 Tf <00410042> Tj ET
    /// </summary>
    private byte[] BuildPdfWithType0Font()
    {
        var sb = new StringBuilder();
        var objects = new List<(int objNum, string content)>();

        int toUnicodeObjNum = 10;
        int cidFontObjNum = 11;
        int type0FontObjNum = 12;
        int pageObjNum = 2;
        int contentsObjNum = 3;

        // 1. Root catalog
        objects.Add((1, @"<< /Type /Catalog /Pages 4 0 R >>"));

        // 2. Page object
        objects.Add((pageObjNum, $@"<< /Type /Page /Parent 4 0 R /MediaBox [0 0 612 792] /Contents {contentsObjNum} 0 R /Resources << /Font << /F1 {type0FontObjNum} 0 R >> >> >>"));

        // 3. Content stream
        var contentStream = @"BT
/F1 12 Tf
<00410042> Tj
ET";
        var contentBytes = Encoding.ASCII.GetBytes(contentStream);
        objects.Add((contentsObjNum, $@"<< /Length {contentBytes.Length} >>
stream
{contentStream}
endstream"));

        // 4. Pages tree
        objects.Add((4, $@"<< /Type /Pages /Kids [{pageObjNum} 0 R] /Count 1 >>"));

        // 10. ToUnicode CMap
        var cmapContent = @"
/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
/CIDSystemInfo
<< /Registry (Adobe)
/Ordering (UCS)
/Supplement 0
>> def
/CMapName /Adobe-Identity-UCS def
/CMapType 2 def
1 begincodespacerange
<0000> <FFFF>
endcodespacerange
1 beginbfchar
<0041> <0041>
<0042> <0042>
endbfchar
endcmap
CMapName currentdict /CMap defineresource pop
end
end
";
        var cmapBytes = Encoding.UTF8.GetBytes(cmapContent);
        objects.Add((toUnicodeObjNum, $@"<< /Length {cmapBytes.Length} >>
stream
{cmapContent}
endstream"));

        // 11. Descendant CIDFont
        objects.Add((cidFontObjNum, @"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Arial /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 1000 >>"));

        // 12. Type0 font
        objects.Add((type0FontObjNum, $@"<< /Type /Font /Subtype /Type0 /BaseFont /Arial /Encoding /Identity-H /DescendantFonts [{cidFontObjNum} 0 R] /ToUnicode {toUnicodeObjNum} 0 R >>"));

        return BuildPdfBytes(objects);
    }

    /// <summary>
    /// Build a minimal PDF with Type0 font using TJ operator (array).
    /// </summary>
    private byte[] BuildPdfWithType0FontTJ()
    {
        var sb = new StringBuilder();
        var objects = new List<(int objNum, string content)>();

        int toUnicodeObjNum = 10;
        int cidFontObjNum = 11;
        int type0FontObjNum = 12;
        int pageObjNum = 2;
        int contentsObjNum = 3;

        // 1. Root catalog
        objects.Add((1, @"<< /Type /Catalog /Pages 4 0 R >>"));

        // 2. Page object
        objects.Add((pageObjNum, $@"<< /Type /Page /Parent 4 0 R /MediaBox [0 0 612 792] /Contents {contentsObjNum} 0 R /Resources << /Font << /F1 {type0FontObjNum} 0 R >> >> >>"));

        // 3. Content stream with TJ array
        var contentStream = @"BT
/F1 12 Tf
[<0041> 20 <0042>] TJ
ET";
        var contentBytes = Encoding.ASCII.GetBytes(contentStream);
        objects.Add((contentsObjNum, $@"<< /Length {contentBytes.Length} >>
stream
{contentStream}
endstream"));

        // 4. Pages tree
        objects.Add((4, $@"<< /Type /Pages /Kids [{pageObjNum} 0 R] /Count 1 >>"));

        // 10. ToUnicode CMap
        var cmapContent = @"
/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
/CIDSystemInfo
<< /Registry (Adobe)
/Ordering (UCS)
/Supplement 0
>> def
/CMapName /Adobe-Identity-UCS def
/CMapType 2 def
1 begincodespacerange
<0000> <FFFF>
endcodespacerange
1 beginbfchar
<0041> <0041>
<0042> <0042>
endbfchar
endcmap
CMapName currentdict /CMap defineresource pop
end
end
";
        var cmapBytes = Encoding.UTF8.GetBytes(cmapContent);
        objects.Add((toUnicodeObjNum, $@"<< /Length {cmapBytes.Length} >>
stream
{cmapContent}
endstream"));

        // 11. Descendant CIDFont
        objects.Add((cidFontObjNum, @"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Arial /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 1000 >>"));

        // 12. Type0 font
        objects.Add((type0FontObjNum, $@"<< /Type /Font /Subtype /Type0 /BaseFont /Arial /Encoding /Identity-H /DescendantFonts [{cidFontObjNum} 0 R] /ToUnicode {toUnicodeObjNum} 0 R >>"));

        return BuildPdfBytes(objects);
    }

    /// <summary>
    /// Build a minimal PDF with Type1 font to verify non-Type0 fonts still work.
    /// </summary>
    private byte[] BuildPdfWithType1Font()
    {
        var objects = new List<(int objNum, string content)>();

        int type1FontObjNum = 10;
        int pageObjNum = 2;
        int contentsObjNum = 3;

        // 1. Root catalog
        objects.Add((1, @"<< /Type /Catalog /Pages 4 0 R >>"));

        // 2. Page object
        objects.Add((pageObjNum, $@"<< /Type /Page /Parent 4 0 R /MediaBox [0 0 612 792] /Contents {contentsObjNum} 0 R /Resources << /Font << /F1 {type1FontObjNum} 0 R >> >> >>"));

        // 3. Content stream
        var contentStream = @"BT
/F1 12 Tf
(Hello) Tj
ET";
        var contentBytes = Encoding.ASCII.GetBytes(contentStream);
        objects.Add((contentsObjNum, $@"<< /Length {contentBytes.Length} >>
stream
{contentStream}
endstream"));

        // 4. Pages tree
        objects.Add((4, $@"<< /Type /Pages /Kids [{pageObjNum} 0 R] /Count 1 >>"));

        // 10. Type1 font (no ToUnicode, just basic font dict)
        objects.Add((type1FontObjNum, @"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));

        return BuildPdfBytes(objects);
    }

    /// <summary>
    /// Build a minimal PDF with Type0 font including CID width table (W array).
    /// </summary>
    private byte[] BuildPdfWithType0FontAndCidWidths()
    {
        var objects = new List<(int objNum, string content)>();

        int toUnicodeObjNum = 10;
        int cidFontObjNum = 11;
        int type0FontObjNum = 12;
        int pageObjNum = 2;
        int contentsObjNum = 3;

        // 1. Root catalog
        objects.Add((1, @"<< /Type /Catalog /Pages 4 0 R >>"));

        // 2. Page object
        objects.Add((pageObjNum, $@"<< /Type /Page /Parent 4 0 R /MediaBox [0 0 612 792] /Contents {contentsObjNum} 0 R /Resources << /Font << /F1 {type0FontObjNum} 0 R >> >> >>"));

        // 3. Content stream
        var contentStream = @"BT
/F1 12 Tf
<00410042> Tj
ET";
        var contentBytes = Encoding.ASCII.GetBytes(contentStream);
        objects.Add((contentsObjNum, $@"<< /Length {contentBytes.Length} >>
stream
{contentStream}
endstream"));

        // 4. Pages tree
        objects.Add((4, $@"<< /Type /Pages /Kids [{pageObjNum} 0 R] /Count 1 >>"));

        // 10. ToUnicode CMap
        var cmapContent = @"
/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
/CIDSystemInfo
<< /Registry (Adobe)
/Ordering (UCS)
/Supplement 0
>> def
/CMapName /Adobe-Identity-UCS def
/CMapType 2 def
1 begincodespacerange
<0000> <FFFF>
endcodespacerange
1 beginbfchar
<0041> <0041>
<0042> <0042>
endbfchar
endcmap
CMapName currentdict /CMap defineresource pop
end
end
";
        var cmapBytes = Encoding.UTF8.GetBytes(cmapContent);
        objects.Add((toUnicodeObjNum, $@"<< /Length {cmapBytes.Length} >>
stream
{cmapContent}
endstream"));

        // 11. Descendant CIDFont with W (width) array
        // W array format: c [w1 w2 w3] or c1 c2 w
        // Here: 0x0041 has width 600, 0x0042 has width 700
        objects.Add((cidFontObjNum, @"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Arial /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 1000 /W [65 [600] 66 [700]] >>"));

        // 12. Type0 font
        objects.Add((type0FontObjNum, $@"<< /Type /Font /Subtype /Type0 /BaseFont /Arial /Encoding /Identity-H /DescendantFonts [{cidFontObjNum} 0 R] /ToUnicode {toUnicodeObjNum} 0 R >>"));

        return BuildPdfBytes(objects);
    }

    /// <summary>
    /// Build PDF bytes from object list.
    /// Creates a minimal valid PDF with xref table.
    /// </summary>
    private byte[] BuildPdfBytes(List<(int objNum, string content)> objects)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.4");

        // Sort objects by number
        var sorted = objects.OrderBy(x => x.objNum).ToList();

        // Calculate byte offsets for xref
        var offsets = new Dictionary<int, long>();
        var currentPos = sb.ToString().Length;

        foreach (var (objNum, content) in sorted)
        {
            offsets[objNum] = currentPos;
            var objStr = $"{objNum} 0 obj\n{content}\nendobj\n";
            sb.Append(objStr);
            currentPos += Encoding.UTF8.GetByteCount(objStr);
        }

        // xref table
        long xrefPos = currentPos;
        sb.AppendLine("xref");
        sb.AppendLine($"0 {sorted.Max(x => x.objNum) + 1}");
        sb.AppendLine("0000000000 65535 f");

        for (int i = 1; i <= sorted.Max(x => x.objNum); i++)
        {
            if (offsets.TryGetValue(i, out var offset))
                sb.AppendLine($"{offset:D10} 00000 n");
            else
                sb.AppendLine("0000000000 00000 f");
        }

        // trailer
        sb.AppendLine("trailer");
        sb.AppendLine($@"<< /Size {sorted.Max(x => x.objNum) + 1} /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefPos.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    #endregion
}
