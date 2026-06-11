using System;
using System.IO;
using AwesomeAssertions;
using PdfEditor.ViewModels;
using Xunit;

namespace PdfEditor.Tests.Unit;

public class DocumentStateManagerTests
{
    [Fact]
    public void Constructor_InitializesWithEmptyState()
    {
        // Act
        var manager = new DocumentStateManager();

        // Assert
        manager.CurrentFilePath.Should().BeEmpty();
        manager.OriginalFilePath.Should().BeEmpty();
        manager.PendingRedactionsCount.Should().Be(0);
        manager.RemovedPagesCount.Should().Be(0);
        manager.FormFieldEditsCount.Should().Be(0);
        manager.TypewriterEditsCount.Should().Be(0);
        manager.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public void SetDocument_InitializesBothPaths()
    {
        // Arrange
        var manager = new DocumentStateManager();
        var testPath = "/test/document.pdf";

        // Act
        manager.SetDocument(testPath);

        // Assert
        manager.CurrentFilePath.Should().Be(testPath);
        manager.OriginalFilePath.Should().Be(testPath);
        manager.IsOriginalFile.Should().BeTrue();
    }

    [Fact]
    public void SetDocument_WithEmptyPath_ThrowsException()
    {
        // Arrange
        var manager = new DocumentStateManager();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => manager.SetDocument(""));
        Assert.Throws<ArgumentException>(() => manager.SetDocument(null!));
    }

    [Fact]
    public void UpdateCurrentPath_PreservesOriginalPath()
    {
        // Arrange
        var manager = new DocumentStateManager();
        manager.SetDocument("/original/document.pdf");

        // Act
        manager.UpdateCurrentPath("/saved/document_REDACTED.pdf");

        // Assert
        manager.CurrentFilePath.Should().Be("/saved/document_REDACTED.pdf");
        manager.OriginalFilePath.Should().Be("/original/document.pdf");
        manager.IsOriginalFile.Should().BeFalse();
    }

    [Fact]
    public void IsOriginalFile_WhenSameAsOriginal_ReturnsTrue()
    {
        // Arrange
        var manager = new DocumentStateManager();
        manager.SetDocument("/test/document.pdf");

        // Assert
        manager.IsOriginalFile.Should().BeTrue();
    }

    [Fact]
    public void IsOriginalFile_WhenDifferent_ReturnsFalse()
    {
        // Arrange
        var manager = new DocumentStateManager();
        manager.SetDocument("/test/document.pdf");

        // Act
        manager.UpdateCurrentPath("/test/document_REDACTED.pdf");

        // Assert
        manager.IsOriginalFile.Should().BeFalse();
    }

    [Fact]
    public void IsRedactedVersion_WhenContainsRedacted_ReturnsTrue()
    {
        // Arrange
        var manager = new DocumentStateManager();

        // Act & Assert
        manager.SetDocument("/test/document_REDACTED.pdf");
        manager.IsRedactedVersion.Should().BeTrue();

        manager.SetDocument("/test/document_redacted.pdf");
        manager.IsRedactedVersion.Should().BeTrue();
    }

    [Fact]
    public void IsRedactedVersion_WhenNoRedacted_ReturnsFalse()
    {
        // Arrange
        var manager = new DocumentStateManager();
        manager.SetDocument("/test/document.pdf");

        // Assert
        manager.IsRedactedVersion.Should().BeFalse();
    }

    [Fact]
    public void HasUnsavedChanges_WhenPendingRedactions_ReturnsTrue()
    {
        // Arrange
        var manager = new DocumentStateManager();
        manager.SetDocument("/test/document.pdf");

        // Act
        manager.PendingRedactionsCount = 3;

        // Assert
        manager.HasUnsavedChanges.Should().BeTrue();
    }

    [Fact]
    public void HasUnsavedChanges_WhenRemovedPages_ReturnsTrue()
    {
        // Arrange
        var manager = new DocumentStateManager();
        manager.SetDocument("/test/document.pdf");

        // Act
        manager.RemovedPagesCount = 2;

        // Assert
        manager.HasUnsavedChanges.Should().BeTrue();
    }

    [Fact]
    public void HasUnsavedChanges_WhenNoChanges_ReturnsFalse()
    {
        // Arrange
        var manager = new DocumentStateManager();
        manager.SetDocument("/test/document.pdf");

        // Assert
        manager.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public void HasUnsavedChanges_WhenTypewriterEdits_ReturnsTrue()
    {
        var manager = new DocumentStateManager();
        manager.SetDocument("/test/document.pdf");

        manager.TypewriterEditsCount = 1;

        manager.HasUnsavedChanges.Should().BeTrue();
    }

    [Fact]
    public void FileType_ReturnsCorrectDescription()
    {
        // Arrange
        var manager = new DocumentStateManager();

        // No document
        manager.FileType.Should().Be("No document");

        // Original
        manager.SetDocument("/test/document.pdf");
        manager.FileType.Should().Be("Original");

        // Original with changes
        manager.PendingRedactionsCount = 1;
        manager.FileType.Should().Be("Original (unsaved changes)");

        // Redacted version (need to create new manager or update current path)
        manager.PendingRedactionsCount = 0; // Reset changes first
        manager.UpdateCurrentPath("/test/document_REDACTED.pdf");
        manager.FileType.Should().Be("Redacted version");
    }

    [Fact]
    public void GetSaveButtonText_ReturnsCorrectText()
    {
        // Arrange
        var manager = new DocumentStateManager();

        // No changes (still returns "Save" but will be disabled)
        manager.SetDocument("/test/document.pdf");
        manager.GetSaveButtonText().Should().Be("Save");

        // Original with changes
        manager.PendingRedactionsCount = 1;
        manager.GetSaveButtonText().Should().Be("Save Redacted Version");

        // Original with non-redaction changes
        manager.PendingRedactionsCount = 0;
        manager.TypewriterEditsCount = 1;
        manager.GetSaveButtonText().Should().Be("Save a Copy");

        // Original with form changes
        manager.TypewriterEditsCount = 0;
        manager.FormFieldEditsCount = 1;
        manager.GetSaveButtonText().Should().Be("Save Filled Copy");

        // Redacted version with changes
        manager.FormFieldEditsCount = 0; // Reset first
        manager.UpdateCurrentPath("/test/document_REDACTED.pdf");
        manager.PendingRedactionsCount = 1; // Now add changes
        manager.GetSaveButtonText().Should().Be("Save");
    }

    [Fact]
    public void MarkSaved_ClearsDirtyCounters()
    {
        var manager = new DocumentStateManager();
        manager.SetDocument("/test/document.pdf");
        manager.PendingRedactionsCount = 1;
        manager.RemovedPagesCount = 1;
        manager.FormFieldEditsCount = 1;
        manager.TypewriterEditsCount = 1;

        manager.MarkSaved();

        manager.PendingRedactionsCount.Should().Be(0);
        manager.RemovedPagesCount.Should().Be(0);
        manager.FormFieldEditsCount.Should().Be(0);
        manager.TypewriterEditsCount.Should().Be(0);
        manager.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        // Arrange
        var manager = new DocumentStateManager();
        manager.SetDocument("/test/document.pdf");
        manager.PendingRedactionsCount = 3;
        manager.RemovedPagesCount = 2;
        manager.FormFieldEditsCount = 1;
        manager.TypewriterEditsCount = 1;

        // Act
        manager.Reset();

        // Assert
        manager.CurrentFilePath.Should().BeEmpty();
        manager.OriginalFilePath.Should().BeEmpty();
        manager.PendingRedactionsCount.Should().Be(0);
        manager.RemovedPagesCount.Should().Be(0);
        manager.FormFieldEditsCount.Should().Be(0);
        manager.TypewriterEditsCount.Should().Be(0);
        manager.HasUnsavedChanges.Should().BeFalse();
    }
}
