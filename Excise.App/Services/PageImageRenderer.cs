using Excise.Core.Document;
using Excise.Rendering;
using SkiaSharp;

namespace Excise.App.Services;

/// <summary>
/// Narrow raster boundary used by page-image export. It deliberately has no
/// cache: export renders each requested page once, while interactive bitmap
/// retention belongs to <c>PdfViewerControl</c> and thumbnail retention belongs
/// to <see cref="ThumbnailCacheService"/>.
/// </summary>
internal interface IPageImageRenderer
{
    Task<SKBitmap?> RenderPageAsync(
        PdfDocument document,
        int pageIndex,
        int dpi,
        CancellationToken cancellationToken = default);
}

internal sealed class PageImageRenderer : IPageImageRenderer
{
    private readonly SkiaRenderer _renderer = new();

    public Task<SKBitmap?> RenderPageAsync(
        PdfDocument document,
        int pageIndex,
        int dpi,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, document.PageCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpi);
        cancellationToken.ThrowIfCancellationRequested();

        var page = document.GetPage(pageIndex + 1);
        return Task.Run<SKBitmap?>(
            () => _renderer.RenderPage(
                page,
                new RenderOptions { Dpi = dpi },
                cancellationToken),
            cancellationToken);
    }
}
