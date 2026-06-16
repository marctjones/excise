using System.Globalization;
using System.Text;
using System.Threading;
using Pdfe.Core.ColorSpaces;
using Pdfe.Core.Content;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;
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
        float s = (float)scale, W = (float)page.Width, H = (float)page.Height;

        // The page /Rotate entry rotates the page clockwise when displayed.
        // The output bitmap is in *visual* dimensions (W/H swap for 90/270).
        int rot = ((page.Rotation % 360) + 360) % 360;
        bool quarter = rot is 90 or 270;
        var fullWidth = (int)Math.Round((quarter ? page.Height : page.Width) * scale);
        var fullHeight = (int)Math.Round((quarter ? page.Width : page.Height) * scale);
        if (fullWidth <= 0 || fullHeight <= 0)
            throw new InvalidPageGeometryException(
                $"Page resolves to an invalid bitmap size: {fullWidth} x {fullHeight} pixels.");

        // Map content space (PDF: bottom-left origin, Y up) to device pixels
        // (top-left origin, Y down) of the visual bitmap, applying /Rotate.
        // Derived as the inverse of PdfPage.ToContentStreamCoordinates (#356);
        // the 0° case is the classic scale+flip+translate. SKMatrix args are
        // (scaleX, skewX, transX, skewY, scaleY, transY, persp0, persp1, persp2)
        // where px = scaleX*cx + skewX*cy + transX, py = skewY*cx + scaleY*cy + transY.
        SKMatrix m = rot switch
        {
            90  => new SKMatrix(0, s, 0,   s, 0, 0,     0, 0, 1),
            180 => new SKMatrix(-s, 0, s * W,   0, s, 0,    0, 0, 1),
            270 => new SKMatrix(0, -s, s * H,   -s, 0, s * W,  0, 0, 1),
            _   => new SKMatrix(s, 0, 0,   0, -s, s * H,   0, 0, 1),
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

    // Typefaces loaded from the PDF's own embedded font streams
    // (/FontFile2 = TrueType, /FontFile3 = OpenType/CFF). Keyed by the
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

    // Type0 / CID font state. Type0 fonts use 2-byte-per-character codes and
    // index a descendant font's /W array for widths (different format from the
    // simple-font /Widths). When _currentFontIsType0 is true, content-stream
    // bytes must be parsed 2 at a time and rendered via glyph ID, not Unicode.
    private bool _currentFontIsType0;
    // True when /FontFile, /FontFile2, or /FontFile3 produced a usable
    // SKTypeface — i.e. Skia is rendering with the actual PDF font and
    // its MeasureText reports correct advances. False means we
    // substituted a system typeface; in that case PDF /Widths (if present)
    // are the source of truth for cursor advance, not Skia metrics.
    private bool _currentFontHasEmbeddedProgram;
    private Dictionary<int, float>? _currentCidWidths;
    private float _currentCidDefaultWidth = 1000f;

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

    private readonly CancellationToken _cancellationToken;

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

            var contentBytes = _page.GetContentStreamBytes();
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
            foreach (var typeface in _embeddedTypefaces.Values)
                typeface.Dispose();
            _embeddedTypefaces.Clear();
        }
    }

    private void ExecuteContentOperator(ContentOperator op)
    {
        var operands = op.Operands;
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

            // Marked content operators (#298)
            case "BMC":
                // Begin marked content - no visual effect
                break;
            case "BDC":
                // Begin marked content with property list - no visual effect
                break;
            case "EMC":
                // End marked content - no visual effect
                break;
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

    private static double Number(IReadOnlyList<PdfObject> operands, int index)
        => index >= 0 && index < operands.Count ? operands[index].GetNumber() : 0;

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
        _inTextBlock = true;
        _textState.Reset();
    }

    private void EndText()
    {
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
        var baseFont = fontDict?.GetNameOrNull("BaseFont") ?? "Helvetica";

        // /Encoding can be either a Name (e.g. /WinAnsiEncoding) or a Dictionary
        // with /BaseEncoding and /Differences. The dictionary form is how embedded
        // subset fonts remap small character codes to specific glyphs — without
        // handling it, text decodes as control characters and renders invisibly.
        // Must resolve the indirect reference; most real PDFs use `/Encoding N 0 R`.
        var encodingDict = fontDict != null ? ResolveDict(fontDict, "Encoding") : null;
        var encodingName = fontDict?.GetNameOrNull("Encoding")
                           ?? encodingDict?.GetNameOrNull("BaseEncoding")
                           ?? "WinAnsiEncoding";

        _currentFontEncoding = encodingName;
        _currentCodeToUnicode = null;
        _currentUnicodeToCode = null;
        if (encodingDict != null)
        {
            BuildEncodingMaps(encodingDict, encodingName);
        }

        // Parse the font's glyph width table FIRST. The CFF→OpenType wrapper
        // (called inside TryLoadEmbeddedTypeface below) reads these to build
        // hmtx — without populating them first, every embedded font would be
        // wrapped with stale widths from the previously-active font, producing
        // visibly wrong layout (mid-word gaps and overlaps).
        _currentFontWidths = null;
        _currentFontFirstChar = 0;
        _currentFontMissingWidth = 0f;
        if (fontDict != null)
        {
            var widthsArray = ResolveArray(fontDict, "Widths");
            if (widthsArray != null && widthsArray.Count > 0)
            {
                _currentFontFirstChar = fontDict.GetInt("FirstChar", 0);
                var widths = new float[widthsArray.Count];
                for (int i = 0; i < widthsArray.Count; i++)
                    widths[i] = (float)widthsArray.GetNumber(i);
                _currentFontWidths = widths;
            }
            var descriptor = ResolveDict(fontDict, "FontDescriptor");
            if (descriptor != null)
                _currentFontMissingWidth = (float)descriptor.GetNumber("MissingWidth", 0);
        }

        // Prefer a typeface loaded from the PDF's own embedded font stream
        // (/FontFile2 = TrueType, /FontFile3 = OpenType/CFF). When no embedded
        // data is present or the format isn't SkiaSharp-loadable (e.g. /FontFile
        // is raw Type1 PostScript), fall through to the system-font mapping.
        var embedded = TryLoadEmbeddedTypeface(fontDict);
        _currentFontHasEmbeddedProgram = embedded != null;
        _currentTypeface = embedded ?? GetTypeface(baseFont);
        _currentByteToGlyph = embedded != null && fontDict != null
            && _embeddedTypefaceByteToGlyph.TryGetValue(fontDict, out var btg)
            ? btg : null;
        _currentCffCidToGlyph = embedded != null && fontDict != null
            && _embeddedCffCidToGlyph.TryGetValue(fontDict, out var cffMap)
            ? cffMap : null;

        // Type0 (composite CID) fonts need a completely different content-stream
        // parse (2 bytes per character, widths indexed via /W not /Widths).
        _currentFontIsType0 = fontDict?.GetNameOrNull("Subtype") == "Type0";
        _currentCidWidths = null;
        _currentCidDefaultWidth = 1000f;
        _currentCidToGidMap = null;
        if (_currentFontIsType0 && fontDict != null)
        {
            var descendants = ResolveArray(fontDict, "DescendantFonts");
            if (descendants != null && descendants.Count > 0 &&
                _page.Document.Resolve(descendants[0]) is Pdfe.Core.Primitives.PdfDictionary cidFont)
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
            }
        }
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
    private SKTypeface? TryLoadEmbeddedTypeface(Pdfe.Core.Primitives.PdfDictionary? fontDict)
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

        // /FontFile2 (TrueType) → SkiaSharp loads directly.
        // /FontFile3 (OpenType/CFF) → if already SFNT-wrapped, Skia loads it;
        //   if it's raw Type1C/CIDFontType0C (more common in modern PDFs),
        //   we wrap it in a minimal OpenType container first.
        // /FontFile (raw Type1 PostScript) — Skia can't load directly. Skipped.
        var ff2 = descriptor.GetOptional("FontFile2");
        var ff3 = descriptor.GetOptional("FontFile3");

        byte[]? fontBytes = null;
        bool isCff = false;
        if (ff2 != null && _page.Document.Resolve(ff2) is Pdfe.Core.Primitives.PdfStream s2)
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
        if (isCff)
        {
            var wrapped = TryWrapCffAsOpenType(fontBytes, fontDict, descriptor, out cffCidToGlyph);
            if (wrapped != null) loadableBytes = wrapped;
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
        _embeddedTypefaceByteToGlyph[fontDict] = ResolveByteCodeCmap(typeface, fontDict);
        _embeddedCffCidToGlyph[fontDict] = cffCidToGlyph;
        return typeface;
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
        Pdfe.Core.Primitives.PdfDictionary? fontDict)
    {
        // Type0 (CID) fonts go through a separate draw path that already
        // walks bytes 2 at a time and resolves through the descendant font;
        // the format-0 workaround would double-encode.
        if (fontDict?.GetNameOrNull("Subtype") == "Type0") return null;

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
        return CmapFormat0Table.TryRead(typeface);
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
        out Dictionary<int, int>? cffCidToGlyph)
    {
        cffCidToGlyph = null;
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

    // Decode a raw PDF character code to its Unicode char under the current
    // font's encoding. Prefers the /Differences-derived map when present,
    // otherwise falls back to the named base encoding (WinAnsi/MacRoman).
    private char GetUnicodeForCode(byte code)
    {
        if (_currentCodeToUnicode != null)
            return _currentCodeToUnicode[code];
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

        var differences = ResolveArray(encodingDict, "Differences");
        if (differences != null)
        {
            int currentCode = 0;
            for (int i = 0; i < differences.Count; i++)
            {
                var item = differences[i];
                if (item is Pdfe.Core.Primitives.PdfName name)
                {
                    if (currentCode >= 0 && currentCode < 256 &&
                        AdobeGlyphList.TryGet(name.Value, out var ch))
                    {
                        map[currentCode] = ch;
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
        _currentUnicodeToCode = new Dictionary<char, byte>(256);
        for (int b = 0; b < 256; b++)
        {
            var c = map[b];
            if (c != '\0' && !_currentUnicodeToCode.ContainsKey(c))
                _currentUnicodeToCode[c] = (byte)b;
        }
    }

    private static char[] BuildBaseEncodingTable(string encodingName)
    {
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

    private SKTypeface GetTypeface(string baseFont)
    {
        // PDF subset fonts wear a 6-letter+'+' prefix (e.g. GFEDCB+MyriadPro-Semibold).
        // Strip it before matching — otherwise even "ZapfDingbats" subsets fall
        // through to Sans-Serif and the glyphs come out as missing-glyph boxes.
        var bareName = baseFont;
        if (bareName.Length >= 8 && bareName[6] == '+')
            bareName = bareName.Substring(7);

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
            // Linux ships NotoSansSymbols2 / OpenSymbol that cover U+27A4 etc.;
            // Skia's family-name lookup falls through to whichever is installed.
            family = "Noto Sans Symbols2";
        else
            family = "Sans-Serif";

        var style = SKFontStyle.Normal;
        if ((bareName.Contains("Bold") || bareName.Contains("Medi"))
            && (bareName.Contains("Italic") || bareName.Contains("Oblique") || bareName.Contains("Ital")))
            style = SKFontStyle.BoldItalic;
        else if (bareName.Contains("Bold") || bareName.Contains("Semibold") || bareName.Contains("Medium") || bareName.Contains("Medi"))
            style = SKFontStyle.Bold;
        else if (bareName.Contains("Italic") || bareName.Contains("Oblique") || bareName.Contains("Ital"))
            style = SKFontStyle.Italic;

        lock (_typefaceLoadLock)
        {
            return SKTypeface.FromFamilyName(family, style) ?? SKTypeface.Default;
        }
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
        if (_currentFontIsType0)
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
            else if (operand is PdfInteger or PdfReal)
            {
                // TJ position adjustment is in thousandths of text-space units,
                // which map to device-space X via the text matrix's X-scale
                // (not Y-scale). For non-uniform Tm (e.g. SCOTUS "SUPREME COURT"
                // with 14.2001/15 ratio), using yScale instead of xScale
                // compounds a ~6% per-glyph error into visible mid-word gaps.
                var adjustment = operand.GetNumber();
                var effectiveSize = GetEffectiveFontSize();
                var xyRatio = GetTextMatrixXYRatio();
                var xOffset = (float)(-adjustment * effectiveSize / 1000.0) * xyRatio;
                _textState.TextMatrixE += xOffset * _textState.HorizontalScale / 100.0f;
            }
        }
    }

    private void RenderText(string text, byte[]? sourceBytes = null)
    {
        if (!_inTextBlock || _currentTypeface == null)
            return;

        var effectiveSize = GetEffectiveFontSize();
        var xyRatio = GetTextMatrixXYRatio();

        // SkiaSharp 3 separated SKPaint and SKFont — draw calls now take
        // both arguments rather than a paint that wraps a font.
        using var font = new SKFont(_currentTypeface, effectiveSize);
        using var paint = new SKPaint
        {
            Color = _state.FillColor,
            BlendMode = _state.BlendMode,
            IsAntialias = _options.AntiAlias
        };

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
        _canvas.Save();
        _canvas.Translate(x, y);
        _canvas.Scale(xyRatio * th, ySign);

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
            for (int i = 0; i < sourceBytes!.Length; i++)
            {
                _canvas.DrawText(text[i].ToString(), cursor, 0, font, paint);
                int idx = sourceBytes[i] - _currentFontFirstChar;
                float w = idx >= 0 && idx < _currentFontWidths!.Length
                    ? _currentFontWidths[idx]
                    : _currentFontMissingWidth;
                float spacing = tc + (sourceBytes[i] == 0x20 ? tw : 0f);
                cursor += (w / 1000f + spacing) * effectiveSize;
            }
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
            if (blob != null) _canvas.DrawText(blob, 0, 0, paint);
        }
        else
        {
            _canvas.DrawText(text, 0, 0, font, paint);
        }
        _canvas.Restore();

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
            widthInFontUnits = font.MeasureText(new ReadOnlySpan<ushort>(gids), paint);
        }
        else
        {
            widthInFontUnits = font.MeasureText(text, paint);
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

        _textState.TextMatrixE += width;
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

    // Type0 rendering path. Content-stream bytes come in 2-at-a-time as
    // big-endian CIDs under /Identity-H (the only CMap we currently handle).
    // CIDs are rendered as glyph IDs directly — correct for /CIDToGIDMap
    // /Identity (the default and most common case for /CIDFontType2 fonts).
    private void RenderCidBytes(byte[] bytes)
    {
        if (!_inTextBlock || _currentTypeface == null || bytes.Length < 2)
            return;

        var count = bytes.Length / 2;
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
        var cids = new ushort[count];
        var gids = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            ushort cid = (ushort)((bytes[i * 2] << 8) | bytes[i * 2 + 1]);
            cids[i] = cid;
            ushort gid;
            if (_currentCidToGidMap != null && cid < _currentCidToGidMap.Length)
                gid = _currentCidToGidMap[cid];
            else if (_currentCffCidToGlyph != null
                     && _currentCffCidToGlyph.TryGetValue(cid, out var cffGid))
                gid = (ushort)cffGid;
            else
                gid = cid;
            gids[i] = gid;
        }

        var effectiveSize = GetEffectiveFontSize();
        var xyRatio = GetTextMatrixXYRatio();

        // SkiaSharp 3: SKPaint no longer carries the font or text encoding;
        // SKTextBlob (built below) embeds glyph IDs natively, so DrawText
        // doesn't need a paint-side encoding hint anymore.
        using var font = new SKFont(_currentTypeface, effectiveSize);
        using var paint = new SKPaint
        {
            Color = _state.FillColor,
            BlendMode = _state.BlendMode,
            IsAntialias = _options.AntiAlias,
        };

        // Match RenderText's Tm.d-aware Y-flip — without this, all CJK text
        // and any other content authored with a browser-style flipped Tm
        // (`1 0 0 -1`) renders upside-down.
        float ySign = _textState.TextMatrixD >= 0 ? -1f : 1f;
        _canvas.Save();
        _canvas.Translate(_textState.TextMatrixE, _textState.TextMatrixF + _textState.TextRise);
        _canvas.Scale(xyRatio, ySign);

        // Build a glyph-id text blob — SkiaSharp 3 routes glyph IDs
        // through SKTextBlob (the v2 byte[] overload was removed). The
        // GID array was already remapped through /CIDToGIDMap (or
        // CFF charset) above, so we feed it straight in.
        using var blob = BuildGlyphBlob(gids, font);
        if (blob != null) _canvas.DrawText(blob, 0, 0, paint);

        _canvas.Restore();

        // Advance by summed widths from /W (with /DW as fallback per CID).
        float sumThousandthsOfEm = 0f;
        foreach (var cid in cids)
        {
            sumThousandthsOfEm += (_currentCidWidths != null &&
                                   _currentCidWidths.TryGetValue(cid, out var w))
                ? w
                : _currentCidDefaultWidth;
        }
        var width = sumThousandthsOfEm * effectiveSize / 1000f * xyRatio;
        width *= _textState.HorizontalScale / 100.0f;
        _textState.TextMatrixE += width;
    }

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
        // Simple CMYK to RGB conversion (not color-managed)
        // R = 255 × (1-C) × (1-K)
        // G = 255 × (1-M) × (1-K)
        // B = 255 × (1-Y) × (1-K)
        var r = (byte)Math.Clamp(255 * (1 - c) * (1 - k), 0, 255);
        var g = (byte)Math.Clamp(255 * (1 - m) * (1 - k), 0, 255);
        var b = (byte)Math.Clamp(255 * (1 - y) * (1 - k), 0, 255);
        return new SKColor(r, g, b);
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
                // Clear soft mask - no visual effect needed
            }
            // Note: full soft mask (transparency group) rendering not yet supported
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

        var subtype = stream.GetNameOrNull("Subtype");
        switch (subtype)
        {
            case "Image":
                RenderImageXObject(stream);
                break;
            case "Form":
                RenderFormXObject(stream);
                break;
        }
    }

    private void RenderImageXObject(Pdfe.Core.Primitives.PdfStream imageStream)
    {
        var width = imageStream.GetInt("Width", 0);
        var height = imageStream.GetInt("Height", 0);
        if (width <= 0 || height <= 0)
            return;

        var bitsPerComponent = imageStream.GetInt("BitsPerComponent", 8);
        var colorSpace = imageStream.GetNameOrNull("ColorSpace") ?? "DeviceRGB";
        var imageData = imageStream.DecodedData;

        // Try to decode image
        SKBitmap? bitmap = null;
        try
        {
            // Check if it's a DCT (JPEG) encoded image
            var filters = imageStream.Filters;
            if (filters.Contains("DCTDecode"))
            {
                // JPEG data - decode directly. SafeDecode null-guards
                // the encoded bytes and swallows SkiaSharp internal
                // exceptions (ArgumentNullException 'codec', etc.) on
                // malformed inputs so a single bad image doesn't kill
                // the whole page.
                bitmap = SafeDecode(imageStream.EncodedData);
            }
            else if (filters.Contains("JPXDecode"))
            {
                // JPEG 2000 data — SkiaSharp's stock build has no JPX
                // codec on Linux, so SKBitmap.Decode returns null or
                // throws ArgumentNullException with codec=null. Either
                // way we fall back to the placeholder.
                bitmap = SafeDecode(imageStream.EncodedData);
                if (bitmap == null && width > 0 && height > 0)
                    bitmap = CreatePlaceholderBitmap(width, height);
            }
            else
            {
                // Raw image data - create bitmap based on color space
                bitmap = CreateBitmapFromRawData(imageData, width, height, bitsPerComponent, colorSpace, imageStream);
            }

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

            _canvas.DrawBitmap(bitmap, 0, 0, paint);
            _canvas.Restore();
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    private SKBitmap? CreateBitmapFromRawData(byte[] data, int width, int height, int bitsPerComponent, string colorSpace, Pdfe.Core.Primitives.PdfStream stream)
    {
        PdfColorSpace? pdfColorSpace = null;
        int componentsPerPixel = 3;

        var csObj = stream.GetOptional("ColorSpace");
        if (csObj != null)
        {
            pdfColorSpace = ResolveImageColorSpace(csObj);
            componentsPerPixel = pdfColorSpace.Components;
        }
        else
        {
            pdfColorSpace = PdfColorSpace.FromName(colorSpace);
            componentsPerPixel = pdfColorSpace.Components;
        }

        if (componentsPerPixel == 0)
            componentsPerPixel = 3;

        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var pixels = new byte[width * height * 4];

        try
        {
            int srcIndex = 0;
            int dstIndex = 0;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte r = 0, g = 0, b = 0, a = 255;

                    if (bitsPerComponent == 8 && pdfColorSpace != null)
                    {
                        var pixelValues = new double[componentsPerPixel];
                        if (srcIndex + componentsPerPixel <= data.Length)
                        {
                            for (int i = 0; i < componentsPerPixel; i++)
                                pixelValues[i] = pdfColorSpace.Type == PdfColorSpaceType.Indexed
                                    ? data[srcIndex + i]
                                    : data[srcIndex + i] / 255.0;
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
                        if (byteIndex < data.Length)
                        {
                            int bit = (data[byteIndex] >> bitIndex) & 1;
                            r = g = b = (byte)(bit == 0 ? 0 : 255);
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

            // Copy pixels to bitmap
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                bitmap.SetPixels(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }
        catch
        {
            bitmap.Dispose();
            return null;
        }

        return bitmap;
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
    private static SKBitmap? SafeDecode(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            return SKBitmap.Decode(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static SKBitmap CreatePlaceholderBitmap(int width, int height)
    {
        var bmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bmp.Erase(new SKColor(192, 192, 192, 255));
        return bmp;
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
            float bx1 = (float)bboxArr.GetNumber(0);
            float by1 = (float)bboxArr.GetNumber(1);
            float bx2 = (float)bboxArr.GetNumber(2);
            float by2 = (float)bboxArr.GetNumber(3);
            float bMinX = Math.Min(bx1, bx2);
            float bMinY = Math.Min(by1, by2);
            float bMaxX = Math.Max(bx1, bx2);
            float bMaxY = Math.Max(by1, by2);

            var formMatrix = SKMatrix.Identity;
            if (appearance.GetOptional("Matrix") is Pdfe.Core.Primitives.PdfArray mArr && mArr.Count >= 6)
            {
                formMatrix = new SKMatrix(
                    (float)mArr.GetNumber(0), (float)mArr.GetNumber(2), (float)mArr.GetNumber(4),
                    (float)mArr.GetNumber(1), (float)mArr.GetNumber(3), (float)mArr.GetNumber(5),
                    0, 0, 1);
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
            var savedFontIsType0 = _currentFontIsType0;
            var savedCidWidths = _currentCidWidths;
            var savedCurrentFontWidths = _currentFontWidths;
            var savedFontFirstChar = _currentFontFirstChar;
            var savedFontMissingWidth = _currentFontMissingWidth;
            var savedFontEncoding = _currentFontEncoding;
            var savedCodeToUnicode = _currentCodeToUnicode;
            var savedUnicodeToCode = _currentUnicodeToCode;
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
                _inTextBlock = false;
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
                _currentFontIsType0 = savedFontIsType0;
                _currentCidWidths = savedCidWidths;
                _currentFontWidths = savedCurrentFontWidths;
                _currentFontFirstChar = savedFontFirstChar;
                _currentFontMissingWidth = savedFontMissingWidth;
                _currentFontEncoding = savedFontEncoding;
                _currentCodeToUnicode = savedCodeToUnicode;
                _currentUnicodeToCode = savedUnicodeToCode;
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
        var savedState = _state.Clone();

        try
        {
            // Apply the form's transformation matrix if present
            var matrixArray = formStream.GetOptional("Matrix") as Pdfe.Core.Primitives.PdfArray;
            if (matrixArray != null && matrixArray.Count >= 6)
            {
                var a = (float)matrixArray.GetNumber(0);
                var b = (float)matrixArray.GetNumber(1);
                var c = (float)matrixArray.GetNumber(2);
                var d = (float)matrixArray.GetNumber(3);
                var e = (float)matrixArray.GetNumber(4);
                var f = (float)matrixArray.GetNumber(5);
                var matrix = new SKMatrix(a, c, e, b, d, f, 0, 0, 1);
                _canvas.Concat(in matrix);
            }

            // Parse and render the form's content stream through the same
            // typed operator path as normal page content. Resource resolution
            // stays on the renderer's stack, so local form resources still
            // override inherited page resources during execution.
            ExecuteContentBytes(formContent);
        }
        finally
        {
            _state = savedState;
            _resourcesStack.Pop();
            _canvas.Restore();
        }
    }

    #endregion

    #region Clipping Path (W, W* operators) - Issue #295

    private void SetClippingPath(bool evenOdd)
    {
        if (_currentPath == null) return;

        _currentPath.FillType = evenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding;

        // Apply the clipping path to the canvas
        _canvas.ClipPath(_currentPath, SKClipOperation.Intersect, _options.AntiAlias);

        // Note: The path is NOT disposed here - it will be used by the following
        // path-painting operator (like n, S, f) which will dispose it
    }

    #endregion

    #region Shading (sh operator) - Issue #300

    private bool RenderFillPattern(SKPath path)
    {
        if (_state.FillPatternName == null)
            return false;

        var pattern = ResolvePatternFromActiveResources(_state.FillPatternName);
        if (pattern == null || pattern.GetInt("PatternType", 0) != 2)
            return false;

        var shadingObj = pattern.GetOptional("Shading");
        if (shadingObj == null)
            return false;
        var shading = _page.Document.Resolve(shadingObj) as Pdfe.Core.Primitives.PdfDictionary;
        if (shading == null)
            return false;

        if (shading.GetInt("ShadingType", 0) != 6)
            return false;

        return RenderType6MeshPattern(path, pattern, shading);
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

            var colors = ResolveMeshColors(components, previous, flag, function, colorSpace);
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

    private static SKColor[] ResolveMeshColors(
        List<double> components,
        MeshPatch? previous,
        int flag,
        Pdfe.Core.Primitives.PdfObject? function,
        string colorSpace)
    {
        var newColors = components
            .Select(c => ComponentsToSkColor(PdfFunctionEvaluator.Evaluate(function, c) ?? new[] { c }, colorSpace))
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

    private static SKMatrix GetMatrix(Pdfe.Core.Primitives.PdfArray? arr)
    {
        if (arr == null || arr.Count < 6)
            return new SKMatrix(1, 0, 0, 0, 1, 0, 0, 0, 1);

        return new SKMatrix(
            (float)arr.GetNumber(0),
            (float)arr.GetNumber(2),
            (float)arr.GetNumber(4),
            (float)arr.GetNumber(1),
            (float)arr.GetNumber(3),
            (float)arr.GetNumber(5),
            0,
            0,
            1);
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

        // Get the shading dictionary from page resources
        var shading = _page.GetShading(name);
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

        using var paint = new SKPaint
        {
            Shader = shader,
            BlendMode = _state.BlendMode,
            IsAntialias = _options.AntiAlias
        };

        // Fill the current clipping area
        var clipBounds = _canvas.LocalClipBounds;
        _canvas.DrawRect(clipBounds, paint);
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

        using var paint = new SKPaint
        {
            Shader = shader,
            BlendMode = _state.BlendMode,
            IsAntialias = _options.AntiAlias
        };

        // Fill the current clipping area
        var clipBounds = _canvas.LocalClipBounds;
        _canvas.DrawRect(clipBounds, paint);
    }

    private void RenderFunctionShading(Pdfe.Core.Primitives.PdfDictionary shading)
    {
        var funcObj = shading.GetOptional("Function");
        var colorSpaceName = shading.GetNameOrNull("ColorSpace") ?? "DeviceGray";
        var comps = PdfFunctionEvaluator.Evaluate(funcObj, 0.5) ?? new[] { 0.0 };
        var color = ComponentsToSkColor(comps, colorSpaceName);

        using var paint = new SKPaint
        {
            Color = color,
            BlendMode = _state.BlendMode,
            IsAntialias = _options.AntiAlias
        };

        var clipBounds = _canvas.LocalClipBounds;
        _canvas.DrawRect(clipBounds, paint);
    }

    private (SKColor start, SKColor end, SKColor[]? stops, float[]? positions) ResolveGradientColors(
        Pdfe.Core.Primitives.PdfDictionary shading)
    {
        var colorSpaceName = shading.GetNameOrNull("ColorSpace") ?? "DeviceGray";
        var funcObj = shading.GetOptional("Function");

        var c0 = PdfFunctionEvaluator.Evaluate(funcObj, 0.0) ?? new[] { 0.0 };
        var c1 = PdfFunctionEvaluator.Evaluate(funcObj, 1.0) ?? new[] { 1.0 };

        var startColor = ComponentsToSkColor(c0, colorSpaceName);
        var endColor = ComponentsToSkColor(c1, colorSpaceName);

        SKColor[]? stops = null;
        float[]? positions = null;

        if (funcObj is Pdfe.Core.Primitives.PdfDictionary fd && fd.GetInt("FunctionType", -1) == 3)
        {
            var boundsObj = fd.GetOptional("Bounds") as Pdfe.Core.Primitives.PdfArray;
            if (boundsObj != null && boundsObj.Count > 0)
            {
                var pts = new List<float> { 0f };
                for (int i = 0; i < boundsObj.Count; i++)
                    pts.Add((float)boundsObj.GetNumber(i));
                pts.Add(1f);

                var colors = new List<SKColor>();
                foreach (var pt in pts)
                {
                    var c = PdfFunctionEvaluator.Evaluate(funcObj, pt) ?? new[] { 0.0 };
                    colors.Add(ComponentsToSkColor(c, colorSpaceName));
                }

                stops = colors.ToArray();
                positions = pts.ToArray();
            }
        }

        return (startColor, endColor, stops, positions);
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
                comps.Length >= 4 ? CmykToRgbColor(comps[0], comps[1], comps[2], comps[3]) : SKColors.Black,
            _ => comps.Length >= 3 ? ToRGB(comps[0], comps[1], comps[2])
               : comps.Length >= 1 ? ToGray(comps[0]) : SKColors.Black
        };
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

    private static SKColor CmykToRgbColor(double c, double m, double y, double k)
    {
        double r = (1 - c) * (1 - k);
        double g = (1 - m) * (1 - k);
        double b = (1 - y) * (1 - k);
        return ToRGB(r, g, b);
    }

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

    private PdfColorSpace? ResolveColorSpace(string name)
    {
        var cs = PdfColorSpace.FromName(name);
        if (cs.Type != PdfColorSpaceType.Unknown)
            return cs;

        foreach (var resources in _resourcesStack)
        {
            if (resources == null) continue;
            var colorSpacesObj = resources.GetOptional("ColorSpace");
            if (colorSpacesObj == null) continue;
            if (_page.Document.Resolve(colorSpacesObj) is not Pdfe.Core.Primitives.PdfDictionary colorSpaces)
                continue;

            var csObj = colorSpaces.GetOptional(name);
            if (csObj == null) continue;
            return PdfColorSpace.Parse(csObj, _page.Document);
        }

        var pageCsObj = _page.GetColorSpaceObject(name);
        if (pageCsObj != null)
            return PdfColorSpace.Parse(pageCsObj, _page.Document);

        return null;
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
