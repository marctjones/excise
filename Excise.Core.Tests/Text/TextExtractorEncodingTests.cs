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
/// - MacRomanEncoding, every high code against ISO 32000-2 Annex D (#1831)
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
    public void ExtractText_MacRomanEncoding_Code200_IsGuillemotRight()
    {
        // Annex D, Table D.2: guillemotright is MAC 310 (0xC8). The Latin-1
        // reading (U+00C8, È) was the pre-#1831 fallback for every code > 159.
        var pdfData = CreatePdfWithMacRomanEncoding("BT /F1 12 Tf 100 700 Td <C8> Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be("»");
    }

    /// <summary>
    /// #1831: every high code ISO 32000-2 Annex D, Table D.2 assigns in the MAC
    /// column, as (octal code, glyph name, AGL code point). Transcribed from the
    /// spec by name; mutool extracts the same code point for each (it splits
    /// fi/fl into two letters). 312 is note 6's non-breaking space and is
    /// pinned separately.
    /// </summary>
    private const string AnnexDMacRomanHigh = """
        200 Adieresis 00C4
        201 Aring 00C5
        202 Ccedilla 00C7
        203 Eacute 00C9
        204 Ntilde 00D1
        205 Odieresis 00D6
        206 Udieresis 00DC
        207 aacute 00E1
        210 agrave 00E0
        211 acircumflex 00E2
        212 adieresis 00E4
        213 atilde 00E3
        214 aring 00E5
        215 ccedilla 00E7
        216 eacute 00E9
        217 egrave 00E8
        220 ecircumflex 00EA
        221 edieresis 00EB
        222 iacute 00ED
        223 igrave 00EC
        224 icircumflex 00EE
        225 idieresis 00EF
        226 ntilde 00F1
        227 oacute 00F3
        230 ograve 00F2
        231 ocircumflex 00F4
        232 odieresis 00F6
        233 otilde 00F5
        234 uacute 00FA
        235 ugrave 00F9
        236 ucircumflex 00FB
        237 udieresis 00FC
        240 dagger 2020
        241 degree 00B0
        242 cent 00A2
        243 sterling 00A3
        244 section 00A7
        245 bullet 2022
        246 paragraph 00B6
        247 germandbls 00DF
        250 registered 00AE
        251 copyright 00A9
        252 trademark 2122
        253 acute 00B4
        254 dieresis 00A8
        256 AE 00C6
        257 Oslash 00D8
        261 plusminus 00B1
        264 yen 00A5
        265 mu 00B5
        273 ordfeminine 00AA
        274 ordmasculine 00BA
        276 ae 00E6
        277 oslash 00F8
        300 questiondown 00BF
        301 exclamdown 00A1
        302 logicalnot 00AC
        304 florin 0192
        307 guillemotleft 00AB
        310 guillemotright 00BB
        311 ellipsis 2026
        313 Agrave 00C0
        314 Atilde 00C3
        315 Otilde 00D5
        316 OE 0152
        317 oe 0153
        320 endash 2013
        321 emdash 2014
        322 quotedblleft 201C
        323 quotedblright 201D
        324 quoteleft 2018
        325 quoteright 2019
        326 divide 00F7
        330 ydieresis 00FF
        331 Ydieresis 0178
        332 fraction 2044
        333 currency 00A4
        334 guilsinglleft 2039
        335 guilsinglright 203A
        336 fi FB01
        337 fl FB02
        340 daggerdbl 2021
        341 periodcentered 00B7
        342 quotesinglbase 201A
        343 quotedblbase 201E
        344 perthousand 2030
        345 Acircumflex 00C2
        346 Ecircumflex 00CA
        347 Aacute 00C1
        350 Edieresis 00CB
        351 Egrave 00C8
        352 Iacute 00CD
        353 Icircumflex 00CE
        354 Idieresis 00CF
        355 Igrave 00CC
        356 Oacute 00D3
        357 Ocircumflex 00D4
        361 Ograve 00D2
        362 Uacute 00DA
        363 Ucircumflex 00DB
        364 Ugrave 00D9
        365 dotlessi 0131
        366 circumflex 02C6
        367 tilde 02DC
        370 macron 00AF
        371 breve 02D8
        372 dotaccent 02D9
        373 ring 02DA
        374 cedilla 00B8
        375 hungarumlaut 02DD
        376 ogonek 02DB
        377 caron 02C7
        """;

    public static TheoryData<int, string, int> AnnexDMacRomanHighCodes()
    {
        var data = new TheoryData<int, string, int>();
        foreach (var line in AnnexDMacRomanHigh.Split('\n'))
        {
            var f = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            data.Add(System.Convert.ToInt32(f[0], 8), f[1], System.Convert.ToInt32(f[2], 16));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AnnexDMacRomanHighCodes))]
    public void ExtractText_MacRomanEncoding_HighCode_DecodesPerAnnexD(int code, string glyphName, int codePoint)
    {
        var pdfData = CreatePdfWithMacRomanEncoding($"BT /F1 12 Tf 100 700 Td <{code:X2}> Tj ET");
        using var doc = PdfDocument.Open(pdfData);

        var letters = new TextExtractor(doc.GetPage(1)).ExtractLetters();

        letters.Should().HaveCount(1);
        letters[0].Value.Should().Be(char.ConvertFromUtf32(codePoint),
            $"Annex D assigns MacRomanEncoding code {code:X2} to /{glyphName}");
    }

    [Theory]
    [InlineData(0xDB, 0x00A4)] // note 1: Apple moved 333 to the euro; PDF's MacRomanEncoding did not
    [InlineData(0xCA, 0x00A0)] // note 6: 312 is a second space that signifies a non-breaking space
    [InlineData(0xAD, 0x2260)] // unassigned in Annex D: Mac OS Roman's notequal, as poppler decodes it
    [InlineData(0xC6, 0x2206)] // unassigned in Annex D: Mac OS Roman's increment (AGL Delta)
    public void ExtractText_MacRomanEncoding_CodesOutsideTableD2Body(int code, int codePoint)
    {
        var pdfData = CreatePdfWithMacRomanEncoding($"BT /F1 12 Tf 100 700 Td <{code:X2}> Tj ET");
        using var doc = PdfDocument.Open(pdfData);

        var letters = new TextExtractor(doc.GetPage(1)).ExtractLetters();

        letters.Should().ContainSingle().Which.Value.Should().Be(char.ConvertFromUtf32(codePoint));
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
