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
    private readonly Dictionary<(PdfDictionary Shading, PdfObject? ColorSpaceSource), GradientColors>
        _gradientColors = new(GradientColorsKeyComparer.Instance);

    // Image and mask streams whose samples THIS render caused to decode, when
    // RenderOptions.ReleaseDecodedImageSamples asked for them to be let go
    // (#1468). Null when it did not, so an unflagged render records nothing.
    // An early release removes the stream, so the set never outlives its use
    // of a stream (#1678).
    private readonly HashSet<PdfStream>? _decodedImageSampleStreams;

    // Every image and mask stream whose samples this render read, decoded or
    // not, when RenderOptions.ImageSampleStreamSink asked for them (#1492).
    // Handed to the sink once, at Dispose. Null when nothing asked.
    private readonly HashSet<PdfStream>? _readImageSampleStreams;
    private readonly ICollection<PdfStream>? _imageSampleStreamSink;
    private bool _disposed;

    public RenderResourceScope(
        bool releaseDecodedImageSamples = false,
        ICollection<PdfStream>? imageSampleStreamSink = null)
    {
        if (releaseDecodedImageSamples)
            _decodedImageSampleStreams = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        if (imageSampleStreamSink != null)
        {
            _imageSampleStreamSink = imageSampleStreamSink;
            _readImageSampleStreams = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        }
    }

    /// <summary>
    /// Call before reading an image or mask stream's samples (#1468). When the
    /// render releases decoded samples, a stream that is not decoded yet is
    /// recorded as this render's to release; one that is already decoded was
    /// decoded by someone else (a viewer band render, an earlier caller) and is
    /// left alone, so this render never makes another renderer decode again.
    /// </summary>
    /// <remarks>
    /// The check is a snapshot: a concurrent renderer can decode the stream
    /// between it and this render's read, and this render will then release
    /// bytes the other one decoded. That costs the other renderer one
    /// re-decode, never a wrong byte — a reader holding the array keeps it,
    /// and a later reader decodes again under the stream's lock.
    /// <para>Independently of that, when the render has an image-sample sink
    /// (#1492) the stream is recorded there WHETHER OR NOT it is decoded: the
    /// sink answers "which samples does this page use", and a stream another
    /// page already decoded is still one this page needs pinned.</para>
    /// </remarks>
    public void NoteImageSampleRead(PdfStream stream)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _readImageSampleStreams?.Add(stream);
        if (_decodedImageSampleStreams != null && !stream.IsDecoded)
            _decodedImageSampleStreams.Add(stream);
    }

    /// <summary>
    /// Releases a recorded stream's decoded samples now rather than at the end
    /// of the render (#1468), for a caller that has just cached what it built
    /// from them. This is what lowers the PEAK of a one-page render: releasing
    /// at dispose only helps a caller that keeps the document open, while
    /// releasing here holds one image's samples at a time instead of every
    /// image on the page. A no-op for a stream this render did not record.
    /// </summary>
    /// <remarks>
    /// The stream also leaves the record here, whether or not there was
    /// anything to release (#1678). Keeping it would root the PdfStream, and
    /// with it the ENCODED bytes, until the render ends: the streamed
    /// subsampled decode (#1677 F2) never writes decoded samples, and the
    /// object store has already forgotten the object (#1207 F3), so this set
    /// was the only thing still holding ~120 MB of encoded image data on a
    /// 75-image page. A later re-read in the same render is re-recorded by
    /// <see cref="NoteImageSampleRead"/>, since a released stream is not
    /// decoded.
    /// </remarks>
    public void ReleaseImageSamplesEarly(PdfStream stream)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_decodedImageSampleStreams?.Remove(stream) == true)
            stream.TryReleaseDecoded();
    }

    /// <summary>
    /// Memoised <c>ResolveGradientColors</c> result (#1404), keyed by the
    /// shading dictionary's REFERENCE identity plus the resource object its
    /// <c>/ColorSpace</c> name resolved through (null when the colour space
    /// does not depend on the resource stack). Reference identity, not
    /// structural equality: two distinct shading objects with identical
    /// content are resolved separately, which is always correct.
    /// </summary>
    public bool TryGetGradientColors(
        PdfDictionary shading,
        PdfObject? colorSpaceSource,
        out GradientColors? colors)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_gradientColors.TryGetValue((shading, colorSpaceSource), out var cached))
        {
            colors = cached;
            return true;
        }

        colors = null;
        return false;
    }

    public void CacheGradientColors(
        PdfDictionary shading,
        PdfObject? colorSpaceSource,
        GradientColors colors)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gradientColors[(shading, colorSpaceSource)] = colors;
    }

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

        // Before the release below: the sink reports what the render READ,
        // which is independent of whether this render then let it go.
        if (_readImageSampleStreams != null)
        {
            foreach (var stream in _readImageSampleStreams)
                _imageSampleStreamSink!.Add(stream);
            _readImageSampleStreams.Clear();
        }

        // Catches every recorded stream the early release did not reach (an
        // uncached stencil or explicit-mask read, or a later re-read of one
        // already released). A stream whose bytes were rewritten meanwhile
        // refuses the release by itself (PdfStream.TryReleaseDecoded).
        if (_decodedImageSampleStreams != null)
        {
            foreach (var stream in _decodedImageSampleStreams)
                stream.TryReleaseDecoded();
            _decodedImageSampleStreams.Clear();
        }

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
        _gradientColors.Clear();
    }

    private sealed class GradientColorsKeyComparer
        : IEqualityComparer<(PdfDictionary Shading, PdfObject? ColorSpaceSource)>
    {
        public static readonly GradientColorsKeyComparer Instance = new();

        public bool Equals(
            (PdfDictionary Shading, PdfObject? ColorSpaceSource) x,
            (PdfDictionary Shading, PdfObject? ColorSpaceSource) y)
            => ReferenceEquals(x.Shading, y.Shading)
               && ReferenceEquals(x.ColorSpaceSource, y.ColorSpaceSource);

        public int GetHashCode((PdfDictionary Shading, PdfObject? ColorSpaceSource) key)
            => HashCode.Combine(
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Shading),
                key.ColorSpaceSource is null
                    ? 0
                    : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.ColorSpaceSource));
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

/// <summary>
/// A resolved axial/radial gradient (#1404). The arrays are shared between
/// every `sh` that hits the memo; callers only hand them to
/// <c>SKShader.Create*</c>, which copies, and must never mutate them.
/// </summary>
internal sealed record GradientColors(
    SKColor Start,
    SKColor End,
    SKColor[]? Stops,
    float[]? Positions);

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
