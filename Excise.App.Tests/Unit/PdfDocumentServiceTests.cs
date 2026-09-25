using System;
using System.Collections.Generic;
using System.IO;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Excise.Core.Document;
using Excise.Core.Operations;
using Excise.Core.Parsing;
using Excise.Core.Security;
using Excise.Core.Text;
using Excise.Rendering.Differential;
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

    /// <summary>
    /// #1567: the current document is read from a FileStream, not a whole-file
    /// copy. The guarantee the copy used to give — the file stays writable and
    /// replaceable while open — is kept by the share mode.
    /// </summary>
    [Fact]
    public void LoadDocument_LeavesTheFileWritableAndReplaceable()
    {
        var filePath = CreateTestFile("open.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Content"));
        var replacement = CreateTestFile("replacement.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Other"));

        _service.LoadDocument(filePath);

        var writeOpen = () => { using var _ = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); };
        writeOpen.Should().NotThrow("another writer may open the file while it is our current document");
        var replace = () => File.Move(replacement, filePath, overwrite: true);
        replace.Should().NotThrow("a rename over the open file is how our own save lands");
        _service.PageCount.Should().Be(1, "the document keeps reading the inode it opened");
    }

    [Fact]
    public void SaveDocument_OntoTheOpenFile_ReloadsTheSavedContent()
    {
        var filePath = CreateTestFile("inplace.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, 3));
        _service.LoadDocument(filePath);
        _service.RemovePage(0);

        _service.SaveDocument();

        _service.PageCount.Should().Be(2, "the reload after the save sees the saved document");
        var fresh = new PdfDocumentService(NullLogger<PdfDocumentService>.Instance);
        fresh.LoadDocument(filePath);
        fresh.PageCount.Should().Be(2);
    }

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
        AssertLoadedPages("Page 2 Content", "Page 3 Content");
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
        AssertLoadedPages("Page 1 Content", "Page 2 Content", "Page 4 Content", "Page 5 Content");
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
        AssertLoadedPages("Page 2 Content", "Page 4 Content");
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
        AssertLoadedPages("Page 1 Content", "Page 2 Content", "Page 3 Content");
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
        AssertLoadedPages("Page 1 Content", "Page 4 Content");
    }

    #endregion

    #region AddPagesFromPdf Tests

    [Fact]
    public void AddPagesFromPdf_WithAllPages_AppendsToEnd()
    {
        // Arrange
        var file1 = CreateTestFile("doc1.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Base"));
        var file2 = CreateTestFile("doc2.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3));

        _service.LoadDocument(file1);

        // Act
        _service.AddPagesFromPdf(file2);

        // Assert
        _service.PageCount.Should().Be(4);
        AssertLoadedPages("Base", "Page 1 Content", "Page 2 Content", "Page 3 Content");
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
        AssertLoadedPages("Doc1", "Page 1 Content", "Page 3 Content");
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
        AssertLoadedPages("Doc1", "Page 1 Content", "Page 2 Content");
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
        AssertLoadedPages("Doc1", "Page 1 Content", "Page 2 Content");
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
            TestPdfGenerator.CreateSimpleTextPdf(path, "Inserted"));

        _service.LoadDocument(file1);

        // Act - Insert at position 1
        _service.InsertPagesFromPdf(file2, insertAtIndex: 1);

        // Assert
        _service.PageCount.Should().Be(3);
        AssertLoadedPages("Page 1 Content", "Inserted", "Page 2 Content");
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
        AssertLoadedPages("New First", "Page 1 Content", "Page 2 Content");
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
        AssertLoadedPages("Page 1 Content", "Page 3 Content", "Base");
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
        AssertLoadedPages("Page 1 Content", "Page 2 Content", "New");
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

    private static string ExtractPageText(PdfDocument document, int pageNumber)
        => new TextExtractor(document.GetPage(pageNumber)).ExtractText();

    /// <summary>
    /// Asserts the loaded document holds exactly these pages, in this order, by page text:
    /// a page count alone cannot tell "removed page 1" from "removed page 3".
    /// </summary>
    private void AssertLoadedPages(params string[] expectedTextPerPage)
    {
        var document = _service.GetCurrentDocument()!;
        document.PageCount.Should().Be(expectedTextPerPage.Length);
        for (var i = 0; i < expectedTextPerPage.Length; i++)
        {
            ExtractPageText(document, i + 1).Should().Contain(expectedTextPerPage[i],
                $"page {i + 1} of the edited document");
        }
    }

    #endregion

    #region Encrypted page assembly (#1829)

    private const string AssemblyUserPassword = "user-1829";

    private string EncryptedMultiPagePdf(string name, int pageCount, string userPassword, long permissions = -4)
    {
        var plain = CreateTestFile($"{name}-plain.pdf", path => TestPdfGenerator.CreateMultiPagePdf(path, pageCount));
        var encrypted = Path.Combine(_tempDir, $"{name}.pdf");
        using var document = PdfDocument.Open(File.ReadAllBytes(plain));
        document.Save(encrypted, new PdfEncryptionOptions
        {
            UserPassword = userPassword,
            OwnerPassword = "owner-1829",
            Permissions = permissions,
            Algorithm = PdfEncryptionAlgorithm.Aes128,
        });
        return encrypted;
    }

    /// <summary>
    /// An encrypted-source copy must need the source's password. Excise reading its own output
    /// cannot tell that from an empty-password downgrade, so qpdf decides.
    /// </summary>
    private static void AssertRequiresPassword(string path, string password)
    {
        var openWithoutPassword = () => PdfDocument.Open(path);
        openWithoutPassword.Should().Throw<PdfEncryptionNotSupportedException>(
            $"{Path.GetFileName(path)} is a copy of a password-protected document");
        using (var reopened = PdfDocument.Open(path, password))
            reopened.IsEncrypted.Should().BeTrue();

        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        QpdfReferenceTool.RequiresPassword(path).Should().Be(QpdfPasswordStatus.PasswordRequired,
            $"qpdf must refuse {Path.GetFileName(path)} without the source's password");
        QpdfReferenceTool.RequiresPassword(path, password).Should().Be(QpdfPasswordStatus.PasswordCorrect);
    }

    [Fact]
    public void ExtractPagesToPdf_EncryptedSource_WritesAPasswordProtectedCopy()
    {
        var source = EncryptedMultiPagePdf("extract-encrypted", 3, AssemblyUserPassword);
        var output = Path.Combine(_tempDir, "extract-encrypted-out.pdf");
        _service.LoadDocument(source, AssemblyUserPassword);

        _service.ExtractPagesToPdf(output, new[] { 1 });

        AssertRequiresPassword(output, AssemblyUserPassword);
    }

    [Fact]
    public void SplitDocument_EncryptedSource_WritesPasswordProtectedFragments()
    {
        var source = EncryptedMultiPagePdf("split-encrypted", 2, AssemblyUserPassword);
        var folder = Path.Combine(_tempDir, "split-encrypted-out");
        _service.LoadDocument(source, AssemblyUserPassword);

        var result = _service.SplitDocument(folder, new SplitDocumentSpecification(SplitDocumentMode.Single));

        result.EncryptionPreserved.Should().BeTrue();
        result.WrittenPaths.Should().HaveCount(2);
        foreach (var path in result.WrittenPaths)
            AssertRequiresPassword(path, AssemblyUserPassword);
    }

    /// <summary>
    /// Merge opens its sources without a password (the GUI does not prompt for one), so the
    /// sources carry an empty user password and an owner password: the copy must stay encrypted.
    /// </summary>
    [Fact]
    public void MergeDocumentsToPdf_EncryptedSources_WritesAnEncryptedCopy()
    {
        var first = EncryptedMultiPagePdf("merge-a", 1, userPassword: "");
        var second = EncryptedMultiPagePdf("merge-b", 1, userPassword: "");
        var output = Path.Combine(_tempDir, "merge-encrypted-out.pdf");

        _service.MergeDocumentsToPdf(new[] { first, second }, output).EncryptionPreserved.Should().BeTrue();

        using (var merged = PdfDocument.Open(output))
        {
            merged.IsEncrypted.Should().BeTrue("encrypted sources must not merge into a plaintext copy");
            merged.PageCount.Should().Be(2);
        }
        Assert.SkipUnless(QpdfReferenceTool.IsAvailable, "qpdf not installed");
        QpdfReferenceTool.RequiresPassword(output).Should().Be(QpdfPasswordStatus.PasswordCorrect,
            "qpdf must see an encrypted file that opens with the sources' empty user password");
    }

    private string AssembleDeniedPdf(string name) =>
        EncryptedMultiPagePdf(name, 2, userPassword: "", permissions: -4 & ~1024L); // bit 11 cleared

    [Fact]
    public void ExtractPagesToPdf_AssembleDenied_IsRefused()
    {
        _service.LoadDocument(AssembleDeniedPdf("extract-denied"));
        var output = Path.Combine(_tempDir, "extract-denied-out.pdf");

        var act = () => _service.ExtractPagesToPdf(output, new[] { 0 });

        act.Should().Throw<InvalidOperationException>().WithMessage("*bit 11*");
        File.Exists(output).Should().BeFalse("a refused extract must not write a file");

        _service.ExtractPagesToPdf(output, new[] { 0 }, ignorePermissions: true);
        File.Exists(output).Should().BeTrue("IgnoreDocumentPermissions overrides the gate");
    }

    [Fact]
    public void SplitDocument_AssembleDenied_IsRefused()
    {
        _service.LoadDocument(AssembleDeniedPdf("split-denied"));
        var folder = Path.Combine(_tempDir, "split-denied-out");

        var single = new SplitDocumentSpecification(SplitDocumentMode.Single);

        var act = () => _service.SplitDocument(folder, single);

        act.Should().Throw<InvalidOperationException>().WithMessage("*bit 11*");
        Directory.Exists(folder).Should().BeFalse("a refused split must not write fragments");

        _service.SplitDocument(folder, single, ignorePermissions: true).WrittenPaths.Should().HaveCount(2);
    }

    [Fact]
    public void MergeDocumentsToPdf_AssembleDeniedSource_IsRefused()
    {
        var output = Path.Combine(_tempDir, "merge-denied-out.pdf");

        var sources = new[] { AssembleDeniedPdf("merge-denied") };

        var act = () => _service.MergeDocumentsToPdf(sources, output);

        act.Should().Throw<InvalidOperationException>().WithMessage("*bit 11*");
        File.Exists(output).Should().BeFalse("a refused merge must not write a file");

        _service.MergeDocumentsToPdf(sources, output, ignorePermissions: true).PageCount.Should().Be(2);
    }

    #endregion

    #region RotatePage Tests

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RotatePage_SetsThePagesRotation(int degrees)
    {
        var filePath = CreateTestFile("rotate.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Rotate me"));
        _service.LoadDocument(filePath);
        _service.GetCurrentDocument()!.GetPage(1).Rotation.Should().Be(0);

        _service.RotatePage(0, degrees);

        _service.GetCurrentDocument()!.GetPage(1).Rotation.Should().Be(degrees);
    }

    [Theory]
    [InlineData("Right", 90)]
    [InlineData("Left", 270)]
    [InlineData("180", 180)]
    public void RotateShortcuts_RotateByTheirDirection(string shortcut, int expectedRotation)
    {
        var filePath = CreateTestFile("rotate.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Shortcut"));
        _service.LoadDocument(filePath);

        switch (shortcut)
        {
            case "Right": _service.RotatePageRight(0); break;
            case "Left": _service.RotatePageLeft(0); break;
            default: _service.RotatePage180(0); break;
        }

        _service.GetCurrentDocument()!.GetPage(1).Rotation.Should().Be(expectedRotation);
    }

    [Fact]
    public void RotatePage_Accumulates_AndWrapsAtFullTurn()
    {
        var filePath = CreateTestFile("rotate.pdf", path =>
            TestPdfGenerator.CreateSimpleTextPdf(path, "Turns"));
        _service.LoadDocument(filePath);

        _service.RotatePageRight(0);
        _service.RotatePageRight(0);
        _service.GetCurrentDocument()!.GetPage(1).Rotation.Should().Be(180);

        _service.RotatePageRight(0);
        _service.RotatePageRight(0);
        _service.GetCurrentDocument()!.GetPage(1).Rotation.Should().Be(0,
            "four right turns are a full circle, not a rotation of 360");
    }

    [Fact]
    public void RotatePage_OnlyTurnsTheRequestedPage_AndSurvivesASave()
    {
        var filePath = CreateTestFile("rotate3.pdf", path =>
            TestPdfGenerator.CreateMultiPagePdf(path, pageCount: 3));
        var savedPath = Path.Combine(_tempDir, "rotated.pdf");
        _service.LoadDocument(filePath);

        _service.RotatePage(1, 90);
        _service.SaveDocument(savedPath);

        using var reopened = PdfDocument.Open(File.ReadAllBytes(savedPath));
        reopened.GetPage(1).Rotation.Should().Be(0);
        reopened.GetPage(2).Rotation.Should().Be(90);
        reopened.GetPage(3).Rotation.Should().Be(0);
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
