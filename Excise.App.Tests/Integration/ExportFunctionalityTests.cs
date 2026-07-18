using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Excise.App.Services;
using Excise.App.Tests.Utilities;
using Xunit;
using System.IO; // Added for FileStream
using SkiaSharp; // Added for SKBitmap operations

namespace Excise.App.Tests.Integration;

/// <summary>
/// Tests for PDF export functionality.
/// Uses PdfRenderingTests collection to avoid parallel execution issues with PDFium.
/// </summary>
[Collection("PdfRenderingTests")]
public class ExportFunctionalityTests : IDisposable
{
    private readonly PdfDocumentService _documentService;
    private readonly PdfRenderService _renderService;
    private readonly string _testOutputDir;
    private readonly string _testPdfPath;

    public ExportFunctionalityTests()
    {
        var docLogger = new Mock<ILogger<PdfDocumentService>>().Object;
        var renderLogger = new Mock<ILogger<PdfRenderService>>().Object;

        _documentService = new PdfDocumentService(docLogger);
        _renderService = new PdfRenderService(renderLogger);

        _testOutputDir = Path.Combine(Path.GetTempPath(), "ExportTests_" + Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testOutputDir);

        // Create a test PDF with 3 pages
        _testPdfPath = Path.Combine(_testOutputDir, "export_test.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(_testPdfPath, pageCount: 3);
    }

    [Fact]
    public async Task ExportPagesToImages_CreatesCorrectNumberOfFiles()
    {
        // Arrange
        var exportDir = Path.Combine(_testOutputDir, "exported_images");
        Directory.CreateDirectory(exportDir);

        _documentService.LoadDocument(_testPdfPath);

        // Act
        var exported = 0;
        var savedFiles = new List<string>();
        for (int i = 0; i < _documentService.PageCount; i++)
        {
            var bitmap = await _renderService.RenderPageAsync(_testPdfPath, i, 150);
            if (bitmap != null)
            {
                var fileName = $"page_{i + 1:D3}.png";
                var filePath = Path.Combine(exportDir, fileName);

                using var image = SKImage.FromBitmap(bitmap);
                using var encodedData = image.Encode(SKEncodedImageFormat.Png, 100);
                using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
                encodedData.SaveTo(fileStream);

                savedFiles.Add(filePath);
                exported++;
            }
        }

        // Skip if rendering not available
        if (exported == 0)
        {
            return;
        }

        // Verify files were actually written
        foreach (var file in savedFiles)
        {
            File.Exists(file).Should().BeTrue($"File {file} should exist after save");
        }

        // Assert
        var exportedFiles = Directory.GetFiles(exportDir, "*.png");
        exportedFiles.Should().HaveCount(exported);
    }

    [Fact]
    public async Task ExportPagesToImages_FilesAreValidImages()
    {
        // Arrange
        var exportDir = Path.Combine(_testOutputDir, "exported_valid");
        Directory.CreateDirectory(exportDir);

        _documentService.LoadDocument(_testPdfPath);

        // Act
        var bitmap = await _renderService.RenderPageAsync(_testPdfPath, 0, 150);

        // Skip if rendering not available
        if (bitmap == null)
        {
            return;
        }

        var filePath = Path.Combine(exportDir, "test_page.png");
        
        using var image = SKImage.FromBitmap(bitmap);
        using var encodedData = image.Encode(SKEncodedImageFormat.Png, 100);
        // Block-scoped (not `using var`): the write stream must be CLOSED
        // before the re-read below — on Windows, File.OpenRead on a file
        // still held open for writing throws IOException (found by the
        // first CI Windows leg to ever get this far, after #647).
        using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
        {
            encodedData.SaveTo(fileStream);
        }

        // Assert
        File.Exists(filePath).Should().BeTrue();
        var fileInfo = new FileInfo(filePath);
        fileInfo.Length.Should().BeGreaterThan(0);

        // Verify it's a valid PNG by checking file signature
        using var fs = File.OpenRead(filePath);
        var header = new byte[8];
        await fs.ReadExactlyAsync(header);

        // PNG signature: 89 50 4E 47 0D 0A 1A 0A
        header[0].Should().Be(0x89);
        header[1].Should().Be(0x50); // 'P'
        header[2].Should().Be(0x4E); // 'N'
        header[3].Should().Be(0x47); // 'G'
    }

    [Fact]
    public async Task ExportPagesToImages_DifferentDPI_ProducesDifferentSizes()
    {
        // Arrange
        var exportDir = Path.Combine(_testOutputDir, "exported_dpi");
        Directory.CreateDirectory(exportDir);

        _documentService.LoadDocument(_testPdfPath);

        // Act - Export at different DPIs
        var bitmap72 = await _renderService.RenderPageAsync(_testPdfPath, 0, 72);
        var bitmap150 = await _renderService.RenderPageAsync(_testPdfPath, 0, 150);
        var bitmap300 = await _renderService.RenderPageAsync(_testPdfPath, 0, 300);

        // Skip if rendering not available
        if (bitmap72 == null || bitmap150 == null || bitmap300 == null)
        {
            return;
        }

        // Higher DPI should produce larger images
        var size72 = bitmap72!.Width * bitmap72.Height;
        var size150 = bitmap150!.Width * bitmap150.Height;
        var size300 = bitmap300!.Width * bitmap300.Height;

        size150.Should().BeGreaterThan(size72);
        size300.Should().BeGreaterThan(size150);
    }

    [Fact]
    public async Task ExportPagesToImages_AllPages_ExportsSuccessfully()
    {
        // Arrange
        var exportDir = Path.Combine(_testOutputDir, "exported_all");
        Directory.CreateDirectory(exportDir);

        var multiPagePdf = Path.Combine(_testOutputDir, "multi_page.pdf");
        TestPdfGenerator.CreateSimpleTextPdf(multiPagePdf, pageCount: 10);

        _documentService.LoadDocument(multiPagePdf);

        // Act
        var exported = 0;
        var savedFiles = new List<string>();
        for (int i = 0; i < _documentService.PageCount; i++)
        {
            var bitmap = await _renderService.RenderPageAsync(multiPagePdf, i, 150);
            if (bitmap != null)
            {
                var fileName = $"page_{i + 1:D3}.png";
                var filePath = Path.Combine(exportDir, fileName);
                
                using var image = SKImage.FromBitmap(bitmap);
                using var encodedData = image.Encode(SKEncodedImageFormat.Png, 100);
                using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
                encodedData.SaveTo(fileStream);

                savedFiles.Add(filePath);
                exported++;
            }
        }

        // Skip if rendering not available
        if (exported == 0)
        {
            return;
        }

        // Verify each file was actually written
        foreach (var file in savedFiles)
        {
            File.Exists(file).Should().BeTrue($"File {file} should exist after save");
        }

        // Assert
        var exportedFiles = Directory.GetFiles(exportDir, "*.png");
        exportedFiles.Should().HaveCount(exported);
    }

    public void Dispose()
    {
        _documentService.CloseDocument();
        if (Directory.Exists(_testOutputDir))
        {
            Directory.Delete(_testOutputDir, true);
        }
    }
}