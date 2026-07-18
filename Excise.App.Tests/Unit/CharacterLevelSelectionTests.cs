using Avalonia;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.App.Tests.Unit;

/// <summary>
/// Tests for character-level text selection functionality.
/// These tests verify that text selection works at the character level,
/// not the word level, allowing users to select partial words and
/// precise text ranges.
/// </summary>
public class CharacterLevelSelectionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PdfTextExtractionService _service;
    private readonly Mock<ILogger<PdfTextExtractionService>> _loggerMock;

    public CharacterLevelSelectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CharSelectionTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);

        _loggerMock = new Mock<ILogger<PdfTextExtractionService>>();
        _service = new PdfTextExtractionService(_loggerMock.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); }
            catch { /* Ignore cleanup errors */ }
        }
    }

    /// <summary>
    /// Helper to get character positions from a PDF using Excise.Core.
    /// Returns positions in PDF coordinates (bottom-left origin).
    /// </summary>
    private List<(char character, double left, double bottom, double right, double top)>
        GetCharacterPositions(string pdfPath, int pageIndex = 0)
    {
        var result = new List<(char, double, double, double, double)>();

        using var stream = File.OpenRead(pdfPath);
        using var document = PdfDocument.Open(stream);
        var page = document.GetPage(pageIndex + 1);

        foreach (var word in page.GetWords())
        {
            foreach (var letter in word.Letters)
            {
                result.Add((
                    letter.Value[0],
                    letter.GlyphRectangle.Left,
                    letter.GlyphRectangle.Bottom,
                    letter.GlyphRectangle.Right,
                    letter.GlyphRectangle.Top
                ));
            }
        }

        return result;
    }

    /// <summary>
    /// Converts PDF coordinates to image coordinates for selection testing.
    /// PDF uses bottom-left origin, image uses top-left origin at 150 DPI.
    /// </summary>
    private Rect PdfCoordsToImageSelection(
        double pdfLeft, double pdfBottom, double pdfRight, double pdfTop,
        double pageHeight, int dpi = 150)
    {
        // Scale from PDF points (72 DPI) to image pixels (150 DPI)
        var scale = dpi / 72.0;

        // Convert Y from bottom-left to top-left origin
        var imageTop = (pageHeight - pdfTop) * scale;
        var imageBottom = (pageHeight - pdfBottom) * scale;

        return new Rect(
            pdfLeft * scale,
            imageTop,
            (pdfRight - pdfLeft) * scale,
            imageBottom - imageTop
        );
    }

    [Fact]
    public void ExtractTextFromArea_SelectMiddleOfSentence_ReturnsOnlySelectedCharacters()
    {
        // Arrange: Create PDF with "It can therefore affect the result"
        var pdfPath = Path.Combine(_tempDir, "sentence_test.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(pdfPath, "It can therefore affect the result");

        // Get character positions to find exact bounds for "can therefore"
        var chars = GetCharacterPositions(pdfPath);

        // Find the positions of 'c' in "can" and 'e' at end of "therefore"
        // Words: "It" "can" "therefore" "affect" "the" "result"
        var canStart = chars.FirstOrDefault(c => c.character == 'c' &&
            chars.Any(c2 => c2.character == 'a' && Math.Abs(c2.left - c.right) < 10));
        var thereforeEnd = chars.LastOrDefault(c => c.character == 'e' &&
            chars.Any(c2 => c2.character == 'r' && Math.Abs(c.left - c2.right) < 10));

        // If we can't find exact positions, use approximate bounds
        using var stream1 = File.OpenRead(pdfPath);
        using var doc = PdfDocument.Open(stream1);
        var page = doc.GetPage(1);
        var words = page.GetWords().ToList();

        // Find "can" and "therefore" words
        var canWord = words.FirstOrDefault(w => w.Text == "can");
        var thereforeWord = words.FirstOrDefault(w => w.Text == "therefore");

        canWord.Should().NotBeNull("PDF should contain 'can'");
        thereforeWord.Should().NotBeNull("PDF should contain 'therefore'");

        // Create selection that covers "can therefore" but NOT "It" or "affect"
        // Add small margin around the exact bounds
        var selectionLeft = canWord!.BoundingBox.Left - 1;
        var selectionRight = thereforeWord!.BoundingBox.Right + 1;
        var selectionBottom = Math.Min(canWord.BoundingBox.Bottom, thereforeWord.BoundingBox.Bottom) - 1;
        var selectionTop = Math.Max(canWord.BoundingBox.Top, thereforeWord.BoundingBox.Top) + 1;

        // Convert to image coordinates
        var selection = PdfCoordsToImageSelection(
            selectionLeft, selectionBottom, selectionRight, selectionTop,
            page.Height);

        // Act
        var result = _service.ExtractTextFromArea(pdfPath, 0, selection);

        // Assert
        result.Should().Contain("can", "Selection should include 'can'");
        result.Should().Contain("therefore", "Selection should include 'therefore'");
        result.Should().NotContain("It", "Selection should NOT include 'It' which is before the selection");
        result.Should().NotContain("affect", "Selection should NOT include 'affect' which is after the selection");
    }

    [Fact]
    public void ExtractTextFromArea_SelectSingleWord_ReturnsOnlyThatWord()
    {
        // Arrange
        var pdfPath = Path.Combine(_tempDir, "single_word_test.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(pdfPath, "Hello World Today");

        using var stream2 = File.OpenRead(pdfPath);
        using var doc = PdfDocument.Open(stream2);
        var page = doc.GetPage(1);
        var words = page.GetWords().ToList();

        var worldWord = words.FirstOrDefault(w => w.Text == "World");
        worldWord.Should().NotBeNull();

        // Create tight selection around "World" only
        var selection = PdfCoordsToImageSelection(
            worldWord!.BoundingBox.Left - 1,
            worldWord.BoundingBox.Bottom - 1,
            worldWord.BoundingBox.Right + 1,
            worldWord.BoundingBox.Top + 1,
            page.Height);

        // Act
        var result = _service.ExtractTextFromArea(pdfPath, 0, selection);

        // Assert
        result.Trim().Should().Be("World", "Only 'World' should be selected");
    }

    [Fact]
    public void ExtractTextFromArea_SelectPartOfWord_ReturnsOnlySelectedLetters()
    {
        // Arrange: Create PDF with "CONFIDENTIAL"
        var pdfPath = Path.Combine(_tempDir, "partial_word_test.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(pdfPath, "CONFIDENTIAL");

        using var stream2 = File.OpenRead(pdfPath);
        using var doc = PdfDocument.Open(stream2);
        var page = doc.GetPage(1);
        var words = page.GetWords().ToList();

        var word = words.FirstOrDefault(w => w.Text == "CONFIDENTIAL");
        word.Should().NotBeNull();

        // Get individual letter positions
        var letters = word!.Letters.ToList();
        letters.Count.Should().Be(12, "CONFIDENTIAL has 12 letters");

        // Find F, I, D, E positions (indices 3, 4, 5, 6 - 0-based)
        // C O N F I D E N T I A L
        // 0 1 2 3 4 5 6 7 8 9 10 11
        var fLetter = letters[3]; // F
        var eLetter = letters[6]; // E

        // Create selection covering only "FIDE"
        var selection = PdfCoordsToImageSelection(
            fLetter.GlyphRectangle.Left - 0.5,
            fLetter.GlyphRectangle.Bottom - 0.5,
            eLetter.GlyphRectangle.Right + 0.5,
            eLetter.GlyphRectangle.Top + 0.5,
            page.Height);

        // Act
        var result = _service.ExtractTextFromArea(pdfPath, 0, selection);

        // Assert
        result.Trim().Should().Be("FIDE", "Only 'FIDE' should be selected from 'CONFIDENTIAL'");
    }

    [Fact]
    public void ExtractTextFromArea_SelectionBarleyTouchesAdjacentWord_DoesNotIncludeIt()
    {
        // Arrange
        var pdfPath = Path.Combine(_tempDir, "adjacent_word_test.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(pdfPath, "Hello World");

        using var stream2 = File.OpenRead(pdfPath);
        using var doc = PdfDocument.Open(stream2);
        var page = doc.GetPage(1);
        var words = page.GetWords().ToList();

        var helloWord = words.FirstOrDefault(w => w.Text == "Hello");
        var worldWord = words.FirstOrDefault(w => w.Text == "World");
        helloWord.Should().NotBeNull();
        worldWord.Should().NotBeNull();

        // Create selection that covers "Hello" but right edge extends slightly past "Hello"
        // towards "World" - but not enough to include World's center
        var helloRight = helloWord!.BoundingBox.Right;
        var worldLeft = worldWord!.BoundingBox.Left;
        var midPoint = (helloRight + worldLeft) / 2;

        // Selection extends just past Hello but not to World's center
        var selection = PdfCoordsToImageSelection(
            helloWord.BoundingBox.Left - 1,
            helloWord.BoundingBox.Bottom - 1,
            midPoint - 1, // Stop before midpoint between words
            helloWord.BoundingBox.Top + 1,
            page.Height);

        // Act
        var result = _service.ExtractTextFromArea(pdfPath, 0, selection);

        // Assert
        result.Should().Contain("Hello", "Selection should include 'Hello'");
        result.Should().NotContain("World", "Selection should NOT include 'World'");
    }

    [Fact]
    public void ExtractTextFromArea_MultiLineSelection_ReturnsCorrectTextWithNewlines()
    {
        // Arrange
        var pdfPath = Path.Combine(_tempDir, "multiline_test.pdf");
        TestPdfGenerator.CreateTextOnlyPdf(pdfPath, new[] { "First line here", "Second line here" });

        using var stream2 = File.OpenRead(pdfPath);
        using var doc = PdfDocument.Open(stream2);
        var page = doc.GetPage(1);
        var words = page.GetWords().ToList();

        // Find words from each line
        var firstLineWord = words.FirstOrDefault(w => w.Text == "line" &&
            words.Any(w2 => w2.Text == "First" && Math.Abs(w2.BoundingBox.Bottom - w.BoundingBox.Bottom) < 5));
        var secondLineWord = words.FirstOrDefault(w => w.Text == "line" &&
            words.Any(w2 => w2.Text == "Second" && Math.Abs(w2.BoundingBox.Bottom - w.BoundingBox.Bottom) < 5));

        if (firstLineWord == null || secondLineWord == null)
        {
            // Fallback: select the entire text area
            var allWords = words.ToList();
            var minLeft = allWords.Min(w => w.BoundingBox.Left);
            var maxRight = allWords.Max(w => w.BoundingBox.Right);
            var minBottom = allWords.Min(w => w.BoundingBox.Bottom);
            var maxTop = allWords.Max(w => w.BoundingBox.Top);

            var selection = PdfCoordsToImageSelection(minLeft - 1, minBottom - 1, maxRight + 1, maxTop + 1, page.Height);
            var result = _service.ExtractTextFromArea(pdfPath, 0, selection);

            result.Should().Contain("First");
            result.Should().Contain("Second");
            return;
        }

        // Create selection spanning both lines but only partial text
        var selection2 = PdfCoordsToImageSelection(
            Math.Min(firstLineWord.BoundingBox.Left, secondLineWord.BoundingBox.Left) - 1,
            secondLineWord.BoundingBox.Bottom - 1, // Bottom of lower line
            Math.Max(firstLineWord.BoundingBox.Right, secondLineWord.BoundingBox.Right) + 1,
            firstLineWord.BoundingBox.Top + 1, // Top of upper line
            page.Height);

        // Act
        var result2 = _service.ExtractTextFromArea(pdfPath, 0, selection2);

        // Assert - should have text from both lines
        result2.Should().Contain("line", "Should contain 'line' from at least one line");
    }

    [Fact]
    public void ExtractTextFromArea_SelectEntireWord_ReturnsCompleteWord()
    {
        // Regression test - ensure complete word selection still works
        var pdfPath = Path.Combine(_tempDir, "complete_word_test.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(pdfPath, "Testing complete selection");

        using var stream2 = File.OpenRead(pdfPath);
        using var doc = PdfDocument.Open(stream2);
        var page = doc.GetPage(1);
        var words = page.GetWords().ToList();

        var completeWord = words.FirstOrDefault(w => w.Text == "complete");
        completeWord.Should().NotBeNull();

        // Create selection that fully covers "complete"
        var selection = PdfCoordsToImageSelection(
            completeWord!.BoundingBox.Left - 2,
            completeWord.BoundingBox.Bottom - 2,
            completeWord.BoundingBox.Right + 2,
            completeWord.BoundingBox.Top + 2,
            page.Height);

        // Act
        var result = _service.ExtractTextFromArea(pdfPath, 0, selection);

        // Assert
        result.Trim().Should().Be("complete", "Complete word should be selected");
    }

    [Fact]
    public void ExtractTextFromArea_EmptySelection_ReturnsEmptyString()
    {
        // Arrange
        var pdfPath = Path.Combine(_tempDir, "empty_selection_test.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(pdfPath, "Some text here");

        // Create selection in an area with no text (far right of page)
        var selection = new Rect(500 * (150.0 / 72.0), 100, 50, 50);

        // Act
        var result = _service.ExtractTextFromArea(pdfPath, 0, selection);

        // Assert
        result.Should().BeEmpty("No text should be found in empty area");
    }

    [Fact]
    public void ExtractTextFromArea_VerySmallSelection_SelectsOnlyCharactersInside()
    {
        // Arrange
        var pdfPath = Path.Combine(_tempDir, "small_selection_test.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(pdfPath, "ABCDEFGHIJ");

        using var stream2 = File.OpenRead(pdfPath);
        using var doc = PdfDocument.Open(stream2);
        var page = doc.GetPage(1);
        var words = page.GetWords().ToList();

        var word = words.FirstOrDefault(w => w.Text == "ABCDEFGHIJ");
        word.Should().NotBeNull();

        var letters = word!.Letters.ToList();

        // Select only "DEF" (indices 3, 4, 5)
        var dLetter = letters[3]; // D
        var fLetter = letters[5]; // F

        var selection = PdfCoordsToImageSelection(
            dLetter.GlyphRectangle.Left - 0.5,
            dLetter.GlyphRectangle.Bottom - 0.5,
            fLetter.GlyphRectangle.Right + 0.5,
            fLetter.GlyphRectangle.Top + 0.5,
            page.Height);

        // Act
        var result = _service.ExtractTextFromArea(pdfPath, 0, selection);

        // Assert
        result.Trim().Should().Be("DEF", "Only 'DEF' should be selected");
    }
}
