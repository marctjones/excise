using System.Buffers.Binary;
using System.Diagnostics;
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

        // #1387 — phase timings. The reference-performance bench could previously
        // only see whole-process wall clock, and ~71 ms of that is .NET startup and JIT
        // on this machine against mutool's 3 ms. On a typical 150 ms page that is 40% of
        // the measurement, so a real 30 ms win in the raster path moved the bench by
        // under 20% and read as noise. Splitting open/render/write lets the bench compare
        // engines rather than runtimes; process wall clock is still reported beside it,
        // because a user running the CLI genuinely pays the startup.
        var openWatch = Stopwatch.StartNew();
        using var document = string.IsNullOrEmpty(request.Password)
            ? PdfDocument.Open(input.FullName)
            : PdfDocument.Open(input.FullName, request.Password);
        openWatch.Stop();
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
        var renderWatch = Stopwatch.StartNew();
        renderer.RenderPageToPng(document.GetPage(request.PageNumber), png, options, cancellationToken);
        renderWatch.Stop();
        cancellationToken.ThrowIfCancellationRequested();

        // The bitmap is disposed inside RenderPageToPng; report the dimensions
        // of what was actually written. PNG IHDR: width then height, big-endian
        // int32 at byte offsets 16 and 20 (8-byte signature + 4 length + 4 type).
        if (png.Length < 24)
            throw new InvalidOperationException("Renderer produced no PNG data.");
        var header = png.GetBuffer().AsSpan(0, 24);
        var width = BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));

        var writeWatch = Stopwatch.StartNew();
        using (var stream = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            png.Position = 0;
            png.CopyTo(stream);
        }
        writeWatch.Stop();

        return new RenderPageResult(
            input.FullName,
            outputPath,
            request.PageNumber,
            request.Dpi,
            width,
            height,
            openWatch.Elapsed.TotalMilliseconds,
            renderWatch.Elapsed.TotalMilliseconds,
            writeWatch.Elapsed.TotalMilliseconds);
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

/// <summary>
/// <paramref name="OpenMs"/>, <paramref name="RenderMs"/> and <paramref name="WriteMs"/>
/// are the in-process phase timings (#1387). They deliberately exclude process startup
/// and JIT, which the caller can obtain by subtracting their sum from the wall clock.
/// </summary>
internal sealed record RenderPageResult(
    string InputPath,
    string OutputPath,
    int PageNumber,
    int Dpi,
    int Width,
    int Height,
    double OpenMs = 0,
    double RenderMs = 0,
    double WriteMs = 0);
