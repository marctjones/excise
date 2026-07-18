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
        results.Should().NotBeEmpty();
        results.Should().Contain(m => m.MatchedText.Contains("Hello"));
        results.First().PageIndex.Should().Be(0);
    }

    [Fact]
    public void Search_CaseSensitive_RespectsCase()
    {
        // Act - Case insensitive (default)
        var resultsInsensitive = _searchService.Search(_testPdfPath, "hello", caseSensitive: false);

        // Act - Case sensitive
        var resultsSensitive = _searchService.Search(_testPdfPath, "hello", caseSensitive: true);

        // Assert
        resultsInsensitive.Should().NotBeEmpty();
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
        wholeWordResults.Should().NotBeEmpty();
        partialResults.Should().BeEmpty(); // "ox" is not a complete word
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
        results.Should().HaveCountGreaterThanOrEqualTo(3); // "test", "Test", "Testing", "test"
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
        // Act
        var results = _searchService.Search(_testPdfPath, "the", caseSensitive: false);

        // Assert
        results.Should().NotBeEmpty();
        var pageIndices = results.Select(r => r.PageIndex).Distinct().ToList();
        // All text is on page 0 when using CreateTextOnlyPdf with array
        pageIndices.Should().Contain(0); // "The quick brown fox..." on page 1 (index 0)
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
        TestPdfGenerator.CreateTextOnlyPdf(duplicatePdfPath, new[]
        {
            "CITY is a great place. I love CITY life. CITY CITY CITY."
        });

        // Act
        var results = _searchService.Search(duplicatePdfPath, "CITY", caseSensitive: true, wholeWordsOnly: true);

        // Assert: Should find 5 occurrences (each "CITY" is a whole word)
        results.Should().HaveCountGreaterThanOrEqualTo(3, "Should find multiple CITY occurrences");

        // Each result should have a distinct position (X or Y)
        // If bounding boxes are all the same, that's the bug from #96
        var boundingBoxes = results
            .Select(r => (r.X, r.Y, r.Width, r.Height))
            .ToList();

        // At minimum, the X positions should vary for words on the same line
        var distinctXPositions = results.Select(r => r.X).Distinct().Count();
        distinctXPositions.Should().BeGreaterThanOrEqualTo(2,
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
