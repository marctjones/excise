using AwesomeAssertions;
using Excise.App.Services;
using Excise.Core.Document;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace Excise.App.Tests.Unit;

public sealed class DocumentImageExportWorkflowServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"excise-image-export-workflow-{Guid.NewGuid():N}");
    private readonly PdfDocument _document;

    public DocumentImageExportWorkflowServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
        var sourcePath = Path.Combine(_tempDir, "source.pdf");
        TestPdfGenerator.CreateMultiPagePdf(sourcePath, pageCount: 3);
        _document = PdfDocument.Open(File.ReadAllBytes(sourcePath));
    }

    [Fact]
    public async Task ExportPageAsync_EncodesAccordingToOutputExtension()
    {
        var outputPath = Path.Combine(_tempDir, "page.jpg");
        var workflow = CreateWorkflow(_ => CreateBitmap());

        var result = await workflow.ExportPageAsync(
            new PageImageExportRequest(_document, 0, outputPath, 150));

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
            new DocumentImageExportRequest(_document, _tempDir, "png", 72));

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
            new DocumentImageExportRequest(_document, _tempDir, "png", 72));

        result.WrittenPaths.Should().HaveCount(2);
        result.SkippedPageCount.Should().Be(1);
        File.Exists(Path.Combine(_tempDir, "page_002.png")).Should().BeFalse();
    }

    [Fact]
    public async Task PageImageRenderer_RendersLiveRotationAtRequestedDpi()
    {
        var page = _document.GetPage(1);
        page.Rotation = 90;
        var renderer = new PageImageRenderer();

        using var bitmap72 = await renderer.RenderPageAsync(_document, 0, 72);
        using var bitmap150 = await renderer.RenderPageAsync(_document, 0, 150);

        bitmap72.Should().NotBeNull();
        bitmap72!.Width.Should().Be(792);
        bitmap72.Height.Should().Be(612);
        bitmap150.Should().NotBeNull();
        bitmap150!.Width.Should().BeGreaterThan(bitmap72.Width);
        bitmap150.Height.Should().BeGreaterThan(bitmap72.Height);
    }

    [Fact]
    public async Task PageImageRenderer_PreCanceledRequestDoesNotRender()
    {
        var renderer = new PageImageRenderer();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => renderer.RenderPageAsync(
            _document,
            0,
            72,
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    public void Dispose()
    {
        _document.Dispose();
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
            new StubPageImageRenderer(render),
            NullLogger<DocumentImageExportWorkflowService>.Instance);

    private static SKBitmap CreateBitmap()
    {
        var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.CornflowerBlue);
        return bitmap;
    }

    private sealed class StubPageImageRenderer(Func<int, SKBitmap?> render) : IPageImageRenderer
    {
        public Task<SKBitmap?> RenderPageAsync(
            PdfDocument document,
            int pageIndex,
            int dpi,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(render(pageIndex));
        }
    }
}
