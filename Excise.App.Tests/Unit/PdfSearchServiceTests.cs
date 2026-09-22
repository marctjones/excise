using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Xunit;

namespace Excise.App.Tests.Unit;

public class PdfSearchServiceTests : IDisposable
{
    private readonly PdfSearchService _searchService;
    private readonly string _testPdfPath;
    private readonly string _testOutputDir;

    public PdfSearchServiceTests()
    {
        var logger = new Mock<ILogger<PdfSearchService>>().Object;
        _searchService = new PdfSearchService(logger);

        _testOutputDir = Path.Combine(Path.GetTempPath(), "PdfSearchTests_" + Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testOutputDir);

        // Create a test PDF with known text content
        _testPdfPath = Path.Combine(_testOutputDir, "search_test.pdf");
        TestPdfGenerator.CreateTextOnlyPdf(_testPdfPath, new[]
        {
            "Hello World! This is page one.",
            "The quick brown fox jumps over the lazy dog.",
            "Testing search functionality with multiple words."
        });
    }

    [Fact]
    public void Search_FindsSimpleText_ReturnsMatches()
    {
        // Act
        var results = _searchService.Search(_testPdfPath, "Hello");

        // Assert
        results.Should().ContainSingle("\"Hello\" appears once in the fixture");
        results[0].MatchedText.Should().Be("Hello");
        results[0].PageIndex.Should().Be(0);
    }

    [Fact]
    public void Search_CaseSensitive_RespectsCase()
    {
        // Act - Case insensitive (default)
        var resultsInsensitive = _searchService.Search(_testPdfPath, "hello", caseSensitive: false);

        // Act - Case sensitive
        var resultsSensitive = _searchService.Search(_testPdfPath, "hello", caseSensitive: true);

        // Assert
        resultsInsensitive.Should().ContainSingle(); // "Hello"
        resultsSensitive.Should().BeEmpty(); // "hello" lowercase not in document
    }

    [Fact]
    public void Search_WholeWordsOnly_FindsCompleteWords()
    {
        // Act - Whole words
        var wholeWordResults = _searchService.Search(_testPdfPath, "fox", wholeWordsOnly: true);

        // Act - Partial match
        var partialResults = _searchService.Search(_testPdfPath, "ox", wholeWordsOnly: true);

        // Assert
        wholeWordResults.Should().ContainSingle();
        partialResults.Should().BeEmpty(); // "ox" is not a complete word
    }

    /// <summary>
    /// The flag is the only difference between these two calls, so a search that
    /// ignores it (in either direction) cannot pass both halves.
    /// </summary>
    [Fact]
    public void Search_WholeWordsFlag_TurnsASubstringHitOnAndOff()
    {
        var substring = _searchService.Search(_testPdfPath, "ox", wholeWordsOnly: false);
        var wholeWord = _searchService.Search(_testPdfPath, "ox", wholeWordsOnly: true);

        substring.Should().ContainSingle("\"ox\" is inside \"fox\"").Which.MatchedText.Should().Be("ox");
        wholeWord.Should().BeEmpty("\"ox\" is not a word on its own");
    }

    [Fact]
    public void Search_MultipleMatches_ReturnsAllOccurrences()
    {
        // Create PDF with repeated word
        var repeatedPdfPath = Path.Combine(_testOutputDir, "repeated.pdf");
        TestPdfGenerator.CreateTextOnlyPdf(repeatedPdfPath, new[]
        {
            "The word test appears multiple times. Test is important. Testing test again."
        });

        // Act
        var results = _searchService.Search(repeatedPdfPath, "test", caseSensitive: false);

        // Assert
        results.Should().HaveCount(4); // "test", "Test", "Testing", "test"
    }

    [Fact]
    public void Search_NonExistentText_ReturnsEmpty()
    {
        // Act
        var results = _searchService.Search(_testPdfPath, "XYZ123NotInDocument");

        // Assert
        results.Should().BeEmpty();
    }

    [Fact]
    public void Search_MultiplePages_FindsMatchesAcrossPages()
    {
        // A real two-page document: "Page N Content" and "Secret on Page N" on each page.
        var twoPagePath = Path.Combine(_testOutputDir, "two_pages.pdf");
        TestPdfGenerator.CreateMultiPagePdf(twoPagePath, pageCount: 2);

        var results = _searchService.Search(twoPagePath, "Content", caseSensitive: true);

        results.Select(r => r.PageIndex).Should().Equal(new[] { 0, 1 },
            "one \"Content\" per page, reported against the page that holds it");
        results.Should().OnlyContain(r => r.MatchedText == "Content");
    }

    [Fact]
    public void Search_SingleCommonWord_CountsEveryOccurrenceOnThePage()
    {
        // "The quick brown fox jumps over the lazy dog." is the only line with "the".
        var results = _searchService.Search(_testPdfPath, "the", caseSensitive: false);

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.PageIndex == 0);
    }

    [Fact]
    public void Search_EmptySearchTerm_ReturnsEmpty()
    {
        // Act
        var results = _searchService.Search(_testPdfPath, "");

        // Assert
        results.Should().BeEmpty();
    }

    [Fact]
    public void Search_InvalidPdfPath_ReturnsEmpty()
    {
        // Act
        // The service catches exceptions internally and returns empty results
        var results = _searchService.Search("nonexistent.pdf", "test");

        // Assert
        results.Should().BeEmpty("Invalid path should return empty results");
    }

    /// <summary>
    /// Test for issue #96: Ensures duplicate words on the same page
    /// get distinct, correct bounding boxes for each occurrence.
    /// </summary>
    [Fact]
    public void Search_DuplicateWords_ReturnDistinctBoundingBoxes()
    {
        // Arrange: Create PDF with same word appearing multiple times at different positions
        var duplicatePdfPath = Path.Combine(_testOutputDir, "duplicates.pdf");
        // No trailing punctuation on the last word: whole-word matching compares
        // tokenized words, and a word tokenizer that keeps a trailing "." attached
        // (e.g. "CITY.") would otherwise silently drop that occurrence.
        TestPdfGenerator.CreateTextOnlyPdf(duplicatePdfPath, new[]
        {
            "CITY is a great place. I love CITY today. CITY CITY CITY"
        });

        // Act
        var results = _searchService.Search(duplicatePdfPath, "CITY", caseSensitive: true, wholeWordsOnly: true);

        // Assert: Should find 5 occurrences (each "CITY" is a whole word)
        results.Should().HaveCount(5, "each of the five CITY words is a whole-word match");

        // Each result should have a distinct position (X or Y)
        // If bounding boxes are all the same, that's the bug from #96
        var boundingBoxes = results
            .Select(r => (r.X, r.Y, r.Width, r.Height))
            .ToList();

        // At minimum, the X positions should vary for words on the same line
        var distinctXPositions = results.Select(r => r.X).Distinct().Count();
        distinctXPositions.Should().Be(5,
            "Different occurrences of CITY should have different X positions (issue #96 fix)");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testOutputDir))
        {
            Directory.Delete(_testOutputDir, true);
        }
    }
}
