using System;
using System.IO;
using AwesomeAssertions;
using PdfEditor.Services;
using Xunit;

namespace PdfEditor.Tests.Unit;

public class FilenameSuggestionServiceTests
{
    private readonly FilenameSuggestionService _service = new();

    [Fact]
    public void SuggestRedactedFilename_AppendsRedacted()
    {
        // Arrange
        // Platform-agnostic: on Windows Path.Combine emits '\\', so the
        // expectation must be built the same way the service builds it.
        var originalPath = Path.Combine("documents", "contract.pdf");

        // Act
        var suggested = _service.SuggestRedactedFilename(originalPath);

        // Assert
        suggested.Should().Be(Path.Combine("documents", "contract_REDACTED.pdf"));
    }

    [Fact]
    public void SuggestRedactedFilename_PreservesDirectory()
    {
        // Arrange
        var dir = Path.Combine("very", "deep", "folder", "structure");
        var originalPath = Path.Combine(dir, "document.pdf");

        // Act
        var suggested = _service.SuggestRedactedFilename(originalPath);

        // Assert
        suggested.Should().StartWith(dir + Path.DirectorySeparatorChar);
        suggested.Should().EndWith("_REDACTED.pdf");
    }

    [Fact]
    public void SuggestRedactedFilename_PreservesExtension()
    {
        // Arrange
        var originalPath = "/documents/contract.PDF"; // Uppercase extension

        // Act
        var suggested = _service.SuggestRedactedFilename(originalPath);

        // Assert
        suggested.Should().EndWith("_REDACTED.PDF");
    }

    [Fact]
    public void SuggestRedactedFilename_HandlesFilenameOnly()
    {
        // Arrange
        var originalPath = "document.pdf";

        // Act
        var suggested = _service.SuggestRedactedFilename(originalPath);

        // Assert
        suggested.Should().Be("document_REDACTED.pdf");
    }

    [Fact]
    public void SuggestRedactedFilename_WithEmptyPath_ThrowsException()
    {
        // Act & Assert
        Action act = () => _service.SuggestRedactedFilename("");
        act.Should().Throw<ArgumentException>();

        Action actNull = () => _service.SuggestRedactedFilename(null!);
        actNull.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SuggestPageSubsetFilename_AddsPageRange()
    {
        // Arrange
        var originalPath = Path.Combine("documents", "report.pdf");

        // Act
        var suggested = _service.SuggestPageSubsetFilename(originalPath, "1-5");

        // Assert
        suggested.Should().Be(Path.Combine("documents", "report_pages_1-5.pdf"));
    }

    [Fact]
    public void SuggestPageSubsetFilename_HandlesComplexRanges()
    {
        // Arrange
        var originalPath = Path.Combine("documents", "report.pdf");

        // Act
        var suggested = _service.SuggestPageSubsetFilename(originalPath, "3,7,9-12");

        // Assert
        suggested.Should().Be(Path.Combine("documents", "report_pages_3,7,9-12.pdf"));
    }

    [Fact]
    public void SuggestPageSubsetFilename_WithEmptyPageRange_ThrowsException()
    {
        // Act & Assert
        Action act = () => _service.SuggestPageSubsetFilename("/test.pdf", "");
        act.Should().Throw<ArgumentException>();

        Action actNull = () => _service.SuggestPageSubsetFilename("/test.pdf", null!);
        actNull.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SuggestWithAutoIncrement_WhenNotExists_ReturnsOriginal()
    {
        // Arrange
        var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".pdf");

        // Act
        var suggested = _service.SuggestWithAutoIncrement(nonExistentPath);

        // Assert
        suggested.Should().Be(nonExistentPath);
    }

    [Fact]
    public void SuggestWithAutoIncrement_WhenExists_AddsNumber()
    {
        // Arrange
        var tempFile = Path.GetTempFileName();
        tempFile = Path.ChangeExtension(tempFile, ".pdf");
        try
        {
            File.WriteAllText(tempFile, "test");

            // Act
            var suggested = _service.SuggestWithAutoIncrement(tempFile);

            // Assert
            suggested.Should().NotBe(tempFile);
            suggested.Should().EndWith("_2.pdf");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void SuggestWithAutoIncrement_WhenMultipleExist_FindsNextAvailable()
    {
        // Arrange
        var tempDir = Path.GetTempPath();
        var baseName = Guid.NewGuid().ToString();
        var originalFile = Path.Combine(tempDir, $"{baseName}.pdf");
        var file2 = Path.Combine(tempDir, $"{baseName}_2.pdf");
        var file3 = Path.Combine(tempDir, $"{baseName}_3.pdf");

        try
        {
            File.WriteAllText(originalFile, "test");
            File.WriteAllText(file2, "test");
            File.WriteAllText(file3, "test");

            // Act
            var suggested = _service.SuggestWithAutoIncrement(originalFile);

            // Assert
            suggested.Should().EndWith("_4.pdf");
        }
        finally
        {
            if (File.Exists(originalFile)) File.Delete(originalFile);
            if (File.Exists(file2)) File.Delete(file2);
            if (File.Exists(file3)) File.Delete(file3);
        }
    }

    [Fact]
    public void SuggestSafeRedactedFilename_WhenOriginalDoesntExist_ReturnsRedactedName()
    {
        // Arrange
        var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".pdf");

        // Act
        var suggested = _service.SuggestSafeRedactedFilename(nonExistentPath);

        // Assert
        suggested.Should().EndWith("_REDACTED.pdf");
        suggested.Should().NotContain("_2");
    }

    [Fact]
    public void SuggestSafeRedactedFilename_WhenRedactedExists_AddsNumber()
    {
        // Arrange
        var tempDir = Path.GetTempPath();
        var baseName = Guid.NewGuid().ToString();
        var originalFile = Path.Combine(tempDir, $"{baseName}.pdf");
        var redactedFile = Path.Combine(tempDir, $"{baseName}_REDACTED.pdf");

        try
        {
            File.WriteAllText(redactedFile, "test");

            // Act
            var suggested = _service.SuggestSafeRedactedFilename(originalFile);

            // Assert
            suggested.Should().EndWith("_REDACTED_2.pdf");
        }
        finally
        {
            if (File.Exists(redactedFile)) File.Delete(redactedFile);
        }
    }
}
