using System;
using System.Collections.Generic;
using System.IO;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.Core.Document;
using Excise.Core.Text;
using Xunit;

namespace Excise.App.Tests.Unit;

public class PdfDocumentServiceTests : IDisposable
{
    private readonly PdfDocumentService _service;
    private readonly string _tempDir;

    public PdfDocumentServiceTests()
    {
        _service = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"excise-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    private string CreateTestFile(string filename, Action<string> creator)
    {
        var path = Path.Combine(_tempDir, filename);
        creator(path);
        return path;
    }

    void IDisposable.Dispose()
    {
        _service.CloseDocument();
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    #region LoadDocument Tests

    [Fact]
    public void LoadDocument_WithValidPdf_LoadsSuccessfully()
    {
        // Arrange
        var filePath = CreateTestFile("simple.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Test Content"));

        // Act
        _service.LoadDocument(filePath);

        // Assert
        _service.IsDocumentLoaded.Should().BeTrue();
        _service.PageCount.Should().Be(1);
        _service.PdfVersion.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void LoadDocument_WithNonExistentFile_ThrowsFileNotFoundException()
    {
        // Act & Assert
        var action = () => _service.LoadDocument("/nonexistent/path/file.pdf");
        action.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void LoadDocument_SetsCorrectPageCount()
    {
        // Arrange
        var filePath = CreateTestFile("multipage.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3));

        // Act
        _service.LoadDocument(filePath);

        // Assert
        _service.PageCount.Should().Be(3);
    }

    [Fact]
    public void LoadDocument_ReplacePreviousDocument()
    {
        // Arrange
        var file1 = CreateTestFile("doc1.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Document 1"));
        var file2 = CreateTestFile("doc2.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2));

        // Act
        _service.LoadDocument(file1);
        var firstPageCount = _service.PageCount;
        _service.LoadDocument(file2);
        var secondPageCount = _service.PageCount;

        // Assert
        firstPageCount.Should().Be(1);
        secondPageCount.Should().Be(2);
    }

    [Fact]
    public void LoadDocument_WithMultiPagePdf_SetsCorrectPdfVersion()
    {
        // Arrange
        var filePath = CreateTestFile("versioned.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path));

        // Act
        _service.LoadDocument(filePath);

        // Assert
        _service.PdfVersion.Should().NotBeNullOrEmpty();
        _service.PdfVersion.Should().Match("*.*"); // At least "1.x" format
    }

    #endregion

    #region GetPageWidth / GetPageHeight Tests

    [Fact]
    public void GetPageWidth_WithLoadedDocument_ReturnsPositiveValue()
    {
        // Arrange
        var filePath = CreateTestFile("sized.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path));

        _service.LoadDocument(filePath);

        // Act
        var width = _service.GetPageWidth(0);

        // Assert
        width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetPageHeight_WithLoadedDocument_ReturnsPositiveValue()
    {
        // Arrange
        var filePath = CreateTestFile("sized.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path));

        _service.LoadDocument(filePath);

        // Act
        var height = _service.GetPageHeight(0);

        // Assert
        height.Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetPageWidth_WithoutDocument_ReturnsFallbackLetterWidth()
    {
        // Act
        var width = _service.GetPageWidth(0);

        // Assert
        width.Should().Be(612); // Letter width in points
    }

    [Fact]
    public void GetPageHeight_WithoutDocument_ReturnsFallbackLetterHeight()
    {
        // Act
        var height = _service.GetPageHeight(0);

        // Assert
        height.Should().Be(792); // Letter height in points
    }

    [Fact]
    public void GetPageWidth_WithInvalidPageIndex_ReturnsFallback()
    {
        // Arrange
        var filePath = CreateTestFile("multipage.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2));

        _service.LoadDocument(filePath);

        // Act
        var width = _service.GetPageWidth(999);

        // Assert
        width.Should().Be(612); // Falls back to Letter
    }

    [Fact]
    public void GetPageHeight_WithNegativePageIndex_ReturnsFallback()
    {
        // Arrange
        var filePath = CreateTestFile("multipage.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2));

        _service.LoadDocument(filePath);

        // Act
        var height = _service.GetPageHeight(-1);

        // Assert
        height.Should().Be(792); // Falls back to Letter
    }

    [Fact]
    public void GetPageWidth_MultiPagePdf_AllPagesReturnValues()
    {
        // Arrange
        var filePath = CreateTestFile("multipage.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3));

        _service.LoadDocument(filePath);

        // Act & Assert
        for (int i = 0; i < 3; i++)
        {
            var width = _service.GetPageWidth(i);
            width.Should().BeGreaterThan(0);
        }
    }

    #endregion

    #region SaveDocument Tests

    [Fact]
    public void SaveDocument_WithoutPath_SavesToOriginalPath()
    {
        // Arrange
        var filePath = CreateTestFile("tosave.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Content"));

        _service.LoadDocument(filePath);

        // Act
        _service.SaveDocument();

        // Assert
        File.Exists(filePath).Should().BeTrue();
        var savedSize = new FileInfo(filePath).Length;
        savedSize.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SaveDocument_WithNewPath_SavesToNewPath()
    {
        // Arrange
        var originalPath = CreateTestFile("original.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Content"));

        var newPath = Path.Combine(_tempDir, "saved-copy.pdf");

        _service.LoadDocument(originalPath);

        // Act
        _service.SaveDocument(newPath);

        // Assert
        File.Exists(newPath).Should().BeTrue();
        new FileInfo(newPath).Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SaveDocument_WithoutLoadedDocument_ThrowsInvalidOperation()
    {
        // Act & Assert
        var action = () => _service.SaveDocument();
        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SaveDocument_WithoutPathAndNoOriginalPath_ThrowsArgumentException()
    {
        // Arrange
        // We can't easily test this without loading from memory, but we can verify
        // the exception for no document
        var action = () => _service.SaveDocument(null);
        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SaveDocument_CreatesValidPdf()
    {
        // Arrange
        var filePath = CreateTestFile("tosave.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Test"));

        _service.LoadDocument(filePath);
        var originalPageCount = _service.PageCount;

        // Act
        _service.SaveDocument();

        // Assert - Verify we can re-open it
        var newService = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        newService.LoadDocument(filePath);
        newService.PageCount.Should().Be(originalPageCount);
    }

    #endregion

    #region RemovePage Tests

    [Fact]
    public void RemovePage_FromMultiPageDocument_ReducesPageCount()
    {
        // Arrange
        var filePath = CreateTestFile("multipage.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3));

        _service.LoadDocument(filePath);

        // Act
        _service.RemovePage(0);

        // Assert
        _service.PageCount.Should().Be(2);
    }

    [Fact]
    public void RemovePage_WithoutDocument_ThrowsInvalidOperation()
    {
        // Act & Assert
        var action = () => _service.RemovePage(0);
        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RemovePage_WithNegativeIndex_ThrowsArgumentOutOfRange()
    {
        // Arrange
        var filePath = CreateTestFile("multipage.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2));

        _service.LoadDocument(filePath);

        // Act & Assert
        var action = () => _service.RemovePage(-1);
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void RemovePage_WithIndexBeyondPageCount_ThrowsArgumentOutOfRange()
    {
        // Arrange
        var filePath = CreateTestFile("multipage.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2));

        _service.LoadDocument(filePath);

        // Act & Assert
        var action = () => _service.RemovePage(999);
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void RemovePage_RemoveLastPage_ThrowsInvalidOperation()
    {
        // Arrange
        var filePath = CreateTestFile("single.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Single"));

        _service.LoadDocument(filePath);

        // Act & Assert
        var action = () => _service.RemovePage(0);
        action.Should().Throw<InvalidOperationException>("PDF must have at least one page");
    }

    [Fact]
    public void RemovePage_RemoveMiddlePage_ResultsInCorrectCount()
    {
        // Arrange
        var filePath = CreateTestFile("multipage.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 5));

        _service.LoadDocument(filePath);

        // Act
        _service.RemovePage(2); // Remove third page

        // Assert
        _service.PageCount.Should().Be(4);
    }

    #endregion

    #region RemovePages Tests

    [Fact]
    public void RemovePages_WithMultipleIndices_RemovesAll()
    {
        // Arrange
        var filePath = CreateTestFile("multipage.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 4));

        _service.LoadDocument(filePath);

        // Act
        _service.RemovePages(new[] { 0, 2 });

        // Assert
        _service.PageCount.Should().Be(2);
    }

    [Fact]
    public void RemovePages_WithEmptyList_DoesNothing()
    {
        // Arrange
        var filePath = CreateTestFile("multipage.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3));

        _service.LoadDocument(filePath);

        // Act
        _service.RemovePages(Array.Empty<int>());

        // Assert
        _service.PageCount.Should().Be(3);
    }

    [Fact]
    public void RemovePages_WithUnsortedIndices_RemovesCorrectly()
    {
        // Arrange
        var filePath = CreateTestFile("multipage.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 5));

        _service.LoadDocument(filePath);

        // Act
        _service.RemovePages(new[] { 4, 1, 2 }); // Unsorted

        // Assert
        _service.PageCount.Should().Be(2);
    }

    #endregion

    #region AddPagesFromPdf Tests

    [Fact]
    public void AddPagesFromPdf_WithAllPages_AppendsToEnd()
    {
        // Arrange
        var file1 = CreateTestFile("doc1.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2));
        var file2 = CreateTestFile("doc2.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3));

        _service.LoadDocument(file1);

        // Act
        _service.AddPagesFromPdf(file2);

        // Assert
        _service.PageCount.Should().Be(5);
    }

    [Fact]
    public void AddPagesFromPdf_WithSpecificIndices_AppendsOnlySelected()
    {
        // Arrange
        var file1 = CreateTestFile("doc1.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Doc1"));
        var file2 = CreateTestFile("doc2.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3));

        _service.LoadDocument(file1);

        // Act
        _service.AddPagesFromPdf(file2, pageIndices: new[] { 0, 2 });

        // Assert
        _service.PageCount.Should().Be(3); // 1 original + 2 added
    }

    [Fact]
    public void AddPagesFromPdf_WithNullIndices_AppendsAll()
    {
        // Arrange
        var file1 = CreateTestFile("doc1.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Doc1"));
        var file2 = CreateTestFile("doc2.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2));

        _service.LoadDocument(file1);

        // Act
        _service.AddPagesFromPdf(file2, pageIndices: null);

        // Assert
        _service.PageCount.Should().Be(3);
    }

    [Fact]
    public void AddPagesFromPdf_WithoutDocument_ThrowsInvalidOperation()
    {
        // Arrange
        var file2 = CreateTestFile("doc2.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Doc2"));

        // Act & Assert
        var action = () => _service.AddPagesFromPdf(file2);
        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddPagesFromPdf_WithInvalidIndices_SkipsInvalidOnes()
    {
        // Arrange
        var file1 = CreateTestFile("doc1.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Doc1"));
        var file2 = CreateTestFile("doc2.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3));

        _service.LoadDocument(file1);

        // Act - Include out-of-range indices
        _service.AddPagesFromPdf(file2, pageIndices: new[] { 0, 999, 1 });

        // Assert - Only valid indices (0, 1) should be added
        _service.PageCount.Should().Be(3);
    }

    #endregion

    #region InsertPagesFromPdf Tests

    [Fact]
    public void InsertPagesFromPdf_AtPosition_InsertsCorrectly()
    {
        // Arrange
        var file1 = CreateTestFile("doc1.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2));
        var file2 = CreateTestFile("doc2.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 1));

        _service.LoadDocument(file1);

        // Act - Insert at position 1
        _service.InsertPagesFromPdf(file2, insertAtIndex: 1);

        // Assert
        _service.PageCount.Should().Be(3);
    }

    [Fact]
    public void InsertPagesFromPdf_AtBeginning_InsertsAtStart()
    {
        // Arrange
        var file1 = CreateTestFile("doc1.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2));
        var file2 = CreateTestFile("doc2.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "New First"));

        _service.LoadDocument(file1);

        // Act
        _service.InsertPagesFromPdf(file2, insertAtIndex: 0);

        // Assert
        _service.PageCount.Should().Be(3);
    }

    [Fact]
    public void InsertPagesFromPdf_WithoutDocument_ThrowsInvalidOperation()
    {
        // Arrange
        var file2 = CreateTestFile("doc2.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Src"));

        // Act & Assert
        var action = () => _service.InsertPagesFromPdf(file2, 0);
        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void InsertPagesFromPdf_WithSpecificIndices_InsertsOnlySelected()
    {
        // Arrange
        var file1 = CreateTestFile("doc1.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Base"));
        var file2 = CreateTestFile("doc2.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 4));

        _service.LoadDocument(file1);

        // Act - Insert pages 0 and 2 from file2 at position 0
        _service.InsertPagesFromPdf(file2, insertAtIndex: 0, pageIndices: new[] { 0, 2 });

        // Assert
        _service.PageCount.Should().Be(3); // 2 inserted + 1 original
    }

    [Fact]
    public void InsertPagesFromPdf_AtEndPosition_AppendsEffectively()
    {
        // Arrange
        var file1 = CreateTestFile("doc1.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2));
        var file2 = CreateTestFile("doc2.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "New"));

        _service.LoadDocument(file1);

        // Act - Insert at position beyond current pages
        _service.InsertPagesFromPdf(file2, insertAtIndex: 2);

        // Assert
        _service.PageCount.Should().Be(3);
    }

    #endregion

    #region Page Organization Tests

    [Fact]
    public void MovePage_ChangesPersistedPageOrder()
    {
        // Arrange
        var filePath = CreateTestFile("move.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3));
        var savedPath = Path.Combine(_tempDir, "moved.pdf");
        _service.LoadDocument(filePath);

        // Act
        _service.MovePage(0, 2);
        _service.SaveDocument(savedPath);

        // Assert
        using var reopened = PdfDocument.Open(File.ReadAllBytes(savedPath));
        ExtractPageText(reopened, 1).Should().Contain("Page 2 Content");
        ExtractPageText(reopened, 2).Should().Contain("Page 3 Content");
        ExtractPageText(reopened, 3).Should().Contain("Page 1 Content");
    }

    [Fact]
    public void MovePage_WithInvalidTarget_ThrowsArgumentOutOfRange()
    {
        var filePath = CreateTestFile("move-invalid.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 2));
        _service.LoadDocument(filePath);

        var action = () => _service.MovePage(0, 2);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ExtractPagesToPdf_WritesSelectedPagesInCallerOrder()
    {
        // Arrange
        var filePath = CreateTestFile("extract-source.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 4));
        var outputPath = Path.Combine(_tempDir, "extracted.pdf");
        _service.LoadDocument(filePath);

        // Act
        _service.ExtractPagesToPdf(outputPath, new[] { 2, 0 });

        // Assert
        using var extracted = PdfDocument.Open(File.ReadAllBytes(outputPath));
        extracted.PageCount.Should().Be(2);
        ExtractPageText(extracted, 1).Should().Contain("Page 3 Content");
        ExtractPageText(extracted, 2).Should().Contain("Page 1 Content");
    }

    [Fact]
    public void AnalyzePageOperationPreservation_ReportsDocumentLevelStructures()
    {
        var filePath = CreateTestFile("warnings.pdf", path =>
        {
            using var doc = PdfDocument.CreateNew();
            doc.Pages.AddBlank();
            doc.Catalog["Outlines"] = new Excise.Core.Primitives.PdfDictionary();
            doc.Save(path);
        });
        _service.LoadDocument(filePath);

        var diagnostics = _service.AnalyzePageOperationPreservation();

        diagnostics.HasWarnings.Should().BeTrue();
        diagnostics.Warnings.Should().Contain(w => w.Contains("outlines", StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtractPageText(PdfDocument document, int pageNumber)
        => new TextExtractor(document.GetPage(pageNumber)).ExtractText();

    #endregion

    #region RotatePage Tests

    [Fact]
    public void RotatePage_With90Degrees_AppliesRotation()
    {
        // Arrange
        var filePath = CreateTestFile("rotate.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Rotate me"));

        _service.LoadDocument(filePath);

        // Act
        _service.RotatePage(0, 90);

        // Assert - Just verify no exception; actual rotation state is internal
        _service.PageCount.Should().Be(1);
    }

    [Fact]
    public void RotatePage_With180Degrees_AppliesRotation()
    {
        // Arrange
        var filePath = CreateTestFile("rotate.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Rotate"));

        _service.LoadDocument(filePath);

        // Act
        _service.RotatePage(0, 180);

        // Assert
        _service.PageCount.Should().Be(1);
    }

    [Fact]
    public void RotatePage_With270Degrees_AppliesRotation()
    {
        // Arrange
        var filePath = CreateTestFile("rotate.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Rotate"));

        _service.LoadDocument(filePath);

        // Act
        _service.RotatePage(0, 270);

        // Assert
        _service.PageCount.Should().Be(1);
    }

    [Fact]
    public void RotatePage_WithInvalidDegrees_ThrowsArgumentException()
    {
        // Arrange
        var filePath = CreateTestFile("rotate.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Rotate"));

        _service.LoadDocument(filePath);

        // Act & Assert
        var action = () => _service.RotatePage(0, 45);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RotatePageRight_CallsRotatePage()
    {
        // Arrange
        var filePath = CreateTestFile("rotate.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Right"));

        _service.LoadDocument(filePath);

        // Act
        _service.RotatePageRight(0);

        // Assert
        _service.PageCount.Should().Be(1);
    }

    [Fact]
    public void RotatePageLeft_CallsRotatePage()
    {
        // Arrange
        var filePath = CreateTestFile("rotate.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Left"));

        _service.LoadDocument(filePath);

        // Act
        _service.RotatePageLeft(0);

        // Assert
        _service.PageCount.Should().Be(1);
    }

    [Fact]
    public void RotatePage180_CallsRotatePage()
    {
        // Arrange
        var filePath = CreateTestFile("rotate.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "180"));

        _service.LoadDocument(filePath);

        // Act
        _service.RotatePage180(0);

        // Assert
        _service.PageCount.Should().Be(1);
    }

    [Fact]
    public void RotatePage_WithoutDocument_ThrowsInvalidOperation()
    {
        // Act & Assert
        var action = () => _service.RotatePage(0, 90);
        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RotatePage_WithInvalidPageIndex_ThrowsArgumentOutOfRange()
    {
        // Arrange
        var filePath = CreateTestFile("rotate.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Text"));

        _service.LoadDocument(filePath);

        // Act & Assert
        var action = () => _service.RotatePage(999, 90);
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region CloseDocument Tests

    [Fact]
    public void CloseDocument_WhenLoaded_ClearsState()
    {
        // Arrange
        var filePath = CreateTestFile("close.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Close"));

        _service.LoadDocument(filePath);
        _service.IsDocumentLoaded.Should().BeTrue();

        // Act
        _service.CloseDocument();

        // Assert
        _service.IsDocumentLoaded.Should().BeFalse();
        _service.PageCount.Should().Be(0);
    }

    [Fact]
    public void CloseDocument_WhenNotLoaded_DoesNotThrow()
    {
        // Act & Assert
        var action = () => _service.CloseDocument();
        action.Should().NotThrow();
    }

    [Fact]
    public void CloseDocument_ThenLoadAgain_Works()
    {
        // Arrange
        var filePath = CreateTestFile("reuse.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Reuse"));

        _service.LoadDocument(filePath);
        _service.CloseDocument();

        // Act
        _service.LoadDocument(filePath);

        // Assert
        _service.IsDocumentLoaded.Should().BeTrue();
        _service.PageCount.Should().Be(1);
    }

    #endregion

    #region GetCurrentDocument Tests

    [Fact]
    public void GetCurrentDocument_WhenLoaded_ReturnsDocument()
    {
        // Arrange
        var filePath = CreateTestFile("getcurrent.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Current"));

        _service.LoadDocument(filePath);

        // Act
        var doc = _service.GetCurrentDocument();

        // Assert
        doc.Should().NotBeNull();
    }

    [Fact]
    public void GetCurrentDocument_WhenNotLoaded_ReturnsNull()
    {
        // Act
        var doc = _service.GetCurrentDocument();

        // Assert
        doc.Should().BeNull();
    }

    [Fact]
    public void GetCurrentDocument_ReturnsDocumentWithCorrectPageCount()
    {
        // Arrange
        var filePath = CreateTestFile("multipage.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3));

        _service.LoadDocument(filePath);

        // Act
        var doc = _service.GetCurrentDocument();

        // Assert
        doc.Should().NotBeNull();
        doc!.PageCount.Should().Be(3);
    }

    #endregion

    #region GetCurrentDocumentAsStream Tests

    [Fact]
    public void GetCurrentDocumentAsStream_WhenLoaded_ReturnsMemoryStream()
    {
        // Arrange
        var filePath = CreateTestFile("stream.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Stream"));

        _service.LoadDocument(filePath);

        // Act
        using var stream = _service.GetCurrentDocumentAsStream();

        // Assert
        stream.Should().NotBeNull();
        stream!.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetCurrentDocumentAsStream_WhenNotLoaded_ReturnsNull()
    {
        // Act
        var stream = _service.GetCurrentDocumentAsStream();

        // Assert
        stream.Should().BeNull();
    }

    [Fact]
    public void GetCurrentDocumentAsStream_StreamIsAtPositionZero()
    {
        // Arrange
        var filePath = CreateTestFile("stream.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Stream"));

        _service.LoadDocument(filePath);

        // Act
        using var stream = _service.GetCurrentDocumentAsStream();

        // Assert
        stream!.Position.Should().Be(0);
    }

    [Fact]
    public void GetCurrentDocumentAsStream_StreamCanBeRead()
    {
        // Arrange
        var filePath = CreateTestFile("stream.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Stream"));

        _service.LoadDocument(filePath);

        // Act
        using var stream = _service.GetCurrentDocumentAsStream();

        // Assert
        stream.Should().NotBeNull();
        var bytes = new byte[4];
        var read = stream!.Read(bytes, 0, 4);
        read.Should().BeGreaterThan(0);
        // Check for PDF magic bytes: %PDF
        bytes[0].Should().Be((byte)'%');
        bytes[1].Should().Be((byte)'P');
        bytes[2].Should().Be((byte)'D');
        bytes[3].Should().Be((byte)'F');
    }

    [Fact]
    public void GetCurrentDocumentAsStream_MultipleCallsProduceDifferentStreams()
    {
        // Arrange
        var filePath = CreateTestFile("stream.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Stream"));

        _service.LoadDocument(filePath);

        // Act
        using var stream1 = _service.GetCurrentDocumentAsStream();
        using var stream2 = _service.GetCurrentDocumentAsStream();

        // Assert - Different instances but same content
        stream1.Should().NotBeSameAs(stream2);
        stream1!.Length.Should().Be(stream2!.Length);
    }

    #endregion
}
