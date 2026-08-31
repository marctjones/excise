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
    private readonly Dictionary<(SKTypeface Typeface, int SizeBits, string Text), SKPath?>
        _glyphOutlines = new(GlyphOutlineKeyComparer.Instance);
    private readonly Dictionary<(SKTypeface Typeface, int SizeBits, ushort Gid), SKPath?>
        _glyphOutlinesById = new(GlyphIdOutlineKeyComparer.Instance);
    private readonly Dictionary<(int ObjectNumber, int Generation, int TargetWidth, int TargetHeight), SoftMaskAlpha?>
        _softMasksByReference = new();
    private readonly Dictionary<PdfStream, Dictionary<(int TargetWidth, int TargetHeight), SoftMaskAlpha?>>
        _softMasksByStream = new(ReferenceEqualityComparer.Instance);
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

    public bool TryGetGlyphOutline(
        SKTypeface typeface,
        int sizeBits,
        string text,
        out SKPath? path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _glyphOutlines.TryGetValue((typeface, sizeBits, text), out path);
    }

    public void CacheGlyphOutline(
        SKTypeface typeface,
        int sizeBits,
        string text,
        SKPath? path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _glyphOutlines[(typeface, sizeBits, text)] = path;
    }

    public bool TryGetGlyphOutlineById(
        SKTypeface typeface,
        int sizeBits,
        ushort glyphId,
        out SKPath? path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _glyphOutlinesById.TryGetValue((typeface, sizeBits, glyphId), out path);
    }

    public void CacheGlyphOutlineById(
        SKTypeface typeface,
        int sizeBits,
        ushort glyphId,
        SKPath? path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _glyphOutlinesById[(typeface, sizeBits, glyphId)] = path;
    }

    public bool TryGetSoftMask(
        PdfObject maskObject,
        PdfStream maskStream,
        int targetWidth,
        int targetHeight,
        out SoftMaskAlpha? mask)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (TryGetSoftMaskReferenceKey(maskObject, maskStream, out var referenceKey))
        {
            return _softMasksByReference.TryGetValue(
                (referenceKey.ObjectNumber, referenceKey.Generation, targetWidth, targetHeight),
                out mask);
        }

        if (_softMasksByStream.TryGetValue(maskStream, out var streamCache))
            return streamCache.TryGetValue((targetWidth, targetHeight), out mask);

        mask = null;
        return false;
    }

    public void CacheSoftMask(
        PdfObject maskObject,
        PdfStream maskStream,
        int targetWidth,
        int targetHeight,
        SoftMaskAlpha? mask)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (TryGetSoftMaskReferenceKey(maskObject, maskStream, out var referenceKey))
        {
            _softMasksByReference[
                (referenceKey.ObjectNumber, referenceKey.Generation, targetWidth, targetHeight)] = mask;
            return;
        }

        if (!_softMasksByStream.TryGetValue(maskStream, out var streamCache))
        {
            streamCache = new Dictionary<(int TargetWidth, int TargetHeight), SoftMaskAlpha?>();
            _softMasksByStream[maskStream] = streamCache;
        }

        streamCache[(targetWidth, targetHeight)] = mask;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var path in _glyphOutlines.Values)
            path?.Dispose();
        foreach (var path in _glyphOutlinesById.Values)
            path?.Dispose();
        foreach (var bitmap in _ownedImageBitmaps)
            bitmap.Dispose();

        _glyphOutlines.Clear();
        _glyphOutlinesById.Clear();
        _ownedImageBitmaps.Clear();
        _imageBitmapsByReference.Clear();
        _imageBitmapsByStream.Clear();
        _parsedContentByBytes.Clear();
        _softMasksByReference.Clear();
        _softMasksByStream.Clear();
    }

    private sealed class GlyphOutlineKeyComparer
        : IEqualityComparer<(SKTypeface Typeface, int SizeBits, string Text)>
    {
        public static readonly GlyphOutlineKeyComparer Instance = new();

        public bool Equals(
            (SKTypeface Typeface, int SizeBits, string Text) x,
            (SKTypeface Typeface, int SizeBits, string Text) y)
            => ReferenceEquals(x.Typeface, y.Typeface)
               && x.SizeBits == y.SizeBits
               && string.Equals(x.Text, y.Text, StringComparison.Ordinal);

        public int GetHashCode((SKTypeface Typeface, int SizeBits, string Text) key)
            => HashCode.Combine(
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Typeface),
                key.SizeBits,
                StringComparer.Ordinal.GetHashCode(key.Text));
    }

    private sealed class GlyphIdOutlineKeyComparer
        : IEqualityComparer<(SKTypeface Typeface, int SizeBits, ushort Gid)>
    {
        public static readonly GlyphIdOutlineKeyComparer Instance = new();

        public bool Equals(
            (SKTypeface Typeface, int SizeBits, ushort Gid) x,
            (SKTypeface Typeface, int SizeBits, ushort Gid) y)
            => ReferenceEquals(x.Typeface, y.Typeface)
               && x.SizeBits == y.SizeBits
               && x.Gid == y.Gid;

        public int GetHashCode((SKTypeface Typeface, int SizeBits, ushort Gid) key)
            => HashCode.Combine(
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Typeface),
                key.SizeBits,
                key.Gid);
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

    private static bool TryGetSoftMaskReferenceKey(
        PdfObject maskObject,
        PdfStream maskStream,
        out (int ObjectNumber, int Generation) key)
    {
        if (maskObject is PdfReference reference)
        {
            key = (reference.ObjectNum, reference.Generation);
            return true;
        }

        return TryGetReferenceKey(maskStream, out key);
    }
}

internal sealed record SoftMaskAlpha(byte[] Data, int Width, int Height);

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
