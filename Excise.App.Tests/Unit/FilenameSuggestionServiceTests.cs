using System;
using System.IO;
using AwesomeAssertions;
using Excise.App.Services;
using Xunit;

namespace Excise.App.Tests.Unit;

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
}
