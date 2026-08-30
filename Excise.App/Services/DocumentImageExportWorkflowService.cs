using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Excise.App.Services;

/// <summary>
/// Owns the render/encode/write transaction for image exports. The desktop
/// shell remains responsible for permission checks and destination pickers.
/// </summary>
internal sealed class DocumentImageExportWorkflowService
{
    private readonly PdfRenderService _renderService;
    private readonly ILogger<DocumentImageExportWorkflowService> _logger;

    public DocumentImageExportWorkflowService(
        PdfRenderService renderService,
        ILogger<DocumentImageExportWorkflowService> logger)
    {
        _renderService = renderService ?? throw new ArgumentNullException(nameof(renderService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PageImageExportResult> ExportPageAsync(
        PageImageExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateSource(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        ArgumentOutOfRangeException.ThrowIfNegative(request.PageIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Dpi);

        using var bitmap = await _renderService.RenderPageAsync(
            request.SourcePath,
            request.PageIndex,
            request.Dpi,
            cancellationToken);
        if (bitmap is null)
            return PageImageExportResult.NotRendered(request.OutputPath);

        var format = ImageExportFormat.FromPath(request.OutputPath);
        WriteBitmap(bitmap, request.OutputPath, format);
        _logger.LogInformation(
            "Exported page {PageNumber} to {OutputPath}",
            request.PageIndex + 1,
            request.OutputPath);
        return PageImageExportResult.Written(request.OutputPath);
    }

    public async Task<DocumentImageExportResult> ExportPagesAsync(
        DocumentImageExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateSource(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputFolder);
        ArgumentOutOfRangeException.ThrowIfNegative(request.PageCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Dpi);

        var format = ImageExportFormat.Parse(request.Format);
        var writtenPaths = new List<string>(request.PageCount);
        for (var pageIndex = 0; pageIndex < request.PageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outputPath = Path.Combine(
                request.OutputFolder,
                $"page_{pageIndex + 1:D3}.{format.Extension}");
            var result = await ExportPageAsync(
                new PageImageExportRequest(
                    request.SourcePath,
                    pageIndex,
                    outputPath,
                    request.Dpi),
                cancellationToken);
            if (result.WasWritten)
                writtenPaths.Add(result.OutputPath);
        }

        _logger.LogInformation(
            "Exported {WrittenCount}/{PageCount} pages to {OutputFolder}",
            writtenPaths.Count,
            request.PageCount,
            request.OutputFolder);
        return new DocumentImageExportResult(request.PageCount, writtenPaths);
    }

    private static void ValidateSource(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The PDF source file does not exist.", sourcePath);
    }

    private static void WriteBitmap(SKBitmap bitmap, string outputPath, ImageExportFormat format)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encodedData = image.Encode(format.SkiaFormat, quality: 90)
            ?? throw new InvalidOperationException($"Could not encode image as {format.Extension}.");
        using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        encodedData.SaveTo(fileStream);
    }

    private readonly record struct ImageExportFormat(
        string Extension,
        SKEncodedImageFormat SkiaFormat)
    {
        public static ImageExportFormat FromPath(string path) =>
            Parse(Path.GetExtension(path));

        public static ImageExportFormat Parse(string value) =>
            value.Trim().TrimStart('.').ToLowerInvariant() switch
            {
                "jpg" or "jpeg" => new("jpg", SKEncodedImageFormat.Jpeg),
                "png" => new("png", SKEncodedImageFormat.Png),
                _ => throw new ArgumentException(
                    "Image export format must be png, jpg, or jpeg.",
                    nameof(value))
            };
    }
}

internal readonly record struct PageImageExportRequest(
    string SourcePath,
    int PageIndex,
    string OutputPath,
    int Dpi);

internal sealed record PageImageExportResult(bool WasWritten, string OutputPath)
{
    public static PageImageExportResult Written(string outputPath) => new(true, outputPath);
    public static PageImageExportResult NotRendered(string outputPath) => new(false, outputPath);
}

internal readonly record struct DocumentImageExportRequest(
    string SourcePath,
    int PageCount,
    string OutputFolder,
    string Format,
    int Dpi);

internal sealed record DocumentImageExportResult(
    int RequestedPageCount,
    IReadOnlyList<string> WrittenPaths)
{
    public int SkippedPageCount => RequestedPageCount - WrittenPaths.Count;
}
