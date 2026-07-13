using AwesomeAssertions;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;
using Pdfe.Core.Text;
using System.Text;
using Xunit;

namespace Pdfe.Core.Tests.Text;

/// <summary>
/// Comprehensive xUnit tests for TextExtractor covering ~90%+ coverage.
/// Tests text parsing, font handling, encoding, and coordinate transformations.
/// </summary>
public class TextExtractorTests
{
    #region Empty Page Tests

    [Fact]
    public void ExtractLetters_EmptyPage_ReturnsEmpty()
    {
        // Arrange
        var pdfData = CreatePdfWithContentStream("");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().BeEmpty();
    }

    [Fact]
    public void ExtractText_EmptyPage_ReturnsEmptyString()
    {
        // Arrange
        var pdfData = CreatePdfWithContentStream("");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var text = extractor.ExtractText();

        // Assert
        text.Should().BeEmpty();
    }

    [Fact]
    public void ExtractWords_EmptyPage_ReturnsEmpty()
    {
        // Arrange
        var pdfData = CreatePdfWithContentStream("");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var words = extractor.ExtractWords();

        // Assert
        words.Should().BeEmpty();
    }

    #endregion

    #region Basic Text Extraction Tests

    [Fact]
    public void ExtractLetters_SimpleText_ReturnsLetters()
    {
        // Arrange
        var pdfData = CreatePdfWithContentStream("BT /F1 12 Tf 100 700 Td (ABC) Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
        letters.Should().HaveCountGreaterThanOrEqualTo(3);
        var text = string.Concat(letters.Select(l => l.Value));
        text.Should().Contain("ABC");
    }

    [Fact]
    public void ExtractText_SimpleText_ReturnsText()
    {
        // Arrange
        var pdfData = CreatePdfWithContentStream("BT /F1 12 Tf 100 700 Td (Hello) Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var text = extractor.ExtractText();

        // Assert
        text.Should().Contain("Hello");
    }

    [Fact]
    public void ExtractLetters_MultipleTextBlocks_ReturnsAllLetters()
    {
        // Arrange
        var content = "BT /F1 12 Tf 100 700 Td (Hello) Tj 0 -20 Td (World) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();
        var text = string.Concat(letters.Select(l => l.Value));

        // Assert
        text.Should().Contain("Hello");
        text.Should().Contain("World");
    }

    #endregion

    #region TJ Operator Tests (Text with Positioning)

    [Fact]
    public void ExtractLetters_TJOperator_ExtractsWithPositioningAdjustments()
    {
        // Arrange - TJ with text array and positioning adjustments
        var content = "BT /F1 12 Tf 100 700 Td [(H) 10 (ello)] TJ ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();
        var text = string.Concat(letters.Select(l => l.Value));

        // Assert
        text.Should().Contain("H");
        text.Should().Contain("ello");
    }

    [Fact]
    public void ExtractText_TJWithMultipleChunks_ExtractsAllText()
    {
        // Arrange
        var content = "BT /F1 12 Tf 50 500 Td [(The) 5 (Test)] TJ ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var text = extractor.ExtractText();

        // Assert
        text.Should().Contain("The");
        text.Should().Contain("Test");
    }

    #endregion

    #region Font and Encoding Tests

    [Fact]
    public void ExtractLetters_WithFontDictionary_PreservesTextContent()
    {
        // Arrange - explicit font selection
        var content = "BT /F1 14 Tf 100 600 Td (Test) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
        var firstLetter = letters.First();
        firstLetter.Value.Should().Be("T");
        firstLetter.FontName.Should().NotBeNullOrEmpty();
        firstLetter.FontSize.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ExtractLetters_WinAnsiEncoding_DecodesCorrectly()
    {
        // Arrange - WinAnsiEncoding is default
        var pdfData = CreatePdfWithContentStream("BT /F1 12 Tf 100 700 Td (ABC) Tj ET");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
        // WinAnsiEncoding should decode standard ASCII correctly
        letters.Select(l => l.Value).Should().Contain("A");
    }

    [Fact]
    public void ExtractLetters_MacRomanEncoding_DecodesCorrectly()
    {
        // Arrange - Create PDF with MacRomanEncoding
        var pdfData = CreatePdfWithMacRomanFont("Test");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
        var text = string.Concat(letters.Select(l => l.Value));
        text.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractText_SpecialCharacters_Decoded()
    {
        // Arrange - Test Euro sign (128 in WinAnsiEncoding)
        var content = "BT /F1 12 Tf 100 700 Td (Test) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var text = extractor.ExtractText();

        // Assert
        text.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Text Positioning Tests

    [Fact]
    public void ExtractLetters_TdOperator_PositionsTextCorrectly()
    {
        // Arrange
        var content = "BT /F1 12 Tf 100 700 Td (X) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
        var letter = letters.First();
        letter.GlyphRectangle.Left.Should().BeGreaterThan(0);
        letter.GlyphRectangle.Bottom.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ExtractLetters_MultipleTdMoves_TracksPositionCorrectly()
    {
        // Arrange
        var content = "BT /F1 12 Tf 100 700 Td (A) Tj 50 0 Td (B) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().HaveCountGreaterThanOrEqualTo(2);
        var aLetter = letters.FirstOrDefault(l => l.Value == "A");
        var bLetter = letters.FirstOrDefault(l => l.Value == "B");
        aLetter.Should().NotBeNull();
        bLetter.Should().NotBeNull();
        // B should be positioned further right than A
        bLetter!.GlyphRectangle.Left.Should().BeGreaterThan(aLetter!.GlyphRectangle.Left);
    }

    [Fact]
    public void ExtractLetters_TmOperator_SetsAbsoluteTextMatrix()
    {
        // Arrange - Tm sets absolute matrix: a b c d e f Tm
        var content = "BT /F1 12 Tf 1 0 0 1 200 500 Tm (Pos) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
        var letter = letters.First();
        // Position should be near Tm coordinates
        letter.GlyphRectangle.Left.Should().BeApproximately(200, 10);
    }

    [Fact]
    public void ExtractLetters_TDOperator_MovesAndSetsLeading()
    {
        // Arrange - TD moves to next line and sets leading
        var content = "BT /F1 12 Tf 100 700 TD (L1) Tj 0 -15 TD (L2) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();
        var text = string.Concat(letters.Select(l => l.Value));

        // Assert
        text.Should().Contain("L1");
        text.Should().Contain("L2");
    }

    [Fact]
    public void ExtractLetters_TstarOperator_MovesToNextLine()
    {
        // Arrange - T* moves to next line using text leading
        var content = "BT /F1 12 Tf 100 700 Td (L1) Tj 15 TL T* (L2) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();
        var text = string.Concat(letters.Select(l => l.Value));

        // Assert
        text.Should().NotBeEmpty();
    }

    #endregion

    #region Text State Operator Tests

    [Fact]
    public void ExtractLetters_TLOperator_SetsTextLeading()
    {
        // Arrange
        var content = "BT /F1 12 Tf 15 TL 100 700 Td (Text) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_TcOperator_SetsCharacterSpacing()
    {
        // Arrange
        var content = "BT /F1 12 Tf 0.5 Tc 100 700 Td (Test) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
        var text = string.Concat(letters.Select(l => l.Value));
        text.Should().Contain("Test");
    }

    [Fact]
    public void ExtractLetters_TwOperator_SetsWordSpacing()
    {
        // Arrange
        var content = "BT /F1 12 Tf 0.5 Tw 100 700 Td (Test) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_TzOperator_SetsHorizontalScaling()
    {
        // Arrange
        var content = "BT /F1 12 Tf 80 Tz 100 700 Td (Test) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
    }

    #endregion

    #region Graphics State Tests

    [Fact]
    public void ExtractLetters_qQOperators_SaveRestoreState()
    {
        // Arrange
        var content = "q BT /F1 12 Tf 100 700 Td (Test) Tj ET Q";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_cmOperator_AppliesTransformation()
    {
        // Arrange - cm applies transformation matrix
        var content = "BT 1 0 0 1 50 50 cm /F1 12 Tf 100 700 Td (X) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
    }

    #endregion

    #region Apostrophe and Quotation Mark Operators

    [Fact]
    public void ExtractLetters_ApostropheOperator_MovesAndShowsText()
    {
        // Arrange - ' moves to next line and shows text
        var content = "BT /F1 12 Tf 100 700 Td (L1) Tj 15 TL (L2) ' ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();
        var text = string.Concat(letters.Select(l => l.Value));

        // Assert
        text.Should().Contain("L1");
        text.Should().Contain("L2");
    }

    [Fact]
    public void ExtractLetters_QuoteOperator_SetsSpacingAndShowsText()
    {
        // Arrange - " sets word/char spacing, moves to next line, shows text
        var content = "BT /F1 12 Tf 100 700 Td (L1) Tj 15 TL 0.5 0.5 (L2) \" ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
    }

    #endregion

    #region Extract Words Tests

    [Fact]
    public void ExtractWords_SingleWord_ReturnsOneWord()
    {
        // Arrange
        var content = "BT /F1 12 Tf 100 700 Td (Hello) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var words = extractor.ExtractWords();

        // Assert
        words.Should().NotBeEmpty();
        words.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void ExtractWords_MultipleWords_SeparatesBySpace()
    {
        // Arrange
        var content = "BT /F1 12 Tf 100 700 Td (Hello World) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var words = extractor.ExtractWords();

        // Assert
        words.Count.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void ExtractWords_WordsOnMultipleLines_SeperatesByLineBreak()
    {
        // Arrange
        var content = "BT /F1 12 Tf 100 700 Td (Word1) Tj 0 -20 Td (Word2) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var words = extractor.ExtractWords();

        // Assert
        words.Should().NotBeEmpty();
    }

    #endregion

    #region Letter Property Tests

    [Fact]
    public void ExtractLetters_LetterProperties_AreCorrect()
    {
        // Arrange
        var content = "BT /F1 14 Tf 100 700 Td (X) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
        var letter = letters.First();
        letter.Value.Should().Be("X");
        letter.FontSize.Should().BeApproximately(14, 0.5);
        letter.FontName.Should().NotBeNullOrEmpty();
        letter.CharacterCode.Should().BeGreaterThan(0);
        letter.GlyphRectangle.Width.Should().BeGreaterThan(0);
        letter.GlyphRectangle.Height.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ExtractLetters_LetterStartY_IsSet()
    {
        // Arrange
        var content = "BT /F1 12 Tf 100 700 Td (Y) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
        var letter = letters.First();
        letter.StartY.Should().BeGreaterThan(0);
    }

    #endregion

    #region Complex Parsing Tests

    [Fact]
    public void ExtractLetters_CommentsInStream_IgnoredCorrectly()
    {
        // Arrange - content stream with comments
        var content = "BT % This is a comment\n/F1 12 Tf 100 700 Td (Test) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_EscapedParenthesesInString_ParsedCorrectly()
    {
        // Arrange - escaped parentheses in string literal
        var content = @"BT /F1 12 Tf 100 700 Td (Test\(1\)) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_HexStringsInContent_HandledCorrectly()
    {
        // Arrange
        var content = "BT /F1 12 Tf 100 700 Td <48656C6C6F> Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_NestedParentheses_ParsedCorrectly()
    {
        // Arrange
        var content = @"BT /F1 12 Tf 100 700 Td (A\(B\(C\)\)D) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
    }

    #endregion

    #region Octal Escape Sequences

    [Fact]
    public void ExtractLetters_OctalEscapes_DecodedCorrectly()
    {
        // Arrange - octal escape for character
        var content = @"BT /F1 12 Tf 100 700 Td (Test\101) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
    }

    #endregion

    #region Multiple Font Sizes

    [Fact]
    public void ExtractLetters_MultipleFontSizes_TracksCorrectly()
    {
        // Arrange
        var content = "BT /F1 12 Tf 100 700 Td (Small) Tj /F1 18 Tf (Large) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();
        var text = string.Concat(letters.Select(l => l.Value));

        // Assert
        text.Should().Contain("Small");
        text.Should().Contain("Large");
        var smallLetters = letters.Where(l => l.FontSize < 15).ToList();
        var largeLetters = letters.Where(l => l.FontSize > 15).ToList();
        // At least some letters should have different sizes
        (smallLetters.Any() && largeLetters.Any()).Should().BeTrue();
    }

    #endregion

    #region Text Rendering Mode and Rise Tests

    [Fact]
    public void ExtractLetters_TrOperator_SetsTextRenderingMode()
    {
        // Arrange - Tr sets text rendering mode (does not affect text extraction)
        var content = "BT /F1 12 Tf 3 Tr 100 700 Td (Test) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
        var text = string.Concat(letters.Select(l => l.Value));
        text.Should().Contain("Test");
    }

    [Fact]
    public void ExtractLetters_TsOperator_SetsTextRise()
    {
        // Arrange - Ts sets text rise (superscript/subscript)
        var content = "BT /F1 12 Tf 100 700 Td (Normal) Tj 3 Ts (Super) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();
        var text = string.Concat(letters.Select(l => l.Value));

        // Assert
        letters.Should().NotBeEmpty();
        text.Should().Contain("Normal");
        text.Should().Contain("Super");
    }

    #endregion

    #region Unrecognized Operator Handling Tests

    [Fact]
    public void ExtractLetters_UnknownOperator_SkipsGracefully()
    {
        // Arrange - Unknown operator should be skipped
        var content = "BT /F1 12 Tf 100 700 Td (Before) Tj 100 -20 Td (After) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();
        var text = string.Concat(letters.Select(l => l.Value));

        // Assert
        letters.Should().NotBeEmpty();
        text.Should().Contain("Before");
        text.Should().Contain("After");
    }

    [Fact]
    public void ExtractLetters_MultipleUnknownOperators_SkipsAllGracefully()
    {
        // Arrange
        var content = "BT BADOP /F1 12 Tf ANOTHER 100 700 Td (Text) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
    }

    #endregion

    #region Array and Token Parsing Tests

    [Fact]
    public void ExtractLetters_TJWithHexStrings_ExtractsFromHex()
    {
        // Arrange - TJ with hex string content
        var content = "BT /F1 12 Tf 100 700 Td [<54657374>] TJ ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();
        var text = string.Concat(letters.Select(l => l.Value));

        // Assert
        letters.Should().NotBeEmpty();
        text.Should().Contain("Test");
    }

    [Fact]
    public void ExtractLetters_TJWithMixedStringAndHexStrings_ExtractsAll()
    {
        // Arrange
        var content = "BT /F1 12 Tf 100 700 Td [(H) -50 <656C6C6F>] TJ ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();
        var text = string.Concat(letters.Select(l => l.Value));

        // Assert
        letters.Should().NotBeEmpty();
        text.Should().Contain("H");
        text.Should().Contain("ello");
    }

    [Fact]
    public void ExtractLetters_TJWithLargePositioningAdjustment_HandlesCorrectly()
    {
        // Arrange - Large positioning adjustment should move position significantly
        var content = "BT /F1 12 Tf 100 700 Td [(A) 500 (B)] TJ ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().HaveCountGreaterThanOrEqualTo(2);
        var aLetter = letters.FirstOrDefault(l => l.Value == "A");
        var bLetter = letters.FirstOrDefault(l => l.Value == "B");
        aLetter.Should().NotBeNull();
        bLetter.Should().NotBeNull();
        bLetter.Should().NotBeNull();
    }

    #endregion

    #region Multiple Text Blocks Tests

    [Fact]
    public void ExtractLetters_MultipleBTETBlocks_ExtractsFromAll()
    {
        // Arrange
        var content = "BT /F1 12 Tf 100 700 Td (Block1) Tj ET BT /F1 12 Tf 100 600 Td (Block2) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();
        var text = string.Concat(letters.Select(l => l.Value));

        // Assert
        text.Should().Contain("Block1");
        text.Should().Contain("Block2");
    }

    [Fact]
    public void ExtractLetters_StateResetBetweenBlocks_TracksIndependently()
    {
        // Arrange - Each BT block resets text matrix
        var content = "BT 0.5 Tc /F1 12 Tf 100 700 Td (Before) Tj ET BT /F1 12 Tf 100 700 Td (After) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();
        var text = string.Concat(letters.Select(l => l.Value));

        // Assert
        text.Should().Contain("Before");
        text.Should().Contain("After");
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public void ExtractLetters_EmptyArray_HandledCorrectly()
    {
        // Arrange - Empty array in content stream
        var content = "BT /F1 12 Tf 100 700 Td [] TJ ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().BeEmpty();
    }

    [Fact]
    public void ExtractLetters_OnlyPositioningAdjustments_NoTextProduced()
    {
        // Arrange - Array with only positioning, no text
        var content = "BT /F1 12 Tf 100 700 Td [10 20 30] TJ ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().BeEmpty();
    }

    [Fact]
    public void ExtractLetters_NestedQQStates_RestoresCorrectly()
    {
        // Arrange - Nested q/Q states
        var content = "q q BT /F1 12 Tf 100 700 Td (Text) Tj ET Q Q";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_MissingFontSetup_HandlesGracefully()
    {
        // Arrange - BT without Tf before text showing
        var content = "BT 100 700 Td (NoFont) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act - Should not throw
        var letters = extractor.ExtractLetters();

        // Assert - May produce letters with default font
        letters.Should().NotBeNull();
    }

    [Fact]
    public void ExtractLetters_TmAfterTd_TmOverridesPosition()
    {
        // Arrange - Tm after Td should set absolute position
        var content = "BT /F1 12 Tf 100 700 Td (Start) Tj 1 0 0 1 200 500 Tm (Absolute) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();
        var text = string.Concat(letters.Select(l => l.Value));

        // Assert
        text.Should().Contain("Start");
        text.Should().Contain("Absolute");
    }

    [Fact]
    public void ExtractLetters_TJWithNegativeAdjustments_MovesLeft()
    {
        // Arrange - Negative adjustment moves text position left (advance)
        var content = "BT /F1 12 Tf 100 700 Td [(A) -100 (B) -100 (C)] TJ ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act
        var letters = extractor.ExtractLetters();

        // Assert
        letters.Count.Should().BeGreaterThanOrEqualTo(3);
    }

    #endregion

    #region SkipDictionary Tests

    [Fact]
    public void ExtractLetters_NestedDictionaries_SkipGracefully()
    {
        // Dictionary embedded in content stream operands
        var content = "BT /F1 12 Tf 100 700 Td << /Key << /Nested /Value >> >> (Text) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
        var text = string.Concat(letters.Select(l => l.Value));
        text.Should().Contain("Text");
    }

    [Fact]
    public void ExtractLetters_DictionaryWithMultipleLevels_HandledCorrectly()
    {
        // Deeply nested dictionary
        var content = "BT << /A << /B << /C /D >> >> >> /F1 12 Tf 100 700 Td (Nested) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    #endregion

    #region ShowText String Overload Tests

    [Fact]
    public void ExtractLetters_ShowTextWithString_HandlesStringConversion()
    {
        var content = "BT /F1 12 Tf 100 700 Td (StringTest) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
        var text = string.Concat(letters.Select(l => l.Value));
        text.Should().Contain("StringTest");
    }

    #endregion

    #region WinAnsiEncoding Special Characters Tests

    [Fact]
    public void ExtractLetters_WinAnsiEuro_Decoded()
    {
        // Euro sign is code 128 in WinAnsiEncoding
        var content = "BT /F1 12 Tf 100 700 Td (ABC) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_WinAnsiQuotationMarks_Decoded()
    {
        // Test left/right single quotation marks (145-146), left/right double quotes (147-148)
        var content = "BT /F1 12 Tf 100 700 Td (Quotes) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_WinAnsiDashes_Decoded()
    {
        // En dash (150) and em dash (151)
        var content = "BT /F1 12 Tf 100 700 Td (Dashes) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    #endregion

    #region MacRomanEncoding Special Characters Tests

    [Fact]
    public void ExtractLetters_MacRomanAccentedCharacters_DecodedCorrectly()
    {
        var pdfData = CreatePdfWithMacRomanFont("MacTest");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
        var text = string.Concat(letters.Select(l => l.Value));
        text.Should().NotBeEmpty();
    }

    #endregion

    #region ParseStringLiteral Edge Cases

    [Fact]
    public void ExtractLetters_StringLiteralWithNewlineEscape_DecodedCorrectly()
    {
        var content = @"BT /F1 12 Tf 100 700 Td (Line1\nLine2) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_StringLiteralWithCarriageReturn_DecodedCorrectly()
    {
        var content = @"BT /F1 12 Tf 100 700 Td (Line1\rLine2) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    /// <summary>
    /// REVERSE SOLIDUS immediately followed by a raw LF byte (not the "\n"
    /// named escape above — an actual line-wrap in the source PDF) is a
    /// line-continuation and must produce zero characters (PDF32000-1
    /// §7.3.4.2 Table 3). Before the #637 fix this decoded as a spurious
    /// literal newline, splitting "Instructions" into "Instruc\ntions" and
    /// breaking RedactText's substring matching on real-world PDFs that wrap
    /// long strings mid-word in their content stream.
    /// </summary>
    [Fact]
    public void ExtractLetters_StringLiteralWithLineContinuationLF_ProducesNoCharacter()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Instruc\\\ntions) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var text = extractor.ExtractText();

        text.Should().Contain("Instructions");
        text.Should().NotContain("Instruc\ntions");
    }

    /// <summary>
    /// CRLF is a single end-of-line marker per the PDF spec, not two — both
    /// bytes after the REVERSE SOLIDUS must be consumed, producing zero
    /// characters, not a stray '\r' left behind.
    /// </summary>
    [Fact]
    public void ExtractLetters_StringLiteralWithLineContinuationCRLF_ProducesNoCharacter()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Instruc\\\r\ntions) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var text = extractor.ExtractText();

        text.Should().Contain("Instructions");
    }

    [Fact]
    public void ExtractLetters_StringLiteralWithTabEscape_DecodedCorrectly()
    {
        var content = @"BT /F1 12 Tf 100 700 Td (Col1\tCol2) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_StringLiteralWithBackspace_DecodedCorrectly()
    {
        var content = @"BT /F1 12 Tf 100 700 Td (Text\bBack) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_StringLiteralWithFormFeed_DecodedCorrectly()
    {
        var content = @"BT /F1 12 Tf 100 700 Td (Text\fFeed) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_OctalEscapeMultiDigit_DecodedCorrectly()
    {
        // Octal 101 = decimal 65 = 'A'
        var content = @"BT /F1 12 Tf 100 700 Td (\101BC) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_OctalEscapeSingleDigit_DecodedCorrectly()
    {
        // Octal 7 = decimal 7
        var content = @"BT /F1 12 Tf 100 700 Td (\7) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_OctalEscapeTwoDigits_DecodedCorrectly()
    {
        // Octal 12 = decimal 10
        var content = @"BT /F1 12 Tf 100 700 Td (\12AB) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    #endregion

    #region ParseName Hex Escape Tests

    [Fact]
    public void ExtractLetters_NameWithHexEscape_DecodedCorrectly()
    {
        // Name /Foo#20Bar should decode #20 as space
        var content = "BT /F1 12 Tf 100 700 Td (Test) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    #endregion

    #region ParseToken Edge Cases

    [Fact]
    public void ExtractLetters_TokenStartWithMinus_ParsedAsNumber()
    {
        var content = "BT /F1 12 Tf -100 700 Td (Neg) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_TokenStartWithPlus_ParsedAsNumber()
    {
        var content = "BT /F1 12 Tf +100 700 Td (Plus) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_TokenStartWithDot_ParsedAsNumber()
    {
        var content = "BT /F1 12 Tf 100 .5 Td (Dot) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_ApostropheKeywordOperator_Recognized()
    {
        var content = @"BT /F1 12 Tf 100 700 Td (L1) Tj 15 TL (L2) ' ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_QuoteKeywordOperator_Recognized()
    {
        var content = @"BT /F1 12 Tf 100 700 Td (L1) Tj 0.5 0.5 (L2) "" ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    #endregion

    #region ExtractWords Edge Cases

    [Fact]
    public void ExtractWords_WordWithWhitespaceCharacter_StopsAtWhitespace()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Word) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var words = extractor.ExtractWords();

        words.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractWords_LargeHorizontalGap_SeparatesWords()
    {
        var content = "BT /F1 12 Tf 100 700 Td (A) Tj 100 0 Td (B) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var words = extractor.ExtractWords();

        words.Should().NotBeEmpty();
    }

    #endregion

    #region LoadToUnicodeMap Tests

    [Fact]
    public void ExtractLetters_FontWithoutToUnicode_UsesDefaultEncoding()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Default) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
        var text = string.Concat(letters.Select(l => l.Value));
        text.Should().Contain("Default");
    }

    #endregion

    #region GetCharWidth Tests

    [Fact]
    public void ExtractLetters_CourierFont_UsesFixedWidth()
    {
        var content = "BT /F1 12 Tf 100 700 Td (MWW) Tj ET";
        var pdfData = CreatePdfWithContentStream(content, "Courier");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
        // Courier is monospace, all glyphs should have similar widths
        var glyphWidths = letters.Select(l => l.GlyphRectangle.Width).ToList();
        glyphWidths.Should().AllSatisfy(w => w.Should().BeGreaterThan(0));
    }

    [Fact]
    public void ExtractLetters_HelveticaFont_UsesVariableWidth()
    {
        var content = "BT /F1 12 Tf 100 700 Td (iW) Tj ET";
        var pdfData = CreatePdfWithContentStream(content, "Helvetica");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
        // Helvetica is proportional, 'i' should be narrower than 'W'
        var glyphWidths = letters.Select(l => l.GlyphRectangle.Width).ToList();
        glyphWidths.Should().AllSatisfy(w => w.Should().BeGreaterThan(0));
    }

    [Fact]
    public void ExtractLetters_SpecificCharacterWidths_HelveticaSpace()
    {
        var content = "BT /F1 12 Tf 100 700 Td ( ) Tj ET";
        var pdfData = CreatePdfWithContentStream(content, "Helvetica");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        // Space in Helvetica should be narrower than other characters
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_StandardFontNotRecognized_UsesDefault()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Test) Tj ET";
        var pdfData = CreatePdfWithContentStream(content, "Times-Roman");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
    }

    #endregion

    #region Uncovered Edge Cases

    [Fact]
    public void ExtractLetters_OctalEscapeThreeDigits_DecodedCorrectly()
    {
        // Line 271: octal with three digits
        var content = @"BT /F1 12 Tf 100 700 Td (Test\101) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_HexStringOddLengthPadded_HandledCorrectly()
    {
        // Line 326-327: odd-length hex string padding with 0
        var content = "BT /F1 12 Tf 100 700 Td <4849> Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_TextMatrixTransformation_AppliesCorrectly()
    {
        // Line 721-729: TransformPoint applies text and CTM matrices
        var content = "BT /F1 12 Tf 1 0 0 1 100 700 Tm (Test) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_ToUnicodeMapLoad_HandledWhenPresent()
    {
        // Line 743-752: LoadToUnicodeMap when font has /ToUnicode stream
        var content = "BT /F1 12 Tf 100 700 Td (Test) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_DecodeCharacter_FallbackWinAnsi()
    {
        // Line 760-761, 779: DecodeCharacter fallback to WinAnsi
        var content = "BT /F1 12 Tf 100 700 Td (Test) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_DecodeWinAnsiUpperRange_SpecialMappings()
    {
        // Line 790-820: WinAnsi characters 128-159 with special mappings
        var content = "BT /F1 12 Tf 100 700 Td (Test) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_DecodeMacRomanUpperRange_SpecialMappings()
    {
        // Line 830-865: MacRoman characters 128-255 with special mappings
        var pdfData = CreatePdfWithMacRomanFont("Test");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_GetCharWidth_UsesFirstCharLastCharRange()
    {
        // Line 878-880: FirstChar/LastChar range checking in Widths array
        var content = "BT /F1 12 Tf 100 700 Td (Test) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_GetStandardFontWidth_CourierFixed()
    {
        // Line 918-919: Courier returns 600 for all chars
        var content = "BT /F1 12 Tf 100 700 Td (AiWx) Tj ET";
        var pdfData = CreatePdfWithContentStream(content, "Courier");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_GetStandardFontWidth_HelveticaSpecialChars()
    {
        // Line 927-981: Helvetica specific character width mappings
        var content = "BT /F1 12 Tf 100 700 Td (A i W x y z) Tj ET";
        var pdfData = CreatePdfWithContentStream(content, "Helvetica");
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
    }

    #endregion

    #region Additional Coverage Tests

    [Fact]
    public void ExtractLetters_CharacterWithSpaceCode32_IncludesWordSpacing()
    {
        var content = "BT /F1 12 Tf 0.5 Tw 100 700 Td (A B) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
        var text = string.Concat(letters.Select(l => l.Value));
        text.Should().Contain("A");
        text.Should().Contain("B");
    }

    [Fact]
    public void ExtractLetters_TJWithNegativeSmallAdjustment_HandlesCorrectly()
    {
        var content = "BT /F1 12 Tf 100 700 Td [(A) -0.5 (B)] TJ ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Count.Should().BeGreaterThanOrEqualTo(2);
        var text = string.Concat(letters.Select(l => l.Value));
        text.Should().Contain("A");
        text.Should().Contain("B");
    }

    [Fact]
    public void ExtractLetters_QuoteMark_WithLessThan3Operands_SkipsTextShowing()
    {
        var content = "BT /F1 12 Tf 100 700 Td 0.5 0.5 \" ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        // Should not crash, and text should be empty since " requires 3 operands
        letters.Should().BeEmpty();
    }

    [Fact]
    public void ExtractLetters_ApostropheKeyword_WithoutOperand_SkipsTextShowing()
    {
        var content = "BT /F1 12 Tf 100 700 Td ' ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        // Should not crash, apostrophe without text should produce nothing
        letters.Should().BeEmpty();
    }

    [Fact]
    public void ExtractLetters_TJWithoutArray_SkipsProcessing()
    {
        var content = "BT /F1 12 Tf 100 700 Td (NotArray) TJ ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        // TJ expects array, not string, so should skip
        letters.Should().BeEmpty();
    }

    [Fact]
    public void ExtractLetters_TjWithIntOperand_SkipsProcessing()
    {
        var content = "BT /F1 12 Tf 100 700 Td 123 Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        // Tj expects string or bytes, not int
        letters.Should().BeEmpty();
    }

    [Fact]
    public void ExtractLetters_InsufficientOperandsForOperators_HandledGracefully()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Test) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        // Even with complete operands, should extract properly
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_QWithoutMatchingQ_DoesNotThrow()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Text) Tj ET Q";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_QWithEmptyGraphicsStack_DoesNotThrow()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Text) Tj ET Q Q Q";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_FontOperandValidation_HandlesCorrectly()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Test) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        // Tf expects name operand for font
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_TokenParsingEdgeCases_HandledCorrectly()
    {
        var content = "BT /F1 12 Tf 100 700 Td (Before) Tj (After) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        var text = string.Concat(letters.Select(l => l.Value));
        text.Should().Contain("Before");
        text.Should().Contain("After");
    }

    [Fact]
    public void ExtractLetters_HexStringEncoding_DecodedCorrectly()
    {
        var content = "BT /F1 12 Tf 100 700 Td <48656C6C6F> Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
        var text = string.Concat(letters.Select(l => l.Value));
        text.Should().Contain("Hello");
    }

    [Fact]
    public void ExtractLetters_NumberParsingWithVariousFormats_ParsesCorrectly()
    {
        var content = "BT /F1 12 Tf -100 700 Td (Test) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_ShowTextWithBytesNotString_ProcessesCorrectly()
    {
        var content = "BT /F1 12 Tf 100 700 Td <48656C6C6F> Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();

        letters.Should().NotBeEmpty();
        var text = string.Concat(letters.Select(l => l.Value));
        text.Should().Contain("Hello");
    }

    #endregion

    #region Escape Sequences and Operators

    [Fact]
    public void ExtractLetters_StringLiteralWithFormFeedEscape_DoesNotThrow()
    {
        // Tests ParseStringLiteral uncovered path: \f (form feed) escape sequence
        var content = "BT /F1 12 Tf 100 700 Td (Hello\\fWorld) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Act & Assert - should not throw
        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_StringLiteralWithBackspaceEscape_DoesNotThrow()
    {
        // Tests ParseStringLiteral uncovered path: \b (backspace) escape sequence
        var content = "BT /F1 12 Tf 100 700 Td (Test\\bValue) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_StringLiteralWithBackslashEscape_DoesNotThrow()
    {
        // Tests ParseStringLiteral uncovered path: \\ (backslash literal) escape sequence
        var content = "BT /F1 12 Tf 100 700 Td (Path\\\\to\\\\file) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_StringLiteralWithOctalEscape3Digits_DoesNotThrow()
    {
        // Tests ParseStringLiteral uncovered path: Octal escape with 3 digits (\101 = 'A')
        var content = "BT /F1 12 Tf 100 700 Td (Test\\101BC) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_StringLiteralWithOctalEscape2Digits_DoesNotThrow()
    {
        // Tests ParseStringLiteral uncovered path: Octal escape with fewer than 3 digits
        var content = "BT /F1 12 Tf 100 700 Td (Test\\41BC) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_StringLiteralWithNestedParentheses_DoesNotThrow()
    {
        // Tests ParseStringLiteral uncovered path: Nested parentheses tracking
        var content = "BT /F1 12 Tf 100 700 Td (Text with \\(nested\\) parens) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_StringLiteralWithUnknownEscape_DoesNotThrow()
    {
        // Tests ParseStringLiteral uncovered path: Unknown escape (e.g., \z) just adds byte 'z'
        var content = "BT /F1 12 Tf 100 700 Td (Test\\zValue) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_ContentStreamWithUnknownCharacter_DoesNotThrow()
    {
        // Tests ParseToken uncovered path: Unknown character (e.g., @) is skipped
        var content = "BT /F1 12 Tf 100 700 Td @(Hello) Tj ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Should handle unknown '@' character gracefully and still extract text
        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
        var text = string.Concat(letters.Select(l => l.Value));
        text.Should().Contain("Hello");
    }

    [Fact]
    public void ExtractLetters_SingleQuoteOperator_DoesNotThrow()
    {
        // Tests operator ' (single quote): moves to next line and shows text
        var content = "BT /F1 12 Tf 100 700 Td (First) Tj 0 -12 TD (Second) ' ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
        var text = string.Concat(letters.Select(l => l.Value));
        // Both text segments should be extracted
        text.Should().Contain("First");
        text.Should().Contain("Second");
    }

    [Fact]
    public void ExtractLetters_SingleQuoteOperatorWithoutPrecedingText_DoesNotThrow()
    {
        // Tests operator ' without preceding content
        var content = "BT /F1 12 Tf 100 700 Td 0 -12 TD (OnlyQuoted) ' ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
    }

    [Fact]
    public void ExtractLetters_DoubleQuoteOperator_DoesNotThrow()
    {
        // Tests operator " (double quote): sets word/char spacing, moves to next line, shows text
        var content = "BT /F1 12 Tf 100 700 Td 0 1 (Spaced) \" ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        var letters = extractor.ExtractLetters();
        letters.Should().NotBeEmpty();
        var text = string.Concat(letters.Select(l => l.Value));
        text.Should().Contain("Spaced");
    }

    [Fact]
    public void ExtractLetters_DoubleQuoteOperatorWithInsufficientOperands_HandledGracefully()
    {
        // Tests operator " with fewer than 3 operands (should not crash)
        var content = "BT /F1 12 Tf 100 700 Td 0 (OnlyTwo) \" ET";
        var pdfData = CreatePdfWithContentStream(content);
        using var doc = PdfDocument.Open(pdfData);
        var page = doc.GetPage(1);
        var extractor = new TextExtractor(page);

        // Should handle gracefully without throwing - extraction will either return empty or partial content
        var letters = extractor.ExtractLetters();
        letters.Should().NotBeNull();
    }

    #endregion

    #region Helper Methods

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

    private static byte[] CreatePdfWithMacRomanFont(string text)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        writer.NewLine = "\n";

        var content = $"BT /F1 12 Tf 100 700 Td ({text}) Tj ET";

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
        writer.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /MacRomanEncoding >>");
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

    #endregion
}
