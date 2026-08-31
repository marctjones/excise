using Excise.Core.Document;
using Excise.Rendering;
using SkiaSharp;

namespace Excise.Cli.Commands;

internal static class RenderPageHandler
{
    internal static RenderPageResult Execute(
        RenderPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        cancellationToken.ThrowIfCancellationRequested();

        var input = new FileInfo(request.InputPath);
        if (!input.Exists)
            throw new FileNotFoundException("The PDF input file does not exist.", input.FullName);

        using var document = string.IsNullOrEmpty(request.Password)
            ? PdfDocument.Open(input.FullName)
            : PdfDocument.Open(input.FullName, request.Password);
        cancellationToken.ThrowIfCancellationRequested();

        DocumentPermissionGuard.Require(
            document,
            DocumentAction.Extract,
            "page image export (render)",
            request.IgnorePermissions,
            overrideHint: request.OverrideHint);

        if (request.PageNumber < 1 || request.PageNumber > document.PageCount)
            throw new DocumentPageOutOfRangeException(request.PageNumber, document.PageCount);

        var outputPath = Path.GetFullPath(request.OutputPath);
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        var renderer = new SkiaRenderer();
        var options = new RenderOptions { Dpi = request.Dpi };
        using var bitmap = renderer.RenderPage(
            document.GetPage(request.PageNumber),
            options,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        data.SaveTo(stream);

        return new RenderPageResult(
            input.FullName,
            outputPath,
            request.PageNumber,
            request.Dpi,
            bitmap.Width,
            bitmap.Height);
    }
}

internal readonly record struct RenderPageRequest(
    string InputPath,
    string OutputPath,
    string? Password,
    int PageNumber,
    int Dpi,
    bool IgnorePermissions,
    string OverrideHint = "--ignore-permissions");

internal sealed record RenderPageResult(
    string InputPath,
    string OutputPath,
    int PageNumber,
    int Dpi,
    int Width,
    int Height);
