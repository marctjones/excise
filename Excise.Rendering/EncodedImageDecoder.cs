using SkiaSharp;

namespace Excise.Rendering;

/// <summary>
/// Context-free decoding of an encoded raster payload. PDF filter selection,
/// color policy, masks, caching, diagnostics, and canvas placement remain with
/// the render execution layer.
/// </summary>
internal static class EncodedImageDecoder
{
    public static SKBitmap? Decode(EncodedImageDecodeRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        var bytes = request.Bytes;
        if (bytes == null || bytes.Length == 0)
            return null;

        try
        {
            if (request.PreferredSize is { Width: > 0, Height: > 0 } size)
            {
                var scaled = SKBitmap.Decode(
                    bytes,
                    new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
                if (scaled != null)
                    return ObserveCancellation(scaled, request.CancellationToken);
            }

            return ObserveCancellation(SKBitmap.Decode(bytes), request.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Unsupported codecs and malformed/truncated image payloads are
            // refused by returning null. The caller owns the PDF-specific
            // diagnostic and no-draw policy.
            return null;
        }
    }

    private static SKBitmap? ObserveCancellation(SKBitmap? bitmap, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
            return bitmap;

        bitmap?.Dispose();
        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }
}

internal readonly record struct EncodedImageDecodeRequest(
    byte[]? Bytes,
    SKSizeI? PreferredSize = null,
    CancellationToken CancellationToken = default);
