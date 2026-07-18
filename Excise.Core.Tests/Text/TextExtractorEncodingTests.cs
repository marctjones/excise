using System.IO;
using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// Tests for TextExtractor encoding paths: WinAnsiEncoding, MacRomanEncoding,
/// and LoadToUnicodeMap exception handling.
///
/// Coverage targets:
/// - Lines 970-1005: DecodeWinAnsi special-character switch for codes 128-159
/// - Lines 1008-1051: DecodeMacRoman special-character switch for codes 128-159
/// - Lines 930-938: LoadToUnicodeMap exception branch (malformed CMap)
/// </summary>
public class TextExtractorEncodingTests
{
    #region WinAnsiEncoding Tests (Lines 970-1005)

    [Fact]
    public void ExtractText_WinAnsiEncoding_Euro_Code128_DecodesCorrectly()
    {
        // Hit line 977: case 128 => Euro sign
        var pdfData = CreatePdfWithWinAnsiEncoding("BT /F1 12 Tf 100 700 Td <80> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("€"); // Euro sign
    }

    [Fact]
    public void ExtractText_WinAnsiEncoding_Ellipsis_Code133_DecodesCorrectly()
    {
        // Hit line 981: case 133 => Horizontal ellipsis
        var pdfData = CreatePdfWithWinAnsiEncoding("BT /F1 12 Tf 100 700 Td <85> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("…"); // Ellipsis
    }

    [Fact]
    public void ExtractText_WinAnsiEncoding_EmDash_Code151_DecodesCorrectly()
    {
        // Hit line 996: case 151 => Em dash
        var pdfData = CreatePdfWithWinAnsiEncoding("BT /F1 12 Tf 100 700 Td <97> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("—"); // Em dash
    }

    [Fact]
    public void ExtractText_WinAnsiEncoding_LeftDoubleQuote_Code147_DecodesCorrectly()
    {
        // Hit line 992: case 147 => Left double quotation mark
        var pdfData = CreatePdfWithWinAnsiEncoding("BT /F1 12 Tf 100 700 Td <93> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("“"); // Left double quote
    }

    [Fact]
    public void ExtractText_WinAnsiEncoding_RightDoubleQuote_Code148_DecodesCorrectly()
    {
        // Hit line 993: case 148 => Right double quotation mark
        var pdfData = CreatePdfWithWinAnsiEncoding("BT /F1 12 Tf 100 700 Td <94> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("”"); // Right double quote
    }

    [Fact]
    public void ExtractText_WinAnsiEncoding_Bullet_Code149_DecodesCorrectly()
    {
        // Hit line 994: case 149 => Bullet
        var pdfData = CreatePdfWithWinAnsiEncoding("BT /F1 12 Tf 100 700 Td <95> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("•"); // Bullet
    }

    [Fact]
    public void ExtractText_WinAnsiEncoding_Trademark_Code153_DecodesCorrectly()
    {
        // Hit line 998: case 153 => Trademark sign
        var pdfData = CreatePdfWithWinAnsiEncoding("BT /F1 12 Tf 100 700 Td <99> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("™"); // Trademark
    }

    [Fact]
    public void ExtractText_WinAnsiEncoding_Dagger_Code134_DecodesCorrectly()
    {
        // Hit line 982: case 134 => Dagger
        var pdfData = CreatePdfWithWinAnsiEncoding("BT /F1 12 Tf 100 700 Td <86> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("†"); // Dagger
    }

    [Fact]
    public void ExtractText_WinAnsiEncoding_PerMilleSign_Code137_DecodesCorrectly()
    {
        // Hit line 985: case 137 => Per mille sign
        var pdfData = CreatePdfWithWinAnsiEncoding("BT /F1 12 Tf 100 700 Td <89> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("‰"); // Per mille
    }

    [Fact]
    public void ExtractText_WinAnsiEncoding_BelowRange_Code100_PassThrough()
    {
        // Hit line 971-972: if (charCode < 128) return ((char)charCode)
        // Use code 100 ('d')
        var pdfData = CreatePdfWithWinAnsiEncoding("BT /F1 12 Tf 100 700 Td (d) Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("d");
    }

    [Fact]
    public void ExtractText_WinAnsiEncoding_AboveRange_Code200_PassThrough()
    {
        // Hit line 971-972: if (charCode >= 160) return ((char)charCode)
        // Use hex string for byte 0xC8
        var pdfData = CreatePdfWithWinAnsiEncoding("BT /F1 12 Tf 100 700 Td <C8> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be(((char)0xC8).ToString()); // Latin capital letter E with grave
    }

    #endregion

    #region MacRomanEncoding Tests (Lines 1008-1051)

    [Fact]
    public void ExtractText_MacRomanEncoding_CapitalA_Umlaut_Code128_DecodesCorrectly()
    {
        // Hit line 1017: case 128 => Latin capital letter A with diaeresis
        var pdfData = CreatePdfWithMacRomanEncoding("BT /F1 12 Tf 100 700 Td <80> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("Ä"); // A with umlaut
    }

    [Fact]
    public void ExtractText_MacRomanEncoding_CapitalE_Acute_Code131_DecodesCorrectly()
    {
        // Hit line 1020: case 131 => Latin capital letter E with acute
        var pdfData = CreatePdfWithMacRomanEncoding("BT /F1 12 Tf 100 700 Td <83> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("É"); // E with acute
    }

    [Fact]
    public void ExtractText_MacRomanEncoding_SmallN_Tilde_Code150_DecodesCorrectly()
    {
        // Hit line 1039: case 150 => Latin small letter n with tilde
        var pdfData = CreatePdfWithMacRomanEncoding("BT /F1 12 Tf 100 700 Td <96> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("ñ"); // n with tilde
    }

    [Fact]
    public void ExtractText_MacRomanEncoding_SmallU_Umlaut_Code159_DecodesCorrectly()
    {
        // Hit line 1048: case 159 => Latin small letter u with diaeresis
        var pdfData = CreatePdfWithMacRomanEncoding("BT /F1 12 Tf 100 700 Td <9F> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("ü"); // u with umlaut
    }

    [Fact]
    public void ExtractText_MacRomanEncoding_SmallE_Acute_Code142_DecodesCorrectly()
    {
        // Hit line 1031: case 142 => Latin small letter e with acute
        var pdfData = CreatePdfWithMacRomanEncoding("BT /F1 12 Tf 100 700 Td <8E> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("é"); // e with acute
    }

    [Fact]
    public void ExtractText_MacRomanEncoding_CapitalC_Cedilla_Code130_DecodesCorrectly()
    {
        // Hit line 1019: case 130 => Latin capital letter C with cedilla
        var pdfData = CreatePdfWithMacRomanEncoding("BT /F1 12 Tf 100 700 Td <82> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("Ç"); // C with cedilla
    }

    [Fact]
    public void ExtractText_MacRomanEncoding_CapitalA_Ring_Code129_DecodesCorrectly()
    {
        // Hit line 1018: case 129 => Latin capital letter A with ring above
        var pdfData = CreatePdfWithMacRomanEncoding("BT /F1 12 Tf 100 700 Td <81> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("Å"); // A with ring
    }

    [Fact]
    public void ExtractText_MacRomanEncoding_BelowRange_Code100_PassThrough()
    {
        // Hit line 1011-1012: if (charCode < 128) return ((char)charCode)
        var pdfData = CreatePdfWithMacRomanEncoding("BT /F1 12 Tf 100 700 Td (A) Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("A");
    }

    [Fact]
    public void ExtractText_MacRomanEncoding_UnmappedCode_DefaultCase_PassThrough()
    {
        // Hit line 1049: _ => ((char)charCode) for unmapped codes above 159
        // Code 200 is above the switch range
        var pdfData = CreatePdfWithMacRomanEncoding("BT /F1 12 Tf 100 700 Td <C8> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be(((char)0xC8).ToString()); // Fallback to character as-is
    }

    #endregion

    #region LoadToUnicodeMap Exception Handling (Lines 930-938)

    [Fact]
    public void ExtractText_ToUnicodeMapWithMalformedCMap_ThrowsOnParse_FallsBackToEncoding()
    {
        // Hit lines 930-938: catch block when ToUnicodeCMapParser.Parse throws
        // Provide a font with ToUnicode pointing to a stream of garbage bytes
        var pdfData = CreatePdfWithMalformedToUnicode("BT /F1 12 Tf 100 700 Td <41> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Should not throw; should fall back to encoding (WinAnsiEncoding)
        var letters = extractor.ExtractLetters();

        // Code 0x41 ('A') should decode via WinAnsiEncoding fallback
        letters.Should().NotBeEmpty();
        letters[0].Value.Should().Be("A");
    }

    [Fact]
    public void ExtractText_ToUnicodeMapInvalidSyntax_FallsBackToWinAnsi()
    {
        // Similar test: invalid CMap syntax (just raw bytes, no bfchar/bfrange)
        // should fall back gracefully
        var pdfData = CreatePdfWithInvalidToUnicodeSyntax("BT /F1 12 Tf 100 700 Td <99> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Should not throw; should fall back
        var letters = extractor.ExtractLetters();

        // Code 0x99 is trademark in WinAnsi
        letters.Should().NotBeEmpty();
        letters[0].Value.Should().Be("™"); // Fallback to WinAnsi: trademark
    }

    #endregion

    #region Helper Methods

    private static byte[] CreatePdfWithWinAnsiEncoding(string content)
    {
        return CreatePdfWithEncoding(content, "WinAnsiEncoding");
    }

    private static byte[] CreatePdfWithMacRomanEncoding(string content)
    {
        return CreatePdfWithEncoding(content, "MacRomanEncoding");
    }

    private static byte[] CreatePdfWithEncoding(string content, string encoding)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[6];

        // Object 1: Catalog
        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 2: Pages
        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 3: Page
        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 4: Content stream
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 5: Font with specified encoding
        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine($"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /{encoding} >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // xref
        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 6");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 5; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        // trailer
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 6 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] CreatePdfWithMalformedToUnicode(string content)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[7];

        // Object 1: Catalog
        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 2: Pages
        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 3: Page
        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 4: Content stream
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 6: Malformed ToUnicode CMap stream (garbage bytes)
        var malformedCMap = "This is not a valid CMap stream!!!@#$%^&*()";
        offsets[6] = ms.Position;
        writer.WriteLine("6 0 obj");
        writer.WriteLine($"<< /Length {malformedCMap.Length} >>");
        writer.WriteLine("stream");
        writer.Write(malformedCMap);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 5: Font with ToUnicode pointing to malformed stream
        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /ToUnicode 6 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // xref
        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 6; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        // trailer
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 7 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static byte[] CreatePdfWithInvalidToUnicodeSyntax(string content)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        var offsets = new long[7];

        // Object 1: Catalog
        offsets[1] = ms.Position;
        writer.WriteLine("1 0 obj");
        writer.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 2: Pages
        offsets[2] = ms.Position;
        writer.WriteLine("2 0 obj");
        writer.WriteLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 3: Page
        offsets[3] = ms.Position;
        writer.WriteLine("3 0 obj");
        writer.WriteLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 4: Content stream
        offsets[4] = ms.Position;
        writer.WriteLine("4 0 obj");
        writer.WriteLine($"<< /Length {content.Length} >>");
        writer.WriteLine("stream");
        writer.Write(content);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 6: Invalid CMap syntax (missing required structures)
        var invalidCMap = "/CIDInit /ProcSet findresource begin\n12 dict begin\nbeginbfchar\n[this is invalid]\nendbfchar\nend end";
        offsets[6] = ms.Position;
        writer.WriteLine("6 0 obj");
        writer.WriteLine($"<< /Length {invalidCMap.Length} >>");
        writer.WriteLine("stream");
        writer.Write(invalidCMap);
        writer.WriteLine();
        writer.WriteLine("endstream");
        writer.WriteLine("endobj");
        writer.Flush();

        // Object 5: Font with ToUnicode pointing to invalid stream
        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /ToUnicode 6 0 R >>");
        writer.WriteLine("endobj");
        writer.Flush();

        // xref
        long xrefPos = ms.Position;
        writer.WriteLine("xref");
        writer.WriteLine("0 7");
        writer.WriteLine("0000000000 65535 f ");
        for (int i = 1; i <= 6; i++)
            writer.WriteLine($"{offsets[i]:D10} 00000 n ");
        writer.Flush();

        // trailer
        writer.WriteLine("trailer");
        writer.WriteLine("<< /Root 1 0 R /Size 7 >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefPos.ToString());
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    #endregion
}
