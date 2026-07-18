using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Ocr;
using Xunit;

namespace Excise.Ocr.Tests;

/// <summary>
/// Tests for OCR result data structures: OcrWord and OcrResult records.
/// </summary>
public class OcrResultTests
{
    // ========================================================================
    // OCR WORD TESTS
    // ========================================================================

    [Fact]
    public void OcrWord_Construction_SetsAllProperties()
    {
        var bbox = new PdfRectangle(100, 200, 150, 220);
        var word = new OcrWord("TEST", bbox, 0.95f);

        word.Text.Should().Be("TEST");
        word.BoundingBox.Should().Be(bbox);
        word.Confidence.Should().Be(0.95f);
    }

    [Fact]
    public void OcrWord_CanBeConstructedWithZeroConfidence()
    {
        var bbox = new PdfRectangle(50, 100, 100, 120);
        var word = new OcrWord("UNCLEAR", bbox, 0.0f);

        word.Text.Should().Be("UNCLEAR");
        word.Confidence.Should().Be(0.0f);
    }

    [Fact]
    public void OcrWord_CanBeConstructedWithMaxConfidence()
    {
        var bbox = new PdfRectangle(0, 0, 10, 10);
        var word = new OcrWord("PERFECT", bbox, 1.0f);

        word.Confidence.Should().Be(1.0f);
    }

    [Fact]
    public void OcrWord_BoundingBox_StoresCoordinates()
    {
        var bbox = new PdfRectangle(10, 20, 30, 40);
        var word = new OcrWord("BOX", bbox, 0.9f);

        word.BoundingBox.Left.Should().Be(10);
        word.BoundingBox.Bottom.Should().Be(20);
        word.BoundingBox.Right.Should().Be(30);
        word.BoundingBox.Top.Should().Be(40);
    }

    [Fact]
    public void OcrWord_CanHaveEmptyText()
    {
        var bbox = new PdfRectangle(0, 0, 5, 5);
        var word = new OcrWord("", bbox, 0.5f);

        word.Text.Should().BeEmpty();
    }

    [Fact]
    public void OcrWord_CanHaveLongText()
    {
        var longText = "SUPERCALIFRAGILISTICEXPIALIDOCIOUS";
        var bbox = new PdfRectangle(0, 0, 200, 20);
        var word = new OcrWord(longText, bbox, 0.85f);

        word.Text.Should().Be(longText);
    }

    [Fact]
    public void OcrWord_RecordsAreEquatable()
    {
        var bbox = new PdfRectangle(100, 200, 150, 220);
        var word1 = new OcrWord("TEST", bbox, 0.95f);
        var word2 = new OcrWord("TEST", bbox, 0.95f);

        word1.Should().Be(word2);
    }

    [Fact]
    public void OcrWord_RecordsWithDifferentTextAreNotEqual()
    {
        var bbox = new PdfRectangle(100, 200, 150, 220);
        var word1 = new OcrWord("TEST", bbox, 0.95f);
        var word2 = new OcrWord("DIFFERENT", bbox, 0.95f);

        word1.Should().NotBe(word2);
    }

    [Fact]
    public void OcrWord_RecordsWithDifferentConfidenceAreNotEqual()
    {
        var bbox = new PdfRectangle(100, 200, 150, 220);
        var word1 = new OcrWord("TEST", bbox, 0.95f);
        var word2 = new OcrWord("TEST", bbox, 0.80f);

        word1.Should().NotBe(word2);
    }

    [Fact]
    public void OcrWord_CanBeStringified()
    {
        var bbox = new PdfRectangle(100, 200, 150, 220);
        var word = new OcrWord("TEST", bbox, 0.95f);
        var str = word.ToString();

        str.Should().Contain("TEST");
        str.Should().NotBeNullOrEmpty();
    }

    // ========================================================================
    // OCR RESULT TESTS
    // ========================================================================

    [Fact]
    public void OcrResult_Construction_SetsTextAndWords()
    {
        var bbox = new PdfRectangle(100, 200, 150, 220);
        var words = new[] { new OcrWord("HELLO", bbox, 0.95f) };
        var result = new OcrResult("HELLO", words);

        result.Text.Should().Be("HELLO");
        result.Words.Should().HaveCount(1);
        result.Words[0].Text.Should().Be("HELLO");
    }

    [Fact]
    public void OcrResult_Empty_HasEmptyTextAndNoWords()
    {
        var empty = OcrResult.Empty;

        empty.Text.Should().BeEmpty();
        empty.Words.Should().BeEmpty();
    }

    [Fact]
    public void OcrResult_Empty_IsReusable()
    {
        var empty1 = OcrResult.Empty;
        var empty2 = OcrResult.Empty;

        empty1.Should().Be(empty2);
    }

    [Fact]
    public void OcrResult_CanHaveMultipleWords()
    {
        var bbox1 = new PdfRectangle(100, 200, 150, 220);
        var bbox2 = new PdfRectangle(160, 200, 210, 220);
        var words = new[]
        {
            new OcrWord("HELLO", bbox1, 0.95f),
            new OcrWord("WORLD", bbox2, 0.92f)
        };
        var result = new OcrResult("HELLO WORLD", words);

        result.Words.Should().HaveCount(2);
        result.Text.Should().Be("HELLO WORLD");
    }

    [Fact]
    public void OcrResult_WordsAreReadOnlyCollection()
    {
        var bbox = new PdfRectangle(100, 200, 150, 220);
        var words = new[] { new OcrWord("TEST", bbox, 0.95f) };
        var result = new OcrResult("TEST", words);

        result.Words.Should().NotBeNull();
        result.Words.Count.Should().Be(1);
    }

    [Fact]
    public void OcrResult_CanHaveEmptyText()
    {
        var result = new OcrResult("", System.Array.Empty<OcrWord>());

        result.Text.Should().BeEmpty();
        result.Words.Should().BeEmpty();
    }

    [Fact]
    public void OcrResult_CanHaveLongText()
    {
        var longText = "This is a very long OCR result with many words and characters that represent a whole page or multiple pages of text content";
        var result = new OcrResult(longText, System.Array.Empty<OcrWord>());

        result.Text.Should().Be(longText);
    }

    [Fact]
    public void OcrResult_RecordsAreEquatable()
    {
        var bbox = new PdfRectangle(100, 200, 150, 220);
        var words = new[] { new OcrWord("TEST", bbox, 0.95f) };

        var result1 = new OcrResult("TEST", words);
        var result2 = new OcrResult("TEST", words);

        result1.Should().Be(result2);
    }

    [Fact]
    public void OcrResult_RecordsWithDifferentTextAreNotEqual()
    {
        var bbox = new PdfRectangle(100, 200, 150, 220);
        var words = new[] { new OcrWord("TEST", bbox, 0.95f) };

        var result1 = new OcrResult("TEST", words);
        var result2 = new OcrResult("DIFFERENT", words);

        result1.Should().NotBe(result2);
    }

    [Fact]
    public void OcrResult_AccessingWordsMultipleTimes_ReturnsSameReference()
    {
        var bbox = new PdfRectangle(100, 200, 150, 220);
        var words = new[] { new OcrWord("TEST", bbox, 0.95f) };
        var result = new OcrResult("TEST", words);

        var words1 = result.Words;
        var words2 = result.Words;

        words1.Should().BeSameAs(words2);
    }

    [Fact]
    public void OcrResult_CanBeStringified()
    {
        var bbox = new PdfRectangle(100, 200, 150, 220);
        var words = new[] { new OcrWord("TEST", bbox, 0.95f) };
        var result = new OcrResult("TEST", words);
        var str = result.ToString();

        str.Should().Contain("TEST");
        str.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void OcrResult_WithMixedConfidenceScores()
    {
        var bbox1 = new PdfRectangle(100, 200, 150, 220);
        var bbox2 = new PdfRectangle(160, 200, 210, 220);
        var bbox3 = new PdfRectangle(220, 200, 270, 220);
        var words = new[]
        {
            new OcrWord("HIGH", bbox1, 0.99f),
            new OcrWord("MEDIUM", bbox2, 0.75f),
            new OcrWord("LOW", bbox3, 0.30f)
        };
        var result = new OcrResult("HIGH MEDIUM LOW", words);

        result.Words[0].Confidence.Should().Be(0.99f);
        result.Words[1].Confidence.Should().Be(0.75f);
        result.Words[2].Confidence.Should().Be(0.30f);
    }
}
