using System.Buffers.Binary;
using Excise.Core.Document;
using Excise.Rendering;

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

        // RenderPageToPng is the library's own render-to-PNG entry point; this
        // handler used to re-implement its three lines with SKImage/Encode and
        // was its only production-adjacent caller that did not call it — the
        // "implemented, tested, zero callers" shape check-unwired-api.sh exists
        // to catch (#1358).
        var renderer = new SkiaRenderer();
        var options = new RenderOptions { Dpi = request.Dpi };
        using var png = new MemoryStream();
        renderer.RenderPageToPng(document.GetPage(request.PageNumber), png, options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // The bitmap is disposed inside RenderPageToPng; report the dimensions
        // of what was actually written. PNG IHDR: width then height, big-endian
        // int32 at byte offsets 16 and 20 (8-byte signature + 4 length + 4 type).
        if (png.Length < 24)
            throw new InvalidOperationException("Renderer produced no PNG data.");
        var header = png.GetBuffer().AsSpan(0, 24);
        var width = BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));

        using var stream = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        png.Position = 0;
        png.CopyTo(stream);

        return new RenderPageResult(
            input.FullName,
            outputPath,
            request.PageNumber,
            request.Dpi,
            width,
            height);
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
