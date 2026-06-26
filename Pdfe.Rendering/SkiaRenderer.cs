using System.Globalization;
using System.Text;
using System.Threading;
using BitMiracle.LibJpeg.Classic;
using Pdfe.Core.ColorSpaces;
using Pdfe.Core.Content;
using Pdfe.Core.Document;
using Pdfe.Core.Filters.Jpx;
using Pdfe.Core.Primitives;
using Pdfe.Core.Text;
using Pdfe.Rendering.Fonts;
using SkiaSharp;
using CoreCffParser = Pdfe.Core.Fonts.CffParser;

namespace Pdfe.Rendering;

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
        int rot = ((page.Rotation % 360) + 360) % 360;
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
        var context = new RenderContext(canvas, page, options, cancellationToken);
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

    internal static PdfRectangle ResolveEffectiveRenderBox(PdfPage page)
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
    private readonly PdfPage _page;
    private readonly RenderOptions _options;
    private readonly Stack<GraphicsState> _stateStack;
    private GraphicsState _state;
    private SKPath? _currentPath;
    private bool? _pendingClipEvenOdd;
    private SKPath? _pendingTextClipPath;
    private TextState _textState;
    private bool _inTextBlock;
    private SKTypeface? _currentTypeface;
    private string _currentFontEncoding;

    // Form-XObject recursion guards. PDF allows Form XObjects to invoke
    // each other via the `Do` operator, and a malformed file can have
    // a cycle (form A → form B → form A). Without protection that
    // recurses until the stack overflows and SIGABRTs the process —
    // observed on a pdf.js corpus fixture during the differential
    // run on 2026-05-01. The visited-set tracks the call stack so
    // a Do-cycle skips the recursive call; the depth counter is a
    // backstop for pathologically deep but acyclic nests.
    private readonly HashSet<Pdfe.Core.Primitives.PdfStream> _formXObjectStack =
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

    private sealed class MeshPatch
    {
        private MeshPatch(IReadOnlyList<SKPoint> points, SKColor[] colors)
        {
            Points = points;
            Colors = colors;
            MinX = points.Min(p => p.X);
            MaxX = points.Max(p => p.X);
            MinY = points.Min(p => p.Y);
            MaxY = points.Max(p => p.Y);
        }

        public IReadOnlyList<SKPoint> Points { get; }
        public SKColor[] Colors { get; }
        public double MinX { get; }
        public double MaxX { get; }
        public double MinY { get; }
        public double MaxY { get; }

        public static MeshPatch From(IReadOnlyList<SKPoint> points, SKColor[] colors) => new(points, colors);
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

    // Glyph widths parsed from the current font dictionary's /Widths array.
    // Null when unavailable (e.g. standard 14 fonts that omit /Widths), in which
    // case we fall back to Skia's MeasureText on the system typeface.
    private float[]? _currentFontWidths;
    private int _currentFontFirstChar;
    private float _currentFontMissingWidth;

    // Per-font character-code → Unicode map, built from /BaseEncoding +
    // /Differences when /Encoding is a dictionary. Null for the common case
    // of a simple name encoding (WinAnsiEncoding/MacRomanEncoding), in which
    // case DecodeTextBytes uses the raw codepage.
    private char[]? _currentCodeToUnicode;
    // Inverse map, populated whenever _currentCodeToUnicode is; lets
    // MeasurePdfAdvance go from Unicode text back to PDF byte codes for
    // indexing /Widths. When a Unicode char appears at multiple codes we
    // keep the first (lowest) to match the likely intent.
    private Dictionary<char, byte>? _currentUnicodeToCode;
    // Per-font character-code -> glyph-name table from the effective simple
    // font encoding. This preserves custom subset glyph names such as /g18
    // that do not have Unicode mappings but do exist in embedded CFF charsets.
    private string?[]? _currentCodeToGlyphName;
    private Pdfe.Core.Primitives.PdfDictionary? _currentFontDict;

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
    private readonly Dictionary<Pdfe.Core.Primitives.PdfDictionary, SKTypeface> _embeddedTypefaces = new();

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
    private readonly Dictionary<Pdfe.Core.Primitives.PdfDictionary, ushort[]?> _embeddedTypefaceByteToGlyph = new();
    private ushort[]? _currentByteToGlyph;

    // Stack of /Resources dictionaries currently active. The page's own
    // /Resources is the bottom; entering a Form XObject pushes its own
    // /Resources (or null when absent — we still push so push/pop pair).
    // Font and XObject lookups walk top-down, falling back to page
    // resources at the bottom of the stack. Without this, annotation
    // appearances and nested Form XObjects can't see the fonts and
    // images defined in their own /Resources, so text in /AP /N streams
    // either rendered with wrong fonts or not at all.
    private readonly Stack<Pdfe.Core.Primitives.PdfDictionary?> _resourcesStack = new();
    private readonly Stack<bool> _optionalContentVisibilityStack = new();
    private int _hiddenOptionalContentDepth;

    // Type0 / CID font state. Type0 fonts use 2-byte-per-character codes and
    // index a descendant font's /W array for widths (different format from the
    // simple-font /Widths). When _currentFontIsType0 is true, content-stream
    // bytes must be parsed 2 at a time and rendered via glyph ID, not Unicode.
    private bool _currentFontIsType0;
    private bool _currentFontIsType3;
    // True when /FontFile, /FontFile2, or /FontFile3 produced a usable
    // SKTypeface — i.e. Skia is rendering with the actual PDF font and
    // its MeasureText reports correct advances. False means we
    // substituted a system typeface; in that case PDF /Widths (if present)
    // are the source of truth for cursor advance, not Skia metrics.
    private bool _currentFontHasEmbeddedProgram;
    private Dictionary<int, float>? _currentCidWidths;
    private float _currentCidDefaultWidth = 1000f;
    private bool _currentCidUseUnicodeCmap;
    private CidCMap? _currentCidEncodingCMap;

    // CID → glyph-id mapping for the active CIDFontType2 font, when a
    // non-identity /CIDToGIDMap stream is present. ushort.MaxValue at
    // index N means "this CID is unmapped". Null means /CIDToGIDMap is
    // absent or /Identity, so CID == GID and RenderCidBytes draws CIDs
    // straight. Subset CIDFontType2 fonts (NotoSans, Noto* family) ship
    // a stream remapping each used CID to its small subset glyph index;
    // skipping the remap draws .notdef for every glyph and the page
    // appears blank.
    private ushort[]? _currentCidToGidMap;

    // CID → glyph-index mapping for the active CIDFontType0 (CFF-based)
    // font, derived from the /ROS-marked CFF's charset table. Same
    // purpose as _currentCidToGidMap, but for CFF-keyed CID fonts where
    // the mapping lives inside the embedded CFF rather than alongside
    // it as /CIDToGIDMap. Subset Adobe-Japan1 fonts (KozMinPro,
    // YuMincho, Source Han Sans, etc.) reach this path; without it,
    // every CJK glyph resolves to .notdef and Japanese / Chinese /
    // Korean PDFs render as blank pages.
    private Dictionary<int, int>? _currentCffCidToGlyph;
    // Per-fontDict cache for the above, keyed the same way as
    // _embeddedTypefaces so two different /Font dicts with the same
    // resource name but different physical fonts don't collide.
    private readonly Dictionary<Pdfe.Core.Primitives.PdfDictionary, Dictionary<int, int>?> _embeddedCffCidToGlyph = new();
    private readonly Dictionary<Pdfe.Core.Primitives.PdfDictionary, CidCMap?> _type0EncodingCMaps = new();
    private readonly HashSet<Pdfe.Core.Primitives.PdfStream> _type3GlyphStack = new();
    private readonly Dictionary<(int ObjectNumber, int Generation, int TargetWidth, int TargetHeight), SoftMaskAlpha?> _softMaskAlphaByReference = new();
    private readonly Dictionary<Pdfe.Core.Primitives.PdfStream, Dictionary<(int TargetWidth, int TargetHeight), SoftMaskAlpha?>> _softMaskAlphaByStream =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(int ObjectNumber, int Generation, ImageBitmapCacheKey Key), SKBitmap?> _imageBitmapByReference = new();
    private readonly Dictionary<Pdfe.Core.Primitives.PdfStream, Dictionary<ImageBitmapCacheKey, SKBitmap?>> _imageBitmapByStream =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<SKBitmap> _cachedImageBitmaps = new();
    private int _tilingPatternDepth;

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
        CancellationToken cancellationToken = default)
    {
        _canvas = canvas;
        _page = page;
        _options = options;
        _cancellationToken = cancellationToken;
        _stateStack = new Stack<GraphicsState>();
        _state = new GraphicsState();
        _textState = new TextState();
        _inTextBlock = false;
        _currentFontEncoding = "WinAnsiEncoding"; // Default encoding

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
            if (contentBytes.Length > 0)
                ExecuteContentBytes(contentBytes);

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
                    _state.FillPatternName = null;
                }
                break;
            case "G":
                if (operands.Count >= 1)
                    _state.StrokeColor = GrayToColor(Number(operands, 0));
                break;

            // Color (RGB)
            case "rg":
                if (operands.Count >= 3)
                {
                    _state.FillColor = RgbToColor(
                        Number(operands, 0),
                        Number(operands, 1),
                        Number(operands, 2));
                    _state.FillPatternName = null;
                }
                break;
            case "RG":
                if (operands.Count >= 3)
                    _state.StrokeColor = RgbToColor(
                        Number(operands, 0),
                        Number(operands, 1),
                        Number(operands, 2));
                break;

            // Color (CMYK)
            case "k":
                if (operands.Count >= 4)
                {
                    _state.FillColor = CmykToColor(
                        Number(operands, 0),
                        Number(operands, 1),
                        Number(operands, 2),
                        Number(operands, 3));
                    _state.FillPatternName = null;
                }
                break;
            case "K":
                if (operands.Count >= 4)
                    _state.StrokeColor = CmykToColor(
                        Number(operands, 0),
                        Number(operands, 1),
                        Number(operands, 2),
                        Number(operands, 3));
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

            // Type 3 font operators (#301)
            case "d0":
                // Set glyph width - only affects metrics, not rendering
                break;
            case "d1":
                // Set glyph width and bounding box - only affects metrics
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
        var content = new ContentStreamParser(contentBytes, _page)
            .Parse(_cancellationToken);
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

    private Pdfe.Core.Primitives.PdfObject? ResolveMarkedContentPropertyObject(Pdfe.Core.Primitives.PdfObject propertyObject)
    {
        if (propertyObject is PdfName propertyName)
            return ResolvePropertyFromActiveResources(propertyName.Value);

        return propertyObject;
    }

    private bool IsOptionalContentObjectVisible(Pdfe.Core.Primitives.PdfObject optionalContentObject)
    {
        var resolved = _page.Document.Resolve(optionalContentObject);
        if (resolved is not Pdfe.Core.Primitives.PdfDictionary dict)
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

    private bool IsOptionalContentMembershipVisible(Pdfe.Core.Primitives.PdfDictionary membership)
    {
        var ocgsObj = membership.GetOptional("OCGs");
        if (ocgsObj == null)
            return true;

        var visibilities = new List<bool>();
        var resolvedOcgs = _page.Document.Resolve(ocgsObj);
        if (resolvedOcgs is Pdfe.Core.Primitives.PdfArray ocgArray)
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

    private bool IsOptionalContentGroupVisible(
        Pdfe.Core.Primitives.PdfObject ocgObject,
        Pdfe.Core.Primitives.PdfDictionary ocg)
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

    private Pdfe.Core.Primitives.PdfDictionary? GetOptionalContentDefaultConfig()
    {
        var ocPropsObj = _page.Document.Catalog.GetOptional("OCProperties");
        if (_page.Document.Resolve(ocPropsObj ?? PdfNull.Instance) is not Pdfe.Core.Primitives.PdfDictionary ocProps)
            return null;

        return _page.Document.Resolve(ocProps.GetOptional("D") ?? PdfNull.Instance)
            as Pdfe.Core.Primitives.PdfDictionary;
    }

    private bool IsOcgListed(
        Pdfe.Core.Primitives.PdfObject? listObject,
        Pdfe.Core.Primitives.PdfObject ocgObject,
        Pdfe.Core.Primitives.PdfDictionary ocg)
    {
        if (_page.Document.Resolve(listObject ?? PdfNull.Instance) is not Pdfe.Core.Primitives.PdfArray list)
            return false;

        foreach (var item in list)
        {
            if (ReferencesSameObject(item, ocgObject, ocg))
                return true;
        }

        return false;
    }

    private bool ReferencesSameObject(
        Pdfe.Core.Primitives.PdfObject item,
        Pdfe.Core.Primitives.PdfObject ocgObject,
        Pdfe.Core.Primitives.PdfDictionary ocg)
    {
        if (item is PdfReference itemRef && ocgObject is PdfReference ocgRef)
            return itemRef == ocgRef;

        if (item is PdfReference refItem &&
            ocg.ObjectNumber == refItem.ObjectNum &&
            ocg.GenerationNumber == refItem.Generation)
            return true;

        var resolvedItem = _page.Document.Resolve(item);
        if (resolvedItem is Pdfe.Core.Primitives.PdfDictionary itemDict)
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

    #region Text Rendering

    private void BeginText()
    {
        ClearPendingTextClipPath();
        _inTextBlock = true;
        _textState.Reset();
    }

    private void EndText()
    {
        ApplyPendingTextClipPath();
        _inTextBlock = false;
    }

    private void SetFont(string fontName, double fontSize)
    {
        // Remove leading / if present
        if (fontName.StartsWith("/"))
            fontName = fontName.Substring(1);

        _textState.FontName = fontName;
        _textState.FontSize = (float)fontSize;

        // Try to get the font from the active resources (innermost Form
        // XObject's /Resources, falling back to the page) to determine the
        // base font and encoding.
        var fontDict = ResolveFontFromActiveResources(fontName);
        var resolvedFont = PdfFontResolver.Resolve(fontName, fontDict, _page.Document);
        _currentFontDict = resolvedFont.Dictionary;
        _currentFontIsType3 = resolvedFont.IsType3;

        // /Encoding can be either a Name (e.g. /WinAnsiEncoding) or a Dictionary
        // with /BaseEncoding and /Differences. The dictionary form is how embedded
        // subset fonts remap small character codes to specific glyphs — without
        // handling it, text decodes as control characters and renders invisibly.
        // Must resolve the indirect reference; most real PDFs use `/Encoding N 0 R`.
        var encodingDict = resolvedFont.EncodingDictionary;
        var encodingName = resolvedFont.EncodingName;

        _currentFontEncoding = encodingName;
        _currentCodeToUnicode = null;
        _currentUnicodeToCode = null;
        _currentCodeToGlyphName = null;
        if (encodingDict != null)
        {
            BuildEncodingMaps(encodingDict, encodingName);
        }
        else if (_currentFontIsType3)
        {
            var map = BuildBaseEncodingTable(encodingName);
            _currentCodeToUnicode = map;
            _currentCodeToGlyphName = BuildBaseEncodingGlyphNameTable(map);
            _currentUnicodeToCode = BuildUnicodeToCodeMap(map);
        }

        // Parse the font's glyph width table FIRST. The CFF→OpenType wrapper
        // (called inside TryLoadEmbeddedTypeface below) reads these to build
        // hmtx — without populating them first, every embedded font would be
        // wrapped with stale widths from the previously-active font, producing
        // visibly wrong layout (mid-word gaps and overlaps).
        _currentFontWidths = resolvedFont.Widths;
        _currentFontFirstChar = resolvedFont.FirstChar;
        _currentFontMissingWidth = resolvedFont.MissingWidth;

        // Prefer a typeface loaded from the PDF's own embedded font stream
        // (/FontFile = Type 1, /FontFile2 = TrueType, /FontFile3 = OpenType/CFF).
        // When no embedded data is present, fall through to the system-font mapping.
        var toUnicodeMap = resolvedFont.ToUnicodeMap;
        var embedded = TryLoadEmbeddedTypeface(fontDict, toUnicodeMap);
        _currentFontHasEmbeddedProgram = embedded != null;
        _currentTypeface = embedded ?? GetTypeface(resolvedFont.BaseFont);
        _currentByteToGlyph = embedded != null && fontDict != null
            && _embeddedTypefaceByteToGlyph.TryGetValue(fontDict, out var btg)
            ? btg : null;
        _currentCffCidToGlyph = embedded != null && fontDict != null
            && _embeddedCffCidToGlyph.TryGetValue(fontDict, out var cffMap)
            ? cffMap : null;

        // Type0 (composite CID) fonts need a completely different content-stream
        // parse (2 bytes per character, widths indexed via /W not /Widths).
        _currentFontIsType0 = resolvedFont.IsType0;
        _currentCidWidths = null;
        _currentCidDefaultWidth = 1000f;
        _currentCidToGidMap = null;
        _currentCidUseUnicodeCmap = false;
        _currentCidEncodingCMap = null;
        if (_currentFontIsType0 && fontDict != null)
        {
            _currentCidEncodingCMap = TryGetType0EncodingCMap(fontDict);

            var cidFont = PdfFontResolver.ResolveDescendantFont(resolvedFont, _page.Document);
            if (cidFont != null)
            {
                _currentCidDefaultWidth = (float)cidFont.GetNumber("DW", 1000);
                var w = ResolveArray(cidFont, "W");
                if (w != null)
                    _currentCidWidths = ParseWArray(w);

                // /CIDToGIDMap is /Identity (or absent) for most modern Type0
                // fonts and CID == GID. Subset CIDFontType2 fonts produced by
                // veraPDF Test Builder, Word, etc. ship a remapping stream
                // (2-byte big-endian uint16 per CID); without applying it,
                // glyph IDs miss every glyph in the subset and pages render
                // .notdef-only blanks.
                var cidToGidObj = cidFont.GetOptional("CIDToGIDMap");
                if (cidToGidObj != null)
                {
                    var resolved = _page.Document.Resolve(cidToGidObj);
                    if (resolved is Pdfe.Core.Primitives.PdfStream cidStream)
                    {
                        try
                        {
                            var data = cidStream.DecodedData;
                            int count = data.Length / 2;
                            var map = new ushort[count];
                            for (int i = 0; i < count; i++)
                                map[i] = (ushort)((data[i * 2] << 8) | data[i * 2 + 1]);
                            _currentCidToGidMap = map;
                        }
                        catch { _currentCidToGidMap = null; }
                    }
                }

                var toUnicodeName = fontDict.GetNameOrNull("ToUnicode");
                _currentCidUseUnicodeCmap =
                    _currentFontHasEmbeddedProgram &&
                    _currentCidToGidMap == null &&
                    _currentCidEncodingCMap == null &&
                    (toUnicodeName == "Identity-H" || toUnicodeName == "Identity-V");
            }
        }
    }

    private CidCMap? TryGetType0EncodingCMap(Pdfe.Core.Primitives.PdfDictionary fontDict)
    {
        if (_type0EncodingCMaps.TryGetValue(fontDict, out var cached))
            return cached;

        CidCMap? cmap = null;
        try
        {
            var encodingObj = fontDict.GetOptional("Encoding");
            if (encodingObj != null &&
                _page.Document.Resolve(encodingObj) is Pdfe.Core.Primitives.PdfStream stream)
            {
                cmap = CidCMap.Parse(stream.DecodedData);
            }
        }
        catch
        {
            cmap = null;
        }

        _type0EncodingCMaps[fontDict] = cmap;
        return cmap;
    }

    // Resolve `dict[key]` as a dictionary, following indirect references.
    // PdfDictionary.GetDictionaryOrNull does a direct type-check and misses the
    // common case where the value is a `N 0 R` reference — most FontDescriptor,
    // /Widths, and /Encoding entries in real PDFs are stored that way.
    private Pdfe.Core.Primitives.PdfDictionary? ResolveDict(
        Pdfe.Core.Primitives.PdfDictionary dict, string key)
    {
        var obj = dict.GetOptional(key);
        if (obj == null) return null;
        var resolved = _page.Document.Resolve(obj);
        return resolved as Pdfe.Core.Primitives.PdfDictionary;
    }

    private Pdfe.Core.Primitives.PdfArray? ResolveArray(
        Pdfe.Core.Primitives.PdfDictionary dict, string key)
    {
        var obj = dict.GetOptional(key);
        if (obj == null) return null;
        var resolved = _page.Document.Resolve(obj);
        return resolved as Pdfe.Core.Primitives.PdfArray;
    }

    private bool TryGetResolvedNumber(PdfObject? obj, out double value)
    {
        value = 0;
        if (obj == null)
            return false;

        try
        {
            var resolved = _page.Document.Resolve(obj);
            return resolved.TryGetNumber(out value);
        }
        catch
        {
            value = 0;
            return false;
        }
    }

    private bool TryGetArrayNumber(PdfArray? array, int index, out double value)
    {
        value = 0;
        return array != null &&
               index >= 0 &&
               index < array.Count &&
               TryGetResolvedNumber(array[index], out value);
    }

    private double ArrayNumberOrDefault(PdfArray? array, int index, double defaultValue = 0)
        => TryGetArrayNumber(array, index, out var value) ? value : defaultValue;

    private SKMatrix GetMatrix(PdfArray? array)
    {
        if (array == null || array.Count < 6)
            return new SKMatrix(1, 0, 0, 0, 1, 0, 0, 0, 1);

        return new SKMatrix(
            (float)ArrayNumberOrDefault(array, 0, 1),
            (float)ArrayNumberOrDefault(array, 2),
            (float)ArrayNumberOrDefault(array, 4),
            (float)ArrayNumberOrDefault(array, 1),
            (float)ArrayNumberOrDefault(array, 3, 1),
            (float)ArrayNumberOrDefault(array, 5),
            0,
            0,
            1);
    }

    // Parse the /W array of a CIDFont (PDF spec 9.7.4.3). Two forms are
    // interleaved in a single array:
    //   cid [w1 w2 w3 ...]     → assigns w1..wN to cid, cid+1, cid+2, ...
    //   cid_start cid_end w    → assigns w to every CID in [cid_start, cid_end]
    // Widths are in glyph units (1/1000 of the designed em).
    private static Dictionary<int, float> ParseWArray(Pdfe.Core.Primitives.PdfArray w)
    {
        var map = new Dictionary<int, float>();
        int i = 0;
        while (i < w.Count)
        {
            if (!IsNumber(w[i])) { i++; continue; }
            int cid = (int)w.GetNumber(i);
            i++;
            if (i >= w.Count) break;

            if (w[i] is Pdfe.Core.Primitives.PdfArray inner)
            {
                for (int j = 0; j < inner.Count; j++)
                    map[cid + j] = (float)inner.GetNumber(j);
                i++;
            }
            else if (IsNumber(w[i]) && i + 1 < w.Count && IsNumber(w[i + 1]))
            {
                int endCid = (int)w.GetNumber(i);
                float width = (float)w.GetNumber(i + 1);
                for (int c = cid; c <= endCid; c++)
                    map[c] = width;
                i += 2;
            }
            else
            {
                i++; // Malformed — skip and recover.
            }
        }
        return map;
    }

    private static bool IsNumber(Pdfe.Core.Primitives.PdfObject o) =>
        o is Pdfe.Core.Primitives.PdfInteger || o is Pdfe.Core.Primitives.PdfReal;

    // Load the font's embedded file (TrueType or OpenType/CFF) as an SKTypeface
    // so glyphs render in the face the PDF actually specifies, with the widths
    // and kerning the PDF's /Widths table was authored against. Cached per-
    // dict for the life of this RenderContext; disposed at end of Render().
    private SKTypeface? TryLoadEmbeddedTypeface(
        Pdfe.Core.Primitives.PdfDictionary? fontDict,
        IReadOnlyDictionary<int, string>? toUnicodeMap)
    {
        if (fontDict == null) return null;
        if (_embeddedTypefaces.TryGetValue(fontDict, out var cached))
            return cached;

        // Handle both simple and Type0 (CID) fonts: Type0 carries the embedded
        // file inside its /DescendantFonts[0]/FontDescriptor, not on itself.
        var descriptor = ResolveDict(fontDict, "FontDescriptor");
        if (descriptor == null)
        {
            var descendants = ResolveArray(fontDict, "DescendantFonts");
            if (descendants != null && descendants.Count > 0)
            {
                var descendantObj = _page.Document.Resolve(descendants[0]);
                if (descendantObj is Pdfe.Core.Primitives.PdfDictionary cidFontDict)
                    descriptor = ResolveDict(cidFontDict, "FontDescriptor");
            }
        }
        if (descriptor == null) return null;

        // /FontFile  (Type 1 PostScript) → SkiaSharp/FreeType loads PFA/PFB directly.
        // /FontFile2 (TrueType) → SkiaSharp loads directly.
        // /FontFile3 (OpenType/CFF) → if already SFNT-wrapped, Skia loads it;
        //   if it's raw Type1C/CIDFontType0C (more common in modern PDFs),
        //   we wrap it in a minimal OpenType container first.
        var ff1 = descriptor.GetOptional("FontFile");
        var ff2 = descriptor.GetOptional("FontFile2");
        var ff3 = descriptor.GetOptional("FontFile3");

        byte[]? fontBytes = null;
        bool isType1 = false;
        bool isCff = false;
        if (ff1 != null && _page.Document.Resolve(ff1) is Pdfe.Core.Primitives.PdfStream s1)
        {
            try { fontBytes = s1.DecodedData; } catch { }
            isType1 = fontBytes != null;
        }
        else if (ff2 != null && _page.Document.Resolve(ff2) is Pdfe.Core.Primitives.PdfStream s2)
        {
            try { fontBytes = s2.DecodedData; } catch { }
        }
        else if (ff3 != null && _page.Document.Resolve(ff3) is Pdfe.Core.Primitives.PdfStream s3)
        {
            try { fontBytes = s3.DecodedData; } catch { }
            var subtype = s3.GetNameOrNull("Subtype");
            // Type1C and CIDFontType0C are raw CFF without SFNT wrapper; OpenType
            // is already SFNT-wrapped and passes through.
            isCff = subtype == "Type1C" || subtype == "CIDFontType0C";
        }
        if (fontBytes == null || fontBytes.Length == 0) return null;

        // For raw CFF (Type1C / CIDFontType0C), synthesize an OpenType container
        // so Skia can load it. The wrapper's cmap has been independently verified
        // (CffWrapperTests.Wrapped_CffSkiaCanResolveKnownGlyphs) — Skia resolves
        // every Unicode char to the correct CFF glyph index.
        // For CID-keyed CFF (Adobe-Japan1 etc.) the wrapper produces a minimal
        // cmap and returns the CID → glyph index map via cffCidToGlyph; the
        // renderer threads that through SetFont so RenderCidBytes can dispatch
        // glyph IDs directly.
        byte[] loadableBytes = fontBytes;
        Dictionary<int, int>? cffCidToGlyph = null;
        ushort[]? cffByteToGlyph = null;
        ushort[]? type1ByteToGlyph = null;
        if (isCff)
        {
            var wrapped = TryWrapCffAsOpenType(fontBytes, fontDict, descriptor, out cffCidToGlyph, out cffByteToGlyph);
            if (wrapped != null) loadableBytes = wrapped;
        }
        else if (isType1)
        {
            type1ByteToGlyph = ShouldBuildType1ByteToGlyphMap(fontDict, _currentCodeToGlyphName)
                ? TryBuildType1ByteToGlyph(fontBytes, _currentCodeToGlyphName)
                : null;
        }

        SKTypeface? typeface;
        lock (_typefaceLoadLock)
        {
            try
            {
                using var data = SKData.CreateCopy(loadableBytes);
                typeface = SKTypeface.FromData(data);
            }
            catch { typeface = null; }

            if (typeface == null) return null;

            // Sanity-probe the wrapped font — for some CFF subsets (most commonly
            // dingbat fonts produced by the XEP toolchain) Skia loads our wrapper
            // and resolves the cmap, but its CFF interpreter finds no charstring
            // outlines and silently draws nothing. Detect that and fall back to
            // the system-font path so the user at least sees *some* glyph for the
            // codepoint instead of empty whitespace.
            if (isCff && !ProducesGlyphOutlines(typeface))
            {
                typeface.Dispose();
                return null;
            }
        }

        _embeddedTypefaces[fontDict] = typeface;
        _embeddedTypefaceByteToGlyph[fontDict] = cffByteToGlyph
            ?? type1ByteToGlyph
            ?? ResolveByteCodeCmap(typeface, fontDict, toUnicodeMap);
        _embeddedCffCidToGlyph[fontDict] = cffCidToGlyph;
        return typeface;
    }

    private static ushort[]? TryBuildType1ByteToGlyph(byte[] fontBytes, string?[]? pdfCodeToGlyphName)
    {
        try
        {
            var fontEncoding = ParseType1Encoding(fontBytes);
            var charStringNames = ParseType1CharStringNames(fontBytes);
            if (charStringNames.Count == 0)
                return null;

            var glyphNameToId = new Dictionary<string, ushort>(StringComparer.Ordinal);
            for (int i = 0; i < charStringNames.Count && i <= ushort.MaxValue; i++)
            {
                if (!glyphNameToId.ContainsKey(charStringNames[i]))
                    glyphNameToId[charStringNames[i]] = (ushort)i;
            }

            var sourceNames = HasAnyGlyphNames(pdfCodeToGlyphName)
                ? pdfCodeToGlyphName
                : fontEncoding;
            if (!HasAnyGlyphNames(sourceNames))
                return null;

            var map = new ushort[256];
            var mapped = 0;
            for (int code = 0; code < map.Length; code++)
            {
                var glyphName = sourceNames?[code];
                if (string.IsNullOrEmpty(glyphName))
                    continue;

                if (glyphNameToId.TryGetValue(glyphName, out var glyphId))
                {
                    map[code] = glyphId;
                    if (glyphId != 0)
                        mapped++;
                }
            }

            return mapped > 0 ? map : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasAnyGlyphNames(string?[]? names)
        => names != null && names.Any(static name => !string.IsNullOrEmpty(name));

    private static bool ShouldBuildType1ByteToGlyphMap(
        Pdfe.Core.Primitives.PdfDictionary fontDict,
        string?[]? pdfCodeToGlyphName)
    {
        if (HasAnyGlyphNames(pdfCodeToGlyphName))
            return false;

        var encodingName = fontDict.GetNameOrNull("Encoding");
        return encodingName != null &&
               encodingName is not "WinAnsiEncoding" and not "MacRomanEncoding" and not "StandardEncoding";
    }

    private static string?[]? ParseType1Encoding(byte[] fontBytes)
    {
        var eexec = IndexOfAscii(fontBytes, "eexec");
        var clearLength = eexec >= 0 ? eexec : fontBytes.Length;
        var clear = Encoding.Latin1.GetString(fontBytes, 0, clearLength);
        var names = new string?[256];
        var mapped = 0;
        var index = 0;
        while ((index = clear.IndexOf("dup", index, StringComparison.Ordinal)) >= 0)
        {
            var p = index + 3;
            SkipAsciiWhite(clear, ref p);
            if (!TryReadInt(clear, ref p, out var code) || code < 0 || code >= 256)
            {
                index += 3;
                continue;
            }

            SkipAsciiWhite(clear, ref p);
            if (p >= clear.Length || clear[p] != '/')
            {
                index += 3;
                continue;
            }

            p++;
            var start = p;
            while (p < clear.Length && IsPdfNameChar(clear[p]))
                p++;
            if (p == start)
            {
                index += 3;
                continue;
            }

            var glyphName = clear[start..p];
            SkipAsciiWhite(clear, ref p);
            if (p + 3 <= clear.Length && string.Equals(clear.AsSpan(p, Math.Min(3, clear.Length - p)).ToString(), "put", StringComparison.Ordinal))
            {
                names[code] = glyphName;
                mapped++;
            }

            index = p;
        }

        return mapped > 0 ? names : null;
    }

    private static List<string> ParseType1CharStringNames(byte[] fontBytes)
    {
        var decrypted = DecryptType1Eexec(fontBytes);
        if (decrypted == null || decrypted.Length == 0)
            return new List<string>();

        var text = Encoding.Latin1.GetString(decrypted);
        var charStrings = text.IndexOf("/CharStrings", StringComparison.Ordinal);
        if (charStrings < 0)
            return new List<string>();

        var names = new List<string>();
        var index = charStrings + "/CharStrings".Length;
        while ((index = text.IndexOf('/', index)) >= 0)
        {
            index++;
            if (index >= text.Length)
                break;

            var start = index;
            while (index < text.Length && IsPdfNameChar(text[index]))
                index++;
            if (index == start)
                continue;

            var glyphName = text[start..index];
            var p = index;
            SkipAsciiWhite(text, ref p);
            if (!TryReadInt(text, ref p, out _))
                continue;

            SkipAsciiWhite(text, ref p);
            if (!StartsType1CharStringOperator(text, p))
                continue;

            names.Add(glyphName);
        }

        return names;
    }

    private static byte[]? DecryptType1Eexec(byte[] fontBytes)
    {
        var eexec = IndexOfAscii(fontBytes, "eexec");
        if (eexec < 0)
            return null;

        var start = eexec + "eexec".Length;
        while (start < fontBytes.Length && IsAsciiWhite(fontBytes[start]))
            start++;
        if (start >= fontBytes.Length)
            return null;

        var encrypted = LooksLikeAsciiHex(fontBytes, start)
            ? ReadAsciiHexBytes(fontBytes, start)
            : fontBytes[start..];
        if (encrypted.Length <= 4)
            return null;

        var plain = DecryptType1Bytes(encrypted, 55665);
        return plain.Length > 4 ? plain[4..] : Array.Empty<byte>();
    }

    private static byte[] DecryptType1Bytes(byte[] encrypted, int seed)
    {
        const int c1 = 52845;
        const int c2 = 22719;
        var r = seed;
        var plain = new byte[encrypted.Length];
        for (int i = 0; i < encrypted.Length; i++)
        {
            var cipher = encrypted[i];
            plain[i] = (byte)(cipher ^ (r >> 8));
            r = ((cipher + r) * c1 + c2) & 0xffff;
        }

        return plain;
    }

    private static bool LooksLikeAsciiHex(byte[] data, int start)
    {
        var significant = 0;
        var hex = 0;
        for (int i = start; i < data.Length && significant < 16; i++)
        {
            var b = data[i];
            if (IsAsciiWhite(b))
                continue;

            significant++;
            if (IsAsciiHex(b))
                hex++;
        }

        return significant >= 8 && hex == significant;
    }

    private static byte[] ReadAsciiHexBytes(byte[] data, int start)
    {
        var nibbles = new List<int>();
        for (int i = start; i < data.Length; i++)
        {
            var b = data[i];
            if (IsAsciiWhite(b))
                continue;
            if (!TryHexValue(b, out var value))
                break;
            nibbles.Add(value);
        }

        if ((nibbles.Count & 1) == 1)
            nibbles.Add(0);

        var bytes = new byte[nibbles.Count / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)((nibbles[i * 2] << 4) | nibbles[i * 2 + 1]);
        return bytes;
    }

    private static int IndexOfAscii(byte[] data, string needle)
    {
        var bytes = Encoding.ASCII.GetBytes(needle);
        for (int i = 0; i <= data.Length - bytes.Length; i++)
        {
            var matched = true;
            for (int j = 0; j < bytes.Length; j++)
            {
                if (data[i + j] != bytes[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return i;
        }

        return -1;
    }

    private static bool StartsType1CharStringOperator(string text, int index)
    {
        if (index >= text.Length)
            return false;
        if (text[index] == '-')
            return true;
        if (index + 2 <= text.Length && text.AsSpan(index, 2).SequenceEqual("RD"))
            return true;
        return false;
    }

    private static void SkipAsciiWhite(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
    }

    private static bool TryReadInt(string text, ref int index, out int value)
    {
        value = 0;
        var start = index;
        var sign = 1;
        if (index < text.Length && text[index] == '-')
        {
            sign = -1;
            index++;
        }

        var parsed = false;
        while (index < text.Length && char.IsDigit(text[index]))
        {
            parsed = true;
            value = checked(value * 10 + (text[index] - '0'));
            index++;
        }

        if (!parsed)
        {
            index = start;
            return false;
        }

        value *= sign;
        return true;
    }

    private static bool IsPdfNameChar(char c)
        => !char.IsWhiteSpace(c) && c is not '/' and not '[' and not ']' and not '<' and not '>' and not '(' and not ')';

    private static bool IsAsciiWhite(byte b)
        => b is 0x00 or 0x09 or 0x0a or 0x0c or 0x0d or 0x20;

    private static bool IsAsciiHex(byte b)
        => (b >= '0' && b <= '9') || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F');

    private static bool TryHexValue(byte b, out int value)
    {
        if (b >= '0' && b <= '9')
        {
            value = b - '0';
            return true;
        }
        if (b >= 'a' && b <= 'f')
        {
            value = b - 'a' + 10;
            return true;
        }
        if (b >= 'A' && b <= 'F')
        {
            value = b - 'A' + 10;
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Detect typefaces that need the byte-coded glyph-ID draw path because
    /// Skia's shaper can't read their cmap, and pre-compute the
    /// byte→glyphId lookup once.
    ///
    /// We probe Skia first: if <c>SKFont.GetGlyph((int)c)</c> resolves
    /// common Unicode codepoints to real glyphs, the font has Unicode
    /// coverage Skia can shape and we don't need the workaround. Otherwise
    /// we read any format-0 subtable from the cmap. Returns null when no
    /// override is needed (the common case for modern Type 0 / CID fonts
    /// with Identity-H or Unicode-mapped cmaps).
    /// </summary>
    private static ushort[]? ResolveByteCodeCmap(
        SKTypeface typeface,
        Pdfe.Core.Primitives.PdfDictionary? fontDict,
        IReadOnlyDictionary<int, string>? toUnicodeMap)
    {
        // Type0 (CID) fonts go through a separate draw path that already
        // walks bytes 2 at a time and resolves through the descendant font;
        // the format-0 workaround would double-encode.
        if (fontDict?.GetNameOrNull("Subtype") == "Type0") return null;

        var byteMap = CmapFormat0Table.TryRead(typeface);
        if (byteMap == null)
            return null;

        if (ToUnicodeMapsToMissingEmbeddedGlyphs(typeface, toUnicodeMap))
            return byteMap;

        using var probe = new SKFont(typeface, 12f);
        int[] unicodeProbe = { 'A', 'a', 'M', 'e', '0', ' ', 'i' };
        foreach (var cp in unicodeProbe)
        {
            if (probe.GetGlyph(cp) != 0)
            {
                // Skia can shape this font directly via its cmap.
                return null;
            }
        }

        // No Unicode coverage Skia can see — fall back to the format-0
        // subtable if present.
        return byteMap;
    }

    private static bool ToUnicodeMapsToMissingEmbeddedGlyphs(
        SKTypeface typeface,
        IReadOnlyDictionary<int, string>? toUnicodeMap)
    {
        if (toUnicodeMap == null || toUnicodeMap.Count == 0)
            return false;

        using var probe = new SKFont(typeface, 12f);
        foreach (var text in toUnicodeMap.Values)
        {
            foreach (var rune in text.EnumerateRunes())
            {
                if (Rune.IsControl(rune) || Rune.IsWhiteSpace(rune))
                    continue;

                if (probe.GetGlyph(rune.Value) == 0)
                    return true;
            }
        }

        return false;
    }

    private static bool ProducesGlyphOutlines(SKTypeface typeface)
    {
        // Sample up to 16 evenly-distributed glyph indices; if none have an
        // outline, the CFF program is unreadable for our purposes.
        int n = typeface.GlyphCount;
        if (n <= 1) return false;
        int probes = Math.Min(16, n - 1);
        int step = Math.Max(1, (n - 1) / probes);
        using var font = new SKFont(typeface, 100f);
        for (int i = 1; i <= probes; i++)
        {
            ushort gid = (ushort)Math.Min(n - 1, i * step);
            using var p = font.GetGlyphPath(gid);
            if (p != null && p.PointCount > 0) return true;
        }
        return false;
    }

    private byte[]? TryWrapCffAsOpenType(
        byte[] cff,
        Pdfe.Core.Primitives.PdfDictionary fontDict,
        Pdfe.Core.Primitives.PdfDictionary descriptor,
        out Dictionary<int, int>? cffCidToGlyph,
        out ushort[]? cffByteToGlyph)
    {
        cffCidToGlyph = null;
        cffByteToGlyph = null;
        var cffInfo = CoreCffParser.Parse(cff);
        if (cffInfo == null) return null;

        var unicodeToGlyph = new Dictionary<char, int>(256);
        var glyphWidths = new Dictionary<int, ushort>(256);

        if (cffInfo.IsCidKeyed)
        {
            // CID-keyed CFF (Adobe-Japan1 / Adobe-CNS1 / Adobe-Korea1). The
            // CFF charset stores CIDs, not glyph names, so the AdobeGlyphList
            // path doesn't apply — there's no Unicode → name → glyph chain
            // to walk. Skip cmap construction and rely on the renderer
            // dispatching glyphs via SKTextEncoding.GlyphId; CFF glyph
            // ordering is preserved by the wrapper, so the OpenType glyph
            // index Skia ultimately uses == the CFF glyph index ==
            // CidToGlyph[cid] from the descendant font's CFF.
            cffCidToGlyph = cffInfo.CidToGlyph;
        }
        else
        {
            cffByteToGlyph = BuildCffSimpleByteToGlyph(cffInfo);

            // Build Unicode → glyph-index map and glyph-index → PDF-width map.
            // Both derive from walking the PDF's character codes 0..255, resolving
            // each to (Unicode, glyph name) and then looking up the glyph index in
            // the CFF charset.
            for (int code = 0; code < 256; code++)
            {
                char unicode = GetUnicodeForCode((byte)code);
                if (unicode == '\0') continue;
                if (!AdobeGlyphList.TryGetName(unicode, out var glyphName)) continue;
                if (!cffInfo.GlyphNameToIndex.TryGetValue(glyphName, out var glyphIndex)) continue;

                if (!unicodeToGlyph.ContainsKey(unicode))
                    unicodeToGlyph[unicode] = glyphIndex;

                // If /Widths covers this code, use it as the per-glyph hmtx width.
                if (_currentFontWidths != null)
                {
                    int idx = code - _currentFontFirstChar;
                    if (idx >= 0 && idx < _currentFontWidths.Length)
                        glyphWidths[glyphIndex] = (ushort)Math.Clamp(_currentFontWidths[idx], 0, 65535);
                }
            }

            if (cffByteToGlyph != null && _currentFontWidths != null)
            {
                for (int code = 0; code < cffByteToGlyph.Length; code++)
                {
                    int glyphIndex = cffByteToGlyph[code];
                    if (glyphIndex == 0) continue;

                    int idx = code - _currentFontFirstChar;
                    if (idx >= 0 && idx < _currentFontWidths.Length)
                        glyphWidths[glyphIndex] = (ushort)Math.Clamp(_currentFontWidths[idx], 0, 65535);
                }
            }
        }

        short xMin = cffInfo.XMin, yMin = cffInfo.YMin, xMax = cffInfo.XMax, yMax = cffInfo.YMax;
        var bbox = ResolveArray(descriptor, "FontBBox");
        if (bbox != null && bbox.Count >= 4)
        {
            xMin = (short)bbox.GetNumber(0);
            yMin = (short)bbox.GetNumber(1);
            xMax = (short)bbox.GetNumber(2);
            yMax = (short)bbox.GetNumber(3);
        }

        var info = new Fonts.CffToOpenType.PdfFontInfo
        {
            PsName = descriptor.GetNameOrNull("FontName")
                     ?? fontDict.GetNameOrNull("BaseFont")
                     ?? "Unknown",
            XMin = xMin, YMin = yMin, XMax = xMax, YMax = yMax,
            Ascent = (short)descriptor.GetNumber("Ascent", 800),
            Descent = (short)descriptor.GetNumber("Descent", -200),
            WeightClass = (ushort)Math.Clamp((int)descriptor.GetNumber("FontWeight", 400), 1, 1000),
            UnicodeToGlyph = unicodeToGlyph,
            GlyphWidths = glyphWidths.Count > 0 ? glyphWidths : null,
        };

        return Fonts.CffToOpenType.Wrap(cff, cffInfo.NumGlyphs, info);
    }

    private ushort[]? BuildCffSimpleByteToGlyph(CoreCffParser.CffFontInfo cffInfo)
    {
        if (_currentCodeToGlyphName == null) return null;

        var map = new ushort[256];
        var mapped = 0;
        for (int code = 0; code < map.Length; code++)
        {
            var glyphName = _currentCodeToGlyphName[code];
            if (string.IsNullOrEmpty(glyphName)) continue;

            if (cffInfo.GlyphNameToIndex.TryGetValue(glyphName, out var glyphIndex))
            {
                map[code] = (ushort)Math.Clamp(glyphIndex, 0, ushort.MaxValue);
                if (glyphIndex != 0) mapped++;
            }
        }

        return mapped > 0 ? map : null;
    }

    // Decode a raw PDF character code to its Unicode char under the current
    // font's encoding. Prefers the /Differences-derived map when present,
    // otherwise falls back to the named base encoding (WinAnsi/MacRoman).
    private char GetUnicodeForCode(byte code)
    {
        if (_currentCodeToUnicode != null)
            return _currentCodeToUnicode[code];
        if (_currentFontEncoding == "ZapfDingbatsEncoding")
            return ZapfDingbatsEncodingTable[code];

        var encoding = _currentFontEncoding == "MacRomanEncoding"
            ? Encoding.GetEncoding(10000)
            : Encoding.GetEncoding(1252);
        var s = encoding.GetString(new[] { code });
        return s.Length > 0 ? s[0] : '\0';
    }

    // Build code→Unicode (and inverse) tables for a font whose /Encoding is a
    // dictionary. Seeds from the named base encoding (WinAnsi/MacRoman), then
    // overlays entries from the /Differences array. Per PDF spec 9.6.5:
    // Differences is a sequence of numbers (starting code) and names (glyph
    // names), e.g. [32 /space /exclam /quotedbl 39 /quoteright].
    private void BuildEncodingMaps(Pdfe.Core.Primitives.PdfDictionary encodingDict, string baseEncoding)
    {
        var map = BuildBaseEncodingTable(baseEncoding);
        var glyphNames = BuildBaseEncodingGlyphNameTable(map);

        var differences = ResolveArray(encodingDict, "Differences");
        if (differences != null)
        {
            int currentCode = 0;
            for (int i = 0; i < differences.Count; i++)
            {
                var item = differences[i];
                if (item is Pdfe.Core.Primitives.PdfName name)
                {
                    if (currentCode >= 0 && currentCode < 256)
                    {
                        glyphNames[currentCode] = name.Value;
                        map[currentCode] = AdobeGlyphList.TryGet(name.Value, out var ch)
                            ? ch
                            : '\0';
                    }
                    currentCode++;
                }
                else if (item is Pdfe.Core.Primitives.PdfInteger intNum)
                {
                    currentCode = (int)intNum.Value;
                }
                else if (item is Pdfe.Core.Primitives.PdfReal realNum)
                {
                    currentCode = (int)realNum.Value;
                }
            }
        }

        _currentCodeToUnicode = map;
        _currentCodeToGlyphName = glyphNames;
        _currentUnicodeToCode = BuildUnicodeToCodeMap(map);
    }

    private static Dictionary<char, byte> BuildUnicodeToCodeMap(char[] map)
    {
        var unicodeToCode = new Dictionary<char, byte>(256);
        for (int b = 0; b < 256; b++)
        {
            var c = map[b];
            if (c != '\0' && !unicodeToCode.ContainsKey(c))
                unicodeToCode[c] = (byte)b;
        }

        return unicodeToCode;
    }

    private static char[] BuildBaseEncodingTable(string encodingName)
    {
        if (encodingName == "ZapfDingbatsEncoding")
            return ZapfDingbatsEncodingTable.ToArray();

        var encoding = encodingName == "MacRomanEncoding"
            ? Encoding.GetEncoding(10000)
            : Encoding.GetEncoding(1252);

        var map = new char[256];
        var buffer = new byte[1];
        for (int b = 0; b < 256; b++)
        {
            buffer[0] = (byte)b;
            var decoded = encoding.GetString(buffer);
            map[b] = decoded.Length > 0 ? decoded[0] : '\0';
        }
        return map;
    }

    private static readonly char[] ZapfDingbatsEncodingTable = BuildZapfDingbatsEncodingTable();

    private static char[] BuildZapfDingbatsEncodingTable()
    {
        var map = new char[256];
        ushort[] values =
        [
            0x0020, 0x2701, 0x2702, 0x2703, 0x2704, 0x260E, 0x2706, 0x2707,
            0x2708, 0x2709, 0x261B, 0x261E, 0x270C, 0x270D, 0x270E, 0x270F,
            0x2710, 0x2711, 0x2712, 0x2713, 0x2714, 0x2715, 0x2716, 0x2717,
            0x2718, 0x2719, 0x271A, 0x271B, 0x271C, 0x271D, 0x271E, 0x271F,
            0x2720, 0x2721, 0x2722, 0x2723, 0x2724, 0x2725, 0x2726, 0x2727,
            0x2728, 0x2605, 0x2729, 0x272A, 0x272B, 0x272C, 0x272D, 0x272E,
            0x272F, 0x2730, 0x2731, 0x2732, 0x2733, 0x2734, 0x2735, 0x2736,
            0x2737, 0x2738, 0x2739, 0x273A, 0x273B, 0x273C, 0x273D, 0x273E,
            0x273F, 0x2740, 0x2741, 0x2742, 0x2743, 0x2744, 0x2745, 0x2746,
            0x2747, 0x2748, 0x2749, 0x274A, 0x274B, 0x25CF, 0x274D, 0x25A0,
            0x274F, 0x2750, 0x2751, 0x2752, 0x25B2, 0x25BC, 0x25C6, 0x2756,
            0x25D7, 0x2758, 0x2759, 0x275A, 0x275B, 0x275C, 0x275D, 0x275E,
        ];

        for (int i = 0; i < values.Length; i++)
            map[0x20 + i] = (char)values[i];

        ushort[] upperValues =
        [
            0x2761, 0x2762, 0x2763, 0x2764, 0x2765, 0x2766, 0x2767, 0x2663,
            0x2666, 0x2665, 0x2660, 0x2460, 0x2461, 0x2462, 0x2463, 0x2464,
            0x2465, 0x2466, 0x2467, 0x2468, 0x2469, 0x2776, 0x2777, 0x2778,
            0x2779, 0x277A, 0x277B, 0x277C, 0x277D, 0x277E, 0x277F, 0x2780,
            0x2781, 0x2782, 0x2783, 0x2784, 0x2785, 0x2786, 0x2787, 0x2788,
            0x2789, 0x278A, 0x278B, 0x278C, 0x278D, 0x278E, 0x278F, 0x2790,
            0x2791, 0x2792, 0x2793, 0x2794, 0x2192, 0x2194, 0x2195, 0x2798,
            0x2799, 0x279A, 0x279B, 0x279C, 0x279D, 0x279E, 0x279F, 0x27A0,
            0x27A1, 0x27A2, 0x27A3, 0x27A4, 0x27A5, 0x27A6, 0x27A7, 0x27A8,
            0x27A9, 0x27AA, 0x27AB, 0x27AC, 0x27AD, 0x27AE, 0x27AF, 0x0000,
            0x27B1, 0x27B2, 0x27B3, 0x27B4, 0x27B5, 0x27B6, 0x27B7, 0x27B8,
            0x27B9, 0x27BA, 0x27BB, 0x27BC, 0x27BD, 0x27BE,
        ];

        for (int i = 0; i < upperValues.Length; i++)
            map[0xA1 + i] = upperValues[i] == 0 ? '\0' : (char)upperValues[i];

        return map;
    }

    private static string?[] BuildBaseEncodingGlyphNameTable(char[] unicodeMap)
    {
        var glyphNames = new string?[256];
        for (int b = 0; b < glyphNames.Length; b++)
        {
            var c = unicodeMap[b];
            if (c != '\0' && AdobeGlyphList.TryGetName(c, out var glyphName))
                glyphNames[b] = glyphName;
        }

        return glyphNames;
    }

    // Effective font size applied to glyph drawing: raw Tf size scaled by the
    // text matrix's Y-scale (handles the common `1 Tf` + `s 0 0 s ... Tm` idiom).
    private float GetEffectiveFontSize()
    {
        var c = _textState.TextMatrixC;
        var d = _textState.TextMatrixD;
        var yScale = (float)Math.Sqrt(c * c + d * d);
        if (yScale < 1e-6f) yScale = 1f;
        return _textState.FontSize * yScale;
    }

    // Horizontal-to-vertical aspect ratio of the text matrix. Most PDFs use a
    // uniform Tm (X-scale == Y-scale) so this is 1. When they don't — e.g. a
    // condensed heading like SCOTUS's `14.2001 0 0 15 ... Tm` for SUPREME COURT
    // — glyphs must render horizontally squeezed by this ratio and advance
    // must scale by this ratio too, otherwise accumulated per-glyph error
    // shows up as mid-word gaps.
    private float GetTextMatrixXYRatio()
    {
        var a = _textState.TextMatrixA;
        var b = _textState.TextMatrixB;
        var c = _textState.TextMatrixC;
        var d = _textState.TextMatrixD;
        var xScale = (float)Math.Sqrt(a * a + b * b);
        var yScale = (float)Math.Sqrt(c * c + d * d);
        if (xScale < 1e-6f || yScale < 1e-6f) return 1f;
        return xScale / yScale;
    }

    private SKMatrix CreateTextRenderingMatrix(float x, float y, float horizontalScale, float ySign)
    {
        var a = _textState.TextMatrixA;
        var b = _textState.TextMatrixB;
        var c = _textState.TextMatrixC;
        var d = _textState.TextMatrixD;
        var yScale = (float)Math.Sqrt(c * c + d * d);
        if (yScale < 1e-6f)
            yScale = 1f;

        // The existing draw path handles the PDF-vs-Skia vertical glyph
        // direction through ySign. Preserve that behavior by removing only
        // the Tm.d sign from the normalized text-matrix Y basis here.
        var verticalSign = d >= 0 ? 1f : -1f;
        var basisA = a / yScale;
        var basisB = b / yScale;
        var basisC = c / (yScale * verticalSign);
        var basisD = d / (yScale * verticalSign);

        return new SKMatrix(
            basisA * horizontalScale,
            basisC * ySign,
            x,
            basisB * horizontalScale,
            basisD * ySign,
            y,
            0,
            0,
            1);
    }

    private void AdvanceTextMatrixX(float distance)
    {
        var a = _textState.TextMatrixA;
        var b = _textState.TextMatrixB;
        var xScale = (float)Math.Sqrt(a * a + b * b);
        if (xScale < 1e-6f)
        {
            _textState.TextMatrixE += distance;
            return;
        }

        _textState.TextMatrixE += distance * (a / xScale);
        _textState.TextMatrixF += distance * (b / xScale);
    }

    private SKTypeface GetTypeface(string baseFont)
    {
        // PDF subset fonts wear a 6-letter+'+' prefix (e.g. GFEDCB+MyriadPro-Semibold).
        // Strip it before matching — otherwise even "ZapfDingbats" subsets fall
        // through to Sans-Serif and the glyphs come out as missing-glyph boxes.
        var bareName = baseFont;
        if (bareName.Length >= 8 && bareName[6] == '+')
            bareName = bareName.Substring(7);

        var style = SKFontStyle.Normal;
        if ((bareName.Contains("Bold") || bareName.Contains("Medi"))
            && (bareName.Contains("Italic") || bareName.Contains("Oblique") || bareName.Contains("Ital")))
            style = SKFontStyle.BoldItalic;
        else if (bareName.Contains("Bold") || bareName.Contains("Semibold") || bareName.Contains("Medium") || bareName.Contains("Medi"))
            style = SKFontStyle.Bold;
        else if (bareName.Contains("Italic") || bareName.Contains("Oblique") || bareName.Contains("Ital"))
            style = SKFontStyle.Italic;

        // Match standard PDF base fonts. Allow both exact and prefix matches so
        // family-named subsets ("ZapfDingbatsStd", "MyriadPro-Semibold", etc.)
        // route to the right system substitute.
        string family;
        if (Starts(bareName, "Helvetica")
            || Starts(bareName, "Arial")
            || Starts(bareName, "NimbusSanL"))
            family = "Helvetica";
        else if (Starts(bareName, "Times")
                 || Starts(bareName, "NimbusRomNo9L")
                 || Starts(bareName, "Bookman"))
            family = "Times New Roman";
        else if (Starts(bareName, "Courier")
                 || Starts(bareName, "NimbusMonL")
                 || Starts(bareName, "CMTT"))
            family = "Courier New";
        else if (Starts(bareName, "Symbol"))
            family = "Symbol";
        else if (bareName.Contains("Dingbat") || bareName.Contains("Wingding"))
            return GetTypefaceWithGlyphCoverage(
                style,
                ['✔', '✘', '♠', '♥', '♦', '♣'],
                "Zapf Dingbats",
                "ZapfDingbats",
                "Apple Symbols",
                "Noto Sans Symbols2",
                "Noto Sans Symbols",
                "Segoe UI Symbol",
                "OpenSymbol",
                "Symbola",
                "DejaVu Sans");
        else if (IsCondensedFontName(bareName))
            return GetTypefaceWithGlyphCoverage(
                style,
                ['A', 'a', 'e', 'i', 'n', 't'],
                "Avenir Next Condensed",
                "Arial Narrow",
                "Helvetica Condensed",
                "Helvetica Neue Condensed",
                "Liberation Sans Narrow",
                "Nimbus Sans Narrow",
                "Noto Sans Condensed",
                "DejaVu Sans Condensed",
                "Arial");
        else
            family = "Sans-Serif";

        return GetTypefaceFromFamily(family, style);
    }

    private static bool IsCondensedFontName(string fontName)
        => fontName.Contains("Condensed", StringComparison.OrdinalIgnoreCase)
           || fontName.Contains("Compressed", StringComparison.OrdinalIgnoreCase)
           || fontName.Contains("Narrow", StringComparison.OrdinalIgnoreCase);

    private static SKTypeface GetTypefaceFromFamily(string family, SKFontStyle style)
    {
        lock (_typefaceLoadLock)
        {
            return SKTypeface.FromFamilyName(family, style) ?? SKTypeface.Default;
        }
    }

    private static SKTypeface GetTypefaceWithGlyphCoverage(
        SKFontStyle style,
        char[] requiredGlyphs,
        params string[] familyNames)
    {
        lock (_typefaceLoadLock)
        {
            foreach (var familyName in familyNames)
            {
                var typeface = SKTypeface.FromFamilyName(familyName, style);
                if (typeface == null)
                    continue;

                if (HasGlyphCoverage(typeface, requiredGlyphs))
                    return typeface;

                typeface.Dispose();
            }

            return SKTypeface.FromFamilyName("Sans-Serif", style) ?? SKTypeface.Default;
        }
    }

    private static bool HasGlyphCoverage(SKTypeface typeface, IReadOnlyList<char> chars)
    {
        using var font = new SKFont(typeface, 12f);
        foreach (var c in chars)
        {
            if (font.GetGlyph(c) == 0)
                return false;
        }

        return true;
    }

    private static bool Starts(string s, string prefix) =>
        s.StartsWith(prefix, StringComparison.Ordinal);

    private void TextMove(double tx, double ty)
    {
        // PDF spec 9.4.2: Td's (tx, ty) are in UNSCALED text space units; the
        // new text matrix is [1 0 0 1 tx ty] × TextLineMatrix. The translation
        // lives in the right-hand side, so after composition:
        //   new_e = a*tx + c*ty + e
        //   new_f = b*tx + d*ty + f
        // Previously we added tx/ty directly to device-space e/f, which under
        // any Tm scale (e.g. `1 Tf` + `10.02 0 0 10.02 Tm`) produced line
        // breaks ~10x too small and pulled subsequent text up under the
        // previous line.
        var a = _textState.TextMatrixA;
        var b = _textState.TextMatrixB;
        var c = _textState.TextMatrixC;
        var d = _textState.TextMatrixD;
        var dx = a * tx + c * ty;
        var dy = b * tx + d * ty;
        _textState.TextMatrixE = _textState.LineMatrixE + (float)dx;
        _textState.TextMatrixF = _textState.LineMatrixF + (float)dy;
        _textState.LineMatrixE = _textState.TextMatrixE;
        _textState.LineMatrixF = _textState.TextMatrixF;
    }

    private void SetTextMatrix(double a, double b, double c, double d, double e, double f)
    {
        _textState.TextMatrixA = (float)a;
        _textState.TextMatrixB = (float)b;
        _textState.TextMatrixC = (float)c;
        _textState.TextMatrixD = (float)d;
        _textState.TextMatrixE = (float)e;
        _textState.TextMatrixF = (float)f;
        _textState.LineMatrixE = (float)e;
        _textState.LineMatrixF = (float)f;
    }

    private void TextNewLine()
    {
        // T* operator: Move to start of next line using leading
        TextMove(0, -_textState.TextLeading);
    }

    private void ShowText(PdfString? text)
    {
        if (text == null || text.Bytes.Length == 0) return;
        ShowTextBytes(text.Bytes);
    }

    private void ShowTextBytes(byte[] bytes)
    {
        if (bytes.Length == 0) return;
        if (_currentFontIsType3)
            RenderType3Bytes(bytes);
        else if (_currentFontIsType0)
            RenderCidBytes(bytes);
        else
            RenderText(DecodeTextBytes(bytes), bytes);
    }

    private void ShowTextArray(PdfArray? array)
    {
        // TJ operator: array of strings and position adjustments.
        if (array == null)
            return;

        foreach (var operand in array)
        {
            if (operand is PdfString text)
            {
                ShowText(text);
            }
            else if (operand.TryGetNumber(out var adjustment))
            {
                // TJ position adjustment is in thousandths of text-space units,
                // which map to device-space X via the text matrix's X-scale
                // (not Y-scale). For non-uniform Tm (e.g. SCOTUS "SUPREME COURT"
                // with 14.2001/15 ratio), using yScale instead of xScale
                // compounds a ~6% per-glyph error into visible mid-word gaps.
                var effectiveSize = GetEffectiveFontSize();
                var xyRatio = GetTextMatrixXYRatio();
                var xOffset = (float)(-adjustment * effectiveSize / 1000.0) * xyRatio;
                AdvanceTextMatrixX(xOffset * _textState.HorizontalScale / 100.0f);
            }
        }
    }

    private void RenderText(string text, byte[]? sourceBytes = null)
    {
        if (!_inTextBlock || _currentTypeface == null)
            return;

        var effectiveSize = GetEffectiveFontSize();
        var xyRatio = GetTextMatrixXYRatio();
        var mode = _textState.RenderMode;
        var fillText = TextRenderModeFills(mode);
        var strokeText = TextRenderModeStrokes(mode);
        var clipText = TextRenderModeAddsClip(mode);

        // SkiaSharp 3 separated SKPaint and SKFont — draw calls now take
        // both arguments rather than a paint that wraps a font.
        using var font = CreateTextFont(_currentTypeface, effectiveSize);
        using var fillPaint = CreateTextPaint(SKPaintStyle.Fill, _state.FillColor, _state.FillAlpha);
        using var strokePaint = CreateTextPaint(SKPaintStyle.Stroke, _state.StrokeColor, _state.StrokeAlpha);
        using var measurePaint = new SKPaint { IsAntialias = _options.AntiAlias };
        using var strokeDash = CreateDashEffect();
        if (strokeDash != null)
            strokePaint.PathEffect = strokeDash;

        // Calculate position in PDF coordinates
        var x = _textState.TextMatrixE;
        var y = _textState.TextMatrixF + _textState.TextRise;

        // The canvas has been transformed with Scale(scale, -scale) to flip
        // Y for paths. Un-flip for text. When the text matrix has non-uniform
        // X/Y scaling, squeeze glyphs horizontally to match the X-scale.
        //
        // For Y direction, follow the *sign* of Tm.d:
        //   d > 0 (typical PDF, Y-up text-space) → -1 cancels outer Y-flip
        //   d < 0 (browser-style Tm `1 0 0 -1`, e.g. WeasyPrint, Word, Word-derived
        //         and most CJK-producing toolchains) → +1 keeps Skia's natural
        //         Y-down glyph drawing, which the outer flip turns into Y-up
        //         on screen exactly as the PDF intends.
        // Without this, browser-flipped text (and most CJK) renders upside-down.
        //
        // Per PDF 32000-2 §9.4.4 the text rendering matrix multiplies the
        // X axis by Th (= Tz / 100). Tz=100 is the default and the no-op,
        // but conformance fixtures use Tz=300, Tz=30000 etc. to test wide
        // / condensed text. Without folding Th into this Scale the glyph
        // itself is drawn at native width while the cursor advances by
        // Th×width, leaving microscopic letters with huge gaps between
        // them (visible as pdfe rendering only ~5% of the expected pixel
        // count on TWG A001 / 6-1-12-t02 fixtures).
        float ySign = _textState.TextMatrixD >= 0 ? -1f : 1f;
        float th = _textState.HorizontalScale / 100.0f;
        if (!IsOptionalContentSuppressed)
        {
            _canvas.Save();
            var textMatrix = CreateTextRenderingMatrix(x, y, th, ySign);
            _canvas.Concat(in textMatrix);

            bool drawWithPdfWidths =
                !_currentFontHasEmbeddedProgram &&
                _currentFontWidths != null &&
                sourceBytes != null &&
                text.Length == sourceBytes.Length;

            if (drawWithPdfWidths)
            {
                // Walk the bytes in lock-step with the decoded characters,
                // drawing each glyph at the cumulative PDF-/Widths position
                // *plus* the character/word spacing the PDF asked for.
                // Visible layout matches what the PDF author authored
                // against Times/Helvetica, regardless of the system font we
                // substituted for the actual glyphs.
                //
                // Per-glyph cursor advance after drawing byte b:
                //     /Widths[b]/1000 * fontSize    (intended glyph width)
                //   + Tc                             (character spacing)
                //   + (b == 0x20 ? Tw : 0)           (word spacing on space)
                //
                // Multiplied by the horizontal-scaling factor Tz (Th) per
                // PDF spec 9.4.4.
                //
                // We're inside a canvas that's already been scaled by xyRatio
                // for the X axis, so cursor is in the pre-xyRatio frame.
                // Tc / Tw are unscaled; we don't apply Tm's xScale here
                // because the canvas transform handles it.
                // Per-glyph advance per PDF spec 9.4.4:
                //   tx = (w0/1000 + Tc + (b == 0x20 ? Tw : 0)) * Tm_scale * Th
                // With Tf=1 and Tm scale = effectiveSize, Tm_scale = effectiveSize.
                // Multiplying everything together puts cursor in the canvas frame
                // we just set up with Scale(xyRatio, -1).
                // The outer Scale already folded Th into the canvas X axis, so
                // cursor advances in the *pre-Th* frame: (w/1000 + spacing) * Tfs.
                // Multiplying by Th again here would double-apply the horizontal
                // scale and over-shoot per-glyph spacing under any non-default Tz.
                float cursor = 0f;
                float tc = _textState.CharSpacing;
                float tw = _textState.WordSpacing;
                SKPath? localClipPath = clipText ? new SKPath() : null;
                for (int i = 0; i < sourceBytes!.Length; i++)
                {
                    var glyphText = text[i].ToString();
                    int idx = sourceBytes[i] - _currentFontFirstChar;
                    float w = idx >= 0 && idx < _currentFontWidths!.Length
                        ? _currentFontWidths[idx]
                        : _currentFontMissingWidth;
                    var pdfGlyphWidth = Math.Max(0f, (w / 1000f) * effectiveSize);
                    var naturalGlyphWidth = font.MeasureText(glyphText, measurePaint);
                    var fallbackGlyphScale = pdfGlyphWidth > 0f && naturalGlyphWidth > 0f
                        ? Math.Min(1f, pdfGlyphWidth / naturalGlyphWidth)
                        : 1f;

                    if (fillText)
                        RenderWithCurrentSoftMask(
                            () => DrawFallbackGlyph(glyphText, cursor, fallbackGlyphScale, font, fillPaint),
                            fillPaint);
                    if (strokeText)
                        RenderWithCurrentSoftMask(
                            () => DrawFallbackGlyph(glyphText, cursor, fallbackGlyphScale, font, strokePaint),
                            strokePaint);
                    if (localClipPath != null)
                    {
                        using var glyphPath = font.GetTextPath(glyphText, SKPoint.Empty);
                        if (glyphPath != null && !glyphPath.IsEmpty)
                        {
                            using var transformedGlyphPath = new SKPath();
                            var glyphMatrix = new SKMatrix(
                                fallbackGlyphScale, 0, cursor,
                                0, 1, 0,
                                0, 0, 1);
                            glyphPath.Transform(glyphMatrix, transformedGlyphPath);
                            localClipPath.AddPath(transformedGlyphPath, SKPathAddMode.Append);
                        }
                    }

                    float spacing = tc + (sourceBytes[i] == 0x20 ? tw : 0f);
                    cursor += (w / 1000f + spacing) * effectiveSize;
                }
                AddPendingTextClipPath(localClipPath, x, y, th, ySign);
                localClipPath?.Dispose();
            }
            else if (_currentByteToGlyph != null && sourceBytes != null)
            {
                // The active typeface's cmap is byte-coded (Mac Roman / format-0)
                // and Skia's shaper can't read it. Look each PDF byte code up in
                // the parsed cmap and dispatch via SKTextBlob with explicit
                // glyph IDs (SkiaSharp 3 dropped the DrawText(byte[], …)
                // overload — SKTextBlob is the supported entry point).
                // Without this branch every glyph would render as .notdef and
                // the page would be blank.
                var gids = BuildGlyphIds(sourceBytes, _currentByteToGlyph);
                using var blob = BuildGlyphBlob(gids, font);
                if (blob != null)
                {
                    if (fillText)
                        RenderWithCurrentSoftMask(
                            () => _canvas.DrawText(blob, 0, 0, fillPaint),
                            fillPaint);
                    if (strokeText)
                        RenderWithCurrentSoftMask(
                            () => _canvas.DrawText(blob, 0, 0, strokePaint),
                            strokePaint);
                }

                if (clipText)
                {
                    using var localClipPath = BuildGlyphIdTextPath(gids, font, measurePaint);
                    AddPendingTextClipPath(localClipPath, x, y, th, ySign);
                }
            }
            else
            {
                if (fillText)
                    RenderWithCurrentSoftMask(
                        () => _canvas.DrawText(text, 0, 0, font, fillPaint),
                        fillPaint);
                if (strokeText)
                    RenderWithCurrentSoftMask(
                        () => _canvas.DrawText(text, 0, 0, font, strokePaint),
                        strokePaint);
                if (clipText)
                {
                    using var localClipPath = font.GetTextPath(text, SKPoint.Empty);
                    AddPendingTextClipPath(localClipPath, x, y, th, ySign);
                }
            }

            _canvas.Restore();
        }

        // Advance the cursor by what the PDF *intended*, which is not
        // always what Skia just drew.
        //   - Embedded font program → Skia loaded the real font, its
        //     MeasureText is correct.
        //   - No embedded program but PDF supplies /Widths → trust the
        //     PDF's explicit widths; the substituted system typeface's
        //     metrics differ and would compound per-glyph drift into
        //     visible mid-word gaps (the birth-cert form is the canary).
        //   - Otherwise fall back to Skia's MeasureText.
        float widthInFontUnits;
        bool advanceFromPdfWidths =
            !_currentFontHasEmbeddedProgram &&
            _currentFontWidths != null &&
            sourceBytes != null;

        if (advanceFromPdfWidths)
        {
            widthInFontUnits = SumPdfWidths(sourceBytes!) * effectiveSize;
        }
        else if (_currentByteToGlyph != null && sourceBytes != null)
        {
            // Same byte-coded glyph-ID path as the draw branch above —
            // SkiaSharp 3 moved MeasureText off SKPaint, the glyph-id
            // overload now lives on SKFont.
            var gids = BuildGlyphIds(sourceBytes, _currentByteToGlyph);
            widthInFontUnits = font.MeasureText(new ReadOnlySpan<ushort>(gids), measurePaint);
        }
        else
        {
            widthInFontUnits = font.MeasureText(text, measurePaint);
        }

        var width = widthInFontUnits * xyRatio;
        var charCount = sourceBytes?.Length ?? text.Length;
        var spaceCount = sourceBytes != null
            ? sourceBytes.Count(b => b == 0x20)
            : text.Count(c => c == ' ');

        // PDF spec 9.4.4: Tc and Tw are in UNSCALED text space units. Scale by
        // the text matrix's X-scale before adding to device-space advance,
        // otherwise Tw-heavy layouts overlap themselves (birth-cert form).
        var tmA = _textState.TextMatrixA;
        var tmB = _textState.TextMatrixB;
        var xScale = (float)Math.Sqrt(tmA * tmA + tmB * tmB);
        if (xScale < 1e-6f) xScale = 1f;
        width += charCount * _textState.CharSpacing * xScale;
        width += spaceCount * _textState.WordSpacing * xScale;
        width *= _textState.HorizontalScale / 100.0f;

        AdvanceTextMatrixX(width);
    }

    private static bool TextRenderModeFills(int mode) => mode is 0 or 2 or 4 or 6;

    private static bool TextRenderModeStrokes(int mode) => mode is 1 or 2 or 5 or 6;

    private static bool TextRenderModeAddsClip(int mode) => mode is 4 or 5 or 6 or 7;

    private static SKFont CreateTextFont(SKTypeface typeface, float size)
    {
        return new SKFont(typeface, size)
        {
            Edging = SKFontEdging.Antialias,
            Hinting = SKFontHinting.Normal,
            LinearMetrics = true,
            Subpixel = true
        };
    }

    private void DrawFallbackGlyph(string glyphText, float cursor, float horizontalScale, SKFont font, SKPaint paint)
    {
        if (Math.Abs(horizontalScale - 1f) < 0.001f)
        {
            _canvas.DrawText(glyphText, cursor, 0, font, paint);
            return;
        }

        _canvas.Save();
        try
        {
            _canvas.Translate(cursor, 0);
            _canvas.Scale(horizontalScale, 1);
            _canvas.DrawText(glyphText, 0, 0, font, paint);
        }
        finally
        {
            _canvas.Restore();
        }
    }

    private SKPaint CreateTextPaint(SKPaintStyle style, SKColor color, float alpha)
    {
        var paint = new SKPaint
        {
            Style = style,
            Color = color.WithAlpha((byte)Math.Clamp(alpha * 255, 0, 255)),
            BlendMode = _state.BlendMode,
            IsAntialias = _options.AntiAlias
        };

        if (style == SKPaintStyle.Stroke)
        {
            paint.StrokeWidth = (float)_state.LineWidth;
            paint.StrokeCap = _state.LineCap switch
            {
                1 => SKStrokeCap.Round,
                2 => SKStrokeCap.Square,
                _ => SKStrokeCap.Butt
            };
            paint.StrokeJoin = _state.LineJoin switch
            {
                1 => SKStrokeJoin.Round,
                2 => SKStrokeJoin.Bevel,
                _ => SKStrokeJoin.Miter
            };
            paint.StrokeMiter = _state.MiterLimit;
        }

        return paint;
    }

    private void AddPendingTextClipPath(SKPath? localPath, float x, float y, float horizontalScale, float scaleY)
    {
        if (localPath == null || localPath.IsEmpty)
            return;

        var matrix = CreateTextRenderingMatrix(x, y, horizontalScale, scaleY);
        using var transformed = new SKPath();
        localPath.Transform(matrix, transformed);
        if (transformed.IsEmpty)
            return;

        _pendingTextClipPath ??= new SKPath();
        _pendingTextClipPath.AddPath(transformed, SKPathAddMode.Append);
    }

    private void ApplyPendingTextClipPath()
    {
        if (_pendingTextClipPath == null)
            return;

        using var clipPath = _pendingTextClipPath;
        _pendingTextClipPath = null;
        if (clipPath.IsEmpty)
            return;

        clipPath.FillType = SKPathFillType.Winding;
        _canvas.ClipPath(clipPath, SKClipOperation.Intersect, _options.AntiAlias);
    }

    private void ClearPendingTextClipPath()
    {
        _pendingTextClipPath?.Dispose();
        _pendingTextClipPath = null;
    }

    private static SKPath BuildGlyphIdTextPath(ushort[] gids, SKFont font, SKPaint measurePaint)
    {
        var path = new SKPath();
        if (gids.Length == 0)
            return path;

        var widths = font.GetGlyphWidths(new ReadOnlySpan<ushort>(gids), measurePaint);
        float cursor = 0f;
        for (int i = 0; i < gids.Length; i++)
        {
            using var glyphPath = font.GetGlyphPath(gids[i]);
            if (glyphPath != null && !glyphPath.IsEmpty)
                path.AddPath(glyphPath, cursor, 0, SKPathAddMode.Append);
            if (i < widths.Length)
                cursor += widths[i];
        }

        return path;
    }

    private static SKPath BuildGlyphIdTextPath(ushort[] gids, SKPoint[] positions, SKFont font)
    {
        var path = new SKPath();
        var count = Math.Min(gids.Length, positions.Length);
        for (int i = 0; i < count; i++)
        {
            using var glyphPath = font.GetGlyphPath(gids[i]);
            if (glyphPath != null && !glyphPath.IsEmpty)
                path.AddPath(glyphPath, positions[i].X, positions[i].Y, SKPathAddMode.Append);
        }

        return path;
    }

    /// <summary>
    /// Total advance for <paramref name="bytes"/> in the current simple
    /// font, expressed as a fraction of the font's em (multiply by font
    /// size to get points). Indexes <c>_currentFontWidths</c> by
    /// (byte − FirstChar); falls back to /MissingWidth or 0 for codes
    /// outside the table.
    /// </summary>
    private float SumPdfWidths(byte[] bytes)
    {
        if (_currentFontWidths == null || _currentFontWidths.Length == 0) return 0f;
        float total = 0f;
        var widths = _currentFontWidths;
        int firstChar = _currentFontFirstChar;
        for (int i = 0; i < bytes.Length; i++)
        {
            int idx = bytes[i] - firstChar;
            float w = idx >= 0 && idx < widths.Length
                ? widths[idx]
                : _currentFontMissingWidth;
            // PDF /Widths are in 1/1000 of em.
            total += w / 1000f;
        }
        return total;
    }

    // Map PDF byte codes through the format-0 cmap into glyph IDs.
    // Used for simple fonts whose embedded typeface has only a
    // format-0 cmap; the byte→glyph map was parsed once at
    // typeface-load time. SkiaSharp 3 routes glyph IDs through
    // SKTextBlob (BuildGlyphBlob below) — the v2 byte-array
    // SKTextEncoding.GlyphId path was removed.
    private static ushort[] BuildGlyphIds(byte[] sourceBytes, ushort[] byteToGlyph)
    {
        var gids = new ushort[sourceBytes.Length];
        for (int i = 0; i < sourceBytes.Length; i++)
            gids[i] = byteToGlyph[sourceBytes[i]];
        return gids;
    }

    /// <summary>
    /// Wrap a glyph-id array in an <see cref="SKTextBlob"/> for
    /// dispatch through <see cref="SKCanvas.DrawText(SKTextBlob,float,float,SKPaint)"/>.
    /// SkiaSharp 3 dropped <c>SKCanvas.DrawText(byte[], …)</c>, and
    /// <see cref="SKTextBlob.Create"/> has no <c>ReadOnlySpan&lt;ushort&gt;</c>
    /// overload — only <c>SKTextBlobBuilder.AddRun</c> takes glyph IDs.
    /// Origin (0, 0) since the caller has already concatenated the
    /// right Translate/Scale onto the canvas; per-glyph advance comes
    /// from the font's hmtx via the run's default positioning.
    /// </summary>
    private static SKTextBlob? BuildGlyphBlob(ushort[] gids, SKFont font)
    {
        if (gids.Length == 0) return null;
        using var builder = new SKTextBlobBuilder();
        builder.AddRun(new ReadOnlySpan<ushort>(gids), font, SKPoint.Empty);
        return builder.Build();
    }

    private static SKTextBlob? BuildPositionedGlyphBlob(ushort[] gids, SKPoint[] positions, SKFont font)
    {
        if (gids.Length == 0) return null;
        var count = Math.Min(gids.Length, positions.Length);
        if (count == 0) return null;
        using var builder = new SKTextBlobBuilder();
        builder.AddPositionedRun(
            new ReadOnlySpan<ushort>(gids, 0, count),
            font,
            new ReadOnlySpan<SKPoint>(positions, 0, count));
        return builder.Build();
    }

    private void RenderType3Bytes(byte[] bytes)
    {
        if (!_inTextBlock || _currentFontDict == null || bytes.Length == 0)
            return;

        var charProcs = ResolveDict(_currentFontDict, "CharProcs");
        var fontResources = ResolveDict(_currentFontDict, "Resources");
        var fontMatrix = GetType3FontMatrix(_currentFontDict);
        var canPaint = !IsOptionalContentSuppressed && _textState.RenderMode is not (3 or 7);
        var cursorTextUnits = 0f;
        var th = _textState.HorizontalScale / 100.0f;

        foreach (var code in bytes)
        {
            if (canPaint &&
                charProcs != null &&
                TryResolveType3CharProc(charProcs, code, out var charProc))
            {
                RenderType3Glyph(charProc, fontResources, fontMatrix, cursorTextUnits, th);
            }

            cursorTextUnits += GetType3TextSpaceAdvance(code, fontMatrix);
            cursorTextUnits += _textState.CharSpacing;
            if (code == 0x20)
                cursorTextUnits += _textState.WordSpacing;
        }

        var effectiveSize = GetEffectiveFontSize();
        var width = cursorTextUnits * effectiveSize * GetTextMatrixXYRatio() * th;
        AdvanceTextMatrixX(width);
    }

    private bool TryResolveType3CharProc(
        Pdfe.Core.Primitives.PdfDictionary charProcs,
        byte code,
        out Pdfe.Core.Primitives.PdfStream charProc)
    {
        charProc = null!;
        var glyphName = GetGlyphNameForCode(code);
        if (string.IsNullOrEmpty(glyphName))
            return false;

        var charProcObj = charProcs.GetOptional(glyphName);
        if (charProcObj == null)
            return false;

        if (_page.Document.Resolve(charProcObj) is not Pdfe.Core.Primitives.PdfStream stream)
            return false;

        charProc = stream;
        return true;
    }

    private string? GetGlyphNameForCode(byte code)
    {
        var glyphName = _currentCodeToGlyphName?[code];
        if (!string.IsNullOrEmpty(glyphName))
            return glyphName;

        var unicode = GetUnicodeForCode(code);
        return unicode != '\0' && AdobeGlyphList.TryGetName(unicode, out var name)
            ? name
            : null;
    }

    private SKMatrix GetType3FontMatrix(Pdfe.Core.Primitives.PdfDictionary fontDict)
    {
        var matrixArray = ResolveArray(fontDict, "FontMatrix");
        return matrixArray != null && matrixArray.Count >= 6
            ? GetMatrix(matrixArray)
            : new SKMatrix(0.001f, 0, 0, 0, 0.001f, 0, 0, 0, 1);
    }

    private float GetType3TextSpaceAdvance(byte code, SKMatrix fontMatrix)
    {
        var rawWidth = GetSimpleFontWidth(code);
        var fontMatrixX = Math.Abs(fontMatrix.ScaleX) > 1e-9f ? fontMatrix.ScaleX : 0.001f;
        return rawWidth * fontMatrixX;
    }

    private float GetSimpleFontWidth(byte code)
    {
        if (_currentFontWidths == null || _currentFontWidths.Length == 0)
            return _currentFontMissingWidth;

        var index = code - _currentFontFirstChar;
        return index >= 0 && index < _currentFontWidths.Length
            ? _currentFontWidths[index]
            : _currentFontMissingWidth;
    }

    private void RenderType3Glyph(
        Pdfe.Core.Primitives.PdfStream charProc,
        Pdfe.Core.Primitives.PdfDictionary? fontResources,
        SKMatrix fontMatrix,
        float cursorTextUnits,
        float horizontalScale)
    {
        if (!_type3GlyphStack.Add(charProc))
            return;

        var savedCanvasCount = _canvas.SaveCount;
        var savedStateStack = SnapshotGraphicsStateStack();
        var savedState = _state.Clone();
        var savedTextState = _textState.Clone();
        var savedFontState = SnapshotCurrentFontState();
        var savedInTextBlock = _inTextBlock;
        var savedCurrentPath = _currentPath;
        var savedPendingClipEvenOdd = _pendingClipEvenOdd;
        var savedPendingTextClipPath = _pendingTextClipPath;

        _currentPath = null;
        _pendingClipEvenOdd = null;
        _pendingTextClipPath = null;
        _inTextBlock = false;
        _canvas.Save();
        _resourcesStack.Push(fontResources);

        try
        {
            var textMatrix = new SKMatrix(
                _textState.TextMatrixA,
                _textState.TextMatrixC,
                _textState.TextMatrixE,
                _textState.TextMatrixB,
                _textState.TextMatrixD,
                _textState.TextMatrixF + _textState.TextRise,
                0,
                0,
                1);
            _canvas.Concat(in textMatrix);
            _canvas.Scale(_textState.FontSize * horizontalScale, _textState.FontSize);
            _canvas.Translate(cursorTextUnits, 0);
            _canvas.Concat(in fontMatrix);

            ExecuteContentBytes(charProc.DecodedData);
        }
        finally
        {
            _currentPath?.Dispose();
            _pendingTextClipPath?.Dispose();
            RestoreGraphicsStateStack(savedStateStack);
            _state = savedState;
            _textState = savedTextState;
            RestoreCurrentFontState(savedFontState);
            _inTextBlock = savedInTextBlock;
            _currentPath = savedCurrentPath;
            _pendingClipEvenOdd = savedPendingClipEvenOdd;
            _pendingTextClipPath = savedPendingTextClipPath;
            _resourcesStack.Pop();
            _canvas.RestoreToCount(savedCanvasCount);
            _type3GlyphStack.Remove(charProc);
        }
    }

    // Type0 rendering path. Content-stream bytes are character codes; the
    // active Encoding CMap maps them to CIDs. Identity-H/Identity-V are the
    // common 2-byte no-op maps, while embedded CMap streams can remap retained
    // Unicode-ish codes onto the descendant font's CID/glyph space.
    private void RenderCidBytes(byte[] bytes)
    {
        if (!_inTextBlock || _currentTypeface == null || bytes.Length == 0)
            return;

        var cids = _currentCidEncodingCMap?.Decode(bytes) ?? DecodeIdentityCidBytes(bytes);
        if (cids.Length == 0)
            return;

        var count = cids.Length;
        var effectiveSize = GetEffectiveFontSize();
        using var font = CreateTextFont(_currentTypeface, effectiveSize);
        // Two parallel arrays: CIDs (used for /W width lookup, which is
        // keyed by CID per spec) and GIDs (what Skia actually draws). The
        // CID → GID resolution depends on the descendant font subtype:
        //   - CIDFontType2 with /CIDToGIDMap stream → _currentCidToGidMap
        //     is the array indexed by CID (handles Word / NotoSans / Office
        //     subsets).
        //   - CIDFontType0 (CFF-keyed, Adobe-Japan1 etc.) → the mapping is
        //     inside the embedded CFF charset; CffCidToGlyph holds it.
        //   - CIDFontType2 with /CIDToGIDMap = /Identity (or absent) → CID
        //     equals GID and we draw straight through.
        var gids = new ushort[count];
        var positions = new SKPoint[count];
        var cursor = 0f;
        for (int i = 0; i < count; i++)
        {
            var cid = cids[i];
            ushort gid;
            if (_currentCidToGidMap != null && cid >= 0 && cid < _currentCidToGidMap.Length)
                gid = _currentCidToGidMap[cid];
            else if (_currentCffCidToGlyph != null
                     && _currentCffCidToGlyph.TryGetValue(cid, out var cffGid))
                gid = (ushort)cffGid;
            else if (_currentCidUseUnicodeCmap)
                gid = (ushort)(font.GetGlyph(cid) is var unicodeGid && unicodeGid != 0 ? unicodeGid : cid);
            else
                gid = ToGlyphId(cid);
            gids[i] = gid;

            positions[i] = new SKPoint(cursor, 0);
            cursor += GetCidWidthThousandths(cid) * effectiveSize / 1000f;
        }

        var xyRatio = GetTextMatrixXYRatio();
        var mode = _textState.RenderMode;
        var fillText = TextRenderModeFills(mode);
        var strokeText = TextRenderModeStrokes(mode);
        var clipText = TextRenderModeAddsClip(mode);

        // SkiaSharp 3: SKPaint no longer carries the font or text encoding;
        // SKTextBlob (built below) embeds glyph IDs natively, so DrawText
        // doesn't need a paint-side encoding hint anymore.
        using var fillPaint = CreateTextPaint(SKPaintStyle.Fill, _state.FillColor, _state.FillAlpha);
        using var strokePaint = CreateTextPaint(SKPaintStyle.Stroke, _state.StrokeColor, _state.StrokeAlpha);
        using var measurePaint = new SKPaint { IsAntialias = _options.AntiAlias };
        using var strokeDash = CreateDashEffect();
        if (strokeDash != null)
            strokePaint.PathEffect = strokeDash;

        // Match RenderText's Tm.d-aware Y-flip — without this, all CJK text
        // and any other content authored with a browser-style flipped Tm
        // (`1 0 0 -1`) renders upside-down.
        float ySign = _textState.TextMatrixD >= 0 ? -1f : 1f;
        float x = _textState.TextMatrixE;
        float y = _textState.TextMatrixF + _textState.TextRise;
        if (!IsOptionalContentSuppressed)
        {
            _canvas.Save();
            var textMatrix = CreateTextRenderingMatrix(x, y, _textState.HorizontalScale / 100.0f, ySign);
            _canvas.Concat(in textMatrix);

            // Build a glyph-id text blob — SkiaSharp 3 routes glyph IDs
            // through SKTextBlob (the v2 byte[] overload was removed). The
            // GID array was already remapped through /CIDToGIDMap (or
            // CFF charset) above, so we feed it straight in.
            using var blob = BuildPositionedGlyphBlob(gids, positions, font);
            if (blob != null)
            {
                if (fillText)
                    RenderWithCurrentSoftMask(
                        () => _canvas.DrawText(blob, 0, 0, fillPaint),
                        fillPaint);
                if (strokeText)
                    RenderWithCurrentSoftMask(
                        () => _canvas.DrawText(blob, 0, 0, strokePaint),
                        strokePaint);
            }

            if (clipText)
            {
                using var localClipPath = BuildGlyphIdTextPath(gids, positions, font);
                AddPendingTextClipPath(localClipPath, x, y, _textState.HorizontalScale / 100.0f, ySign);
            }

            _canvas.Restore();
        }

        // Advance by summed widths from /W (with /DW as fallback per CID).
        float sumThousandthsOfEm = 0f;
        foreach (var cid in cids)
            sumThousandthsOfEm += GetCidWidthThousandths(cid);
        var width = sumThousandthsOfEm * effectiveSize / 1000f * xyRatio;
        width *= _textState.HorizontalScale / 100.0f;
        AdvanceTextMatrixX(width);
    }

    private static int[] DecodeIdentityCidBytes(byte[] bytes)
    {
        var count = bytes.Length / 2;
        if (count == 0)
            return Array.Empty<int>();

        var cids = new int[count];
        for (var i = 0; i < count; i++)
            cids[i] = (bytes[i * 2] << 8) | bytes[i * 2 + 1];
        return cids;
    }

    private static ushort ToGlyphId(int cid)
        => cid is >= 0 and <= ushort.MaxValue ? (ushort)cid : (ushort)0;

    private float GetCidWidthThousandths(int cid)
        => (_currentCidWidths != null && _currentCidWidths.TryGetValue(cid, out var width))
            ? width
            : _currentCidDefaultWidth;

    // Returns the raw PDF string bytes WITHOUT decoding via encoding. Simple
    // fonts route these through DecodeTextBytes → Unicode → RenderText; Type0
    // fonts interpret the bytes directly as 2-byte CIDs via RenderCidBytes.
    private byte[] ParsePdfStringBytes(string operand)
    {
        if (string.IsNullOrEmpty(operand))
            return Array.Empty<byte>();

        // Literal string: (text)
        if (operand.StartsWith("(") && operand.EndsWith(")"))
            return UnescapePdfStringBytes(operand.Substring(1, operand.Length - 2));

        // Hex string: <hexdata>
        if (operand.StartsWith("<") && operand.EndsWith(">"))
            return DecodeHexStringBytes(operand.Substring(1, operand.Length - 2));

        return Encoding.Latin1.GetBytes(operand);
    }

    internal static byte[] UnescapePdfStringBytes(string s)
    {
        var unescaped = new List<byte>(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                var next = s[i + 1];
                switch (next)
                {
                    case 'n': unescaped.Add((byte)'\n'); i += 2; break;
                    case 'r': unescaped.Add((byte)'\r'); i += 2; break;
                    case 't': unescaped.Add((byte)'\t'); i += 2; break;
                    case 'b': unescaped.Add((byte)'\b'); i += 2; break;
                    case 'f': unescaped.Add((byte)'\f'); i += 2; break;
                    case '(': unescaped.Add((byte)'('); i += 2; break;
                    case ')': unescaped.Add((byte)')'); i += 2; break;
                    case '\\': unescaped.Add((byte)'\\'); i += 2; break;
                    case '\r':
                    case '\n':
                        // PDF spec 7.3.4.2: backslash followed by an EOL
                        // marker — both shall be ignored. Treat CRLF as a
                        // single EOL.
                        i += 2;
                        if (next == '\r' && i < s.Length && s[i] == '\n')
                            i++;
                        break;
                    default:
                        if (char.IsDigit(next))
                        {
                            var octal = "";
                            i++;
                            while (i < s.Length && octal.Length < 3 && char.IsDigit(s[i]) && s[i] < '8')
                                octal += s[i++];
                            unescaped.Add((byte)Convert.ToInt32(octal, 8));
                        }
                        else
                        {
                            // Backslash before unknown char — spec 7.3.4.2:
                            // backslash is ignored; emit just `next`.
                            unescaped.Add((byte)next);
                            i += 2;
                        }
                        break;
                }
            }
            else
            {
                // The content stream was decoded as Latin1, so char = byte.
                unescaped.Add((byte)s[i++]);
            }
        }
        return unescaped.ToArray();
    }

    private static byte[] DecodeHexStringBytes(string hex)
    {
        hex = new string(hex.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (hex.Length % 2 != 0) hex += "0";

        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, null, out var b))
                bytes[i] = b;
        }
        return bytes;
    }

    private string DecodeTextBytes(byte[] bytes)
    {
        // If the current font has an /Encoding dictionary, use the
        // /BaseEncoding + /Differences-derived map. Without this, embedded
        // subset fonts (which remap codes like 3 → "N", 4 → "A" via
        // /Differences) decode as control characters and render invisibly.
        if (_currentCodeToUnicode != null)
        {
            var sb = new StringBuilder(bytes.Length);
            foreach (var b in bytes)
            {
                var c = _currentCodeToUnicode[b];
                if (c != '\0') sb.Append(c);
            }
            return sb.ToString();
        }

        // Named-encoding fast path. WinAnsiEncoding = cp1252 is the default
        // for most modern PDFs.
        if (_currentFontEncoding == "ZapfDingbatsEncoding")
        {
            var sb = new StringBuilder(bytes.Length);
            foreach (var b in bytes)
            {
                var c = ZapfDingbatsEncodingTable[b];
                if (c != '\0') sb.Append(c);
            }

            return sb.ToString();
        }

        if (_currentFontEncoding == "MacRomanEncoding")
            return Encoding.GetEncoding(10000).GetString(bytes);
        return Encoding.GetEncoding(1252).GetString(bytes);
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

        if (extGState.ContainsKey("BM"))
        {
            var bm = extGState.GetNameOrNull("BM") ?? "Normal";
            _state.BlendMode = MapBlendMode(bm);
        }

        if (extGState.ContainsKey("SMask"))
        {
            var smaskObj = extGState.GetOptional("SMask");
            if (smaskObj is Pdfe.Core.Primitives.PdfName n && n.Value == "None")
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

    private void RenderWithCurrentSoftMask(Action drawAction, SKPaint sourcePaint, SKRect? preferredBounds = null)
    {
        if (_state.SoftMask == null)
        {
            drawAction();
            return;
        }

        var softMaskSource = _state.SoftMask;
        var resolvedSoftMask = _page.Document.Resolve(softMaskSource) ?? softMaskSource;
        Pdfe.Core.Primitives.PdfObject maskLookupObject = resolvedSoftMask;
        if (resolvedSoftMask is Pdfe.Core.Primitives.PdfDictionary softMaskDictionary)
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

        if (resolvedSoftMask is not Pdfe.Core.Primitives.PdfStream maskStream)
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

    #region XObject Rendering (Do operator)

    private void RenderXObject(string nameOperand)
    {
        // Remove leading / if present
        var name = nameOperand.TrimStart('/');
        var xobj = ResolveXObjectFromActiveResources(name);
        if (xobj == null)
            return;

        if (xobj is not Pdfe.Core.Primitives.PdfStream stream)
            return;

        if (stream.GetOptional("OC") is { } ocObject && !IsOptionalContentObjectVisible(ocObject))
            return;

        var subtype = stream.GetNameOrNull("Subtype");
        switch (subtype)
        {
            case "Image":
                RenderImageXObject(stream);
                break;
            case "Form":
                RenderFormXObjectAtInvocation(stream);
                break;
        }
    }

    private void RenderFormXObjectAtInvocation(Pdfe.Core.Primitives.PdfStream formStream)
    {
        var isTransparencyGroup = IsTransparencyGroupForm(formStream);
        if (!isTransparencyGroup)
        {
            RenderFormXObject(formStream);
            return;
        }

        var invocationState = _state.Clone();
        using var paint = new SKPaint
        {
            BlendMode = invocationState.BlendMode,
            Color = SKColors.White.WithAlpha((byte)Math.Clamp(invocationState.FillAlpha * 255, 0, 255)),
            IsAntialias = _options.AntiAlias
        };

        var layerBounds = GetFormInvocationBounds(formStream);
        void DrawFormContent()
        {
            var savedState = _state;
            try
            {
                _state = invocationState.Clone();
                _state.BlendMode = SKBlendMode.SrcOver;
                _state.FillAlpha = 1;
                _state.StrokeAlpha = 1;
                _state.SoftMask = null;

                RenderFormXObject(formStream);
            }
            finally
            {
                _state = savedState;
            }
        }

        if (invocationState.SoftMask != null)
        {
            var savedState = _state;
            try
            {
                _state = invocationState.Clone();
                RenderWithCurrentSoftMask(DrawFormContent, paint, layerBounds);
            }
            finally
            {
                _state = savedState;
            }
            return;
        }

        if (!TryGetLayerBounds(layerBounds, out var bounds))
        {
            DrawFormContent();
            return;
        }

        _canvas.SaveLayer(bounds, paint);
        try
        {
            DrawFormContent();
        }
        finally
        {
            _canvas.Restore();
        }
    }

    private bool IsTransparencyGroupForm(Pdfe.Core.Primitives.PdfStream formStream)
    {
        var groupObj = formStream.GetOptional("Group");
        if (groupObj == null)
            return false;

        var group = _page.Document.Resolve(groupObj) as Pdfe.Core.Primitives.PdfDictionary;
        return string.Equals(group?.GetNameOrNull("S"), "Transparency", StringComparison.Ordinal);
    }

    private SKRect? GetFormInvocationBounds(Pdfe.Core.Primitives.PdfStream formStream)
    {
        var bbox = ResolveArray(formStream, "BBox");
        if (bbox == null || bbox.Count < 4)
            return null;

        var bounds = new SKRect(
            (float)Math.Min(ArrayNumberOrDefault(bbox, 0), ArrayNumberOrDefault(bbox, 2)),
            (float)Math.Min(ArrayNumberOrDefault(bbox, 1), ArrayNumberOrDefault(bbox, 3)),
            (float)Math.Max(ArrayNumberOrDefault(bbox, 0), ArrayNumberOrDefault(bbox, 2)),
            (float)Math.Max(ArrayNumberOrDefault(bbox, 1), ArrayNumberOrDefault(bbox, 3)));
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return null;

        var matrix = GetMatrix(formStream.GetOptional("Matrix") as Pdfe.Core.Primitives.PdfArray);
        return MapRect(matrix, bounds);
    }

    private void RenderImageXObject(Pdfe.Core.Primitives.PdfStream imageStream)
    {
        var width = imageStream.GetInt("Width", 0);
        var height = imageStream.GetInt("Height", 0);
        if (width <= 0 || height <= 0)
            return;

        if (imageStream.GetBool("ImageMask") &&
            _state.FillPatternName != null &&
            TryDrawImageMaskWithPattern(imageStream, width, height))
        {
            return;
        }

        var bitsPerComponent = imageStream.GetInt("BitsPerComponent", 8);
        var colorSpace = imageStream.GetNameOrNull("ColorSpace") ?? "DeviceRGB";

        SKBitmap? mutableBitmap = null;
        try
        {
            var bitmap = GetOrDecodeImageBitmap(imageStream, width, height, bitsPerComponent, colorSpace);
            if (bitmap == null)
                return;

            // Draw the image at unit square (0,0)-(1,1), the CTM handles positioning
            _canvas.Save();

            // Images are drawn into a 1x1 unit square, scaled by the CTM
            // We need to flip Y because images have origin at top-left
            _canvas.Scale(1.0f / width, -1.0f / height);
            _canvas.Translate(0, -height);

            using var paint = new SKPaint
            {
                BlendMode = _state.BlendMode,
                IsAntialias = _options.AntiAlias
            };
            if (_state.FillAlpha < 1.0f)
            {
                paint.Color = paint.Color.WithAlpha((byte)(_state.FillAlpha * 255));
            }

            if (!TryDrawImageWithSoftMask(bitmap, imageStream, width, height, paint) &&
                !TryDrawImageWithExplicitMask(bitmap, imageStream, width, height, paint))
            {
                var bitmapToDraw = bitmap;
                if (imageStream.GetOptional("SMask") != null)
                {
                    mutableBitmap = bitmap.Copy(SKColorType.Rgba8888);
                    if (mutableBitmap != null)
                    {
                        ApplySoftMask(mutableBitmap, imageStream);
                        bitmapToDraw = mutableBitmap;
                    }
                }

                _canvas.DrawBitmap(bitmapToDraw, new SKRect(0, 0, width, height), paint);
            }
            _canvas.Restore();
        }
        finally
        {
            mutableBitmap?.Dispose();
        }
    }

    private bool TryDrawImageMaskWithPattern(
        Pdfe.Core.Primitives.PdfStream imageStream,
        int width,
        int height)
    {
        if (_state.FillPatternName == null)
            return false;

        SKBitmap? stencil = null;
        try
        {
            stencil = CreateImageMaskStencilBitmap(imageStream.DecodedData, width, height, imageStream);
            if (stencil == null)
                return false;

            _canvas.Save();
            try
            {
                // Image XObjects paint into the unit square in the current
                // user space. The source pixel grid is only the stencil
                // sampler; pattern colors must still be evaluated in that
                // current user space, not in source-pixel coordinates.
                var dest = new SKRect(0, 0, 1, 1);
                using var clipPath = new SKPath();
                clipPath.AddRect(dest);

                _canvas.SaveLayer();
                try
                {
                    if (!RenderFillPattern(clipPath))
                        return false;

                    using var maskPaint = new SKPaint
                    {
                        BlendMode = SKBlendMode.DstIn,
                        IsAntialias = _options.AntiAlias
                    };
                    DrawImageMaskStencil(stencil, dest, maskPaint);
                }
                finally
                {
                    _canvas.Restore();
                }

                return true;
            }
            finally
            {
                _canvas.Restore();
            }
        }
        catch (Exception __ex) when (__ex is not OutOfMemoryException)
        {
            return false;
        }
        finally
        {
            stencil?.Dispose();
        }
    }

    private void DrawImageMaskStencil(SKBitmap stencil, SKRect dest, SKPaint maskPaint)
    {
        if (TryCreateDeviceSpaceAreaResampledStencil(stencil, dest, out var coverage, out var deviceDest) &&
            coverage != null)
        {
            using (coverage)
            {
                _canvas.Save();
                try
                {
                    _canvas.SetMatrix(SKMatrix.Identity);
                    _canvas.DrawBitmap(coverage, deviceDest, maskPaint);
                }
                finally
                {
                    _canvas.Restore();
                }
            }

            return;
        }

        using var stencilImage = SKImage.FromBitmap(stencil);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        _canvas.Save();
        try
        {
            _canvas.Translate(dest.Left, dest.Bottom);
            _canvas.Scale(dest.Width / stencil.Width, -dest.Height / stencil.Height);
            _canvas.DrawImage(
                stencilImage,
                new SKRect(0, 0, stencil.Width, stencil.Height),
                sampling,
                maskPaint);
        }
        finally
        {
            _canvas.Restore();
        }
    }

    private bool TryCreateDeviceSpaceAreaResampledStencil(
        SKBitmap stencil,
        SKRect dest,
        out SKBitmap? coverage,
        out SKRect deviceDest)
    {
        coverage = null;
        deviceDest = default;
        var matrix = _canvas.TotalMatrix;
        const float epsilon = 0.001f;
        if (Math.Abs(matrix.SkewX) > epsilon ||
            Math.Abs(matrix.SkewY) > epsilon ||
            Math.Abs(matrix.ScaleX) <= epsilon ||
            Math.Abs(matrix.ScaleY) <= epsilon)
            return false;

        var deviceBounds = MapAxisAlignedRect(matrix, dest);
        var deviceLeft = (int)Math.Floor(deviceBounds.Left);
        var deviceTop = (int)Math.Floor(deviceBounds.Top);
        var deviceRight = (int)Math.Ceiling(deviceBounds.Right);
        var deviceBottom = (int)Math.Ceiling(deviceBounds.Bottom);
        var destWidth = deviceRight - deviceLeft;
        var destHeight = deviceBottom - deviceTop;
        if (destWidth <= 0 || destHeight <= 0)
            return false;
        if (destWidth >= stencil.Width && destHeight >= stencil.Height)
            return false;

        const int maxCoveragePixels = 16_000_000;
        if ((long)destWidth * destHeight > maxCoveragePixels)
            return false;

        coverage = CreateDeviceSpaceAreaResampledStencil(
            stencil,
            dest,
            matrix,
            deviceLeft,
            deviceTop,
            destWidth,
            destHeight);
        if (coverage == null)
            return false;

        deviceDest = new SKRect(deviceLeft, deviceTop, deviceRight, deviceBottom);
        return true;
    }

    private static SKRect MapAxisAlignedRect(SKMatrix matrix, SKRect rect)
    {
        var x0 = matrix.ScaleX * rect.Left + matrix.TransX;
        var x1 = matrix.ScaleX * rect.Right + matrix.TransX;
        var y0 = matrix.ScaleY * rect.Top + matrix.TransY;
        var y1 = matrix.ScaleY * rect.Bottom + matrix.TransY;
        return new SKRect(
            Math.Min(x0, x1),
            Math.Min(y0, y1),
            Math.Max(x0, x1),
            Math.Max(y0, y1));
    }

    private static SKRect MapRect(SKMatrix matrix, SKRect rect)
    {
        var p0 = MapPoint(matrix, rect.Left, rect.Top);
        var p1 = MapPoint(matrix, rect.Right, rect.Top);
        var p2 = MapPoint(matrix, rect.Right, rect.Bottom);
        var p3 = MapPoint(matrix, rect.Left, rect.Bottom);
        return new SKRect(
            Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X)),
            Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y)),
            Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X)),
            Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y)));
    }

    private static SKPoint MapPoint(SKMatrix matrix, float x, float y)
        => new(
            (matrix.ScaleX * x) + (matrix.SkewX * y) + matrix.TransX,
            (matrix.SkewY * x) + (matrix.ScaleY * y) + matrix.TransY);

    private bool TryGetLayerBounds(SKRect? preferredBounds, out SKRect bounds)
    {
        bounds = preferredBounds ?? _canvas.LocalClipBounds;
        var clipBounds = _canvas.LocalClipBounds;
        if (bounds.Width > 0 && bounds.Height > 0 && clipBounds.Width > 0 && clipBounds.Height > 0)
        {
            bounds.Intersect(clipBounds);
            return bounds.Width > 0 && bounds.Height > 0;
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
            bounds = clipBounds;

        if (bounds.Width <= 0 || bounds.Height <= 0)
            bounds = _canvas.DeviceClipBounds;

        return bounds.Width > 0 && bounds.Height > 0;
    }

    private (int Width, int Height) EstimateSoftMaskBitmapSize(SKRect localBounds)
    {
        var deviceBounds = MapRect(_canvas.TotalMatrix, localBounds);
        var targetWidth = Math.Max(1, (int)Math.Ceiling(deviceBounds.Width));
        var targetHeight = Math.Max(1, (int)Math.Ceiling(deviceBounds.Height));
        return ClampSoftMaskTargetSize(targetWidth, targetHeight, targetWidth, targetHeight);
    }

    private static SKBitmap? CreateDeviceSpaceAreaResampledStencil(
        SKBitmap source,
        SKRect dest,
        SKMatrix deviceFromLocal,
        int deviceLeft,
        int deviceTop,
        int destWidth,
        int destHeight)
    {
        if (destWidth <= 0 || destHeight <= 0)
            return null;

        var pixels = new byte[destWidth * destHeight * 4];
        var sourceScaleX = source.Width / (double)dest.Width;
        var sourceScaleY = source.Height / (double)dest.Height;
        var localPixelWidth = Math.Abs(1.0 / deviceFromLocal.ScaleX);
        var localPixelHeight = Math.Abs(1.0 / deviceFromLocal.ScaleY);
        var localPixelArea = localPixelWidth * localPixelHeight;
        var dst = 0;

        for (var y = 0; y < destHeight; y++)
        {
            var localY0 = (deviceTop + y - deviceFromLocal.TransY) / deviceFromLocal.ScaleY;
            var localY1 = (deviceTop + y + 1 - deviceFromLocal.TransY) / deviceFromLocal.ScaleY;
            var localTop = Math.Min(localY0, localY1);
            var localBottom = Math.Max(localY0, localY1);
            var clippedTop = Math.Max(dest.Top, localTop);
            var clippedBottom = Math.Min(dest.Bottom, localBottom);

            for (var x = 0; x < destWidth; x++)
            {
                var localX0 = (deviceLeft + x - deviceFromLocal.TransX) / deviceFromLocal.ScaleX;
                var localX1 = (deviceLeft + x + 1 - deviceFromLocal.TransX) / deviceFromLocal.ScaleX;
                var localLeft = Math.Min(localX0, localX1);
                var localRight = Math.Max(localX0, localX1);
                var clippedLeft = Math.Max(dest.Left, localLeft);
                var clippedRight = Math.Min(dest.Right, localRight);

                byte alpha = 0;
                if (clippedRight > clippedLeft && clippedBottom > clippedTop)
                {
                    var sourceLeft = (clippedLeft - dest.Left) * sourceScaleX;
                    var sourceRight = (clippedRight - dest.Left) * sourceScaleX;
                    // PDF image space maps the first image sample row to the
                    // top of the unit-square image area. Normal image drawing
                    // applies this as a Y flip; the device-space stencil path
                    // must do the same while sampling coverage directly.
                    var sourceTop = (dest.Bottom - clippedBottom) * sourceScaleY;
                    var sourceBottom = (dest.Bottom - clippedTop) * sourceScaleY;
                    var averageAlpha = AverageAlphaOverSourceRect(
                        source,
                        sourceLeft,
                        sourceTop,
                        sourceRight,
                        sourceBottom);
                    var coveredLocalArea = (clippedRight - clippedLeft) * (clippedBottom - clippedTop);
                    var devicePixelCoverage = localPixelArea > 0
                        ? Math.Clamp(coveredLocalArea / localPixelArea, 0, 1)
                        : 0;
                    alpha = (byte)Math.Clamp(
                        (int)Math.Round(averageAlpha * devicePixelCoverage),
                        0,
                        255);
                }

                // The bitmap is declared premultiplied, so partial stencil
                // pixels must use premultiplied white rather than RGB 255.
                pixels[dst++] = alpha;
                pixels[dst++] = alpha;
                pixels[dst++] = alpha;
                pixels[dst++] = alpha;
            }
        }

        return CreateBitmapFromRgbaBytes(destWidth, destHeight, pixels);
    }

    private static double AverageAlphaOverSourceRect(
        SKBitmap source,
        double left,
        double top,
        double right,
        double bottom)
    {
        left = Math.Clamp(left, 0, source.Width);
        right = Math.Clamp(right, 0, source.Width);
        top = Math.Clamp(top, 0, source.Height);
        bottom = Math.Clamp(bottom, 0, source.Height);
        if (right <= left || bottom <= top)
            return 0;

        var firstSourceX = Math.Max(0, (int)Math.Floor(left));
        var lastSourceX = Math.Min(source.Width - 1, (int)Math.Ceiling(right) - 1);
        var firstSourceY = Math.Max(0, (int)Math.Floor(top));
        var lastSourceY = Math.Min(source.Height - 1, (int)Math.Ceiling(bottom) - 1);
        var weightedAlpha = 0.0;
        var coveredArea = 0.0;

        for (var sy = firstSourceY; sy <= lastSourceY; sy++)
        {
            var yCoverage = Math.Min(sy + 1, bottom) - Math.Max(sy, top);
            if (yCoverage <= 0)
                continue;

            for (var sx = firstSourceX; sx <= lastSourceX; sx++)
            {
                var xCoverage = Math.Min(sx + 1, right) - Math.Max(sx, left);
                if (xCoverage <= 0)
                    continue;

                var area = xCoverage * yCoverage;
                weightedAlpha += source.GetPixel(sx, sy).Alpha * area;
                coveredArea += area;
            }
        }

        return coveredArea > 0 ? weightedAlpha / coveredArea : 0;
    }

    private SKBitmap? GetOrDecodeImageBitmap(
        Pdfe.Core.Primitives.PdfStream imageStream,
        int width,
        int height,
        int bitsPerComponent,
        string colorSpace)
    {
        var key = CreateImageBitmapCacheKey(imageStream, width, height, bitsPerComponent, colorSpace);
        if (TryGetImageReferenceKey(imageStream, out var referenceKey))
        {
            var cacheKey = (referenceKey.ObjectNumber, referenceKey.Generation, key);
            if (!_imageBitmapByReference.TryGetValue(cacheKey, out var bitmap))
            {
                bitmap = DecodeImageBitmap(imageStream, width, height, bitsPerComponent, colorSpace);
                TrackCachedImageBitmap(bitmap);
                _imageBitmapByReference[cacheKey] = bitmap;
            }

            return bitmap;
        }

        if (!_imageBitmapByStream.TryGetValue(imageStream, out var streamCache))
        {
            streamCache = new Dictionary<ImageBitmapCacheKey, SKBitmap?>();
            _imageBitmapByStream[imageStream] = streamCache;
        }

        if (!streamCache.TryGetValue(key, out var streamBitmap))
        {
            streamBitmap = DecodeImageBitmap(imageStream, width, height, bitsPerComponent, colorSpace);
            TrackCachedImageBitmap(streamBitmap);
            streamCache[key] = streamBitmap;
        }

        return streamBitmap;
    }

    private SKBitmap? DecodeImageBitmap(
        Pdfe.Core.Primitives.PdfStream imageStream,
        int width,
        int height,
        int bitsPerComponent,
        string colorSpace)
    {
        try
        {
            var filters = imageStream.Filters;
            if (IsTerminalDctFilter(filters))
            {
                var (targetWidth, targetHeight) = EstimateImageDecodeSize(width, height);
                var dctData = GetTerminalDctData(imageStream, filters);
                if (ResolveDctColorTransform(imageStream, filters, dctData, colorSpace) is { } colorTransform)
                {
                    var decoded = DecodeDctImageWithColorTransform(
                        dctData,
                        width,
                        height,
                        colorSpace,
                        targetWidth,
                        targetHeight,
                        colorTransform,
                        imageStream);
                    if (decoded != null)
                        return decoded;
                }

                return SafeDecode(
                    dctData,
                    GetDecodeSize(width, height, targetWidth, targetHeight));
            }

            if (filters.Contains("JPXDecode"))
            {
                var bitmap = DecodeJpxImage(imageStream, width, height);
                bitmap ??= SafeDecode(imageStream.EncodedData);
                return bitmap;
            }

            return CreateBitmapFromRawData(
                imageStream.DecodedData,
                width,
                height,
                bitsPerComponent,
                colorSpace,
                imageStream);
        }
        catch
        {
            return null;
        }
    }

    private ImageBitmapCacheKey CreateImageBitmapCacheKey(
        Pdfe.Core.Primitives.PdfStream imageStream,
        int width,
        int height,
        int bitsPerComponent,
        string colorSpace)
    {
        var filters = imageStream.Filters;
        var (targetWidth, targetHeight) = filters.Contains("JPXDecode") || ContainsDctFilter(filters)
            ? EstimateImageDecodeSize(width, height)
            : (width, height);
        var isImageMask = imageStream.GetBool("ImageMask");
        var fillAlpha = isImageMask
            ? (byte)Math.Clamp(_state.FillAlpha * 255, 0, 255)
            : (byte)0;
        var dctColorTransform = IsTerminalDctFilter(filters)
            ? ResolveDctColorTransform(
                imageStream,
                filters,
                GetTerminalDctData(imageStream, filters),
                colorSpace)
            : null;

        return new ImageBitmapCacheKey(
            width,
            height,
            bitsPerComponent,
            colorSpace,
            targetWidth,
            targetHeight,
            isImageMask,
            isImageMask ? _state.FillColor.Red : (byte)0,
            isImageMask ? _state.FillColor.Green : (byte)0,
            isImageMask ? _state.FillColor.Blue : (byte)0,
            fillAlpha,
            dctColorTransform);
    }

    private void TrackCachedImageBitmap(SKBitmap? bitmap)
    {
        if (bitmap != null)
            _cachedImageBitmaps.Add(bitmap);
    }

    private void DisposeImageBitmapCache()
    {
        foreach (var bitmap in _cachedImageBitmaps)
            bitmap.Dispose();
        _cachedImageBitmaps.Clear();
        _imageBitmapByReference.Clear();
        _imageBitmapByStream.Clear();
    }

    private void DisposeOwnedResources()
    {
        DisposeImageBitmapCache();
        foreach (var typeface in _embeddedTypefaces.Values)
            typeface.Dispose();
        _embeddedTypefaces.Clear();
    }

    private static bool TryGetImageReferenceKey(
        Pdfe.Core.Primitives.PdfStream imageStream,
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

    private bool TryDrawImageWithSoftMask(
        SKBitmap bitmap,
        Pdfe.Core.Primitives.PdfStream imageStream,
        int width,
        int height,
        SKPaint imagePaint)
    {
        var maskObj = imageStream.GetOptional("SMask");
        if (maskObj == null)
            return false;

        var resolved = _page.Document.Resolve(maskObj);
        if (resolved is not Pdfe.Core.Primitives.PdfStream maskStream)
            return false;

        if (string.Equals(maskStream.GetNameOrNull("Subtype"), "Form", StringComparison.Ordinal))
            return false;

        var maskWidth = maskStream.GetInt("Width", 0);
        var maskHeight = maskStream.GetInt("Height", 0);
        if (maskWidth <= 0 || maskHeight <= 0)
            return false;

        var (targetWidth, targetHeight) = EstimateImageSoftMaskTargetSize(
            width,
            height,
            bitmap.Width,
            bitmap.Height,
            maskWidth,
            maskHeight);
        var maskData = GetSoftMaskData(maskObj, maskStream, maskWidth, maskHeight, targetWidth, targetHeight);
        if (maskData == null || maskData.Data.Length == 0)
            return false;

        using var maskedBitmap = CreateSoftMaskedImageBitmap(bitmap, maskData);
        if (maskedBitmap == null)
            return false;

        var dest = new SKRect(0, 0, width, height);
        if ((maskedBitmap.Width != bitmap.Width || maskedBitmap.Height != bitmap.Height) &&
            TryDrawSoftMaskedImageInDeviceSpace(maskedBitmap, dest, imagePaint))
        {
            return true;
        }

        _canvas.DrawBitmap(maskedBitmap, dest, imagePaint);
        return true;
    }

    private (int Width, int Height) EstimateImageSoftMaskTargetSize(
        int imageWidth,
        int imageHeight,
        int decodedImageWidth,
        int decodedImageHeight,
        int maskWidth,
        int maskHeight)
    {
        if (decodedImageWidth == maskWidth && decodedImageHeight == maskHeight)
            return (decodedImageWidth, decodedImageHeight);

        var deviceDest = MapAxisAlignedRect(
            _canvas.TotalMatrix,
            new SKRect(0, 0, imageWidth, imageHeight));
        var targetWidth = Math.Max(1, (int)Math.Ceiling(deviceDest.Width));
        var targetHeight = Math.Max(1, (int)Math.Ceiling(deviceDest.Height));
        return ClampSoftMaskTargetSize(maskWidth, maskHeight, targetWidth, targetHeight);
    }

    private bool TryDrawSoftMaskedImageInDeviceSpace(SKBitmap bitmap, SKRect dest, SKPaint imagePaint)
    {
        var matrix = _canvas.TotalMatrix;
        const float epsilon = 0.001f;
        if (Math.Abs(matrix.SkewX) > epsilon ||
            Math.Abs(matrix.SkewY) > epsilon ||
            Math.Abs(matrix.ScaleX) <= epsilon ||
            Math.Abs(matrix.ScaleY) <= epsilon)
        {
            return false;
        }

        var deviceDest = MapAxisAlignedRect(matrix, dest);
        if (deviceDest.Width <= epsilon || deviceDest.Height <= epsilon)
            return false;

        _canvas.Save();
        try
        {
            _canvas.SetMatrix(SKMatrix.Identity);
            _canvas.DrawBitmap(bitmap, deviceDest, imagePaint);
        }
        finally
        {
            _canvas.Restore();
        }

        return true;
    }

    private static SKBitmap? CreateSoftMaskedImageBitmap(SKBitmap source, SoftMaskAlpha mask)
    {
        if (source.Width <= 0 || source.Height <= 0 || mask.Width <= 0 || mask.Height <= 0)
            return null;

        var pixels = new byte[checked(mask.Width * mask.Height * 4)];
        try
        {
            var sourcePixels = source.Pixels;
            if (sourcePixels.Length == 0)
                return null;

            var dst = 0;
            var maskIndex = 0;
            for (int y = 0; y < mask.Height; y++)
            {
                var sourceY = MapTargetToSource(y, mask.Height, source.Height);
                var sourceRow = sourceY * source.Width;
                for (int x = 0; x < mask.Width; x++)
                {
                    var sourceX = MapTargetToSource(x, mask.Width, source.Width);
                    var sourceIndex = sourceRow + sourceX;
                    var sourceColor = sourceIndex < sourcePixels.Length
                        ? sourcePixels[sourceIndex]
                        : SKColors.Transparent;
                    var maskAlpha = maskIndex < mask.Data.Length ? mask.Data[maskIndex] : (byte)0;
                    var alpha = (byte)((sourceColor.Alpha * maskAlpha + 127) / 255);
                    pixels[dst++] = sourceColor.Red;
                    pixels[dst++] = sourceColor.Green;
                    pixels[dst++] = sourceColor.Blue;
                    pixels[dst++] = alpha;
                    maskIndex++;
                }
            }

            return CreateBitmapFromRgbaBytes(mask.Width, mask.Height, pixels, SKAlphaType.Unpremul);
        }
        catch (Exception __ex) when (__ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private bool TryDrawImageWithExplicitMask(
        SKBitmap bitmap,
        Pdfe.Core.Primitives.PdfStream imageStream,
        int width,
        int height,
        SKPaint imagePaint)
    {
        var maskObj = imageStream.GetOptional("Mask");
        if (maskObj == null)
            return false;

        var resolved = _page.Document.Resolve(maskObj) ?? maskObj;
        if (resolved is not Pdfe.Core.Primitives.PdfStream maskStream)
            return false;

        var maskWidth = maskStream.GetInt("Width", 0);
        var maskHeight = maskStream.GetInt("Height", 0);
        if (maskWidth <= 0 || maskHeight <= 0)
            return false;

        using var maskBitmap = CreateExplicitImageMaskBitmap(maskStream, maskWidth, maskHeight);
        if (maskBitmap == null)
            return false;

        var dest = new SKRect(0, 0, width, height);
        using var layerPaint = new SKPaint
        {
            BlendMode = imagePaint.BlendMode,
            Color = imagePaint.Color,
            IsAntialias = imagePaint.IsAntialias
        };

        _canvas.SaveLayer(dest, layerPaint);
        _canvas.DrawBitmap(bitmap, dest);

        using var maskPaint = new SKPaint
        {
            BlendMode = SKBlendMode.DstIn,
            IsAntialias = _options.AntiAlias
        };
        _canvas.DrawBitmap(maskBitmap, dest, maskPaint);
        _canvas.Restore();
        return true;
    }

    private SKBitmap? DecodeSoftMaskBitmap(
        Pdfe.Core.Primitives.PdfObject maskObj,
        Pdfe.Core.Primitives.PdfStream maskStream,
        int targetWidth,
        int targetHeight,
        SKRect? maskBounds = null)
    {
        if (string.Equals(maskStream.GetNameOrNull("Subtype"), "Form", StringComparison.Ordinal))
            return maskBounds.HasValue
                ? RenderFormSoftMaskBitmap(maskStream, targetWidth, targetHeight, maskBounds.Value)
                : null;

        var width = maskStream.GetInt("Width", 0);
        var height = maskStream.GetInt("Height", 0);
        if (width <= 0 || height <= 0)
            return null;

        var alpha = GetSoftMaskData(maskObj, maskStream, width, height, targetWidth, targetHeight);
        return alpha != null ? CreateSoftMaskLumaBitmap(alpha) : null;
    }

    private SKBitmap? RenderFormSoftMaskBitmap(
        Pdfe.Core.Primitives.PdfStream maskStream,
        int targetWidth,
        int targetHeight,
        SKRect maskBounds)
    {
        if (targetWidth <= 0 || targetHeight <= 0 || maskBounds.Width <= 0 || maskBounds.Height <= 0)
            return null;

        (targetWidth, targetHeight) = ClampSoftMaskTargetSize(
            Math.Max(1, targetWidth),
            Math.Max(1, targetHeight),
            targetWidth,
            targetHeight);

        var bitmap = new SKBitmap(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
        try
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Black);

            var scaleX = targetWidth / maskBounds.Width;
            var scaleY = targetHeight / maskBounds.Height;
            canvas.SetMatrix(new SKMatrix(
                scaleX,
                0,
                -maskBounds.Left * scaleX,
                0,
                scaleY,
                -maskBounds.Top * scaleY,
                0,
                0,
                1));

            var child = new RenderContext(canvas, _page, _options, _cancellationToken);
            child._resourcesStack.Push(_page.Resources);
            child._state = _state.Clone();
            child._state.SoftMask = null;
            try
            {
                child.RenderFormXObject(maskStream);
            }
            finally
            {
                child._resourcesStack.Clear();
                child.DisposeOwnedResources();
            }

            return bitmap;
        }
        catch (Exception __ex) when (__ex is not OutOfMemoryException)
        {
            bitmap.Dispose();
            return null;
        }
    }

    private SKBitmap? DecodeJpxImage(Pdfe.Core.Primitives.PdfStream imageStream, int sourceWidth, int sourceHeight)
    {
        try
        {
            var colorSpaceObject = imageStream.GetOptional("ColorSpace");
            var colorSpace = colorSpaceObject != null
                ? ResolveImageColorSpace(colorSpaceObject)
                : PdfColorSpace.DeviceRGB;

            var desiredComponents = Math.Max(1, colorSpace.Components);
            if (imageStream.GetOptional("SMask") == null)
                desiredComponents++;

            var estimatedTarget = EstimateImageDecodeSize(sourceWidth, sourceHeight);
            var image = desiredComponents == 1 && imageStream.GetOptional("SMask") != null
                ? JpxDecoder.TryDecodeOpenJpegGray(imageStream.EncodedData)
                : TryDecodeLargeJpxWithOpenJpeg(imageStream, sourceWidth, sourceHeight, estimatedTarget.Width, estimatedTarget.Height, desiredComponents);
            image ??= JpxDecoder.TryDecodeManaged(imageStream.EncodedData, desiredComponents);
            if (image == null || sourceWidth <= 0 || sourceHeight <= 0 || image.Components <= 0)
                return null;

            var components = image.ComponentData;
            if (components.Length == 0)
                return null;

            var decodedWidth = image.Width > 0 ? image.Width : sourceWidth;
            var decodedHeight = image.Height > 0 ? image.Height : sourceHeight;
            var (targetWidth, targetHeight) = image.BitsPerComponent > 8
                ? ClampImageTargetSize(sourceWidth, sourceHeight, sourceWidth, sourceHeight)
                : ClampImageTargetSize(decodedWidth, decodedHeight, estimatedTarget.Width, estimatedTarget.Height);

            var pixels = new byte[checked(targetWidth * targetHeight * 4)];
            var dst = 0;
            var sourcePixelCount = (long)decodedWidth * decodedHeight;
            var hasEmbeddedAlpha = components.Length > colorSpace.Components
                                   && colorSpace.Components >= 1;
            for (int y = 0; y < targetHeight; y++)
            {
                var sourceY = MapTargetToSource(y, targetHeight, decodedHeight);
                var sourceRow = (long)sourceY * decodedWidth;
                for (int x = 0; x < targetWidth; x++)
                {
                    var sourceX = MapTargetToSource(x, targetWidth, decodedWidth);
                    var idx = sourceRow + sourceX;
                    if (idx >= sourcePixelCount)
                        continue;

                    var values = new double[Math.Max(1, colorSpace.Components)];
                    if (colorSpace.Type == PdfColorSpaceType.Indexed)
                    {
                        values[0] = idx < components[0].Length ? components[0][idx] : 0;
                    }
                    else
                    {
                        for (int c = 0; c < values.Length; c++)
                        {
                            var componentIndex = GetJpxColorComponentIndex(image, colorSpace, c, components.Length);
                            var sample = componentIndex < components.Length && idx < components[componentIndex].LongLength
                                ? components[componentIndex][(int)idx]
                                : 0;
                            values[c] = colorSpace.DecodeSampleByte(c, NormalizeJpxSampleToByte(sample, image.BitsPerComponent));
                        }
                    }

                    var (rd, gd, bd) = colorSpace.ToRgb(values);
                    var alpha = 255;
                    if (hasEmbeddedAlpha)
                    {
                        var alphaComponentIndex = GetJpxAlphaComponentIndex(image, colorSpace.Components, components.Length);
                        var alphaComponent = components[alphaComponentIndex];
                        if (idx < alphaComponent.LongLength)
                            alpha = NormalizeJpxSampleToByte(alphaComponent[(int)idx], image.BitsPerComponent);
                    }

                    pixels[dst++] = (byte)Math.Clamp(rd * 255, 0, 255);
                    pixels[dst++] = (byte)Math.Clamp(gd * 255, 0, 255);
                    pixels[dst++] = (byte)Math.Clamp(bd * 255, 0, 255);
                    pixels[dst++] = (byte)alpha;
                }
            }

            return CreateBitmapFromRgbaBytes(targetWidth, targetHeight, pixels);
        }
        catch
        {
            return null;
        }
    }

    private JpxImage? TryDecodeLargeJpxWithOpenJpeg(
        Pdfe.Core.Primitives.PdfStream imageStream,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        int desiredComponents)
    {
        if (desiredComponents < 3)
            return null;

        var sourcePixels = (long)sourceWidth * sourceHeight;
        if (sourcePixels <= MaxExpandedSoftMaskPixels)
            return null;

        var reduceFactor = ChooseOpenJpegReduceFactor(sourceWidth, sourceHeight, targetWidth, targetHeight);
        return JpxDecoder.TryDecodeOpenJpeg(imageStream.EncodedData, reduceFactor);
    }

    private static int ChooseOpenJpegReduceFactor(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var reduce = 0;
        while (reduce < 8)
        {
            var next = reduce + 1;
            var nextWidth = Math.Max(1, sourceWidth >> next);
            var nextHeight = Math.Max(1, sourceHeight >> next);
            if (nextWidth < targetWidth || nextHeight < targetHeight)
                break;

            reduce = next;
        }

        return reduce;
    }

    private static int GetJpxColorComponentIndex(
        JpxImage image,
        PdfColorSpace colorSpace,
        int requestedComponent,
        int decodedComponentCount)
    {
        if (image.ComponentDefinitions.Count > 0)
        {
            var association = requestedComponent + 1;
            foreach (var component in image.ComponentDefinitions)
            {
                if (component.Type == 0 &&
                    component.Association == association &&
                    component.ComponentIndex >= 0 &&
                    component.ComponentIndex < decodedComponentCount)
                {
                    return component.ComponentIndex;
                }
            }
        }

        if (decodedComponentCount >= 3 &&
            requestedComponent < 3 &&
            colorSpace.Components == 3 &&
            (colorSpace.Type == PdfColorSpaceType.DeviceRGB ||
             colorSpace.Type == PdfColorSpaceType.CalRGB ||
             colorSpace.Type == PdfColorSpaceType.ICCBased))
        {
            // CSJ2K exposes decoded color components in bitmap BGR order for
            // RGB JP2 images. PDF color conversion expects logical RGB order.
            return 2 - requestedComponent;
        }

        return requestedComponent;
    }

    private static int GetJpxAlphaComponentIndex(JpxImage image, int fallbackIndex, int decodedComponentCount)
    {
        if (image.ComponentDefinitions.Count > 0)
        {
            foreach (var component in image.ComponentDefinitions)
            {
                if (component.Type is 1 or 2 &&
                    component.ComponentIndex >= 0 &&
                    component.ComponentIndex < decodedComponentCount)
                {
                    return component.ComponentIndex;
                }
            }
        }

        return Math.Clamp(fallbackIndex, 0, Math.Max(0, decodedComponentCount - 1));
    }

    private static byte NormalizeJpxSampleToByte(int sample, int bitsPerComponent)
    {
        if (bitsPerComponent <= 8)
            return (byte)Math.Clamp(sample, 0, 255);

        var maxSample = bitsPerComponent >= 31
            ? int.MaxValue
            : (1 << bitsPerComponent) - 1;
        if (maxSample <= 255)
            return (byte)Math.Clamp(sample, 0, 255);

        var normalized = (long)Math.Clamp(sample, 0, maxSample) * 255 + (maxSample / 2);
        return (byte)(normalized / maxSample);
    }

    private (int Width, int Height) EstimateImageDecodeSize(int sourceWidth, int sourceHeight)
    {
        var userWidth = Math.Sqrt(
            (_state.CurrentTransform.ScaleX * _state.CurrentTransform.ScaleX)
            + (_state.CurrentTransform.SkewY * _state.CurrentTransform.SkewY));
        var userHeight = Math.Sqrt(
            (_state.CurrentTransform.SkewX * _state.CurrentTransform.SkewX)
            + (_state.CurrentTransform.ScaleY * _state.CurrentTransform.ScaleY));

        var scale = Math.Max(1, _options.Dpi) / 72.0;
        var targetWidth = userWidth > 0
            ? Math.Clamp((int)Math.Round(userWidth * scale), 1, sourceWidth)
            : sourceWidth;
        var targetHeight = userHeight > 0
            ? Math.Clamp((int)Math.Round(userHeight * scale), 1, sourceHeight)
            : sourceHeight;

        return ClampImageTargetSize(sourceWidth, sourceHeight, targetWidth, targetHeight);
    }

    private static (int Width, int Height) ClampImageTargetSize(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        var width = Math.Clamp(targetWidth, 1, Math.Max(1, sourceWidth));
        var height = Math.Clamp(targetHeight, 1, Math.Max(1, sourceHeight));
        var pixels = (long)width * height;
        if (pixels <= MaxExpandedSoftMaskPixels)
            return (width, height);

        var scale = Math.Sqrt(MaxExpandedSoftMaskPixels / (double)pixels);
        return (
            Math.Max(1, (int)Math.Floor(width * scale)),
            Math.Max(1, (int)Math.Floor(height * scale)));
    }

    private bool ApplySoftMask(SKBitmap bitmap, Pdfe.Core.Primitives.PdfStream imageStream)
    {
        var maskObj = imageStream.GetOptional("SMask");
        if (maskObj == null)
            return false;

        var resolved = _page.Document.Resolve(maskObj);
        if (resolved is not Pdfe.Core.Primitives.PdfStream maskStream)
            return false;

        var maskWidth = maskStream.GetInt("Width", 0);
        var maskHeight = maskStream.GetInt("Height", 0);
        if (maskWidth <= 0 || maskHeight <= 0)
            return false;

        var targetWidth = Math.Max(1, bitmap.Width);
        var targetHeight = Math.Max(1, bitmap.Height);
        var maskData = GetSoftMaskData(maskObj, maskStream, maskWidth, maskHeight, targetWidth, targetHeight);
        if (maskData == null || maskData.Data.Length == 0)
            return false;

        for (int y = 0; y < bitmap.Height; y++)
        {
            var maskY = Math.Clamp((int)((long)y * maskData.Height / bitmap.Height), 0, maskData.Height - 1);
            for (int x = 0; x < bitmap.Width; x++)
            {
                var maskX = Math.Clamp((int)((long)x * maskData.Width / bitmap.Width), 0, maskData.Width - 1);
                var alphaIndex = maskY * maskData.Width + maskX;
                if (alphaIndex >= maskData.Data.Length)
                    continue;

                var color = bitmap.GetPixel(x, y);
                bitmap.SetPixel(x, y, color.WithAlpha(maskData.Data[alphaIndex]));
            }
        }

        return true;
    }

    private SoftMaskAlpha? GetSoftMaskData(
        Pdfe.Core.Primitives.PdfObject maskObj,
        Pdfe.Core.Primitives.PdfStream maskStream,
        int maskWidth,
        int maskHeight,
        int targetWidth,
        int targetHeight)
    {
        SoftMaskAlpha? maskData;
        if (TryGetSoftMaskReferenceKey(maskObj, maskStream, out var key))
        {
            var cacheKey = (key.ObjectNumber, key.Generation, targetWidth, targetHeight);
            if (!_softMaskAlphaByReference.TryGetValue(cacheKey, out maskData))
            {
                maskData = DecodeSoftMaskData(maskStream, maskWidth, maskHeight, targetWidth, targetHeight);
                _softMaskAlphaByReference[cacheKey] = maskData;
            }
        }
        else
        {
            if (!_softMaskAlphaByStream.TryGetValue(maskStream, out var streamCache))
            {
                streamCache = new Dictionary<(int TargetWidth, int TargetHeight), SoftMaskAlpha?>();
                _softMaskAlphaByStream[maskStream] = streamCache;
            }

            var cacheKey = (targetWidth, targetHeight);
            if (!streamCache.TryGetValue(cacheKey, out maskData))
            {
                maskData = DecodeSoftMaskData(maskStream, maskWidth, maskHeight, targetWidth, targetHeight);
                streamCache[cacheKey] = maskData;
            }
        }

        return maskData;
    }

    private static SoftMaskAlpha? DecodeSoftMaskData(
        Pdfe.Core.Primitives.PdfStream maskStream,
        int width,
        int height,
        int targetWidth,
        int targetHeight)
    {
        (targetWidth, targetHeight) = ClampSoftMaskTargetSize(width, height, targetWidth, targetHeight);

        var filters = maskStream.Filters;
        if (IsTerminalDctFilter(filters))
        {
            using var maskBitmap = SafeDecode(
                GetTerminalDctData(maskStream, filters),
                GetDecodeSize(width, height, targetWidth, targetHeight));
            if (maskBitmap != null)
                return new SoftMaskAlpha(
                    ExtractSoftMaskAlpha(maskBitmap, targetWidth, targetHeight, maskStream),
                    targetWidth,
                    targetHeight);

            return null;
        }

        if (filters.Contains("JPXDecode"))
        {
            var jpx = JpxDecoder.TryDecodeManaged(maskStream.EncodedData);
            if (jpx is { Components: > 0 } && jpx.ComponentData.Length > 0)
            {
                var component = jpx.ComponentData[0];
                if (component.Length >= width * height)
                {
                    var alpha = CreateSoftMaskAlphaFromSamples(component, width, height, targetWidth, targetHeight, maskStream);
                    return new SoftMaskAlpha(alpha, targetWidth, targetHeight);
                }
            }

            using var maskBitmap = SafeDecode(maskStream.EncodedData);
            if (maskBitmap != null)
                return new SoftMaskAlpha(ExtractSoftMaskAlpha(maskBitmap, targetWidth, targetHeight, maskStream), targetWidth, targetHeight);
        }

        var bitsPerComponent = maskStream.GetInt("BitsPerComponent", 8);
        if (bitsPerComponent == 8)
        {
            var data = maskStream.DecodedData;
            if (data.LongLength < (long)width * height)
                return null;

            var alpha = CreateSoftMaskAlphaFrom8Bit(data, width, height, targetWidth, targetHeight, maskStream);
            return new SoftMaskAlpha(alpha, targetWidth, targetHeight);
        }

        if (bitsPerComponent == 1)
        {
            var data = maskStream.DecodedData;
            var alpha = new byte[targetWidth * targetHeight];
            var dst = 0;
            for (int y = 0; y < targetHeight; y++)
            {
                var sourceY = MapTargetToSource(y, targetHeight, height);
                for (int x = 0; x < targetWidth; x++)
                {
                    var sourceX = MapTargetToSource(x, targetWidth, width);
                    alpha[dst++] = DecodeSoftMaskSample(
                        maskStream,
                        ReadOneBitImageSample(data, width, sourceX, sourceY),
                        bitsPerComponent);
                }
            }
            return new SoftMaskAlpha(alpha, targetWidth, targetHeight);
        }

        return null;
    }

    private static (int Width, int Height) ClampSoftMaskTargetSize(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        var width = Math.Clamp(targetWidth, 1, Math.Max(1, sourceWidth));
        var height = Math.Clamp(targetHeight, 1, Math.Max(1, sourceHeight));
        var pixels = (long)width * height;
        if (pixels <= MaxExpandedSoftMaskPixels)
            return (width, height);

        var scale = Math.Sqrt(MaxExpandedSoftMaskPixels / (double)pixels);
        return (
            Math.Max(1, (int)Math.Floor(width * scale)),
            Math.Max(1, (int)Math.Floor(height * scale)));
    }

    private static bool IsTerminalDctFilter(IReadOnlyList<string> filters)
        => filters.Count > 0 && IsDctFilter(filters[^1]);

    private static bool ContainsDctFilter(IReadOnlyList<string> filters)
        => filters.Any(IsDctFilter);

    private static bool IsDctFilter(string filter)
        => string.Equals(filter, "DCTDecode", StringComparison.Ordinal)
           || string.Equals(filter, "DCT", StringComparison.Ordinal);

    private static byte[] GetTerminalDctData(Pdfe.Core.Primitives.PdfStream stream, IReadOnlyList<string> filters)
        => filters.Count == 1 ? stream.EncodedData : stream.DecodedData;

    private int? ResolveDctColorTransform(
        PdfStream stream,
        IReadOnlyList<string> filters,
        byte[] dctData,
        string colorSpace)
    {
        var normalizedColorSpace = NormalizeDctColorSpaceName(colorSpace);
        if (TryGetAdobeDctColorTransform(dctData, out var markerColorTransform))
        {
            if (normalizedColorSpace == "DeviceCMYK" || markerColorTransform == 0)
                return markerColorTransform;

            if (normalizedColorSpace == "DeviceRGB")
                return null;
        }

        if (GetTerminalDctDecodeParmsColorTransform(stream, filters) is { } decodeParmsColorTransform)
            return decodeParmsColorTransform;

        return normalizedColorSpace == "DeviceCMYK" ? 0 : null;
    }

    private int? GetTerminalDctDecodeParmsColorTransform(PdfStream stream, IReadOnlyList<string> filters)
    {
        try
        {
            var parmsObject = stream.GetOptional("DecodeParms") ?? stream.GetOptional("DP");
            if (parmsObject == null || filters.Count == 0)
                return null;

            PdfDictionary? parms = null;
            var resolved = _page.Document.Resolve(parmsObject);
            if (resolved is PdfDictionary dictionary && filters.Count == 1)
            {
                parms = dictionary;
            }
            else if (resolved is PdfArray array)
            {
                var filterIndex = filters.Count - 1;
                if (filterIndex >= 0 && filterIndex < array.Count)
                    parms = _page.Document.Resolve(array[filterIndex]) as PdfDictionary;
            }

            if (parms == null)
                return null;

            var colorTransformObj = parms.GetOptional("ColorTransform");
            if (!TryGetResolvedNumber(colorTransformObj, out var colorTransform))
                return null;

            var value = (int)colorTransform;
            return value is 0 or 1 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private SKBitmap? DecodeDctImageWithColorTransform(
        byte[] data,
        int sourceWidth,
        int sourceHeight,
        string colorSpace,
        int targetWidth,
        int targetHeight,
        int colorTransform,
        Pdfe.Core.Primitives.PdfStream stream)
    {
        if (data.Length == 0 ||
            !TryGetDctColorSpaces(colorSpace, colorTransform, out var inputColorSpace, out var outputColorSpace))
        {
            return null;
        }

        var scaleDenominator = ChooseDctScaleDenominator(sourceWidth, sourceHeight, targetWidth, targetHeight);
        var cinfo = new jpeg_decompress_struct();
        try
        {
            using var input = new MemoryStream(data, writable: false);
            cinfo.jpeg_stdio_src(input);
            cinfo.jpeg_read_header(true);
            cinfo.Jpeg_color_space = inputColorSpace;
            cinfo.Out_color_space = outputColorSpace;
            cinfo.Scale_num = 1;
            cinfo.Scale_denom = scaleDenominator;

            cinfo.jpeg_start_decompress();
            var width = cinfo.Output_width;
            var height = cinfo.Output_height;
            if (width <= 0 || height <= 0)
                return null;

            if (outputColorSpace == J_COLOR_SPACE.JCS_CMYK)
                return DecodeDctCmykBitmap(cinfo, width, height, sourceWidth, sourceHeight, targetWidth, targetHeight, stream);

            if (cinfo.Output_components != 3)
                return null;

            var pixels = new byte[checked(width * height * 4)];
            var scanline = new[] { new byte[checked(width * cinfo.Output_components)] };
            var dst = 0;
            while (cinfo.Output_scanline < cinfo.Output_height)
            {
                cinfo.jpeg_read_scanlines(scanline, 1);
                var row = scanline[0];
                for (var src = 0; src < width * 3;)
                {
                    pixels[dst++] = row[src++];
                    pixels[dst++] = row[src++];
                    pixels[dst++] = row[src++];
                    pixels[dst++] = 255;
                }
            }

            cinfo.jpeg_finish_decompress();
            var bitmap = CreateBitmapFromRgbaBytes(width, height, pixels);
            if (bitmap == null)
                return null;

            return ResizeDecodedBitmap(
                bitmap,
                Math.Clamp(targetWidth, 1, sourceWidth),
                Math.Clamp(targetHeight, 1, sourceHeight));
        }
        catch
        {
            return null;
        }
        finally
        {
            try { cinfo.jpeg_destroy(); }
            catch { /* Ignore cleanup failures from malformed JPEG data. */ }
        }
    }

    private static bool TryGetDctColorSpaces(
        string colorSpace,
        int colorTransform,
        out J_COLOR_SPACE inputColorSpace,
        out J_COLOR_SPACE outputColorSpace)
    {
        inputColorSpace = J_COLOR_SPACE.JCS_UNKNOWN;
        outputColorSpace = J_COLOR_SPACE.JCS_UNKNOWN;
        switch (NormalizeDctColorSpaceName(colorSpace))
        {
            case "DeviceRGB":
                inputColorSpace = colorTransform == 0
                    ? J_COLOR_SPACE.JCS_RGB
                    : J_COLOR_SPACE.JCS_YCbCr;
                outputColorSpace = J_COLOR_SPACE.JCS_RGB;
                return true;
            case "DeviceCMYK":
                inputColorSpace = colorTransform == 0
                    ? J_COLOR_SPACE.JCS_CMYK
                    : J_COLOR_SPACE.JCS_YCCK;
                outputColorSpace = J_COLOR_SPACE.JCS_CMYK;
                return true;
            default:
                return false;
        }
    }

    private SKBitmap? DecodeDctCmykBitmap(
        jpeg_decompress_struct cinfo,
        int width,
        int height,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        Pdfe.Core.Primitives.PdfStream stream)
    {
        if (cinfo.Output_components != 4)
            return null;

        var samples = new byte[checked(width * height * 4)];
        var scanline = new[] { new byte[checked(width * cinfo.Output_components)] };
        var dst = 0;
        while (cinfo.Output_scanline < cinfo.Output_height)
        {
            cinfo.jpeg_read_scanlines(scanline, 1);
            var row = scanline[0];
            Array.Copy(row, 0, samples, dst, width * 4);
            dst += width * 4;
        }

        cinfo.jpeg_finish_decompress();
        var bitmap = CreateBitmapFromRawData(samples, width, height, bitsPerComponent: 8, "DeviceCMYK", stream);
        if (bitmap == null)
            return null;

        return ResizeDecodedBitmap(
            bitmap,
            Math.Clamp(targetWidth, 1, sourceWidth),
            Math.Clamp(targetHeight, 1, sourceHeight));
    }

    private static bool TryGetAdobeDctColorTransform(byte[] data, out int colorTransform)
    {
        colorTransform = 0;
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            return false;

        var offset = 2;
        while (offset + 3 < data.Length)
        {
            if (data[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            while (offset < data.Length && data[offset] == 0xFF)
                offset++;
            if (offset >= data.Length)
                return false;

            var marker = data[offset++];
            if (marker == 0xDA || marker == 0xD9)
                return false;
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
                continue;
            if (offset + 1 >= data.Length)
                return false;

            var segmentLength = (data[offset] << 8) | data[offset + 1];
            if (segmentLength < 2)
                return false;
            var payloadOffset = offset + 2;
            var nextOffset = offset + segmentLength;
            if (nextOffset > data.Length)
                return false;

            if (marker == 0xEE &&
                segmentLength >= 14 &&
                data[payloadOffset] == (byte)'A' &&
                data[payloadOffset + 1] == (byte)'d' &&
                data[payloadOffset + 2] == (byte)'o' &&
                data[payloadOffset + 3] == (byte)'b' &&
                data[payloadOffset + 4] == (byte)'e')
            {
                colorTransform = data[payloadOffset + 11] switch
                {
                    0 => 0,
                    1 => 1,
                    2 => 1,
                    _ => -1
                };
                return colorTransform >= 0;
            }

            offset = nextOffset;
        }

        return false;
    }

    private static string NormalizeDctColorSpaceName(string colorSpace)
        => colorSpace switch
        {
            "RGB" => "DeviceRGB",
            "CMYK" => "DeviceCMYK",
            _ => colorSpace
        };

    private static int ChooseDctScaleDenominator(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        foreach (var denominator in new[] { 8, 4, 2 })
        {
            if ((sourceWidth + denominator - 1) / denominator >= targetWidth &&
                (sourceHeight + denominator - 1) / denominator >= targetHeight)
            {
                return denominator;
            }
        }

        return 1;
    }

    private static SKBitmap? ResizeDecodedBitmap(SKBitmap bitmap, int targetWidth, int targetHeight)
    {
        if (bitmap.Width == targetWidth && bitmap.Height == targetHeight)
            return bitmap;

        try
        {
            var resized = bitmap.Resize(
                new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            return resized;
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    private static byte[] CreateSoftMaskAlphaFrom8Bit(
        byte[] data,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        Pdfe.Core.Primitives.PdfStream maskStream)
    {
        var alpha = new byte[targetWidth * targetHeight];
        var dst = 0;
        for (int y = 0; y < targetHeight; y++)
        {
            var sourceY = MapTargetToSource(y, targetHeight, sourceHeight);
            var sourceRow = sourceY * sourceWidth;
            for (int x = 0; x < targetWidth; x++)
            {
                var sourceX = MapTargetToSource(x, targetWidth, sourceWidth);
                var sourceIndex = sourceRow + sourceX;
                alpha[dst++] = sourceIndex < data.Length
                    ? DecodeSoftMaskSample(maskStream, data[sourceIndex], 8)
                    : DecodeSoftMaskSample(maskStream, 0, 8);
            }
        }

        return alpha;
    }

    private static byte[] CreateSoftMaskAlphaFromSamples(
        int[] data,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        Pdfe.Core.Primitives.PdfStream maskStream)
    {
        var alpha = new byte[targetWidth * targetHeight];
        var dst = 0;
        for (int y = 0; y < targetHeight; y++)
        {
            var sourceY = MapTargetToSource(y, targetHeight, sourceHeight);
            var sourceRow = sourceY * sourceWidth;
            for (int x = 0; x < targetWidth; x++)
            {
                var sourceX = MapTargetToSource(x, targetWidth, sourceWidth);
                var sourceIndex = sourceRow + sourceX;
                alpha[dst++] = sourceIndex < data.Length
                    ? DecodeSoftMaskSample(maskStream, data[sourceIndex], 8)
                    : DecodeSoftMaskSample(maskStream, 0, 8);
            }
        }

        return alpha;
    }

    private static int ReadOneBitImageSample(byte[] data, int width, int x, int y)
    {
        var rowStrideBits = ((width + 7) / 8) * 8;
        var bitIndex = (long)y * rowStrideBits + x;
        var byteIndex = bitIndex / 8;
        if (byteIndex < 0 || byteIndex >= data.LongLength)
            return 0;

        var bitInByte = 7 - (int)(bitIndex % 8);
        return (data[byteIndex] >> bitInByte) & 1;
    }

    private static int MapTargetToSource(int targetPosition, int targetSize, int sourceSize)
        => Math.Clamp((int)(((targetPosition + 0.5) * sourceSize) / targetSize), 0, sourceSize - 1);

    private static SKSizeI GetDecodeSize(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
        => new(
            Math.Clamp(targetWidth, 1, sourceWidth),
            Math.Clamp(targetHeight, 1, sourceHeight));

    private static bool TryGetSoftMaskReferenceKey(
        Pdfe.Core.Primitives.PdfObject maskObj,
        Pdfe.Core.Primitives.PdfStream maskStream,
        out (int ObjectNumber, int Generation) key)
    {
        if (maskObj is PdfReference reference)
        {
            key = (reference.ObjectNum, reference.Generation);
            return true;
        }

        if (maskStream.ObjectNumber.HasValue)
        {
            key = (maskStream.ObjectNumber.Value, maskStream.GenerationNumber ?? 0);
            return true;
        }

        key = default;
        return false;
    }

    private static byte[] ExtractSoftMaskAlpha(SKBitmap maskBitmap, int width, int height, Pdfe.Core.Primitives.PdfStream maskStream)
    {
        var alpha = new byte[width * height];
        var pixels = maskBitmap.Pixels;
        if (pixels.Length == 0)
            return alpha;

        for (int y = 0; y < height; y++)
        {
            var sourceY = Math.Clamp((int)((long)y * maskBitmap.Height / height), 0, maskBitmap.Height - 1);
            var sourceRow = sourceY * maskBitmap.Width;
            for (int x = 0; x < width; x++)
            {
                var sourceX = Math.Clamp((int)((long)x * maskBitmap.Width / width), 0, maskBitmap.Width - 1);
                var pixel = pixels[sourceRow + sourceX];
                var luma = (byte)Math.Clamp(
                    (0.299 * pixel.Red) + (0.587 * pixel.Green) + (0.114 * pixel.Blue),
                    0,
                    255);
                alpha[y * width + x] = DecodeSoftMaskSample(maskStream, luma, 8);
            }
        }

        return alpha;
    }

    private static byte DecodeSoftMaskSample(Pdfe.Core.Primitives.PdfStream maskStream, int sample, int bitsPerComponent)
    {
        var decode = maskStream.GetOptional("Decode") as Pdfe.Core.Primitives.PdfArray;
        var d0 = decode?.Count >= 2 ? decode.GetNumber(0) : 0.0;
        var d1 = decode?.Count >= 2 ? decode.GetNumber(1) : 1.0;
        var maxSample = Math.Pow(2, bitsPerComponent) - 1;
        var decoded = maxSample > 0
            ? d0 + Math.Clamp(sample, 0, maxSample) * ((d1 - d0) / maxSample)
            : d0;
        return (byte)Math.Clamp((int)Math.Round(decoded * 255), 0, 255);
    }

    private static SKBitmap CreateSoftMaskLumaBitmap(SoftMaskAlpha mask)
    {
        var pixels = new byte[checked(mask.Width * mask.Height * 4)];
        var dst = 0;
        for (int i = 0; i < mask.Data.Length; i++)
        {
            var alpha = mask.Data[i];
            pixels[dst++] = alpha;
            pixels[dst++] = alpha;
            pixels[dst++] = alpha;
            pixels[dst++] = 255;
        }

        return CreateBitmapFromRgbaBytes(mask.Width, mask.Height, pixels)
               ?? new SKBitmap(mask.Width, mask.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
    }

    private SKBitmap? CreateBitmapFromRawData(byte[] data, int width, int height, int bitsPerComponent, string colorSpace, Pdfe.Core.Primitives.PdfStream stream)
    {
        var isImageMask = stream.GetBool("ImageMask");
        PdfColorSpace? pdfColorSpace = null;
        int componentsPerPixel = 3;

        if (bitsPerComponent == 1 && isImageMask)
            return CreateImageMaskBitmapFromPackedBits(data, width, height, stream);

        var csObj = isImageMask ? null : stream.GetOptional("ColorSpace");
        if (csObj != null)
        {
            pdfColorSpace = ResolveImageColorSpace(csObj);
            componentsPerPixel = pdfColorSpace.Components;
        }
        else if (!isImageMask)
        {
            pdfColorSpace = PdfColorSpace.FromName(colorSpace);
            componentsPerPixel = pdfColorSpace.Components;
        }

        if (componentsPerPixel == 0)
            componentsPerPixel = 3;

        var fastBitmap = TryCreateFast8BitBitmapFromRawData(
            data,
            width,
            height,
            bitsPerComponent,
            pdfColorSpace,
            componentsPerPixel,
            stream);
        if (fastBitmap != null)
            return fastBitmap;

        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var pixels = new byte[width * height * 4];

        try
        {
            int srcIndex = 0;
            int dstIndex = 0;
            var pixelValues = new double[componentsPerPixel];
            var imageMaskPaintBits = isImageMask
                ? ResolveImageMaskPaintBits(stream)
                : default;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte r = 0, g = 0, b = 0, a = 255;

                    if (bitsPerComponent == 8 && pdfColorSpace != null)
                    {
                        if (srcIndex + componentsPerPixel <= data.Length)
                        {
                            for (int i = 0; i < componentsPerPixel; i++)
                                pixelValues[i] = DecodeImageSample(
                                    stream,
                                    pdfColorSpace,
                                    i,
                                    data[srcIndex + i],
                                    bitsPerComponent);
                            srcIndex += componentsPerPixel;

                            var (rd, gd, bd) = pdfColorSpace.ToRgb(pixelValues);
                            r = (byte)Math.Clamp(rd * 255, 0, 255);
                            g = (byte)Math.Clamp(gd * 255, 0, 255);
                            b = (byte)Math.Clamp(bd * 255, 0, 255);
                        }
                    }
                    else if (bitsPerComponent == 1)
                    {
                        // 1-bit monochrome
                        int byteIndex = srcIndex / 8;
                        int bitIndex = 7 - (srcIndex % 8);
                        int bit = 0;
                        if (byteIndex < data.Length)
                        {
                            bit = (data[byteIndex] >> bitIndex) & 1;
                        }

                        if (isImageMask)
                        {
                            r = _state.FillColor.Red;
                            g = _state.FillColor.Green;
                            b = _state.FillColor.Blue;
                            a = imageMaskPaintBits.Paints(bit)
                                ? (byte)Math.Clamp(_state.FillAlpha * 255, 0, 255)
                                : (byte)0;
                        }
                        else if (pdfColorSpace != null)
                        {
                            var sample = DecodeOneBitImageSample(stream, bit);
                            var (rd, gd, bd) = pdfColorSpace.ToRgb(new[] { sample });
                            r = (byte)Math.Clamp(rd * 255, 0, 255);
                            g = (byte)Math.Clamp(gd * 255, 0, 255);
                            b = (byte)Math.Clamp(bd * 255, 0, 255);
                        }
                        srcIndex++;
                    }

                    // RGBA format
                    pixels[dstIndex++] = r;
                    pixels[dstIndex++] = g;
                    pixels[dstIndex++] = b;
                    pixels[dstIndex++] = a;
                }

                // Handle row padding for 1-bit images
                if (bitsPerComponent == 1)
                {
                    srcIndex = ((srcIndex + 7) / 8) * 8; // Align to byte boundary
                }
            }

            var destination = bitmap.GetPixels();
            if (destination == IntPtr.Zero)
            {
                bitmap.Dispose();
                return null;
            }

            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, destination, pixels.Length);
        }
        catch
        {
            bitmap.Dispose();
            return null;
        }

        return bitmap;
    }

    private static SKBitmap? TryCreateFast8BitBitmapFromRawData(
        byte[] data,
        int width,
        int height,
        int bitsPerComponent,
        PdfColorSpace? colorSpace,
        int componentsPerPixel,
        Pdfe.Core.Primitives.PdfStream stream)
    {
        if (bitsPerComponent != 8 ||
            colorSpace == null ||
            stream.GetOptional("Decode") != null ||
            width <= 0 ||
            height <= 0)
        {
            return null;
        }

        var expectedPixels = checked((long)width * height);
        var requiredBytes = expectedPixels * componentsPerPixel;
        if (requiredBytes > data.LongLength)
            return null;

        return colorSpace.Type switch
        {
            PdfColorSpaceType.DeviceGray when componentsPerPixel == 1 =>
                CreateFastGrayBitmap(data, width, height),
            PdfColorSpaceType.DeviceRGB when componentsPerPixel == 3 =>
                CreateFastRgbBitmap(data, width, height),
            PdfColorSpaceType.DeviceCMYK when componentsPerPixel == 4 =>
                CreateFastCmykBitmap(data, width, height, colorSpace),
            _ => null
        };
    }

    private static SKBitmap? CreateFastGrayBitmap(byte[] data, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var src = 0;
        var dst = 0;
        for (var i = 0; i < width * height; i++)
        {
            var gray = data[src++];
            pixels[dst++] = gray;
            pixels[dst++] = gray;
            pixels[dst++] = gray;
            pixels[dst++] = 255;
        }

        return CreateBitmapFromRgbaBytes(width, height, pixels);
    }

    private static SKBitmap? CreateFastRgbBitmap(byte[] data, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var src = 0;
        var dst = 0;
        for (var i = 0; i < width * height; i++)
        {
            pixels[dst++] = data[src++];
            pixels[dst++] = data[src++];
            pixels[dst++] = data[src++];
            pixels[dst++] = 255;
        }

        return CreateBitmapFromRgbaBytes(width, height, pixels);
    }

    private static SKBitmap? CreateFastCmykBitmap(byte[] data, int width, int height, PdfColorSpace colorSpace)
    {
        var pixels = new byte[width * height * 4];
        var src = 0;
        var dst = 0;
        for (var i = 0; i < width * height; i++)
        {
            var c = data[src++] / 255.0;
            var m = data[src++] / 255.0;
            var y = data[src++] / 255.0;
            var k = data[src++] / 255.0;
            var (r, g, b) = colorSpace.ToRgb(new[] { c, m, y, k });
            pixels[dst++] = (byte)Math.Clamp(r * 255, 0, 255);
            pixels[dst++] = (byte)Math.Clamp(g * 255, 0, 255);
            pixels[dst++] = (byte)Math.Clamp(b * 255, 0, 255);
            pixels[dst++] = 255;
        }

        return CreateBitmapFromRgbaBytes(width, height, pixels);
    }

    private static SKBitmap? CreateBitmapFromRgbaBytes(
        int width,
        int height,
        byte[] pixels,
        SKAlphaType alphaType = SKAlphaType.Premul)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, alphaType);
        try
        {
            var destination = bitmap.GetPixels();
            if (destination == IntPtr.Zero)
            {
                bitmap.Dispose();
                return null;
            }

            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, destination, pixels.Length);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            return null;
        }
    }

    private SKBitmap? CreateImageMaskBitmapFromPackedBits(
        byte[] data,
        int width,
        int height,
        Pdfe.Core.Primitives.PdfStream stream)
    {
        if (width <= 0 || height <= 0)
            return null;

        var pixels = new byte[width * height * 4];
        var fillAlpha = (byte)Math.Clamp(_state.FillAlpha * 255, 0, 255);
        var paintBits = ResolveImageMaskPaintBits(stream);
        var rowBytes = (width + 7) / 8;
        var dst = 0;

        for (int y = 0; y < height; y++)
        {
            var rowOffset = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                var byteIndex = rowOffset + (x >> 3);
                var bit = byteIndex < data.Length
                    ? (data[byteIndex] >> (7 - (x & 7))) & 1
                    : 0;
                var paint = paintBits.Paints(bit);

                pixels[dst++] = paint ? _state.FillColor.Red : (byte)0;
                pixels[dst++] = paint ? _state.FillColor.Green : (byte)0;
                pixels[dst++] = paint ? _state.FillColor.Blue : (byte)0;
                pixels[dst++] = paint ? fillAlpha : (byte)0;
            }
        }

        return CreateBitmapFromRgbaBytes(width, height, pixels);
    }

    private SKBitmap? CreateImageMaskStencilBitmap(
        byte[] data,
        int width,
        int height,
        Pdfe.Core.Primitives.PdfStream stream)
    {
        if (width <= 0 || height <= 0)
            return null;

        var pixels = new byte[width * height * 4];
        var paintBits = ResolveImageMaskPaintBits(stream);
        var rowBytes = (width + 7) / 8;
        var dst = 0;

        for (int y = 0; y < height; y++)
        {
            var rowOffset = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                var byteIndex = rowOffset + (x >> 3);
                var bit = byteIndex < data.Length
                    ? (data[byteIndex] >> (7 - (x & 7))) & 1
                    : 0;
                var paint = paintBits.Paints(bit);
                var alpha = paint ? (byte)Math.Clamp(_state.FillAlpha * 255, 0, 255) : (byte)0;

                pixels[dst++] = 255;
                pixels[dst++] = 255;
                pixels[dst++] = 255;
                pixels[dst++] = alpha;
            }
        }

        return CreateBitmapFromRgbaBytes(width, height, pixels);
    }

    private static SKBitmap? CreateExplicitImageMaskBitmap(
        Pdfe.Core.Primitives.PdfStream stream,
        int width,
        int height)
    {
        if (width <= 0 || height <= 0)
            return null;

        var bitsPerComponent = stream.GetInt("BitsPerComponent", 1);
        if (bitsPerComponent != 1)
            return null;

        var data = stream.DecodedData;
        var pixels = new byte[width * height * 4];
        var dst = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var bit = ReadOneBitImageSample(data, width, x, y);
                // Explicit image /Mask streams use the same decoded stencil
                // convention to decide which source-image pixels remain
                // visible, but the result becomes alpha instead of current
                // fill color.
                var opaque = DecodeImageMaskBit(stream, bit);
                var alpha = opaque ? (byte)255 : (byte)0;

                pixels[dst++] = 255;
                pixels[dst++] = 255;
                pixels[dst++] = 255;
                pixels[dst++] = alpha;
            }
        }

        return CreateBitmapFromRgbaBytes(width, height, pixels);
    }

    private static double DecodeOneBitImageSample(Pdfe.Core.Primitives.PdfStream stream, int bit)
    {
        var decode = stream.GetOptional("Decode") as Pdfe.Core.Primitives.PdfArray;
        var d0 = decode?.Count >= 2 ? decode.GetNumber(0) : 0.0;
        var d1 = decode?.Count >= 2 ? decode.GetNumber(1) : 1.0;
        return bit == 0 ? d0 : d1;
    }

    private static double DecodeImageSample(
        Pdfe.Core.Primitives.PdfStream stream,
        PdfColorSpace colorSpace,
        int componentIndex,
        byte sample,
        int bitsPerComponent)
    {
        var decode = stream.GetOptional("Decode") as Pdfe.Core.Primitives.PdfArray;
        var offset = componentIndex * 2;
        if (decode != null && decode.Count >= offset + 2)
        {
            var d0 = decode.GetNumber(offset);
            var d1 = decode.GetNumber(offset + 1);
            var maxSample = Math.Pow(2, bitsPerComponent) - 1;
            return maxSample > 0
                ? d0 + sample * ((d1 - d0) / maxSample)
                : d0;
        }

        if (colorSpace.Type == PdfColorSpaceType.Indexed)
            return sample;

        return colorSpace.DecodeSampleByte(componentIndex, sample);
    }

    private static bool DecodeImageMaskBit(Pdfe.Core.Primitives.PdfStream stream, int bit)
    {
        var decode = stream.GetOptional("Decode") as Pdfe.Core.Primitives.PdfArray;
        var d0 = decode?.Count >= 2 ? decode.GetNumber(0) : 0.0;
        var d1 = decode?.Count >= 2 ? decode.GetNumber(1) : 1.0;
        // Image masks are stencils: decoded 0 paints with the current color,
        // decoded 1 is transparent. /Decode [1 0] reverses the source-bit
        // polarity while preserving that decoded-value convention.
        return (bit == 0 ? d0 : d1) < 0.5;
    }

    private static ImageMaskPaintBits ResolveImageMaskPaintBits(Pdfe.Core.Primitives.PdfStream stream)
    {
        if (HasExplicitImageMaskDecode(stream))
            return new ImageMaskPaintBits(DecodeImageMaskBit(stream, 0), DecodeImageMaskBit(stream, 1));

        if (TryGetCcittImageBlackIsOne(stream, out var blackIsOne))
            return blackIsOne
                ? new ImageMaskPaintBits(PaintWhenZero: false, PaintWhenOne: true)
                : new ImageMaskPaintBits(PaintWhenZero: true, PaintWhenOne: false);

        if (HasStreamFilter(stream, "JBIG2Decode"))
        {
            // The JBIG2 decoder returns normalized PDF one-bit image samples:
            // 1 is white/background and 0 is black/foreground. For an image
            // mask without an explicit /Decode, the foreground is the stencil.
            return new ImageMaskPaintBits(PaintWhenZero: true, PaintWhenOne: false);
        }

        return new ImageMaskPaintBits(DecodeImageMaskBit(stream, 0), DecodeImageMaskBit(stream, 1));
    }

    private static bool HasExplicitImageMaskDecode(Pdfe.Core.Primitives.PdfStream stream)
        => stream.GetOptional("Decode") is Pdfe.Core.Primitives.PdfArray decode && decode.Count >= 2;

    private static bool TryGetCcittImageBlackIsOne(
        Pdfe.Core.Primitives.PdfStream stream,
        out bool blackIsOne)
    {
        var filters = stream.Filters;
        var decodeParams = stream.DecodeParams;
        for (int i = filters.Count - 1; i >= 0; i--)
        {
            if (!IsNamedFilter(filters[i], "CCITTFaxDecode"))
                continue;

            var parms = i < decodeParams.Count
                ? decodeParams[i]
                : decodeParams.Count == 1 ? decodeParams[0] : null;
            blackIsOne = parms?.GetBool("BlackIs1", false) ?? false;
            return true;
        }

        blackIsOne = false;
        return false;
    }

    private static bool HasStreamFilter(Pdfe.Core.Primitives.PdfStream stream, string filterName)
    {
        foreach (var filter in stream.Filters)
        {
            if (IsNamedFilter(filter, filterName))
                return true;
        }

        return false;
    }

    private static bool IsNamedFilter(string actual, string expected)
        => string.Equals(actual, expected, StringComparison.Ordinal) ||
           (string.Equals(expected, "CCITTFaxDecode", StringComparison.Ordinal) &&
            string.Equals(actual, "CCF", StringComparison.Ordinal)) ||
           (string.Equals(expected, "JBIG2Decode", StringComparison.Ordinal) &&
            string.Equals(actual, "JBIG2", StringComparison.Ordinal));

    private readonly record struct ImageMaskPaintBits(bool PaintWhenZero, bool PaintWhenOne)
    {
        public bool Paints(int bit) => bit == 0 ? PaintWhenZero : PaintWhenOne;
    }

    private PdfColorSpace ResolveImageColorSpace(Pdfe.Core.Primitives.PdfObject colorSpaceObject)
    {
        if (colorSpaceObject is Pdfe.Core.Primitives.PdfName name)
            return ResolveColorSpace(name.Value) ?? PdfColorSpace.Parse(colorSpaceObject, _page.Document);

        return PdfColorSpace.Parse(colorSpaceObject, _page.Document);
    }

    /// <summary>
    /// Wrap SKBitmap.Decode so any exception (ArgumentNullException
    /// when SkiaSharp can't find a codec for this image format,
    /// AccessViolationException on truncated/corrupt input, etc.)
    /// returns null instead of propagating up and crashing the
    /// page render. Found by the pdf.js corpus differential —
    /// 8 fixtures with JPEG2000 inline images caused
    /// "Value cannot be null. (Parameter 'codec')" because
    /// SkiaSharp's Linux build ships without a JPX codec.
    /// </summary>
    private static SKBitmap? SafeDecode(byte[]? bytes, SKSizeI? targetSize = null)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            if (targetSize is { Width: > 0, Height: > 0 } size)
            {
                var scaled = SKBitmap.Decode(
                    bytes,
                    new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
                if (scaled != null)
                    return scaled;
            }

            return SKBitmap.Decode(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Render every visible annotation on the page on top of the main
    /// content. Each annotation's <c>/AP /N</c> stream is a Form XObject
    /// in the appearance's own coordinate space; we compute the matrix
    /// that maps its <c>/BBox</c> (transformed by /Matrix) onto the
    /// annotation's <c>/Rect</c> per ISO 32000-2 §12.5.5, then dispatch
    /// the appearance through the existing Form XObject pipeline.
    /// Annotations without an /AP entry are skipped — synthesizing a
    /// default appearance from /Subtype-specific properties (sticky-note
    /// icon, link rectangles, etc.) is handled separately, if at all.
    /// </summary>
    private void RenderAnnotations()
    {
        IReadOnlyList<Pdfe.Core.Document.PdfAnnotation> annots;
        try { annots = _page.GetAnnotations(); }
        catch { return; }
        if (annots.Count == 0) return;

        foreach (var annot in annots)
        {
            // Skip annotations the spec says shouldn't be displayed.
            // Print=4 is fine — that's an opt-in for *also* including
            // the annotation in printed output, not a "screen only" flag.
            var f = annot.Flags;
            if ((f & (Pdfe.Core.Document.PdfAnnotationFlags.Hidden
                    | Pdfe.Core.Document.PdfAnnotationFlags.NoView
                    | Pdfe.Core.Document.PdfAnnotationFlags.Invisible)) != 0)
                continue;

            var appearance = ResolveAppearanceN(annot);
            if (appearance == null)
            {
                // No baked /AP /N stream — synthesize a minimal default
                // appearance for the subtypes commercial viewers
                // routinely show (interactive widgets, link rectangles,
                // shape annotations). Without this, signature widgets
                // and unfilled form fields are invisible and PDFs look
                // visibly less complete than in Acrobat / Preview /
                // Chrome.
                RenderDefaultAppearance(annot);
                continue;
            }

            // Appearance bbox + matrix.
            if (appearance.GetOptional("BBox") is not Pdfe.Core.Primitives.PdfArray bboxArr ||
                bboxArr.Count < 4) continue;
            if (!TryGetArrayNumber(bboxArr, 0, out var bx1Value) ||
                !TryGetArrayNumber(bboxArr, 1, out var by1Value) ||
                !TryGetArrayNumber(bboxArr, 2, out var bx2Value) ||
                !TryGetArrayNumber(bboxArr, 3, out var by2Value)) continue;
            float bx1 = (float)bx1Value;
            float by1 = (float)by1Value;
            float bx2 = (float)bx2Value;
            float by2 = (float)by2Value;
            float bMinX = Math.Min(bx1, bx2);
            float bMinY = Math.Min(by1, by2);
            float bMaxX = Math.Max(bx1, bx2);
            float bMaxY = Math.Max(by1, by2);

            var formMatrix = SKMatrix.Identity;
            if (appearance.GetOptional("Matrix") is Pdfe.Core.Primitives.PdfArray mArr && mArr.Count >= 6)
            {
                formMatrix = GetMatrix(mArr);
            }

            // Transform the four bbox corners through the form's /Matrix
            // and take the axis-aligned bounding box of the result. Spec
            // step from §12.5.5: "a quadrilateral whose corners are the
            // four corners of BBox transformed by Matrix … then the
            // smallest rectangle enclosing those four points."
            var p1 = formMatrix.MapPoint(new SKPoint(bMinX, bMinY));
            var p2 = formMatrix.MapPoint(new SKPoint(bMaxX, bMinY));
            var p3 = formMatrix.MapPoint(new SKPoint(bMaxX, bMaxY));
            var p4 = formMatrix.MapPoint(new SKPoint(bMinX, bMaxY));
            float bbMinX = Math.Min(Math.Min(p1.X, p2.X), Math.Min(p3.X, p4.X));
            float bbMinY = Math.Min(Math.Min(p1.Y, p2.Y), Math.Min(p3.Y, p4.Y));
            float bbMaxX = Math.Max(Math.Max(p1.X, p2.X), Math.Max(p3.X, p4.X));
            float bbMaxY = Math.Max(Math.Max(p1.Y, p2.Y), Math.Max(p3.Y, p4.Y));
            if (bbMaxX <= bbMinX || bbMaxY <= bbMinY) continue;

            // Annotation /Rect (PDF stores [llx lly urx ury], but some
            // producers swap pairs — normalize both ways).
            float rx1 = (float)Math.Min(annot.Rect.Left, annot.Rect.Right);
            float ry1 = (float)Math.Min(annot.Rect.Bottom, annot.Rect.Top);
            float rx2 = (float)Math.Max(annot.Rect.Left, annot.Rect.Right);
            float ry2 = (float)Math.Max(annot.Rect.Bottom, annot.Rect.Top);
            if (rx2 <= rx1 || ry2 <= ry1) continue;

            // A = scale + translate that maps the AABB of the transformed
            // bbox onto Rect. RenderFormXObject will additionally concat
            // the form's own Matrix, so the final on-page transform is
            // A · Matrix, which by construction takes BBox → Rect.
            float sx = (rx2 - rx1) / (bbMaxX - bbMinX);
            float sy = (ry2 - ry1) / (bbMaxY - bbMinY);
            float tx = rx1 - bbMinX * sx;
            float ty = ry1 - bbMinY * sy;
            var fitMatrix = new SKMatrix(sx, 0, tx, 0, sy, ty, 0, 0, 1);

            _canvas.Save();
            try
            {
                _canvas.ClipRect(new SKRect(rx1, ry1, rx2, ry2), SKClipOperation.Intersect, _options.AntiAlias);
                _canvas.Concat(in fitMatrix);
                RenderFormXObject(appearance);
            }
            catch
            {
                // Never let one malformed annotation kill the rest of
                // the page; it's strictly an overlay on top of content
                // we've already successfully rendered.
            }
            finally
            {
                _canvas.Restore();
            }
        }
    }

    /// <summary>
    /// Resolve <paramref name="annot"/>'s normal appearance to a Form
    /// XObject stream. <c>/AP /N</c> is either:
    /// <list type="bullet">
    /// <item>a single stream — used regardless of state, or</item>
    /// <item>a dictionary keyed by state name (Off / Yes / etc.) where
    ///   <c>/AS</c> picks the active entry — Widget annotations and
    ///   appearance-stateful ones use this.</item>
    /// </list>
    /// Returns null when no usable appearance is present.
    /// </summary>
    private Pdfe.Core.Primitives.PdfStream? ResolveAppearanceN(Pdfe.Core.Document.PdfAnnotation annot)
    {
        var apObj = annot.RawDictionary.GetOptional("AP");
        if (apObj == null) return null;
        if (_page.Document.Resolve(apObj) is not Pdfe.Core.Primitives.PdfDictionary ap) return null;
        var nObj = ap.GetOptional("N");
        if (nObj == null) return null;
        var resolved = _page.Document.Resolve(nObj);

        if (resolved is Pdfe.Core.Primitives.PdfStream stream)
            return stream;

        if (resolved is Pdfe.Core.Primitives.PdfDictionary stateDict)
        {
            var stateName = annot.RawDictionary.GetNameOrNull("AS");
            if (stateName != null)
            {
                var stateObj = stateDict.GetOptional(stateName);
                if (stateObj != null &&
                    _page.Document.Resolve(stateObj) is Pdfe.Core.Primitives.PdfStream s)
                    return s;
            }
            // No /AS or unknown state — fall through to first usable entry.
            foreach (var kvp in stateDict)
            {
                if (_page.Document.Resolve(kvp.Value) is Pdfe.Core.Primitives.PdfStream s)
                    return s;
            }
        }
        return null;
    }

    /// <summary>
    /// Synthesize a minimum-viable visual for an annotation without
    /// <c>/AP /N</c>. Modeled after what Acrobat / Preview / Chrome show
    /// for interactive PDFs — a colored rectangle around the field —
    /// not a full reproduction of the field's would-be value (we don't
    /// interpret /DA + /V here; that's a substantial separate feature).
    /// Covers:
    /// <list type="bullet">
    /// <item><c>/Widget</c>: form-field highlight rectangle (background
    ///   from <c>/MK /BG</c> if present, border from <c>/MK /BC</c>
    ///   plus the <c>/BS</c> width, falling back to a neutral
    ///   light-blue field highlight similar to Acrobat's default).</item>
    /// <item><c>/Link</c>: thin border using the annotation's <c>/C</c>
    ///   color when present (links without /C are intentionally
    ///   invisible in print, matching every commercial viewer).</item>
    /// <item><c>/Square</c> / <c>/Circle</c>: stroked rectangle / ellipse
    ///   using <c>/C</c> + <c>/BS</c>.</item>
    /// </list>
    /// </summary>
    private void RenderDefaultAppearance(Pdfe.Core.Document.PdfAnnotation annot)
    {
        // PDF Y-up Rect; normalize so min < max.
        float rx1 = (float)Math.Min(annot.Rect.Left, annot.Rect.Right);
        float ry1 = (float)Math.Min(annot.Rect.Bottom, annot.Rect.Top);
        float rx2 = (float)Math.Max(annot.Rect.Left, annot.Rect.Right);
        float ry2 = (float)Math.Max(annot.Rect.Bottom, annot.Rect.Top);
        if (rx2 - rx1 < 0.5f || ry2 - ry1 < 0.5f) return;

        var rect = new SKRect(rx1, ry1, rx2, ry2);

        switch (annot.Subtype)
        {
            case Pdfe.Core.Document.PdfAnnotationSubtype.Widget:
                RenderWidgetDefault(annot, rect);
                break;
            case Pdfe.Core.Document.PdfAnnotationSubtype.Link:
                RenderLinkDefault(annot, rect);
                break;
            case Pdfe.Core.Document.PdfAnnotationSubtype.Square:
                RenderShapeDefault(annot, rect, isEllipse: false);
                break;
            case Pdfe.Core.Document.PdfAnnotationSubtype.Circle:
                RenderShapeDefault(annot, rect, isEllipse: true);
                break;
            case Pdfe.Core.Document.PdfAnnotationSubtype.Highlight:
            case Pdfe.Core.Document.PdfAnnotationSubtype.Underline:
            case Pdfe.Core.Document.PdfAnnotationSubtype.Squiggly:
            case Pdfe.Core.Document.PdfAnnotationSubtype.StrikeOut:
                RenderTextMarkupDefault(annot, rect);
                break;
        }
    }

    /// <summary>
    /// Resolve and cache the AcroForm <c>/DR</c> resources dict (where
    /// the document's interactive form keeps its default fonts) plus
    /// the AcroForm <c>/DA</c> default-appearance string. Both are used
    /// when a widget annotation lacks its own <c>/AP</c> and falls back
    /// to drawing the field value through the variable-text path.
    /// Cached per-render-context so we don't re-resolve per widget.
    /// </summary>
    private Pdfe.Core.Primitives.PdfDictionary? _acroFormDr;
    private string? _acroFormDa;
    private bool _acroFormResolved;
    private void ResolveAcroFormResources()
    {
        if (_acroFormResolved) return;
        _acroFormResolved = true;
        var afObj = _page.Document.Catalog.GetOptional("AcroForm");
        if (afObj == null) return;
        if (_page.Document.Resolve(afObj) is not Pdfe.Core.Primitives.PdfDictionary af) return;
        _acroFormDa = af.GetStringOrNull("DA");
        var drObj = af.GetOptional("DR");
        if (drObj == null) return;
        _acroFormDr = _page.Document.Resolve(drObj) as Pdfe.Core.Primitives.PdfDictionary;
    }

    /// <summary>
    /// Render a default appearance for a Widget annotation that lacks
    /// <c>/AP</c>. Two distinct cases:
    ///
    /// <list type="number">
    /// <item><b>Signature widgets (<c>/FT /Sig</c>):</b> draw a visible
    ///   placeholder border so the user can see "sign here." This
    ///   matches mutool's behaviour and what every commercial viewer
    ///   does for unsigned signature fields. Color comes from
    ///   <c>/MK /BC</c> when set, falling back to a neutral border
    ///   tone that's visible against white but not jarring.</item>
    /// <item><b>Other widgets (<c>/Tx</c>, <c>/Btn</c>, <c>/Ch</c>) with
    ///   <c>/MK</c> styling:</b> render background and/or border using
    ///   the explicitly-supplied colors. Skip when no /MK is set —
    ///   text fields in unfilled forms (IRS-1040, passport renewals,
    ///   etc.) are intentionally invisible at print time and adding
    ///   our own borders here makes pdfe's output diverge from mutool
    ///   by ~10% on real-world form PDFs.</item>
    /// </list>
    /// </summary>
    private void RenderWidgetDefault(Pdfe.Core.Document.PdfAnnotation annot, SKRect rect)
    {
        var fieldType = annot.RawDictionary.GetNameOrNull("FT");
        var mk = annot.RawDictionary.GetOptional("MK") is { } mkObj
            ? _page.Document.Resolve(mkObj) as Pdfe.Core.Primitives.PdfDictionary
            : null;

        var bgColor = mk != null ? ParseColorArray(mk.GetOptional("BG")) : null;
        var bcColor = mk != null ? ParseColorArray(mk.GetOptional("BC")) : null;
        bool isSignature = fieldType == "Sig";
        bool hasExplicitStyle = bgColor.HasValue || bcColor.HasValue;

        // Text fields with a value /V should render the value even
        // without /AP — common in unflattened filled forms (Acrobat,
        // Foxit and mutool all do this). Pull /V and route through the
        // variable-text path before falling back to the empty-field
        // policy.
        if (fieldType == "Tx")
        {
            var rawV = annot.RawDictionary.GetOptional("V");
            string? value = rawV != null
                ? ExtractStringFromObject(_page.Document.Resolve(rawV))
                : null;
            if (!string.IsNullOrEmpty(value))
            {
                RenderTextFieldValue(annot, rect, value!);
                if (!hasExplicitStyle) return;
            }
        }

        // Only signature fields get a synthesized "sign here" placeholder
        // border. Text / button / choice widgets in unfilled forms are
        // routinely emitted without /AP and are intentionally invisible
        // until filled — mutool, Poppler and Foxit all leave them blank
        // unless the author opted into /MK styling.
        if (!isSignature && !hasExplicitStyle) return;

        float borderWidth = (float)(annot.BorderWidth ?? 1.0);
        _canvas.Save();
        try
        {
            using var paint = new SKPaint { IsAntialias = _options.AntiAlias };

            if (bgColor.HasValue)
            {
                paint.Style = SKPaintStyle.Fill;
                paint.Color = bgColor.Value;
                _canvas.DrawRect(rect, paint);
            }

            // Border: use /MK /BC when supplied. For signature fields
            // without /MK, fall back to a neutral medium-blue tone —
            // the goal is "user can see the field exists," not pixel
            // parity with any specific viewer.
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = borderWidth;
            paint.Color = bcColor ?? new SKColor(0x66, 0x99, 0xFF, 0xFF);
            _canvas.DrawRect(rect, paint);
        }
        finally
        {
            _canvas.Restore();
        }
    }

    /// <summary>
    /// Stroke a thin border around a /Link annotation when /C is set —
    /// mimics Acrobat's "show all link borders" default. Without /C the
    /// link is invisible in print, which matches every commercial viewer.
    /// </summary>
    private void RenderLinkDefault(Pdfe.Core.Document.PdfAnnotation annot, SKRect rect)
    {
        if (annot.Color is not { } color) return;
        var (r, g, b) = color;
        float borderWidth = (float)(annot.BorderWidth ?? 1.0);

        using var paint = new SKPaint
        {
            IsAntialias = _options.AntiAlias,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = borderWidth,
            Color = RgbToColor(r, g, b),
        };
        _canvas.DrawRect(rect, paint);
    }

    /// <summary>
    /// Stroke a Square or Circle annotation outline using its /C color
    /// and /BS width. These annotations are rare without /AP — most
    /// authoring tools bake an appearance — but the few that don't
    /// fall back here.
    /// </summary>
    private void RenderShapeDefault(
        Pdfe.Core.Document.PdfAnnotation annot, SKRect rect, bool isEllipse)
    {
        if (annot.Color is not { } color) return;
        var (r, g, b) = color;
        float borderWidth = (float)(annot.BorderWidth ?? 1.0);

        using var paint = new SKPaint
        {
            IsAntialias = _options.AntiAlias,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = borderWidth,
            Color = RgbToColor(r, g, b),
        };
        if (isEllipse) _canvas.DrawOval(rect, paint);
        else _canvas.DrawRect(rect, paint);
    }

    /// <summary>
    /// Render a text-markup annotation when the PDF omits /AP /N.
    /// This intentionally stays simple: exact quad geometry is already
    /// reduced to per-quad boxes by PdfAnnotationParser, which is enough
    /// for the common no-appearance highlight/comment fixtures.
    /// </summary>
    private void RenderTextMarkupDefault(
        Pdfe.Core.Document.PdfAnnotation annot, SKRect fallbackRect)
    {
        var boxes = annot.QuadPoints is { Count: > 0 }
            ? annot.QuadPoints.Select(NormalizeAnnotationRect)
            : new[] { fallbackRect };

        var baseColor = AnnotationMarkupColor(annot);
        using var paint = new SKPaint
        {
            IsAntialias = _options.AntiAlias,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };

        _canvas.Save();
        try
        {
            foreach (var box in boxes)
            {
                if (box.Width < 0.5f || box.Height < 0.5f)
                    continue;

                switch (annot.Subtype)
                {
                    case Pdfe.Core.Document.PdfAnnotationSubtype.Highlight:
                        paint.Style = SKPaintStyle.Fill;
                        paint.BlendMode = SKBlendMode.Multiply;
                        paint.Color = WithAlpha(baseColor, AnnotationOpacityAlpha(annot));
                        var radius = Math.Min(box.Height * 0.5f, box.Width * 0.5f);
                        var highlightBox = box;
                        highlightBox.Inflate(radius, 0);
                        _canvas.DrawRoundRect(highlightBox, radius, radius, paint);
                        paint.BlendMode = SKBlendMode.SrcOver;
                        break;

                    case Pdfe.Core.Document.PdfAnnotationSubtype.Underline:
                        DrawMarkupLine(box, baseColor, box.Top + box.Height * 0.12f, paint);
                        break;

                    case Pdfe.Core.Document.PdfAnnotationSubtype.StrikeOut:
                        DrawMarkupLine(box, baseColor, box.MidY, paint);
                        break;

                    case Pdfe.Core.Document.PdfAnnotationSubtype.Squiggly:
                        DrawMarkupSquiggly(box, baseColor, paint);
                        break;
                }
            }
        }
        finally
        {
            _canvas.Restore();
        }
    }

    private static SKRect NormalizeAnnotationRect(Pdfe.Core.Document.PdfRectangle rect)
    {
        float rx1 = (float)Math.Min(rect.Left, rect.Right);
        float ry1 = (float)Math.Min(rect.Bottom, rect.Top);
        float rx2 = (float)Math.Max(rect.Left, rect.Right);
        float ry2 = (float)Math.Max(rect.Bottom, rect.Top);
        return new SKRect(rx1, ry1, rx2, ry2);
    }

    private void DrawMarkupLine(SKRect box, SKColor color, float y, SKPaint paint)
    {
        paint.Style = SKPaintStyle.Stroke;
        paint.BlendMode = SKBlendMode.SrcOver;
        paint.Color = WithAlpha(color, 230);
        paint.StrokeWidth = Math.Clamp(box.Height * 0.08f, 1.0f, 3.0f);
        paint.StrokeCap = SKStrokeCap.Round;
        paint.StrokeJoin = SKStrokeJoin.Round;
        _canvas.DrawLine(box.Left, y, box.Right, y, paint);
    }

    private void DrawMarkupSquiggly(SKRect box, SKColor color, SKPaint paint)
    {
        paint.Style = SKPaintStyle.Stroke;
        paint.BlendMode = SKBlendMode.SrcOver;
        paint.Color = WithAlpha(color, 230);
        paint.StrokeWidth = Math.Clamp(box.Height * 0.06f, 1.0f, 2.5f);
        paint.StrokeCap = SKStrokeCap.Round;
        paint.StrokeJoin = SKStrokeJoin.Round;

        float amplitude = Math.Clamp(box.Height * 0.08f, 1.0f, 3.0f);
        float step = Math.Max(2.0f, amplitude * 2.0f);
        float baseline = box.Top + box.Height * 0.16f;
        using var path = new SKPath();
        path.MoveTo(box.Left, baseline);

        bool up = true;
        for (float x = box.Left + step; x <= box.Right; x += step)
        {
            path.LineTo(x, baseline + (up ? amplitude : -amplitude));
            up = !up;
        }
        path.LineTo(box.Right, baseline);
        _canvas.DrawPath(path, paint);
    }

    private static SKColor AnnotationMarkupColor(Pdfe.Core.Document.PdfAnnotation annot)
    {
        if (annot.Color is { } color)
        {
            var (r, g, b) = color;
            return RgbToColor(r, g, b);
        }

        return annot.Subtype == Pdfe.Core.Document.PdfAnnotationSubtype.Highlight
            ? new SKColor(255, 255, 0)
            : SKColors.Black;
    }

    private static SKColor WithAlpha(SKColor color, byte alpha) =>
        new(color.Red, color.Green, color.Blue, alpha);

    private static byte AnnotationOpacityAlpha(Pdfe.Core.Document.PdfAnnotation annot)
    {
        var opacity = annot.RawDictionary.GetNumber("CA", 1.0);
        if (double.IsNaN(opacity) || double.IsInfinity(opacity))
            opacity = 1.0;
        opacity = Math.Clamp(opacity, 0.0, 1.0);
        return (byte)Math.Round(opacity * 255.0);
    }

    /// <summary>
    /// Parse a PDF color array (1, 3, or 4 components — gray / RGB /
    /// CMYK) into an SKColor. Returns null when the value isn't a valid
    /// array of numbers.
    /// </summary>
    private SKColor? ParseColorArray(Pdfe.Core.Primitives.PdfObject? obj)
    {
        if (obj == null) return null;
        var resolved = _page.Document.Resolve(obj);
        if (resolved is not Pdfe.Core.Primitives.PdfArray arr || arr.Count == 0) return null;
        try
        {
            switch (arr.Count)
            {
                case 1:
                    return GrayToColor(arr.GetNumber(0));
                case 3:
                    return RgbToColor(arr.GetNumber(0), arr.GetNumber(1), arr.GetNumber(2));
                case 4:
                    return CmykToColor(
                        arr.GetNumber(0), arr.GetNumber(1),
                        arr.GetNumber(2), arr.GetNumber(3));
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Render the <c>/V</c> value of a text field widget that has no
    /// <c>/AP /N</c> to fall back on. Mirrors the variable-text
    /// algorithm from PDF 32000-2 §12.7.4.3:
    ///
    /// <list type="number">
    /// <item>Pick a default-appearance string — widget's <c>/DA</c> if
    ///   set, else the AcroForm-level <c>/DA</c>.</item>
    /// <item>Push the AcroForm <c>/DR</c> resources so font names in
    ///   <c>/DA</c> resolve (the widget's own /Resources are usually
    ///   empty for unfilled fields).</item>
    /// <item>Tokenize <c>/DA</c> and execute its operators against a
    ///   fresh text state — sets the active font, size, fill colour.</item>
    /// <item>Position the value text inside the rect with horizontal
    ///   alignment from <c>/Q</c> (0=left, 1=center, 2=right) and
    ///   vertical centering.</item>
    /// <item>Draw the string via the regular RenderText path so font
    ///   substitution / cmap / CID handling all share the same code.</item>
    /// </list>
    /// </summary>
    private void RenderTextFieldValue(
        Pdfe.Core.Document.PdfAnnotation annot, SKRect rect, string value)
    {
        ResolveAcroFormResources();

        var da = annot.RawDictionary.GetStringOrNull("DA") ?? _acroFormDa;
        if (string.IsNullOrEmpty(da)) return;

        // Auto-size 0 in /DA means "fit text to height" per spec; pick a
        // pragmatic default (75% of rect height, capped at 16pt) so the
        // value is at least visible. Real Acrobat does iterative fitting
        // — we approximate.
        float autoSize = Math.Min(rect.Height * 0.75f, 16f);
        if (autoSize < 4f) autoSize = 4f;

        _resourcesStack.Push(_acroFormDr);
        _canvas.Save();
        try
        {
            // Save and reset the text state so /DA's Tf / g / rg etc.
            // don't leak back into the page-level text state we've been
            // accumulating.
            var savedTextState = CloneTextState();
            var savedFillColor = _state.FillColor;
            var savedStrokeColor = _state.StrokeColor;
            var savedTypeface = _currentTypeface;
            var savedFontHasEmbeddedProgram = _currentFontHasEmbeddedProgram;
            var savedByteToGlyph = _currentByteToGlyph;
            var savedCffCidToGlyph = _currentCffCidToGlyph;
            var savedCidToGidMap = _currentCidToGidMap;
            var savedCidUseUnicodeCmap = _currentCidUseUnicodeCmap;
            var savedCidEncodingCMap = _currentCidEncodingCMap;
            var savedFontIsType0 = _currentFontIsType0;
            var savedCidWidths = _currentCidWidths;
            var savedCurrentFontWidths = _currentFontWidths;
            var savedFontFirstChar = _currentFontFirstChar;
            var savedFontMissingWidth = _currentFontMissingWidth;
            var savedFontEncoding = _currentFontEncoding;
            var savedCodeToUnicode = _currentCodeToUnicode;
            var savedUnicodeToCode = _currentUnicodeToCode;
            var savedCodeToGlyphName = _currentCodeToGlyphName;
            try
            {
                _textState = new TextState();

                // Run /DA — sets _textState.FontName/FontSize, fill colour, etc.
                ExecuteContentBytes(Encoding.Latin1.GetBytes(da!));

                float fontSize = _textState.FontSize > 0.001f
                    ? _textState.FontSize : autoSize;
                if (_currentTypeface == null)
                    _currentTypeface = GetTypeface("Helvetica");

                // Measure text to compute alignment. Use the active
                // typeface so the width matches what we're about to draw.
                using var measureFont = new SKFont(_currentTypeface, fontSize);
                using var measurePaint = new SKPaint();
                float textWidth = measureFont.MeasureText(value, measurePaint);

                int q = annot.RawDictionary.GetInt("Q", 0);
                const float padX = 2f;
                float textX;
                if (q == 1)      textX = rect.Left + (rect.Width - textWidth) * 0.5f;
                else if (q == 2) textX = rect.Right - textWidth - padX;
                else             textX = rect.Left + padX;

                // Vertical baseline: center the cap-height inside the
                // rect. fontSize × 0.3 puts the baseline below center
                // by roughly the descender's worth, which looks about
                // right for typical fonts at typical sizes.
                float textY = rect.Top + (rect.Height + fontSize * 0.7f) * 0.5f
                              - fontSize * 0.5f;

                // Drive RenderText through the standard text-block path.
                _inTextBlock = true;
                _textState.TextMatrixA = 1; _textState.TextMatrixB = 0;
                _textState.TextMatrixC = 0; _textState.TextMatrixD = 1;
                _textState.TextMatrixE = textX;
                _textState.TextMatrixF = textY;
                _textState.LineMatrixE = textX;
                _textState.LineMatrixF = textY;
                _textState.FontSize = fontSize;

                // Latin-1 round-trip into bytes — same shape as a Tj
                // operand. RenderText then handles cmap / encoding for
                // the resolved typeface.
                var bytes = Encoding.Latin1.GetBytes(value);
                RenderText(value, bytes);
                EndText();
            }
            finally
            {
                _textState = savedTextState;
                _state.FillColor = savedFillColor;
                _state.StrokeColor = savedStrokeColor;
                _currentTypeface = savedTypeface;
                _currentFontHasEmbeddedProgram = savedFontHasEmbeddedProgram;
                _currentByteToGlyph = savedByteToGlyph;
                _currentCffCidToGlyph = savedCffCidToGlyph;
                _currentCidToGidMap = savedCidToGidMap;
                _currentCidUseUnicodeCmap = savedCidUseUnicodeCmap;
                _currentCidEncodingCMap = savedCidEncodingCMap;
                _currentFontIsType0 = savedFontIsType0;
                _currentCidWidths = savedCidWidths;
                _currentFontWidths = savedCurrentFontWidths;
                _currentFontFirstChar = savedFontFirstChar;
                _currentFontMissingWidth = savedFontMissingWidth;
                _currentFontEncoding = savedFontEncoding;
                _currentCodeToUnicode = savedCodeToUnicode;
                _currentUnicodeToCode = savedUnicodeToCode;
                _currentCodeToGlyphName = savedCodeToGlyphName;
            }
        }
        catch
        {
            // A malformed /DA shouldn't kill the rest of the page; the
            // widget just stays unrendered.
        }
        finally
        {
            _canvas.Restore();
            _resourcesStack.Pop();
        }
    }

    /// <summary>
    /// Pull a string out of a /V or similar value object — handles both
    /// PDF string literals (most common) and PDF names (rare).
    /// </summary>
    private static string? ExtractStringFromObject(Pdfe.Core.Primitives.PdfObject? obj)
    {
        return obj switch
        {
            Pdfe.Core.Primitives.PdfString s => s.Value,
            Pdfe.Core.Primitives.PdfName n => n.Value,
            _ => null,
        };
    }

    private TextState CloneTextState()
    {
        return new TextState
        {
            FontName = _textState.FontName,
            FontSize = _textState.FontSize,
            CharSpacing = _textState.CharSpacing,
            WordSpacing = _textState.WordSpacing,
            HorizontalScale = _textState.HorizontalScale,
            TextLeading = _textState.TextLeading,
            TextRise = _textState.TextRise,
            RenderMode = _textState.RenderMode,
            TextMatrixA = _textState.TextMatrixA,
            TextMatrixB = _textState.TextMatrixB,
            TextMatrixC = _textState.TextMatrixC,
            TextMatrixD = _textState.TextMatrixD,
            TextMatrixE = _textState.TextMatrixE,
            TextMatrixF = _textState.TextMatrixF,
            LineMatrixE = _textState.LineMatrixE,
            LineMatrixF = _textState.LineMatrixF,
        };
    }

    private void RenderFormXObject(Pdfe.Core.Primitives.PdfStream formStream)
    {
        // Cycle detection: a Form XObject that ends up invoking itself
        // (transitively) would otherwise recurse until the .NET stack
        // overflows, which is uncatchable and aborts the whole process.
        if (!_formXObjectStack.Add(formStream)) return;
        if (_formXObjectDepth >= MaxFormXObjectDepth)
        {
            _formXObjectStack.Remove(formStream);
            return;
        }
        _formXObjectDepth++;

        try
        {
            RenderFormXObjectInner(formStream);
        }
        finally
        {
            _formXObjectStack.Remove(formStream);
            _formXObjectDepth--;
        }
    }

    private void RenderFormXObjectInner(Pdfe.Core.Primitives.PdfStream formStream)
    {
        // Form XObjects contain their own content stream
        // Get the form's content and render it recursively
        var formContent = formStream.DecodedData;
        if (formContent.Length == 0)
            return;

        var savedCanvasCount = _canvas.SaveCount;
        var savedStateStack = SnapshotGraphicsStateStack();
        var savedState = _state.Clone();
        var savedTextState = _textState.Clone();
        var savedInTextBlock = _inTextBlock;
        var savedCurrentPath = _currentPath;
        var savedPendingClipEvenOdd = _pendingClipEvenOdd;
        var savedPendingTextClipPath = _pendingTextClipPath;

        _currentPath = null;
        _pendingClipEvenOdd = null;
        _pendingTextClipPath = null;
        _canvas.Save();

        // Push the form's own /Resources so font / XObject lookups inside
        // its content stream resolve against the form's resource dict
        // first (with fallback to outer scopes via the resources stack).
        // PDF 32000-2 §7.8.3: a Form XObject inherits resources from its
        // page, so falling through is required for forms that omit names
        // their content references.
        var formResources = formStream.GetOptional("Resources") is { } resObj
            ? _page.Document.Resolve(resObj) as Pdfe.Core.Primitives.PdfDictionary
            : null;
        _resourcesStack.Push(formResources);

        try
        {
            // Apply the form's transformation matrix if present
            var matrixArray = formStream.GetOptional("Matrix") as Pdfe.Core.Primitives.PdfArray;
            if (matrixArray != null && matrixArray.Count >= 6)
            {
                var matrix = GetMatrix(matrixArray);
                _canvas.Concat(in matrix);
                _state.CurrentTransform = Concat(_state.CurrentTransform, matrix);
            }

            var bboxArray = ResolveArray(formStream, "BBox");
            if (bboxArray != null && bboxArray.Count >= 4)
            {
                var x0 = (float)ArrayNumberOrDefault(bboxArray, 0);
                var y0 = (float)ArrayNumberOrDefault(bboxArray, 1);
                var x1 = (float)ArrayNumberOrDefault(bboxArray, 2);
                var y1 = (float)ArrayNumberOrDefault(bboxArray, 3);
                var bounds = new SKRect(
                    Math.Min(x0, x1),
                    Math.Min(y0, y1),
                    Math.Max(x0, x1),
                    Math.Max(y0, y1));
                if (bounds.Width > 0 && bounds.Height > 0)
                    _canvas.ClipRect(bounds, SKClipOperation.Intersect, _options.AntiAlias);
            }

            // Parse and render the form's content stream through the same
            // typed operator path as normal page content. Resource resolution
            // stays on the renderer's stack, so local form resources still
            // override inherited page resources during execution.
            ExecuteContentBytes(formContent);
        }
        finally
        {
            _currentPath?.Dispose();
            _pendingTextClipPath?.Dispose();
            RestoreGraphicsStateStack(savedStateStack);
            _state = savedState;
            _textState = savedTextState;
            _inTextBlock = savedInTextBlock;
            _currentPath = savedCurrentPath;
            _pendingClipEvenOdd = savedPendingClipEvenOdd;
            _pendingTextClipPath = savedPendingTextClipPath;
            _resourcesStack.Pop();
            _canvas.RestoreToCount(savedCanvasCount);
        }
    }

    private GraphicsState[] SnapshotGraphicsStateStack()
    {
        var snapshot = _stateStack.ToArray();
        for (var i = 0; i < snapshot.Length; i++)
            snapshot[i] = snapshot[i].Clone();
        return snapshot;
    }

    private void RestoreGraphicsStateStack(GraphicsState[] snapshot)
    {
        _stateStack.Clear();
        for (var i = snapshot.Length - 1; i >= 0; i--)
            _stateStack.Push(snapshot[i]);
    }

    private CurrentFontState SnapshotCurrentFontState() => new(
        _currentFontWidths,
        _currentFontFirstChar,
        _currentFontMissingWidth,
        _currentCodeToUnicode,
        _currentUnicodeToCode,
        _currentCodeToGlyphName,
        _currentFontDict,
        _currentTypeface,
        _currentByteToGlyph,
        _currentFontEncoding,
        _currentFontIsType0,
        _currentFontIsType3,
        _currentFontHasEmbeddedProgram,
        _currentCidWidths,
        _currentCidDefaultWidth,
        _currentCidUseUnicodeCmap,
        _currentCidEncodingCMap,
        _currentCidToGidMap,
        _currentCffCidToGlyph);

    private void RestoreCurrentFontState(CurrentFontState state)
    {
        _currentFontWidths = state.FontWidths;
        _currentFontFirstChar = state.FontFirstChar;
        _currentFontMissingWidth = state.FontMissingWidth;
        _currentCodeToUnicode = state.CodeToUnicode;
        _currentUnicodeToCode = state.UnicodeToCode;
        _currentCodeToGlyphName = state.CodeToGlyphName;
        _currentFontDict = state.FontDict;
        _currentTypeface = state.Typeface;
        _currentByteToGlyph = state.ByteToGlyph;
        _currentFontEncoding = state.FontEncoding;
        _currentFontIsType0 = state.FontIsType0;
        _currentFontIsType3 = state.FontIsType3;
        _currentFontHasEmbeddedProgram = state.FontHasEmbeddedProgram;
        _currentCidWidths = state.CidWidths;
        _currentCidDefaultWidth = state.CidDefaultWidth;
        _currentCidUseUnicodeCmap = state.CidUseUnicodeCmap;
        _currentCidEncodingCMap = state.CidEncodingCMap;
        _currentCidToGidMap = state.CidToGidMap;
        _currentCffCidToGlyph = state.CffCidToGlyph;
    }

    private sealed record CurrentFontState(
        float[]? FontWidths,
        int FontFirstChar,
        float FontMissingWidth,
        char[]? CodeToUnicode,
        Dictionary<char, byte>? UnicodeToCode,
        string?[]? CodeToGlyphName,
        Pdfe.Core.Primitives.PdfDictionary? FontDict,
        SKTypeface? Typeface,
        ushort[]? ByteToGlyph,
        string FontEncoding,
        bool FontIsType0,
        bool FontIsType3,
        bool FontHasEmbeddedProgram,
        Dictionary<int, float>? CidWidths,
        float CidDefaultWidth,
        bool CidUseUnicodeCmap,
        CidCMap? CidEncodingCMap,
        ushort[]? CidToGidMap,
        Dictionary<int, int>? CffCidToGlyph);

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

    #region Shading (sh operator) - Issue #300

    private bool RenderFillPattern(SKPath path)
    {
        if (_state.FillPatternName == null)
            return false;

        var pattern = ResolvePatternFromActiveResources(_state.FillPatternName);
        if (pattern == null)
            return false;

        if (pattern.GetInt("PatternType", 0) == 1)
            return RenderTilingPattern(path, pattern);

        if (pattern.GetInt("PatternType", 0) != 2)
            return false;

        var shadingObj = pattern.GetOptional("Shading");
        if (shadingObj == null)
            return false;
        var shading = _page.Document.Resolve(shadingObj) as Pdfe.Core.Primitives.PdfDictionary;
        if (shading == null)
            return false;

        return shading.GetInt("ShadingType", 0) switch
        {
            1 or 2 or 3 => RenderShadingPattern(path, pattern, shading),
            4 => RenderType4MeshPattern(path, pattern, shading),
            6 => RenderType6MeshPattern(path, pattern, shading),
            _ => false
        };
    }

    private bool RenderTilingPattern(SKPath clipPath, Pdfe.Core.Primitives.PdfDictionary pattern)
    {
        if (pattern is not Pdfe.Core.Primitives.PdfStream stream)
            return false;

        var content = stream.DecodedData;
        if (content.Length == 0)
            return false;

        var bboxArray = pattern.GetOptional("BBox") as Pdfe.Core.Primitives.PdfArray;
        if (bboxArray == null || bboxArray.Count < 4)
            return false;

        var bbox = new SKRect(
            (float)Math.Min(ArrayNumberOrDefault(bboxArray, 0), ArrayNumberOrDefault(bboxArray, 2)),
            (float)Math.Min(ArrayNumberOrDefault(bboxArray, 1), ArrayNumberOrDefault(bboxArray, 3)),
            (float)Math.Max(ArrayNumberOrDefault(bboxArray, 0), ArrayNumberOrDefault(bboxArray, 2)),
            (float)Math.Max(ArrayNumberOrDefault(bboxArray, 1), ArrayNumberOrDefault(bboxArray, 3)));
        if (bbox.Width <= 0 || bbox.Height <= 0)
            return false;

        var xStep = (float)pattern.GetNumber("XStep", bbox.Width);
        var yStep = (float)pattern.GetNumber("YStep", bbox.Height);
        if (Math.Abs(xStep) < 0.001f || Math.Abs(yStep) < 0.001f)
            return false;

        var paintType = pattern.GetInt("PaintType", 1);
        var patternResources = pattern.GetOptional("Resources") is { } resObj
            ? _page.Document.Resolve(resObj) as Pdfe.Core.Primitives.PdfDictionary
            : null;
        var patternMatrix = GetMatrix(pattern.GetOptional("Matrix") as Pdfe.Core.Primitives.PdfArray);
        var inverseCtm = InvertAffine(_state.CurrentTransform);
        if (!inverseCtm.HasValue)
            return false;

        _canvas.Save();
        _resourcesStack.Push(patternResources);
        var savedState = _state.Clone();
        try
        {
            _tilingPatternDepth++;
            _canvas.ClipPath(clipPath, SKClipOperation.Intersect, _options.AntiAlias);

            var inv = inverseCtm.Value;
            _canvas.Concat(in inv);
            _canvas.Concat(in patternMatrix);
            _state.FillPatternName = null;
            if (paintType == 2)
            {
                _state.StrokeColor = savedState.FillColor;
                _state.FillColor = savedState.FillColor;
                _state.StrokeAlpha = savedState.FillAlpha;
                _state.FillAlpha = savedState.FillAlpha;
            }

            var clip = _canvas.LocalClipBounds;
            if (clip.Width <= 0 || clip.Height <= 0)
                return false;

            var xStepAbs = Math.Abs(xStep);
            var yStepAbs = Math.Abs(yStep);
            if (NeedsComposedTilingCell(bbox, xStepAbs, yStepAbs))
                return RenderComposedTilingPatternCells(content, clip, bbox, xStepAbs, yStepAbs);

            var tileMinX = (float)(Math.Ceiling((clip.Left - bbox.Right) / xStepAbs) * xStepAbs);
            var tileMaxX = (float)(Math.Floor((clip.Right - bbox.Left) / xStepAbs) * xStepAbs);
            var tileMinY = (float)(Math.Ceiling((clip.Top - bbox.Bottom) / yStepAbs) * yStepAbs);
            var tileMaxY = (float)(Math.Floor((clip.Bottom - bbox.Top) / yStepAbs) * yStepAbs);
            if (tileMinX > tileMaxX || tileMinY > tileMaxY)
                return true;

            const int maxTiles = 4096;
            var tileCount = 0;
            for (var ty = tileMinY; ty <= tileMaxY; ty += yStepAbs)
            {
                for (var tx = tileMinX; tx <= tileMaxX; tx += xStepAbs)
                {
                    if (++tileCount > maxTiles)
                        return false;

                    RenderTilingPatternContentInstance(content, tx, ty, bbox);
                }
            }

            return true;
        }
        finally
        {
            _tilingPatternDepth--;
            _state = savedState;
            _resourcesStack.Pop();
            _canvas.Restore();
        }
    }

    private bool RenderComposedTilingPatternCells(
        byte[] content,
        SKRect clip,
        SKRect bbox,
        float xStep,
        float yStep)
    {
        const float epsilon = 0.0001f;
        var cellMinX = (float)(Math.Floor(clip.Left / xStep) * xStep);
        var cellMaxX = (float)(Math.Floor((clip.Right - epsilon) / xStep) * xStep);
        var cellMinY = (float)(Math.Floor(clip.Top / yStep) * yStep);
        var cellMaxY = (float)(Math.Floor((clip.Bottom - epsilon) / yStep) * yStep);
        if (cellMinX > cellMaxX || cellMinY > cellMaxY)
            return true;

        var cellBounds = new SKRect(0, 0, xStep, yStep);

        var contributionMinX = (float)(Math.Ceiling((0 - bbox.Right + epsilon) / xStep) * xStep);
        var contributionMaxX = (float)(Math.Floor((xStep - bbox.Left - epsilon) / xStep) * xStep);
        var contributionMinY = (float)(Math.Ceiling((0 - bbox.Bottom + epsilon) / yStep) * yStep);
        var contributionMaxY = (float)(Math.Floor((yStep - bbox.Top - epsilon) / yStep) * yStep);
        if (contributionMinX > contributionMaxX || contributionMinY > contributionMaxY)
            return true;

        const int maxCellContentInstances = 4096;
        var origins = new List<SKPoint>();
        for (var relY = contributionMinY; relY <= contributionMaxY + epsilon; relY += yStep)
        {
            for (var relX = contributionMinX; relX <= contributionMaxX + epsilon; relX += xStep)
            {
                if (origins.Count >= maxCellContentInstances)
                    return false;

                var tileBounds = new SKRect(
                    relX + bbox.Left,
                    relY + bbox.Top,
                    relX + bbox.Right,
                    relY + bbox.Bottom);
                if (tileBounds.IntersectsWith(cellBounds))
                    origins.Add(new SKPoint(relX, relY));
            }
        }

        if (origins.Count == 0)
            return true;

        var cellCountX = 1 + (long)Math.Floor((cellMaxX - cellMinX) / xStep);
        var cellCountY = 1 + (long)Math.Floor((cellMaxY - cellMinY) / yStep);
        const long maxDirectContentInstances = 8192;
        if (cellCountX > 0 &&
            cellCountY > 0 &&
            cellCountX * cellCountY * origins.Count <= maxDirectContentInstances)
        {
            return RenderDirectComposedTilingPatternCells(
                content,
                clip,
                bbox,
                xStep,
                yStep,
                origins,
                cellMinX,
                cellMaxX,
                cellMinY,
                cellMaxY);
        }

        return RenderRepeatedComposedTilingPatternCell(content, clip, bbox, cellBounds, origins);
    }

    private bool RenderDirectComposedTilingPatternCells(
        byte[] content,
        SKRect clip,
        SKRect bbox,
        float xStep,
        float yStep,
        IReadOnlyList<SKPoint> origins,
        float cellMinX,
        float cellMaxX,
        float cellMinY,
        float cellMaxY)
    {
        const float epsilon = 0.0001f;
        for (var cellY = cellMinY; cellY <= cellMaxY + epsilon; cellY += yStep)
        {
            for (var cellX = cellMinX; cellX <= cellMaxX + epsilon; cellX += xStep)
            {
                var cell = new SKRect(cellX, cellY, cellX + xStep, cellY + yStep);
                if (!cell.IntersectsWith(clip))
                    continue;

                _canvas.Save();
                try
                {
                    // Pattern cell and BBox clips are lattice boundaries, not painted edges.
                    // Antialiased clipping here creates repeat seams on thin pattern strokes.
                    _canvas.ClipRect(cell, SKClipOperation.Intersect, antialias: false);

                    foreach (var origin in origins)
                        RenderTilingPatternContentInstance(content, cellX + origin.X, cellY + origin.Y, bbox);
                }
                finally
                {
                    _canvas.Restore();
                }
            }
        }

        return true;
    }

    private bool RenderRepeatedComposedTilingPatternCell(
        byte[] content,
        SKRect clip,
        SKRect bbox,
        SKRect cellBounds,
        IReadOnlyList<SKPoint> origins)
    {
        using var recorder = new SKPictureRecorder();
        var cellCanvas = recorder.BeginRecording(cellBounds);
        RenderContext? child = null;
        SKPicture? cellPicture = null;
        var recordingEnded = false;
        try
        {
            cellCanvas.Save();
            cellCanvas.ClipRect(cellBounds, SKClipOperation.Intersect, antialias: false);

            child = new RenderContext(cellCanvas, _page, _options, _cancellationToken);
            CopyRenderScopeTo(child);
            child._state = _state.Clone();
            child._state.FillPatternName = null;
            child._tilingPatternDepth = _tilingPatternDepth;

            foreach (var origin in origins)
                child.RenderTilingPatternContentInstance(content, origin.X, origin.Y, bbox);

            cellCanvas.Restore();
            cellPicture = recorder.EndRecording();
            recordingEnded = true;
            if (cellPicture == null)
                return false;

            using var shader = SKShader.CreatePicture(
                cellPicture,
                SKShaderTileMode.Repeat,
                SKShaderTileMode.Repeat,
                SKFilterMode.Nearest,
                cellBounds);
            using var paint = new SKPaint
            {
                Shader = shader,
                IsAntialias = _options.AntiAlias
            };

            _canvas.DrawRect(clip, paint);
            return true;
        }
        finally
        {
            if (!recordingEnded)
                recorder.EndRecording()?.Dispose();
            child?._resourcesStack.Clear();
            child?._optionalContentVisibilityStack.Clear();
            child?.DisposeOwnedResources();
            cellPicture?.Dispose();
        }
    }

    private void CopyRenderScopeTo(RenderContext child)
    {
        foreach (var resources in _resourcesStack.Reverse())
            child._resourcesStack.Push(resources);
        foreach (var visible in _optionalContentVisibilityStack.Reverse())
            child._optionalContentVisibilityStack.Push(visible);
        child._hiddenOptionalContentDepth = _hiddenOptionalContentDepth;
    }

    private void RenderTilingPatternContentInstance(byte[] content, float tx, float ty, SKRect bbox)
    {
        var savedCanvasCount = _canvas.SaveCount;
        var savedStateStack = SnapshotGraphicsStateStack();
        var savedPath = _currentPath;
        var savedPendingClip = _pendingClipEvenOdd;
        var savedPendingTextClipPath = _pendingTextClipPath;
        var savedState = _state.Clone();
        var savedTextState = _textState.Clone();
        var savedFontState = SnapshotCurrentFontState();
        var savedInTextBlock = _inTextBlock;
        _currentPath = null;
        _pendingClipEvenOdd = null;
        _pendingTextClipPath = null;
        _canvas.Save();
        try
        {
            _canvas.Translate(tx, ty);
            // Keep BBox clipping hard for the same reason as the repeat-cell clip above.
            _canvas.ClipRect(bbox, SKClipOperation.Intersect, antialias: false);
            ExecuteContentBytes(content);
        }
        finally
        {
            _currentPath?.Dispose();
            _pendingTextClipPath?.Dispose();
            RestoreGraphicsStateStack(savedStateStack);
            _currentPath = savedPath;
            _pendingClipEvenOdd = savedPendingClip;
            _pendingTextClipPath = savedPendingTextClipPath;
            _state = savedState;
            _textState = savedTextState;
            RestoreCurrentFontState(savedFontState);
            _inTextBlock = savedInTextBlock;
            _canvas.RestoreToCount(savedCanvasCount);
        }
    }

    private static bool NeedsComposedTilingCell(SKRect bbox, float xStep, float yStep)
    {
        const float epsilon = 0.0001f;
        return Math.Abs(bbox.Left) > epsilon
            || Math.Abs(bbox.Top) > epsilon
            || Math.Abs(bbox.Right - xStep) > epsilon
            || Math.Abs(bbox.Bottom - yStep) > epsilon;
    }

    private bool RenderShadingPattern(
        SKPath clipPath,
        Pdfe.Core.Primitives.PdfDictionary pattern,
        Pdfe.Core.Primitives.PdfDictionary shading)
    {
        _canvas.Save();
        try
        {
            _canvas.ClipPath(clipPath, SKClipOperation.Intersect, _options.AntiAlias);

            var inverseCtm = InvertAffine(_state.CurrentTransform);
            if (inverseCtm.HasValue)
            {
                var inv = inverseCtm.Value;
                _canvas.Concat(in inv);
            }

            var patternMatrix = GetMatrix(pattern.GetOptional("Matrix") as Pdfe.Core.Primitives.PdfArray);
            _canvas.Concat(in patternMatrix);

            switch (shading.GetInt("ShadingType", 0))
            {
                case 1:
                    RenderFunctionShading(shading);
                    return true;
                case 2:
                    RenderAxialShading(shading);
                    return true;
                case 3:
                    RenderRadialShading(shading);
                    return true;
                default:
                    return false;
            }
        }
        finally
        {
            _canvas.Restore();
        }
    }

    private bool RenderType6MeshPattern(
        SKPath clipPath,
        Pdfe.Core.Primitives.PdfDictionary pattern,
        Pdfe.Core.Primitives.PdfDictionary shading)
    {
        if (shading is not Pdfe.Core.Primitives.PdfStream stream)
            return false;

        var patches = DecodeType6MeshPatches(stream);
        if (patches.Count == 0)
            return false;

        var minX = patches.Min(p => p.MinX);
        var minY = patches.Min(p => p.MinY);
        var maxX = patches.Max(p => p.MaxX);
        var maxY = patches.Max(p => p.MaxY);
        if (maxX <= minX || maxY <= minY)
            return false;

        var width = Math.Clamp((int)Math.Ceiling(maxX - minX) * 2, 16, 768);
        var height = Math.Clamp((int)Math.Ceiling(maxY - minY) * 2, 16, 768);
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);

        foreach (var patch in patches)
            RasterizeMeshPatch(bitmap, patch, minX, minY, maxX, maxY);

        using var image = SKImage.FromBitmap(bitmap);
        using var shader = SKShader.CreateImage(
            image,
            SKShaderTileMode.Clamp,
            SKShaderTileMode.Clamp,
            SKMatrix.CreateScale(
                (float)((maxX - minX) / width),
                (float)((maxY - minY) / height)));

        using var paint = new SKPaint
        {
            Shader = shader,
            BlendMode = _state.BlendMode,
            Color = SKColors.White.WithAlpha((byte)Math.Clamp(_state.FillAlpha * 255, 0, 255)),
            IsAntialias = _options.AntiAlias
        };

        _canvas.Save();
        try
        {
            _canvas.ClipPath(clipPath, SKClipOperation.Intersect, _options.AntiAlias);

            var inverseCtm = InvertAffine(_state.CurrentTransform);
            if (inverseCtm.HasValue)
            {
                var inv = inverseCtm.Value;
                _canvas.Concat(in inv);
            }

            var patternMatrix = GetMatrix(pattern.GetOptional("Matrix") as Pdfe.Core.Primitives.PdfArray);
            _canvas.Concat(in patternMatrix);
            _canvas.Translate((float)minX, (float)minY);
            _canvas.DrawRect(
                new SKRect(0, 0, (float)(maxX - minX), (float)(maxY - minY)),
                paint);
        }
        finally
        {
            _canvas.Restore();
        }

        return true;
    }

    private bool RenderType4MeshPattern(
        SKPath clipPath,
        Pdfe.Core.Primitives.PdfDictionary pattern,
        Pdfe.Core.Primitives.PdfDictionary shading)
    {
        if (shading is not Pdfe.Core.Primitives.PdfStream stream)
            return false;

        var triangles = DecodeType4MeshTriangles(stream);
        if (triangles.Count == 0)
            return false;

        var minX = triangles.Min(t => t.MinX);
        var minY = triangles.Min(t => t.MinY);
        var maxX = triangles.Max(t => t.MaxX);
        var maxY = triangles.Max(t => t.MaxY);
        if (maxX <= minX || maxY <= minY)
            return false;

        var width = Math.Clamp((int)Math.Ceiling(maxX - minX) * 2, 16, 1024);
        var height = Math.Clamp((int)Math.Ceiling(maxY - minY) * 2, 16, 1024);
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);

        foreach (var triangle in triangles)
            RasterizeMeshTriangle(bitmap, triangle, minX, minY, maxX, maxY);

        using var image = SKImage.FromBitmap(bitmap);
        using var shader = SKShader.CreateImage(
            image,
            SKShaderTileMode.Clamp,
            SKShaderTileMode.Clamp,
            SKMatrix.CreateScale(
                (float)((maxX - minX) / width),
                (float)((maxY - minY) / height)));

        using var paint = new SKPaint
        {
            Shader = shader,
            BlendMode = _state.BlendMode,
            Color = SKColors.White.WithAlpha((byte)Math.Clamp(_state.FillAlpha * 255, 0, 255)),
            IsAntialias = _options.AntiAlias
        };

        _canvas.Save();
        try
        {
            _canvas.ClipPath(clipPath, SKClipOperation.Intersect, _options.AntiAlias);

            var inverseCtm = InvertAffine(_state.CurrentTransform);
            if (inverseCtm.HasValue)
            {
                var inv = inverseCtm.Value;
                _canvas.Concat(in inv);
            }

            var patternMatrix = GetMatrix(pattern.GetOptional("Matrix") as Pdfe.Core.Primitives.PdfArray);
            _canvas.Concat(in patternMatrix);
            _canvas.Translate((float)minX, (float)minY);
            _canvas.DrawRect(
                new SKRect(0, 0, (float)(maxX - minX), (float)(maxY - minY)),
                paint);
        }
        finally
        {
            _canvas.Restore();
        }

        return true;
    }

    private List<MeshTriangle> DecodeType4MeshTriangles(Pdfe.Core.Primitives.PdfStream stream)
    {
        var decode = stream.GetOptional("Decode") as Pdfe.Core.Primitives.PdfArray;
        var xMin = decode?.Count >= 2 ? decode.GetNumber(0) : 0;
        var xMax = decode?.Count >= 2 ? decode.GetNumber(1) : 1;
        var yMin = decode?.Count >= 4 ? decode.GetNumber(2) : 0;
        var yMax = decode?.Count >= 4 ? decode.GetNumber(3) : 1;
        var bitsPerCoordinate = stream.GetInt("BitsPerCoordinate", 16);
        var bitsPerComponent = stream.GetInt("BitsPerComponent", 8);
        var bitsPerFlag = stream.GetInt("BitsPerFlag", 2);
        var functionObj = stream.GetOptional("Function");
        var function = functionObj != null ? _page.Document.Resolve(functionObj) : null;
        var colorSpace = stream.GetNameOrNull("ColorSpace") ?? "DeviceRGB";
        var componentCount = GetMeshComponentCount(stream, colorSpace, function);

        var reader = new MeshBitReader(stream.DecodedData);
        var triangles = new List<MeshTriangle>();
        var pending = new List<MeshVertex>(3);
        MeshTriangle? previous = null;

        while (reader.RemainingBits >= bitsPerFlag + (2 * bitsPerCoordinate) + (componentCount * bitsPerComponent))
        {
            MeshVertex vertex;
            try
            {
                var flag = (int)reader.Read(bitsPerFlag);
                var x = Decode(reader.Read(bitsPerCoordinate), bitsPerCoordinate, xMin, xMax);
                var y = Decode(reader.Read(bitsPerCoordinate), bitsPerCoordinate, yMin, yMax);
                var components = new double[componentCount];
                for (int i = 0; i < componentCount; i++)
                {
                    var cMin = decode?.Count >= 6 + (2 * i) ? decode.GetNumber(4 + (2 * i)) : 0;
                    var cMax = decode?.Count >= 6 + (2 * i) ? decode.GetNumber(5 + (2 * i)) : 1;
                    components[i] = Decode(reader.Read(bitsPerComponent), bitsPerComponent, cMin, cMax);
                }

                var color = ComponentsToSkColor(
                    function != null
                        ? PdfFunctionEvaluator.Evaluate(function, components[0], _page.Document) ?? new[] { components[0] }
                        : components,
                    colorSpace);
                vertex = new MeshVertex(flag, new SKPoint((float)x, (float)y), color);
            }
            catch
            {
                break;
            }

            if (vertex.Flag == 0 || previous == null)
            {
                pending.Add(vertex);
                if (pending.Count < 3)
                    continue;

                previous = new MeshTriangle(pending[0], pending[1], pending[2]);
                triangles.Add(previous);
                pending.Clear();
                continue;
            }

            pending.Clear();
            previous = vertex.Flag switch
            {
                1 => new MeshTriangle(previous.B, previous.C, vertex),
                2 => new MeshTriangle(previous.A, previous.C, vertex),
                _ => null
            };

            if (previous != null)
                triangles.Add(previous);
        }

        return triangles;
    }

    private List<MeshPatch> DecodeType6MeshPatches(Pdfe.Core.Primitives.PdfStream stream)
    {
        var decode = stream.GetOptional("Decode") as Pdfe.Core.Primitives.PdfArray;
        var xMin = decode?.Count >= 2 ? decode.GetNumber(0) : 0;
        var xMax = decode?.Count >= 2 ? decode.GetNumber(1) : 1;
        var yMin = decode?.Count >= 4 ? decode.GetNumber(2) : 0;
        var yMax = decode?.Count >= 4 ? decode.GetNumber(3) : 1;
        var cMin = decode?.Count >= 6 ? decode.GetNumber(4) : 0;
        var cMax = decode?.Count >= 6 ? decode.GetNumber(5) : 1;
        var bitsPerCoordinate = stream.GetInt("BitsPerCoordinate", 16);
        var bitsPerComponent = stream.GetInt("BitsPerComponent", 8);
        var bitsPerFlag = stream.GetInt("BitsPerFlag", 2);
        var functionObj = stream.GetOptional("Function");
        var function = functionObj != null ? _page.Document.Resolve(functionObj) : null;
        var colorSpace = stream.GetNameOrNull("ColorSpace") ?? "DeviceRGB";

        var reader = new MeshBitReader(stream.DecodedData);
        var patches = new List<MeshPatch>();
        MeshPatch? previous = null;

        while (reader.RemainingBits >= bitsPerFlag + (8 * bitsPerCoordinate))
        {
            int flag;
            try
            {
                flag = (int)reader.Read(bitsPerFlag);
            }
            catch
            {
                break;
            }

            var coordinateCount = flag == 0 ? 12 : 8;
            var componentCount = flag == 0 ? 4 : 2;
            var points = new List<SKPoint>(coordinateCount);
            var components = new List<double>(componentCount);

            try
            {
                for (int i = 0; i < coordinateCount; i++)
                {
                    var x = Decode(reader.Read(bitsPerCoordinate), bitsPerCoordinate, xMin, xMax);
                    var y = Decode(reader.Read(bitsPerCoordinate), bitsPerCoordinate, yMin, yMax);
                    points.Add(new SKPoint((float)x, (float)y));
                }

                for (int i = 0; i < componentCount; i++)
                    components.Add(Decode(reader.Read(bitsPerComponent), bitsPerComponent, cMin, cMax));
            }
            catch
            {
                break;
            }

            var colors = ResolveMeshColors(components, previous, flag, function, colorSpace, _page.Document);
            var patch = MeshPatch.From(points, colors);
            patches.Add(patch);
            previous = patch;
        }

        return patches;
    }

    private static double Decode(uint encoded, int bits, double min, double max)
    {
        var denominator = Math.Pow(2, bits) - 1;
        return min + encoded * ((max - min) / denominator);
    }

    private static int GetMeshComponentCount(
        Pdfe.Core.Primitives.PdfStream stream,
        string colorSpace,
        Pdfe.Core.Primitives.PdfObject? function)
    {
        if (function != null)
            return 1;

        if (stream.GetOptional("Decode") is Pdfe.Core.Primitives.PdfArray decode && decode.Count > 4)
            return Math.Max(1, (decode.Count - 4) / 2);

        return colorSpace switch
        {
            "DeviceRGB" or "RGB" => 3,
            "DeviceCMYK" or "CMYK" => 4,
            _ => 1
        };
    }

    private static SKColor[] ResolveMeshColors(
        List<double> components,
        MeshPatch? previous,
        int flag,
        Pdfe.Core.Primitives.PdfObject? function,
        string colorSpace,
        PdfDocument document)
    {
        var newColors = components
            .Select(c => ComponentsToSkColor(PdfFunctionEvaluator.Evaluate(function, c, document) ?? new[] { c }, colorSpace))
            .ToArray();

        if (newColors.Length >= 4)
            return newColors.Take(4).ToArray();

        if (previous == null)
            return new[] { newColors[0], newColors[0], newColors[^1], newColors[^1] };

        return flag switch
        {
            1 => new[] { previous.Colors[1], newColors[0], newColors[^1], previous.Colors[2] },
            2 => new[] { previous.Colors[2], previous.Colors[3], newColors[0], newColors[^1] },
            3 => new[] { previous.Colors[3], previous.Colors[0], newColors[0], newColors[^1] },
            _ => new[] { newColors[0], newColors[0], newColors[^1], newColors[^1] }
        };
    }

    private static void RasterizeMeshPatch(SKBitmap bitmap, MeshPatch patch, double minX, double minY, double maxX, double maxY)
    {
        var startX = Math.Clamp((int)Math.Floor((patch.MinX - minX) / (maxX - minX) * bitmap.Width), 0, bitmap.Width - 1);
        var endX = Math.Clamp((int)Math.Ceiling((patch.MaxX - minX) / (maxX - minX) * bitmap.Width), 0, bitmap.Width - 1);
        var startY = Math.Clamp((int)Math.Floor((patch.MinY - minY) / (maxY - minY) * bitmap.Height), 0, bitmap.Height - 1);
        var endY = Math.Clamp((int)Math.Ceiling((patch.MaxY - minY) / (maxY - minY) * bitmap.Height), 0, bitmap.Height - 1);

        for (var y = startY; y <= endY; y++)
        {
            var py = minY + ((y + 0.5) / bitmap.Height) * (maxY - minY);
            var v = patch.MaxY > patch.MinY ? (py - patch.MinY) / (patch.MaxY - patch.MinY) : 0;
            v = Math.Clamp(v, 0, 1);

            for (var x = startX; x <= endX; x++)
            {
                var px = minX + ((x + 0.5) / bitmap.Width) * (maxX - minX);
                var u = patch.MaxX > patch.MinX ? (px - patch.MinX) / (patch.MaxX - patch.MinX) : 0;
                u = Math.Clamp(u, 0, 1);
                bitmap.SetPixel(x, bitmap.Height - 1 - y, Bilinear(patch.Colors, u, v));
            }
        }
    }

    private static void RasterizeMeshTriangle(
        SKBitmap bitmap,
        MeshTriangle triangle,
        double minX,
        double minY,
        double maxX,
        double maxY)
    {
        var startX = Math.Clamp((int)Math.Floor((triangle.MinX - minX) / (maxX - minX) * bitmap.Width), 0, bitmap.Width - 1);
        var endX = Math.Clamp((int)Math.Ceiling((triangle.MaxX - minX) / (maxX - minX) * bitmap.Width), 0, bitmap.Width - 1);
        var startY = Math.Clamp((int)Math.Floor((triangle.MinY - minY) / (maxY - minY) * bitmap.Height), 0, bitmap.Height - 1);
        var endY = Math.Clamp((int)Math.Ceiling((triangle.MaxY - minY) / (maxY - minY) * bitmap.Height), 0, bitmap.Height - 1);

        var a = triangle.A.Point;
        var b = triangle.B.Point;
        var c = triangle.C.Point;
        var denominator =
            (b.Y - c.Y) * (a.X - c.X) +
            (c.X - b.X) * (a.Y - c.Y);
        if (Math.Abs(denominator) < 1e-9)
            return;

        for (var y = startY; y <= endY; y++)
        {
            var py = minY + ((y + 0.5) / bitmap.Height) * (maxY - minY);
            for (var x = startX; x <= endX; x++)
            {
                var px = minX + ((x + 0.5) / bitmap.Width) * (maxX - minX);
                var wa = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / denominator;
                var wb = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / denominator;
                var wc = 1 - wa - wb;
                const double epsilon = -0.001;
                if (wa < epsilon || wb < epsilon || wc < epsilon)
                    continue;

                bitmap.SetPixel(x, y, Barycentric(
                    triangle.A.Color,
                    triangle.B.Color,
                    triangle.C.Color,
                    wa,
                    wb,
                    wc));
            }
        }
    }

    private static SKColor Barycentric(SKColor a, SKColor b, SKColor c, double wa, double wb, double wc)
    {
        return new SKColor(
            (byte)Math.Clamp((a.Red * wa) + (b.Red * wb) + (c.Red * wc), 0, 255),
            (byte)Math.Clamp((a.Green * wa) + (b.Green * wb) + (c.Green * wc), 0, 255),
            (byte)Math.Clamp((a.Blue * wa) + (b.Blue * wb) + (c.Blue * wc), 0, 255),
            255);
    }

    private static SKColor Bilinear(SKColor[] colors, double u, double v)
    {
        static double Lerp(double a, double b, double t) => a + (b - a) * t;
        var r0 = Lerp(colors[0].Red, colors[1].Red, u);
        var r1 = Lerp(colors[3].Red, colors[2].Red, u);
        var g0 = Lerp(colors[0].Green, colors[1].Green, u);
        var g1 = Lerp(colors[3].Green, colors[2].Green, u);
        var b0 = Lerp(colors[0].Blue, colors[1].Blue, u);
        var b1 = Lerp(colors[3].Blue, colors[2].Blue, u);
        return new SKColor(
            (byte)Math.Clamp(Lerp(r0, r1, v), 0, 255),
            (byte)Math.Clamp(Lerp(g0, g1, v), 0, 255),
            (byte)Math.Clamp(Lerp(b0, b1, v), 0, 255),
            255);
    }

    private static SKMatrix? InvertAffine(SKMatrix matrix)
    {
        var det = matrix.ScaleX * matrix.ScaleY - matrix.SkewX * matrix.SkewY;
        if (Math.Abs(det) < 1e-9)
            return null;

        var invA = matrix.ScaleY / det;
        var invB = -matrix.SkewY / det;
        var invC = -matrix.SkewX / det;
        var invD = matrix.ScaleX / det;
        var invE = -(invA * matrix.TransX + invC * matrix.TransY);
        var invF = -(invB * matrix.TransX + invD * matrix.TransY);
        return new SKMatrix(invA, invC, invE, invB, invD, invF, 0, 0, 1);
    }

    private void RenderShading(string nameOperand)
    {
        // Remove leading / if present
        var name = nameOperand.TrimStart('/');

        var shading = ResolveShadingFromActiveResources(name);
        if (shading == null)
            return;

        var shadingType = shading.GetInt("ShadingType", 0);

        // Handle different shading types
        switch (shadingType)
        {
            case 1: // Function-based shading
                RenderFunctionShading(shading);
                break;
            case 2: // Axial shading (linear gradient)
                RenderAxialShading(shading);
                break;
            case 3: // Radial shading (radial gradient)
                RenderRadialShading(shading);
                break;
            // Types 4-7 are more complex (mesh-based)
            // For now, just fill with background color as fallback
            default:
                // Shading fills the current clipping path
                break;
        }
    }

    private void RenderAxialShading(Pdfe.Core.Primitives.PdfDictionary shading)
    {
        // Get the coordinate array [x0, y0, x1, y1]
        var coords = shading.GetOptional("Coords") as Pdfe.Core.Primitives.PdfArray;
        if (coords == null || coords.Count < 4)
            return;

        var x0 = (float)coords.GetNumber(0);
        var y0 = (float)coords.GetNumber(1);
        var x1 = (float)coords.GetNumber(2);
        var y1 = (float)coords.GetNumber(3);

        var (startColor, endColor, stops, positions) = ResolveGradientColors(shading);

        // Create the gradient shader
        using var shader = stops != null && stops.Length > 2
            ? SKShader.CreateLinearGradient(
                new SKPoint(x0, y0),
                new SKPoint(x1, y1),
                stops,
                positions,
                SKShaderTileMode.Clamp)
            : SKShader.CreateLinearGradient(
                new SKPoint(x0, y0),
                new SKPoint(x1, y1),
                new[] { startColor, endColor },
                null,
                SKShaderTileMode.Clamp);

        DrawShaderOverCurrentClip(shader);
    }

    private void RenderRadialShading(Pdfe.Core.Primitives.PdfDictionary shading)
    {
        // Get the coordinate array [x0, y0, r0, x1, y1, r1]
        var coords = shading.GetOptional("Coords") as Pdfe.Core.Primitives.PdfArray;
        if (coords == null || coords.Count < 6)
            return;

        var x0 = (float)coords.GetNumber(0);
        var y0 = (float)coords.GetNumber(1);
        var r0 = (float)coords.GetNumber(2);
        var x1 = (float)coords.GetNumber(3);
        var y1 = (float)coords.GetNumber(4);
        var r1 = (float)coords.GetNumber(5);

        var (startColor, endColor, stops, positions) = ResolveGradientColors(shading);

        // Create the two-point conical gradient
        using var shader = stops != null && stops.Length > 2
            ? SKShader.CreateTwoPointConicalGradient(
                new SKPoint(x0, y0), r0,
                new SKPoint(x1, y1), r1,
                stops,
                positions,
                SKShaderTileMode.Clamp)
            : SKShader.CreateTwoPointConicalGradient(
                new SKPoint(x0, y0), r0,
                new SKPoint(x1, y1), r1,
                new[] { startColor, endColor },
                null,
                SKShaderTileMode.Clamp);

        var (extendStart, extendEnd) = GetShadingExtend(shading);
        _canvas.Save();
        try
        {
            ApplyRadialShadingDomainClip(x0, y0, r0, x1, y1, r1, extendStart, extendEnd);
            DrawShaderOverCurrentClip(shader);
        }
        finally
        {
            _canvas.Restore();
        }
    }

    private void ApplyRadialShadingDomainClip(
        float x0,
        float y0,
        float r0,
        float x1,
        float y1,
        float r1,
        bool extendStart,
        bool extendEnd)
    {
        if (!extendEnd)
        {
            using var endPath = new SKPath();
            endPath.AddCircle(x1, y1, Math.Max(0, r1));
            _canvas.ClipPath(endPath, SKClipOperation.Intersect, _options.AntiAlias);
        }

        if (!extendStart && r0 > 0)
        {
            using var startPath = new SKPath();
            startPath.AddCircle(x0, y0, r0);
            _canvas.ClipPath(startPath, SKClipOperation.Difference, _options.AntiAlias);
        }
    }

    private static (bool Start, bool End) GetShadingExtend(Pdfe.Core.Primitives.PdfDictionary shading)
    {
        if (shading.GetOptional("Extend") is not Pdfe.Core.Primitives.PdfArray extend)
            return (false, false);

        return (
            extend.Count > 0 && extend[0] is Pdfe.Core.Primitives.PdfBoolean start && start.Value,
            extend.Count > 1 && extend[1] is Pdfe.Core.Primitives.PdfBoolean end && end.Value);
    }

    private void DrawShaderOverCurrentClip(SKShader shader)
    {
        var clipBounds = _canvas.LocalClipBounds;
        if (clipBounds.Width <= 0 || clipBounds.Height <= 0)
            return;

        var alpha = (byte)Math.Clamp(_state.FillAlpha * 255, 0, 255);
        using var paint = new SKPaint
        {
            Shader = shader,
            BlendMode = alpha == 255 ? _state.BlendMode : SKBlendMode.SrcOver,
            IsAntialias = _options.AntiAlias
        };

        if (alpha == 255)
        {
            _canvas.DrawRect(clipBounds, paint);
            return;
        }

        using var layerPaint = new SKPaint
        {
            Color = SKColors.White.WithAlpha(alpha),
            BlendMode = _state.BlendMode,
            IsAntialias = _options.AntiAlias
        };
        _canvas.SaveLayer(clipBounds, layerPaint);
        try
        {
            _canvas.DrawRect(clipBounds, paint);
        }
        finally
        {
            _canvas.Restore();
        }
    }

    private void RenderFunctionShading(Pdfe.Core.Primitives.PdfDictionary shading)
    {
        var funcRef = shading.GetOptional("Function");
        var funcObj = funcRef != null ? _page.Document.Resolve(funcRef) : null;
        var colorSpace = ResolveShadingColorSpace(shading);
        var domain = GetNumberArray(shading.GetOptional("Domain") as Pdfe.Core.Primitives.PdfArray)
                     ?? new[] { 0.0, 1.0, 0.0, 1.0 };
        if (domain.Length < 4)
            return;

        var matrix = GetMatrix(shading.GetOptional("Matrix") as Pdfe.Core.Primitives.PdfArray);
        var inverseMatrix = InvertAffine(matrix);
        if (!inverseMatrix.HasValue)
            return;

        var bounds = _canvas.LocalClipBounds;
        var bbox = GetNumberArray(shading.GetOptional("BBox") as Pdfe.Core.Primitives.PdfArray);
        if (bbox is { Length: >= 4 })
        {
            var bboxRect = new SKRect(
                (float)Math.Min(bbox[0], bbox[2]),
                (float)Math.Min(bbox[1], bbox[3]),
                (float)Math.Max(bbox[0], bbox[2]),
                (float)Math.Max(bbox[1], bbox[3]));
            bounds.Intersect(bboxRect);
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var width = Math.Clamp((int)Math.Ceiling(bounds.Width), 1, 1024);
        var height = Math.Clamp((int)Math.Ceiling(bounds.Height), 1, 1024);
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);

        var inv = inverseMatrix.Value;
        var xMin = Math.Min(domain[0], domain[1]);
        var xMax = Math.Max(domain[0], domain[1]);
        var yMin = Math.Min(domain[2], domain[3]);
        var yMax = Math.Max(domain[2], domain[3]);
        var alpha = (byte)Math.Clamp(_state.FillAlpha * 255, 0, 255);

        for (var y = 0; y < height; y++)
        {
            var targetY = bounds.Top + ((y + 0.5f) / height) * bounds.Height;
            for (var x = 0; x < width; x++)
            {
                var targetX = bounds.Left + ((x + 0.5f) / width) * bounds.Width;
                var sourceX = inv.ScaleX * targetX + inv.SkewX * targetY + inv.TransX;
                var sourceY = inv.SkewY * targetX + inv.ScaleY * targetY + inv.TransY;
                if (sourceX < xMin || sourceX > xMax || sourceY < yMin || sourceY > yMax)
                    continue;

                var functionY = yMin + yMax - sourceY;
                var comps = PdfFunctionEvaluator.Evaluate(funcObj, new[] { (double)sourceX, (double)functionY }, _page.Document);
                if (comps == null)
                    continue;

                var color = ComponentsToSkColor(comps, colorSpace);
                bitmap.SetPixel(x, height - 1 - y, color.WithAlpha(alpha));
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var paint = new SKPaint
        {
            BlendMode = _state.BlendMode,
            IsAntialias = _options.AntiAlias
        };
        _canvas.DrawImage(image, bounds, paint);
    }

    private double[]? GetNumberArray(Pdfe.Core.Primitives.PdfArray? arr)
    {
        if (arr == null)
            return null;

        var values = new double[arr.Count];
        for (var i = 0; i < arr.Count; i++)
            values[i] = ArrayNumberOrDefault(arr, i);
        return values;
    }

    private (SKColor start, SKColor end, SKColor[]? stops, float[]? positions) ResolveGradientColors(
        Pdfe.Core.Primitives.PdfDictionary shading)
    {
        var colorSpace = ResolveShadingColorSpace(shading);
        var funcRef = shading.GetOptional("Function");
        var funcObj = funcRef != null ? _page.Document.Resolve(funcRef) : null;
        var domain = GetNumberArray(shading.GetOptional("Domain") as Pdfe.Core.Primitives.PdfArray)
                     ?? GetNumberArray((funcObj as Pdfe.Core.Primitives.PdfDictionary)?.GetOptional("Domain") as Pdfe.Core.Primitives.PdfArray)
                     ?? new[] { 0.0, 1.0 };
        var domainMin = domain.Length >= 2 ? domain[0] : 0.0;
        var domainMax = domain.Length >= 2 ? domain[1] : 1.0;
        if (Math.Abs(domainMax - domainMin) < 1e-9)
            domainMax = domainMin + 1.0;

        var c0 = PdfFunctionEvaluator.Evaluate(funcObj, domainMin, _page.Document) ?? new[] { 0.0 };
        var c1 = PdfFunctionEvaluator.Evaluate(funcObj, domainMax, _page.Document) ?? new[] { 1.0 };

        var startColor = ComponentsToSkColor(c0, colorSpace);
        var endColor = ComponentsToSkColor(c1, colorSpace);

        var (stops, positions) = ShouldSampleGradientFunction(funcObj)
            ? SampleGradientFunction(funcObj, colorSpace, domainMin, domainMax)
            : (null, null);

        return (startColor, endColor, stops, positions);
    }

    private (SKColor[] stops, float[] positions) SampleGradientFunction(
        PdfObject? funcObj,
        PdfColorSpace colorSpace,
        double domainMin,
        double domainMax)
    {
        var stops = new SKColor[ComplexGradientSampleCount + 1];
        var positions = new float[ComplexGradientSampleCount + 1];

        for (var i = 0; i <= ComplexGradientSampleCount; i++)
        {
            var position = (double)i / ComplexGradientSampleCount;
            var t = domainMin + ((domainMax - domainMin) * position);
            var comps = PdfFunctionEvaluator.Evaluate(funcObj, t, _page.Document) ?? new[] { 0.0 };
            stops[i] = ComponentsToSkColor(comps, colorSpace);
            positions[i] = (float)position;
        }

        return (stops, positions);
    }

    private PdfColorSpace ResolveShadingColorSpace(Pdfe.Core.Primitives.PdfDictionary shading)
    {
        var colorSpaceObj = shading.GetOptional("ColorSpace");
        if (colorSpaceObj == null)
            return PdfColorSpace.DeviceGray;

        try
        {
            if (colorSpaceObj is PdfName name)
                return ResolveColorSpace(name.Value) ?? PdfColorSpace.Parse(colorSpaceObj, _page.Document);

            return PdfColorSpace.Parse(colorSpaceObj, _page.Document);
        }
        catch
        {
            return PdfColorSpace.DeviceGray;
        }
    }

    private bool ShouldSampleGradientFunction(PdfObject? funcObj)
    {
        if (funcObj == null)
            return false;

        var resolved = _page.Document.Resolve(funcObj);
        if (resolved is PdfArray array)
            return array.Any(ShouldSampleGradientFunction);

        if (resolved is not PdfDictionary function)
            return false;

        return function.GetInt("FunctionType", -1) switch
        {
            0 or 3 or 4 => true,
            2 => Math.Abs(function.GetNumber("N", 1.0) - 1.0) > 1e-9,
            _ => false
        };
    }

    private static SKColor ComponentsToSkColor(double[] comps, string colorSpace)
    {
        return colorSpace switch
        {
            "DeviceGray" or "G" =>
                comps.Length >= 1 ? ToGray(comps[0]) : SKColors.Black,
            "DeviceRGB" or "RGB" =>
                comps.Length >= 3 ? ToRGB(comps[0], comps[1], comps[2]) : SKColors.Black,
            "DeviceCMYK" or "CMYK" =>
                comps.Length >= 4 ? CmykToColor(comps[0], comps[1], comps[2], comps[3]) : SKColors.Black,
            _ => comps.Length >= 3 ? ToRGB(comps[0], comps[1], comps[2])
               : comps.Length >= 1 ? ToGray(comps[0]) : SKColors.Black
        };
    }

    private static SKColor ComponentsToSkColor(double[] comps, PdfColorSpace colorSpace)
    {
        if (comps.Length == 0)
            return SKColors.Black;

        var (r, g, b) = colorSpace.ToRgb(comps);
        return ToRGB(r, g, b);
    }

    private static SKColor ToGray(double g)
    {
        byte v = (byte)Math.Clamp(g * 255, 0, 255);
        return new SKColor(v, v, v);
    }

    private static SKColor ToRGB(double r, double g, double b) =>
        new SKColor(
            (byte)Math.Clamp(r * 255, 0, 255),
            (byte)Math.Clamp(g * 255, 0, 255),
            (byte)Math.Clamp(b * 255, 0, 255));

    #endregion

    #region Color Space Operators (SC, SCN, sc, scn)

    private void SetStrokingColor(IReadOnlyList<PdfObject> operands)
    {
        var color = ParseColorFromOperands(operands, _state.StrokeColorSpace);
        if (color.HasValue)
            _state.StrokeColor = color.Value;
    }

    private void SetNonStrokingColor(IReadOnlyList<PdfObject> operands)
    {
        var fillColorSpace = ResolveColorSpace(_state.FillColorSpace);
        if (fillColorSpace?.Type == PdfColorSpaceType.Pattern)
        {
            _state.FillPatternName = operands.OfType<PdfName>().FirstOrDefault()?.Value;
            var tintColor = ParsePatternTintColor(operands, _state.FillColorSpace);
            if (tintColor.HasValue)
                _state.FillColor = tintColor.Value;
            return;
        }

        var color = ParseColorFromOperands(operands, _state.FillColorSpace);
        if (color.HasValue)
        {
            _state.FillColor = color.Value;
            _state.FillPatternName = null;
        }
    }

    private SKColor? ParseColorFromOperands(IReadOnlyList<PdfObject> operands, string colorSpace)
    {
        var values = operands
            .Where(o => o is not PdfName)
            .Select(o => o.GetNumber())
            .ToArray();

        if (values.Length == 0)
            return null;

        var cs = ResolveColorSpace(colorSpace);
        if (cs != null && cs.Type != PdfColorSpaceType.Pattern)
        {
            var (r, g, b) = cs.ToRgb(values);
            return RgbToColor(r, g, b);
        }

        return colorSpace switch
        {
            "Pattern" when operands.Any(o => o is PdfName) =>
                null,

            _ => null
        };
    }

    private SKColor? ParsePatternTintColor(IReadOnlyList<PdfObject> operands, string colorSpaceName)
    {
        var colorSpaceObj = ResolveColorSpaceObject(colorSpaceName);
        var resolved = colorSpaceObj != null ? _page.Document.Resolve(colorSpaceObj) : null;
        if (resolved is not PdfArray arr ||
            arr.Count < 2 ||
            arr[0] is not PdfName typeName ||
            typeName.Value != "Pattern")
        {
            return null;
        }

        var values = operands
            .Where(o => o is not PdfName)
            .Select(o => o.GetNumber())
            .ToArray();
        if (values.Length == 0)
            return null;

        var baseColorSpace = ResolvePatternBaseColorSpace(arr[1]);
        if (baseColorSpace == null || baseColorSpace.Type == PdfColorSpaceType.Pattern)
            return null;

        var (r, g, b) = baseColorSpace.ToRgb(values);
        return RgbToColor(r, g, b);
    }

    private PdfColorSpace? ResolvePatternBaseColorSpace(PdfObject colorSpaceObj)
    {
        if (colorSpaceObj is PdfName name)
        {
            var named = ResolveColorSpace(name.Value);
            if (named != null)
                return named;

            var resourceObj = ResolveColorSpaceObject(name.Value);
            return resourceObj != null
                ? PdfColorSpace.Parse(resourceObj, _page.Document)
                : null;
        }

        return PdfColorSpace.Parse(colorSpaceObj, _page.Document);
    }

    private PdfColorSpace? ResolveColorSpace(string name)
    {
        var defaultCsObj = ResolveDefaultColorSpaceObject(name);
        if (defaultCsObj != null)
            return PdfColorSpace.Parse(defaultCsObj, _page.Document);

        var cs = PdfColorSpace.FromName(name);
        if (cs.Type != PdfColorSpaceType.Unknown)
            return cs;

        var csObj = ResolveColorSpaceObject(name);
        return csObj != null ? PdfColorSpace.Parse(csObj, _page.Document) : null;
    }

    private PdfObject? ResolveDefaultColorSpaceObject(string deviceColorSpaceName)
    {
        var defaultName = deviceColorSpaceName switch
        {
            "DeviceGray" or "G" => "DefaultGray",
            "DeviceRGB" or "RGB" => "DefaultRGB",
            "DeviceCMYK" or "CMYK" => "DefaultCMYK",
            _ => null
        };

        return defaultName != null ? ResolveColorSpaceObject(defaultName) : null;
    }

    private PdfObject? ResolveColorSpaceObject(string name)
    {
        foreach (var resources in _resourcesStack)
        {
            if (resources == null) continue;
            var colorSpacesObj = resources.GetOptional("ColorSpace");
            if (colorSpacesObj == null) continue;
            if (_page.Document.Resolve(colorSpacesObj) is not Pdfe.Core.Primitives.PdfDictionary colorSpaces)
                continue;

            var csObj = colorSpaces.GetOptional(name);
            if (csObj != null)
                return csObj;
        }

        return _page.GetColorSpaceObject(name);
    }

    /// <summary>
    /// Walk the resources stack top-down looking for a font definition.
    /// The innermost Form XObject's /Resources wins; we fall through to
    /// outer XObjects and finally the page when the name isn't defined
    /// locally — matches the "inherit if not found" rule from PDF 32000-2
    /// §7.8.3 for Form XObject resource resolution.
    /// </summary>
    private Pdfe.Core.Primitives.PdfDictionary? ResolveFontFromActiveResources(string fontName)
    {
        foreach (var resources in _resourcesStack)
        {
            if (resources == null) continue;
            var fontsObj = resources.GetOptional("Font");
            if (fontsObj == null) continue;
            if (_page.Document.Resolve(fontsObj) is not Pdfe.Core.Primitives.PdfDictionary fonts)
                continue;
            var fontObj = fonts.GetOptional(fontName);
            if (fontObj == null) continue;
            return _page.Document.Resolve(fontObj) as Pdfe.Core.Primitives.PdfDictionary;
        }
        return null;
    }

    /// <summary>
    /// Stack-aware XObject lookup, same fallback rule as
    /// <see cref="ResolveFontFromActiveResources"/>. Returns the resolved
    /// XObject (typically a stream); caller checks /Subtype.
    /// </summary>
    private Pdfe.Core.Primitives.PdfObject? ResolveXObjectFromActiveResources(string name)
    {
        foreach (var resources in _resourcesStack)
        {
            if (resources == null) continue;
            var xobjsObj = resources.GetOptional("XObject");
            if (xobjsObj == null) continue;
            if (_page.Document.Resolve(xobjsObj) is not Pdfe.Core.Primitives.PdfDictionary xobjs)
                continue;
            var x = xobjs.GetOptional(name);
            if (x == null) continue;
            return _page.Document.Resolve(x);
        }
        return null;
    }

    private Pdfe.Core.Primitives.PdfDictionary? ResolveExtGStateFromActiveResources(string name)
    {
        foreach (var resources in _resourcesStack)
        {
            if (resources == null) continue;
            var extGStatesObj = resources.GetOptional("ExtGState");
            if (extGStatesObj == null) continue;
            if (_page.Document.Resolve(extGStatesObj) is not Pdfe.Core.Primitives.PdfDictionary extGStates)
                continue;
            var extGState = extGStates.GetOptional(name);
            if (extGState == null) continue;
            return _page.Document.Resolve(extGState) as Pdfe.Core.Primitives.PdfDictionary;
        }

        return null;
    }

    private Pdfe.Core.Primitives.PdfObject? ResolvePropertyFromActiveResources(string name)
    {
        foreach (var resources in _resourcesStack)
        {
            if (resources == null) continue;
            var propertiesObj = resources.GetOptional("Properties");
            if (propertiesObj == null) continue;
            if (_page.Document.Resolve(propertiesObj) is not Pdfe.Core.Primitives.PdfDictionary properties)
                continue;
            var property = properties.GetOptional(name);
            if (property != null)
                return property;
        }

        return null;
    }

    private Pdfe.Core.Primitives.PdfDictionary? ResolvePatternFromActiveResources(string name)
    {
        foreach (var resources in _resourcesStack)
        {
            if (resources == null) continue;
            var patternsObj = resources.GetOptional("Pattern");
            if (patternsObj == null) continue;
            if (_page.Document.Resolve(patternsObj) is not Pdfe.Core.Primitives.PdfDictionary patterns)
                continue;
            var pattern = patterns.GetOptional(name);
            if (pattern == null) continue;
            return _page.Document.Resolve(pattern) as Pdfe.Core.Primitives.PdfDictionary;
        }

        return null;
    }

    private Pdfe.Core.Primitives.PdfDictionary? ResolveShadingFromActiveResources(string name)
    {
        foreach (var resources in _resourcesStack)
        {
            if (resources == null) continue;
            var shadingsObj = resources.GetOptional("Shading");
            if (shadingsObj == null) continue;
            if (_page.Document.Resolve(shadingsObj) is not Pdfe.Core.Primitives.PdfDictionary shadings)
                continue;
            var shadingObj = shadings.GetOptional(name);
            if (shadingObj == null) continue;
            return _page.Document.Resolve(shadingObj) as Pdfe.Core.Primitives.PdfDictionary;
        }

        return _page.GetShading(name);
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
                new Pdfe.Core.Parsing.StreamDecompressor().Decompress(stream);
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
