using Excise.Core.Primitives;
using Excise.Core.Content;
using SkiaSharp;

namespace Excise.Rendering;

/// <summary>
/// Owns reusable native resources for one top-level page render. Nested render
/// contexts borrow this scope; the public renderer remains its sole lifetime
/// owner, so tearing down a form, mask, or pattern context cannot invalidate a
/// sibling context.
/// </summary>
internal sealed class RenderResourceScope : IDisposable
{
    private readonly Dictionary<(int ObjectNumber, int Generation, ImageBitmapCacheKey Key), SKBitmap?>
        _imageBitmapsByReference = new();
    private readonly Dictionary<PdfStream, Dictionary<ImageBitmapCacheKey, SKBitmap?>>
        _imageBitmapsByStream = new(ReferenceEqualityComparer.Instance);
    private readonly List<SKBitmap> _ownedImageBitmaps = new();
    private readonly Dictionary<byte[], ContentStream> _parsedContentByBytes =
        new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    public bool TryGetDecodedImage(
        PdfStream imageStream,
        ImageBitmapCacheKey key,
        out SKBitmap? bitmap)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (TryGetReferenceKey(imageStream, out var referenceKey))
        {
            return _imageBitmapsByReference.TryGetValue(
                (referenceKey.ObjectNumber, referenceKey.Generation, key),
                out bitmap);
        }

        if (_imageBitmapsByStream.TryGetValue(imageStream, out var streamCache))
            return streamCache.TryGetValue(key, out bitmap);

        bitmap = null;
        return false;
    }

    public void CacheDecodedImage(
        PdfStream imageStream,
        ImageBitmapCacheKey key,
        SKBitmap? bitmap)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (bitmap != null)
            _ownedImageBitmaps.Add(bitmap);

        if (TryGetReferenceKey(imageStream, out var referenceKey))
        {
            _imageBitmapsByReference[(referenceKey.ObjectNumber, referenceKey.Generation, key)] = bitmap;
            return;
        }

        if (!_imageBitmapsByStream.TryGetValue(imageStream, out var streamCache))
        {
            streamCache = new Dictionary<ImageBitmapCacheKey, SKBitmap?>();
            _imageBitmapsByStream[imageStream] = streamCache;
        }

        streamCache[key] = bitmap;
    }

    public bool TryGetParsedContent(byte[] contentBytes, out ContentStream? content)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _parsedContentByBytes.TryGetValue(contentBytes, out content);
    }

    public void CacheParsedContent(byte[] contentBytes, ContentStream content)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _parsedContentByBytes[contentBytes] = content;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var bitmap in _ownedImageBitmaps)
            bitmap.Dispose();

        _ownedImageBitmaps.Clear();
        _imageBitmapsByReference.Clear();
        _imageBitmapsByStream.Clear();
        _parsedContentByBytes.Clear();
    }

    private static bool TryGetReferenceKey(
        PdfStream imageStream,
        out (int ObjectNumber, int Generation) key)
    {
        if (imageStream.ObjectNumber.HasValue)
        {
            key = (imageStream.ObjectNumber.Value, imageStream.GenerationNumber ?? 0);
            return true;
        }

        key = default;
        return false;
    }
}

internal readonly record struct ImageBitmapCacheKey(
    int Width,
    int Height,
    int BitsPerComponent,
    string ColorSpace,
    int TargetWidth,
    int TargetHeight,
    bool ImageMask,
    byte FillRed,
    byte FillGreen,
    byte FillBlue,
    byte FillAlpha,
    int? DctColorTransform);
