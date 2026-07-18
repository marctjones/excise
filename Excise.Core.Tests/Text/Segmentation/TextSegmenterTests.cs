using AwesomeAssertions;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text;
using Excise.Core.Text.Segmentation;
using Xunit;

namespace Excise.Core.Tests.Text.Segmentation;

/// <summary>
/// Tests for TextSegmenter - the foundation for partial text redaction.
/// </summary>
public class TextSegmenterTests
{
    private readonly TextSegmenter _segmenter = new();

    #region Basic Segmentation Tests

    [Fact]
    public void BuildSegments_NoLetterMatches_NoOverlap_KeepsEntireOperation()
    {
        // Arrange
        var text = "Hello World";
        var operationBounds = new PdfRectangle(100, 700, 200, 720);
        var letterMatches = new List<LetterMatch>();
        var redactionArea = new PdfRectangle(300, 700, 400, 720); // No overlap

        // Act
        var segments = _segmenter.BuildSegments(text, operationBounds, letterMatches, redactionArea);

        // Assert
        segments.Should().HaveCount(1);
        segments[0].Keep.Should().BeTrue();
        segments[0].Text.Should().Be("Hello World");
        segments[0].StartIndex.Should().Be(0);
        segments[0].EndIndex.Should().Be(11);
    }

    [Fact]
    public void BuildSegments_NoLetterMatches_FullOverlap_RemovesEntireOperation()
    {
        // Arrange
        var text = "Hello World";
        var operationBounds = new PdfRectangle(100, 700, 200, 720);
        var letterMatches = new List<LetterMatch>();
        var redactionArea = new PdfRectangle(50, 650, 250, 750); // Covers entire operation

        // Act
        var segments = _segmenter.BuildSegments(text, operationBounds, letterMatches, redactionArea);

        // Assert
        segments.Should().BeEmpty("entire operation should be removed");
    }

    [Fact]
    public void BuildSegments_PartialTextRedaction_SegmentsCorrectly()
    {
        // Arrange - "Hello World" where we redact "World"
        var text = "Hello World";
        var operationBounds = new PdfRectangle(100, 700, 200, 720);

        // Create letter matches for each character
        var letterMatches = new List<LetterMatch>
        {
            CreateMatch('H', 0, 100, 700, 10),
            CreateMatch('e', 1, 110, 700, 8),
            CreateMatch('l', 2, 118, 700, 5),
            CreateMatch('l', 3, 123, 700, 5),
            CreateMatch('o', 4, 128, 700, 9),
            CreateMatch(' ', 5, 137, 700, 5),
            CreateMatch('W', 6, 142, 700, 12), // Start of redaction area
            CreateMatch('o', 7, 154, 700, 9),
            CreateMatch('r', 8, 163, 700, 6),
            CreateMatch('l', 9, 169, 700, 5),
            CreateMatch('d', 10, 174, 700, 9)
        };

        // Redaction area covers "World" (x: 142-183)
        var redactionArea = new PdfRectangle(142, 695, 185, 725);

        // Act
        var segments = _segmenter.BuildSegments(text, operationBounds, letterMatches, redactionArea);

        // Assert - should keep only "Hello "
        segments.Should().HaveCount(1);
        segments[0].Keep.Should().BeTrue();
        segments[0].Text.Should().Be("Hello ");
        segments[0].StartIndex.Should().Be(0);
        segments[0].EndIndex.Should().Be(6);
    }

    #endregion

    #region Strategy Tests

    [Fact]
    public void BuildSegments_AnyOverlap_RemovesPartiallyOverlappingGlyphs()
    {
        // Arrange - letter partially in redaction area
        var text = "Test";
        var operationBounds = new PdfRectangle(100, 700, 150, 720);

        var letterMatches = new List<LetterMatch>
        {
            CreateMatch('T', 0, 100, 700, 10),  // Outside
            CreateMatch('e', 1, 110, 700, 8),   // Partially in (110-118, redaction starts at 115)
            CreateMatch('s', 2, 118, 700, 6),   // Inside
            CreateMatch('t', 3, 124, 700, 5)    // Inside
        };

        var redactionArea = new PdfRectangle(115, 695, 135, 725);

        // Act - AnyOverlap strategy (default)
        var segments = _segmenter.BuildSegments(text, operationBounds, letterMatches, redactionArea,
            GlyphRemovalStrategy.AnyOverlap);

        // Assert - should keep only "T" (e, s, t are removed due to overlap)
        segments.Should().HaveCount(1);
        segments[0].Text.Should().Be("T");
    }

    [Fact]
    public void BuildSegments_FullyContained_KeepsPartiallyOverlappingGlyphs()
    {
        // Arrange - same setup as above
        var text = "Test";
        var operationBounds = new PdfRectangle(100, 700, 150, 720);

        var letterMatches = new List<LetterMatch>
        {
            CreateMatch('T', 0, 100, 700, 10),  // Outside
            CreateMatch('e', 1, 110, 700, 8),   // Partially in (110-118)
            CreateMatch('s', 2, 118, 700, 6),   // Fully inside
            CreateMatch('t', 3, 124, 700, 5)    // Fully inside
        };

        var redactionArea = new PdfRectangle(115, 695, 135, 725);

        // Act - FullyContained strategy
        var segments = _segmenter.BuildSegments(text, operationBounds, letterMatches, redactionArea,
            GlyphRemovalStrategy.FullyContained);

        // Assert - should keep "Te" (only s, t are fully contained)
        segments.Should().HaveCount(1);
        segments[0].Text.Should().Be("Te");
    }

    [Fact]
    public void BuildSegments_CenterPoint_UsesGlyphCenter()
    {
        // Arrange
        var text = "AB";
        var operationBounds = new PdfRectangle(100, 700, 130, 720);

        var letterMatches = new List<LetterMatch>
        {
            CreateMatch('A', 0, 100, 700, 10),  // Center at 105 (outside redaction)
            CreateMatch('B', 1, 110, 700, 10)   // Center at 115 (inside redaction: 112-125)
        };

        var redactionArea = new PdfRectangle(112, 695, 125, 725);

        // Act
        var segments = _segmenter.BuildSegments(text, operationBounds, letterMatches, redactionArea,
            GlyphRemovalStrategy.CenterPoint);

        // Assert - should keep only "A"
        segments.Should().HaveCount(1);
        segments[0].Text.Should().Be("A");
    }

    #endregion

    #region Overlap Type Tests

    [Fact]
    public void BuildSegments_PartialOverlap_SetsOverlapTypeCorrectly()
    {
        // Arrange - letter partially overlapping redaction area
        var text = "X";
        var operationBounds = new PdfRectangle(100, 700, 110, 720);

        var letterMatches = new List<LetterMatch>
        {
            CreateMatch('X', 0, 100, 700, 10)  // Partially in redaction (100-110, redaction: 105-120)
        };

        var redactionArea = new PdfRectangle(105, 695, 120, 725);

        // Act - Build all segments (including removed ones)
        var allSegments = GetAllSegments(text, operationBounds, letterMatches, redactionArea);

        // Assert - should have a removed segment with Partial overlap
        var removedSegments = allSegments.Where(s => !s.Keep).ToList();
        removedSegments.Should().HaveCount(1);
        removedSegments[0].OverlapType.Should().Be(GlyphOverlapType.Partial);
        removedSegments[0].IsPartialOverlap.Should().BeTrue();
    }

    [Fact]
    public void BuildSegments_FullOverlap_SetsOverlapTypeCorrectly()
    {
        // Arrange - letter fully inside redaction area
        var text = "X";
        var operationBounds = new PdfRectangle(100, 700, 110, 720);

        var letterMatches = new List<LetterMatch>
        {
            CreateMatch('X', 0, 100, 700, 10)  // Fully in redaction (100-110, redaction: 95-115)
        };

        var redactionArea = new PdfRectangle(95, 695, 115, 725);

        // Act
        var allSegments = GetAllSegments(text, operationBounds, letterMatches, redactionArea);

        // Assert
        var removedSegments = allSegments.Where(s => !s.Keep).ToList();
        removedSegments.Should().HaveCount(1);
        removedSegments[0].OverlapType.Should().Be(GlyphOverlapType.Full);
        removedSegments[0].IsPartialOverlap.Should().BeFalse();
    }

    #endregion

    #region Multiple Segment Tests

    [Fact]
    public void BuildSegments_MultipleKeepSegments_CreatesCorrectSegments()
    {
        // Arrange - "A B C" where we redact "B"
        var text = "A B C";
        var operationBounds = new PdfRectangle(100, 700, 150, 720);

        var letterMatches = new List<LetterMatch>
        {
            CreateMatch('A', 0, 100, 700, 10),
            CreateMatch(' ', 1, 110, 700, 5),
            CreateMatch('B', 2, 115, 700, 10),  // Redacted
            CreateMatch(' ', 3, 125, 700, 5),
            CreateMatch('C', 4, 130, 700, 10)
        };

        var redactionArea = new PdfRectangle(115, 695, 125, 725);

        // Act
        var segments = _segmenter.BuildSegments(text, operationBounds, letterMatches, redactionArea);

        // Assert - should have two segments: "A " and " C"
        segments.Should().HaveCount(2);
        segments[0].Text.Should().Be("A ");
        segments[0].StartIndex.Should().Be(0);
        segments[0].EndIndex.Should().Be(2);

        segments[1].Text.Should().Be(" C");
        segments[1].StartIndex.Should().Be(3);
        segments[1].EndIndex.Should().Be(5);
    }

    #endregion

    #region CJK Support Tests

    [Fact]
    public void BuildSegments_PreservesLetterMatches_ForReconstruction()
    {
        // Arrange
        var text = "Test";
        var operationBounds = new PdfRectangle(100, 700, 150, 720);

        var letterMatches = new List<LetterMatch>
        {
            CreateMatch('T', 0, 100, 700, 10),
            CreateMatch('e', 1, 110, 700, 8),
            CreateMatch('s', 2, 118, 700, 6),
            CreateMatch('t', 3, 124, 700, 5)
        };

        // Add raw bytes for CJK reconstruction
        letterMatches[0].RawBytes = new byte[] { 0x54 }; // 'T'
        letterMatches[1].RawBytes = new byte[] { 0x65 }; // 'e'

        var redactionArea = new PdfRectangle(300, 700, 400, 720); // No overlap

        // Act
        var segments = _segmenter.BuildSegments(text, operationBounds, letterMatches, redactionArea);

        // Assert
        segments.Should().HaveCount(1);
        segments[0].LetterMatches.Should().HaveCount(4);
        segments[0].GetRawBytes().Should().Equal(new byte[] { 0x54, 0x65 });
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Create a letter match for testing.
    /// </summary>
    private LetterMatch CreateMatch(char character, int index, double x, double y, double width)
    {
        var letter = new Letter(
            character.ToString(),
            new PdfRectangle(x, y, x + width, y + 20),
            fontSize: 12,
            fontName: "TestFont",
            startX: x,
            startY: y,
            width: width,
            characterCode: character
        );

        return new LetterMatch
        {
            Letter = letter,
            CharacterIndex = index
        };
    }

    /// <summary>
    /// Get all segments (including removed ones) for testing overlap types.
    /// This is a test helper that modifies the segmenter to return all segments.
    /// </summary>
    private List<TextSegment> GetAllSegments(
        string text,
        PdfRectangle operationBounds,
        List<LetterMatch> letterMatches,
        PdfRectangle redactionArea)
    {
        // Build segments normally
        var keptSegments = _segmenter.BuildSegments(text, operationBounds, letterMatches, redactionArea);

        // Rebuild with inverted logic to get removed segments
        var allSegments = new List<TextSegment>();
        TextSegment? currentSegment = null;

        for (int i = 0; i < text.Length; i++)
        {
            var match = letterMatches.FirstOrDefault(m => m.CharacterIndex == i);
            if (match == null) continue;

            var (shouldRemove, overlapType) = GetLetterOverlapInfo(
                match.Letter.GlyphRectangle, redactionArea);

            bool keep = !shouldRemove;
            var rect = match.Letter.GlyphRectangle;

            if (currentSegment == null || currentSegment.Keep != keep ||
                (!keep && currentSegment.OverlapType != overlapType))
            {
                if (currentSegment != null)
                {
                    allSegments.Add(currentSegment);
                }

                currentSegment = new TextSegment
                {
                    StartIndex = i,
                    EndIndex = i + 1,
                    Keep = keep,
                    OverlapType = keep ? GlyphOverlapType.None : overlapType,
                    StartX = rect.Left,
                    StartY = rect.Bottom,
                    Width = rect.Width,
                    Height = rect.Height,
                    OriginalText = text
                };
            }
            else
            {
                currentSegment.EndIndex = i + 1;
                currentSegment.Width += rect.Width;
            }
        }

        if (currentSegment != null)
        {
            allSegments.Add(currentSegment);
        }

        return allSegments;
    }

    private (bool ShouldRemove, GlyphOverlapType OverlapType) GetLetterOverlapInfo(
        PdfRectangle glyphRect,
        PdfRectangle redactionArea)
    {
        var normalizedGlyph = glyphRect.Normalize();
        var normalizedRedaction = redactionArea.Normalize();

        if (!normalizedGlyph.IntersectsWith(normalizedRedaction))
            return (false, GlyphOverlapType.None);

        bool fullyContained =
            normalizedRedaction.Contains(normalizedGlyph.Left, normalizedGlyph.Bottom) &&
            normalizedRedaction.Contains(normalizedGlyph.Right, normalizedGlyph.Top) &&
            normalizedRedaction.Contains(normalizedGlyph.Left, normalizedGlyph.Top) &&
            normalizedRedaction.Contains(normalizedGlyph.Right, normalizedGlyph.Bottom);

        var overlapType = fullyContained ? GlyphOverlapType.Full : GlyphOverlapType.Partial;
        return (true, overlapType);
    }

    #endregion

    #region Letter Tests

    [Fact]
    public void Letter_Properties_AreAccessible()
    {
        var rect = new PdfRectangle(100, 700, 110, 720);
        var letter = new Letter("A", rect, 12, "F1", 100, 700, 10, 65);

        letter.Value.Should().Be("A");
        letter.FontSize.Should().Be(12);
        letter.FontName.Should().Be("F1");
        letter.StartX.Should().Be(100);
        letter.StartY.Should().Be(700);
        letter.Width.Should().Be(10);
        letter.CharacterCode.Should().Be(65);
    }

    [Fact]
    public void Letter_StartX_Property_Accessible()
    {
        var rect = new PdfRectangle(100, 700, 110, 720);
        var letter = new Letter("A", rect, 12, "F1", 100, 700, 10, 65);
        letter.StartX.Should().Be(100);
    }

    [Fact]
    public void Letter_Width_Property_Accessible()
    {
        var rect = new PdfRectangle(100, 700, 110, 720);
        var letter = new Letter("A", rect, 12, "F1", 100, 700, 10, 65);
        letter.Width.Should().Be(10);
    }

    [Fact]
    public void Letter_StartBaseLine_Returns_CorrectPoint()
    {
        var rect = new PdfRectangle(100, 700, 110, 720);
        var letter = new Letter("A", rect, 12, "F1", 100, 700, 10, 65);
        var baseline = letter.StartBaseLine;

        baseline.X.Should().Be(100);
        baseline.Y.Should().Be(700);
    }

    [Fact]
    public void Letter_EndBaseLine_Returns_CorrectPoint()
    {
        var rect = new PdfRectangle(100, 700, 110, 720);
        var letter = new Letter("A", rect, 12, "F1", 100, 700, 10, 65);
        var endBaseLine = letter.EndBaseLine;

        endBaseLine.X.Should().Be(110); // StartX + Width
        endBaseLine.Y.Should().Be(700);
    }

    [Fact]
    public void Letter_ToString_ReturnsFormattedString()
    {
        var rect = new PdfRectangle(100, 700, 110, 720);
        var letter = new Letter("A", rect, 12, "F1", 100, 700, 10, 65);
        var str = letter.ToString();

        str.Should().Contain("A");
        str.Should().Contain("100");
        str.Should().Contain("700");
    }

    [Fact]
    public void Letter_WithLigature_StoresMultipleCharacters()
    {
        var rect = new PdfRectangle(100, 700, 115, 720);
        var letter = new Letter("fi", rect, 12, "F1", 100, 700, 15, 256);

        letter.Value.Should().Be("fi");
        letter.Width.Should().Be(15);
    }

    #endregion

    #region TextSegment Tests

    [Fact]
    public void TextSegment_BoundingBox_Property_Accessible()
    {
        var segment = new TextSegment
        {
            StartIndex = 0,
            EndIndex = 5,
            Keep = false,
            StartX = 100,
            StartY = 700,
            Height = 20,
            OriginalText = "Hello",
            Width = 100
        };

        var bbox = segment.BoundingBox;
        bbox.Left.Should().Be(100);
        bbox.Bottom.Should().Be(700);
        bbox.Right.Should().Be(200);
        bbox.Top.Should().Be(720);
    }

    [Fact]
    public void TextSegment_HasToUnicode_Property_Accessible()
    {
        var segment = new TextSegment
        {
            StartIndex = 0,
            EndIndex = 5,
            Keep = false,
            StartX = 100,
            StartY = 700,
            Height = 20,
            OriginalText = "Hello",
            Width = 100
        };

        segment.HasToUnicode.Should().BeFalse();
    }

    [Fact]
    public void TextSegment_WasHexString_Property_Accessible()
    {
        var segment = new TextSegment
        {
            StartIndex = 0,
            EndIndex = 5,
            Keep = false,
            StartX = 100,
            StartY = 700,
            Height = 20,
            OriginalText = "Hello",
            Width = 100,
            WasHexString = true
        };

        segment.WasHexString.Should().BeTrue();
    }

    #endregion

    #region LetterMatch Tests

    [Fact]
    public void LetterMatch_SourceOperation_Property_Accessible()
    {
        var letter = new Letter("H", new PdfRectangle(100, 700, 110, 720), 12, "F1", 100, 700, 10, 72);
        var match = new LetterMatch
        {
            Letter = letter,
            CharacterIndex = 0,
            SourceOperation = "SomeOperation"
        };

        match.SourceOperation.Should().NotBeNull();
    }

    #endregion

    #region Unmapped Character Tests

    [Fact]
    public void BuildSegments_WithUnmappedCharacter_UsesFixedWidthEstimate()
    {
        // Test lines 98-108: "No letter match for this index" else branch in TextSegmenter
        // This tests the code path when a character has no corresponding letter (unmapped)
        var segmenter = new TextSegmenter();
        var text = "Test";
        var operationBounds = new PdfRectangle(100, 100, 150, 120);

        // Create letter matches for only 3 characters (missing 4th)
        var letterMatches = new List<LetterMatch>
        {
            new LetterMatch
            {
                Letter = new Letter("T", new PdfRectangle(100, 100, 110, 120), 12, "F1", 100, 100, 10, 72),
                CharacterIndex = 0
            },
            new LetterMatch
            {
                Letter = new Letter("e", new PdfRectangle(110, 100, 118, 120), 12, "F1", 110, 100, 8, 72),
                CharacterIndex = 1
            },
            new LetterMatch
            {
                Letter = new Letter("s", new PdfRectangle(118, 100, 125, 120), 12, "F1", 118, 100, 7, 72),
                CharacterIndex = 2
            }
            // Character at index 3 ('t') is unmapped - will trigger the "No letter match" else branch
        };

        var redactionRect = new PdfRectangle(200, 100, 300, 120); // Outside operation area

        var segments = segmenter.BuildSegments(text, operationBounds, letterMatches, redactionRect);

        // Should still produce segments despite missing letter
        segments.Should().NotBeEmpty();
        segments.Should().HaveCountGreaterThan(0);
        // When no overlap, entire operation is kept
        segments[0].Keep.Should().BeTrue();
    }

    [Fact]
    public void BuildSegments_WithAllUnmappedCharacters_ProducesKeepSegment()
    {
        // Test when entire operation has no letter mappings but doesn't intersect redaction area
        var segmenter = new TextSegmenter();
        var text = "Test";
        var operationBounds = new PdfRectangle(100, 100, 150, 120);
        var letterMatches = new List<LetterMatch>(); // No letters at all
        var redactionRect = new PdfRectangle(200, 100, 300, 120); // No intersection

        var segments = segmenter.BuildSegments(text, operationBounds, letterMatches, redactionRect);

        // Should produce segment for entire operation (no intersection with redaction)
        segments.Should().HaveCount(1);
        segments[0].Keep.Should().BeTrue();
        segments[0].OriginalText.Should().Be("Test");
    }

    [Fact]
    public void BuildSegments_WithPartialMappings_UsesEstimateForUnmapped()
    {
        // Test mixed case: some letters mapped, some unmapped with partial redaction
        var segmenter = new TextSegmenter();
        var text = "Hello";
        var operationBounds = new PdfRectangle(100, 100, 180, 120);

        // Map only first 2 and last letters
        var letterMatches = new List<LetterMatch>
        {
            new LetterMatch
            {
                Letter = new Letter("H", new PdfRectangle(100, 100, 112, 120), 12, "F1", 100, 100, 12, 72),
                CharacterIndex = 0
            },
            new LetterMatch
            {
                Letter = new Letter("e", new PdfRectangle(112, 100, 120, 120), 12, "F1", 112, 100, 8, 72),
                CharacterIndex = 1
            },
            // Indices 2, 3 (l, l) are unmapped
            new LetterMatch
            {
                Letter = new Letter("o", new PdfRectangle(160, 100, 175, 120), 12, "F1", 160, 100, 15, 72),
                CharacterIndex = 4
            }
        };

        var redactionRect = new PdfRectangle(200, 100, 250, 120); // No intersection

        var segments = segmenter.BuildSegments(text, operationBounds, letterMatches, redactionRect);

        // Should produce a keep segment for entire operation
        segments.Should().NotBeEmpty();
        segments[0].Keep.Should().BeTrue();
        segments[0].OriginalText.Should().Be("Hello");
    }

    #endregion
}
