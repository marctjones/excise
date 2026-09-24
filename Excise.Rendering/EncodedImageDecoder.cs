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
                // A JPEG decodes at reduced size for free, but only at the sizes the codec
                // supports (1/2, 1/4, 1/8). Asking SKBitmap.Decode for an ARBITRARY size
                // returns null for a JPEG, and the fall-through below then decodes the whole
                // image: a 2480x2630 soft-masked plate cost 3.4 s and 376 MB to draw at 36 dpi
                // (#1821). Take the deepest supported reduction that still covers the target.
                var reduced = DecodeReducedScale(bytes, size);
                if (reduced != null)
                    return ObserveCancellation(reduced, request.CancellationToken);

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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unsupported codecs and malformed/truncated image payloads are
            // refused by returning null. The caller owns the PDF-specific
            // diagnostic and no-draw policy.
            return null;
        }
    }

    /// <summary>
    /// Decode <paramref name="bytes"/> at the deepest reduction the codec supports whose
    /// dimensions still cover <paramref name="target"/> (so a later downscale, never an
    /// upscale, reaches the target). Null when the codec cannot reduce, or no reduction
    /// would still cover the target; the caller then decodes as before.
    /// </summary>
    internal static SKBitmap? DecodeReducedScale(byte[] bytes, SKSizeI target)
    {
        using var stream = new SKMemoryStream(bytes);
        using var codec = SKCodec.Create(stream);
        if (codec == null)
            return null;

        var full = codec.Info;
        if (target.Width >= full.Width || target.Height >= full.Height)
            return null;

        SKSizeI? chosen = null;
        foreach (var denominator in new[] { 8, 4, 2 })
        {
            var dims = codec.GetScaledDimensions(1f / denominator);
            if (dims.Width >= target.Width && dims.Height >= target.Height &&
                dims.Width < full.Width && dims.Height < full.Height)
            {
                chosen = dims;
                break;
            }
        }

        if (chosen is not { } size)
            return null;

        var info = new SKImageInfo(
            size.Width,
            size.Height,
            SKImageInfo.PlatformColorType,
            full.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul);
        return SKBitmap.Decode(codec, info);
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
