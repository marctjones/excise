using System.Text;
using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

/// <summary>
/// Tests for TextExtractor.GetStandardFontWidth method.
/// Covers all switch arms in Helvetica character width table (ASCII 32-122),
/// Courier fixed-width path, default fallback, and encoding methods.
/// </summary>
public class StandardFontWidthTests
{
    /// <summary>
    /// Theory test for Helvetica character widths.
    /// Tests all switch arms in the GetStandardFontWidth Helvetica branch.
    /// Each test extracts a single character with Helvetica /F1 font
    /// and verifies the Letter width matches the expected width value.
    /// </summary>
    [Theory]
    // Space and ASCII punctuation
    [InlineData(32, 278)]    // space
    // Uppercase A-Z (ASCII 65-90)
    [InlineData(65, 667)]    // A
    [InlineData(66, 667)]    // B
    [InlineData(67, 722)]    // C
    [InlineData(68, 722)]    // D
    [InlineData(69, 667)]    // E
    [InlineData(70, 611)]    // F
    [InlineData(71, 778)]    // G
    [InlineData(72, 722)]    // H
    [InlineData(73, 278)]    // I
    [InlineData(74, 500)]    // J
    [InlineData(75, 667)]    // K
    [InlineData(76, 556)]    // L
    [InlineData(77, 833)]    // M
    [InlineData(78, 722)]    // N
    [InlineData(79, 778)]    // O
    [InlineData(80, 667)]    // P
    [InlineData(81, 778)]    // Q
    [InlineData(82, 722)]    // R
    [InlineData(83, 667)]    // S
    [InlineData(84, 611)]    // T
    [InlineData(85, 722)]    // U
    [InlineData(86, 667)]    // V
    [InlineData(87, 944)]    // W
    [InlineData(88, 667)]    // X
    [InlineData(89, 667)]    // Y
    [InlineData(90, 611)]    // Z
    // Lowercase a-z (ASCII 97-122)
    [InlineData(97, 556)]    // a
    [InlineData(98, 556)]    // b
    [InlineData(99, 500)]    // c
    [InlineData(100, 556)]   // d
    [InlineData(101, 556)]   // e
    [InlineData(102, 278)]   // f
    [InlineData(103, 556)]   // g
    [InlineData(104, 556)]   // h
    [InlineData(105, 222)]   // i
    [InlineData(106, 222)]   // j
    [InlineData(107, 500)]   // k
    [InlineData(108, 222)]   // l
    [InlineData(109, 833)]   // m
    [InlineData(110, 556)]   // n
    [InlineData(111, 556)]   // o
    [InlineData(112, 556)]   // p
    [InlineData(113, 556)]   // q
    [InlineData(114, 333)]   // r
    [InlineData(115, 500)]   // s
    [InlineData(116, 278)]   // t
    [InlineData(117, 556)]   // u
    [InlineData(118, 500)]   // v
    [InlineData(119, 722)]   // w
    [InlineData(120, 500)]   // x
    [InlineData(121, 500)]   // y
    [InlineData(122, 500)]   // z
    public void ExtractLetters_HelveticaCharacterWidths_ReturnsExpectedWidth(int charCode, int expectedWidthUnits)
    {
        // Arrange
        char c = (char)charCode;
        var contentStream = $"BT /F1 12 Tf 100 700 Td ({c}) Tj ET";
        var pdfData = CreatePdfWithContentStream(contentStream, "Helvetica");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty("Character should be extracted");
        var letter = letters[0];

        // Convert from PDF units (1/1000 of font size in points) to display width
        // At 12pt font size, width = (widthUnits / 1000) * 12
        double expectedDisplayWidth = (expectedWidthUnits / 1000.0) * 12;

        // Allow small tolerance for floating-point math and rendering
        letter.Width.Should().BeApproximately(expectedDisplayWidth, 0.5,
            $"Character {c} (code {charCode}) should have width {expectedDisplayWidth}");
    }

    /// <summary>
    /// Test Courier fixed-width path.
    /// Courier is a monospace font where all glyphs are 600 units wide.
    /// </summary>
    [Fact]
    public void ExtractLetters_CourierFont_AllCharactersHaveFixedWidth()
    {
        // Arrange
        const int expectedWidthUnits = 600;
        const double fontSize = 12;
        const double expectedDisplayWidth = (expectedWidthUnits / 1000.0) * fontSize;
        const double tolerance = 0.5;

        // Test multiple different characters to verify fixed width applies to all
        var testChars = new[] { 'A', 'a', '1', ' ', 'W' };

        foreach (var c in testChars)
        {
            var contentStream = $"BT /F1 {fontSize} Tf 100 700 Td ({c}) Tj ET";
            var pdfData = CreatePdfWithContentStream(contentStream, "Courier");
            using var doc = PdfDocument.Open(pdfData);
            var page = doc.GetPage(1);
            var extractor = new TextExtractor(page);

            // Act
            var letters = extractor.ExtractLetters();

            // Assert
            letters.Should().NotBeEmpty($"Character {c} should be extracted from Courier font");
            var letter = letters[0];
            letter.Width.Should().BeApproximately(expectedDisplayWidth, tolerance,
                $"Character {c} in Courier should have fixed width {expectedDisplayWidth}");
        }
    }

    /// <summary>
    /// Test default fallback for non-Helvetica, non-Courier fonts.
    /// Should return 600 units (average width).
    /// </summary>
    [Fact]
    public void ExtractLetters_TimesRomanFont_UsesDefaultFallback()
    {
        // Arrange
        const int defaultWidthUnits = 600;
        const double fontSize = 12;
        const double expectedDisplayWidth = (defaultWidthUnits / 1000.0) * fontSize;
        const double tolerance = 1.0; // Slightly wider tolerance for fallback

        var contentStream = "BT /F1 12 Tf 100 700 Td (X) Tj ET";
        var pdfData = CreatePdfWithContentStream(contentStream, "Times-Roman");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty("Character should be extracted");
        var letter = letters[0];
        // Times-Roman is not in the switch, should use default 600
        letter.Width.Should().BeApproximately(expectedDisplayWidth, tolerance,
            "Times-Roman should use default width fallback");
    }

    /// <summary>
    /// Test Helvetica non-matched character code (outside switch arms).
    /// Should return default case value of 556 (average).
    /// </summary>
    [Fact]
    public void ExtractLetters_HelveticaUnmatchedCharCode_UsesAverageDefault()
    {
        // Arrange
        const int defaultWidthUnits = 556;  // Default case in Helvetica switch
        const double fontSize = 12;
        const double expectedDisplayWidth = (defaultWidthUnits / 1000.0) * fontSize;
        const double tolerance = 0.5;

        // Use a character code that doesn't have an explicit switch arm (e.g., '!' = 33)
        // This will hit the default case
        var contentStream = "BT /F1 12 Tf 100 700 Td (!) Tj ET";
        var pdfData = CreatePdfWithContentStream(contentStream, "Helvetica");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty("Character should be extracted");
        var letter = letters[0];
        letter.Width.Should().BeApproximately(expectedDisplayWidth, tolerance,
            "Unmapped Helvetica character should use default width");
    }

    /// <summary>
    /// Test WinAnsi encoding for common special codes (128-159).
    /// Verifies that codes with special unicode mappings are decoded correctly.
    /// </summary>
    [Theory]
    [InlineData(128, '€')]   // Euro sign
    [InlineData(130, '‚')]   // Single low-9 quotation mark
    [InlineData(132, '„')]   // Double low-9 quotation mark
    [InlineData(134, '†')]   // Dagger
    [InlineData(138, 'Š')]   // Latin capital letter S with caron
    [InlineData(140, 'Œ')]   // Latin capital ligature OE
    [InlineData(142, 'Ž')]   // Latin capital letter Z with caron
    [InlineData(152, '˜')]   // Small tilde
    [InlineData(156, 'œ')]   // Latin small ligature oe
    public void ExtractLetters_WinAnsiSpecialCodes_DecodedCorrectly(int charCode, char expectedChar)
    {
        // Arrange
        // We'll use a direct character code in PDF (can't always input unicode directly)
        // For testing, we use hex codes in PDF: character code as byte in content stream
        var contentStream = $"BT /F1 12 Tf 100 700 Td <{charCode:X2}> Tj ET";
        var pdfData = CreatePdfWithContentStream(contentStream, "Helvetica");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty($"Character code {charCode} should be extracted");
        // The extracted character should map to the expected unicode
        letters[0].Value.Should().Contain(expectedChar.ToString(),
            $"Character code {charCode} in WinAnsi should decode to {expectedChar}");
    }

    /// <summary>
    /// Test MacRoman encoding for special codes.
    /// Verifies MacRoman-specific character mappings (128-159).
    /// </summary>
    [Theory]
    [InlineData(128, 'Ä')]   // Ä
    [InlineData(130, 'Ç')]   // Ç
    [InlineData(132, 'Ñ')]   // Ñ
    [InlineData(134, 'Ü')]   // Ü
    [InlineData(138, 'ä')]   // ä
    [InlineData(144, 'ê')]   // ê
    [InlineData(152, 'ò')]   // ò
    [InlineData(158, 'û')]   // û
    public void ExtractLetters_MacRomanSpecialCodes_DecodedCorrectly(int charCode, char expectedChar)
    {
        // Arrange
        var contentStream = $"BT /F1 12 Tf 100 700 Td <{charCode:X2}> Tj ET";
        var pdfData = CreatePdfWithMacRomanFont(contentStream);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty($"MacRoman character code {charCode} should be extracted");
        letters[0].Value.Should().Contain(expectedChar.ToString(),
            $"MacRoman code {charCode} should decode to {expectedChar}");
    }

    /// <summary>
    /// Test that Helvetica 'W' (code 87) has correct width (944, widest character).
    /// This is a boundary condition test.
    /// </summary>
    [Fact]
    public void ExtractLetters_HelveticaWideCharacter_HasLargestWidth()
    {
        // Arrange
        const double fontSize = 12;
        const int wideCharWidth = 944;  // 'W' has the largest width in Helvetica
        const double expectedDisplayWidth = (wideCharWidth / 1000.0) * fontSize;

        var contentStream = "BT /F1 12 Tf 100 700 Td (W) Tj ET";
        var pdfData = CreatePdfWithContentStream(contentStream, "Helvetica");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty("Character 'W' should be extracted");
        var letter = letters[0];
        letter.Width.Should().BeApproximately(expectedDisplayWidth, 0.5);

        // Verify 'W' is wider than 'M' (833)
        var mWidth = (833 / 1000.0) * fontSize;
        letter.Width.Should().BeGreaterThan(mWidth, "'W' should be wider than 'M'");
    }

    /// <summary>
    /// Test that Helvetica 'i', 'j', 'l' (narrow characters) have small widths.
    /// These are boundary conditions (smallest widths).
    /// </summary>
    [Theory]
    [InlineData('i', 222)]   // Narrow
    [InlineData('j', 222)]   // Narrow
    [InlineData('l', 222)]   // Narrow
    public void ExtractLetters_HelveticaNarrowCharacters_HaveSmallWidth(char c, int expectedWidthUnits)
    {
        // Arrange
        const double fontSize = 12;
        const double tolerance = 0.5;
        double expectedDisplayWidth = (expectedWidthUnits / 1000.0) * fontSize;

        var contentStream = $"BT /F1 {fontSize} Tf 100 700 Td ({c.ToString()}) Tj ET";
        var pdfData = CreatePdfWithContentStream(contentStream, "Helvetica");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty($"Character '{c}' should be extracted");
        var letter = letters[0];
        letter.Width.Should().BeApproximately(expectedDisplayWidth, tolerance);
    }

    /// <summary>
    /// Test multiple sizes with Helvetica to verify width scaling.
    /// Width should scale proportionally with font size.
    /// </summary>
    [Theory]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(36)]
    public void ExtractLetters_HelveticaVariousSizes_WidthScalesWithFontSize(double fontSize)
    {
        // Arrange
        const int charWidthUnits = 667;  // 'A' width
        double expectedDisplayWidth = (charWidthUnits / 1000.0) * fontSize;

        var contentStream = $"BT /F1 {fontSize:G} Tf 100 700 Td (A) Tj ET";
        var pdfData = CreatePdfWithContentStream(contentStream, "Helvetica");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty($"Character 'A' at {fontSize}pt should be extracted");
        var letter = letters[0];
        letter.Width.Should().BeApproximately(expectedDisplayWidth, 0.5,
            $"Width should scale with font size {fontSize}");
    }

    // Helper methods

    private static byte[] CreatePdfWithContentStream(string content, string fontName = "Helvetica")
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

        // Object 5: Font
        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine($"<< /Type /Font /Subtype /Type1 /BaseFont /{fontName} /Encoding /WinAnsiEncoding >>");
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

    private static byte[] CreatePdfWithMacRomanFont(string content)
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

        // Object 5: Font with MacRomanEncoding
        offsets[5] = ms.Position;
        writer.WriteLine("5 0 obj");
        writer.WriteLine($"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /MacRomanEncoding >>");
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
}
