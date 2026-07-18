using AwesomeAssertions;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.Core.Tests.Text;

public class WordTests
{
    private static Letter CreateLetter(string value, double left, double bottom, double right, double top)
    {
        return new Letter(
            value: value,
            glyphRectangle: new PdfRectangle(left, bottom, right, top),
            fontSize: 12.0,
            fontName: "/F1",
            startX: left,
            startY: bottom,
            width: right - left,
            characterCode: (int)value[0]);
    }

    [Fact]
    public void Constructor_WithSingleLetter_CreatesWord()
    {
        var letter = CreateLetter("A", 10.0, 20.0, 20.0, 32.0);
        var letters = new List<Letter> { letter };

        var word = new Word(letters);

        word.Text.Should().Be("A");
        word.Letters.Should().HaveCount(1);
        word.Letters[0].Should().Be(letter);
    }

    [Fact]
    public void Constructor_WithMultipleLetters_ConcatenatesText()
    {
        var letters = new List<Letter>
        {
            CreateLetter("H", 10.0, 20.0, 20.0, 32.0),
            CreateLetter("e", 20.0, 20.0, 27.0, 32.0),
            CreateLetter("l", 27.0, 20.0, 32.0, 32.0),
            CreateLetter("l", 32.0, 20.0, 37.0, 32.0),
            CreateLetter("o", 37.0, 20.0, 46.0, 32.0)
        };

        var word = new Word(letters);

        word.Text.Should().Be("Hello");
        word.Letters.Should().HaveCount(5);
    }

    [Fact]
    public void BoundingBox_WithSingleLetter_EqualsLetterGlyphRectangle()
    {
        var letter = CreateLetter("X", 10.0, 20.0, 20.0, 32.0);
        var letters = new List<Letter> { letter };

        var word = new Word(letters);

        word.BoundingBox.Should().Be(new PdfRectangle(10.0, 20.0, 20.0, 32.0));
    }

    [Fact]
    public void BoundingBox_WithMultipleLetters_EnvelopesAllGlyphs()
    {
        var letters = new List<Letter>
        {
            CreateLetter("H", 10.0, 20.0, 20.0, 32.0),
            CreateLetter("i", 20.0, 22.0, 25.0, 32.0),
        };

        var word = new Word(letters);

        var bbox = word.BoundingBox;
        bbox.Left.Should().Be(10.0);
        bbox.Bottom.Should().Be(20.0);
        bbox.Right.Should().Be(25.0);
        bbox.Top.Should().Be(32.0);
    }

    [Fact]
    public void BoundingBox_WithEmptyLetters_ReturnsDefaultRectangle()
    {
        var letters = new List<Letter>();

        var word = new Word(letters);

        word.BoundingBox.Should().Be(default(PdfRectangle));
    }

    [Fact]
    public void BoundingBox_WithNonAlignedLetters_FindsProperEnvelope()
    {
        var letters = new List<Letter>
        {
            CreateLetter("T", 0.0, 20.0, 10.0, 40.0),    // tall letter
            CreateLetter("y", 10.0, 0.0, 18.0, 20.0),    // descender
            CreateLetter("p", 18.0, 0.0, 26.0, 20.0)     // descender
        };

        var word = new Word(letters);

        var bbox = word.BoundingBox;
        bbox.Left.Should().Be(0.0);
        bbox.Bottom.Should().Be(0.0);
        bbox.Right.Should().Be(26.0);
        bbox.Top.Should().Be(40.0);
    }

    [Fact]
    public void ToString_ReturnsText()
    {
        var letters = new List<Letter>
        {
            CreateLetter("T", 0.0, 20.0, 10.0, 32.0),
            CreateLetter("e", 10.0, 20.0, 16.0, 32.0),
            CreateLetter("s", 16.0, 20.0, 23.0, 32.0),
            CreateLetter("t", 23.0, 20.0, 29.0, 32.0)
        };

        var word = new Word(letters);

        word.ToString().Should().Be("Test");
    }

    [Fact]
    public void Letters_ReturnsAllProvidedLetters()
    {
        var letters = new List<Letter>
        {
            CreateLetter("A", 10.0, 20.0, 20.0, 32.0),
            CreateLetter("B", 20.0, 20.0, 30.0, 32.0),
            CreateLetter("C", 30.0, 20.0, 40.0, 32.0)
        };
        var word = new Word(letters);

        word.Letters.Should().HaveCount(3);
        word.Letters[0].Value.Should().Be("A");
        word.Letters[1].Value.Should().Be("B");
        word.Letters[2].Value.Should().Be("C");
    }

    [Fact]
    public void Constructor_WithEmptyLetters_CreatesEmptyWord()
    {
        var letters = new List<Letter>();

        var word = new Word(letters);

        word.Text.Should().Be("");
        word.Letters.Should().BeEmpty();
    }

    [Fact]
    public void BoundingBox_WithSameBoundsLetters_ReturnsCorrectBounds()
    {
        var letters = new List<Letter>
        {
            CreateLetter("i", 10.0, 20.0, 15.0, 32.0),
            CreateLetter("i", 15.0, 20.0, 20.0, 32.0),
            CreateLetter("i", 20.0, 20.0, 25.0, 32.0)
        };

        var word = new Word(letters);

        var bbox = word.BoundingBox;
        bbox.Should().Be(new PdfRectangle(10.0, 20.0, 25.0, 32.0));
    }
}
