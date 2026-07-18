using System;
using System.IO;
using Avalonia;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Xunit;

namespace Excise.App.Tests.Unit;

public class PdfTextExtractionServiceTests
{
    private readonly PdfTextExtractionService _service;

    public PdfTextExtractionServiceTests()
    {
        var logger = new Mock<ILogger<PdfTextExtractionService>>();
        _service = new PdfTextExtractionService(logger.Object);
    }

    [Fact]
    public void ExtractTextFromPage_WithStream_ExtractsCorrectText()
    {
        // Arrange
        var pdfBytes = TestPdfGenerator.CreateSimplePdf("Hello from stream");
        using var stream = new MemoryStream(pdfBytes);

        // Act
        var text = _service.ExtractTextFromPage(stream, 0);

        // Assert
        text.Should().Contain("Hello from stream");
    }

    [Fact]
    public void ExtractTextFromPage_WithFilePath_ExtractsCorrectText()
    {
        // Arrange
        var tempFile = Path.GetTempFileName() + ".pdf";
        try
        {
            var pdfBytes = TestPdfGenerator.CreateSimplePdf("Hello from file");
            File.WriteAllBytes(tempFile, pdfBytes);

            // Act
            var text = _service.ExtractTextFromPage(tempFile, 0);

            // Assert
            text.Should().Contain("Hello from file");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void ExtractTextFromPage_WithInvalidPageIndex_ReturnsEmpty()
    {
        // Arrange
        var pdfBytes = TestPdfGenerator.CreateSimplePdf("Test");
        using var stream = new MemoryStream(pdfBytes);

        // Act
        var text = _service.ExtractTextFromPage(stream, 99);

        // Assert
        text.Should().BeEmpty();
    }

    [Fact]
    public void ExtractTextFromArea_StreamAndFile_ProduceSameResults()
    {
        // Arrange - Same PDF content
        var pdfBytes = TestPdfGenerator.CreateSimplePdf("Test Content");
        var tempFile = Path.GetTempFileName() + ".pdf";

        try
        {
            File.WriteAllBytes(tempFile, pdfBytes);

            // Use a large area that captures all text on the page
            var area = new Rect(0, 0, 500, 500);

            // Act - Extract using both methods
            string streamResult;
            using (var stream = new MemoryStream(pdfBytes))
            {
                streamResult = _service.ExtractTextFromArea(stream, 0, area);
            }

            var fileResult = _service.ExtractTextFromArea(tempFile, 0, area);

            // Assert - Both methods should return identical results
            streamResult.Should().Be(fileResult);
            streamResult.Should().Contain("Test Content");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void ExtractTextFromArea_WithEmptyArea_ReturnsEmpty()
    {
        // Arrange
        var pdfBytes = TestPdfGenerator.CreateSimplePdf("Test");
        using var stream = new MemoryStream(pdfBytes);

        // Act - Area with no text
        var area = new Rect(0, 0, 1, 1);
        var text = _service.ExtractTextFromArea(stream, 0, area);

        // Assert
        text.Should().BeEmpty();
    }

    [Fact]
    public void StreamBasedExtraction_WorksWithMemoryStream()
    {
        // Arrange - This simulates the use case where we have modified PDF in memory
        var pdfBytes = TestPdfGenerator.CreateSimplePdf("In-Memory PDF");
        using var memoryStream = new MemoryStream(pdfBytes);

        // Act - Extract text from memory without touching disk
        var text = _service.ExtractTextFromPage(memoryStream, 0);

        // Assert
        text.Should().Contain("In-Memory PDF");
        // Verify stream was used, not disk (implicitly verified by not creating temp file)
    }

    /// <summary>
    /// Issue #105: Verify text extraction returns letters in correct left-to-right order.
    /// Previously, text like "birthsize" was extracted as "tsizebirt" (scrambled).
    /// </summary>
    [Fact]
    public void ExtractTextFromArea_PreservesLeftToRightOrder()
    {
        // Arrange - Create PDF with text that could be scrambled
        var pdfBytes = TestPdfGenerator.CreateSimplePdf("ABCDEFGHIJ");
        using var stream = new MemoryStream(pdfBytes);

        // Use a large area that captures all text
        var area = new Rect(0, 0, 500, 500);

        // Act
        var text = _service.ExtractTextFromArea(stream, 0, area);

        // Assert - Text should be in exact left-to-right order, not scrambled
        text.Trim().Should().Be("ABCDEFGHIJ",
            "Text should be extracted in left-to-right reading order, not scrambled");
    }

    /// <summary>
    /// Issue #105: Verify that compound words are extracted correctly.
    /// </summary>
    [Fact]
    public void ExtractTextFromArea_CompoundWords_CorrectOrder()
    {
        // Arrange - Compound word similar to "birthsize" issue
        var pdfBytes = TestPdfGenerator.CreateSimplePdf("birthsize");
        using var stream = new MemoryStream(pdfBytes);

        var area = new Rect(0, 0, 500, 500);

        // Act
        var text = _service.ExtractTextFromArea(stream, 0, area);

        // Assert - Should NOT be "tsizebirt" or any other scrambling
        text.Trim().Should().Be("birthsize",
            "Compound words should be extracted in correct order, not scrambled like 'tsizebirt'");
    }

    /// <summary>
    /// Issue #105: Verify multiword extraction maintains word order.
    /// </summary>
    [Fact]
    public void ExtractTextFromArea_MultipleWords_CorrectOrder()
    {
        // Arrange
        var pdfBytes = TestPdfGenerator.CreateSimplePdf("The quick brown fox");
        using var stream = new MemoryStream(pdfBytes);

        var area = new Rect(0, 0, 500, 500);

        // Act
        var text = _service.ExtractTextFromArea(stream, 0, area);

        // Assert - Words should be in correct order with proper spacing
        text.Trim().Should().Contain("The");
        text.Trim().Should().Contain("quick");
        text.Trim().Should().Contain("brown");
        text.Trim().Should().Contain("fox");

        // Check relative order - "The" should appear before "quick"
        var theIndex = text.IndexOf("The");
        var quickIndex = text.IndexOf("quick");
        theIndex.Should().BeLessThan(quickIndex, "Words should be in reading order");
    }
}
