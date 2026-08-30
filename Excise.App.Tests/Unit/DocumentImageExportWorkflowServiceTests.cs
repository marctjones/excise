using AwesomeAssertions;
using Excise.App.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace Excise.App.Tests.Unit;

public sealed class DocumentImageExportWorkflowServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"excise-image-export-workflow-{Guid.NewGuid():N}");
    private readonly string _sourcePath;

    public DocumentImageExportWorkflowServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
        _sourcePath = Path.Combine(_tempDir, "source.pdf");
        File.WriteAllBytes(_sourcePath, new byte[] { 1 });
    }

    [Fact]
    public async Task ExportPageAsync_EncodesAccordingToOutputExtension()
    {
        var outputPath = Path.Combine(_tempDir, "page.jpg");
        var workflow = CreateWorkflow(_ => CreateBitmap());

        var result = await workflow.ExportPageAsync(
            new PageImageExportRequest(_sourcePath, 0, outputPath, 150));

        result.WasWritten.Should().BeTrue();
        result.OutputPath.Should().Be(outputPath);
        var bytes = File.ReadAllBytes(outputPath);
        bytes.Should().HaveCountGreaterThan(2);
        bytes[0].Should().Be(0xff);
        bytes[1].Should().Be(0xd8);
    }

    [Fact]
    public async Task ExportPagesAsync_ReturnsImmutableWrittenPathOutcome()
    {
        var workflow = CreateWorkflow(_ => CreateBitmap());

        var result = await workflow.ExportPagesAsync(
            new DocumentImageExportRequest(_sourcePath, 3, _tempDir, "png", 72));

        result.RequestedPageCount.Should().Be(3);
        result.SkippedPageCount.Should().Be(0);
        result.WrittenPaths.Select(Path.GetFileName).Should().Equal(
            "page_001.png",
            "page_002.png",
            "page_003.png");
        result.WrittenPaths.Should().OnlyContain(path => File.Exists(path));
    }

    [Fact]
    public async Task ExportPagesAsync_ReportsPagesThatCouldNotBeRendered()
    {
        var workflow = CreateWorkflow(pageIndex => pageIndex == 1 ? null : CreateBitmap());

        var result = await workflow.ExportPagesAsync(
            new DocumentImageExportRequest(_sourcePath, 3, _tempDir, "png", 72));

        result.WrittenPaths.Should().HaveCount(2);
        result.SkippedPageCount.Should().Be(1);
        File.Exists(Path.Combine(_tempDir, "page_002.png")).Should().BeFalse();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private static DocumentImageExportWorkflowService CreateWorkflow(
        Func<int, SKBitmap?> render) =>
        new(
            new StubRenderService(render),
            NullLogger<DocumentImageExportWorkflowService>.Instance);

    private static SKBitmap CreateBitmap()
    {
        var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.CornflowerBlue);
        return bitmap;
    }

    private sealed class StubRenderService(Func<int, SKBitmap?> render)
        : PdfRenderService(NullLogger<PdfRenderService>.Instance)
    {
        public override Task<SKBitmap?> RenderPageAsync(
            string pdfPath,
            int pageIndex,
            int dpi = 150,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(render(pageIndex));
    }
}
