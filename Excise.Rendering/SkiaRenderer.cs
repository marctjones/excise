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
    // True while executing an UNCOLORED (d1) Type 3 glyph CharProc. Colour
    // operators in the CharProc are suppressed and the glyph paints in the
    // text object's fill colour (ISO 32000-1 §9.6.5). Reset per glyph in
    // RenderType3Glyph; set by the d1 operator.
    private bool _type3GlyphColorLocked;
    private readonly Dictionary<(int ObjectNumber, int Generation, int TargetWidth, int TargetHeight), SoftMaskAlpha?> _softMaskAlphaByReference = new();
    private readonly Dictionary<Excise.Core.Primitives.PdfStream, Dictionary<(int TargetWidth, int TargetHeight), SoftMaskAlpha?>> _softMaskAlphaByStream =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(int ObjectNumber, int Generation, ImageBitmapCacheKey Key), SKBitmap?> _imageBitmapByReference = new();
    private readonly Dictionary<Excise.Core.Primitives.PdfStream, Dictionary<ImageBitmapCacheKey, SKBitmap?>> _imageBitmapByStream =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<SKBitmap> _cachedImageBitmaps = new();
    private DeviceCmykBackdrop? _deviceCmykKnockoutInitialBackdrop;
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
        CancellationToken cancellationToken = default,
        SKBitmap? rootBitmap = null,
        bool startsInDeviceCmykTransparencyGroup = false)
    {
        _canvas = canvas;
        _rootBitmap = rootBitmap;
        _page = page;
        _options = options;
        _cancellationToken = cancellationToken;
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

            // Type 3 font operators (#301)
            case "d0":
                // Set glyph width - only affects metrics, not rendering
                break;
            case "d1":
                // d1 declares an UNCOLORED glyph description: the CharProc paints
                // only a shape/mask, colour operators in the rest of it are
                // ignored, and the glyph is filled with the text object's current
                // colour (ISO 32000-1 §9.6.5, Table 113). The wx/wy/bbox operands
                // only affect metrics, which come from /Widths.
                _type3GlyphColorLocked = true;
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

        if (extGState.ContainsKey("BM"))
        {
            var bm = extGState.GetNameOrNull("BM") ?? "Normal";
            _state.BlendMode = MapBlendMode(bm);
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
