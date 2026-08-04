using System.Globalization;
using System.Text;
using System.Threading;
using BitMiracle.LibJpeg.Classic;
using Excise.Core.ColorSpaces;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Filters.Jpx;
using Excise.Core.Primitives;
using Excise.Core.Text;
using Excise.Rendering.Fonts;
using Excise.Rendering.Transparency;
using SkiaSharp;
using CoreCffParser = Excise.Core.Fonts.CffParser;

namespace Excise.Rendering;

/// <summary>
/// Renders PDF pages to SkiaSharp bitmaps.
/// </summary>
public class SkiaRenderer
{
    /// <summary>
    /// Render a PDF page to a bitmap with default options (150 DPI).
    /// </summary>
    public SKBitmap RenderPage(PdfPage page)
    {
        return RenderPage(page, new RenderOptions());
    }

    /// <summary>
    /// Render a PDF page to a bitmap with specified options.
    /// </summary>
    public SKBitmap RenderPage(PdfPage page, RenderOptions options)
        => RenderPage(page, options, CancellationToken.None);

    /// <summary>
    /// Render a PDF page to a bitmap, observing a <see cref="CancellationToken"/>.
    /// The token is checked between content-stream operators, so a long render of
    /// a complex or hostile page can be abandoned promptly (companion to the
    /// cancellable parsing added in #346). Throws <see cref="OperationCanceledException"/>
    /// if cancellation is requested.
    /// </summary>
    public SKBitmap RenderPage(PdfPage page, RenderOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scale = options.Dpi / 72.0;
        var displayBox = ResolveEffectiveRenderBox(page);
        float s = (float)scale;
        float L = (float)displayBox.Left;
        float B = (float)displayBox.Bottom;
        float R = (float)displayBox.Right;
        float T = (float)displayBox.Top;

        // The page /Rotate entry rotates the page clockwise when displayed.
        // The output bitmap is in *visual* dimensions (W/H swap for 90/270).
        int rot = page.Rotation;   // already canonical {0,90,180,270}
        bool quarter = rot is 90 or 270;
        var fullWidth = CeilingPixelCount((quarter ? displayBox.Height : displayBox.Width) * scale);
        var fullHeight = CeilingPixelCount((quarter ? displayBox.Width : displayBox.Height) * scale);
        if (fullWidth <= 0 || fullHeight <= 0)
            throw new InvalidPageGeometryException(
                $"Page resolves to an invalid bitmap size: {fullWidth} x {fullHeight} pixels.");

        // Map content space (PDF: bottom-left origin, Y up) to device pixels
        // (top-left origin, Y down) of the visible CropBox bitmap, applying /Rotate.
        // The 0° case is the classic scale+flip+translate with the CropBox
        // origin subtracted. SKMatrix args are
        // (scaleX, skewX, transX, skewY, scaleY, transY, persp0, persp1, persp2)
        // where px = scaleX*cx + skewX*cy + transX, py = skewY*cx + scaleY*cy + transY.
        SKMatrix m = rot switch
        {
            90  => new SKMatrix(0, s, -s * B,   s, 0, -s * L,     0, 0, 1),
            180 => new SKMatrix(-s, 0, s * R,   0, s, -s * B,    0, 0, 1),
            270 => new SKMatrix(0, -s, s * T,   -s, 0, s * R,  0, 0, 1),
            _   => new SKMatrix(s, 0, -s * L,   0, -s, s * T,   0, 0, 1),
        };

        var deviceBounds = options.ClipRect.HasValue
            ? TransformBounds(m, options.ClipRect.Value)
            : new SKRect(0, 0, fullWidth, fullHeight);
        deviceBounds.Intersect(new SKRect(0, 0, fullWidth, fullHeight));

        var width = (int)Math.Ceiling(deviceBounds.Width);
        var height = (int)Math.Ceiling(deviceBounds.Height);
        if (width <= 0 || height <= 0)
            throw new InvalidPageGeometryException(
                $"Page clip resolves to an invalid bitmap size: {width} x {height} pixels.");

        var pixelCount = (long)width * height;
        if (pixelCount > options.MaxPixelCount)
            throw new RenderResourceLimitException(
                $"Page render would allocate {width} x {height} pixels ({pixelCount:N0}), " +
                $"exceeding the configured limit of {options.MaxPixelCount:N0} pixels.");

        // Create bitmap
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);

        // Fill background
        canvas.Clear(options.BackgroundColor);

        if (options.ClipRect.HasValue)
        {
            m.TransX -= deviceBounds.Left;
            m.TransY -= deviceBounds.Top;
        }
        canvas.SetMatrix(m);
        if (options.ClipRect.HasValue)
            canvas.ClipRect(options.ClipRect.Value, SKClipOperation.Intersect, options.AntiAlias);

        // Render content
        var context = new RenderContext(
            canvas,
            page,
            options,
            cancellationToken,
            bitmap,
            IsDeviceCmykTransparencyGroup(page.Dictionary.GetOptional("Group"), page.Document));
        context.Render();

        return bitmap;
    }

    private static SKRect TransformBounds(SKMatrix matrix, SKRect rect)
    {
        var p1 = matrix.MapPoint(new SKPoint(rect.Left, rect.Top));
        var p2 = matrix.MapPoint(new SKPoint(rect.Right, rect.Top));
        var p3 = matrix.MapPoint(new SKPoint(rect.Right, rect.Bottom));
        var p4 = matrix.MapPoint(new SKPoint(rect.Left, rect.Bottom));

        var left = MathF.Min(MathF.Min(p1.X, p2.X), MathF.Min(p3.X, p4.X));
        var top = MathF.Min(MathF.Min(p1.Y, p2.Y), MathF.Min(p3.Y, p4.Y));
        var right = MathF.Max(MathF.Max(p1.X, p2.X), MathF.Max(p3.X, p4.X));
        var bottom = MathF.Max(MathF.Max(p1.Y, p2.Y), MathF.Max(p3.Y, p4.Y));
        return new SKRect(left, top, right, bottom);
    }

    internal static bool IsDeviceCmykTransparencyGroup(PdfObject? groupObject, PdfDocument document)
    {
        if (groupObject == null)
            return false;

        if (document.Resolve(groupObject) is not PdfDictionary group)
            return false;

        if (!string.Equals(group.GetNameOrNull("S"), "Transparency", StringComparison.Ordinal))
            return false;

        var colorSpaceObject = group.GetOptional("CS");
        if (colorSpaceObject == null)
            return false;

        var resolvedColorSpace = document.Resolve(colorSpaceObject);
        if (resolvedColorSpace is PdfName name)
            return string.Equals(name.Value, "DeviceCMYK", StringComparison.Ordinal);

        try
        {
            var colorSpace = PdfColorSpace.Parse(colorSpaceObject, document);
            return colorSpace.Type == PdfColorSpaceType.DeviceCMYK;
        }
        catch
        {
            return false;
        }
    }

    public static PdfRectangle ResolveEffectiveRenderBox(PdfPage page)
    {
        var mediaBox = page.MediaBox.Normalize();
        var cropBox = page.CropBox.Normalize();

        if (HasPositiveArea(mediaBox))
        {
            if (!HasPositiveArea(cropBox))
                return mediaBox;

            var visibleMediaCrop = Intersect(mediaBox, cropBox);
            return HasPositiveArea(visibleMediaCrop)
                ? visibleMediaCrop
                : mediaBox;
        }

        if (HasPositiveArea(cropBox))
            return cropBox;

        return new PdfRectangle(0, 0, 612, 792);
    }

    private static PdfRectangle Intersect(PdfRectangle a, PdfRectangle b)
    {
        return new PdfRectangle(
            Math.Max(a.Left, b.Left),
            Math.Max(a.Bottom, b.Bottom),
            Math.Min(a.Right, b.Right),
            Math.Min(a.Top, b.Top));
    }

    private static bool HasPositiveArea(PdfRectangle rect)
    {
        return rect.Right > rect.Left && rect.Top > rect.Bottom;
    }

    private static int CeilingPixelCount(double value)
    {
        if (value <= 0)
            return 0;

        var rounded = Math.Round(value);
        if (Math.Abs(value - rounded) < 1e-4)
            return Math.Max(1, (int)rounded);
        return Math.Max(1, (int)Math.Ceiling(value));
    }

    /// <summary>
    /// Render a PDF page and encode it as a PNG into <paramref name="destination"/>.
    /// Convenience for framework-neutral consumers that want bytes/streams rather
    /// than an <see cref="SKBitmap"/> (e.g. a web handler or a non-Skia UI). For an
    /// <see cref="SKImage"/>/<see cref="SKPicture"/> path, call <see cref="RenderPage(PdfPage, RenderOptions)"/>
    /// and use SkiaSharp directly.
    /// </summary>
    public void RenderPageToPng(PdfPage page, Stream destination, RenderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var bitmap = RenderPage(page, options ?? new RenderOptions(), cancellationToken);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        data.SaveTo(destination);
    }
}

/// <summary>
/// Thrown when a page's box and rotation resolve to a non-positive output size.
/// </summary>
public sealed class InvalidPageGeometryException : InvalidOperationException
{
    public InvalidPageGeometryException(string message) : base(message)
    {
    }
}

/// <summary>
/// Thrown when a render request exceeds the configured bitmap resource limit.
/// </summary>
public sealed class RenderResourceLimitException : InvalidOperationException
{
    public RenderResourceLimitException(string message) : base(message)
    {
    }
}

/// <summary>
/// Context for rendering PDF content stream operators.
/// </summary>
internal partial class RenderContext
{
    // SkiaSharp's font subsystem is not safe under concurrent typeface creation:
    // SKTypeface.FromData and SKTypeface.FromFamilyName both reach into a process-wide
    // native font manager whose cache can corrupt or deadlock when two managed threads
    // call them simultaneously. Visual tests previously failed under xUnit parallelism
    // because of this; we serialize all typeface acquisition with a process-wide lock.
    private static readonly object _typefaceLoadLock = new();

    private readonly SKCanvas _canvas;
    private readonly SKBitmap? _rootBitmap;
    private readonly PdfPage _page;
    private readonly RenderOptions _options;
    private readonly Stack<GraphicsState> _stateStack;
    private GraphicsState _state;
    private SKPath? _currentPath;
    private bool? _pendingClipEvenOdd;
    private SKPath? _pendingTextClipPath;
    private TextState _textState;
    private bool _inTextBlock;
    // The complete resolved state of the font set by the most recent Tf
    // operator — see Fonts/ResolvedRenderFont.cs (#513). Null before the
    // first Tf in a content stream; read sites fall back to the same
    // defaults the old scattered fields had (WinAnsiEncoding, empty widths,
    // no typeface) rather than throwing, matching prior behavior for
    // malformed streams that show text before setting a font.
    private Fonts.ResolvedRenderFont? _currentFont;

    // Form-XObject recursion guards. PDF allows Form XObjects to invoke
    // each other via the `Do` operator, and a malformed file can have
    // a cycle (form A → form B → form A). Without protection that
    // recurses until the stack overflows and SIGABRTs the process —
    // observed on a pdf.js corpus fixture during the differential
    // run on 2026-05-01. The visited-set tracks the call stack so
    // a Do-cycle skips the recursive call; the depth counter is a
    // backstop for pathologically deep but acyclic nests.
    private readonly HashSet<Excise.Core.Primitives.PdfStream> _formXObjectStack =
        new(ReferenceEqualityComparer.Instance);
    private int _formXObjectDepth;
    // Form XObject nesting cap. Has to be high enough to satisfy PDF/A-1
    // §6.1.12's "implementation limits" conformance fixtures — those
    // specifically chain a long ladder of Form XObjects to test that a
    // reader supports deep nesting (the spec says 28+ levels of graphic
    // state nesting must work). 64 is still well below any plausible
    // .NET stack overflow point and is the same neighbourhood that
    // mutool / Poppler use; cycle detection via _formXObjectStack
    // catches genuine self-reference loops independently.
    private const int MaxFormXObjectDepth = 64;
    private const int ComplexGradientSampleCount = 384;
    private const long MaxExpandedSoftMaskPixels = 32L * 1024L * 1024L;

    private sealed class MeshBitReader
    {
        private readonly byte[] _data;
        private int _bitOffset;

        public MeshBitReader(byte[] data)
        {
            _data = data;
        }

        public int RemainingBits => (_data.Length * 8) - _bitOffset;

        public uint Read(int bitCount)
        {
            uint value = 0;
            for (var i = 0; i < bitCount; i++)
            {
                if (_bitOffset >= _data.Length * 8)
                    throw new InvalidOperationException("Mesh stream ended mid-field.");

                var b = _data[_bitOffset / 8];
                var shift = 7 - (_bitOffset % 8);
                value = (value << 1) | (uint)((b >> shift) & 1);
                _bitOffset++;
            }

            return value;
        }
    }

    private readonly record struct MeshVertex(int Flag, SKPoint Point, SKColor Color);

    private sealed class MeshTriangle
    {
        public MeshTriangle(MeshVertex a, MeshVertex b, MeshVertex c)
        {
            A = a;
            B = b;
            C = c;
            MinX = Math.Min(a.Point.X, Math.Min(b.Point.X, c.Point.X));
            MaxX = Math.Max(a.Point.X, Math.Max(b.Point.X, c.Point.X));
            MinY = Math.Min(a.Point.Y, Math.Min(b.Point.Y, c.Point.Y));
            MaxY = Math.Max(a.Point.Y, Math.Max(b.Point.Y, c.Point.Y));
        }

        public MeshVertex A { get; }
        public MeshVertex B { get; }
        public MeshVertex C { get; }
        public double MinX { get; }
        public double MaxX { get; }
        public double MinY { get; }
        public double MaxY { get; }
    }

    // Typefaces loaded from the PDF's own embedded font streams
    // (/FontFile = Type 1, /FontFile2 = TrueType, /FontFile3 = OpenType/CFF).
    // Keyed by the
    // resolved /Font dictionary's reference identity (PdfDocument.Resolve
    // caches by object number, so two ResolveFontFromActiveResources calls
    // for the same indirect ref return the same C# instance). Keying by
    // the dict instead of the resource name correctly distinguishes two
    // different physical fonts that share the same logical name (e.g.
    // /F0) in different /Resources scopes — common in widget annotation
    // appearances where each appearance dict's /Resources defines its
    // own /F0. Disposed at the end of Render().
    private readonly Dictionary<Excise.Core.Primitives.PdfDictionary, SKTypeface> _embeddedTypefaces = new();

    // Per-font byte→glyphId map extracted from a format-0 cmap subtable when
    // the typeface has no Unicode-mapped subtable Skia's shaper can use
    // (Mac Roman / format-0 subsets from veraPDF Test Builder, LibreOffice
    // and Office). When non-null for the active font, RenderText draws via
    // SKTextEncoding.GlyphId with explicit glyph IDs. Otherwise text would
    // shape to all-.notdef and the page would render blank, even though the
    // parser correctly extracts the Unicode text via /ToUnicode.
    // Cache value of null = "checked, not needed" so we don't re-probe.
    // Keyed by the same fontDict reference as _embeddedTypefaces so two
    // appearance-scoped /F0 entries that point to different fonts get
    // independent byte-cmap probes.
    private readonly Dictionary<Excise.Core.Primitives.PdfDictionary, ushort[]?> _embeddedTypefaceByteToGlyph = new();

    // Font dicts whose embedded program is a raw Type 1 /FontFile (PFA/PFB),
    // as opposed to /FontFile2 TrueType or /FontFile3 CFF/OpenType. Fill-mode
    // text for these faces must keep the pre-#710 DrawText (glyph mask) path:
    // scoping check for FillTextUsingGlyphPath — see the comment there.
    // Keyed by the same fontDict reference as _embeddedTypefaces.
    private readonly HashSet<Excise.Core.Primitives.PdfDictionary> _embeddedRawType1FontDicts = new();

    // Stack of /Resources dictionaries currently active. The page's own
    // /Resources is the bottom; entering a Form XObject pushes its own
    // /Resources (or null when absent — we still push so push/pop pair).
    // Font and XObject lookups walk top-down, falling back to page
    // resources at the bottom of the stack. Without this, annotation
    // appearances and nested Form XObjects can't see the fonts and
    // images defined in their own /Resources, so text in /AP /N streams
    // either rendered with wrong fonts or not at all.
    private readonly Stack<Excise.Core.Primitives.PdfDictionary?> _resourcesStack = new();
    private readonly Stack<bool> _optionalContentVisibilityStack = new();
    private int _hiddenOptionalContentDepth;
    private int _deviceCmykTransparencyGroupDepth;
    private int _deviceCmykKnockoutGroupDepth;
    private int _deviceCmykIsolatedGroupDepth;
    private bool _deviceCmykPreserveZeroAlphaShape;
    private bool _deviceCmykBackdropDirtyFromRgbPaint;
    private readonly DeviceCmykBackdrop? _deviceCmykBackdrop;
    private readonly PdfColorSpace _deviceCmykPreviewColorSpace;

    // Per-fontDict cache for CFF CID→glyph maps, keyed the same way as
    // _embeddedTypefaces so two different /Font dicts with the same
    // resource name but different physical fonts don't collide.
    private readonly Dictionary<Excise.Core.Primitives.PdfDictionary, Dictionary<int, int>?> _embeddedCffCidToGlyph = new();
    private readonly Dictionary<Excise.Core.Primitives.PdfDictionary, CidCMap?> _type0EncodingCMaps = new();
    private readonly HashSet<Excise.Core.Primitives.PdfStream> _type3GlyphStack = new();
    // wx metric declared by each CharProc's leading d0/d1 operator, peeked
    // once per stream (see PeekType3CharProcWx). Reference-keyed like the
    // other per-stream caches.
    private readonly Dictionary<Excise.Core.Primitives.PdfStream, float?> _type3CharProcWx =
        new(ReferenceEqualityComparer.Instance);
    // True while executing an UNCOLORED (d1) Type 3 glyph CharProc. Colour
    // operators in the CharProc are suppressed and the glyph paints in the
    // text object's fill colour (ISO 32000-1 §9.6.5). Reset per glyph in
    // RenderType3Glyph; set by the d1 operator.
    private bool _type3GlyphColorLocked;
    // Device-space accumulator for the painted coverage of the Type 3 glyph
    // currently executing under a clipping text render mode (Tr 4-7, #514).
    // Type 3 glyphs have no outline to hand the text-clip machinery, so the
    // shapes their CharProcs paint (path fills, stroke outlines, image
    // bounds) are collected here and folded into _pendingTextClipPath when
    // the glyph finishes. Null whenever no clipping Type 3 glyph is running.
    private SKPath? _type3ClipCollector;
    // True while a CharProc is executed ONLY for its clip contribution
    // (Tr 7, clip-without-paint). Painting operators still contribute their
    // geometry to _type3ClipCollector but must not mark the page.
    private bool _type3ClipOnlyPass;
    private readonly Dictionary<(int ObjectNumber, int Generation, int TargetWidth, int TargetHeight), SoftMaskAlpha?> _softMaskAlphaByReference = new();
    private readonly Dictionary<Excise.Core.Primitives.PdfStream, Dictionary<(int TargetWidth, int TargetHeight), SoftMaskAlpha?>> _softMaskAlphaByStream =
        new(ReferenceEqualityComparer.Instance);
    // Parsed content-stream operators per source byte[] instance (#598) — see
    // ExecuteContentBytes. Reference-keyed like the other per-stream caches;
    // lifetime is this RenderContext (a single page render).
    private readonly Dictionary<byte[], Excise.Core.Content.ContentStream> _parsedContentByBytes =
        new(ReferenceEqualityComparer.Instance);
    // Observability hook for tests: parse-cache hits on the current thread
    // (rendering executes synchronously on the calling thread). Thread-static
    // so parallel test collections cannot interfere with each other's counts.
    [ThreadStatic]
    internal static long ContentStreamParseCacheHits;
    [ThreadStatic]
    internal static long GlyphOutlineCacheHits;
    [ThreadStatic]
    internal static long GlyphOutlineCacheMisses;
    // Decoded-image cache observability (#599). A hit means a repeated image
    // XObject (logo, background, tiled-pattern cell, form-invoked art) reused an
    // already-decoded SKBitmap instead of re-running the SKCodec decode + color
    // conversion. Counted across the whole page render, including child
    // transparency-group / pattern contexts, which share the root cache.
    // Thread-static for the same reason as the glyph counters above.
    [ThreadStatic]
    internal static long ImageBitmapCacheHits;
    [ThreadStatic]
    internal static long ImageBitmapCacheMisses;
    // Decoded-image caches. Shared with child transparency-group / tiling-pattern
    // contexts via the shared-scope constructor so a repeated image decodes once
    // per page, not once per context (#599). Only the owning (root) context
    // disposes them — see _ownsImageBitmapCache / DisposeImageBitmapCache.
    private readonly Dictionary<(int ObjectNumber, int Generation, ImageBitmapCacheKey Key), SKBitmap?> _imageBitmapByReference;
    private readonly Dictionary<Excise.Core.Primitives.PdfStream, Dictionary<ImageBitmapCacheKey, SKBitmap?>> _imageBitmapByStream;
    private readonly List<SKBitmap> _cachedImageBitmaps;
    private readonly bool _ownsImageBitmapCache;
    // Tessellated glyph outlines keyed by (typeface, font-size bits, glyph
    // string) — see GetCachedGlyphOutline (#598). SKFont.GetTextPath drives the
    // platform font scaler to build the outline on every call; body text
    // repeats the same glyph at one size thousands of times per page, so the
    // uncached path re-tessellates identical geometry. The cache stores the
    // UNPOSITIONED outline at origin; callers transform a fresh copy per draw
    // (cursor + horizontal squeeze) and never mutate the cached path, so the
    // geometry handed to DrawPath is byte-identical to the uncached path.
    // CreateTextFont is a fixed-property constructor, so any two SKFonts for the
    // same (typeface, size) yield identical outlines — that is what makes the
    // key exact. Lifetime is this RenderContext (one page render); disposed in
    // DisposeOwnedResources. Typeface is compared by reference.
    private readonly Dictionary<(SKTypeface Typeface, int SizeBits, string Text), SKPath?> _glyphOutlineCache =
        new(GlyphOutlineKeyComparer.Instance);
    // The glyph-ID variant of the outline cache (#598). Embedded subset fonts
    // (Type0/CID and byte-cmap simple fonts) are drawn glyph-by-glyph via
    // SKFont.GetGlyphPath in BuildGlyphIdTextPath — this is the tessellation
    // hot path for real-world body text, where the same glyph ID recurs
    // thousands of times per page at one size. Same immutability/lifetime rules
    // as _glyphOutlineCache; the key is the glyph ID, so no per-glyph string is
    // allocated. Typeface is compared by reference.
    private readonly Dictionary<(SKTypeface Typeface, int SizeBits, ushort Gid), SKPath?> _glyphOutlineByIdCache =
        new(GlyphIdOutlineKeyComparer.Instance);
    private DeviceCmykBackdrop? _deviceCmykKnockoutInitialBackdrop;
    private int _tilingPatternDepth;

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

    private readonly CancellationToken _cancellationToken;

    private sealed record SoftMaskAlpha(byte[] Data, int Width, int Height);
    private readonly record struct ImageBitmapCacheKey(
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

    public RenderContext(SKCanvas canvas, PdfPage page, RenderOptions options,
        CancellationToken cancellationToken = default,
        SKBitmap? rootBitmap = null,
        bool startsInDeviceCmykTransparencyGroup = false,
        RenderContext? imageCacheOwner = null)
    {
        _canvas = canvas;
        _rootBitmap = rootBitmap;
        _page = page;
        _options = options;
        _cancellationToken = cancellationToken;
        if (imageCacheOwner != null)
        {
            // Share the decoded-image cache with the owning context so an image
            // reused across a transparency group / tiling pattern decodes once
            // per page (#599). The owner disposes; this context does not.
            _imageBitmapByReference = imageCacheOwner._imageBitmapByReference;
            _imageBitmapByStream = imageCacheOwner._imageBitmapByStream;
            _cachedImageBitmaps = imageCacheOwner._cachedImageBitmaps;
            _ownsImageBitmapCache = false;
        }
        else
        {
            _imageBitmapByReference = new();
            _imageBitmapByStream = new(ReferenceEqualityComparer.Instance);
            _cachedImageBitmaps = new();
            _ownsImageBitmapCache = true;
        }
        _deviceCmykPreviewColorSpace = PdfColorSpace.Parse(PdfName.DeviceCMYK, page.Document);
        _deviceCmykTransparencyGroupDepth = startsInDeviceCmykTransparencyGroup ? 1 : 0;
        _deviceCmykBackdrop = startsInDeviceCmykTransparencyGroup && rootBitmap != null
            ? new DeviceCmykBackdrop(rootBitmap.Width, rootBitmap.Height)
            : null;
        _stateStack = new Stack<GraphicsState>();
        _state = new GraphicsState();
        _textState = new TextState();
        _inTextBlock = false;

        // Register code pages encoding provider for Windows-1252, Mac Roman, etc.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public void Render()
    {
        try
        {
            // Page resources sit at the bottom of the stack. Form XObjects
            // (incl. annotation appearances) push their own /Resources on
            // top of this when entered; lookups fall back to the page when
            // a name isn't defined locally.
            _resourcesStack.Push(_page.Resources);

            _page.TryGetContentStreamBytes(out var contentBytes, out var contentWarnings);
            AddDiagnostics(contentWarnings);

            // Execute page content inside its own save/restore.
            //
            // A content stream is NOT required to leave the graphics state as
            // it found it, and real files don't. pdfium's bug_896366.pdf is
            // exactly one operator long:
            //
            //     1 0 0 -1 0 792 cm
            //
            // — a Y-flip concatenated onto the CTM with no enclosing q/Q. Any
            // unbalanced `q` or trailing `cm` has the same effect.
            //
            // Annotation appearances are positioned by their /Rect in DEFAULT
            // user space (§12.5.5); they do not inherit whatever transform the
            // content stream happened to finish with. Without this bracket the
            // leftover CTM silently relocated every annotation on the page —
            // on bug_896366 excise drew the widget at raster y80..119 while
            // mutool and pdftocairo both drew it at y672..711, mirrored about
            // the page centre.
            // _state.CurrentTransform is a parallel CTM the image and shading
            // paths read, so restoring only the canvas matrix would leave the
            // two disagreeing. Reset both, the same way RenderFormXObjectInner
            // brackets a nested form.
            var baseState = _state.Clone();
            var baseTextState = _textState.Clone();
            _canvas.Save();
            try
            {
                if (contentBytes.Length > 0)
                    ExecuteContentBytes(contentBytes);
            }
            finally
            {
                _canvas.Restore();
                _stateStack.Clear();
                _state = baseState;
                _textState = baseTextState;
                _inTextBlock = false;
            }

            // Annotations render on top of page content — sticky notes,
            // FreeText callouts, Widget appearances, etc. live in the
            // page's /Annots array as separate Form XObjects rather than
            // in the content stream. Without this pass, pages where the
            // visible text is entirely in annotations (a common veraPDF
            // PDF/UA fixture pattern) come out blank.
            RenderAnnotations();
        }
        finally
        {
            _resourcesStack.Clear();
            DisposeOwnedResources();
        }
    }

    private void ExecuteContentOperator(ContentOperator op)
    {
        var operands = op.Operands;
        switch (op.Name)
        {
            case "BMC":
                BeginMarkedContent(visible: true);
                return;
            case "BDC":
                BeginMarkedContent(ResolveMarkedContentVisibility(op));
                return;
            case "EMC":
                EndMarkedContent();
                return;
        }

        if (IsOptionalContentSuppressed && SuppressHiddenOptionalContentPaint(op.Name))
            return;

        // Clip-only Type 3 glyph pass (Tr 7, #514): path-painting and image
        // operators fall through — their handlers collect clip coverage
        // before skipping the actual draw. A shading fills the whole current
        // clip region rather than a describable shape, so it is dropped here
        // (its coverage is not collected; see RenderType3Glyph).
        if (_type3ClipOnlyPass && op.Name == "sh")
            return;

        // Inside an uncolored (d1) Type 3 glyph, colour-setting operators are
        // ignored so the glyph is painted with the fill colour in effect in the
        // text object (ISO 32000-1 §9.6.5, Table 113). d0 (colored) glyphs are
        // unaffected because the lock is only set by the d1 operator.
        if (_type3GlyphColorLocked && IsColorSettingOperator(op.Name))
            return;

        switch (op.Name)
        {
            // Graphics state
            case "q":
                SaveState();
                break;
            case "Q":
                RestoreState();
                break;
            case "cm":
                if (operands.Count >= 6)
                    ApplyTransform(op);
                break;
            case "w":
                if (operands.Count >= 1)
                    _state.LineWidth = Number(operands, 0);
                break;
            case "J":
                if (operands.Count >= 1)
                    _state.LineCap = (int)Number(operands, 0);
                break;
            case "j":
                if (operands.Count >= 1)
                    _state.LineJoin = (int)Number(operands, 0);
                break;
            case "M":
                if (operands.Count >= 1)
                    _state.MiterLimit = (float)Number(operands, 0);
                break;
            case "d":
                SetDashPattern(operands.Count > 0 ? operands[0] as PdfArray : null, Number(operands, 1));
                break;
            case "ri":
                // Rendering intent - no effect on rendering for now
                break;
            case "i":
                // Flatness tolerance - no effect on rendering for now
                break;

            // Color (grayscale)
            case "g":
                if (operands.Count >= 1)
                {
                    _state.FillColor = GrayToColor(Number(operands, 0));
                    _state.FillColorSpace = "DeviceGray";
                    _state.FillDeviceCmyk = null;
                    _state.FillPatternName = null;
                }
                break;
            case "G":
                if (operands.Count >= 1)
                {
                    _state.StrokeColor = GrayToColor(Number(operands, 0));
                    _state.StrokeColorSpace = "DeviceGray";
                    _state.StrokeDeviceCmyk = null;
                }
                break;

            // Color (RGB)
            case "rg":
                if (operands.Count >= 3)
                {
                    _state.FillColor = RgbToColor(
                        Number(operands, 0),
                        Number(operands, 1),
                        Number(operands, 2));
                    _state.FillColorSpace = "DeviceRGB";
                    _state.FillDeviceCmyk = null;
                    _state.FillPatternName = null;
                }
                break;
            case "RG":
                if (operands.Count >= 3)
                {
                    _state.StrokeColor = RgbToColor(
                        Number(operands, 0),
                        Number(operands, 1),
                        Number(operands, 2));
                    _state.StrokeColorSpace = "DeviceRGB";
                    _state.StrokeDeviceCmyk = null;
                }
                break;

            // Color (CMYK)
            case "k":
                if (operands.Count >= 4)
                {
                    var c = Number(operands, 0);
                    var m = Number(operands, 1);
                    var y = Number(operands, 2);
                    var k = Number(operands, 3);
                    _state.FillColor = DeviceCmykToColor(new DeviceCmykColor(c, m, y, k));
                    _state.FillColorSpace = "DeviceCMYK";
                    _state.FillDeviceCmyk = new DeviceCmykColor(c, m, y, k);
                    _state.FillPatternName = null;
                }
                break;
            case "K":
                if (operands.Count >= 4)
                {
                    var c = Number(operands, 0);
                    var m = Number(operands, 1);
                    var y = Number(operands, 2);
                    var k = Number(operands, 3);
                    _state.StrokeColor = DeviceCmykToColor(new DeviceCmykColor(c, m, y, k));
                    _state.StrokeColorSpace = "DeviceCMYK";
                    _state.StrokeDeviceCmyk = new DeviceCmykColor(c, m, y, k);
                }
                break;

            // Extended graphics state
            case "gs":
                if (operands.Count >= 1)
                    ApplyExtGState(Name(operands, 0));
                break;

            // XObject rendering (images and forms)
            case "Do":
                if (operands.Count >= 1)
                    RenderXObject(Name(operands, 0));
                break;
            case "BI":
                if (operands.Count >= 1
                    && operands[0] is PdfDictionary imageParams
                    && op.InlineImageData is { } inlineImageData)
                    RenderInlineImage(imageParams, inlineImageData);
                break;

            // Path construction
            case "m":
                if (operands.Count >= 2)
                    MoveTo(Number(operands, 0), Number(operands, 1));
                break;
            case "l":
                if (operands.Count >= 2)
                    LineTo(Number(operands, 0), Number(operands, 1));
                break;
            case "c":
                if (operands.Count >= 6)
                    CurveTo(
                        Number(operands, 0), Number(operands, 1),
                        Number(operands, 2), Number(operands, 3),
                        Number(operands, 4), Number(operands, 5));
                break;
            case "v":
                if (operands.Count >= 4)
                    CurveToV(
                        Number(operands, 0), Number(operands, 1),
                        Number(operands, 2), Number(operands, 3));
                break;
            case "y":
                if (operands.Count >= 4)
                    CurveToY(
                        Number(operands, 0), Number(operands, 1),
                        Number(operands, 2), Number(operands, 3));
                break;
            case "h":
                ClosePath();
                break;
            case "re":
                if (operands.Count >= 4)
                    Rectangle(
                        Number(operands, 0), Number(operands, 1),
                        Number(operands, 2), Number(operands, 3));
                break;

            // Path painting
            case "S":
                StrokePath();
                break;
            case "s":
                ClosePath();
                StrokePath();
                break;
            case "f":
            case "F":
                FillPath(false);
                break;
            case "f*":
                FillPath(true);
                break;
            case "B":
                FillAndStroke(false);
                break;
            case "B*":
                FillAndStroke(true);
                break;
            case "b":
                ClosePath();
                FillAndStroke(false);
                break;
            case "b*":
                ClosePath();
                FillAndStroke(true);
                break;
            case "n":
                // End path without fill or stroke (no-op)
                ApplyPendingClipToCurrentPath();
                _currentPath?.Dispose();
                _currentPath = null;
                break;

            // Clipping path operators (#295)
            case "W":
                SetClippingPath(false);
                break;
            case "W*":
                SetClippingPath(true);
                break;

            // Marked content operators (#298) are handled before paint suppression.
            case "MP":
                // Marked content point - no visual effect
                break;
            case "DP":
                // Marked content point with property list - no visual effect
                break;

            // Shading operator (#300)
            case "sh":
                if (operands.Count >= 1)
                    RenderShading(Name(operands, 0));
                break;

            // Type 3 font operators (#301, #514). d0/d1 are only legal as the
            // first operator of a glyph description (ISO 32000-1 §9.6.5); a
            // stray d0/d1 in an ordinary content stream is ignored so it can
            // neither colour-lock nor clip the rest of the page.
            case "d0":
                // Colored glyph description. The wx metric is consumed by
                // PeekType3CharProcWx when /Widths doesn't cover the code.
                break;
            case "d1":
                if (_type3GlyphStack.Count == 0)
                    break;
                // d1 declares an UNCOLORED glyph description: the CharProc paints
                // only a shape/mask, colour operators in the rest of it are
                // ignored, and the glyph is filled with the text object's current
                // colour (ISO 32000-1 §9.6.5, Table 113). Its llx/lly/urx/ury
                // operands declare the glyph bounding box, which clips the glyph
                // description (an all-zero box declares no bounds).
                _type3GlyphColorLocked = true;
                ApplyType3GlyphBBoxClip(op);
                break;

            // Color space operators
            case "CS":
                // Set stroking color space - store for later use with SC/SCN
                if (operands.Count >= 1)
                    _state.StrokeColorSpace = Name(operands, 0);
                break;
            case "cs":
                // Set non-stroking color space
                if (operands.Count >= 1)
                    _state.FillColorSpace = Name(operands, 0);
                break;
            case "SC":
            case "SCN":
                // Set stroking color
                SetStrokingColor(operands);
                break;
            case "sc":
            case "scn":
                // Set non-stroking (fill) color
                SetNonStrokingColor(operands);
                break;

            // Text state operators
            case "BT":
                BeginText();
                break;
            case "ET":
                EndText();
                break;
            case "Tf":
                if (operands.Count >= 2)
                    SetFont(Name(operands, 0), Number(operands, 1));
                break;
            case "Td":
                if (operands.Count >= 2)
                    TextMove(Number(operands, 0), Number(operands, 1));
                break;
            case "TD":
                if (operands.Count >= 2)
                {
                    _textState.TextLeading = -(float)Number(operands, 1);
                    TextMove(Number(operands, 0), Number(operands, 1));
                }
                break;
            case "Tm":
                if (operands.Count >= 6)
                    SetTextMatrix(
                        Number(operands, 0), Number(operands, 1),
                        Number(operands, 2), Number(operands, 3),
                        Number(operands, 4), Number(operands, 5));
                break;
            case "T*":
                TextNewLine();
                break;
            case "Tc":
                if (operands.Count >= 1)
                    _textState.CharSpacing = (float)Number(operands, 0);
                break;
            case "Tw":
                if (operands.Count >= 1)
                    _textState.WordSpacing = (float)Number(operands, 0);
                break;
            case "Tz":
                if (operands.Count >= 1)
                    _textState.HorizontalScale = (float)Number(operands, 0);
                break;
            case "TL":
                if (operands.Count >= 1)
                    _textState.TextLeading = (float)Number(operands, 0);
                break;
            case "Tr":
                if (operands.Count >= 1)
                    _textState.RenderMode = (int)Number(operands, 0);
                break;
            case "Ts":
                if (operands.Count >= 1)
                    _textState.TextRise = (float)Number(operands, 0);
                break;

            // Text showing operators
            case "Tj":
                if (operands.Count >= 1)
                    ShowText(operands[0] as PdfString);
                break;
            case "TJ":
                ShowTextArray(operands.Count > 0 ? operands[0] as PdfArray : null);
                break;
            case "'":
                TextNewLine();
                if (operands.Count >= 1)
                    ShowText(operands[0] as PdfString);
                break;
            case "\"":
                if (operands.Count >= 3)
                {
                    _textState.WordSpacing = (float)Number(operands, 0);
                    _textState.CharSpacing = (float)Number(operands, 1);
                    TextNewLine();
                    ShowText(operands[2] as PdfString);
                }
                break;

            // Compatibility operators (BX/EX) — accepted as no-ops so the
            // dispatcher consumes them without flagging them as unknown.
            case "BX":
            case "EX":
                break;

            // Ignore unknown operators
            default:
                break;
        }
    }

    // Colour-setting operators (ISO 32000-1 Table 74). Suppressed inside an
    // uncolored (d1) Type 3 glyph CharProc so it paints in the text colour.
    private static bool IsColorSettingOperator(string op) => op switch
    {
        "g" or "G" or "rg" or "RG" or "k" or "K" or
        "cs" or "CS" or "sc" or "scn" or "SC" or "SCN" => true,
        _ => false,
    };

    private void AddDiagnostics(IEnumerable<ContentStreamReadWarning> warnings)
    {
        if (_options.Diagnostics == null)
            return;

        foreach (var warning in warnings)
            _options.Diagnostics.Add(warning.ToString());
    }

    private void ExecuteContentOperators(IEnumerable<ContentOperator> operators)
    {
        foreach (var op in operators)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            ExecuteContentOperator(op);
        }
    }

    private void ExecuteContentBytes(byte[] contentBytes)
    {
        // Parsed-operator cache for repeatedly executed content streams (#598).
        // A Form XObject invoked N times on a page, a Type 3 CharProc executed
        // per glyph occurrence, and a tiling pattern cell stamped per tile all
        // hand this method the SAME byte[] instance (PdfStream caches its
        // decoded bytes), and re-parsing it per execution was the tractable
        // share of the form-execution hot path in the #597 render trace.
        // This caches the PARSE only — never the drawn result: the cached
        // ContentOperator list is immutable data the renderer only reads, and
        // every execution still runs the full operator interpreter under the
        // invocation's own graphics state / CTM / clip / resource scope.
        // Keying by byte[] REFERENCE gives natural invalidation (replacing a
        // stream's data via SetDecodedData/SetEncodedData or the DecodedData
        // setter produces a new array instance and therefore a cache miss),
        // and the cache lives only for this RenderContext (one page render),
        // so a stream mutated between renders can never serve stale operators.
        if (!_parsedContentByBytes.TryGetValue(contentBytes, out var content))
        {
            // Metadata-free parse (#598): the renderer re-executes every
            // operator under its own graphics/text state machine and never
            // reads the parser-computed BoundingBox/TextContent, so skip the
            // parser's own state-tracking pass (font resolution, ToUnicode
            // CMaps, glyph-width advances, bounds accumulation).
            content = new ContentStreamParser(contentBytes, _page)
                { ComputeOperatorMetadata = false }
                .Parse(_cancellationToken);
            _parsedContentByBytes[contentBytes] = content;
        }
        else
        {
            ContentStreamParseCacheHits++;
        }

        ExecuteContentOperators(content.Operators);
    }

    private bool IsOptionalContentSuppressed => _hiddenOptionalContentDepth > 0;

    private void BeginMarkedContent(bool visible)
    {
        _optionalContentVisibilityStack.Push(visible);
        if (!visible)
            _hiddenOptionalContentDepth++;
    }

    private void EndMarkedContent()
    {
        if (_optionalContentVisibilityStack.Count == 0)
            return;

        if (!_optionalContentVisibilityStack.Pop())
            _hiddenOptionalContentDepth--;
    }

    private bool ResolveMarkedContentVisibility(ContentOperator op)
    {
        var tag = Name(op.Operands, 0);
        if (tag != "OC" || op.Operands.Count < 2)
            return true;

        var propertyObject = ResolveMarkedContentPropertyObject(op.Operands[1]);
        if (propertyObject == null)
            return true;

        return IsOptionalContentObjectVisible(propertyObject);
    }

    private Excise.Core.Primitives.PdfObject? ResolveMarkedContentPropertyObject(Excise.Core.Primitives.PdfObject propertyObject)
    {
        if (propertyObject is PdfName propertyName)
            return ResolvePropertyFromActiveResources(propertyName.Value);

        return propertyObject;
    }

    private bool IsOptionalContentObjectVisible(Excise.Core.Primitives.PdfObject optionalContentObject)
    {
        var resolved = _page.Document.Resolve(optionalContentObject);
        if (resolved is not Excise.Core.Primitives.PdfDictionary dict)
            return true;

        var type = dict.GetNameOrNull("Type");
        return type switch
        {
            "OCG" => IsOptionalContentGroupVisible(optionalContentObject, dict),
            "OCMD" => IsOptionalContentMembershipVisible(dict),
            _ => dict.GetOptional("OC") is { } nested
                ? IsOptionalContentObjectVisible(nested)
                : true,
        };
    }

    private bool IsOptionalContentMembershipVisible(Excise.Core.Primitives.PdfDictionary membership)
    {
        if (membership.GetOptional("VE") is { } visibilityExpression)
            return EvaluateOptionalContentVisibilityExpression(visibilityExpression);

        var ocgsObj = membership.GetOptional("OCGs");
        if (ocgsObj == null)
            return true;

        var visibilities = new List<bool>();
        var resolvedOcgs = _page.Document.Resolve(ocgsObj);
        if (resolvedOcgs is Excise.Core.Primitives.PdfArray ocgArray)
        {
            foreach (var ocg in ocgArray)
                visibilities.Add(IsOptionalContentObjectVisible(ocg));
        }
        else
        {
            visibilities.Add(IsOptionalContentObjectVisible(ocgsObj));
        }

        if (visibilities.Count == 0)
            return true;

        var policy = membership.GetNameOrNull("P") ?? "AnyOn";
        return policy switch
        {
            "AllOn" => visibilities.All(v => v),
            "AnyOff" => visibilities.Any(v => !v),
            "AllOff" => visibilities.All(v => !v),
            _ => visibilities.Any(v => v),
        };
    }

    private bool EvaluateOptionalContentVisibilityExpression(Excise.Core.Primitives.PdfObject expressionObject)
    {
        var resolved = _page.Document.Resolve(expressionObject);
        if (resolved is Excise.Core.Primitives.PdfDictionary dict)
            return IsOptionalContentObjectVisible(dict);

        if (resolved is not Excise.Core.Primitives.PdfArray expression || expression.Count == 0)
            return true;

        var op = expression[0] as PdfName;
        if (op == null)
            return true;

        return op.Value switch
        {
            "And" => EvaluateVisibilityOperands(expression).All(v => v),
            "Or" => EvaluateVisibilityOperands(expression).Any(v => v),
            "Not" => expression.Count < 2 || !EvaluateOptionalContentVisibilityExpression(expression[1]),
            _ => true,
        };
    }

    private IEnumerable<bool> EvaluateVisibilityOperands(Excise.Core.Primitives.PdfArray expression)
    {
        for (var i = 1; i < expression.Count; i++)
            yield return EvaluateOptionalContentVisibilityExpression(expression[i]);
    }

    private bool IsOptionalContentGroupVisible(
        Excise.Core.Primitives.PdfObject ocgObject,
        Excise.Core.Primitives.PdfDictionary ocg)
    {
        var defaultConfig = GetOptionalContentDefaultConfig();
        if (defaultConfig == null)
            return true;

        if (IsOcgListed(defaultConfig.GetOptional("OFF"), ocgObject, ocg))
            return false;

        if (IsOcgListed(defaultConfig.GetOptional("ON"), ocgObject, ocg))
            return true;

        return !string.Equals(defaultConfig.GetNameOrNull("BaseState"), "OFF", StringComparison.Ordinal);
    }

    private Excise.Core.Primitives.PdfDictionary? GetOptionalContentDefaultConfig()
    {
        var ocPropsObj = _page.Document.Catalog.GetOptional("OCProperties");
        if (_page.Document.Resolve(ocPropsObj ?? PdfNull.Instance) is not Excise.Core.Primitives.PdfDictionary ocProps)
            return null;

        return _page.Document.Resolve(ocProps.GetOptional("D") ?? PdfNull.Instance)
            as Excise.Core.Primitives.PdfDictionary;
    }

    private bool IsOcgListed(
        Excise.Core.Primitives.PdfObject? listObject,
        Excise.Core.Primitives.PdfObject ocgObject,
        Excise.Core.Primitives.PdfDictionary ocg)
    {
        if (_page.Document.Resolve(listObject ?? PdfNull.Instance) is not Excise.Core.Primitives.PdfArray list)
            return false;

        foreach (var item in list)
        {
            if (ReferencesSameObject(item, ocgObject, ocg))
                return true;
        }

        return false;
    }

    private bool ReferencesSameObject(
        Excise.Core.Primitives.PdfObject item,
        Excise.Core.Primitives.PdfObject ocgObject,
        Excise.Core.Primitives.PdfDictionary ocg)
    {
        if (item is PdfReference itemRef && ocgObject is PdfReference ocgRef)
            return itemRef == ocgRef;

        if (item is PdfReference refItem &&
            ocg.ObjectNumber == refItem.ObjectNum &&
            ocg.GenerationNumber == refItem.Generation)
            return true;

        var resolvedItem = _page.Document.Resolve(item);
        if (resolvedItem is Excise.Core.Primitives.PdfDictionary itemDict)
        {
            if (itemDict.ObjectNumber.HasValue && ocg.ObjectNumber.HasValue)
                return itemDict.ObjectNumber == ocg.ObjectNumber &&
                       itemDict.GenerationNumber == ocg.GenerationNumber;

            return ReferenceEquals(itemDict, ocg);
        }

        return false;
    }

    private bool SuppressHiddenOptionalContentPaint(string name)
    {
        switch (name)
        {
            case "S":
            case "s":
            case "f":
            case "F":
            case "f*":
            case "B":
            case "B*":
            case "b":
            case "b*":
                DiscardCurrentPath();
                return true;
            case "Do":
            case "BI":
            case "sh":
                return true;
            default:
                return false;
        }
    }

    private void DiscardCurrentPath()
    {
        _pendingClipEvenOdd = null;
        _currentPath?.Dispose();
        _currentPath = null;
    }

    private static double Number(IReadOnlyList<PdfObject> operands, int index)
        => index >= 0 && index < operands.Count && operands[index].TryGetNumber(out var value)
            ? value
            : 0;

    private static string Name(IReadOnlyList<PdfObject> operands, int index)
        => index >= 0 && index < operands.Count && operands[index] is PdfName name
            ? name.Value
            : string.Empty;

    #region State Management

    private void SaveState()
    {
        _stateStack.Push(_state.Clone());
        _canvas.Save();
    }

    private void RestoreState()
    {
        if (_stateStack.Count > 0)
        {
            _state = _stateStack.Pop();
            _canvas.Restore();
        }
    }

    private void ApplyTransform(ContentOperator op)
    {
        // Clamp matrix components to PDF 32000-2 §6.1.12's
        // "implementation limit" range. Values larger than ±32767 are
        // outside the spec's guaranteed range and either cause Skia's
        // accumulated CTM to overflow into NaN/Inf (so subsequent draws
        // collapse to nothing) or push content astronomically far
        // off-page. Real PDFs never have values this big; conformance
        // tests like A019-pdfa2-pass-* use ±FLT_MAX to verify the
        // reader degrades gracefully. Clamping matches mutool's policy.
        var a = ClampMatrix(op.GetNumber(0));
        var b = ClampMatrix(op.GetNumber(1));
        var c = ClampMatrix(op.GetNumber(2));
        var d = ClampMatrix(op.GetNumber(3));
        var e = ClampMatrix(op.GetNumber(4));
        var f = ClampMatrix(op.GetNumber(5));

        var matrix = new SKMatrix(a, c, e, b, d, f, 0, 0, 1);
        _canvas.Concat(in matrix);
        _state.CurrentTransform = Concat(_state.CurrentTransform, matrix);
    }

    private static SKMatrix Concat(SKMatrix first, SKMatrix second)
    {
        return new SKMatrix(
            first.ScaleX * second.ScaleX + first.SkewX * second.SkewY,
            first.ScaleX * second.SkewX + first.SkewX * second.ScaleY,
            first.ScaleX * second.TransX + first.SkewX * second.TransY + first.TransX,
            first.SkewY * second.ScaleX + first.ScaleY * second.SkewY,
            first.SkewY * second.SkewX + first.ScaleY * second.ScaleY,
            first.SkewY * second.TransX + first.ScaleY * second.TransY + first.TransY,
            0,
            0,
            1);
    }

    private const float MatrixComponentMax = 32767f;
    private static float ClampMatrix(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return 0f;
        if (v > MatrixComponentMax) return MatrixComponentMax;
        if (v < -MatrixComponentMax) return -MatrixComponentMax;
        return (float)v;
    }

    #endregion

    #region Color Conversion

    private static SKColor GrayToColor(double gray)
    {
        var g = (byte)Math.Clamp(gray * 255, 0, 255);
        return new SKColor(g, g, g);
    }

    private static SKColor RgbToColor(double r, double g, double b)
    {
        return new SKColor(
            (byte)Math.Clamp(r * 255, 0, 255),
            (byte)Math.Clamp(g * 255, 0, 255),
            (byte)Math.Clamp(b * 255, 0, 255));
    }

    private SKColor DeviceCmykToColor(DeviceCmykColor color, byte alpha = 255)
    {
        var (r, g, b) = DeviceCmykToRgb(color);
        return new SKColor(
            (byte)Math.Clamp(r * 255, 0, 255),
            (byte)Math.Clamp(g * 255, 0, 255),
            (byte)Math.Clamp(b * 255, 0, 255),
            alpha);
    }

    private (double R, double G, double B) DeviceCmykToRgb(DeviceCmykColor color)
        => _deviceCmykPreviewColorSpace.ToRgb(new[] { color.C, color.M, color.Y, color.K });

    private static SKColor CmykToColor(double c, double m, double y, double k)
    {
        var (r, g, b) = PdfColorSpace.ConvertDeviceCmykToRgb(c, m, y, k);
        return RgbToColor(r, g, b);
    }

    #endregion

    #region Extended Graphics State (gs operator)

    private void ApplyExtGState(string nameOperand)
    {
        // Remove leading / if present
        var name = nameOperand.TrimStart('/');
        var extGState = ResolveExtGStateFromActiveResources(name);
        if (extGState == null)
            return;

        // CA - Stroking alpha
        if (extGState.ContainsKey("CA"))
        {
            var alpha = extGState.GetNumber("CA", 1.0);
            _state.StrokeAlpha = (float)Math.Clamp(alpha, 0, 1);
        }

        // ca - Non-stroking (fill) alpha
        if (extGState.ContainsKey("ca"))
        {
            var alpha = extGState.GetNumber("ca", 1.0);
            _state.FillAlpha = (float)Math.Clamp(alpha, 0, 1);
        }

        // LW - Line width
        if (extGState.ContainsKey("LW"))
        {
            _state.LineWidth = extGState.GetNumber("LW", 1.0);
        }

        // LC - Line cap style
        if (extGState.ContainsKey("LC"))
        {
            _state.LineCap = (int)extGState.GetNumber("LC", 0);
        }

        // LJ - Line join style
        if (extGState.ContainsKey("LJ"))
        {
            _state.LineJoin = (int)extGState.GetNumber("LJ", 0);
        }

        // ML - Miter limit
        if (extGState.ContainsKey("ML"))
        {
            _state.MiterLimit = (float)extGState.GetNumber("ML", 10.0);
        }

        // OP / op / OPM - overprint control (ISO 32000-1 Table 58, #634).
        // A dictionary that sets /OP without /op sets BOTH flags: op's
        // default is OP's value when only OP is present in the same dict.
        // OPM persists across gs operators that omit it (like every other
        // ExtGState entry), which real prepress fixtures rely on (Ghent
        // GWG011 sets /OPM 1 in one ExtGState and toggles /OP in the next).
        if (extGState.ContainsKey("OP"))
        {
            var strokeOverprint = extGState.GetBool("OP");
            _state.StrokeOverprint = strokeOverprint;
            if (!extGState.ContainsKey("op"))
                _state.FillOverprint = strokeOverprint;
        }

        if (extGState.ContainsKey("op"))
        {
            _state.FillOverprint = extGState.GetBool("op");
        }

        if (extGState.ContainsKey("OPM"))
        {
            _state.OverprintMode = (int)extGState.GetNumber("OPM", 0);
        }

        if (extGState.ContainsKey("BM"))
        {
            var bm = extGState.GetNameOrNull("BM") ?? "Normal";
            _state.BlendMode = MapBlendMode(bm);
        }

        // /Font — §8.4.5 Table 58: [ fontRef size ]. Equivalent to Tf, except
        // the font arrives as a DIRECT REFERENCE to a font dictionary rather
        // than as a name to look up in /Resources /Font.
        //
        // Nine corpus pages (veraPDF 6-1-12-t02 x5, TWG A001 x4) rendered blank
        // because of this. Their content streams carry no Tf at all and their
        // pages carry no /Font resource — the font and its size come solely
        // from the ExtGState. Nothing was missing from the font machinery:
        // their subsets have an ordinary (3,1) cmap that resolves the glyph
        // with an outline. The state key was simply never read, so the text
        // had no font and drew nothing.
        //
        // Those nine were filed under #886 ("code->GID mapping fails on
        // embedded subsets") because the clustering script matched /FontFile in
        // the document. They are not font-program bugs.
        if (extGState.ContainsKey("Font") &&
            ResolveArray(extGState, "Font") is { Count: >= 2 } fontEntry)
        {
            if (_page.Document.Resolve(fontEntry[0]) is Excise.Core.Primitives.PdfDictionary gsFontDict &&
                TryGetResolvedNumber(_page.Document.Resolve(fontEntry[1]), out var gsFontSize))
            {
                _textState.FontSize = (float)gsFontSize;
                _currentFont = ResolveRenderFont(_textState.FontName ?? string.Empty, gsFontDict);
            }
        }

        if (extGState.ContainsKey("SMask"))
        {
            var smaskObj = extGState.GetOptional("SMask");
            if (smaskObj is Excise.Core.Primitives.PdfName n && n.Value == "None")
            {
                _state.SoftMask = null;
            }
            else if (smaskObj != null)
            {
                _state.SoftMask = smaskObj;
            }
            // Note: full soft mask (transparency group) rendering not yet supported
        }
    }

    private void RenderWithCurrentSoftMask(
        Action drawAction,
        SKPaint sourcePaint,
        SKRect? preferredBounds = null,
        bool seedBackdrop = false)
    {
        if (_state.SoftMask == null)
        {
            drawAction();
            return;
        }

        var softMaskSource = _state.SoftMask;
        var resolvedSoftMask = _page.Document.Resolve(softMaskSource) ?? softMaskSource;
        Excise.Core.Primitives.PdfObject maskLookupObject = resolvedSoftMask;
        if (resolvedSoftMask is Excise.Core.Primitives.PdfDictionary softMaskDictionary)
        {
            var smaskMode = softMaskDictionary.GetNameOrNull("S");
            if (string.Equals(smaskMode, "None", StringComparison.Ordinal))
            {
                _state.SoftMask = null;
                drawAction();
                return;
            }

            var softMaskStreamObj = softMaskDictionary.GetOptional("G");
            if (softMaskStreamObj == null)
            {
                drawAction();
                return;
            }

            resolvedSoftMask = _page.Document.Resolve(softMaskStreamObj) ?? softMaskStreamObj;
            maskLookupObject = softMaskStreamObj;
        }

        if (resolvedSoftMask is not Excise.Core.Primitives.PdfStream maskStream)
        {
            drawAction();
            return;
        }

        // Soft-mask compositing is the same as images: draw content in an
        // offscreen layer, then apply mask luminance with DstIn.
        if (!TryGetLayerBounds(preferredBounds, out var maskBounds))
        {
            drawAction();
            return;
        }

        var (maskWidth, maskHeight) = EstimateSoftMaskBitmapSize(maskBounds);
        using var maskBitmap = DecodeSoftMaskBitmap(
            maskLookupObject,
            maskStream,
            maskWidth,
            maskHeight,
            maskBounds);

        if (maskBitmap == null)
        {
            drawAction();
            return;
        }

        using var layerPaint = new SKPaint
        {
            BlendMode = sourcePaint.BlendMode,
            Color = sourcePaint.Color,
            IsAntialias = sourcePaint.IsAntialias
        };

        _canvas.SaveLayer(maskBounds, layerPaint);
        try
        {
            if (seedBackdrop && _rootBitmap != null)
            {
                _canvas.Save();
                _canvas.ResetMatrix();
                using var backdropPaint = new SKPaint
                {
                    BlendMode = SKBlendMode.Src,
                    IsAntialias = false
                };
                _canvas.DrawBitmap(_rootBitmap, 0, 0, backdropPaint);
                _canvas.Restore();
            }
            drawAction();
            using var lumaFilter = SKColorFilter.CreateLumaColor();
            using var maskPaint = new SKPaint
            {
                BlendMode = SKBlendMode.DstIn,
                ColorFilter = lumaFilter,
                IsAntialias = _options.AntiAlias
            };
            _canvas.DrawBitmap(maskBitmap, maskBounds, maskPaint);
        }
        finally
        {
            _canvas.Restore();
        }
    }

    private static SKBlendMode MapBlendMode(string pdfName) => pdfName switch
    {
        "Multiply"   => SKBlendMode.Multiply,
        "Screen"     => SKBlendMode.Screen,
        "Overlay"    => SKBlendMode.Overlay,
        "Darken"     => SKBlendMode.Darken,
        "Lighten"    => SKBlendMode.Lighten,
        "ColorDodge" => SKBlendMode.ColorDodge,
        "ColorBurn"  => SKBlendMode.ColorBurn,
        "HardLight"  => SKBlendMode.HardLight,
        "SoftLight"  => SKBlendMode.SoftLight,
        "Difference" => SKBlendMode.Difference,
        "Exclusion"  => SKBlendMode.Exclusion,
        "Hue"        => SKBlendMode.Hue,
        "Saturation" => SKBlendMode.Saturation,
        "Color"      => SKBlendMode.Color,
        "Luminosity" => SKBlendMode.Luminosity,
        _            => SKBlendMode.SrcOver,
    };

    #endregion

    #region Clipping Path (W, W* operators) - Issue #295

    private void SetClippingPath(bool evenOdd)
    {
        if (_currentPath == null)
        {
            _pendingClipEvenOdd = evenOdd;
            return;
        }

        _currentPath.FillType = evenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding;

        // Apply the clipping path to the canvas
        _canvas.ClipPath(_currentPath, SKClipOperation.Intersect, _options.AntiAlias);

        // Note: The path is NOT disposed here - it will be used by the following
        // path-painting operator (like n, S, f) which will dispose it
    }

    private void ApplyPendingClipToCurrentPath()
    {
        if (!_pendingClipEvenOdd.HasValue || _currentPath == null)
            return;

        _currentPath.FillType = _pendingClipEvenOdd.Value
            ? SKPathFillType.EvenOdd
            : SKPathFillType.Winding;
        _canvas.ClipPath(_currentPath, SKClipOperation.Intersect, _options.AntiAlias);
        _pendingClipEvenOdd = null;
    }

    #endregion

    #region Inline Images (BI, ID, EI operators) - Issue #297

    private void RenderInlineImage(PdfDictionary imageParams, byte[] dataBytes)
    {
        var dict = NormalizeInlineImageDictionary(imageParams);
        if (!dict.ContainsKey("Width") || !dict.ContainsKey("Height"))
            return;

        // Inline image data may be filter-encoded the same way an
        // image XObject's stream is (FlateDecode for raw RGB, DCTDecode
        // for JPEG, etc.). Build a synthetic PdfStream so the existing
        // RenderImageXObject pipeline handles colour-space resolution
        // and rasterization uniformly.
        var stream = new PdfStream(dict, dataBytes);
        try
        {
            // PdfStream constructed in-process has no cached decoded
            // bytes; PdfStream.DecodedData throws InvalidOperationException
            // until something runs the filter chain. Run it now —
            // RenderImageXObject reads DecodedData when the filter is
            // FlateDecode / RunLength / etc., and EncodedData when it's
            // DCTDecode / JPXDecode (JPEG path stays pass-through).
            if (stream.IsFiltered)
                new Excise.Core.Parsing.StreamDecompressor().Decompress(stream);
            RenderImageXObject(stream);
        }
        catch
        {
            // Single bad inline image shouldn't kill the page.
        }
    }

    private static PdfDictionary NormalizeInlineImageDictionary(PdfDictionary imageParams)
    {
        var dict = new PdfDictionary();
        foreach (var (keyObj, value) in imageParams)
        {
            var key = NormalizeInlineKey(keyObj.Value);
            dict[key] = NormalizeInlineImageValue(key, value);
        }
        return dict;
    }

    private static PdfObject NormalizeInlineImageValue(string key, PdfObject value)
    {
        if (value is PdfName name)
        {
            var expanded = key switch
            {
                "Filter" => ExpandInlineFilter(name.Value),
                "ColorSpace" => ExpandInlineColorSpace(name.Value),
                _ => name.Value,
            };
            return new PdfName(expanded);
        }

        if (value is PdfArray array)
        {
            var normalized = new PdfArray();
            foreach (var item in array)
                normalized.Add(NormalizeInlineImageValue(key, item));
            return normalized;
        }

        if (value is PdfDictionary dict)
        {
            var normalized = new PdfDictionary();
            foreach (var (childKey, childValue) in dict)
                normalized[childKey] = NormalizeInlineImageValue(childKey.Value, childValue);
            return normalized;
        }

        return value;
    }

    /// <summary>
    /// Per Table 91, inline image dicts may use one-or-two-letter
    /// abbreviations in place of the full names — normalize to full so
    /// downstream code (which expects /Width, /Filter, etc.) works.
    /// </summary>
    private static string NormalizeInlineKey(string abbr) => abbr switch
    {
        "W"   => "Width",
        "H"   => "Height",
        "CS"  => "ColorSpace",
        "BPC" => "BitsPerComponent",
        "F"   => "Filter",
        "DP"  => "DecodeParms",
        "D"   => "Decode",
        "IM"  => "ImageMask",
        "I"   => "Interpolate",
        "L"   => "Length",
        _     => abbr,
    };

    private static string ExpandInlineFilter(string abbr) => abbr switch
    {
        "A"   => "ASCIIHexDecode",
        "AHx" => "ASCIIHexDecode",
        "A85" => "ASCII85Decode",
        "LZW" => "LZWDecode",
        "Fl"  => "FlateDecode",
        "RL"  => "RunLengthDecode",
        "CCF" => "CCITTFaxDecode",
        "DCT" => "DCTDecode",
        _     => abbr,
    };

    private static string ExpandInlineColorSpace(string abbr) => abbr switch
    {
        "G"    => "DeviceGray",
        "RGB"  => "DeviceRGB",
        "CMYK" => "DeviceCMYK",
        "I"    => "Indexed",
        _      => abbr,
    };

    #endregion

    private static double ParseNumber(string s)
    {
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            return result;
        return 0;
    }

}
