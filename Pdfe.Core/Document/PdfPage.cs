using Pdfe.Core.Content;
using Pdfe.Core.Graphics;
using Pdfe.Core.Primitives;
using Pdfe.Core.Text;

namespace Pdfe.Core.Document;

/// <summary>
/// Represents a page in a PDF document.
/// </summary>
public class PdfPage
{
    private readonly PdfDocument _document;
    private readonly PdfDictionary _pageDict;
    private IReadOnlyList<Letter>? _cachedLetters;
    private string? _cachedText;
    private IReadOnlyList<Word>? _cachedWords;

    /// <summary>
    /// The 1-based page number.
    /// </summary>
    public int PageNumber { get; }

    /// <summary>
    /// Creates a new page wrapper.
    /// </summary>
    internal PdfPage(PdfDocument document, PdfDictionary pageDict, int pageNumber)
    {
        _document = document;
        _pageDict = pageDict;
        PageNumber = pageNumber;
    }

    /// <summary>
    /// The underlying page dictionary.
    /// </summary>
    public PdfDictionary Dictionary => _pageDict;

    /// <summary>
    /// The document this page belongs to.
    /// </summary>
    public PdfDocument Document => _document;

    /// <summary>
    /// Get the extracted text content from the page.
    /// Cached on first access; subsequent calls return the cached result.
    /// </summary>
    public string Text
    {
        get
        {
            if (_cachedText != null)
                return _cachedText;

            var extractor = new TextExtractor(this);
            _cachedText = extractor.ExtractText();
            return _cachedText;
        }
    }

    /// <summary>
    /// Get all letters extracted from the page with position information.
    /// Cached on first access; subsequent calls return the cached result.
    /// </summary>
    public IReadOnlyList<Letter> Letters
    {
        get
        {
            if (_cachedLetters != null)
                return _cachedLetters;

            var extractor = new TextExtractor(this);
            _cachedLetters = extractor.ExtractLetters();
            return _cachedLetters;
        }
    }

    /// <summary>
    /// Get all words extracted from the page.
    /// A word is a sequence of letters separated by whitespace.
    /// Cached on first access; subsequent calls return the cached result.
    /// </summary>
    /// <returns>List of words with their letters and bounding boxes.</returns>
    public IReadOnlyList<Word> GetWords()
    {
        if (_cachedWords != null)
            return _cachedWords;

        var extractor = new TextExtractor(this);
        _cachedWords = extractor.ExtractWords();
        return _cachedWords;
    }

    /// <summary>
    /// All annotations on this page (§12.5).
    /// Covers every subtype: Text, Link, Highlight, Widget, Stamp, Ink, etc.
    /// </summary>
    public IReadOnlyList<PdfAnnotation> GetAnnotations()
    {
        var pageMap    = PdfOutlineParser.BuildPageRefMap(_document);
        var namedDests = PdfOutlineParser.BuildNamedDestinations(_document);
        return PdfAnnotationParser.Parse(_document, _pageDict, pageMap, namedDests);
    }

    /// <summary>
    /// AcroForm fields whose Widget annotation lives on this page (§12.7).
    /// Returns the document-wide AcroForm filtered to this page; returns an
    /// empty list when the document has no /AcroForm dictionary or none of
    /// its widgets reference this page.
    /// </summary>
    public IReadOnlyList<PdfField> GetFormFields()
    {
        var form = _document.GetAcroForm();
        if (form == null) return Array.Empty<PdfField>();
        var pageNum = PageNumber;
        return form.Fields.Where(f => f.PageNumber == pageNum).ToList();
    }

    /// <summary>
    /// Internal-document link annotations on this page (PDF spec §12.5.6.5).
    /// External / URI links are filtered out; what's returned is only the
    /// kind of link a clickable table-of-contents or back-of-book index
    /// produces — pointers to other pages in this document.
    /// </summary>
    public IReadOnlyList<PdfLink> GetLinks()
    {
        // Build the page-ref map and named-dest map fresh per call.
        // Callers that want them across many pages should use the static
        // PdfLinkParser.Parse with shared maps to avoid the redundant work.
        var pageMap = PdfOutlineParser.BuildPageRefMap(_document);
        var namedDests = PdfOutlineParser.BuildNamedDestinations(_document);
        return PdfLinkParser.Parse(_document, _pageDict, pageMap, namedDests);
    }

    /// <summary>
    /// Page width in points.
    /// </summary>
    public double Width => MediaBox.Width;

    /// <summary>
    /// Page height in points.
    /// </summary>
    public double Height => MediaBox.Height;

    /// <summary>
    /// Page rotation in degrees (0, 90, 180, 270).
    /// </summary>
    public int Rotation
    {
        get => GetInheritedInt("Rotate", 0) % 360;
        set
        {
            // Normalize to 0, 90, 180, or 270
            value = ((value % 360) + 360) % 360;
            if (value != 0 && value != 90 && value != 180 && value != 270)
                throw new ArgumentException("Rotation must be 0, 90, 180, or 270 degrees", nameof(value));

            if (value == 0)
                _pageDict.Remove("Rotate");
            else
                _pageDict.SetInt("Rotate", value);
        }
    }

    /// <summary>
    /// The media box (page boundaries).
    /// </summary>
    public PdfRectangle MediaBox => GetInheritedRectangle("MediaBox")
        ?? throw new InvalidOperationException("Page has no MediaBox");

    /// <summary>
    /// The crop box (visible area). Falls back to MediaBox if not specified.
    /// </summary>
    public PdfRectangle CropBox => GetInheritedRectangle("CropBox") ?? MediaBox;

    /// <summary>
    /// The bleed box. Falls back to CropBox if not specified.
    /// </summary>
    public PdfRectangle BleedBox => GetRectangle("BleedBox") ?? CropBox;

    /// <summary>
    /// The trim box. Falls back to CropBox if not specified.
    /// </summary>
    public PdfRectangle TrimBox => GetRectangle("TrimBox") ?? CropBox;

    /// <summary>
    /// The art box. Falls back to CropBox if not specified.
    /// </summary>
    public PdfRectangle ArtBox => GetRectangle("ArtBox") ?? CropBox;

    /// <summary>
    /// Width of the page as displayed, i.e. after applying <see cref="Rotation"/>.
    /// Equals <see cref="Width"/> for 0°/180° and <see cref="Height"/> for 90°/270°.
    /// </summary>
    public double VisualWidth => NormalizedRotation is 90 or 270 ? Height : Width;

    /// <summary>
    /// Height of the page as displayed, i.e. after applying <see cref="Rotation"/>.
    /// Equals <see cref="Height"/> for 0°/180° and <see cref="Width"/> for 90°/270°.
    /// </summary>
    public double VisualHeight => NormalizedRotation is 90 or 270 ? Width : Height;

    /// <summary><see cref="Rotation"/> folded into the canonical {0,90,180,270}.</summary>
    private int NormalizedRotation
    {
        get { int r = ((Rotation % 360) + 360) % 360; return r; }
    }

    /// <summary>
    /// Map a rectangle from <em>visual</em> space — what the viewer sees after
    /// the page <see cref="Rotation"/> is applied: origin at the top-left,
    /// x increasing right, y increasing <b>down</b>, sized
    /// <see cref="VisualWidth"/>×<see cref="VisualHeight"/> — into
    /// content-stream space (PDF default: MediaBox origin at the bottom-left,
    /// y increasing up), which is what <see cref="PdfPageRedactionExtensions.RedactArea(PdfPage, PdfRectangle, Pdfe.Core.Text.Segmentation.GlyphRemovalStrategy)"/>
    /// and the rest of the engine operate in.
    /// </summary>
    /// <remarks>
    /// This is the single source of truth for the visual↔content mapping on
    /// rotated pages (#356). Callers that have a selection in rendered/visual
    /// coordinates must route it through here rather than applying an ad-hoc
    /// Y-flip, which is only correct at 0° rotation and silently redacts the
    /// wrong region at 90/180/270. The four corners are transformed and the
    /// axis-aligned bounding box returned (a 90° rotation keeps rectangles
    /// axis-aligned, so no area is lost or gained).
    /// </remarks>
    /// <param name="visualRect">Rectangle in visual space; its numeric x range
    /// and y range are interpreted as visual coordinates (y measured downward).</param>
    public PdfRectangle ToContentStreamCoordinates(PdfRectangle visualRect)
    {
        var mb = MediaBox.Normalize();
        double l = mb.Left, b = mb.Bottom, w = mb.Width, h = mb.Height;
        int r = NormalizedRotation;

        // Map one visual point (x right, y down, top-left origin) to content
        // space (x right, y up, MediaBox bottom-left origin). Derived from
        // rotating the unrotated page image clockwise by /Rotate degrees.
        (double x, double y) Map(double vx, double vy) => r switch
        {
            90  => (l + vy,         b + vx),
            180 => (l + w - vx,     b + vy),
            270 => (l + w - vy,     b + h - vx),
            _   => (l + vx,         b + h - vy),   // 0°
        };

        var p1 = Map(visualRect.Left, visualRect.Bottom);
        var p2 = Map(visualRect.Left, visualRect.Top);
        var p3 = Map(visualRect.Right, visualRect.Bottom);
        var p4 = Map(visualRect.Right, visualRect.Top);

        double minX = Math.Min(Math.Min(p1.x, p2.x), Math.Min(p3.x, p4.x));
        double maxX = Math.Max(Math.Max(p1.x, p2.x), Math.Max(p3.x, p4.x));
        double minY = Math.Min(Math.Min(p1.y, p2.y), Math.Min(p3.y, p4.y));
        double maxY = Math.Max(Math.Max(p1.y, p2.y), Math.Max(p3.y, p4.y));

        return new PdfRectangle(minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Get the page resources dictionary.
    /// </summary>
    public PdfDictionary? Resources => GetInheritedDictionary("Resources");

    /// <summary>
    /// Get the raw content stream bytes (decoded).
    /// </summary>
    public byte[] GetContentStreamBytes()
    {
        var contentsObj = _pageDict.GetOptional("Contents");
        if (contentsObj == null)
            return Array.Empty<byte>();

        contentsObj = _document.Resolve(contentsObj);

        if (contentsObj is PdfStream stream)
        {
            return stream.DecodedData;
        }
        else if (contentsObj is PdfArray array)
        {
            // Multiple content streams - concatenate
            using var ms = new MemoryStream();
            foreach (var item in array)
            {
                var resolved = _document.Resolve(item);
                if (resolved is PdfStream s)
                {
                    ms.Write(s.DecodedData);
                    ms.WriteByte((byte)'\n'); // Separate streams with newline
                }
            }
            return ms.ToArray();
        }

        return Array.Empty<byte>();
    }

    /// <summary>
    /// Sets the content stream bytes for this page.
    /// </summary>
    public void SetContentStreamBytes(byte[] data)
    {
        // Any cached extraction (Letters/Text/Words from A4) is now stale —
        // the content has changed underneath it. Multi-match redaction relies
        // on the second RedactArea call seeing freshly-extracted letters that
        // reflect the first redaction's deletions.
        _cachedLetters = null;
        _cachedText = null;
        _cachedWords = null;

        var contentsObj = _pageDict.GetOptional("Contents");

        if (contentsObj == null)
        {
            // Create a new content stream as a proper indirect object —
            // PDF streams are not valid inline in a dictionary.
            var newStream = new PdfStream(data);
            var streamRef = _document.AddIndirectObject(newStream);
            _pageDict["Contents"] = streamRef;
            return;
        }

        contentsObj = _document.Resolve(contentsObj);

        if (contentsObj is PdfStream stream)
        {
            // Update existing stream (also updates encoded data and length)
            stream.DecodedData = data;
        }
        else if (contentsObj is PdfArray array && array.Count > 0)
        {
            // Update first stream in array
            var firstRef = array[0];
            var resolved = _document.Resolve(firstRef);
            if (resolved is PdfStream firstStream)
            {
                // Update first stream (removes filters too)
                firstStream.DecodedData = data;
                // Clear other streams in the array if present
                while (array.Count > 1)
                    array.RemoveAt(array.Count - 1);
            }
        }
    }

    /// <summary>
    /// Gets a graphics context for drawing on this page.
    /// </summary>
    public PdfGraphics GetGraphics()
    {
        // Return a fresh graphics context each call. Caching a single
        // instance bit us when callers used `using var g = …` — once
        // disposed, subsequent calls handed back the same dead instance.
        return new PdfGraphics(this);
    }

    /// <summary>
    /// Get the content stream as a parsed ContentStream object.
    /// </summary>
    public ContentStream GetContentStream()
    {
        var bytes = GetContentStreamBytes();
        if (bytes.Length == 0)
            return new ContentStream();

        var parser = new ContentStreamParser(bytes, this);
        return parser.Parse();
    }

    /// <summary>
    /// Set the content stream from a ContentStream object.
    /// </summary>
    public void SetContentStream(ContentStream content)
    {
        var writer = new ContentStreamWriter();
        var bytes = writer.Write(content);
        SetContentStreamBytes(bytes);
    }

    /// <summary>
    /// Get a font from the page resources.
    /// </summary>
    public PdfDictionary? GetFont(string fontName)
    {
        // /Font is often an indirect reference (most browsers/Word/WeasyPrint
        // emit `/Font N 0 R`), and Resources is sometimes one too. Resolve
        // both, otherwise the font lookup silently misses and the renderer
        // falls back to a default fallback typeface.
        var fontsObj = Resources?.GetOptional("Font");
        if (fontsObj == null) return null;
        var fonts = _document.Resolve(fontsObj) as PdfDictionary;
        if (fonts == null) return null;

        var fontObj = fonts.GetOptional(fontName);
        if (fontObj == null)
            return null;

        return _document.Resolve(fontObj) as PdfDictionary;
    }

    /// <summary>
    /// Get all fonts used on this page.
    /// </summary>
    public IEnumerable<(string Name, PdfDictionary Font)> GetFonts()
    {
        var fonts = Resources?.GetDictionaryOrNull("Font");
        if (fonts == null)
            yield break;

        foreach (var kvp in fonts)
        {
            var fontDict = _document.Resolve(kvp.Value) as PdfDictionary;
            if (fontDict != null)
            {
                yield return (kvp.Key.Value, fontDict);
            }
        }
    }

    /// <summary>
    /// Adds a font to the page resources if not already present.
    /// Returns the font resource name (e.g., "F1", "F2").
    /// </summary>
    public string AddFont(PdfFont font)
    {
        // Get or create Resources dictionary
        var resources = EnsureResources();

        // Get or create Font dictionary within Resources
        if (!resources.TryGetValue("Font", out var fontDictObj) || fontDictObj is not PdfDictionary fontDict)
        {
            fontDict = new PdfDictionary();
            resources["Font"] = fontDict;
        }

        // Check if this font is already registered by base font name
        foreach (var kvp in fontDict)
        {
            var existingFont = _document.Resolve(kvp.Value) as PdfDictionary;
            if (existingFont != null)
            {
                var existingBaseFont = existingFont.GetNameOrNull("BaseFont");
                if (existingBaseFont == font.BaseFont)
                {
                    return kvp.Key.Value; // Return existing name
                }
            }
        }

        // Find next available font name
        var fontName = font.Name;
        int counter = 1;
        while (fontDict.ContainsKey(fontName))
        {
            fontName = $"F{counter++}";
        }

        // Add the font dictionary (embedded fonts register their own indirect
        // stream objects in the document and return a Type0 dictionary).
        var builtFont = font.BuildFontDictionary(_document);
        fontDict[fontName] = font.PreferIndirectFontDictionary
            ? _document.AddIndirectObject(builtFont)
            : builtFont;

        return fontName;
    }

    /// <summary>
    /// Ensures the page has a Resources dictionary, creating one if needed.
    /// </summary>
    private PdfDictionary EnsureResources()
    {
        var resources = Resources;
        if (resources != null)
            return resources;

        // Create a new Resources dictionary
        resources = new PdfDictionary();
        _pageDict["Resources"] = resources;
        return resources;
    }

    /// <summary>
    /// Get an XObject (form or image) from the page resources.
    /// </summary>
    public PdfObject? GetXObject(string name)
    {
        var xobjects = Resources?.GetDictionaryOrNull("XObject");
        if (xobjects == null)
            return null;

        var obj = xobjects.GetOptional(name);
        return obj != null ? _document.Resolve(obj) : null;
    }

    /// <summary>
    /// Get a graphics state from the page resources.
    /// </summary>
    public PdfDictionary? GetExtGState(string name)
    {
        var extGState = Resources?.GetDictionaryOrNull("ExtGState");
        if (extGState == null)
            return null;

        var obj = extGState.GetOptional(name);
        return obj != null ? _document.Resolve(obj) as PdfDictionary : null;
    }

    /// <summary>
    /// Get a shading dictionary from the page resources.
    /// </summary>
    public PdfDictionary? GetShading(string name)
    {
        var shadings = Resources?.GetDictionaryOrNull("Shading");
        if (shadings == null)
            return null;

        var obj = shadings.GetOptional(name);
        return obj != null ? _document.Resolve(obj) as PdfDictionary : null;
    }

    /// <summary>
    /// Get a color space object from the page resources.
    /// Returns the raw PdfObject (name or array) for parsing into PdfColorSpace.
    /// </summary>
    public PdfObject? GetColorSpaceObject(string name)
    {
        var colorSpaces = Resources?.GetDictionaryOrNull("ColorSpace");
        if (colorSpaces == null)
            return null;

        var obj = colorSpaces.GetOptional(name);
        return obj != null ? _document.Resolve(obj) : null;
    }

    #region Inherited Properties

    /// <summary>
    /// Get an inherited integer value (walks up page tree).
    /// </summary>
    private int GetInheritedInt(string key, int defaultValue)
    {
        var current = _pageDict;
        while (current != null)
        {
            if (current.ContainsKey(key))
                return current.GetInt(key, defaultValue);

            var parentRef = current.GetReferenceOrNull("Parent");
            current = parentRef != null ? _document.GetObject(parentRef) as PdfDictionary : null;
        }
        return defaultValue;
    }

    /// <summary>
    /// Get an inherited dictionary value (walks up page tree).
    /// </summary>
    private PdfDictionary? GetInheritedDictionary(string key)
    {
        var current = _pageDict;
        while (current != null)
        {
            var obj = current.GetOptional(key);
            if (obj != null)
            {
                var resolved = _document.Resolve(obj);
                if (resolved is PdfDictionary dict)
                    return dict;
            }

            var parentRef = current.GetReferenceOrNull("Parent");
            current = parentRef != null ? _document.GetObject(parentRef) as PdfDictionary : null;
        }
        return null;
    }

    /// <summary>
    /// Get an inherited rectangle (walks up page tree).
    /// </summary>
    private PdfRectangle? GetInheritedRectangle(string key)
    {
        var current = _pageDict;
        while (current != null)
        {
            var rect = GetRectangleFromDict(current, key);
            if (rect.HasValue)
                return rect;

            var parentRef = current.GetReferenceOrNull("Parent");
            current = parentRef != null ? _document.GetObject(parentRef) as PdfDictionary : null;
        }
        return null;
    }

    /// <summary>
    /// Get a rectangle from this page's dictionary (non-inherited).
    /// </summary>
    private PdfRectangle? GetRectangle(string key)
    {
        return GetRectangleFromDict(_pageDict, key);
    }

    /// <summary>
    /// Get a rectangle from a dictionary.
    /// </summary>
    private PdfRectangle? GetRectangleFromDict(PdfDictionary dict, string key)
    {
        var obj = dict.GetOptional(key);
        if (obj == null)
            return null;

        var resolved = _document.Resolve(obj);
        if (resolved is not PdfArray arr || arr.Count != 4)
            return null;

        return new PdfRectangle(
            arr.GetNumber(0),
            arr.GetNumber(1),
            arr.GetNumber(2),
            arr.GetNumber(3)
        );
    }

    #endregion

    /// <inheritdoc />
    public override string ToString() => $"Page {PageNumber} ({Width}x{Height} pts)";
}

/// <summary>
/// A rectangle in PDF coordinates (bottom-left origin).
/// </summary>
public readonly record struct PdfRectangle(double Left, double Bottom, double Right, double Top)
{
    /// <summary>
    /// Width of the rectangle.
    /// </summary>
    public double Width => Math.Abs(Right - Left);

    /// <summary>
    /// Height of the rectangle.
    /// </summary>
    public double Height => Math.Abs(Top - Bottom);

    /// <summary>
    /// Create a rectangle from an array.
    /// </summary>
    public static PdfRectangle FromArray(PdfArray arr)
    {
        if (arr.Count != 4)
            throw new ArgumentException("Rectangle array must have 4 elements");

        return new PdfRectangle(
            arr.GetNumber(0),
            arr.GetNumber(1),
            arr.GetNumber(2),
            arr.GetNumber(3)
        );
    }

    /// <summary>
    /// Normalize the rectangle (ensure Left &lt; Right and Bottom &lt; Top).
    /// </summary>
    public PdfRectangle Normalize()
    {
        return new PdfRectangle(
            Math.Min(Left, Right),
            Math.Min(Bottom, Top),
            Math.Max(Left, Right),
            Math.Max(Bottom, Top)
        );
    }

    /// <summary>
    /// Check if this rectangle intersects with another rectangle.
    /// </summary>
    public bool IntersectsWith(PdfRectangle other)
    {
        var a = Normalize();
        var b = other.Normalize();
        return a.Left < b.Right && a.Right > b.Left &&
               a.Bottom < b.Top && a.Top > b.Bottom;
    }

    /// <summary>
    /// Check if a point is contained within this rectangle.
    /// </summary>
    public bool Contains(double x, double y)
    {
        var norm = Normalize();
        return x >= norm.Left && x <= norm.Right &&
               y >= norm.Bottom && y <= norm.Top;
    }

    /// <inheritdoc />
    public override string ToString() => $"[{Left:F2}, {Bottom:F2}, {Right:F2}, {Top:F2}]";
}

/// <summary>
/// A point in PDF coordinates (bottom-left origin).
/// </summary>
public readonly record struct PdfPoint(double X, double Y)
{
    /// <inheritdoc />
    public override string ToString() => $"({X:F2}, {Y:F2})";
}
