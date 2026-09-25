using System;
using System.IO;
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
        var tempFile = Path.Combine(Path.GetTempPath(), $"excise-textextract-{Guid.NewGuid():N}.pdf");
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
}
