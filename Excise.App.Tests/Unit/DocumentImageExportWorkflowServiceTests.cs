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

    [Fact]
    public async Task ExportPageAsync_PngDecodesToTheSameRgbaAsTheDefaultEncoder()
    {
        // #1471: the PNG export path uses Sub filter + zlib 6. Encoded bytes
        // change; decoded RGBA must not. Opaque pixels must round-trip exactly;
        // partially transparent ones must decode as they did under the
        // previous default encoder (premul -> PNG unpremul is not bit-exact
        // under either encoder, so that is the right comparison for them).
        var outputPath = Path.Combine(_tempDir, "roundtrip.png");
        using var source = new SKBitmap(64, 48, SKColorType.Rgba8888, SKAlphaType.Premul);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var alpha = y < source.Height / 2 ? (byte)255 : (byte)((x * 11 + y * 5) % 256);
                source.SetPixel(x, y, new SKColor((byte)(x * 4), (byte)(y * 5), (byte)((x ^ y) * 3), alpha));
            }
        }

        var workflow = CreateWorkflow(_ => source.Copy());
        var result = await workflow.ExportPageAsync(
            new PageImageExportRequest(_document, 0, outputPath, 150));
        result.WasWritten.Should().BeTrue();

        var unpremul = new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var exported = SKBitmap.Decode(outputPath, unpremul);
        using var sourceImage = SKImage.FromBitmap(source);
        using var defaultPng = sourceImage.Encode(SKEncodedImageFormat.Png, 90);
        using var defaultDecoded = SKBitmap.Decode(defaultPng, unpremul);

        exported.Should().NotBeNull();
        defaultDecoded.Should().NotBeNull();
        exported!.GetPixelSpan().ToArray().Should().Equal(defaultDecoded!.GetPixelSpan().ToArray());

        var opaqueBytes = source.RowBytes * (source.Height / 2);
        exported.GetPixelSpan().Slice(0, opaqueBytes).ToArray().Should().Equal(
            source.GetPixelSpan().Slice(0, opaqueBytes).ToArray(),
            "opaque rows must decode to exactly the source pixels");
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
