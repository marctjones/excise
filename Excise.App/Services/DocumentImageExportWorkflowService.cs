using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Excise.App.Services;

/// <summary>
/// Owns the render/encode/write transaction for image exports. The desktop
/// shell remains responsible for permission checks and destination pickers.
/// </summary>
internal sealed class DocumentImageExportWorkflowService
{
    private readonly IPageImageRenderer _renderer;
    private readonly ILogger<DocumentImageExportWorkflowService> _logger;

    public DocumentImageExportWorkflowService(
        IPageImageRenderer renderer,
        ILogger<DocumentImageExportWorkflowService> logger)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PageImageExportResult> ExportPageAsync(
        PageImageExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request.Document);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        ArgumentOutOfRangeException.ThrowIfNegative(request.PageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            request.PageIndex,
            request.Document.PageCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Dpi);

        using var bitmap = await _renderer.RenderPageAsync(
            request.Document,
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
        ArgumentNullException.ThrowIfNull(request.Document);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputFolder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Dpi);

        var format = ImageExportFormat.Parse(request.Format);
        var pageCount = request.Document.PageCount;
        var writtenPaths = new List<string>(pageCount);
        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outputPath = Path.Combine(
                request.OutputFolder,
                $"page_{pageIndex + 1:D3}.{format.Extension}");
            var result = await ExportPageAsync(
                new PageImageExportRequest(
                    request.Document,
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
            pageCount,
            request.OutputFolder);
        return new DocumentImageExportResult(pageCount, writtenPaths);
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
    Excise.Core.Document.PdfDocument Document,
    int PageIndex,
    string OutputPath,
    int Dpi);

internal sealed record PageImageExportResult(bool WasWritten, string OutputPath)
{
    public static PageImageExportResult Written(string outputPath) => new(true, outputPath);
    public static PageImageExportResult NotRendered(string outputPath) => new(false, outputPath);
}

internal readonly record struct DocumentImageExportRequest(
    Excise.Core.Document.PdfDocument Document,
    string OutputFolder,
    string Format,
    int Dpi);

internal sealed record DocumentImageExportResult(
    int RequestedPageCount,
    IReadOnlyList<string> WrittenPaths)
{
    public int SkippedPageCount => RequestedPageCount - WrittenPaths.Count;
}
